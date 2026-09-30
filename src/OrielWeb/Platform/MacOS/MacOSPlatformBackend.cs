using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using OrielWeb.Platform.MacOS.Interop;

namespace OrielWeb.Platform.MacOS;

/// <summary>
/// macOS 平台后端：NSApplication 生命周期 + 主线程调度 + 窗口工厂。
/// </summary>
internal sealed unsafe class MacOSPlatformBackend : IPlatformBackend
{
    private nint _nsApp;
    private nint _appDelegate;
    private nint _pumpHelper;
    private int _aliveWindows;
    private int _ran;

    private static readonly ConcurrentQueue<Action> MainThreadQueue = MacOSObjCClasses.MainThreadQueue;
    // 主线程身份：托管线程 ID 在同一原生线程上保持稳定，足够用于回执快速路径判断
    private readonly int _mainManagedThreadId = Environment.CurrentManagedThreadId;

    public MacOSPlatformBackend()
    {
        // 必须最先做：纯 P/Invoke 的可执行文件不链接任何框架，不显式加载的话
        // objc_getClass 全部返回 nil，后续 objc_msgSend 退化为静默 no-op——
        // 进程会"正常"退出但从未建出窗口（见 ObjCRuntime.LoadFrameworks 的说明）。
        ObjCRuntime.LoadFrameworks();

        _nsApp = ObjCRuntime.SendId(ObjCRuntime.GetClassOrThrow("NSApplication"), ObjCRuntime.Sel("sharedApplication"));
        if (_nsApp == 0)
        {
            throw new InvalidOperationException("NSApplication.sharedApplication 返回 nil：AppKit 未能正常初始化。");
        }
        // NSApplicationActivationPolicyRegular = 0（普通应用，出现在 Dock）
        ObjCRuntime.SendVoidNint(_nsApp, ObjCRuntime.Sel("setActivationPolicy:"), 0);

        _appDelegate = MacOSObjCClasses.CreateAppDelegate();
        ObjCRuntime.SendVoidObj(_nsApp, ObjCRuntime.Sel("setDelegate:"), _appDelegate);

        _pumpHelper = MacOSObjCClasses.CreatePumpHelper();

        // 主题：先记初值（否则"启动时已是深色"会漏报一次），再订系统通知。
        // NSDistributedNotificationCenter 的 addObserver:selector:name:object: **不**持有观察者，
        // 所以必须自己留着引用（_themeObserver），否则通知到达时对象已释放。
        _lastTheme = CurrentTheme;
        _themeObserver = MacOSObjCClasses.CreateThemeObserver(this);
        nint center = ObjCRuntime.SendId(
            ObjCRuntime.GetClassOrThrow("NSDistributedNotificationCenter"),
            ObjCRuntime.Sel("defaultCenter"));
        ObjCRuntime.SendVoidObjSelObjObj(
            center,
            ObjCRuntime.Sel("addObserver:selector:name:object:"),
            _themeObserver,
            ObjCRuntime.Sel("orielThemeChanged:"),
            ObjCRuntime.MakeNSString("AppleInterfaceThemeChangedNotification"),
            0);
    }

    // ---- 系统主题 ----

    private OrielTheme _lastTheme = OrielTheme.Light;
    private nint _themeObserver;

    public event Action<OrielTheme>? ThemeChanged;

    public OrielTheme CurrentTheme => IsDarkTheme() ? OrielTheme.Dark : OrielTheme.Light;

    /// <summary>重新读主题，变化了才上报。</summary>
    internal void RaiseThemeIfChanged()
    {
        OrielTheme theme = CurrentTheme;
        if (theme == _lastTheme)
        {
            return;
        }

        _lastTheme = theme;
        ThemeChanged?.Invoke(theme);
    }

    /// <summary>
    /// 判定当前是否为深色：<c>NSUserDefaults</c> 的 <c>AppleInterfaceStyle</c> **仅在深色时存在**
    /// （值为 "Dark"），浅色时该键不存在——所以"取不到"就是浅色，不是失败。
    /// </summary>
    private static bool IsDarkTheme()
    {
        nint defaults = ObjCRuntime.SendId(
            ObjCRuntime.GetClassOrThrow("NSUserDefaults"), ObjCRuntime.Sel("standardUserDefaults"));
        if (defaults == 0)
        {
            return false;
        }

        nint value = ObjCRuntime.SendIdObj(
            defaults, ObjCRuntime.Sel("stringForKey:"), ObjCRuntime.MakeNSString("AppleInterfaceStyle"));
        if (value == 0)
        {
            return false;
        }

        string? style = ObjCRuntime.ToManagedString(value);
        return style is not null && style.Contains("Dark", StringComparison.OrdinalIgnoreCase);
    }

    // ---- IPlatformBackend ----

    public IWindowBackend CreateWindow(WebviewWindow window, OrielWindowOptions options, OrielApp app, string? assetDirectory)
    {
        var host = new MacOSWindowHost(window, options, app, assetDirectory, this);
        _aliveWindows++;
        // 构造函数只装配宿主，真正的 NSWindow 创建（initWithContentRect → 挂 delegate/webview → orderFront）在这里完成。
        // Windows 后端的建窗在静态工厂内完成，Linux/macOS 后端是实例方法，必须显式调用。
        // 未真机验证：本修复与 Linux 侧同源（见 docs/DECISIONS.md「Linux 首次真机运行验证」），待有 Mac 环境时复验。
        host.Create();
        return host;
    }

    public ITrayBackend CreateTray(OrielTrayOptions options, OrielApp app) => new MacOSTrayBackend(app, options);

    // ---- 应用菜单（macOS 就是顶部主菜单栏）----

    private MacOSMenu? _appMenu;

    public event Action<string>? AppMenuItemClicked;

    /// <summary>
    /// macOS 的应用菜单就是主菜单栏（<c>NSApplication.setMainMenu:</c>），这是三平台里语义最贴合的。
    /// </summary>
    public void SetAppMenu(IReadOnlyList<OrielMenuItem> items, OrielApp app)
    {
        _appMenu?.Dispose();
        _appMenu = MacOSMenu.Build(items, item => ActivateAppMenuItem(item, app));
        _appMenu?.SetAsMainMenu();
    }

    public void ResetAppMenu(OrielApp app)
    {
        _appMenu?.Dispose();
        _appMenu = null;
        MacOSMenu.ClearMainMenu();
    }

    /// <summary>
    /// 应用菜单里的 role 有明确的作用目标（不像托盘那样无窗口可用）：取第一个窗口。
    /// 多窗口场景下"关闭/最小化哪个窗口"本就没有通用答案，取首个是明确且可预期的选择。
    /// </summary>
    private void ActivateAppMenuItem(OrielMenuItem item, OrielApp app)
    {
        if (item.Role is { Length: > 0 } role)
        {
            WebviewWindow? target = app.Windows.Count > 0 ? app.Windows[0] : null;
            if (!OrielMenuRoles.TryActivate(role, app, target))
            {
                System.Diagnostics.Debug.WriteLine($"[OrielWeb] 应用菜单的 role「{role}」在 macOS 上未被处理。");
            }
            return;
        }

        if (item.Id is { Length: > 0 } id)
        {
            AppMenuItemClicked?.Invoke(id);
        }
    }

    // ---- 通知 ----

    /// <summary>macOS 自带 osascript，因此通知总是可用（未打包运行时的唯一可行路径）。</summary>
    public bool NotificationsSupported => MacOSNotificationSender.IsAvailable;

    /// <summary>
    /// 声明但**永不触发**：osascript 投递拿不到点击回调。要在 macOS 上报点击得进 .app bundle
    /// 后用 <c>UNUserNotificationCenter</c>（已记入 ROADMAP）。
    /// </summary>
    /// <remarks>显式空实现：与 Linux 侧同一理由——让"没有触发源"这件事在代码里看得见。</remarks>
    public event Action<string>? NotificationClicked
    {
        add { }
        remove { }
    }

    public void ShowNotification(OrielNotificationOptions notification) => MacOSNotificationSender.Send(notification);

    public void RunMessageLoop()
    {
        if (Interlocked.Exchange(ref _ran, 1) == 1)
        {
            return;
        }
        // run 前把窗口带到前台
        ObjCRuntime.SendVoidBool(_nsApp, ObjCRuntime.Sel("activateIgnoringOtherApps:"), true);
        ObjCRuntime.SendVoid(_nsApp, ObjCRuntime.Sel("run"));
    }

    public void Quit() => ObjCRuntime.SendVoidObj(_nsApp, ObjCRuntime.Sel("terminate:"), 0);

    public bool IsOnUiThread() => Environment.CurrentManagedThreadId == _mainManagedThreadId;

    public void PostToMainThread(Action action)
    {
        MainThreadQueue.Enqueue(action);
        // pump 帮助实例常驻，selector 在主线程排空队列
        ObjCRuntime.SendVoidSelObjBool(
            _pumpHelper,
            ObjCRuntime.Sel("performSelectorOnMainThread:withObject:waitUntilDone:"),
            ObjCRuntime.Sel("orielPump:"),
            0,
            false);
    }

    internal void OnWindowDestroyed()
    {
        if (Interlocked.Decrement(ref _aliveWindows) <= 0)
        {
            // 最后一个窗口关闭 → 结束应用（NSApplication.run 返回）
            Quit();
        }
    }

    public void Dispose()
    {
    }
}
