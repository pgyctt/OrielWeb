namespace OrielWeb;

/// <summary>
/// 能力（capabilities）配置：哪些 IPC 命令可以被页面调用。
/// </summary>
/// <remarks>
/// <para>
/// 默认姿态：**未配置时 Debug 构建全放行、Release 构建全部拒绝**。
/// 一旦调用了 <c>UseCapabilities</c>，就以这里写的内容为准（Debug / Release 都是）。
/// </para>
/// <para>
/// 模式支持两种：<c>todo.add</c>（精确名）与 <c>todo.*</c>（前缀通配）；<c>*</c> 匹配一切。
/// <b>deny 优先于 allow</b>——"先宽泛 allow、再用 deny 挖掉例外"是最常见的写法，
/// 若 allow 优先，那条 deny 就等于没写，而这个错误是静默的。
/// 两条都不命中时按拒绝算（deny-by-default：不在名单上就是没有这个能力）。
/// </para>
/// <para>
/// <b><c>win.</c> 前缀始终放行</b>，不受本配置约束：那是无边框窗口的标题栏按钮
/// （最小化 / 最大化 / 关闭 / 拖动），只操作自己那个窗口，不是"能力"。
/// 释放掉它们会让无边框窗口变成关不掉的窗口。应用自己的命令请避开这个前缀。
/// </para>
/// </remarks>
public sealed class OrielCapabilityOptions
{
    internal List<string> AllowedCommands { get; } = [];

    internal List<string> DeniedCommands { get; } = [];

    internal List<string> AllowedOrigins { get; } = [];

    /// <summary>允许页面调用这些命令（精确名或 <c>前缀.*</c>；<c>*</c> 表示全部）。可多次调用。</summary>
    public OrielCapabilityOptions Allow(params string[] patterns)
    {
        AddAll(AllowedCommands, patterns, nameof(patterns));
        return this;
    }

    /// <summary>拒绝页面调用这些命令（<b>优先于 <see cref="Allow"/></b>）。可多次调用。</summary>
    public OrielCapabilityOptions Deny(params string[] patterns)
    {
        AddAll(DeniedCommands, patterns, nameof(patterns));
        return this;
    }

    /// <summary>
    /// 追加一个可信来源：<b>URL 前缀</b>，如 <c>oriel://app.oriel/</c> 或 <c>http://localhost:5173/</c>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 内嵌资源的来源（<c>oriel://&lt;host&gt;/</c>）**自动**可信，不必在这里写。
    /// 这里只用来放行你自己加载的其它来源——典型场景是开发期连 Vite dev server。
    /// </para>
    /// <para>
    /// 前缀在这里**归一化**：解析为绝对 URL、自动补尾斜杠（<c>http://localhost:5173</c> →
    /// <c>http://localhost:5173/</c>）。这不是美化——可信判定是逐字节前缀匹配，缺尾斜杠时
    /// 该前缀会命中 <c>http://localhost:5173.evil.com/</c>，形似域名即可白拿整套桥接与令牌。
    /// 裸 host（漏写 scheme）、带 query/fragment 的形式直接拒绝，不让它静默变成一个
    /// 匹配不到任何真实页面 URL 的死前缀。归一化同时喂给宿主侧判定与注入脚本的
    /// <c>__ORIEL_TRUSTED__</c>（两者都消费这份列表），不会出现两侧标准不一。
    /// </para>
    /// <para>
    /// 除这里列出的来源之外，桥接脚本**根本不会安装**：远程页面里连 <c>window.oriel</c> 都不存在，
    /// 而不是"装上了再拦"。这也是为什么不需要担心"远程页面拿到令牌"。
    /// </para>
    /// </remarks>
    public OrielCapabilityOptions AllowOrigin(string urlPrefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(urlPrefix);
        AllowedOrigins.Add(NormalizeOrigin(urlPrefix));
        return this;
    }

    /// <summary>可信来源前缀的归一化：绝对 URL + 去掉 query/fragment + 保证以 <c>/</c> 结尾。</summary>
    internal static string NormalizeOrigin(string urlPrefix)
    {
        // "localhost:5173" 这种笔误会被 Uri 读成 scheme=localhost、没有 host——
        // 正是必须拒绝的那类输入，而不是当成功解析。
        if (!Uri.TryCreate(urlPrefix, UriKind.Absolute, out Uri? uri) || uri is null || uri.Host.Length == 0)
        {
            throw new ArgumentException(
                $"AllowOrigin 的「{urlPrefix}」不是可判定的绝对 URL。常见笔误是漏掉 scheme" +
                "（\"localhost:5173\" 会被读成 scheme=localhost）——请写成 \"http://localhost:5173/\" 这样的完整形式。",
                nameof(urlPrefix));
        }

        // 可信判定是 URL 前缀匹配：带 query/fragment 的"前缀"匹配不到任何真实页面 URL，
        // 留着只会让人以为配了却没生效。
        if (uri.Query.Length > 0 || uri.Fragment.Length > 0)
        {
            throw new ArgumentException(
                $"AllowOrigin 的「{urlPrefix}」不能带 query 或 fragment（可信判定是前缀匹配，" +
                "带 query 的前缀匹配不到真实页面 URL）。",
                nameof(urlPrefix));
        }

        // 前缀必须以 '/' 结尾：这是安全边界，见 AllowOrigin 的 remarks。
        string path = uri.AbsolutePath;
        if (!path.EndsWith('/'))
        {
            path += "/";
        }

        return $"{uri.Scheme}://{uri.Authority}{path}";
    }

    // "是否显式配置过"由 OrielAppBuilder 判断（Capabilities 非 null 即算），
    // 不放在这里——配置对象自己不知道它有没有被交给构建器。

    private static void AddAll(List<string> target, string[] patterns, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(patterns, parameterName);
        foreach (string pattern in patterns)
        {
            // 空模式不是"拒绝一切"而是"写错了"——静默放进名单只会让人以为配了却没生效
            if (string.IsNullOrWhiteSpace(pattern))
            {
                throw new ArgumentException("能力模式不能为空或空白。", parameterName);
            }

            target.Add(pattern);
        }
    }
}
