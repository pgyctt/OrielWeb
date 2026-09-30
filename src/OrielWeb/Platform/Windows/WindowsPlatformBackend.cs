using System.Runtime.InteropServices;
using OrielWeb.Platform.Windows.Interop;

namespace OrielWeb.Platform.Windows;

/// <summary>
/// Windows 平台后端：Win32 消息循环 + 主线程调度（消息专用窗口）+ 窗口工厂。
/// </summary>
internal sealed unsafe class WindowsPlatformBackend : IPlatformBackend
{
    private const string MessageWindowClassName = "OrielWeb_MsgWindow";

    private static int s_messageClassRegistered;
    private static readonly object s_messageClassGate = new();

    private readonly uint _uiThreadId;
    private readonly nint _messageHwnd;
    private int _aliveWindows;

    private Win32TrayBackend? _tray;
    private Win32BalloonIcon? _balloon;

    /// <summary>调度窗口句柄。托盘与通知都用它做回调宿主（见 <see cref="Win32TrayBackend"/> 的说明）。</summary>
    internal nint MessageWindowHandle => _messageHwnd;

    public WindowsPlatformBackend()
    {
        EnsureDpiAwareness();
        EnsureMessageWindowClass();

        _uiThreadId = Win32.GetCurrentThreadId();
        // lpParam 传 0：MessageWindowProc 只处理 WM_APP_DISPATCH（要被执行的 Action 随消息的
        // lParam 传递），从不读取创建参数或 GWLP_USERDATA，因此无需分配 GCHandle
        _messageHwnd = Win32.CreateWindowExW(
            0, RegisterClassNamePtr(MessageWindowClassName), 0, 0,
            0, 0, 0, 0,
            Win32Constants.HWND_MESSAGE, 0, Win32.GetModuleHandleW(null),
            null);
        if (_messageHwnd == 0)
        {
            throw new InvalidOperationException($"创建 OrielWeb 调度窗口失败（Win32 错误 {Marshal.GetLastWin32Error()}）。");
        }

        // Win32 消息循环本身没有 SynchronizationContext：安装后，await 的续体会被 Post 回
        // UI 线程队列，这是 WebView2 生成绑定异步装配（Task 化）能正确工作的前提。
        Win32SynchronizationContext.Install(PostToMainThread);

        // 主题：记下初值（否则"启动时已是深色"会漏报一次），并让静态 WndProc 能回调到本实例
        // （消息窗口的 WndProc 必须是静态的，见 MessageWindowProc）。
        _lastTheme = CurrentTheme;
        s_current = this;
    }

    // ---- 系统主题 ----
    // 应用级（而非窗口级）：主题是系统状态，与具体窗口无关。

    private static WindowsPlatformBackend? s_current;
    private OrielTheme _lastTheme = OrielTheme.Light;

    public event Action<OrielTheme>? ThemeChanged;

    public OrielTheme CurrentTheme => ReadAppsUseLightTheme() == 0 ? OrielTheme.Dark : OrielTheme.Light;

    /// <summary>
    /// 读 <c>HKCU\…\Themes\Personalize\AppsUseLightTheme</c>：1 = 浅色、0 = 深色。
    /// 键不存在（旧系统或未设置）时返回 null，调用方按浅色处理。
    /// </summary>
    private static unsafe int? ReadAppsUseLightTheme()
    {
        uint size = sizeof(int);
        int value = 0;
        int status = Win32.RegGetValueW(
            Win32Constants.HKEY_CURRENT_USER,
            @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            "AppsUseLightTheme",
            Win32Constants.RRF_RT_REG_DWORD,
            0,
            (nint)(&value),
            ref size);
        return status == 0 ? value : null;
    }

    /// <summary>重新读主题，变化了才上报（去重：WM_SETTINGCHANGE 会因很多原因发来）。</summary>
    private void RaiseThemeIfChanged()
    {
        OrielTheme theme = CurrentTheme;
        if (theme == _lastTheme)
        {
            return;
        }

        _lastTheme = theme;
        ThemeChanged?.Invoke(theme);
    }

    // ---- IPlatformBackend ----

    public IWindowBackend CreateWindow(WebviewWindow window, OrielWindowOptions options, OrielApp app, string? assetDirectory)
    {
        var host = Win32WindowHost.Create(window, options, app, assetDirectory, this);

        _aliveWindows++;

        // 应用菜单若已设置，后创建的窗口要补上菜单栏（Windows 的菜单挂在窗口上，不是进程级）
        if (_appMenuItems.Count > 0)
        {
            host.ApplyAppMenu(_appMenuItems);
        }

        return host;
    }

    // ---- 应用菜单（Windows 上就是每个窗口的菜单栏）----

    private IReadOnlyList<OrielMenuItem> _appMenuItems = [];

    public event Action<string>? AppMenuItemClicked;

    internal void RaiseAppMenuItemClicked(string id) => AppMenuItemClicked?.Invoke(id);

    /// <summary>
    /// Windows 没有"应用级菜单栏"这种东西：菜单属于窗口（<c>SetMenu</c>），所以给当前所有窗口各设一份，
    /// 并记住这份定义，供之后创建的窗口补上。
    /// </summary>
    public void SetAppMenu(IReadOnlyList<OrielMenuItem> items, OrielApp app)
    {
        _appMenuItems = items;
        foreach (WebviewWindow window in app.Windows)
        {
            if (window.Backend is Win32WindowHost host)
            {
                host.ApplyAppMenu(items);
            }
        }
    }

    public void ResetAppMenu(OrielApp app)
    {
        _appMenuItems = [];
        foreach (WebviewWindow window in app.Windows)
        {
            if (window.Backend is Win32WindowHost host)
            {
                host.ApplyAppMenu([]);
            }
        }
    }

    // ---- 全局快捷键 / 徽章 ----

    private Win32GlobalShortcuts? _shortcuts;

    public event Action<string>? GlobalShortcutActivated;

    private Win32GlobalShortcuts Shortcuts
    {
        get
        {
            if (_shortcuts is null)
            {
                _shortcuts = new Win32GlobalShortcuts(_messageHwnd);
                _shortcuts.Activated += id => GlobalShortcutActivated?.Invoke(id);
            }

            return _shortcuts;
        }
    }

    public bool RegisterGlobalShortcut(OrielAccelerator accelerator, string id)
        => Shortcuts.Register(accelerator, id);

    public bool UnregisterGlobalShortcut(string id) => _shortcuts?.Unregister(id) ?? false;

    public void UnregisterAllGlobalShortcuts() => _shortcuts?.UnregisterAll();

    /// <summary>
    /// 徽章在 Windows 上需要自绘 overlay 图标（<c>ITaskbarList3.SetOverlayIcon</c> + GDI 画位图），
    /// 当前是 **no-op**（已记入 ROADMAP）。不做半吊子实现的理由：调用方会以为设置成功了，
    /// 而"设了但看不见"比"明确不支持"更难查。
    /// </summary>
    public void SetBadge(string? label)
    {
    }

    // ---- 开机自启（HKCU 的 Run 键）----

    public bool EnableAutoStart(string id, IReadOnlyList<string>? arguments) => Win32AutoStart.Enable(id, arguments);

    public bool DisableAutoStart(string id) => Win32AutoStart.Disable(id);

    public bool IsAutoStartEnabled(string id) => Win32AutoStart.IsEnabled(id);

    public void RunMessageLoop()
    {
        while (Win32.GetMessageW(out var message, 0, 0, 0) > 0)
        {
            Win32.TranslateMessage(ref message);
            Win32.DispatchMessageW(ref message);
        }
    }

    public ITrayBackend CreateTray(OrielTrayOptions options, OrielApp app)
    {
        _tray = new Win32TrayBackend(this, app, options);
        return _tray;
    }

    // ---- 通知 ----

    /// <summary>Windows 的通知始终可用：载体是一个独立的隐藏托盘项（见 <see cref="Win32BalloonIcon"/>）。</summary>
    public bool NotificationsSupported => true;

    public event Action<string>? NotificationClicked;

    public void ShowNotification(OrielNotificationOptions notification) => Balloon.Show(notification);

    private Win32BalloonIcon Balloon
    {
        get
        {
            if (_balloon is null)
            {
                _balloon = new Win32BalloonIcon(_messageHwnd);
                _balloon.Clicked += id => NotificationClicked?.Invoke(id);
            }
            return _balloon;
        }
    }

    public void Quit() => Win32.PostQuitMessage(0);

    public bool IsOnUiThread() => Win32.GetCurrentThreadId() == _uiThreadId;

    public void PostToMainThread(Action action)
    {
        var handle = GCHandle.Alloc(action);
        if (!Win32.PostMessageW(_messageHwnd, Win32Constants.WM_APP_DISPATCH, 0, GCHandle.ToIntPtr(handle)))
        {
            handle.Free();
            throw new InvalidOperationException("PostToMainThread 失败：调度窗口已失效。");
        }
    }

    internal void OnWindowDestroyed()
    {
        if (--_aliveWindows <= 0)
        {
            Win32.PostQuitMessage(0);
        }
    }

    /// <summary>释放托盘、通知载体与已注册的全局快捷键；调度窗口随消息循环结束销毁。</summary>
    public void Dispose()
    {
        _tray?.Dispose();
        _balloon?.Dispose();
        // 热键注册属于进程级资源：不显式注销，系统会一直占着这个组合
        _shortcuts?.UnregisterAll();
    }

    // ---- 消息窗口 ----

    [UnmanagedCallersOnly]
    private static nint MessageWindowProc(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        if (message == Win32Constants.WM_APP_DISPATCH)
        {
            // 先取回并释放 GCHandle，避免 Action 抛异常时句柄泄漏
            var handle = GCHandle.FromIntPtr(lParam);
            Action? action;
            try
            {
                action = (Action)handle.Target!;
            }
            finally
            {
                handle.Free();
            }

            try
            {
                action?.Invoke();
            }
            catch (Exception ex)
            {
                // PostToMainThread 是 public API，用户 Action 的异常绝不能穿越原生边界
                // （外泄 = 进程 fail-fast，不可捕获）
                System.Diagnostics.Debug.WriteLine($"[OrielWeb] PostToMainThread 回调抛出异常：{ex}");
            }
            return 0;
        }
        if (message == Win32Constants.WM_APP_TRAY)
        {
            // V4 起回调的 wParam 才是事件类型（NIN_SELECT 等），坐标在 lParam
            s_current?._tray?.HandleCallback(wParam);
            return 0;
        }
        if (message == Win32Constants.WM_APP_NOTIFY)
        {
            s_current?._balloon?.HandleCallback(wParam);
            return 0;
        }
        if (message == Win32Constants.WM_HOTKEY)
        {
            // wParam 是注册时给的热键 id（见 Win32GlobalShortcuts.Register）
            s_current?._shortcuts?.HandleHotKey(wParam);
            return 0;
        }
        if (message == Win32Constants.WM_CONTEXTMENU)
        {
            // V4 起托盘的右键不再是回调消息，而是宿主窗口收到 WM_CONTEXTMENU
            s_current?._tray?.ShowMenu();
            return 0;
        }
        if (message == Win32Constants.WM_SETTINGCHANGE)
        {
            // 主题切换会带 "ImmersiveColorSet" 参数；其它设置变化（环境变量、区域等）不理会
            if (lParam != 0
                && Marshal.PtrToStringUni(lParam) is { } changed
                && changed.Contains("ImmersiveColorSet", StringComparison.OrdinalIgnoreCase))
            {
                s_current?.RaiseThemeIfChanged();
            }
            return Win32.DefWindowProcW(hwnd, message, wParam, lParam);
        }

        return Win32.DefWindowProcW(hwnd, message, wParam, lParam);
    }

    private static void EnsureMessageWindowClass()
    {
        if (Volatile.Read(ref s_messageClassRegistered) == 1)
        {
            return;
        }

        lock (s_messageClassGate)
        {
            if (Volatile.Read(ref s_messageClassRegistered) == 1)
            {
                return;
            }

            var windowClass = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = (nint)(delegate* unmanaged<nint, uint, nuint, nint, nint>)&MessageWindowProc,
                hInstance = Win32.GetModuleHandleW(null),
                lpszClassName = RegisterClassNamePtr(MessageWindowClassName),
            };
            if (Win32.RegisterClassExW(ref windowClass) == 0)
            {
                throw new InvalidOperationException($"注册 OrielWeb 调度窗口类失败（Win32 错误 {Marshal.GetLastWin32Error()}）。");
            }

            // 注册成功后才置位（理由同 Win32WindowHost.EnsureWindowClass）
            Volatile.Write(ref s_messageClassRegistered, 1);
        }
    }

    private static void EnsureDpiAwareness()
    {
        try
        {
            _ = Win32.SetProcessDpiAwarenessContext(Win32Constants.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        }
        catch (EntryPointNotFoundException)
        {
            // 旧系统无此 API，忽略
        }
    }

    private static readonly Dictionary<string, nint> s_classNamePtrs = new(StringComparer.Ordinal);

    private static nint RegisterClassNamePtr(string className)
    {
        lock (s_classNamePtrs)
        {
            if (!s_classNamePtrs.TryGetValue(className, out var pointer))
            {
                pointer = Marshal.StringToHGlobalUni(className);
                s_classNamePtrs[className] = pointer;
            }
            return pointer;
        }
    }
}
