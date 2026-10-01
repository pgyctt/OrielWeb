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
    /// 追加一个可信来源：<b>URL 前缀</b>，如 <c>https://app.oriel/</c> 或 <c>http://localhost:5173/</c>。
    /// </summary>
    /// <remarks>
    /// 内嵌资源的来源（<c>https://&lt;host&gt;/</c>，以及 Linux/macOS 上被改写的
    /// <c>file://&lt;解压目录&gt;/</c>）**自动**可信，不必在这里写。
    /// 这里只用来放行你自己加载的其它来源——典型场景是开发期连 Vite dev server。
    ///
    /// 除这里列出的来源之外，桥接脚本**根本不会安装**：远程页面里连 <c>window.oriel</c> 都不存在，
    /// 而不是"装上了再拦"。这也是为什么不需要担心"远程页面拿到令牌"。
    /// </remarks>
    public OrielCapabilityOptions AllowOrigin(string urlPrefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(urlPrefix);
        AllowedOrigins.Add(urlPrefix);
        return this;
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
