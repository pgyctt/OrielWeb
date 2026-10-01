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
        Dispatcher = new OrielCommandDispatcher(builder.TargetFactories, builder.JsonContext);
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

    // 通知点击上报**已移除**（原 `NotificationClicked` 事件）。
    // 三个平台都拿不到点击：未打包应用的 toast 激活需要开始菜单快捷方式携带 AUMID 并注册 COM 激活器
    // （打包器的职责），Linux 的 notify-send 与 macOS 的 osascript 则根本没有回调入口。
    // 原先三处都是"显式空实现"，看起来像是如实标注——但**订阅一个永不触发的事件不会编译报错**，
    // 调用方只会在运行时才发现收不到。删掉它，让"不支持"变成编译错误，这才是对的信号。
    // 详见 docs/DECISIONS.md 的「通知点击上报：直接删掉，而不是留一个空事件」。

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
        return backend.ShowNotification(notification, NotificationAppId);
    }

    /// <summary>发送一条系统通知（便捷重载）；返回值语义见 <see cref="ShowNotification(OrielNotificationOptions)"/>。</summary>
    public bool ShowNotification(string title, string? body = null)
        => ShowNotification(new OrielNotificationOptions { Title = title, Body = body });

    /// <summary>
    /// 通知的"应用标识"：Windows 上即 AUMID，Linux 上是 <c>notify-send --app-name</c>。
    /// 可用 <see cref="OrielAppBuilder.UseNotificationAppId"/> 覆盖，默认取入口程序集名。
    /// </summary>
    internal string NotificationAppId => _builder.NotificationAppId ?? DefaultNotificationAppId();

    /// <summary>
    /// 默认通知标识 = 入口程序集名（规范化后）；取不到程序集时退回可执行文件名，再退回 <c>OrielWeb</c>。
    /// </summary>
    /// <remarks>
    /// 用程序集名而不是可执行文件名：<c>dotnet run</c> 下 <see cref="Environment.ProcessPath"/> 是
    /// <c>dotnet</c>，所有开发期的应用会挤进同一个标识里。程序集名在 AOT 单文件下同样可用。
    /// </remarks>
    internal static string DefaultNotificationAppId()
    {
        string? name = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name;
        if (string.IsNullOrWhiteSpace(name))
        {
            name = Environment.ProcessPath is { } executable
                ? Path.GetFileNameWithoutExtension(executable)
                : null;
        }

        return OrielNotificationAppId.Sanitize(name);
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
            switch (SingleInstanceGuard.TryAcquire(instanceId, out FileStream? lockFile))
            {
                case SingleInstanceGuard.AcquireResult.AlreadyRunning:
                    // 已有实例在跑：激活请求已在 TryAcquire 里发出（那个探活连接就是判定手段），
                    // 这里直接返回（不进消息循环）——Main 随之结束，退出码保持 0。
                    // 下面这行是 tools/verify-macos.sh 与 demo 自检 grep 的**契约标记**，不要改措辞。
                    Console.WriteLine("SINGLE-INSTANCE-SECONDARY: 已有实例，已通知并退出");
                    Console.Out.Flush();
                    return;

                case SingleInstanceGuard.AcquireResult.Unavailable:
                    // 锁文件创建不出来（目录只读、磁盘满、沙箱拦截……），无法判定有没有别的实例。
                    // 这里**倾向于启动**：静默不启动（不建窗、退出码 0）比多开一个窗口难排查得多。
                    // 判定细节与理由见 SingleInstanceGuard.TryAcquire。
                    break;

                case SingleInstanceGuard.AcquireResult.Acquired:
                default:
                    _singleInstanceLock = lockFile;
                    _singleInstanceServer = SingleInstanceGuard.CreateListener(instanceId);
                    break;
            }
        }

        _backend = PlatformBackendFactory.Create();
        _backend.ThemeChanged += OnThemeChanged;

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

        // 消息循环结束 = 应用正在退出：这里主动释放一次。
        // 托盘图标尤其关键——它是**系统级**资源，只属于本进程却由 explorer 持有：
        // 不调 NIM_DELETE 撤销的话，进程没了图标还留在通知区（"幽灵图标"），
        // 用户只能把鼠标划过去等它自己消失。以前的 Run() 直接返回，于是每次退出都留一个。
        // Dispose 幂等，调用方之后再 Dispose 一次也无妨。
        try
        {
            Dispose();
        }
        catch (Exception ex)
        {
            // 退出路径不该因为清理失败而崩：报出来，让进程照常结束
            System.Diagnostics.Debug.WriteLine($"[OrielWeb] 退出清理时抛出异常：{ex}");
        }
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
