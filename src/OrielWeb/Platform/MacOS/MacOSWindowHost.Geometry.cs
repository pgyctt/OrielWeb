using System.Runtime.InteropServices;
using System.Text.Json;
using OrielWeb.Platform.MacOS.Interop;

namespace OrielWeb.Platform.MacOS;

/// <summary>尺寸、位置、DPI 换算与变化上报。</summary>
/// <remarks>本文件是 <see cref="MacOSWindowHost"/> 的 partial 分片，按关注点从原单文件搬来；成员原样搬运，未改一行逻辑。</remarks>
internal sealed partial class MacOSWindowHost
{
    public bool IsMaximized => ObjCRuntime.SendBoolRet(_nsWindow, ObjCRuntime.Sel("isZoomed"));

    /// <summary>设置缩放倍率（见 <see cref="IWindowBackend.SetZoom"/>）。</summary>
    /// <remarks>
    /// 落点是 <c>WKWebView.pageZoom</c>（1.0 = 100%，macOS 10.14+），与 WebView2 的
    /// ZoomFactor、WebKitGTK 的 zoom_level 语义一致。必须在主线程调——命令层本来就在。
    /// </remarks>
    public void SetZoom(double scale)
    {
        if (_webview == 0)
        {
            return;
        }

        ObjCRuntime.SendVoidDouble(_webview, ObjCRuntime.Sel("setPageZoom:"), scale);
    }

    /// <summary>取窗口所在的 <c>NSScreen</c>（取不到时退回主屏）。</summary>
    private nint ScreenOfWindow()
    {
        nint screen = _nsWindow == 0 ? 0 : ObjCRuntime.SendId(_nsWindow, ObjCRuntime.Sel("screen"));
        return screen != 0
            ? screen
            : ObjCRuntime.SendId(ObjCRuntime.GetClassOrThrow("NSScreen"), ObjCRuntime.Sel("mainScreen"));
    }

    /// <summary>取某个 <c>NSScreen</c> 的矩形（Cocoa 点，左下原点）。</summary>
    private static (double X, double Y, double Width, double Height) ScreenFrame(nint screen)
        => screen == 0 ? (0, 0, 0, 0) : ReadRect(screen, "frame");

    /// <summary>
    /// 按 KVC + <c>getValue:</c> 读一个 NSRect 属性（key 形如 <c>frame</c> / <c>bounds</c>）。
    /// </summary>
    /// <remarks>
    /// <b>不直取返回值</b>：NSRect 是结构体，x86_64 上要走 <c>objc_msgSend_stret</c>、
    /// arm64 上是普通返回——一个 P/Invoke 表达不了两套 ABI。本库读窗口 frame 的既有做法
    /// 就是这个（见 <c>ReadFrame</c>），这里抽成通用版本给 NSScreen / NSView 复用。
    /// </remarks>
    private static (double X, double Y, double Width, double Height) ReadRect(nint obj, string key)
    {
        if (obj == 0)
        {
            return (0, 0, 0, 0);
        }

        nint boxed = ObjCRuntime.SendIdObj(obj, ObjCRuntime.Sel("valueForKey:"), ObjCRuntime.MakeNSString(key));
        if (boxed == 0)
        {
            return (0, 0, 0, 0);
        }

        nint buffer = Marshal.AllocHGlobal(4 * sizeof(double));
        try
        {
            ObjCRuntime.SendVoidObj(boxed, ObjCRuntime.Sel("getValue:"), buffer);
            return (
                BitConverter.Int64BitsToDouble(Marshal.ReadInt64(buffer, 0)),
                BitConverter.Int64BitsToDouble(Marshal.ReadInt64(buffer, 8)),
                BitConverter.Int64BitsToDouble(Marshal.ReadInt64(buffer, 16)),
                BitConverter.Int64BitsToDouble(Marshal.ReadInt64(buffer, 24)));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>按屏幕**物理像素**坐标找屏幕；找不到返回 0。</summary>
    private static nint ScreenForPhysicalPoint(int x, int y)
    {
        nint cls = ObjCRuntime.GetClassOrThrow("NSScreen");
        nint main = ObjCRuntime.SendId(cls, ObjCRuntime.Sel("mainScreen"));
        if (main == 0)
        {
            return 0;
        }

        // 物理像素 → 点：主屏的缩放比；y 还要相对主屏高度翻转（Cocoa 是左下原点）
        double scale = ObjCRuntime.SendDouble(main, ObjCRuntime.Sel("backingScaleFactor"));
        if (scale <= 0)
        {
            scale = 1;
        }

        (double _, double _, double _, double mainHeight) = ScreenFrame(main);
        double px = x / scale;
        double py = mainHeight - (y / scale);

        nint screens = ObjCRuntime.SendId(cls, ObjCRuntime.Sel("screens"));
        if (screens == 0)
        {
            return main;
        }

        nint count = ObjCRuntime.SendNint(screens, ObjCRuntime.Sel("count"));
        for (nint i = 0; i < count; i++)
        {
            nint screen = ObjCRuntime.SendIdNint(screens, ObjCRuntime.Sel("objectAtIndex:"), i);
            (double sx, double sy, double sw, double sh) = ScreenFrame(screen);
            if (px >= sx && px < sx + sw && py >= sy && py < sy + sh)
            {
                return screen;
            }
        }

        return 0;
    }

    /// <summary>设置最大尺寸（见 <see cref="IWindowBackend.SetMaxSize"/>）。</summary>
    /// <remarks>
    /// <c>setContentMaxSize:</c> 收 <c>NSSize</c>。<b>传 0 表示不限制</b>——
    /// Cocoa 的约定是用一个很大的值（<c>CGFLOAT_MAX</c>）表示"没有上限"，
    /// 而不是 0（0 会把窗口卡成 0 宽）。这里用 <c>double.MaxValue</c> 对应它。
    /// </remarks>
    public void SetMaxSize(int width, int height)
    {
        _maxWidth = width;
        _maxHeight = height;

        if (_nsWindow == 0)
        {
            return;
        }

        // 0 → "没有上限"。用 double.MaxValue 而不是 0：后者是"最多 0 像素"。
        ObjCRuntime.SendVoidDouble2(
            _nsWindow,
            ObjCRuntime.Sel("setContentMaxSize:"),
            width > 0 ? width : double.MaxValue,
            height > 0 ? height : double.MaxValue);
    }

    /// <summary>
    /// 取当前拖动位置（客户区物理像素）。
    /// </summary>
    /// <remarks>
    /// <b>不用 <c>-draggingLocation</c></b>：它返回 <c>NSPoint</c> 结构体，而本库的约定是不做
    /// struct 返回的 <c>objc_msgSend</c>（x86_64 要走 <c>objc_msgSend_stret</c>、arm64 是隐式 sret，
    /// 绑定里没有那条路）。拖动期间鼠标就在拖动点上，所以用 <c>CGEventGetLocation</c> 的全局位置
    /// 减去客户区原点，结果一致，且完全绕开结构体返回的 ABI。
    /// </remarks>
    internal (int X, int Y) DragPosition()
    {
        try
        {
            OrielPosition cursor = App.MonitorBackend.GetCursorPosition(); // 物理像素、全局左上原点
            OrielPosition inner = InnerPosition;                            // 逻辑像素、客户区左上原点
            double scale = ScaleFactor;

            return ((int)Math.Round(cursor.X - (inner.X * scale)),
                    (int)Math.Round(cursor.Y - (inner.Y * scale)));
        }
        catch (Exception ex)
        {
            OrielDiagnostics.Report($"[OrielWeb] 读取拖动坐标失败：{ex}");
            return (0, 0);
        }
    }

    /// <summary>
    /// 窗口所在显示器的缩放比（见 <see cref="IWindowBackend.ScaleFactor"/>）。
    /// </summary>
    /// <remarks>
    /// 取 <c>backingScaleFactor</c>（Retina 上为 2.0）。窗口 API 收的是**点**，
    /// 点与逻辑像素一一对应，所以换算 <c>PhysicalSize</c> 时除以它正是想要的。
    /// 取 <c>window.screen</c> 而不是 <c>NSScreen.mainScreen</c>：后者永远是主屏，
    /// 窗口被拖到副屏（不同缩放）时会给错倍数。
    /// </remarks>
    public double ScaleFactor
    {
        get
        {
            if (_nsWindow == 0)
            {
                return 1.0;
            }

            nint screen = ObjCRuntime.SendId(_nsWindow, ObjCRuntime.Sel("screen"));
            if (screen == 0)
            {
                return 1.0;
            }

            double scale = ObjCRuntime.SendDouble(screen, ObjCRuntime.Sel("backingScaleFactor"));
            return scale > 0 ? scale : 1.0;
        }
    }

    /// <summary>
    /// 客户区尺寸（逻辑像素）；见 <see cref="IWindowBackend.InnerSize"/>。
    /// </summary>
    /// <remarks>
    /// 读 <c>contentView.frame</c> 而不是整窗 frame 减标题栏：contentView 就是客户区，
    /// 无边框（FullSizeContentView）与装饰窗口下它都给出正确答案，而"减标题栏"要猜高度。
    /// 注意 macOS 的"点"与逻辑像素一一对应，所以**不**需要除缩放比。
    /// </remarks>
    public OrielSize InnerSize
    {
        get
        {
            if (ReadContentFrame() is not { } content)
            {
                return default;
            }

            return new OrielSize(content.Width, content.Height);
        }
    }

    /// <summary>整窗尺寸（逻辑像素）；见 <see cref="IWindowBackend.OuterSize"/>。</summary>
    public OrielSize OuterSize
    {
        get
        {
            if (_nsWindow == 0)
            {
                return default;
            }

            (_, _, double width, double height) = ReadFrame();
            return new OrielSize(width, height);
        }
    }

    /// <summary>
    /// 窗口左上角（逻辑像素、左上角原点）；见 <see cref="IWindowBackend.OuterPosition"/>。
    /// </summary>
    /// <remarks>
    /// cocoa 的原点在左下、y 向上，而公共契约是左上角原点——所以按主屏高度换算：
    /// <c>y_app = maxY - (origin.y + height)</c>。与 <c>ReportPosition</c> 用的是同一套换算。
    /// </remarks>
    public OrielPosition OuterPosition
    {
        get
        {
            if (_nsWindow == 0)
            {
                return default;
            }

            (double originX, double originY, _, double height) = ReadFrame();
            return new OrielPosition(originX, ScreenMaxY() - (originY + height));
        }
    }

    /// <summary>
    /// 客户区左上角（逻辑像素、左上角原点）；见 <see cref="IWindowBackend.InnerPosition"/>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 不需要 <c>contentRectForFrameRect:</c>：**contentView 锚在窗口的左下角**
    /// （cocoa 的 y 向上，窗口底边就是内容区底边），所以内容区顶边 = <c>frame.origin.y + contentHeight</c>。
    /// 两者之差正好是标题栏高度。装饰窗口与无边框窗口（<c>StyleFullSizeContentView</c>）
    /// 都成立——后者的 contentHeight 等于 frameHeight，于是内外位置相等。
    /// </para>
    /// <para>
    /// 那个 ObjC 方法的返回值是 NSRect 结构体，msgSend 直取要走 stret（x86_64）/ 隐式 sret（arm64），
    /// 绑定里没有那条路。上面的算法用两次已有的 frame 读取就得出同样结果，没有理由再引一层结构体传参。
    /// </para>
    /// </remarks>
    public OrielPosition InnerPosition
    {
        get
        {
            if (_nsWindow == 0 || ReadContentFrame() is not { } content)
            {
                return OuterPosition;
            }

            (double originX, double originY, _, _) = ReadFrame();
            return new OrielPosition(originX, ScreenMaxY() - (originY + content.Height));
        }
    }

    /// <summary>读 contentView 的 frame（客户区，cocoa 点）；取不到时返回 null。</summary>
    private (double X, double Y, double Width, double Height)? ReadContentFrame()
    {
        if (_nsWindow == 0)
        {
            return null;
        }

        nint contentView = ObjCRuntime.SendId(_nsWindow, ObjCRuntime.Sel("contentView"));
        if (contentView == 0)
        {
            return null;
        }

        nint boxed = ObjCRuntime.SendIdObj(
            contentView, ObjCRuntime.Sel("valueForKey:"), ObjCRuntime.MakeNSString("frame"));
        if (boxed == 0)
        {
            return null;
        }

        nint buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(32);
        try
        {
            ObjCRuntime.SendVoidObj(boxed, ObjCRuntime.Sel("getValue:"), buffer);
            return (
                BitConverter.Int64BitsToDouble(System.Runtime.InteropServices.Marshal.ReadInt64(buffer, 0)),
                BitConverter.Int64BitsToDouble(System.Runtime.InteropServices.Marshal.ReadInt64(buffer, 8)),
                BitConverter.Int64BitsToDouble(System.Runtime.InteropServices.Marshal.ReadInt64(buffer, 16)),
                BitConverter.Int64BitsToDouble(System.Runtime.InteropServices.Marshal.ReadInt64(buffer, 24)));
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// 读取当前最大化状态（NSWindow isZoomed），与上次上报值比对，变化时（或 <paramref name="force"/> 时）
    /// 上报给托管事件与页面。调用时机：ToggleMaximize（zoom: 同步生效）、windowDidResize（原生 Zoom 路径）、
    /// 每次导航完成。
    /// </summary>
    internal bool SyncMaximizedState(bool force = false)
    {
        bool isMaximized = IsMaximized;
        if (!force && isMaximized == _wasMaximized)
        {
            return isMaximized;
        }

        _wasMaximized = isMaximized;
        MaximizedChanged?.Invoke(isMaximized);
        PushEventOnUi("maximized", isMaximized ? "true" : "false");
        return isMaximized;
    }

    public void SetMinSize(int width, int height)
    {
        _minWidth = width;
        _minHeight = height;
        ObjCRuntime.SendVoidDouble2(_nsWindow, ObjCRuntime.Sel("setContentMinSize:"), width, height);
    }

    public void MoveTo(int x, int y)
    {
        // API.md 的契约：屏幕坐标（应用像素、**左上角**原点）。cocoa 是左下角原点且 y 向上，
        // 顶左点 (x, y_app) 的 cocoa Y = maxY - y_app；setFrameTopLeftPoint 直接按顶左语义
        // 落位，连窗口高度都不需要。（旧实现把 x/y 当左下角坐标原样喂给 setFrameOrigin:，
        // 与公共契约相反——同文件 BeginDragStreaming 的 sh - wy - wh 才是契约本意。）
        double maxY = ScreenMaxY();
        ObjCRuntime.SendVoidDouble2(_nsWindow, ObjCRuntime.Sel("setFrameTopLeftPoint:"), x, maxY - y);
    }

    /// <summary>
    /// 主屏高度的上沿（cocoa 点、y 向上坐标系的最大 Y）。"左上角原点"契约换算成 cocoa
    /// 坐标时要用它。NSRect 是结构体：msgSend 直取要走 stret（x86_64）/ 隐式 sret（arm64），
    /// 绑定里没有那条路——改走 KVC：<c>valueForKey:@"frame"</c> 得到 NSValue，再
    /// <c>getValue:</c> 按指针读出四个 double（origin.x/y、size.w/h）。
    /// </summary>
    private double ScreenMaxY()
    {
        nint screen = ObjCRuntime.SendId(ObjCRuntime.GetClass("NSScreen"), ObjCRuntime.Sel("mainScreen"));
        nint boxed = screen == 0
            ? 0
            : ObjCRuntime.SendIdObj(screen, ObjCRuntime.Sel("valueForKey:"), ObjCRuntime.MakeNSString("frame"));
        if (boxed == 0)
        {
            return 0;
        }

        nint buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(32);
        try
        {
            ObjCRuntime.SendVoidObj(boxed, ObjCRuntime.Sel("getValue:"), buffer);
            // Marshal 没有 ReadDouble：按 8 字节整数读出再按位转回 double（IEEE 754 直通）
            return BitConverter.Int64BitsToDouble(System.Runtime.InteropServices.Marshal.ReadInt64(buffer, 8))    // origin.y
                 + BitConverter.Int64BitsToDouble(System.Runtime.InteropServices.Marshal.ReadInt64(buffer, 24)); // size.height
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer);
        }
    }

    // ------------------------------------------------------------------
    // 尺寸 / 位置 / 焦点上报（对标 Tauri 2 的 onResized / onMoved / onFocusChanged）
    // ------------------------------------------------------------------

    /// <summary>
    /// 读窗口的 cocoa frame（origin.x、origin.y、size.width、size.height，单位 cocoa 点）。
    /// </summary>
    /// <remarks>
    /// 走 KVC + <c>getValue:</c> 而不是直取 <c>frame</c> 的返回值：NSRect 是结构体，
    /// msgSend 直取在 x86_64 上要走 stret、arm64 上是隐式 sret，绑定里没有那条路。
    /// 与 <see cref="ScreenMaxY"/> 用的是同一套读法（它读的是 NSScreen 的 frame）。
    /// </remarks>
    private (double X, double Y, double Width, double Height) ReadFrame()
    {
        nint boxed = ObjCRuntime.SendIdObj(_nsWindow, ObjCRuntime.Sel("valueForKey:"), ObjCRuntime.MakeNSString("frame"));
        if (boxed == 0)
        {
            return (0, 0, 0, 0);
        }

        nint buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(32);
        try
        {
            ObjCRuntime.SendVoidObj(boxed, ObjCRuntime.Sel("getValue:"), buffer);
            return (
                BitConverter.Int64BitsToDouble(System.Runtime.InteropServices.Marshal.ReadInt64(buffer, 0)),   // origin.x
                BitConverter.Int64BitsToDouble(System.Runtime.InteropServices.Marshal.ReadInt64(buffer, 8)),   // origin.y
                BitConverter.Int64BitsToDouble(System.Runtime.InteropServices.Marshal.ReadInt64(buffer, 16)),  // size.width
                BitConverter.Int64BitsToDouble(System.Runtime.InteropServices.Marshal.ReadInt64(buffer, 24))); // size.height
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// 上报客户区尺寸（逻辑像素）。
    /// </summary>
    /// <remarks>
    /// 用 <c>contentLayoutRect</c> 的等价量：先取整窗 frame 的 size，再减去标题栏高度。
    /// 但更可靠的是直接问 <c>contentView</c> 的 frame——它在无边框（FullSizeContentView）
    /// 与装饰窗口下都给出**客户区**，而整窗 frame 在无边框下等于客户区、装饰下多一圈标题栏。
    /// </remarks>
    internal void ReportGeometry()
    {
        if (_nsWindow == 0)
        {
            return;
        }

        // 窗口几何变了就顺带看一眼缩放比：窗口被拖到不同缩放的显示器时，
        // windowDidResize: / windowDidMove: 至少有一条会发（见下面 ReportScaleFactorIfChanged 的说明）。
        ReportScaleFactorIfChanged();

        nint contentView = ObjCRuntime.SendId(_nsWindow, ObjCRuntime.Sel("contentView"));
        if (contentView == 0)
        {
            return;
        }

        // contentView 的 frame 直接就是客户区尺寸（cocoa 点 = 逻辑像素，macOS 不需要额外折算）
        nint boxed = ObjCRuntime.SendIdObj(contentView, ObjCRuntime.Sel("valueForKey:"), ObjCRuntime.MakeNSString("frame"));
        if (boxed == 0)
        {
            return;
        }

        nint buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(32);
        try
        {
            ObjCRuntime.SendVoidObj(boxed, ObjCRuntime.Sel("getValue:"), buffer);
            double width = BitConverter.Int64BitsToDouble(System.Runtime.InteropServices.Marshal.ReadInt64(buffer, 16));
            double height = BitConverter.Int64BitsToDouble(System.Runtime.InteropServices.Marshal.ReadInt64(buffer, 24));
            ReportSize(width, height);
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// 上报窗口位置（逻辑像素、**左上角原点**）。
    /// </summary>
    /// <remarks>
    /// cocoa 的原点在左下、y 向上，而公共契约是左上角原点——所以要按主屏高度换算：
    /// <c>y_app = maxY - (origin.y + height)</c>。这与 <see cref="MoveTo"/> 的换算是同一套
    /// （那边是 <c>setFrameTopLeftPoint</c> 直接吃左上角语义，方向相反但依据相同）。
    /// </remarks>
    /// <summary>
    /// 缩放比变了就上报（见 <see cref="IWindowBackend.ScaleFactorChanged"/>）。
    /// </summary>
    /// <remarks>
    /// <b>触发点有三个</b>：<c>windowDidResize:</c>（经 <see cref="ReportGeometry"/>）、
    /// <c>windowDidMove:</c>（经 <see cref="ReportPosition()"/>），以及
    /// <c>windowDidChangeBackingProperties:</c>（直接调本方法）。
    /// <para>
    /// 前两个覆盖"窗口被拖到不同缩放的显示器"；第三个覆盖"只改系统缩放、窗口不动"
    /// ——那时前两条都不发。三个都是 <c>NSWindowDelegate</c> 的方法，
    /// 所以不需要额外的通知观察者（早先记的"已知漏报"就是这么补掉的）。
    /// </para>
    /// <para>
    /// payload 里的 <c>size</c> 是**物理像素**客户区（与 Tauri 的 <c>PhysicalSize</c> 对齐）。
    /// </para>
    /// </remarks>
    internal void ReportScaleFactorIfChanged()
    {
        double scale = ScaleFactor;
        if (_lastReportedScaleFactor is { } previous && Math.Abs(previous - scale) < 0.0001)
        {
            return;
        }

        _lastReportedScaleFactor = scale;
        ScaleFactorChanged?.Invoke(scale);

        if ((EventForwarding & OrielWindowEventKind.Scale) != 0)
        {
            OrielSize inner = InnerSize;
            PushEventOnUi(
                "scale.changed",
                OrielEventPayload.ScaleFactor(scale, inner.Width * scale, inner.Height * scale));
        }
    }

    internal void ReportPosition()
    {
        if (_nsWindow == 0)
        {
            return;
        }

        (double originX, double originY, _, double height) = ReadFrame();
        double maxY = ScreenMaxY();
        ReportPosition(maxY - (originY + height), originX);
    }

    /// <summary>上报位置（已换算成左上角原点）；与上次相同则跳过。</summary>
    private void ReportPosition(double x, double y)
    {
        if (_lastReportedPosition is { } previous
            && Math.Abs(previous.X - x) < 0.5
            && Math.Abs(previous.Y - y) < 0.5)
        {
            return;
        }

        _lastReportedPosition = (x, y);
        Moved?.Invoke(new OrielPositionChangedEventArgs(x, y));

        // 页面侧转发是按需的（默认关）：拖动时这条路径每个像素走一次，
        // 而这里的推送是一次 JS 求值。见 OrielWindowEventKind。
        if ((EventForwarding & OrielWindowEventKind.Move) != 0)
        {
            PushEventOnUi("move", OrielEventPayload.Position(x, y));
        }
    }

    /// <summary>上报客户区尺寸；与上次相同则跳过。</summary>
    /// <remarks>
    /// 去重是必须的：拖边框时 <c>windowDidResize:</c> 每个像素来一次，
    /// 每次都触发托管事件 + 页面 IPC 会把一次拖动变成流量风暴。
    /// </remarks>
    private void ReportSize(double width, double height)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        if (_lastReportedSize is { } previous
            && Math.Abs(previous.Width - width) < 0.5
            && Math.Abs(previous.Height - height) < 0.5)
        {
            return;
        }

        _lastReportedSize = (width, height);
        Resized?.Invoke(new OrielSizeChangedEventArgs(width, height));

        if ((EventForwarding & OrielWindowEventKind.Resize) != 0)
        {
            PushEventOnUi("resize", OrielEventPayload.Size(width, height));
        }
    }

    /// <summary>上报焦点变化（与上次相同则跳过；首个事件一定会报）。</summary>
    internal void ReportFocus(bool focused)
    {
        if (_lastReportedFocus == focused)
        {
            return;
        }

        _lastReportedFocus = focused;
        FocusChanged?.Invoke(focused);

        // 页面侧是两个事件名（focus / blur）而不是一个带布尔值的事件——
        // 那是 Tauri 的形状，页面可以只订阅其中一个。
        if ((EventForwarding & OrielWindowEventKind.Focus) != 0)
        {
            PushEventOnUi(focused ? "focus" : "blur", "null");
        }
    }

    public void Resize(int width, int height)
    {
        ObjCRuntime.SendVoidDouble2(_nsWindow, ObjCRuntime.Sel("setContentSize:"), width, height);
    }

    public void Center() => ObjCRuntime.SendVoid(_nsWindow, ObjCRuntime.Sel("center"));
}
