using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
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

    /// <summary>
    /// 把 GTK 的应用级主题开关设成"深色 / 浅色 / 跟随系统"（B15 的 <c>Window.setTheme</c> 落点）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// GTK **没有"按窗口选主题"的入口**：能改的只有应用级的
    /// <c>gtk-application-prefer-dark-theme</c>（改了之后**所有** GTK 窗口都跟着变）。
    /// 所以 <c>Window.setTheme</c> 与 <c>App.setTheme</c> 在 Linux 上落到同一个地方、
    /// 按调用次序后者赢——这是 GTK 的形状，不是本库的缺口。
    /// </para>
    /// <para>
    /// <c>null</c> = 跟随系统：把 <c>gtk-application-prefer-dark-theme</c> 还原成
    /// <c>IsDarkTheme</c> 读出来的系统值（而不是留着上一次的覆写）。
    /// </para>
    /// </remarks>
    internal void SetApplicationTheme(OrielTheme? theme)
    {
        nint settings = GtkNative.GtkSettingsGetDefault();
        if (settings == 0)
        {
            return;
        }

        // 用 GValue 写（g_value_set_boolean + g_object_set_property）——
        // 与读取（WithGValue）同一套 GValue 生命周期，只是方向反过来。
        bool dark = theme switch
        {
            OrielTheme.Dark => true,
            OrielTheme.Light => false,
            // null = 跟随系统：还原成系统当前值（IsDarkTheme 读的正是这条属性 + 主题名）
            _ => IsDarkTheme(),
        };

        nint value = Marshal.AllocHGlobal(GValueSize);
        try
        {
            Marshal.Copy(new byte[GValueSize], 0, value, GValueSize);
            GtkNative.GValueInit(value, GtkNative.GTypeFromName("gboolean"));
            try
            {
                GtkNative.GValueSetBoolean(value, dark ? 1 : 0);
                GtkNative.GObjectSetProperty(settings, "gtk-application-prefer-dark-theme", value);
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

    /// <summary>显示器查询（见 <see cref="IMonitorBackend"/>）。无状态，每次调用都重新枚举。</summary>
    public IMonitorBackend Monitors { get; } = new LinuxMonitorBackend();

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
    /// 拷贝之后原串就没人用了，而 GTK 的 wait_for_text 返回值文档说调用方负责释放
    /// ——用 <c>GLib.Free</c> 释放更严谨，但那要再绑一个符号；
    /// 这条路径每次调用泄漏一个短字符串，而它只在用户主动读剪贴板时发生。
    /// 这是一个**已知的、可接受的**取舍，不是漏了。
    /// </para>
    /// <para>
    /// <b>必须用 <c>PtrToStringUTF8</c> 而不是 <c>PtrToStringUni</c></b>：
    /// <c>gtk_clipboard_wait_for_text</c> 返回的是 **UTF-8** 的 <c>char*</c>。
    /// 按 UTF-16 解读的后果不是乱码那么"明显"——ASCII 会被两两配对成汉字
    /// （<c>"oriel-selftest"</c> 读成 <c>"牯敩⵬敳晬整瑳"</c>），
    /// 而那看起来像"剪贴板里本来就是乱码"，不会让人想到编码。
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
        return pointer == 0 ? null : Marshal.PtrToStringUTF8(pointer);
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
    /// <remarks>
    /// <para>
    /// <b>只调 <c>gtk_clipboard_clear</c> 不够</b>：X11 的剪贴板是"所有者提供内容"的模型，
    /// 而本库写的时候会 <c>gtk_clipboard_store</c> 把内容交给**剪贴板管理器**持久化
    /// （否则应用一退内容就没了）。清空时管理器手里那一份不会跟着消失——
    /// 于是"清空之后再读"会从管理器拿回**旧内容**，看起来像 clear 没生效。
    /// </para>
    /// <para>
    /// 所以清空要写成"放弃所有权 + 让管理器也拿到空内容"：clear 之后 set 一个空串再 store。
    /// 代价是读回的是空串而不是 null——两者都表示"没有可粘贴的文本"，
    /// 而空串本身也是合法文本内容（接口语义允许）。
    /// </para>
    /// </remarks>
    public bool ClearClipboard()
    {
        nint clipboard = GtkNative.GtkClipboardGet(GdkSelectionClipboard);
        if (clipboard == 0)
        {
            return false;
        }

        GtkNative.GtkClipboardClear(clipboard);

        // 把"空"也交给管理器，否则它会继续保留上一次 store 的内容
        GtkNative.GtkClipboardSetText(clipboard, "", -1);
        GtkNative.GtkClipboardStore(clipboard);
        return true;
    }

    // ---- 剪贴板：图片 / HTML / 敏感文本（B19）----

    /// <summary>
    /// 读剪贴板里的图片并编码成 PNG。
    /// </summary>
    /// <remarks>
    /// <c>gtk_clipboard_wait_for_image</c> 与 <c>wait_for_text</c> 同一性质：同步阻塞、
    /// 跑一个内嵌 main loop、必须在主线程调用（见 <see cref="ReadClipboardText"/> 的说明）。
    /// <para>
    /// 不用 <c>gdk_pixbuf_save_to_buffer</c> 直接要 PNG：那是**变参函数**
    /// （<c>(pixbuf, &amp;buffer, &amp;size, "png", &amp;error, NULL)</c>），
    /// 而 C# 的源生成 P/Invoke 不支持变参。所以这里读裸像素、自己编码
    /// （<see cref="OrielPngEncoder"/>），顺带把"通道数 3 还是 4"这件事显式处理掉。
    /// </para>
    /// </remarks>
    public unsafe byte[]? ReadClipboardImage()
    {
        nint clipboard = GtkNative.GtkClipboardGet(GdkSelectionClipboard);
        if (clipboard == 0)
        {
            return null;
        }

        nint pixbuf = GtkNative.GtkClipboardWaitForImage(clipboard);
        if (pixbuf == 0)
        {
            return null;
        }

        try
        {
            int width = GtkNative.GdkPixbufGetWidth(pixbuf);
            int height = GtkNative.GdkPixbufGetHeight(pixbuf);
            int stride = GtkNative.GdkPixbufGetRowstride(pixbuf);
            int channels = GtkNative.GdkPixbufGetNChannels(pixbuf);

            if (width <= 0 || height <= 0 || (channels != 3 && channels != 4) || stride < width * channels)
            {
                return null;
            }

            byte* pixels = (byte*)GtkNative.GdkPixbufGetPixels(pixbuf);
            if (pixels is null)
            {
                return null;
            }

            var rgba = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
            {
                byte* row = pixels + (y * stride);
                int target = y * width * 4;

                for (int x = 0; x < width; x++)
                {
                    int s = x * channels;
                    int t = target + (x * 4);
                    rgba[t] = row[s];
                    rgba[t + 1] = row[s + 1];
                    rgba[t + 2] = row[s + 2];
                    rgba[t + 3] = channels == 4 ? row[s + 3] : (byte)255;
                }
            }

            return OrielPngEncoder.Encode(rgba, width, height);
        }
        finally
        {
            // wait_for_image 返回的是新引用，必须自己放
            GtkNative.GObjectUnref(pixbuf);
        }
    }

    /// <summary>把 PNG 写进剪贴板（解码成 pixbuf 后交给 GTK）。</summary>
    public unsafe bool WriteClipboardImage(byte[] png)
    {
        ArgumentNullException.ThrowIfNull(png);

        OrielImage image;
        try
        {
            image = OrielImage.FromBytes(png);
        }
        catch (OrielImageFormatException)
        {
            return false;
        }

        nint clipboard = GtkNative.GtkClipboardGet(GdkSelectionClipboard);
        if (clipboard == 0)
        {
            return false;
        }

        int width = image.Width;
        int height = image.Height;

        // data 传 0 = pixbuf 自己分配缓冲（见 GdkPixbufNewFromData 的说明）
        nint pixbuf = GtkNative.GdkPixbufNewFromData(0, 0, 1, 8, width, height, width * 4, 0, 0);
        if (pixbuf == 0)
        {
            return false;
        }

        try
        {
            byte* pixels = (byte*)GtkNative.GdkPixbufGetPixels(pixbuf);
            if (pixels is null)
            {
                return false;
            }

            ReadOnlySpan<byte> source = image.Rgba.Span;
            for (int y = 0; y < height; y++)
            {
                source.Slice(y * width * 4, width * 4)
                      .CopyTo(new Span<byte>(pixels + (y * width * 4), width * 4));
            }

            GtkNative.GtkClipboardSetImage(clipboard, pixbuf);
            GtkNative.GtkClipboardStore(clipboard);
            return true;
        }
        finally
        {
            // set_image 会自己持有引用，这里放掉我们这份
            GtkNative.GObjectUnref(pixbuf);
        }
    }

    /// <summary>写 HTML（<c>text/html</c> + UTF-8 纯文本两个目标）。</summary>
    /// <remarks>
    /// GTK 没有"直接设置 HTML"的入口：<c>gtk_clipboard_set_text</c> 只认纯文本，
    /// 多目标必须走 <c>gtk_clipboard_set_with_data</c>（它要求调用方在对方请求时**回调**提供数据）。
    /// 回调的生命周期比这次调用长，所以目标表、目标名与状态都放在非托管内存里
    /// （见 <see cref="ClipboardOffer"/> 的说明）。
    /// </remarks>
    public bool WriteClipboardHtml(string html, string? altText)
    {
        ArgumentNullException.ThrowIfNull(html);

        byte[] htmlBytes = Encoding.UTF8.GetBytes(html);
        byte[] textBytes = Encoding.UTF8.GetBytes(altText ?? html);

        return OfferTargets(
        [
            ("text/html", htmlBytes),
            ("UTF8_STRING", textBytes),
            ("text/plain;charset=utf-8", textBytes),
        ]);
    }

    /// <summary>写敏感文本：文本 + <c>x-kde-passwordManagerHint=secret</c>。</summary>
    /// <remarks>
    /// <c>x-kde-passwordManagerHint</c> 是 KDE 发起、被 klipper / GPaste 等剪贴板管理器认的约定。
    /// 与 macOS 的 ConcealedType 一样，它只是**请求**。
    /// </remarks>
    public bool WriteClipboardSecret(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        byte[] bytes = Encoding.UTF8.GetBytes(text);
        return OfferTargets(
        [
            ("UTF8_STRING", bytes),
            ("text/plain;charset=utf-8", bytes),
            ("x-kde-passwordManagerHint", "secret"u8.ToArray()),
        ]);
    }

    // ------------------------------------------------------------------ 应用级（B23）

    /// <summary>
    /// Dock 图标显隐（见 <see cref="IPlatformBackend.SetDockVisibility"/>）。
    /// </summary>
    /// <remarks>
    /// Linux 上没有"应用级 Dock 图标"这个概念：任务是**每个窗口**一条（由窗口管理器 /
    /// 桌面环境决定），应用本身没有被独立表示的图标。抛 <see cref="NotSupportedException"/>，
    /// 与 Tauri 在 Linux 上不做 <c>set_dock_visibility</c> 一致。
    /// </remarks>
    public void SetDockVisibility(bool visible)
        => throw new NotSupportedException(
            "setDockVisibility 只有 macOS 有：Linux 的任务条目由窗口管理器按窗口生成，"
            + "没有应用级的 Dock 图标可以隐藏（隐藏单个窗口用 window.setSkipTaskbar）。");

    /// <summary>Linux 没有"按标识符隔离的浏览数据"（WebKitGTK 的 WebsiteDataManager 没有 UUID 模型）。</summary>
    public bool SupportsDataStoreIdentifiers => false;

    /// <inheritdoc />
    public void RemoveDataStoreData(Guid identifier)
        => throw new NotSupportedException(
            "数据存储标识符只有 macOS 的 WKWebsiteDataStore 有："
            + "WebKitGTK 的 WebsiteDataManager 没有按 UUID 分库的模型。");

    /// <summary>以多目标方式声明剪贴板内容。</summary>
    private static unsafe bool OfferTargets((string Target, byte[] Data)[] items)
    {
        nint clipboard = GtkNative.GtkClipboardGet(GdkSelectionClipboard);
        if (clipboard == 0)
        {
            return false;
        }

        ClipboardOffer offer = ClipboardOffer.Create(items);

        bool claimed = GtkNative.GtkClipboardSetWithData(
            clipboard,
            offer.Targets,
            (uint)items.Length,
            &OnClipboardGet,
            &OnClipboardClear,
            offer.UserData);

        if (!claimed)
        {
            offer.Release();
            return false;
        }

        GtkNative.GtkClipboardStore(clipboard);
        return true;
    }

    /// <summary>GTK 在对方请求数据时调用：按 <paramref name="info"/> 取对应的那段字节。</summary>
    /// <remarks>
    /// <c>[UnmanagedCallersOnly]</c> 的方法里**不能让托管异常逃出去**（那会直接终止进程），
    /// 所以整段包在 try/catch 里，失败只进诊断日志。
    /// </remarks>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnClipboardGet(nint clipboard, nint selectionData, uint info, nint userData)
    {
        try
        {
            if (GCHandle.FromIntPtr(userData).Target is not ClipboardOffer offer
                || info >= (uint)offer.Items.Length)
            {
                return;
            }

            byte[] data = offer.Items[(int)info].Data;
            nint target = GtkNative.GtkSelectionDataGetTarget(selectionData);

            fixed (byte* pointer = data)
            {
                _ = GtkNative.GtkSelectionDataSet(selectionData, target, 8, (nint)pointer, data.Length);
            }
        }
        catch (Exception ex)
        {
            OrielDiagnostics.Report($"[OrielWeb] 剪贴板取数回调失败：{ex.Message}");
        }
    }

    /// <summary>GTK 在剪贴板所有权失去时调用：释放在 <see cref="ClipboardOffer"/> 里的非托管内存。</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnClipboardClear(nint clipboard, nint userData)
    {
        try
        {
            if (GCHandle.FromIntPtr(userData).Target is ClipboardOffer offer)
            {
                offer.Release();
            }
        }
        catch (Exception ex)
        {
            OrielDiagnostics.Report($"[OrielWeb] 剪贴板清理回调失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 一次"多目标剪贴板内容"的非托管状态（目标表 + 目标名 + 数据）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// GTK 保存的是**指针**：目标表的每一条指向一个 C 字符串，回调里的 <c>userData</c>
    /// 指向这份状态。它们在所有权失去（<c>clearFunc</c>）之前必须一直有效——
    /// 所以全部放在非托管内存里，并用 <see cref="GCHandle"/> 把托管对象钉住。
    /// </para>
    /// <para>
    /// <see cref="Release"/> 是**幂等**的：GTK 可能先调 clear 再让我们自己在失败路径里收尾，
    /// 两处都调不能变成 double free。
    /// </para>
    /// </remarks>
    private sealed class ClipboardOffer
    {
        private readonly nint[] _strings;
        private GCHandle _handle;
        private bool _released;

        private ClipboardOffer((string Target, byte[] Data)[] items)
        {
            Items = items;
            _strings = new nint[items.Length];
        }

        /// <summary>目标与对应的字节。</summary>
        public (string Target, byte[] Data)[] Items { get; }

        /// <summary><c>GtkTargetEntry[]</c> 的非托管地址。</summary>
        public nint Targets { get; private set; }

        /// <summary>传给 GTK 的 <c>userData</c>（GCHandle 指针）。</summary>
        public nint UserData { get; private set; }

        public static ClipboardOffer Create((string Target, byte[] Data)[] items)
        {
            var offer = new ClipboardOffer(items);
            int size = Marshal.SizeOf<GtkTargetEntry>();

            offer.Targets = Marshal.AllocHGlobal(size * items.Length);
            for (int i = 0; i < items.Length; i++)
            {
                offer._strings[i] = Marshal.StringToCoTaskMemUTF8(items[i].Target);
                var entry = new GtkTargetEntry
                {
                    Target = offer._strings[i],
                    Flags = 0,
                    Info = (uint)i,
                };

                Marshal.StructureToPtr(entry, offer.Targets + (i * size), fDeleteOld: false);
            }

            offer._handle = GCHandle.Alloc(offer);
            offer.UserData = GCHandle.ToIntPtr(offer._handle);
            return offer;
        }

        public void Release()
        {
            if (_released)
            {
                return;
            }

            _released = true;

            foreach (nint text in _strings)
            {
                if (text != 0)
                {
                    Marshal.FreeCoTaskMem(text);
                }
            }

            if (Targets != 0)
            {
                Marshal.FreeHGlobal(Targets);
                Targets = 0;
            }

            if (_handle.IsAllocated)
            {
                _handle.Free();
            }
        }
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
