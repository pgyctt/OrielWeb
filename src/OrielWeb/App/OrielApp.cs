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
