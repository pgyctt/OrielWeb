using DirectN.Extensions.Com;
using WebView2;
using WebView2.Utilities;

namespace OrielWeb.Platform.Windows;

/// <summary>
/// Windows 侧的 <c>oriel://</c> 应答器：把内嵌资源直接喂给 WebView2，不经过磁盘。
/// </summary>
/// <remarks>
/// <para>
/// 取代了旧的 <c>SetVirtualHostNameToFolderMapping(host, 解压目录, DENY_CORS)</c>。那一套要求资源先落盘，
/// 而且页面来源是 https 虚拟主机（与 Linux/macOS 的 file:// 不一致）。现在三平台同一套 URL、同一个来源。
/// </para>
/// <para>
/// 两件事在有窗口之前就要做对：
/// </para>
/// <list type="number">
///   <item><b>登记自定义 scheme</b>：<c>CoreWebView2CustomSchemeRegistration</c> 必须随**环境**创建一起给出去
///     （环境全进程只建一次），所以调用点在 <c>WindowsPlatformBackend.GetEnvironmentAsync</c>；
///     <c>TreatAsSecure</c> 对齐 Linux 的 <c>register_uri_scheme_as_secure</c>，
///     <c>HasAuthorityComponent</c> 则决定 URL 里的 host 是否算 authority——少了它，页面来源会退化成
///     "oriel://"（无主机），可信前缀永远配不上，桥接根本不安装。</item>
///   <item><b>拦截过滤器要在导航前加</b>：<c>AddWebResourceRequestedFilter</c> 只匹配本 scheme，
///     于是 dev server 那类 http/https 请求原样走网络，不会被我们接管。</item>
/// </list>
/// </remarks>
internal static class Win32AssetScheme
{
    private static readonly object Gate = new();
    private static CoreWebView2EnvironmentOptions? s_options;

    /// <summary>
    /// 登记自定义 scheme。必须在创建 WebView2 环境之前调用（环境全进程共享，只建一次）。
    /// </summary>
    internal static void Prepare(string host)
    {
        lock (Gate)
        {
            if (s_options is not null)
            {
                return;
            }

            var registration = new CoreWebView2CustomSchemeRegistration(AssetUrl.Scheme);

            // 与 Linux 的 register_uri_scheme_as_secure 对齐：决定 window.isSecureContext
            // （crypto.subtle、部分存储 API 看它）。
            registration.put_TreatAsSecure(true);

            // URL 里的 app.oriel 必须被当作 authority。少了这一条，Chromium 会把 oriel://app.oriel/x
            // 解析成"无主机的自定义 scheme"：页面来源变成 oriel://，可信前缀 oriel://app.oriel/ 配不上，
            // 表现为"窗口打开了、页面也在，但什么命令都没反应"。
            registration.put_HasAuthorityComponent(true);

            // AllowedOrigins 刻意留空：页面本身就在这个 scheme 下，同源请求不需要 CORS 条目；
            // 真需要从别处跨源访问内嵌资源时再加（那属于扩权，要有明确理由）。

            var options = new CoreWebView2EnvironmentOptions();
            options.SetCustomSchemeRegistrations([registration]);
            s_options = options;
        }
    }

    /// <summary>环境创建用的选项（未登记时返回 null，行为与"没有内嵌资源"时一致）。</summary>
    internal static ICoreWebView2EnvironmentOptions? EnvironmentOptions
    {
        get
        {
            lock (Gate)
            {
                return s_options;
            }
        }
    }

    /// <summary>
    /// 把 <c>oriel://</c> 请求拦截挂到某个 webview 上。
    /// </summary>
    /// <returns>
    /// 事件处理器——**调用方必须持有**：掉引用后托管侧的回调对象就没人引用了，拦截会静默失效
    /// （症状是页面白屏，而宿主侧什么都不报）。
    /// </returns>
    internal static CoreWebView2WebResourceRequestedEventHandler Attach(
        ICoreWebView2Environment environment, ICoreWebView2 webView, EmbeddedAssetStore store, string host)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(webView);

        // 订阅与过滤器都不回报 HRESULT（生成的绑定是 void）：真出错时症状是"资源加载不出来"，
        // 所以 Windows 冒烟里的自检是这条路径唯一的守门人。
        webView.AddWebResourceRequestedFilter(
            $"{AssetUrl.Scheme}://*",
            COREWEBVIEW2_WEB_RESOURCE_CONTEXT.COREWEBVIEW2_WEB_RESOURCE_CONTEXT_ALL);

        var handler = new CoreWebView2WebResourceRequestedEventHandler(
            (sender, args) => OnRequest(environment, args, store, host));

        // 订阅返回 void（生成的绑定不回报 HRESULT），失败会在"资源加载不出来"上体现；
        // handler 的存活由调用方负责。
        EventRegistrationToken token = default;
        webView.add_WebResourceRequested(handler, ref token);
        return handler;
    }

    private static void OnRequest(
        ICoreWebView2Environment environment,
        ICoreWebView2WebResourceRequestedEventArgs args,
        EmbeddedAssetStore store,
        string host)
    {
        // 任何异常都不能漏出去：这里跑在 WebView2 的回调里，抛出等于让引擎等一个永不到来的应答。
        try
        {
            args.get_Request(out ICoreWebView2WebResourceRequest request).ThrowOnError();
            if (request is null)
            {
                return;
            }

            // 用扩展属性而不是 get_Uri(out PWSTR)：字符串的释放交给包装层，这里不碰 PWSTR。
            string uri = request.Uri ?? string.Empty;

            if (!AssetUrl.TryResolve(uri, host, out string relative))
            {
                // 过滤器只放本 scheme 进来，正常不会走到这；真走到了就不设 Response，
                // 交给网络层按"未知地址"处理，而不是悄悄给一份自己的资源。
                return;
            }

            if (!store.TryGet(relative, out EmbeddedAsset asset))
            {
                // 404 必须是 404：自检里"导航到不存在的页面应当失败"那一步靠它。
                SetResponse(environment, args, Stream.Null, 404, "Not Found", "text/plain; charset=utf-8");
                return;
            }

            // 先把内容读进托管内存再交出去：WebView2 是**异步**读这个流的（回调返回之后还在读），
            // 所以不能在这里 using/dispose；留一个 MemoryStream 由响应对象持有，GC 管它。
            byte[] payload;
            using (Stream source = store.Open(asset))
            {
                payload = new byte[source.Length];
                source.ReadExactly(payload);
            }

            SetResponse(environment, args, new MemoryStream(payload, writable: false), 200, "OK", asset.ContentType);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[OrielWeb] 应答内嵌资源失败：{ex}");
            TryFail(environment, args);
        }
    }

    private static void TryFail(ICoreWebView2Environment environment, ICoreWebView2WebResourceRequestedEventArgs args)
    {
        try
        {
            SetResponse(environment, args, Stream.Null, 500, "Internal Server Error", "text/plain; charset=utf-8");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[OrielWeb] 写 500 响应也失败了：{ex}");
        }
    }

    private static void SetResponse(
        ICoreWebView2Environment environment,
        ICoreWebView2WebResourceRequestedEventArgs args,
        Stream content,
        int statusCode,
        string reasonPhrase,
        string contentType)
    {
        string headers = $"Content-Type: {contentType}";
        IComObject<ICoreWebView2WebResourceResponse>? response =
            environment.CreateWebResourceResponse(content, statusCode, reasonPhrase, headers);

        if (response is null)
        {
            return;
        }

        args.put_Response(response.Object).ThrowOnError();
    }
}
