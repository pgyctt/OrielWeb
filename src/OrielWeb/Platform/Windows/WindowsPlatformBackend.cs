using System.Runtime.InteropServices;
using DirectN.Extensions.Com;
using OrielWeb.Platform.Windows.Interop;
using WebView2;

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

    /// <param name="userDataFolder">
    /// 覆盖 WebView2 的 user data folder（<see cref="OrielAppBuilder.UseUserDataFolder"/>）；
    /// 为空则用 <see cref="DefaultUserDataFolder"/>。它必须是**全进程一个**：环境按它创建，见
    /// <see cref="GetEnvironmentAsync"/>。
    /// </param>
    public WindowsPlatformBackend(string? userDataFolder)
    {
        _userDataFolder = string.IsNullOrWhiteSpace(userDataFolder) ? DefaultUserDataFolder : userDataFolder;
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

    // ---- WebView2 环境（全进程共享）----

    /// <summary>
    /// 全进程共享的 WebView2 环境（惰性创建，只建一次）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>一个进程里每个窗口各建一个环境是不行的</b>：环境的 user data folder 是**全进程同一个目录**，
    /// 而 WebView2 不允许同一目录上并存两个环境——第二个会以"资源状态不对"失败，
    /// 于是"运行时再开一个窗口"在 Windows 上直接起不来。正确形态是**一份环境 + 多个控制器**。
    /// </para>
    /// <para>
    /// 缓存的是 <see cref="Task{TResult}"/> 而不是结果：多个窗口同时首次装配时，只会真正创建一个环境，
    /// 后来者 await 同一个任务。创建与读取都只在 UI 线程发生。
    /// </para>
    /// </remarks>
    private Task<IComObject<ICoreWebView2Environment>?>? _environment;

    /// <summary>默认的 user data folder：<c>%LOCALAPPDATA%\OrielWeb\WebView2</c>。</summary>
    internal static string DefaultUserDataFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OrielWeb",
        Win32WindowHost.WebView2UserDataFolderName);

    /// <summary>本次应用的 user data folder（<c>UseUserDataFolder</c> 覆盖过就用它）。</summary>
    private readonly string _userDataFolder;

    /// <summary>取（必要时创建）共享环境。必须在 UI 线程调用。</summary>
    /// <remarks>
    /// 这里刻意不写成 <c>async</c>/<c>await</c>：本类是 <c>unsafe</c> 上下文，在其中 await 会报 CS4004。
    /// 判空与继续 await 由窗口那侧完成（那边不是 unsafe）。
    /// </remarks>
    internal Task<IComObject<ICoreWebView2Environment>?> GetEnvironmentAsync(string? browserFolder)
        => _environment ??= Functions.CreateCoreWebView2EnvironmentWithOptionsAsync(
            browserFolder, _userDataFolder, Win32AssetScheme.EnvironmentOptions);

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

    public IWindowBackend CreateWindow(WebviewWindow window, OrielWindowOptions options, OrielApp app, EmbeddedAssetStore? assets)
    {
        var host = Win32WindowHost.Create(window, options, app, assets, this);

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
    /// 后端自身只持有共享的 WebView2 环境；托盘归 <see cref="OrielApp"/> 所有并由它释放
    /// （托盘会连带释放自己的消息窗口），调度窗口随消息循环结束销毁。
    /// </summary>
    public void Dispose()
    {
        // 共享环境比任何单个窗口活得久，所以窗口销毁时**不**释放它（见 Win32WindowHost 的清理），
        // 只在这里、应用退出时释放一次。
        if (_environment is { IsCompletedSuccessfully: true } environment)
        {
            environment.Result?.Dispose();
        }

        _environment = null;
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
