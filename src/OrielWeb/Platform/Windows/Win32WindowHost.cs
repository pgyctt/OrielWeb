using System.Buffers;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Text;
using System.Text.Json;
using OrielWeb.Ipc;
using OrielWeb.Platform.Windows.Interop;

namespace OrielWeb.Platform.Windows;

/// <summary>
/// 单个窗口的 Win32 + WebView2 宿主：窗口创建、WndProc、WebView2 装配、
/// 事件回抛、IPC 回执线程切换与对话框。
/// </summary>
internal sealed partial class Win32WindowHost : IWindowBackend
{
    private const string WindowClassName = "OrielWeb_Window";
    private const string WebView2UserDataFolderName = "WebView2";

    private static int s_windowClassRegistered;
    private static readonly object s_windowClassGate = new();
    private static readonly nint s_windowClassNamePtr = Marshal.StringToHGlobalUni(WindowClassName);

    private readonly WebviewWindow _window;
    private readonly OrielWindowOptions _options;
    private readonly OrielApp _app;
    private readonly WindowsPlatformBackend _backend;
    private readonly string? _assetDirectory;
    private readonly string _assetHost;
    private readonly string _userDataFolder;

    private GCHandle _selfHandle;
    private nint _hwnd;

    private WebView2Ptr? _webview;
    private WebView2ControllerPtr? _controller;
    private volatile bool _loadedRaised;

    private int _minWidth;
    private int _minHeight;
    private bool _isFullscreen;
    private bool _isOnTop;
    private WINDOWPLACEMENT _savedPlacement;
    private nint _savedStyle;
    private string _title;

    private event Action? Loaded;
    private event Action<OrielCloseRequestEventArgs>? Closing;
    private event Action? Closed;
    private event Action<string>? TitleChanged;

    private Win32WindowHost(WebviewWindow window, OrielWindowOptions options, OrielApp app, string? assetDirectory, WindowsPlatformBackend backend)
    {
        _window = window;
        _options = options;
        _app = app;
        _backend = backend;
        _assetDirectory = assetDirectory;
        // 取自构建器 UseEmbeddedAssets(host)：此前硬编码 "app.oriel" 会使用户自定义 host 失效
        // （虚拟主机映射到自定义 host，导航却指向 app.oriel → 白屏/404）
        _assetHost = app.AssetHost;
        _userDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OrielWeb", WebView2UserDataFolderName);
        _title = options.Title;
        if (options.MinWidth is int minWidth) _minWidth = minWidth;
        if (options.MinHeight is int minHeight) _minHeight = minHeight;
    }

    public nint NativeWindowHandle => _hwnd;

    event Action? IWindowBackend.Loaded { add => Loaded += value; remove => Loaded -= value; }
    event Action<OrielCloseRequestEventArgs>? IWindowBackend.Closing { add => Closing += value; remove => Closing -= value; }
    event Action? IWindowBackend.Closed { add => Closed += value; remove => Closed -= value; }
    event Action<string>? IWindowBackend.TitleChanged { add => TitleChanged += value; remove => TitleChanged -= value; }

    // ------------------------------------------------------------------
    // 创建
    // ------------------------------------------------------------------

    public static unsafe Win32WindowHost Create(WebviewWindow window, OrielWindowOptions options, OrielApp app, string? assetDirectory, WindowsPlatformBackend backend)
    {
        EnsureWindowClass();
        var host = new Win32WindowHost(window, options, app, assetDirectory, backend);
        host._selfHandle = GCHandle.Alloc(host);

        uint style = ComputeStyle(options);
        int x = options.X ?? Win32Constants.CW_USEDEFAULT;
        int y = options.Y ?? Win32Constants.CW_USEDEFAULT;

        nint windowNamePtr = Marshal.StringToHGlobalUni(options.Title);
        var hwnd = Win32.CreateWindowExW(
            0,
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

        host.PostCreate();
        return host;
    }

    private static uint ComputeStyle(OrielWindowOptions options)
    {
        uint style;
        if (options.Frameless)
        {
            style = Win32Constants.WS_POPUP | Win32Constants.WS_SYSMENU;
            if (options.Resizable)
            {
                style |= Win32Constants.WS_THICKFRAME | Win32Constants.WS_MAXIMIZEBOX | Win32Constants.WS_MINIMIZEBOX;
            }
        }
        else
        {
            style = Win32Constants.WS_OVERLAPPEDWINDOW;
            if (!options.Resizable)
            {
                style &= ~(Win32Constants.WS_THICKFRAME | Win32Constants.WS_MAXIMIZEBOX);
            }
        }
        return style;
    }

    private void PostCreate()
    {
        if (_options.Center && _options.X is null && _options.Y is null)
        {
            Center();
        }

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

            var windowClass = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                style = Win32Constants.CS_HREDRAW | Win32Constants.CS_VREDRAW | Win32Constants.CS_DBLCLKS,
                lpfnWndProc = (nint)(delegate* unmanaged<nint, uint, nuint, nint, nint>)&WindowProc,
                hInstance = Win32.GetModuleHandleW(null),
                hCursor = Win32.LoadCursorW(0, Win32Constants.IDC_ARROW),
                hbrBackground = 0, // WebView2 自绘客户端区，置空避免闪烁
                lpszClassName = s_windowClassNamePtr,
            };
            if (Win32.RegisterClassExW(ref windowClass) == 0)
            {
                throw new InvalidOperationException($"注册窗口类失败（Win32 错误 {Marshal.GetLastWin32Error()}）。");
            }

            // 注册成功后才置位：此前是先置位后注册，若注册失败抛异常，标志已是 1
            // → 后续调用直接跳过 → 用未注册的类名去 CreateWindowExW，错误信息误导
            Volatile.Write(ref s_windowClassRegistered, 1);
        }
    }

    // ------------------------------------------------------------------
    // WndProc
    // ------------------------------------------------------------------

    [UnmanagedCallersOnly]
    private static unsafe nint WindowProc(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        try
        {
            nint userData = Win32.GetWindowLongPtrW(hwnd, Win32Constants.GWLP_USERDATA);

            if (message == Win32Constants.WM_NCCREATE && userData == 0)
            {
                var createStruct = (CREATESTRUCTW*)lParam;
                if (createStruct->lpCreateParams != 0)
                {
                    Win32.SetWindowLongPtrW(hwnd, Win32Constants.GWLP_USERDATA, createStruct->lpCreateParams);
                    userData = createStruct->lpCreateParams;
                }
            }

            if (userData == 0)
            {
                return Win32.DefWindowProcW(hwnd, message, wParam, lParam);
            }

            var host = (Win32WindowHost)GCHandle.FromIntPtr(userData).Target!;
            return host.HandleMessage(hwnd, message, wParam, lParam);
        }
        catch (Exception ex)
        {
            // 用户事件处理器（Closing/Loaded/Closed 等）的异常绝不能穿越原生边界
            // （外泄 = 进程 fail-fast，不可捕获）。
            // 降级为默认处理：若异常来自 WM_CLOSE 的 Closing 回调，窗口会按默认语义销毁
            // ——即"取消关闭"的意图失效，这是"异常时无法确定用户意图"的合理降级。
            System.Diagnostics.Debug.WriteLine($"[OrielWeb] WindowProc 处理消息 0x{message:X} 时抛出异常：{ex}");
            return Win32.DefWindowProcW(hwnd, message, wParam, lParam);
        }
    }

    private unsafe nint HandleMessage(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        switch (message)
        {
            case Win32Constants.WM_SIZE:
                UpdateBounds();
                return 0;

            case Win32Constants.WM_MOVE:
                // 注意：此处理论上应调用 controller.NotifyParentWindowPositionChanged()（槽 23），
                // 但实测在窗口最大化等场景该调用会触发 AV（疑似本机运行时 ComWrappers/互操作问题），
                // 且窗口位置变化由 WM_SIZE 的 put_Bounds 兜底，MVP 先跳过（见 docs/DECISIONS.md）。
                return 0;

            case Win32Constants.WM_GETMINMAXINFO:
            {
                var info = (MINMAXINFO*)lParam;

                // 最大化限制到显示器工作区（无边框 WS_POPUP 不会自动避开任务栏）
                var monitor = Win32.MonitorFromWindow(hwnd, Win32Constants.MONITOR_DEFAULTTONEAREST);
                var monitorInfo = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
                if (Win32.GetMonitorInfo(monitor, ref monitorInfo))
                {
                    info->ptMaxSize.X = monitorInfo.rcWork.Right - monitorInfo.rcWork.Left;
                    info->ptMaxSize.Y = monitorInfo.rcWork.Bottom - monitorInfo.rcWork.Top;
                    info->ptMaxPosition.X = monitorInfo.rcWork.Left;
                    info->ptMaxPosition.Y = monitorInfo.rcWork.Top;
                }

                if (_minWidth > 0) info->ptMinTrackSize.X = _minWidth;
                if (_minHeight > 0) info->ptMinTrackSize.Y = _minHeight;
                return 0;
            }

            case Win32Constants.WM_CLOSE:
                var closeArgs = new OrielCloseRequestEventArgs();
                Closing?.Invoke(closeArgs);
                if (closeArgs.Cancel)
                {
                    return 0;
                }
                Win32.DestroyWindow(hwnd);
                return 0;

            case Win32Constants.WM_DESTROY:
                Closed?.Invoke();
                _selfHandle.Free();
                _backend.OnWindowDestroyed();
                return 0;
        }

        return Win32.DefWindowProcW(hwnd, message, wParam, lParam);
    }

    // ------------------------------------------------------------------
    // WebView2 装配（异步回调均在 UI 线程）
    // ------------------------------------------------------------------

    private void InitializeWebView2()
    {
        Directory.CreateDirectory(_userDataFolder);

        nint handlerPointer = WebView2NativeCallbacks.CreateEnvironmentHandler(this);
        // 测试开关：指定固定版本运行时（如 153.0.4234.46）
        var browserFolder = Environment.GetEnvironmentVariable("ORIEL_WEBVIEW2_FOLDER");
        int hr = WebView2LoaderNative.CreateCoreWebView2EnvironmentWithOptions(browserFolder, _userDataFolder, 0, handlerPointer);
        WebView2ComHelper.ThrowIfFailed(hr, "创建 WebView2 环境（加载 WebView2Loader.dll）");
    }

    internal void OnWebViewFailed(string message)
    {
        MessageBoxResult(message, "OrielWeb", OrielMessageBoxIcon.Error);
        Win32.DestroyWindow(_hwnd);
    }

    internal void RaiseLoadedIfFirst()
    {
        if (!_loadedRaised)
        {
            _loadedRaised = true;
            Loaded?.Invoke();
        }
    }

    internal void RaiseTitleChanged(string title)
    {
        _title = title;
        Win32.SetWindowTextW(_hwnd, title); // 文档标题 → 窗口标题同步
        TitleChanged?.Invoke(title);
    }

    internal unsafe int OnEnvironmentCreated(int errorCode, void* environment)
    {
        if (errorCode < 0 || environment is null)
        {
            OnWebViewFailed($"创建 WebView2 环境失败（HRESULT 0x{errorCode:X8}）。");
            return 0;
        }
        // 回调参数只在调用期间有效：AddRef 后长期持有（不再 Release，随进程生命周期存活），
        // 否则 WebView2 在回调返回后 Release，环境析构、浏览器进程树随之关闭
        WebView2Native.AddRefComObject(environment);
        var env = new WebView2EnvironmentPtr(environment);
        nint controllerHandler = WebView2NativeCallbacks.CreateControllerHandler(this);
        return env.CreateCoreWebView2Controller(_hwnd, controllerHandler);
    }

    /// <summary>控制器创建完成回调（由手工 CCW thunk 调用，UI 线程）。</summary>
    internal unsafe int OnControllerCreated(int errorCode, void* controller)
    {
        if (errorCode < 0 || controller is null)
        {
            OnWebViewFailed($"创建 WebView2 控制器失败（HRESULT 0x{errorCode:X8}）。");
            return 0;
        }
        // 同上：回调参数需要 AddRef 才能存过回调生命周期
        WebView2Native.AddRefComObject(controller);
        OnControllerCreatedCore(new WebView2ControllerPtr(controller));
        return 0;
    }

    private void OnControllerCreatedCore(WebView2ControllerPtr controller)
    {
        _controller = controller;
        controller.get_CoreWebView2(out var webview);
        _webview = webview;

        // 设置：DevTools 按调试开关
        var settings = webview.GetSettings();
        settings.put_AreDevToolsEnabled(_options.Debug ? 1 : 0);

        // 内嵌资产 → 虚拟主机（同源 https，免 CORS）
        if (_assetDirectory is not null)
        {
            unsafe
            {
                if (WebView2Native.TryQueryInterface(webview.Self, WebView2Iids.ICoreWebView2_3, out var webview3Ptr))
                {
                    var webview3 = new WebView2_3Ptr(webview3Ptr);
                    const int HOST_RESOURCE_ACCESS_KIND_DENY_CORS = 2;
                    WebView2ComHelper.ThrowIfFailed(
                        webview3.SetVirtualHostNameToFolderMapping(_assetHost, _assetDirectory, HOST_RESOURCE_ACCESS_KIND_DENY_CORS),
                        "映射虚拟主机");
                }
                else
                {
                    // ICoreWebView2_3 需要 WebView2 Runtime ≥ 1.0.864.35。企业固定版本、Windows Server、
                    // 离线镜像可能更旧。此前此处静默跳过 → 虚拟主机不映射 → Navigate 失败 → 白屏且零提示。
                    // 该路径绕过 ThrowIfFailed（QI 返回 bool，没有 HRESULT 可检查），必须显式报错。
                    string version;
                    try
                    {
                        nint versionPtr = 0;
                        var browserFolder = Environment.GetEnvironmentVariable("ORIEL_WEBVIEW2_FOLDER");
                        if (WebView2LoaderNative.GetAvailableCoreWebView2BrowserVersionString(browserFolder, out versionPtr) >= 0
                            && versionPtr != 0
                            && Marshal.PtrToStringUni(versionPtr) is { Length: > 0 } installed)
                        {
                            version = installed;
                        }
                        else
                        {
                            version = "(未知)";
                        }
                        if (versionPtr != 0)
                        {
                            Marshal.FreeCoTaskMem(versionPtr);
                        }
                    }
                    catch
                    {
                        version = "(未知)"; // 诊断信息获取失败不影响报错本身
                    }

                    throw new InvalidOperationException(
                        "当前 WebView2 运行时过旧，不支持 ICoreWebView2_3（虚拟主机映射），内嵌资源无法加载。" +
                        $"已安装运行时版本：{version}。请将 WebView2 Runtime 升级至 1.0.864.35 或更高版本。");
                }
            }
        }

        // JS 桥 + 事件（回调对象为手工 CCW）
        WebView2ComHelper.ThrowIfFailed(
            webview.AddScriptToExecuteOnDocumentCreated(OrielBridgeJs.Script, WebView2NativeCallbacks.CreateAddScriptHandler()),
            "注入 JS 桥");
        WebView2ComHelper.ThrowIfFailed(
            webview.add_WebMessageReceived(WebView2NativeCallbacks.CreateWebMessageHandler(new WebMessageReceivedHandler(this)), out _),
            "注册 WebMessage 事件");
        WebView2ComHelper.ThrowIfFailed(
            webview.add_NavigationCompleted(WebView2NativeCallbacks.CreateNavigationCompletedHandler(this), out _),
            "注册 NavigationCompleted 事件");
        WebView2ComHelper.ThrowIfFailed(
            webview.add_DocumentTitleChanged(WebView2NativeCallbacks.CreateDocumentTitleHandler(this), out _),
            "注册 DocumentTitleChanged 事件");

        // 导航
        string url = _options.Url ?? $"https://{_assetHost}/index.html";
        WebView2ComHelper.ThrowIfFailed(webview.Navigate(url), "加载首页");

        WebView2ComHelper.ThrowIfFailed(controller.put_IsVisible(1), "显示 WebView2");
        UpdateBounds();
    }

    private unsafe void UpdateBounds()
    {
        var controller = _controller;
        if (controller is null)
        {
            return;
        }
        if (!Win32.GetClientRect(_hwnd, out var client))
        {
            return;
        }
        var bounds = new WebView2Rect { Left = 0, Top = 0, Right = client.Right, Bottom = client.Bottom };
        controller.Value.put_Bounds(bounds);
    }

    // ------------------------------------------------------------------
    // IPC（WebMessage 接收与回执见 Win32WebView2Ipc.cs，避免 async 与 unsafe 混用）
    // ------------------------------------------------------------------

    internal OrielApp App => _app;

    internal WindowsPlatformBackend Backend => _backend;

    internal bool IsOnUiThread() => _backend.IsOnUiThread();

    internal void PostWebMessageOnUi(string json)
    {
        try
        {
            if (_webview is { } webview)
            {
                webview.PostWebMessageAsJson(json);
            }
        }
        catch
        {
            // 窗口销毁后的迟到回执，忽略
        }
    }

    private void MessageBoxResult(string text, string? title, OrielMessageBoxIcon icon)
    {
        uint flags = Win32Constants.MB_OK | icon switch
        {
            OrielMessageBoxIcon.Warning => Win32Constants.MB_ICONWARNING,
            OrielMessageBoxIcon.Error => Win32Constants.MB_ICONERROR,
            OrielMessageBoxIcon.Question => Win32Constants.MB_ICONQUESTION,
            _ => Win32Constants.MB_ICONINFORMATION,
        };
        Win32.MessageBoxW(_hwnd, text, title ?? _title, flags);
    }

    // ------------------------------------------------------------------
    // IWindowBackend
    // ------------------------------------------------------------------

    public void Show() => Win32.ShowWindow(_hwnd, Win32Constants.SW_SHOW);
    public void Hide() => Win32.ShowWindow(_hwnd, Win32Constants.SW_HIDE);

    public void Close()
    {
        Win32.PostMessageW(_hwnd, Win32Constants.WM_CLOSE, 0, 0);
    }

    public void Focus()
    {
        Win32.SetForegroundWindow(_hwnd);
        Win32.SetFocus(_hwnd);
    }

    public void Maximize() => Win32.ShowWindow(_hwnd, Win32Constants.SW_SHOWMAXIMIZED);
    public void Minimize() => Win32.ShowWindow(_hwnd, Win32Constants.SW_SHOWMINIMIZED);
    public void Restore() => Win32.ShowWindow(_hwnd, Win32Constants.SW_RESTORE);

    public void BeginDrag()
    {
        // 经典技巧：释放鼠标捕获后发送 NC 按钮消息，让系统进入标题栏拖动模态循环
        if (Win32.ReleaseCapture())
        {
            _ = Win32.SendMessageW(_hwnd, Win32Constants.WM_NCLBUTTONDOWN, (nuint)Win32Constants.HTCAPTION, 0);
        }
    }

    // 流式拖动（macOS 用）；Windows 原生模态拖动已覆盖，此处 no-op
    public void BeginDragStreaming(double px, double py, double wx, double wy, double ww, double wh, double sh)
    {
    }

    public void DragTo(double dx, double dy)
    {
    }

    public void EndDrag()
    {
    }

    public void ToggleMaximize()
    {
        if (Win32.IsZoomed(_hwnd))
        {
            Restore();
        }
        else
        {
            Maximize();
        }
    }

    public bool ToggleFullscreen()
    {
        SetFullscreen(!_isFullscreen);
        return _isFullscreen;
    }

    public bool ToggleOnTop()
    {
        SetOnTop(!_isOnTop);
        return _isOnTop;
    }

    public void SetOnTop(bool enabled)
    {
        _isOnTop = enabled;
        Win32.SetWindowPos(
            _hwnd,
            enabled ? Win32Constants.HWND_TOPMOST : Win32Constants.HWND_NOTOPMOST,
            0, 0, 0, 0,
            Win32Constants.SWP_NOMOVE | Win32Constants.SWP_NOSIZE | Win32Constants.SWP_NOACTIVATE);
    }

    public void SetFullscreen(bool enabled)
    {
        if (enabled == _isFullscreen)
        {
            return;
        }

        if (enabled)
        {
            var placement = default(WINDOWPLACEMENT);
            placement.length = (uint)Marshal.SizeOf<WINDOWPLACEMENT>();
            Win32.GetWindowPlacement(_hwnd, ref placement);
            _savedPlacement = placement;

            // 保存原始样式：退出全屏时必须原样还原。此前退出时无条件
            // style |= WS_OVERLAPPEDWINDOW，会把无边框（WS_POPUP）窗口全屏后变成带边框
            nint style = Win32.GetWindowLongPtrW(_hwnd, Win32Constants.GWL_STYLE);
            _savedStyle = style;
            style &= ~(nint)(Win32Constants.WS_CAPTION | Win32Constants.WS_THICKFRAME);
            style |= (nint)Win32Constants.WS_POPUP;
            Win32.SetWindowLongPtrW(_hwnd, Win32Constants.GWL_STYLE, style);

            var monitor = Win32.MonitorFromWindow(_hwnd, Win32Constants.MONITOR_DEFAULTTONEAREST);
            var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
            Win32.GetMonitorInfo(monitor, ref info);
            Win32.SetWindowPos(_hwnd, 0,
                info.rcMonitor.Left, info.rcMonitor.Top,
                info.rcMonitor.Right - info.rcMonitor.Left,
                info.rcMonitor.Bottom - info.rcMonitor.Top,
                Win32Constants.SWP_NOOWNERZORDER | Win32Constants.SWP_FRAMECHANGED | Win32Constants.SWP_SHOWWINDOW);
            _isFullscreen = true;
        }
        else
        {
            Win32.SetWindowLongPtrW(_hwnd, Win32Constants.GWL_STYLE, _savedStyle);

            var placement = _savedPlacement;
            Win32.SetWindowPlacement(_hwnd, ref placement);
            Win32.SetWindowPos(_hwnd, 0, 0, 0, 0, 0,
                Win32Constants.SWP_NOMOVE | Win32Constants.SWP_NOSIZE | Win32Constants.SWP_NOZORDER |
                Win32Constants.SWP_NOOWNERZORDER | Win32Constants.SWP_FRAMECHANGED);
            _isFullscreen = false;
        }
    }

    public void SetTitle(string title)
    {
        _title = title;
        Win32.SetWindowTextW(_hwnd, title);
    }

    public void SetResizable(bool enabled)
    {
        nint style = Win32.GetWindowLongPtrW(_hwnd, Win32Constants.GWL_STYLE);
        const uint resizableMask = Win32Constants.WS_THICKFRAME | Win32Constants.WS_MAXIMIZEBOX;
        style = enabled
            ? style | (nint)resizableMask
            : style & ~(nint)resizableMask;
        Win32.SetWindowLongPtrW(_hwnd, Win32Constants.GWL_STYLE, style);
        Win32.SetWindowPos(_hwnd, 0, 0, 0, 0, 0,
            Win32Constants.SWP_NOMOVE | Win32Constants.SWP_NOSIZE | Win32Constants.SWP_NOZORDER |
            Win32Constants.SWP_NOOWNERZORDER | Win32Constants.SWP_FRAMECHANGED);
    }

    public void SetMinSize(int width, int height)
    {
        _minWidth = width;
        _minHeight = height;
    }

    public void MoveTo(int x, int y)
    {
        Win32.SetWindowPos(_hwnd, 0, x, y, 0, 0, Win32Constants.SWP_NOSIZE | Win32Constants.SWP_NOZORDER | Win32Constants.SWP_NOACTIVATE);
    }

    public void Resize(int width, int height)
    {
        Win32.SetWindowPos(_hwnd, 0, 0, 0, width, height, Win32Constants.SWP_NOMOVE | Win32Constants.SWP_NOZORDER | Win32Constants.SWP_NOACTIVATE);
    }

    public void Center()
    {
        if (!Win32.GetWindowRect(_hwnd, out var windowRect))
        {
            return;
        }
        int width = windowRect.Right - windowRect.Left;
        int height = windowRect.Bottom - windowRect.Top;

        var monitor = Win32.MonitorFromWindow(_hwnd, Win32Constants.MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        if (!Win32.GetMonitorInfo(monitor, ref info))
        {
            return;
        }

        int x = info.rcWork.Left + ((info.rcWork.Right - info.rcWork.Left) - width) / 2;
        int y = info.rcWork.Top + ((info.rcWork.Bottom - info.rcWork.Top) - height) / 2;
        Win32.SetWindowPos(_hwnd, 0, x, y, 0, 0, Win32Constants.SWP_NOSIZE | Win32Constants.SWP_NOZORDER | Win32Constants.SWP_NOACTIVATE);
    }

    public Task<string> ExecuteScriptAsync(string script)
    {
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _backend.PostToMainThread(() =>
        {
            if (_webview is not { } webview)
            {
                completion.TrySetException(new InvalidOperationException("WebView2 尚未就绪。"));
                return;
            }
            try
            {
                nint handler = WebView2NativeCallbacks.CreateExecuteScriptHandler(completion);
                int hr = webview.ExecuteScript(script, handler);
                if (hr < 0)
                {
                    completion.TrySetException(new InvalidOperationException($"ExecuteScript 失败（HRESULT 0x{hr:X8}）。"));
                }
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });
        return completion.Task;
    }

    public void PostMessageAsJson(string json)
    {
        _backend.PostToMainThread(() =>
        {
            try
            {
                if (_webview is { } webview)
                {
                    webview.PostWebMessageAsJson(json);
                }
            }
            catch
            {
                // 窗口销毁后忽略
            }
        });
    }

    // ------------------------------------------------------------------
    // 对话框
    // ------------------------------------------------------------------

    public string? ShowOpenFileDialog(string? title, string? filter, string? initialDirectory)
        => ShowFileDialog(isSave: false, title, filter, defaultExtension: null, initialDirectory);

    public string? ShowSaveFileDialog(string? title, string? filter, string? defaultExtension)
        => ShowFileDialog(isSave: true, title, filter, defaultExtension, initialDirectory: null);

    private string? ShowFileDialog(bool isSave, string? title, string? filter, string? defaultExtension, string? initialDirectory)
    {
        string filterString = ConvertFilter(filter);
        nint filterPtr = Marshal.StringToHGlobalUni(filterString);
        nint bufferPtr = Marshal.AllocHGlobal(32768 * sizeof(char));
        nint titlePtr = title is null ? 0 : Marshal.StringToHGlobalUni(title);
        nint initialDirPtr = initialDirectory is null ? 0 : Marshal.StringToHGlobalUni(initialDirectory);
        nint defExtPtr = defaultExtension is null ? 0 : Marshal.StringToHGlobalUni(defaultExtension);

        try
        {
            // 预填当前目录，避免对话框落在系统目录
            Marshal.WriteInt16(bufferPtr, 0);

            var ofn = new OPENFILENAMEW
            {
                lStructSize = (uint)Marshal.SizeOf<OPENFILENAMEW>(),
                hwndOwner = _hwnd,
                lpstrFilter = filterPtr,
                lpstrFile = bufferPtr,
                nMaxFile = 32768,
                lpstrTitle = titlePtr,
                lpstrInitialDir = initialDirPtr,
                lpstrDefExt = defExtPtr,
                Flags = isSave
                    ? Win32Constants.OFN_OVERWRITEPROMPT | Win32Constants.OFN_PATHMUSTEXIST | Win32Constants.OFN_HIDEREADONLY | Win32Constants.OFN_NOCHANGEDIR
                    : Win32Constants.OFN_FILEMUSTEXIST | Win32Constants.OFN_PATHMUSTEXIST | Win32Constants.OFN_HIDEREADONLY | Win32Constants.OFN_NOCHANGEDIR,
            };

            bool ok = isSave ? Win32.GetSaveFileNameW(ref ofn) : Win32.GetOpenFileNameW(ref ofn);
            if (!ok)
            {
                return null; // 取消或错误（CommDlgExtendedError 区分，M1 统一返回 null）
            }

            return Marshal.PtrToStringUni(bufferPtr)?.TrimEnd('\0') is { Length: > 0 } path ? path : null;
        }
        finally
        {
            Marshal.FreeHGlobal(filterPtr);
            Marshal.FreeHGlobal(bufferPtr);
            if (titlePtr != 0) Marshal.FreeHGlobal(titlePtr);
            if (initialDirPtr != 0) Marshal.FreeHGlobal(initialDirPtr);
            if (defExtPtr != 0) Marshal.FreeHGlobal(defExtPtr);
        }
    }

    private static string ConvertFilter(string? filter)
    {
        // "文本|*.txt|全部|*.*" → Win32 要求以双 null 结尾的过滤器格式。
        // 此前只补一个 '\0'，靠 StringToHGlobalUni 自动追加的终止符侥幸成立，
        // 用户自定义 filter 实际依赖该 API 容错；此处显式补足双 null，
        // 并容忍用户 filter 末尾多出的 '|'（否则会产生空条目）。
        if (string.IsNullOrEmpty(filter))
        {
            return "所有文件\0*.*\0\0";
        }
        return filter.TrimEnd('|').Replace('|', '\0') + "\0\0";
    }

    void IWindowBackend.ShowMessageBox(string text, string? title, OrielMessageBoxIcon icon)
        => MessageBoxResult(text, title, icon);
}
