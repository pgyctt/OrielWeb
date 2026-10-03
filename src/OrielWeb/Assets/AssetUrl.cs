namespace OrielWeb;

/// <summary>
/// 内嵌资源的 URL 形态与解析：<c>oriel://&lt;host&gt;/&lt;path&gt;</c>。
/// </summary>
/// <remarks>
/// <para>
/// 三平台统一走自定义 scheme。以前是"Windows 用 https 虚拟主机、Linux/macOS 改写成
/// <c>file://</c> 本地文件"，两个形态带来两个后果：一是必须把资源解压到磁盘（file:// 要真文件），
/// 二是页面来源三平台不一致（<c>file://</c> 是不透明来源，<c>fetch</c>／存储／安全上下文的行为都不一样）。
/// 统一成 <c>oriel://</c> 后，三平台同一套 URL、同一份来源、同一个安全上下文语义。
/// </para>
/// <para>
/// <c>https://&lt;host&gt;/…</c> 仍然接受，作为**兼容别名**映射到同一份资源：0.2.0 及以前
/// 的文档与示例写的都是这个形态，直接不认会让已有项目白屏。别名只影响"入参怎么解析"，
/// 页面真正的来源始终是 <c>oriel://</c>，所以可信前缀只需要登记 <c>oriel://</c> 一条。
/// </para>
/// </remarks>
internal static class AssetUrl
{
    /// <summary>内嵌资源的 scheme。</summary>
    internal const string Scheme = "oriel";

    /// <summary>可信来源前缀（页面真实来源的判定用，逐字节 StartsWith）。</summary>
    internal static string TrustedPrefix(string host) => $"{Scheme}://{host}/";

    /// <summary>拼一个资源 URL（默认首页等地方用）。</summary>
    internal static string ForHost(string host, string relativePath) => $"{Scheme}://{host}/{relativePath}";

    /// <summary>窗口没设 <c>Url</c> 时导航到的默认文档。</summary>
    internal static string DefaultDocument(string host) => ForHost(host, "index.html");

    /// <summary>
    /// 尝试把 URL 解析成内嵌资源的相对路径。
    /// </summary>
    /// <param name="url">窗口要导航到的 URL。</param>
    /// <param name="host">内嵌资源的 host（<c>UseEmbeddedAssets</c> 的第一个参数）。</param>
    /// <param name="relativePath">解析出的资源相对路径（已归一，可能是 <c>index.html</c>）。</param>
    /// <returns>
    /// true 表示这是一个指向内嵌资源的 URL，调用方应交给 scheme 处理器；
    /// false 表示按普通外部 URL 处理（别的 host、别的 scheme、或路径里有穿越段）。
    /// </returns>
    internal static bool TryResolve(string url, string host, out string relativePath)
    {
        relativePath = string.Empty;

        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        // 只有 oriel 与兼容别名 http/https 属于"内嵌资源"这条语义；别的 scheme 原样放行。
        if (!IsAssetScheme(uri.Scheme))
        {
            return false;
        }

        // host 必须精确相等：像 app.oriel.example.com 这种"后缀上像"的域名不能命中。
        if (!string.Equals(uri.Host, host, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // query 与 fragment 不参与资源定位（URI 的 AbsolutePath 本来就不含它们）。
        string? normalized = EmbeddedAssetStore.NormalizeRequestPath(uri.AbsolutePath);
        if (normalized is null)
        {
            return false;
        }

        relativePath = normalized;
        return true;
    }

    private static bool IsAssetScheme(string scheme)
        => string.Equals(scheme, Scheme, StringComparison.OrdinalIgnoreCase)
           || scheme == Uri.UriSchemeHttp
           || scheme == Uri.UriSchemeHttps;
}
