using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using DirectN.Extensions.Com;
using OrielWeb.Ipc;
using OrielWeb.Platform.Windows.Interop;
using WebView2;
using WebView2.Utilities;

namespace OrielWeb.Platform.Windows;

/// <summary>
/// Windows 窗口宿主的 V2 实现（迁移路线 A：现成的 <c>WebView2Aot</c> 包）。
/// Win32 侧逻辑完全复用 <see cref="Win32WindowHost"/>，只把 WebView2 的 COM 互操作
/// 从"手工 vtable + 手工 CCW"换成源生成绑定：
/// <c>[GeneratedComInterface]</c>（RCW）与 <c>[GeneratedComClass]</c>（CCW，由包内事件处理器承担）。
/// </summary>
/// <remarks>
/// 与 V1 的关键差异：
/// <list type="bullet">
/// <item>环境/控制器以 <c>Task</c> 异步创建，结果是可释放的 <c>IComObject&lt;T&gt;</c>；
/// 不再有"裸指针 + AddRef 后永不 Release"的泄漏。</item>
/// <item>事件经 <see cref="CoreWebView2Events"/> 以标准 .NET 事件订阅，回调 CCW 由包提供，
/// 因此不再需要手工维护 vtable / IID / RefCount。</item>
/// <item>版本化接口成员（如 <c>SetVirtualHostNameToFolderMapping</c>）在包装层直达，
/// 无需手工 <c>QueryInterface</c>，也就没有"运行时过旧导致静默降级"的盲区。</item>
/// </list>
/// 用环境变量 <c>ORIEL_WIN_BACKEND=legacy</c> 可切回 V1。
/// </remarks>
internal sealed class Win32WindowHostV2 : Win32WindowHost
{
    private static int s_loaderInitialized;

    private IComObject<ICoreWebView2Environment>? _environment;
    private IComObject<ICoreWebView2Controller>? _controller;
    private IComObject<ICoreWebView2>? _webView;
    private CoreWebView2Events? _webViewEvents;

    private Win32WindowHostV2(
        WebviewWindow window, OrielWindowOptions options, OrielApp app,
        string? assetDirectory, WindowsPlatformBackend backend)
        : base(window, options, app, assetDirectory, backend)
    {
    }

    /// <summary>创建 V2 宿主；窗口类、WndProc 与创建流程全部复用 V1。</summary>
    public static new Win32WindowHostV2 Create(
        WebviewWindow window, OrielWindowOptions options, OrielApp app,
        string? assetDirectory, WindowsPlatformBackend backend)
        => CreateHost(window, options, app, assetDirectory, backend,
            static (w, o, a, d, b) => new Win32WindowHostV2(w, o, a, d, b));

    // ------------------------------------------------------------------
    // WebView2 装配（覆盖 V1 的手工 COM 路径）
    // ------------------------------------------------------------------

    internal override void InitializeWebView2()
    {
        // 装配是异步的：立即返回，完成后继续（失败经 OnWebViewFailed 提示并销毁窗口）
        _ = InitializeWebView2Async();
    }

    private async Task InitializeWebView2Async()
    {
        try
        {
            EnsureLoaderInitialized();

            string? browserFolder = Environment.GetEnvironmentVariable("ORIEL_WEBVIEW2_FOLDER");
            _environment = await WebView2.Functions.CreateCoreWebView2EnvironmentWithOptionsAsync(
                browserFolder, _userDataFolder, null).ConfigureAwait(true)
                ?? throw new InvalidOperationException("创建 WebView2 环境失败（返回 null）。");

            _controller = await _environment.CreateCoreWebView2ControllerAsync(_hwnd).ConfigureAwait(true)
                ?? throw new InvalidOperationException("创建 WebView2 控制器失败（返回 null）。");

            _webView = _controller.CoreWebView2
                ?? throw new InvalidOperationException("获取 CoreWebView2 失败（返回 null）。");

            // 内嵌资产 → 虚拟主机（同源 https，免 CORS）；版本化接口成员在包装层直达，
            // 因此不需要像 V1 那样做 QueryInterface + 失败诊断
            if (_assetDirectory is not null)
            {
                _webView.SetVirtualHostNameToFolderMapping(
                    _assetHost,
                    _assetDirectory,
                    COREWEBVIEW2_HOST_RESOURCE_ACCESS_KIND.COREWEBVIEW2_HOST_RESOURCE_ACCESS_KIND_DENY_CORS);
            }

            if (_webView.Settings is { } settings)
            {
                using (settings)
                {
                    settings.AreDevToolsEnabled = _options.Debug;
                }
            }

            SubscribeEvents();

            await _webView.AddScriptToExecuteOnDocumentCreatedAsync(OrielBridgeJs.Script).ConfigureAwait(true);

            _controller.IsVisible = true;
            UpdateBounds();

            string url = _options.Url ?? $"https://{_assetHost}/index.html";
            _webView.Navigate(url);
        }
        catch (Exception ex)
        {
            OnWebViewFailed($"初始化 WebView2 失败：{ex.Message}");
        }
    }

    /// <summary>一次性把 WebView2Loader.dll 从入口程序集的嵌入资源解压并加载（单文件发布友好）。</summary>
    private static void EnsureLoaderInitialized()
    {
        if (Interlocked.Exchange(ref s_loaderInitialized, 1) == 1)
        {
            return;
        }

        WebView2Utilities.Initialize(Assembly.GetEntryAssembly());
    }

    private void SubscribeEvents()
    {
        _webViewEvents = new CoreWebView2Events(_webView!);
        _webViewEvents.WebMessageReceived += OnWebMessageReceived;
        _webViewEvents.NavigationCompleted += OnNavigationCompleted;
        _webViewEvents.DocumentTitleChanged += OnDocumentTitleChanged;
    }

    private void OnWebMessageReceived(object? sender, ICoreWebView2WebMessageReceivedEventArgs args)
    {
        string? json = args.WebMessageAsJson;
        if (string.IsNullOrEmpty(json))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("__oriel", out var kind)
                && kind.ValueKind == JsonValueKind.String
                && kind.GetString() == "invoke")
            {
                _ = DispatchInvokeAsync(root.Clone());
            }
        }
        catch (JsonException)
        {
            // 非 OrielWeb 消息（页面自定义 postMessage），忽略
        }
    }

    private async Task DispatchInvokeAsync(JsonElement message)
    {
        try
        {
            await App.Dispatcher.HandleInvokeAsync(message, new V2ReplySink(this)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // 分发器内部已按命令捕获；此处兜底分发器之外的错误
            PostMessageOnUi($"{{\"__oriel\":\"result\",\"id\":0,\"ok\":false,\"error\":{JsonText.EncodeString(ex.Message)}}}");
        }
    }

    private void OnNavigationCompleted(object? sender, ICoreWebView2NavigationCompletedEventArgs args)
    {
        if (args.IsSuccess)
        {
            RaiseLoadedIfFirst();
        }
    }

    private void OnDocumentTitleChanged(object? sender, EventArgs args)
    {
        string? title = _webView?.DocumentTitle;
        if (!string.IsNullOrEmpty(title))
        {
            RaiseTitleChanged(title);
        }
    }

    // ------------------------------------------------------------------
    // 覆盖 V1 的 COM 相关成员
    // ------------------------------------------------------------------

    internal override void UpdateBounds()
    {
        var controller = _controller;
        if (controller is null || !Win32.GetClientRect(_hwnd, out var client))
        {
            return;
        }

        controller.Bounds = new DirectN.RECT
        {
            left = 0,
            top = 0,
            right = client.Right,
            bottom = client.Bottom,
        };
    }

    public override async Task<string> ExecuteScriptAsync(string script)
    {
        var webView = _webView
            ?? throw new InvalidOperationException("WebView2 尚未就绪。");

        return await webView.ExecuteScriptAsync(script).ConfigureAwait(true) ?? "null";
    }

    public override void PostMessageAsJson(string json) => _backend.PostToMainThread(() => PostMessageOnUi(json));

    internal override void PostWebMessageOnUi(string json) => PostMessageOnUi(json);

    private void PostMessageOnUi(string json)
    {
        try
        {
            _webView?.PostWebMessageAsJson(json);
        }
        catch (Exception ex)
        {
            // 窗口销毁后的迟到回执，忽略
            Debug.WriteLine($"[OrielWeb] 投递回执失败（窗口可能已销毁）：{ex.Message}");
        }
    }

    internal override void OnWindowDestroyedCore()
    {
        // 先释放 COM 资源，再交给基类触发 Closed 与窗口计数
        _webViewEvents?.Dispose();
        _webViewEvents = null;

        var controller = Interlocked.Exchange(ref _controller, null);
        if (controller is not null)
        {
            try
            {
                controller.Close(); // 先通知控制器关闭，再释放句柄
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[OrielWeb] 关闭 WebView2 控制器失败：{ex.Message}");
            }
            controller.Dispose();
        }

        Interlocked.Exchange(ref _webView, null)?.Dispose();
        Interlocked.Exchange(ref _environment, null)?.Dispose();

        base.OnWindowDestroyedCore();
    }

    /// <summary>IPC 回执通道：必须切回 UI 线程（WebView2 的 COM 绑定在 STA）。</summary>
    private sealed class V2ReplySink(Win32WindowHostV2 host) : IIpcReplySink
    {
        public void PostJson(string json)
        {
            if (host.IsOnUiThread())
            {
                host.PostWebMessageOnUi(json);
            }
            else
            {
                host._backend.PostToMainThread(() => host.PostWebMessageOnUi(json));
            }
        }
    }
}
