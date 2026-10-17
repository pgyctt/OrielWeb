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

    /// <summary>
    /// 磁盘文件（<c>convertFileSrc</c>）专用的 host。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么用另一个 host 而不是复用内嵌资源的 host</b>：这是安全性上的关键一步。
    /// 页面从 <c>oriel://app.oriel/</c> 加载，而桥接脚本只注入到**可信来源**
    /// （见 <see cref="TrustedPrefix"/>）。若磁盘文件也从同一个 host 提供，
    /// 那么磁盘上任何一个 HTML 文件被导航到就会**继承应用的来源**并拿到完整的 IPC 能力——
    /// "下载一个 .html 到应用数据目录、再让应用打开它"就成了一条提权路径。
    /// </para>
    /// <para>
    /// 换 host 之所以真的能隔开，是因为 Windows 侧登记 scheme 时开了
    /// <c>HasAuthorityComponent</c>（见 Win32AssetScheme 的说明）——那时 host 是真正的
    /// authority、参与来源计算，<c>oriel://file.oriel</c> 与 <c>oriel://app.oriel</c>
    /// 是**两个来源**。少了那个开关，两者的来源都退化成 <c>oriel://</c>，隔离就没了。
    /// </para>
    /// <para>
    /// Tauri 用的是另一个**协议**（<c>asset://</c>）而不是另一个 host。这里用 host 是因为
    /// 本库只登记一个自定义 scheme（三平台各要登记一次，多一个就多三处平台代码），
    /// 而隔离效果同源——两条路的要点都是"文件内容与页面不同源"。
    /// </para>
    /// </remarks>
    internal const string FileHost = "file.oriel";

    /// <summary>拼一个资源 URL（默认首页等地方用）。</summary>
    internal static string ForHost(string host, string relativePath) => $"{Scheme}://{host}/{relativePath}";

    /// <summary>
    /// 把宿主路径拼成页面可以放进 <c>src</c> 的 URL（对标 Tauri 的 <c>convertFileSrc</c>）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>整条路径编成**一个**路径段</b>（分隔符也编进去），而不是逐段编码。
    /// 这一点是踩过之后才定的，理由值得写下来：
    /// </para>
    /// <para>
    /// URI 的 path 部分**总是以 <c>/</c> 开头**，所以"绝对路径的前导斜杠"与"我们为了拼 URL
    /// 加的那个斜杠"在解析时无法区分——逐段编码时 <c>/tmp/a.png</c> 会解成 <c>tmp/a.png</c>，
    /// 在 POSIX 上就从绝对路径变成了相对路径（于是解析到进程当前目录，取错文件）。
    /// 整段编码没有这个问题：编码后的整串里没有分隔符，解析回来就是原样。
    /// </para>
    /// <para>
    /// 代价是 URL 不可读（<c>oriel://file.oriel/%2Ftmp%2Fa.png</c>）。可以接受——
    /// 它是给 <c>src=</c> 用的，不是给人看的，而正确性比可读性重要。
    /// </para>
    /// <para>
    /// <b>这里不校验"路径是否被允许"</b>（Tauri 也不校验）：这一步只是拼字符串，
    /// 真正的强制点在于 webview 去请求它时（见 <see cref="OrielAssetFileServer"/> 查 scope）。
    /// 在拼 URL 这步拦没有意义——页面自己也能拼出一个同样的 URL。
    /// </para>
    /// </remarks>
    internal static string FileUrl(string path)
        => $"{Scheme}://{FileHost}/{Uri.EscapeDataString(path)}";

    /// <summary>
    /// 尝试把 URL 解析成"磁盘文件路径"（<see cref="FileHost"/> 下的请求）。
    /// </summary>
    /// <returns>true 表示这是文件请求，<paramref name="path"/> 是**已解码**的原始路径。</returns>
    /// <remarks>
    /// 与 <see cref="FileUrl"/> 严格对称：去掉 URI path 那个必然存在的前导 <c>/</c>，
    /// 然后百分号解码整段。**不要 <c>TrimStart('/')</c>**——那会把绝对路径的前导斜杠
    /// 一起吃掉（见 <see cref="FileUrl"/> 的说明）。
    /// </remarks>
    internal static bool TryResolveFile(string url, out string path)
    {
        path = string.Empty;

        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        if (!IsAssetScheme(uri.Scheme)
            || !string.Equals(uri.Host, FileHost, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string raw = uri.AbsolutePath;

        // 精确去掉一个前导 '/'（URI path 必然有，而它不属于原路径）
        if (raw.StartsWith('/'))
        {
            raw = raw[1..];
        }

        path = Uri.UnescapeDataString(raw);
        return path.Length > 0;
    }

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
