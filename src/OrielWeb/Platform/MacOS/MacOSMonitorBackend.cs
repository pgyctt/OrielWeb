using System.Runtime.InteropServices;
using OrielWeb.Platform.MacOS.Interop;

namespace OrielWeb.Platform.MacOS;

/// <summary>
/// macOS 的显示器后端：<c>[NSScreen screens]</c> 取几何与名称，CoreGraphics 取光标。
/// </summary>
/// <remarks>
/// <para>
/// <b>坐标系换算</b>：AppKit 的屏幕坐标是 cocoa 点、**左下角原点、y 向上**；本库对外是
/// 物理像素、**主屏左上角原点、y 向下**（与 Tauri 一致）。两步换算：
/// </para>
/// <list type="number">
///   <item><description><c>topY = 主屏上沿 - (frame.y + frame.height)</c>，把 y 翻成向下；</description></item>
///   <item><description>乘 <c>backingScaleFactor</c>（Retina 上 2.0）把点换成物理像素。</description></item>
/// </list>
/// <para>
/// <b>为什么几何走 KVC 而不是直接读 <c>frame</c></b>：<c>NSRect</c> 是 32 字节结构体，
/// <c>objc_msgSend</c> 直取在 x86_64 上要走 <c>objc_msgSend_stret</c>、arm64 上是隐式 sret，
/// 互操作层没有那条路（见 <c>ObjCRuntime</c> 文件头）。这与
/// <c>MacOSWindowHost.ReadFrame</c> / <c>ScreenMaxY</c> 用的是同一套读法：
/// <c>valueForKey:@"frame"</c> → <c>NSValue</c> → <c>getValue:</c> 写进 4 个 double。
/// </para>
/// <para>
/// <b>主屏取 <c>mainScreen</c></b>：AppKit 的 <c>mainScreen</c> 是"有键盘焦点的那个窗口所在屏
/// （没有窗口时是菜单栏所在屏）"，Tauri（tao）的 <c>primary_monitor</c> 用的就是它。
/// 它同时决定了本库坐标原点的位置——所以"主屏"在这里既是返回值也是坐标系基准，两者必须是同一块屏。
/// </para>
/// </remarks>
internal sealed unsafe partial class MacOSMonitorBackend : IMonitorBackend
{
    /// <summary>显示器枚举上限（macOS 实际支持远少于这个数）。</summary>
    private const int MaxDisplays = 32;

    private const string CoreGraphicsLib = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CoreFoundationLib = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    /// <summary>CG 全局坐标空间是否以"点"为单位（缓存；运行时不会变）。</summary>
    private bool? _cgSpaceIsPoints;

    public IReadOnlyList<OrielMonitor> GetMonitors()
    {
        List<ScreenEntry> screens = ReadScreens();
        double mainTop = MainScreenTop(screens);
        uint mainId = AppKitMainDisplayId();

        var monitors = new List<OrielMonitor>(screens.Count);
        foreach (ScreenEntry entry in screens)
        {
            monitors.Add(ToMonitor(entry, mainTop, mainId));
        }

        return monitors;
    }

    public OrielMonitor? GetPrimaryMonitor()
    {
        List<ScreenEntry> screens = ReadScreens();
        if (screens.Count == 0)
        {
            return null;
        }

        double mainTop = MainScreenTop(screens);

        // [NSScreen mainScreen] 与列表里的对象是同一个（同一块屏可能被包装成不同实例，
        // 所以按 displayId 比而不是按指针比）。
        uint mainId = AppKitMainDisplayId();

        foreach (ScreenEntry entry in screens)
        {
            if (entry.DisplayId == mainId)
            {
                return ToMonitor(entry, mainTop, mainId);
            }
        }

        return ToMonitor(screens[0], mainTop, mainId);
    }

    public OrielMonitor? MonitorFromPoint(double x, double y)
    {
        // 直接用本库自己的物理矩形做包含判断，而不是再走一遍 CG/NSScreen 的坐标换算：
        // 报给页面的矩形与判定用的是同一套数值，不存在"算出来在不同屏"的两套真相。
        foreach (OrielMonitor monitor in GetMonitors())
        {
            double left = monitor.Position.X;
            double top = monitor.Position.Y;
            if (x >= left && x < left + monitor.Size.Width &&
                y >= top && y < top + monitor.Size.Height)
            {
                return monitor;
            }
        }

        return null;
    }

    public OrielMonitor? MonitorFromWindow(nint nativeWindowHandle)
    {
        if (nativeWindowHandle == 0)
        {
            return null;
        }

        // -[NSWindow screen]：窗口所在的 NSScreen；窗口已移出所有屏幕时为 nil。
        nint screen = ObjCRuntime.SendId(nativeWindowHandle, ObjCRuntime.Sel("screen"));
        uint displayId = DisplayIdOf(screen);
        if (displayId == 0)
        {
            return null;
        }

        List<ScreenEntry> screens = ReadScreens();
        double mainTop = MainScreenTop(screens);
        uint mainId = AppKitMainDisplayId();
        foreach (ScreenEntry entry in screens)
        {
            if (entry.DisplayId == displayId)
            {
                return ToMonitor(entry, mainTop, mainId);
            }
        }

        return null;
    }

    public OrielPosition GetCursorPosition()
    {
        // -[NSEvent mouseLocation] 返回 NSPoint（结构体返回值），走不了 msgSend；
        // CGEventGetLocation 是普通 C 函数，结构体返回由运行时的 ABI 处理，且它的
        // 全局坐标已经是**左上角原点、y 向下**——省掉一次翻转。
        nint ev = CGEventCreate(0);
        if (ev == 0)
        {
            return new OrielPosition(0, 0);
        }

        try
        {
            CGPoint location = CGEventGetLocation(ev);

            // CG 的全局坐标空间在不同系统版本上分别以点/像素计（取决于主屏是否 Retina）。
            // 用主屏的"CG 高度 vs AppKit 点高度"比一次就能判出来，比猜更可靠。
            double unit = CgSpaceIsPoints() ? MainScreenScale() : 1.0;
            return new OrielPosition(
                Math.Round(location.X * unit),
                Math.Round(location.Y * unit));
        }
        finally
        {
            CFRelease(ev);
        }
    }

    // ------------------------------------------------------------------
    // NSScreen 读取
    // ------------------------------------------------------------------

    /// <summary>一台屏的原始读数（cocoa 点，左下角原点）。</summary>
    private readonly record struct ScreenEntry(
        uint DisplayId, string? Name, double X, double Y, double Width, double Height, double Scale);

    /// <summary>枚举 <c>[NSScreen screens]</c>。</summary>
    private static List<ScreenEntry> ReadScreens()
    {
        var result = new List<ScreenEntry>(2);

        nint cls = ObjCRuntime.GetClass("NSScreen");
        if (cls == 0)
        {
            return result;
        }

        nint screens = ObjCRuntime.SendId(cls, ObjCRuntime.Sel("screens"));
        if (screens == 0)
        {
            return result;
        }

        nint count = ObjCRuntime.SendNint(screens, ObjCRuntime.Sel("count"));
        for (nint i = 0; i < count; i++)
        {
            nint screen = ObjCRuntime.SendIdObj(screens, ObjCRuntime.Sel("objectAtIndex:"), i);
            if (screen == 0)
            {
                continue;
            }

            (double x, double y, double w, double h) = ReadFrame(screen);
            double scale = ObjCRuntime.SendDouble(screen, ObjCRuntime.Sel("backingScaleFactor"));
            if (scale <= 0)
            {
                scale = 1.0;
            }

            result.Add(new ScreenEntry(DisplayIdOf(screen), ReadName(screen), x, y, w, h, scale));
        }

        return result;
    }

    /// <summary>
    /// 主屏上沿（cocoa 坐标系的"最大 Y"）。翻转 y 轴时用它。
    /// </summary>
    /// <remarks>
    /// 取 <c>[NSScreen mainScreen]</c> 而不是"列表里第一块"：坐标系原点由 mainScreen 决定，
    /// 与枚举顺序无关。mainScreen 取不到（极早期）时退到第一块，保证不返回 NaN/0 之外的怪值。
    /// </remarks>
    private static double MainScreenTop(List<ScreenEntry> screens)
    {
        nint main = ObjCRuntime.GetClass("NSScreen") is var cls && cls != 0
            ? ObjCRuntime.SendId(cls, ObjCRuntime.Sel("mainScreen"))
            : 0;

        if (main != 0)
        {
            (double _, double y, double _, double h) = ReadFrame(main);
            return y + h;
        }

        return screens.Count > 0 ? screens[0].Y + screens[0].Height : 0;
    }

    /// <summary>主屏的缩放比（光标换算用）。</summary>
    private static double MainScreenScale()
    {
        nint cls = ObjCRuntime.GetClass("NSScreen");
        if (cls == 0)
        {
            return 1.0;
        }

        nint main = ObjCRuntime.SendId(cls, ObjCRuntime.Sel("mainScreen"));
        if (main == 0)
        {
            return 1.0;
        }

        double scale = ObjCRuntime.SendDouble(main, ObjCRuntime.Sel("backingScaleFactor"));
        return scale > 0 ? scale : 1.0;
    }

    /// <summary>把一台屏换算成本库的对外形状。</summary>
    private static OrielMonitor ToMonitor(ScreenEntry entry, double mainTop, uint mainDisplayId)
    {
        // cocoa 的下沿 → 左上角原点的 y
        double top = mainTop - (entry.Y + entry.Height);

        return new OrielMonitor(
            Name: entry.Name,
            Position: new OrielPosition(
                Math.Round(entry.X * entry.Scale),
                Math.Round(top * entry.Scale)),
            Size: new OrielSize(
                Math.Round(entry.Width * entry.Scale),
                Math.Round(entry.Height * entry.Scale)),
            ScaleFactor: entry.Scale)
        {
            // 用 AppKit 的 mainScreen 而不是 CGMainDisplayID 判定"主屏"：
            // 本库的坐标原点是由 mainScreen 的上沿翻出来的，两者必须是同一块屏，
            // 否则会出现"标着 IsPrimary 的那块不在 (0,0)"的自相矛盾结果。
            IsPrimary = entry.DisplayId != 0 && entry.DisplayId == mainDisplayId,
        };
    }

    /// <summary><c>[NSScreen mainScreen]</c> 的 display id；取不到时返回 0。</summary>
    private static uint AppKitMainDisplayId()
    {
        nint cls = ObjCRuntime.GetClass("NSScreen");
        return cls == 0
            ? 0
            : DisplayIdOf(ObjCRuntime.SendId(cls, ObjCRuntime.Sel("mainScreen")));
    }

    /// <summary>读 <c>NSValue</c> 包着的 rect（<c>frame</c> / <c>visibleFrame</c>）。</summary>
    /// <remarks>
    /// <c>getValue:</c> 往调用方给的缓冲区里写；这里给 4 个 double（NSRect 就是 4 个 CGFloat）。
    /// 走栈上缓冲而不是 <c>AllocHGlobal</c>：这个函数每次枚举每个屏都会调一次。
    /// </remarks>
    private static (double X, double Y, double Width, double Height) ReadFrame(nint screen)
    {
        nint boxed = ObjCRuntime.SendIdObj(
            screen, ObjCRuntime.Sel("valueForKey:"), ObjCRuntime.MakeNSString("frame"));

        if (boxed == 0)
        {
            return (0, 0, 0, 0);
        }

        double* buffer = stackalloc double[4];
        ObjCRuntime.SendVoidObj(boxed, ObjCRuntime.Sel("getValue:"), (nint)buffer);
        return (buffer[0], buffer[1], buffer[2], buffer[3]);
    }

    /// <summary>读屏名（<c>localizedName</c>，10.15+；旧系统上返回 null）。</summary>
    /// <remarks>
    /// 用 <c>respondsToSelector:</c> 先问一句：直接给旧系统发未实现的 selector 会抛
    /// <c>NSException</c>，那是不可捕获的原生崩溃（进程直接没了），不是能 try/catch 的异常。
    /// </remarks>
    private static string? ReadName(nint screen)
    {
        nint selector = ObjCRuntime.Sel("localizedName");
        if (!ObjCRuntime.SendBoolRetObj(screen, ObjCRuntime.Sel("respondsToSelector:"), selector))
        {
            return null;
        }

        nint localized = ObjCRuntime.SendId(screen, selector);
        if (localized == 0)
        {
            return null;
        }

        string name = ObjCRuntime.ToManagedString(localized);
        return string.IsNullOrEmpty(name) ? null : name;
    }

    /// <summary>从 NSScreen 的 <c>deviceDescription["NSScreenNumber"]</c> 取 display id。</summary>
    private static uint DisplayIdOf(nint screen)
    {
        if (screen == 0)
        {
            return 0;
        }

        nint description = ObjCRuntime.SendId(screen, ObjCRuntime.Sel("deviceDescription"));
        if (description == 0)
        {
            return 0;
        }

        nint number = ObjCRuntime.SendIdObj(
            description, ObjCRuntime.Sel("objectForKey:"), ObjCRuntime.MakeNSString("NSScreenNumber"));

        return number == 0 ? 0 : ObjCRuntime.SendUint(number, ObjCRuntime.Sel("unsignedIntValue"));
    }

    // ------------------------------------------------------------------
    // CoreGraphics：主屏 id 与光标
    // ------------------------------------------------------------------

    [LibraryImport(CoreGraphicsLib, EntryPoint = "CGMainDisplayID")]
    private static partial uint CGMainDisplayID();

    /// <summary>取主屏的 CG 尺寸（用来判定 CG 全局空间的单位）。</summary>
    [LibraryImport(CoreGraphicsLib, EntryPoint = "CGDisplayBounds")]
    private static partial CGRect CGDisplayBounds(uint display);

    [LibraryImport(CoreGraphicsLib, EntryPoint = "CGEventCreate")]
    private static partial nint CGEventCreate(nint source);

    [LibraryImport(CoreGraphicsLib, EntryPoint = "CGEventGetLocation")]
    private static partial CGPoint CGEventGetLocation(nint @event);

    [LibraryImport(CoreFoundationLib, EntryPoint = "CFRelease")]
    private static partial void CFRelease(nint cf);

    private static uint MainDisplayId()
    {
        try
        {
            return CGMainDisplayID();
        }
        catch (DllNotFoundException)
        {
            return 0;
        }
    }

    /// <summary>CG 的全局坐标是否以点为单位（否则是像素）。</summary>
    /// <remarks>
    /// 判据：同一块主屏，CG 的高度与 AppKit 的点高度一致 → CG 用点；等于
    /// "点高度 × 缩放" → CG 用像素。这个差异确实存在（同一份代码在不同 macOS 版本
    /// 上观察到的行为不同），所以不能写死任何一种。
    /// </remarks>
    private bool CgSpaceIsPoints()
    {
        if (_cgSpaceIsPoints is { } cached)
        {
            return cached;
        }

        bool points = true;
        try
        {
            CGRect bounds = CGDisplayBounds(MainDisplayId());
            double cgHeight = bounds.Size.Height;
            double pointHeight = MainScreenHeight();
            double scale = MainScreenScale();

            if (cgHeight > 0 && pointHeight > 0 && scale > 1)
            {
                double asPoints = Math.Abs(cgHeight - pointHeight);
                double asPixels = Math.Abs(cgHeight - pointHeight * scale);
                points = asPoints <= asPixels;
            }
        }
        catch (DllNotFoundException)
        {
            // 没有 CoreGraphics（不可能，但防御）：按最常见的情形处理。
        }

        _cgSpaceIsPoints = points;
        return points;
    }

    /// <summary>主屏高度（cocoa 点）。</summary>
    private static double MainScreenHeight()
    {
        nint cls = ObjCRuntime.GetClass("NSScreen");
        if (cls == 0)
        {
            return 0;
        }

        nint main = ObjCRuntime.SendId(cls, ObjCRuntime.Sel("mainScreen"));
        if (main == 0)
        {
            return 0;
        }

        (double _, double _, double _, double h) = ReadFrame(main);
        return h;
    }

    // ---- CG 的结构体（CGFloat 在 64 位上是 double）----

    [StructLayout(LayoutKind.Sequential)]
    private struct CGPoint
    {
        public double X;
        public double Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CGSize
    {
        public double Width;
        public double Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CGRect
    {
        public CGPoint Origin;
        public CGSize Size;
    }
}
