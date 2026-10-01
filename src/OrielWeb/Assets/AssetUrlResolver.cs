namespace OrielWeb;

/// <summary>
/// 把指向内嵌资源虚拟主机的 URL（形如 <c>https://app.oriel/manual-check.html</c>）解析成它在磁盘上的
/// 本地文件路径，供**没有虚拟主机机制**的平台在导航前改写。
/// </summary>
/// <remarks>
/// 三平台在这件事上的能力不同，而 <see cref="OrielWindowOptions.Url"/> 的语义只有一套：
/// <list type="bullet">
///   <item>Windows：WebView2 有 <c>SetVirtualHostNameToFolderMapping</c>，URL 原样交给引擎即可，同源 https、免 CORS。</item>
///   <item>Linux（WebKitGTK）与 macOS（WKWebView）：两者都只能注册**自定义** scheme，
///   而 <c>https</c> 是保留 scheme、注册不了。于是 <c>https://app.oriel/…</c> 会变成一次真实的网络请求，
///   DNS 解析失败后引擎渲染错误页——表现就是**一片空白的窗口**，且没有任何宿主侧异常。
///   这两个平台因此在导航前用本类把 URL 映射成本地文件。</item>
/// </list>
/// 映射只是补平台能力，不改调用方语义：同一个 URL 在三个平台上落到同一份资源上。
/// </remarks>
internal static class AssetUrlResolver
{
    /// <summary>
    /// 若 <paramref name="url"/> 指向 <paramref name="assetHost"/> 下的内嵌资源、且对应文件确实存在，
    /// 返回该文件的绝对路径；否则返回 null，调用方应按普通外部 URL 处理。
    /// </summary>
    /// <param name="url">窗口要导航到的 URL（<see cref="OrielWindowOptions.Url"/> 的值）。</param>
    /// <param name="assetHost">虚拟主机名，取自 <c>UseEmbeddedAssets(host)</c>。</param>
    /// <param name="assetDirectory">内嵌资源解压出的本地目录（<see cref="EmbeddedAssetExtractor"/> 的产物）。</param>
    public static string? TryResolveLocalFile(string url, string assetHost, string? assetDirectory)
    {
        if (string.IsNullOrEmpty(assetDirectory) || string.IsNullOrWhiteSpace(assetHost))
        {
            return null;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        // 只有 http/https 才属于"虚拟主机"这条语义；别的 scheme（含 file:）原样放行。
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        // host 必须精确相等：像 app.oriel.example.com 这种"后缀上像"的域名不能命中。
        if (!string.Equals(uri.Host, assetHost, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // 路径取 URI 的 path 部分（query 与 fragment 不参与文件定位）；空路径按站点根处理。
        var relative = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/');
        if (relative.Length == 0)
        {
            relative = "index.html";
        }

        // 目录穿越防护：解析后必须仍在资源目录之内，`../` 与绝对路径都在这里被挡下。
        var root = Path.GetFullPath(assetDirectory);
        var separator = Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', separator)));
        if (!full.StartsWith(root + separator, StringComparison.Ordinal))
        {
            return null;
        }

        return File.Exists(full) ? full : null;
    }
}
