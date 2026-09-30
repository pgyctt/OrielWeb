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

    /// <summary>本平台是否支持系统通知（不支持时 <see cref="ShowNotification(OrielNotificationOptions)"/> 是空操作）。</summary>
    public bool NotificationsSupported => _backend?.NotificationsSupported ?? false;

    /// <summary>用户点击了某条通知；参数是 <see cref="OrielNotificationOptions.Id"/>（见其平台差异说明）。</summary>
    public event Action<string>? NotificationClicked;

    /// <summary>发送一条系统通知。</summary>
    public void ShowNotification(OrielNotificationOptions notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        ArgumentException.ThrowIfNullOrWhiteSpace(notification.Title);
        var backend = _backend ?? throw new InvalidOperationException("应用尚未运行（未调用 Run()）。");
        backend.ShowNotification(notification);
    }

    /// <summary>发送一条系统通知（便捷重载）。</summary>
    public void ShowNotification(string title, string? body = null)
        => ShowNotification(new OrielNotificationOptions { Title = title, Body = body });

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

        // 托盘先于窗口创建：托盘是应用的外壳，先就绪才能让"启动即最小化到托盘"这类形态成立
        if (_builder.TrayOptions is { } trayOptions)
        {
            Tray = new OrielTray(_backend.CreateTray(trayOptions, this), trayOptions);
        }

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
