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

        // GTK 主循环没有 SynchronizationContext：安装后 async 命令 await 的续体回主线程，
        // 与 Windows 语义一致（命令里 await 之后可以直接碰 UI/平台对象）。见 API.md 线程模型。
        MainThreadSynchronizationContext.Install(LinuxSignalHandlers.PostToMainThread);

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
    /// 系统双击间隔（毫秒）：GtkSettings 的 <c>gtk-double-click-time</c>。
    /// </summary>
    /// <remarks>
    /// 取不到时返回 0，由 <see cref="OrielSystemSnapshot.Normalize"/> 统一兜底——
    /// 回退值只该有那一处，否则三个平台会各自攒出一个不一样的默认值。
    /// </remarks>
    internal static int ReadDoubleClickTimeMs()
    {
        nint settings = GtkNative.GtkSettingsGetDefault();
        return settings == 0
            ? 0
            : WithGValue(settings, "gtk-double-click-time", "gint", value => GtkNative.GValueGetInt(value));
    }

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

    public IWindowBackend CreateWindow(WebviewWindow window, OrielWindowOptions options, OrielApp app, EmbeddedAssetStore? assets)
    {
        var host = new LinuxWindowHost(window, options, app, assets, this);

        // 构造函数只装配宿主，真正的 GTK 窗口（gtk_window_new → show_all → 首次导航）在这里创建。
        // Windows 后端的建窗在静态工厂内完成，Linux/macOS 后端是实例方法，必须显式调用。
        host.Create();

        // 计数在建窗**成功之后**才自增：Create 抛异常时窗口并不存在，先自增会让计数虚高、
        // 把"最后一个窗口关闭 → 退出"的时点推早（评审 P3）。销毁侧的自减与之对称。
        _aliveWindows++;
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

    // ---- 开机自启（freedesktop 的 autostart 目录）----

    public bool EnableAutoStart(string id, IReadOnlyList<string>? arguments) => LinuxAutoStart.Enable(id, arguments);

    public bool DisableAutoStart(string id) => LinuxAutoStart.Disable(id);

    public bool IsAutoStartEnabled(string id) => LinuxAutoStart.IsEnabled(id);

    // ---- 通知 ----
    // Linux 的通知交给 freedesktop 通知守护（经 notify-send 子进程）。

    /// <summary>有 notify-send 就认为通知可用（没有守护进程时它也只是静默失败，不会带崩进程）。</summary>
    public bool NotificationsSupported => LinuxNotificationSender.IsAvailable;

    public bool ShowNotification(OrielNotificationOptions notification, string appId)
        => LinuxNotificationSender.Send(notification, appId);

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
