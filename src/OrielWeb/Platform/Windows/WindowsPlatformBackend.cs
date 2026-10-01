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
    private int _ran;

    // 托盘与通知**不再**借用调度窗口：那个窗口的消息空间与 WebView2 共享，
    // 自选的 WM_APP + n 会与它的私有消息撞车（见 Win32MessageWindow 的说明）。
    // 两者各自持有 Win32MessageWindow。

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

        Interlocked.Increment(ref _aliveWindows);

        return host;
    }

    // ---- 开机自启（HKCU 的 Run 键）----

    public bool EnableAutoStart(string id, IReadOnlyList<string>? arguments) => Win32AutoStart.Enable(id, arguments);

    public bool DisableAutoStart(string id) => Win32AutoStart.Disable(id);

    public bool IsAutoStartEnabled(string id) => Win32AutoStart.IsEnabled(id);

    public void RunMessageLoop()
    {
        // 与 Linux/macOS 后端一致：重复调用直接返回，不嵌套第二个消息循环
        if (Interlocked.Exchange(ref _ran, 1) == 1)
        {
            return;
        }

        while (Win32.GetMessageW(out var message, 0, 0, 0) > 0)
        {
            Win32.TranslateMessage(ref message);
            Win32.DispatchMessageW(ref message);
        }
    }

    /// <summary>
    /// 创建托盘。这里**不持有**托盘对象：托盘的归属与释放统一由 <see cref="OrielApp"/> 负责
    /// （见 <see cref="OrielApp.Dispose"/>），三个后端在这一点上写法保持一致。
    /// </summary>
    public ITrayBackend CreateTray(OrielTrayOptions options, OrielApp app) => new Win32TrayBackend(app, options);

    // ---- 通知 ----

    /// <summary>
    /// Windows 的通知始终可用：走 WinRT toast（经 PowerShell 调用，见 <see cref="Win32ToastNotification"/>）。
    /// </summary>
    public bool NotificationsSupported => true;

    /// <summary>
    /// 声明但**永不触发**：未打包应用的 toast 激活需要开始菜单快捷方式携带 AUMID 并注册 COM 激活器，
    /// 那是打包器的职责（参考实现 Ryn 也把这一项列为已知缺口）。
    /// </summary>
    /// <remarks>
    /// 显式空实现，与 Linux/macOS 同一理由：让"这里确实没有触发源"在代码里可见，
    /// 而不是看起来像"忘了触发"。三平台一致——都拿不到点击，而不是某个平台看起来支持。
    /// </remarks>
    public event Action<string>? NotificationClicked
    {
        add { }
        remove { }
    }

    public bool ShowNotification(OrielNotificationOptions notification, string appId)
        => Win32ToastNotification.Send(notification, appId);

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
        if (Interlocked.Decrement(ref _aliveWindows) <= 0)
        {
            Win32.PostQuitMessage(0);
        }
    }

    /// <summary>
    /// 后端自身不持有需要显式释放的原生资源：托盘归 <see cref="OrielApp"/> 所有并由它释放
    /// （托盘会连带释放自己的消息窗口），调度窗口随消息循环结束销毁。三平台写法一致。
    /// </summary>
    public void Dispose()
    {
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
        // 托盘的 WM_APP_TRAY 与通知的 WM_APP_NOTIFY 不在这里处理：
        // 那个消息号会与 WebView2 的私有消息撞车，所以它们各自用专属窗口
        //（见 Win32MessageWindow），不再共享本窗口的消息空间。
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
