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

    /// <summary>创建窗口、进入消息循环；阻塞直到所有窗口关闭。</summary>
    public void Run()
    {
        EnsureApartment();

        _backend = PlatformBackendFactory.Create();
        _backend.ThemeChanged += OnThemeChanged;

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

    public void Dispose()
    {
        _backend?.Dispose();
    }
}
