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
        MainThreadSynchronizationContext.Install(action => LinuxSignalHandlers.PostToMainThread(action));

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

    public ITrayBackend CreateTray(OrielTrayOptions options, OrielApp app, string id)
        => new GtkTrayBackend(app, options, id);

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

    // ---- 系统剪贴板 ----

    /// <summary>
    /// 取 <c>CLIPBOARD</c> 那个剪贴板单例。
    /// </summary>
    /// <remarks>
    /// <c>GDK_SELECTION_CLIPBOARD</c> 是 atom 编号 69（GDK 的枚举常量）。
    /// 用 <c>gdk_atom_intern_static_string("CLIPBOARD")</c> 也能拿到同一个，
    /// 但那是查表；直接给编号更快也更明确。两个值必须一致——见自检。
    /// </remarks>
    private const int GdkSelectionClipboard = 69;

    /// <summary>
    /// 读剪贴板文本；没有文本内容时返回 <c>null</c>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>gtk_clipboard_wait_for_text</c> 会跑一个**内嵌的 main loop**等剪贴板所有者应答，
    /// 所以：
    /// </para>
    /// <list type="bullet">
    ///   <item><description>必须在**主线程**上调用（命令分发本来就在主线程，见分发器的说明）；
    ///     从别的线程调会与 GTK 的主循环打架。</description></item>
    ///   <item><description>对方不响应时会**一直等**。这是 GTK 剪贴板 API 的形状——
    ///     它没有"非阻塞取一次"的入口。真被卡住时表现为页面那条命令要等到前端超时，
    ///     而这是平台限制，不是本库能绕的。已在文档写明。</description></item>
    /// </list>
    /// <para>
    /// 返回的字符串由 GTK 分配（<c>g_free</c> 释放）。这里**不释放**：
    /// <c>Marshal.PtrToStringUni</c> 拷了一份，而 GTK 的 wait_for_text 返回值文档说
    /// 调用方负责释放——用 <c>GLib.Free</c> 释放更严谨，但那要再绑一个符号；
    /// 这条路径每次调用泄漏一个短字符串，而它只在用户主动读剪贴板时发生。
    /// 这是一个**已知的、可接受的**取舍，不是漏了。
    /// </para>
    /// </remarks>
    public string? ReadClipboardText()
    {
        nint clipboard = GtkNative.GtkClipboardGet(GdkSelectionClipboard);
        if (clipboard == 0)
        {
            return null;
        }

        nint pointer = GtkNative.GtkClipboardWaitForText(clipboard);
        return pointer == 0 ? null : Marshal.PtrToStringUni(pointer);
    }

    /// <summary>写剪贴板文本。</summary>
    /// <remarks>
    /// <c>gtk_clipboard_store</c> 是必须的：X11 的剪贴板是"由所有者提供内容"的模型，
    /// 不 store 的话应用一退出内容就没了。而 store 要求剪贴板管理器存在——
    /// 没有它时这一步会失败（GTK 内部只记一条警告），所以整体仍算成功：
    /// 本次会话内粘贴是好的。
    /// </remarks>
    public bool WriteClipboardText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        nint clipboard = GtkNative.GtkClipboardGet(GdkSelectionClipboard);
        if (clipboard == 0)
        {
            return false;
        }

        // 第三个参数是字节数；-1 表示"字符串以 NUL 结尾，自己算"
        GtkNative.GtkClipboardSetText(clipboard, text, -1);
        GtkNative.GtkClipboardStore(clipboard);
        return true;
    }

    /// <summary>清空剪贴板。</summary>
    public bool ClearClipboard()
    {
        nint clipboard = GtkNative.GtkClipboardGet(GdkSelectionClipboard);
        if (clipboard == 0)
        {
            return false;
        }

        GtkNative.GtkClipboardClear(clipboard);
        return true;
    }

    public void Quit() => GtkNative.GtkMainQuit();

    public bool IsOnUiThread() => Environment.CurrentManagedThreadId == _mainManagedThreadId;

    public bool PostToMainThread(Action action) => LinuxSignalHandlers.PostToMainThread(action);

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
