using System.Runtime.InteropServices;
using OrielWeb.Platform.Linux.Interop;

namespace OrielWeb.Platform.Linux;

/// <summary>
/// Linux 平台后端：GTK3 主循环 + 主线程调度（g_idle_add_full）+ 窗口工厂。
/// 依赖：libgtk-3 / libwebkit2gtk-4.1（apt install libwebkit2gtk-4.1-dev）。
/// </summary>
internal sealed class LinuxPlatformBackend : IPlatformBackend
{
    private int _aliveWindows;
    private int _ran;
    private readonly int _mainManagedThreadId = Environment.CurrentManagedThreadId;

    public LinuxPlatformBackend()
    {
        GtkNative.GtkInit(0, 0);

        // 主题：先记初值（否则"启动时已是深色"会漏报一次），再接上变化信号
        _lastTheme = CurrentTheme;
        nint settings = GtkNative.GtkSettingsGetDefault();
        if (settings != 0)
        {
            LinuxSignalHandlers.ConnectThemeSignals(settings, this);
        }
    }

    // ---- 系统主题 ----

    /// <summary>GValue 在 64 位平台上的大小：GType（8）+ data 联合（2×8）= 24 字节。</summary>
    private const int GValueSize = 24;

    private OrielTheme _lastTheme = OrielTheme.Light;

    public event Action<OrielTheme>? ThemeChanged;

    public OrielTheme CurrentTheme => IsDarkTheme() ? OrielTheme.Dark : OrielTheme.Light;

    /// <summary>重新读主题，变化了才上报（信号可能因任一属性变化而触发）。</summary>
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
    /// 判定当前是否为深色。两条依据任一成立即深色：
    /// ① <c>gtk-application-prefer-dark-theme</c>（<c>GTK_THEME=Adwaita:dark</c> 这类会置真）；
    /// ② 主题名里含 "dark"（Adwaita-dark / Yaru-dark / Breeze-Dark …）。
    /// 只看主题名会在默认主题上漏判（名字通常不含 dark），所以两条都要看。
    /// </summary>
    private static bool IsDarkTheme()
    {
        // 依据 ①：GTK_THEME 环境变量（如 "Adwaita:dark"）。这是 GTK 认可的显式覆盖，也是 CI 里
        // 「造两种值」的入口——它**不**会反映到下面两个属性上（实测过），所以要单独看。
        string? envTheme = Environment.GetEnvironmentVariable("GTK_THEME");
        if (envTheme is not null && envTheme.Contains("dark", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        nint settings = GtkNative.GtkSettingsGetDefault();
        if (settings == 0)
        {
            return false;
        }

        // 依据 ②：gtk-application-prefer-dark-theme（桌面环境切深色时常走这条）
        if (ReadSettingsBoolean(settings, "gtk-application-prefer-dark-theme"))
        {
            return true;
        }

        // 依据 ③：主题名里含 dark（Adwaita-dark / Yaru-dark / Breeze-Dark …）
        string? name = ReadSettingsString(settings, "gtk-theme-name");
        return name is not null && name.Contains("dark", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ReadSettingsBoolean(nint settings, string property)
        => WithGValue(settings, property, "gboolean", value => GtkNative.GValueGetBoolean(value) != 0);

    private static string? ReadSettingsString(nint settings, string property)
        => WithGValue(settings, property, "gchararray", value =>
        {
            nint text = GtkNative.GValueGetString(value);
            return text == 0 ? null : Marshal.PtrToStringUTF8(text);
        });

    /// <summary>
    /// 经 GValue 读一个 GObject 属性的通用壳。GValue 必须先清零再 <c>g_value_init</c>，
    /// 读完必须 <c>g_value_unset</c>（否则字符串类型会泄漏内部拷贝）。
    /// </summary>
    private static T WithGValue<T>(nint obj, string property, string typeName, Func<nint, T> read)
    {
        nint value = Marshal.AllocHGlobal(GValueSize);
        try
        {
            Marshal.Copy(new byte[GValueSize], 0, value, GValueSize);
            GtkNative.GValueInit(value, GtkNative.GTypeFromName(typeName));
            try
            {
                GtkNative.GObjectGetProperty(obj, property, value);
                return read(value);
            }
            finally
            {
                GtkNative.GValueUnset(value);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(value);
        }
    }

    public IWindowBackend CreateWindow(WebviewWindow window, OrielWindowOptions options, OrielApp app, string? assetDirectory)
    {
        var host = new LinuxWindowHost(window, options, app, assetDirectory, this);
        _aliveWindows++;
        // 构造函数只装配宿主，真正的 GTK 窗口（gtk_window_new → show_all → 首次导航）在这里创建。
        // Windows 后端的建窗在静态工厂内完成，Linux/macOS 后端是实例方法，必须显式调用。
        host.Create();
        return host;
    }

    public void RunMessageLoop()
    {
        if (Interlocked.Exchange(ref _ran, 1) == 1)
        {
            return;
        }
        GtkNative.GtkMain();
    }

    public ITrayBackend CreateTray(OrielTrayOptions options, OrielApp app) => new GtkTrayBackend(app, options);

    // ---- 应用菜单 ----
    // Linux 上**不实现**：现代 GTK 应用用 header bar，GTK3 的 GtkMenuBar 在主流桌面上已不再惯用；
    // 硬把一个菜单栏塞进 GtkWindow 还会与 webview 的布局层级打架（Ryn 在同一处也放弃了 Linux 菜单栏，
    // 给的理由是"应用惯例 + 框架拥有窗口的 child 层级"）。
    // 所以 SetAppMenu 是空操作、AppMenuItemClicked 永不触发——这是刻意的平台取舍，不是漏做。
    // 需要菜单的应用应当把入口画在页面里。

    public void SetAppMenu(IReadOnlyList<OrielMenuItem> items, OrielApp app)
    {
    }

    public void ResetAppMenu(OrielApp app)
    {
    }

    public event Action<string>? AppMenuItemClicked
    {
        add { }
        remove { }
    }

    // ---- 通知 ----
    // Linux 的通知交给 freedesktop 通知守护（经 notify-send 子进程）。

    /// <summary>有 notify-send 就认为通知可用（没有守护进程时它也只是静默失败，不会带崩进程）。</summary>
    public bool NotificationsSupported => LinuxNotificationSender.IsAvailable;

    /// <summary>
    /// 声明但**永不触发**：当前实现（notify-send）拿不到点击事件。
    /// 之所以保留而不是"Linux 上不实现该成员"：接口形态要跨平台一致，
    /// 平台差异写在文档里（<see cref="OrielNotificationOptions.Id"/>），而不是让 API 面随平台变。
    /// 要点击回传需改用 libnotify 的 action 回调，已记入 ROADMAP。
    /// </summary>
    /// <remarks>
    /// 写成显式空 add/remove，而不是留一个自动事件：这样"这里确实没有触发源"在代码里是可见的，
    /// 而不是看起来像"忘了触发"（编译器对从未触发的事件报警，正好把这个决定逼出来）。
    /// </remarks>
    public event Action<string>? NotificationClicked
    {
        add { }
        remove { }
    }

    public void ShowNotification(OrielNotificationOptions notification) => LinuxNotificationSender.Send(notification);

    public void Quit() => GtkNative.GtkMainQuit();

    public bool IsOnUiThread() => Environment.CurrentManagedThreadId == _mainManagedThreadId;

    public void PostToMainThread(Action action) => LinuxSignalHandlers.PostToMainThread(action);

    internal void OnWindowDestroyed()
    {
        if (Interlocked.Decrement(ref _aliveWindows) <= 0)
        {
            GtkNative.GtkMainQuit();
        }
    }

    public void Dispose()
    {
    }
}
