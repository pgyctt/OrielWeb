using System.IO.Pipes;
using System.Text.Json;
using OrielWeb.Ipc;

namespace OrielWeb;

/// <summary>OrielWeb 应用实例：持有平台后端、窗口列表与 IPC 分发器。</summary>
public sealed class OrielApp : IDisposable
{
    private readonly OrielAppBuilder _builder;
    private IPlatformBackend? _backend;

    internal OrielApp(OrielAppBuilder builder)
    {
        _builder = builder;
        Dispatcher = new OrielCommandDispatcher(builder.TargetFactories);
    }

    internal OrielCommandDispatcher Dispatcher { get; }

    /// <summary>内嵌资源使用的虚拟主机名（取自构建器 <c>UseEmbeddedAssets</c> 的 host 参数）。</summary>
    internal string AssetHost => _builder.AssetHost;

    /// <summary>「WebView2 运行时不可用」的处理回调（可能为 null，表示用库的默认提示）。</summary>
    internal Action<OrielWebView2RuntimeMissingEventArgs>? WebView2RuntimeMissingHandler
        => _builder.WebView2RuntimeMissingHandler;

    public IReadOnlyList<WebviewWindow> Windows => _windows;
    private readonly List<WebviewWindow> _windows = [];

    /// <summary>页面侧的主题事件名：<c>oriel.on('theme.changed', theme =&gt; …)</c>，payload 为 <c>"light"</c> / <c>"dark"</c>。</summary>
    internal const string ThemeEventName = "theme.changed";

    /// <summary>当前系统主题（应用未运行、或平台检测不到时按 <see cref="OrielTheme.Light"/> 处理）。</summary>
    public OrielTheme Theme => _backend?.CurrentTheme ?? OrielTheme.Light;

    /// <summary>
    /// 系统主题变化（用户在系统设置里切换深/浅色时触发）。
    /// 同一变化也会以 <c>theme.changed</c> 事件推给每个窗口的页面（含每次导航完成后的补推）。
    /// </summary>
    public event Action<OrielTheme>? ThemeChanged;

    private void OnThemeChanged(OrielTheme theme)
    {
        ThemeChanged?.Invoke(theme);
        foreach (WebviewWindow window in _windows)
        {
            PushTheme(window, theme);
        }
    }

    /// <summary>把主题推给某个窗口的页面。参数已是 JSON 文本（见 <see cref="WebviewWindow.EmitEvent(string, string)"/>）。</summary>
    private static void PushTheme(WebviewWindow window, OrielTheme theme)
        => window.EmitEvent(ThemeEventName, theme == OrielTheme.Dark ? "\"dark\"" : "\"light\"");

    /// <summary>把动作切回 UI 线程执行（命令完成后的回执、跨线程 UI 更新都用它）。</summary>
    public void PostToMainThread(Action action)
    {
        var backend = _backend ?? throw new InvalidOperationException("应用尚未运行（未调用 Run()）。");
        backend.PostToMainThread(action);
    }

    /// <summary>
    /// 请求退出应用：结束消息循环、让 <see cref="Run"/> 返回。
    /// 它不等于 <c>Environment.Exit</c>——<see cref="Run"/> 返回后该做的清理仍会执行，
    /// 这既让退出可测（进程真的走完流程），也让"退出前保存状态"有地方放。
    /// </summary>
    public void Quit()
    {
        var backend = _backend ?? throw new InvalidOperationException("应用尚未运行（未调用 Run()）。");
        backend.Quit();
    }

    // ---- 托盘与通知（应用级；详见 README 平台矩阵）----

    /// <summary>托盘图标；未用 <see cref="OrielAppBuilder.AddTray"/> 启用时为 null。</summary>
    public OrielTray? Tray { get; private set; }

    /// <summary>
    /// 移除托盘图标（<c>Shell_NotifyIcon(NIM_DELETE)</c> / <c>gtk_status_icon_set_visible(false)</c> /
    /// <c>NSStatusBar.removeStatusItem:</c>），应用继续运行。之后可用 <see cref="RestoreTray"/> 重建。
    /// </summary>
    /// <returns>是否确实移除了一个托盘（本来就没有时返回 <c>false</c>）。</returns>
    /// <remarks>
    /// 真值在于**原生资源真的被撤销**：Windows 上"图标消失但进程还在"与"进程没了图标还留着"
    /// （幽灵图标）是完全不同的两件事，后者只有靠正确调 <c>NIM_DELETE</c> 才不会发生。
    /// </remarks>
    public bool RemoveTray()
    {
        if (Tray is null)
        {
            return false;
        }

        Tray.Dispose();
        Tray = null;
        return true;
    }

    /// <summary>
    /// 按构建期 <see cref="OrielAppBuilder.AddTray"/> 的选项重建托盘。
    /// </summary>
    /// <returns>重建后的托盘；未配置过托盘或应用未运行时返回 null。</returns>
    /// <remarks>
    /// <b>重建出来的是一个新对象</b>：事件订阅与 <c>SetMenu</c> 都**不会**被带过去，
    /// 调用方需要重新挂上（这是不可避免的——事件处理器属于调用方）。
    /// </remarks>
    public OrielTray? RestoreTray()
    {
        if (Tray is not null)
        {
            return Tray; // 幂等：已经有了就返回现成的
        }

        if (_backend is null || _builder.TrayOptions is not { } options)
        {
            return null;
        }

        Tray = new OrielTray(_backend.CreateTray(options, this), options);
        return Tray;
    }

    /// <summary>本平台是否支持系统通知（不支持时 <see cref="ShowNotification(OrielNotificationOptions)"/> 是空操作）。</summary>
    public bool NotificationsSupported => _backend?.NotificationsSupported ?? false;

    /// <summary>用户点击了某条通知；参数是 <see cref="OrielNotificationOptions.Id"/>（见其平台差异说明）。</summary>
    public event Action<string>? NotificationClicked;

    /// <summary>
    /// 发送一条系统通知。
    /// </summary>
    /// <returns>
    /// <c>true</c> 表示**已成功提交给系统**，不等于"用户看见了"——横幅显示与否还取决于系统的
    /// 通知设置与专注助手（Windows 上可在「设置 → 系统 → 通知」里按应用名关闭；
    /// macOS 上未打包运行的通知归属于 Script Editor）。失败不抛异常，返回 <c>false</c>。
    /// </returns>
    public bool ShowNotification(OrielNotificationOptions notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        ArgumentException.ThrowIfNullOrWhiteSpace(notification.Title);
        var backend = _backend ?? throw new InvalidOperationException("应用尚未运行（未调用 Run()）。");
        return backend.ShowNotification(notification);
    }

    /// <summary>发送一条系统通知（便捷重载）；返回值语义见 <see cref="ShowNotification(OrielNotificationOptions)"/>。</summary>
    public bool ShowNotification(string title, string? body = null)
        => ShowNotification(new OrielNotificationOptions { Title = title, Body = body });

    // ---- 应用菜单（应用级）----

    /// <summary>应用菜单里的自定义项被点击；参数是该项的 <see cref="OrielMenuItem.Id"/>（role 项不走这里）。</summary>
    public event Action<string>? AppMenuItemClicked;

    /// <summary>
    /// 设置应用菜单：<b>macOS</b> 是顶部主菜单栏、<b>Windows</b> 是每个窗口的菜单栏、
    /// <b>Linux 不支持</b>（现代 GTK 应用用 header bar；详见 README 平台矩阵）。
    /// </summary>
    /// <remarks>
    /// 平台不支持的实现是**空操作**而不是抛异常：同一份跨平台代码里调用它不该因为换了平台就崩，
    /// "这个平台没有这个表面"应当是静默但可被文档查到的事实。
    /// </remarks>
    public void SetAppMenu(IReadOnlyList<OrielMenuItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var backend = _backend ?? throw new InvalidOperationException("应用尚未运行（未调用 Run()）。");
        backend.SetAppMenu(items, this);
    }

    /// <summary>清空应用菜单（macOS 与 Windows 都会移除现有菜单）。</summary>
    public void ResetAppMenu()
    {
        var backend = _backend ?? throw new InvalidOperationException("应用尚未运行（未调用 Run()）。");
        backend.ResetAppMenu(this);
    }

    // ---- 全局快捷键（应用级）----

    /// <summary>规范化串 → 用户给的原始加速键串（回调时原样回传，便于调用方直接比对）。</summary>
    private readonly Dictionary<string, string> _shortcuts = new(StringComparer.Ordinal);

    /// <summary>某个已注册的全局快捷键被按下；参数是注册时给的加速键串。</summary>
    public event Action<string>? GlobalShortcutActivated;

    /// <summary>
    /// 注册系统级快捷键（应用不在前台时也会触发）。返回 false 表示没注册成功，可能原因：
    /// 语法不合法、没有修饰键（会吞掉正常打字）、平台不支持（Linux 见 README）、
    /// 或该组合已被别的程序占用。<b>失败是正常结果，不是异常</b>——调用方应当据此提示用户换一个。
    /// </summary>
    public bool RegisterGlobalShortcut(string accelerator)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accelerator);
        var backend = _backend ?? throw new InvalidOperationException("应用尚未运行（未调用 Run()）。");

        // 必须带修饰键：没有修饰键的全局组合会把普通输入吞掉，这是 Ryn 也有的同一道闸
        if (!OrielAccelerator.TryParse(accelerator, OperatingSystem.IsMacOS(), requireModifier: true, out OrielAccelerator? parsed))
        {
            return false;
        }

        string id = parsed!.ToCanonicalString();
        if (_shortcuts.ContainsKey(id))
        {
            return true;
        }

        if (!backend.RegisterGlobalShortcut(parsed, id))
        {
            return false;
        }

        _shortcuts[id] = accelerator;
        return true;
    }

    /// <summary>注销一个已注册的快捷键；未注册时返回 false。</summary>
    public bool UnregisterGlobalShortcut(string accelerator)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accelerator);
        var backend = _backend ?? throw new InvalidOperationException("应用尚未运行（未调用 Run()）。");

        if (!OrielAccelerator.TryParse(accelerator, OperatingSystem.IsMacOS(), requireModifier: false, out OrielAccelerator? parsed))
        {
            return false;
        }

        string id = parsed!.ToCanonicalString();
        if (!_shortcuts.Remove(id))
        {
            return false;
        }

        _ = backend.UnregisterGlobalShortcut(id);
        return true;
    }

    /// <summary>该加速键当前是否已注册（按规范化结果比较，写法不同但等价算同一个）。</summary>
    public bool IsGlobalShortcutRegistered(string accelerator)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accelerator);
        return OrielAccelerator.TryParse(accelerator, OperatingSystem.IsMacOS(), requireModifier: false, out OrielAccelerator? parsed)
            && _shortcuts.ContainsKey(parsed!.ToCanonicalString());
    }

    /// <summary>注销本应用注册的全部快捷键。</summary>
    public void UnregisterAllGlobalShortcuts()
    {
        _shortcuts.Clear();
        _backend?.UnregisterAllGlobalShortcuts();
    }

    // ---- 徽章 ----

    /// <summary>
    /// 设置任务栏/Dock 徽章（<c>null</c> 或空串清除）。平台支持度见 README 平台矩阵：
    /// macOS 是 Dock 徽章（文字或数字），Windows 与 Linux 当前是 no-op。
    /// </summary>
    public void SetBadge(string? label)
    {
        var backend = _backend ?? throw new InvalidOperationException("应用尚未运行（未调用 Run()）。");
        backend.SetBadge(label);
    }

    // ---- 开机自启 ----

    /// <summary>自启标识：默认可执行文件名，可用 <see cref="OrielAppBuilder.UseAutoStartId"/> 覆盖。</summary>
    private string AutoStartId => _builder.AutoStartId ?? DefaultAutoStartId();

    /// <summary>默认自启标识 = 可执行文件名（不含扩展名）；取不到路径时退回 "OrielWeb"。</summary>
    internal static string DefaultAutoStartId()
    {
        string? executable = Environment.ProcessPath;
        return string.IsNullOrEmpty(executable)
            ? "OrielWeb"
            : Path.GetFileNameWithoutExtension(executable);
    }

    /// <summary>
    /// 本应用当前是否已设为开机自启。
    /// </summary>
    /// <remarks>
    /// 读的是**平台里实际存在的配置**，不是内存标记：用户可能自己在系统设置里关掉它
    /// （或手工删了 .desktop 文件），那时这里应当如实反映 false。
    /// </remarks>
    public bool IsAutoStartEnabled
    {
        get
        {
            var backend = _backend ?? throw new InvalidOperationException("应用尚未运行（未调用 Run()）。");
            return backend.IsAutoStartEnabled(AutoStartId);
        }
    }

    /// <summary>
    /// 启用开机自启（把配置写进平台的自启位置）。
    /// </summary>
    /// <param name="arguments">
    /// 随自启一起传入的命令行参数。典型用途是"开机静默启动到托盘"：<c>app.EnableAutoStart(["--minimized"])</c>。
    /// </param>
    /// <returns>是否写入成功（例如沙箱/权限受限时会失败）。</returns>
    public bool EnableAutoStart(IReadOnlyList<string>? arguments = null)
    {
        var backend = _backend ?? throw new InvalidOperationException("应用尚未运行（未调用 Run()）。");
        return backend.EnableAutoStart(AutoStartId, arguments);
    }

    /// <summary>关闭开机自启。返回是否执行成功（本来就没启用也算成功）。</summary>
    public bool DisableAutoStart()
    {
        var backend = _backend ?? throw new InvalidOperationException("应用尚未运行（未调用 Run()）。");
        return backend.DisableAutoStart(AutoStartId);
    }

    // ---- Shell（交给系统默认程序）----

    private static readonly OrielShellOptions s_defaultShellOptions = new();

    private OrielShellOptions ShellOptions => _builder.ShellOptions ?? s_defaultShellOptions;

    /// <summary>
    /// 用系统默认程序打开一个外部链接。
    /// </summary>
    /// <remarks>
    /// **只放行白名单内的 scheme**（默认 <c>http</c>/<c>https</c>/<c>mailto</c>，
    /// 见 <see cref="OrielShellOptions.AllowedSchemes"/>）：<c>file:</c>、<c>javascript:</c>、
    /// 裸路径与含控制字符的目标一律拒绝并返回 <c>false</c>。
    /// 这类 API 的风险不在"自己执行了什么"，而在"它决定让别的程序去打开什么"。
    /// </remarks>
    public bool OpenExternal(string url)
    {
        if (!OrielShellPolicy.IsAllowedUrl(url, ShellOptions.AllowedSchemes))
        {
            System.Diagnostics.Debug.WriteLine($"[OrielWeb] OpenExternal 拒绝了不在白名单内的目标：{url}");
            return false;
        }

        return OrielShellLauncher.Launch(OrielShellAction.OpenUrl, url);
    }

    /// <summary>
    /// 在系统文件管理器里显示一个文件：Windows 选中它、macOS 用 Finder 显示、
    /// Linux 打开它所在的目录（<c>xdg-open</c> 没有"选中"这个入口）。
    /// 路径不存在或不是绝对路径时返回 <c>false</c>，并且**不做任何调用**。
    /// </summary>
    public bool RevealInFileManager(string path)
    {
        if (!OrielShellPolicy.IsUsablePath(path))
        {
            System.Diagnostics.Debug.WriteLine($"[OrielWeb] RevealInFileManager 拒绝了不可用路径：{path}");
            return false;
        }

        return OrielShellLauncher.Launch(OrielShellAction.RevealPath, path);
    }

    /// <summary>用系统默认程序打开一个文件（路径必须存在且为绝对路径）。</summary>
    public bool OpenWithDefaultApp(string path)
    {
        if (!OrielShellPolicy.IsUsablePath(path))
        {
            System.Diagnostics.Debug.WriteLine($"[OrielWeb] OpenWithDefaultApp 拒绝了不可用路径：{path}");
            return false;
        }

        return OrielShellLauncher.Launch(OrielShellAction.OpenPath, path);
    }

    /// <summary>创建窗口、进入消息循环；阻塞直到所有窗口关闭。</summary>
    public void Run()
    {
        EnsureApartment();

        // 单实例判定必须在建窗之前：否则第二个实例会先闪出一个窗口再退出。
        if (_builder.SingleInstanceId is { } instanceId)
        {
            if (!SingleInstanceGuard.TryAcquire(instanceId, out FileStream? lockFile))
            {
                // 已有实例在跑：通知它，然后直接返回（不进消息循环）——Main 随之结束，退出码保持 0。
                SingleInstanceGuard.NotifyPrimary(instanceId);
                Console.WriteLine("SINGLE-INSTANCE-SECONDARY: 已有实例，已通知并退出");
                Console.Out.Flush();
                return;
            }

            _singleInstanceLock = lockFile;
            _singleInstanceServer = SingleInstanceGuard.CreateListener(instanceId);
        }

        _backend = PlatformBackendFactory.Create();
        _backend.ThemeChanged += OnThemeChanged;
        _backend.NotificationClicked += id => NotificationClicked?.Invoke(id);
        _backend.AppMenuItemClicked += id => AppMenuItemClicked?.Invoke(id);
        _backend.GlobalShortcutActivated += id =>
        {
            // 平台只回传规范化串；这里换回用户给的原始写法，调用方不必自己规范化
            if (_shortcuts.TryGetValue(id, out string? original))
            {
                GlobalShortcutActivated?.Invoke(original);
            }
        };

        // 托盘先于窗口创建：托盘是应用的外壳，先就绪才能让"启动即最小化到托盘"这类形态成立。
        // 与 RestoreTray 同一条路径，免得"启动时建"和"重建"两处各写一遍。
        _ = RestoreTray();

        if (_singleInstanceServer is not null)
        {
            StartSingleInstanceListener();
        }

        string? assetDirectory = _builder.UseAssets
            ? EmbeddedAssetExtractor.Extract(_builder.AssetResourcePrefix)
            : null;

        _windows.EnsureCapacity(_builder.PendingWindows.Count);
        foreach (var (window, options) in _builder.PendingWindows)
        {
            if (_builder.Debug)
            {
                options.Debug = true;
            }
            var backend = _backend.CreateWindow(window, options, this, assetDirectory);
            window.Attach(backend);
            _windows.Add(window);

            // 每次导航成功都补推一次当前主题：否则新文档要等到用户下次切换才知道现在是深还是浅
            WebviewWindow created = window;
            created.NavigationCompleted += args =>
            {
                if (args.Success)
                {
                    PushTheme(created, Theme);
                }
            };
        }

        _backend.RunMessageLoop();
    }

    /// <summary>
    /// Windows 要求主线程为 STA（WebView2 的 COM 初始化与 UI 消息循环依赖它）。
    /// Unix 平台的 <see cref="Thread.GetApartmentState"/> 恒返回 <see cref="ApartmentState.Unknown"/>，
    /// <c>[STAThread]</c> 特性在 Linux/macOS 上也被忽略，因此在非 Windows 平台必须跳过该检查。
    /// </summary>
    internal static void EnsureApartment()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
        {
            throw new InvalidOperationException(
                "OrielWeb 在 Windows 上要求主线程为 STA 线程：请在 Main 方法上标注 [STAThread]。" +
                "（WebView2 的 COM 初始化与 UI 消息循环依赖 STA）");
        }
    }

    // ---- 单实例 ----

    private NamedPipeServerStream? _singleInstanceServer;
    private FileStream? _singleInstanceLock;

    /// <summary>
    /// 首实例侧：后台等后续实例的连接，收到后在 UI 线程激活窗口。
    /// </summary>
    private void StartSingleInstanceListener()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await SingleInstanceGuard
                    .ListenAsync(_singleInstanceServer!, _ => PostToMainThread(ActivateAllWindows))
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // 监听结束（例如首实例正在退出）不算错误，记一笔即可
                System.Diagnostics.Debug.WriteLine($"[OrielWeb] 单实例监听结束：{ex.Message}");
            }
        });
    }

    /// <summary>把窗口前置并激活，随后交给应用的回调（若有）。</summary>
    private void ActivateAllWindows()
    {
        foreach (WebviewWindow window in _windows)
        {
            window.Show();
            window.Focus();
        }

        _builder.SingleInstanceActivateHandler?.Invoke(_windows.Count > 0 ? _windows[0] : null);
    }

    public void Dispose()
    {
        _singleInstanceServer?.Dispose();
        _singleInstanceLock?.Dispose();
        // 托盘先于后端释放：它的原生资源要靠后端所在的消息循环/主线程清理
        Tray?.Dispose();
        _backend?.Dispose();
    }
}
