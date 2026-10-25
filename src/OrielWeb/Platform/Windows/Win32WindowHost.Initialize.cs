using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Text;
using System.Text.Json;
using DirectN.Extensions.Com;
using OrielWeb.Ipc;
using OrielWeb.Platform.Windows.Interop;
using WebView2;
using WebView2.Utilities;

// DirectN 全名空间导入会与本地的 Win32 结构（如 WINDOWPLACEMENT、RECT）冲突，故只取需要的类型。
using DCompDevice = DirectN.IDCompositionDevice;
using DCompFunctions = DirectN.Functions;
using DCompHWND = DirectN.HWND;
using DCompPoint = DirectN.POINT;
using DCompTarget = DirectN.IDCompositionTarget;
using DCompVisual = DirectN.IDCompositionVisual;

namespace OrielWeb.Platform.Windows;

/// <summary>创建流程：窗口类注册、样式计算、WebView2 装配与初始图标。</summary>
/// <remarks>本文件是 <see cref="Win32WindowHost"/> 的 partial 分片，按关注点从原单文件搬来；成员原样搬运，未改一行逻辑。</remarks>
internal partial class Win32WindowHost
{
    // ------------------------------------------------------------------
    // 创建
    // ------------------------------------------------------------------

    /// <summary>
    /// 创建窗口：注册窗口类（幂等）→ CreateWindowExW（宿主经 lpParam 存入 GWLP_USERDATA，
    /// WndProc 据此取回实例）→ 触发 PostCreate（装配 WebView2）。
    /// </summary>
    public static unsafe Win32WindowHost Create(WebviewWindow window, OrielWindowOptions options, OrielApp app, EmbeddedAssetStore? assets, WindowsPlatformBackend backend)
    {
        EnsureWindowClass();
        var host = new Win32WindowHost(window, options, app, assets, backend);
        host._selfHandle = GCHandle.Alloc(host);

        uint style = Win32Conversions.ComputeStyle(options);
        int x = options.X ?? Win32Constants.CW_USEDEFAULT;
        int y = options.Y ?? Win32Constants.CW_USEDEFAULT;

        nint windowNamePtr = Marshal.StringToHGlobalUni(options.Title);
        var hwnd = Win32.CreateWindowExW(
            // 无重定向表面：窗口内容完全由 DirectComposition 提供，WebView2 作为视觉合成进来。
            // 这是 Composition 宿主的前提，也让 WebView2 不再是子窗口——边缘的 WM_NCHITTEST
            // 因此能到达本窗口，系统原生的边缘调整大小随之可用。
            // 接受文件拖放：Composition 宿主下 WebView2 不是子窗口（没有自己的 HWND），
            // 所以拖到窗口任意位置都会落到本窗口的 WM_DROPFILES，不会被 webview 截走。
            Win32Constants.WS_EX_NOREDIRECTIONBITMAP | Win32Constants.WS_EX_ACCEPTFILES,
            s_windowClassNamePtr,
            windowNamePtr,
            style,
            x, y, options.Width, options.Height,
            0, 0,
            Win32.GetModuleHandleW(null),
            (void*)GCHandle.ToIntPtr(host._selfHandle));
        Marshal.FreeHGlobal(windowNamePtr); // CreateWindowExW 已复制字符串

        if (hwnd == 0)
        {
            host._selfHandle.Free();
            throw new InvalidOperationException($"创建窗口失败（Win32 错误 {Marshal.GetLastWin32Error()}）。");
        }
        host._hwnd = hwnd;

        // 尺寸与位置统一为**逻辑像素的客户区**（与 Linux/macOS 的 WithSize/At 同语义）：
        //   * 逻辑像素 → 物理像素：Per-Monitor-V2 下按**窗口所在显示器**的 DPI 折算。
        //     不折算的后果是 150%/200% 屏上窗口只有一半大（评审 2026-10-08 发现 2 修的就是位置漏折算）。
        //   * 客户区 → 整窗：Win32 的 CreateWindowExW / SetWindowPos 吃的是**整窗**尺寸，
        //     而 Linux 的 gtk_window_resize 与 macOS 的 setContentSize: 吃的是**客户区**。
        //     不换算的话同一份 WithSize(800,600) 在 Windows 上得到 784x561 的客户区
        //     （实测，见 --selftest geometry），而另两个平台得到 800x600。
        //     **frameless 窗口不换算**：它的 WM_NCCALCSIZE 把客户区铺满整窗，客户区已等于整窗。
        // 时机在窗口已建、尚未显示（PostCreate 才 ShowWindow）——用户看不到这次修正。
        uint initialDpi = Win32.GetDpiForWindow(hwnd);
        int physicalWidth = LogicalPixels.ToPhysical(options.Width, initialDpi);
        int physicalHeight = LogicalPixels.ToPhysical(options.Height, initialDpi);

        if (!options.Frameless)
        {
            (physicalWidth, physicalHeight) = ClientToWindowSize(
                physicalWidth, physicalHeight, style, initialDpi);
        }

        // 位置只在显式给了 At(x,y) 时才动：没给时窗口保持系统摆位（或稍后由 Center 摆），
        // 用 SWP_NOMOVE 保住它。At 一次给两维，缺一维按"没给位置"处理。
        // 注意位置**不需要**做客户区换算：At 定的是窗口左上角，与客户区大小无关。
        if (initialDpi != 96 || !options.Frameless)
        {
            if (options.X is int atX && options.Y is int atY)
            {
                _ = Win32.SetWindowPos(
                    hwnd, 0,
                    LogicalPixels.ToPhysical(atX, initialDpi), LogicalPixels.ToPhysical(atY, initialDpi),
                    physicalWidth, physicalHeight,
                    Win32Constants.SWP_NOZORDER | Win32Constants.SWP_NOACTIVATE);
            }
            else
            {
                _ = Win32.SetWindowPos(
                    hwnd, 0, 0, 0, physicalWidth, physicalHeight,
                    Win32Constants.SWP_NOMOVE | Win32Constants.SWP_NOZORDER | Win32Constants.SWP_NOACTIVATE);
            }
        }

        // WithIcon：覆盖窗口类带来的（exe）图标
        if (!string.IsNullOrEmpty(options.Icon))
        {
            ApplyExplicitIcon(hwnd, options.Icon);
        }

        try
        {
            host.SetupComposition();
            host.PostCreate();
        }
        catch
        {
            // 窗口已经建出来了：中段失败（如 DComp 设备创建不了）必须收掉 HWND 与根引用，
            // 否则窗口残留、WndProc 还在经 GWLP_USERDATA 把消息路由给这个半成品宿主。
            // DestroyWindow **同步**触发 WM_DESTROY → OnWindowDestroyedCore，那里已经放掉根引用、
            // 并按"未交付"跳过了应用可见的副作用；这里的调用只是幂等兜底——重复 Free 会抛
            // InvalidOperationException，把真正的失败原因顶掉（评审 2026-10-08 发现 1）。
            _ = Win32.DestroyWindow(hwnd);
            host.FreeSelfHandle();
            throw;
        }

        // 交付：此后销毁才触发 Closed 与后端窗口计数（见 _handedOff 的说明）。
        host._handedOff = true;
        return host;
    }

    /// <summary>
    /// 建立 DirectComposition 合成树：设备 → 窗口目标 → 根视觉。WebView2 稍后作为视觉挂到根视觉上
    /// （见 <see cref="InitializeWebView2Async"/>）。
    /// </summary>
    /// <remarks>
    /// 直接用 dcomp.h 的 COM 接口，而不是 WinRT 的 Windows.UI.Composition：后者需要
    /// net10.0-windows TFM，而本库面向跨平台的 net10.0。DirectNAot 正是 Win32 COM 的 AOT 绑定。
    /// </remarks>
    private unsafe void SetupComposition()
    {
        int hr = DCompFunctions.DCompositionCreateDevice(null, typeof(DCompDevice).GUID, out nint devicePtr);
        if (hr < 0 || devicePtr == 0)
        {
            throw new InvalidOperationException($"创建 DirectComposition 设备失败（HRESULT 0x{hr:X8}）。");
        }

        _dcompDevice = ComInterfaceMarshaller<DCompDevice>.ConvertToManaged((void*)devicePtr)
            ?? throw new InvalidOperationException("DirectComposition 设备接口转换失败。");

        hr = _dcompDevice.CreateTargetForHwnd(new DCompHWND(_hwnd), topmost: true, out var target);
        if (hr < 0)
        {
            throw new InvalidOperationException($"创建 DirectComposition 窗口目标失败（HRESULT 0x{hr:X8}）。");
        }
        _dcompTarget = target;

        hr = _dcompDevice.CreateVisual(out var visual);
        if (hr < 0)
        {
            throw new InvalidOperationException($"创建 DirectComposition 视觉失败（HRESULT 0x{hr:X8}）。");
        }
        _dcompVisual = visual;

        if (_dcompTarget.SetRoot(_dcompVisual) < 0 || _dcompDevice.Commit() < 0)
        {
            throw new InvalidOperationException("提交 DirectComposition 合成树失败。");
        }
    }

    private void PostCreate()
    {
        if (_options.Center && _options.X is null && _options.Y is null)
        {
            Center();
        }

        // 可见性与最大化是**两个正交维度**，不能塞进同一条三元链：Hidden 优先时只发了
        // SW_HIDE，SW_SHOWMAXIMIZED 那条分支从未执行，WS_MAXIMIZE 因此从未建立——
        // 之后 Show() 的 SW_SHOW 只能把窗口按还原态显示（下游 2026-10-25 报的正是这个组合）。
        // 隐藏启动时把最大化记成"待生效"，由 Show() 一次到位；见 _maximizeOnShow 的说明。
        _maximizeOnShow = _options.Hidden && _options.Maximized;

        int showCommand = _options.Hidden ? Win32Constants.SW_HIDE
            : _options.Maximized ? Win32Constants.SW_SHOWMAXIMIZED
            : Win32Constants.SW_SHOW;
        Win32.ShowWindow(_hwnd, showCommand);

        if (_options.OnTop)
        {
            SetOnTop(true);
        }
        if (_options.Fullscreen)
        {
            SetFullscreen(true);
        }

        InitializeWebView2();

        // 创建期的底色在 InitializeWebView2 里就应用过了（_controller 在那里才建出来），
        // 这里不再重复。
    }

    /// <summary>用 <see cref="OrielWindowOptions.Icon"/> 指定的文件覆盖本窗口的图标。</summary>
    /// <remarks>
    /// 大/小两个尺寸分别按当前系统度量加载（小图标用于标题栏与 Alt-Tab，大图标用于任务栏）。
    /// 走 <c>WM_SETICON</c> 而不是改窗口类：窗口类是进程级共享的，而图标是每窗口的选项。
    /// 加载到的 HICON 不释放——WM_SETICON 不接管所有权，但窗口销毁时要逐个跟踪释放，
    /// 而句柄数量与窗口数同阶（实际应用通常 1～2 个窗口），这里选择明确不释放而不是引入一套句柄生命周期管理。
    /// <para>
    /// <b>PNG 走另一条路</b>：<c>LoadImageW</c> 只认 ICO/BMP，喂 PNG 会返回 0（静默不生效）。
    /// 所以这里先看扩展名，是 PNG 就先解码成 RGBA 再 <c>CreateIconIndirect</c>——
    /// 这与创建期图标用的是同一套（<see cref="OrielImage"/>）。
    /// </para>
    /// </remarks>
    private static void ApplyExplicitIcon(nint hwnd, string path)
    {
        // PNG：LoadImageW 不认，先解码再手工造 HICON。
        // 判扩展名而不是试着解码：路径可能是不存在的文件，解码失败与"格式不支持"
        // 要给出不同的行为（前者静默走默认图标，后者才需要报错）。
        if (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            ApplyPngIcon(hwnd, path);
            return;
        }

        nint large = Win32.LoadImageW(
            0, path, Win32Constants.IMAGE_ICON,
            Win32.GetSystemMetrics(Win32Constants.SM_CXICON),
            Win32.GetSystemMetrics(Win32Constants.SM_CYICON),
            Win32Constants.LR_LOADFROMFILE);
        nint small = Win32.LoadImageW(
            0, path, Win32Constants.IMAGE_ICON,
            Win32.GetSystemMetrics(Win32Constants.SM_CXSMICON),
            Win32.GetSystemMetrics(Win32Constants.SM_CYSMICON),
            Win32Constants.LR_LOADFROMFILE);

        if (large != 0)
        {
            _ = Win32.SendMessageW(hwnd, Win32Constants.WM_SETICON, Win32Constants.ICON_BIG, large);
        }
        if (small != 0)
        {
            _ = Win32.SendMessageW(hwnd, Win32Constants.WM_SETICON, Win32Constants.ICON_SMALL, small);
        }
    }

    /// <summary>把 PNG 文件解码成 RGBA 再造成 HICON 设上去（见 <see cref="ApplyExplicitIcon"/>）。</summary>
    private static void ApplyPngIcon(nint hwnd, string path)
    {
        if (!OrielImage.TryFromFile(path, out OrielImage? image, out string? failure) || image is null)
        {
            // 路径不存在或不是可识别的 PNG/ICO：保持现有图标，不抛——
            // 与 LoadImageW 返回 0 时的行为一致（创建期图标也是这个语义）。
            // 但**记一笔**：与字节那条路（SetIconFromBytes）保持对称——两条路都是
            // "静默保持原图标"，就不该只有一条留下诊断。
            OrielDiagnostics.Report(
                $"[OrielWeb] setIcon 的图标文件无法解码（保持原图标）：{path}" +
                (string.IsNullOrEmpty(failure) ? "。" : $"——{failure}"));
            return;
        }

        // 复用托盘那套（Win32IconFactory）：它已经处理好了自顶向下 DIB、掩码位初始化
        // 与全部中间句柄的释放，没有理由再写第二份。
        nint icon = Win32IconFactory.CreateFromRgba(image);
        if (icon != 0)
        {
            SetIconHandles(hwnd, icon);
        }
    }

    /// <summary>
    /// 把同一个 HICON 同时设到大、小两个槽位（见 <see cref="ApplyExplicitIcon"/>）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>两个槽位都要设</b>：<c>ICON_BIG</c> 给任务栏与 Alt-Tab 大视图，<c>ICON_SMALL</c>
    /// 给标题栏与 Alt-Tab 小视图。只设大图标的表现是"图标换了，但标题栏那个还是旧的"
    /// ——而 ICO/BMP 路径（<see cref="ApplyExplicitIcon"/> 里走 <c>LoadImageW</c> 那条）
    /// 一直是两个都设，所以这里必须对齐，否则"设了图标但部分没生效"会按输入格式分叉。
    /// </para>
    /// <para>
    /// 同一个句柄设两次是合法的：<c>WM_SETICON</c> 不接管所有权，系统按槽位各记一份。
    /// 本库的图标句柄本来就不释放（见 <see cref="ApplyExplicitIcon"/> 的说明），
    /// 所以这里不需要额外跟踪。
    /// </para>
    /// </remarks>
    private static void SetIconHandles(nint hwnd, nint icon)
    {
        _ = Win32.SendMessageW(hwnd, Win32Constants.WM_SETICON, Win32Constants.ICON_BIG, icon);
        _ = Win32.SendMessageW(hwnd, Win32Constants.WM_SETICON, Win32Constants.ICON_SMALL, icon);
    }

    private static unsafe void EnsureWindowClass()
    {
        if (Volatile.Read(ref s_windowClassRegistered) == 1)
        {
            return;
        }

        lock (s_windowClassGate)
        {
            if (Volatile.Read(ref s_windowClassRegistered) == 1)
            {
                return;
            }

            // 任务栏 / Alt-Tab 的按钮图标取自**窗口图标**。窗口完全没有图标时，shell 退回的是
            // 通用应用图标——不是 exe 自带的图标（实测确认过），所以这里必须显式把 exe 图标设到
            // 窗口类上，否则即使 csproj 配了 ApplicationIcon，运行期任务栏仍是通用图标。
            (nint hIcon, nint hIconSm) = LoadExecutableIcons();

            var windowClass = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                style = Win32Constants.CS_HREDRAW | Win32Constants.CS_VREDRAW | Win32Constants.CS_DBLCLKS,
                lpfnWndProc = (nint)(delegate* unmanaged<nint, uint, nuint, nint, nint>)&WindowProc,
                hInstance = Win32.GetModuleHandleW(null),
                hIcon = hIcon,
                hCursor = Win32.LoadCursorW(0, Win32Constants.IDC_ARROW),
                hbrBackground = 0, // WebView2 自绘客户端区，置空避免闪烁
                lpszClassName = s_windowClassNamePtr,
                hIconSm = hIconSm,
            };
            if (Win32.RegisterClassExW(ref windowClass) == 0)
            {
                int error = Marshal.GetLastWin32Error(); // 先取错误码，DestroyIcon 会覆盖它
                _ = Win32.DestroyIcon(hIcon);
                _ = Win32.DestroyIcon(hIconSm);
                throw new InvalidOperationException($"注册窗口类失败（Win32 错误 {error}）。");
            }

            // 注册成功后才置位：此前是先置位后注册，若注册失败抛异常，标志已是 1
            // → 后续调用直接跳过 → 用未注册的类名去 CreateWindowExW，错误信息误导
            Volatile.Write(ref s_windowClassRegistered, 1);
        }
    }

    /// <summary>提取本进程可执行文件的图标（大/小两个尺寸），供窗口类使用。</summary>
    /// <remarks>
    /// 用 <c>ExtractIconEx</c> 而不是 <c>LoadIcon(module, IDI_APPLICATION)</c>：前者走 shell 自身的
    /// 图标解析、与资源管理器显示的一致，因此不依赖图标资源 ID（.NET SDK 用 32512，原生 .rc 可以是别的）。
    /// 应用未提供图标时返回 (0, 0)，窗口即不带图标。
    /// 取到的句柄归窗口类持有到进程结束——窗口类从不注销，故不再单独释放。
    /// </remarks>
    private static (nint Large, nint Small) LoadExecutableIcons()
    {
        string? path = Environment.ProcessPath;
        if (string.IsNullOrEmpty(path))
        {
            return (0, 0);
        }

        uint extracted = Win32.ExtractIconExW(path, 0, out nint large, out nint small, 1);
        if (extracted == 0)
        {
            // 无图标或提取失败：此时句柄仍可能被回填，必须释放，避免泄漏。
            // （DestroyIcon(0) 是无害的，返回 false 并置一个无关的错误码。）
            _ = Win32.DestroyIcon(large);
            _ = Win32.DestroyIcon(small);
            return (0, 0);
        }

        return (large, small);
    }

    /// <summary>
    /// 记录可见性意图，并在控制器已就绪时同步给 WebView2。
    /// </summary>
    /// <remarks>
    /// 控制器在装配完成前为 null（例如 <c>WithHidden()</c> 起手、页面尚未加载就调 <see cref="Show"/>）：
    /// 这时只更新意图，等 <see cref="InitializeWebView2Async"/> 按意图恢复。
    /// 设置失败只记一笔——可见性是窗口状态的一部分，不该让一次 COM 调用失败掀翻整个窗口操作。
    /// </remarks>
    private void SetControllerVisible(bool visible)
    {
        _isVisible = visible;
        if (_controller is not { } controller)
        {
            return;
        }

        try
        {
            controller.IsVisible = visible;
        }
        catch (Exception ex)
        {
            OrielDiagnostics.Report($"[OrielWeb] 设置 WebView2 可见性失败：{ex.Message}");
        }
    }
}
