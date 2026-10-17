using OrielWeb.Ipc;

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

    /// <summary>
    /// 命令模式 → 路径 scope（对标 Tauri 的"权限条目自带 scope"）。
    /// </summary>
    /// <remarks>
    /// 一个模式可以有多条 scope 条目（多次 <see cref="Allow(string, Action{OrielPathScope})"/>），
    /// 判定时"任意一条允许 且 没有任何一条拒绝"。
    /// </remarks>
    internal List<(string Pattern, OrielPathScope Scope)> PathScopes { get; } = [];

    /// <summary>允许页面调用这些命令（精确名或 <c>前缀.*</c>；<c>*</c> 表示全部）。可多次调用。</summary>
    public OrielCapabilityOptions Allow(params string[] patterns)
    {
        AddAll(AllowedCommands, patterns, nameof(patterns));
        return this;
    }

    /// <summary>
    /// 允许这些命令，并给它们声明**路径 scope**（仅对 <c>fs.*</c> 这类要碰路径的命令有意义）。
    /// </summary>
    /// <param name="pattern">命令模式（精确名或 <c>前缀.*</c>）。</param>
    /// <param name="configure">配置 scope：<c>s =&gt; s.AllowPath("$APPDATA/**")</c>。</param>
    /// <example>
    /// <code>
    /// c.Allow("fs.readTextFile",  s => s.AllowPath("$APPDATA/**"))
    ///  .Allow("fs.writeTextFile", s => s.AllowPath("$APPDATA/**").DenyPath("$APPDATA/secrets/**"));
    /// </code>
    /// </example>
    /// <remarks>
    /// <para>
    /// <b>只允许命令、不给 scope，等于什么都不允许</b>——不是"不限制"。
    /// 这一条是本层最容易误解的地方，所以它有一条专门的错误文案
    /// （见 <see cref="OrielPathScope.IsMatch"/>）。
    /// 反过来若空 scope 表示"不限制"，那 <c>Allow("fs.readTextFile")</c> 就成了"能读整台机器"。
    /// </para>
    /// <para>
    /// 形状照 Tauri：那边每个权限条目自带 <c>allow</c> / <c>deny</c> 两个路径数组，
    /// 所以"读"与"写"可以各自 scope（读整个数据目录、写只到子目录）。
    /// </para>
    /// </remarks>
    public OrielCapabilityOptions Allow(string pattern, Action<OrielPathScope> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        ArgumentNullException.ThrowIfNull(configure);

        // 命令本身也要进 allow 名单：scope 是**附加**约束，不是授权本身。
        // 少了这一步的后果是"声明了 scope 却仍然被拒"，而错误文案会说"命令没授权"——
        // 那会把人引向完全错的方向。
        AllowedCommands.Add(pattern);

        var scope = new OrielPathScope();
        configure(scope);

        if (scope.IsEmpty)
        {
            throw new ArgumentException(
                $"Allow(\"{pattern}\", …) 的 scope 是空的：空 scope 表示不允许任何路径，" +
                $"那与不声明 scope 没有区别。要么用 AllowPath 声明允许的路径，" +
                $"要么用普通的 Allow(\"{pattern}\") 只授权命令（那样所有路径都会被拒）。",
                nameof(configure));
        }

        PathScopes.Add((pattern, scope));
        return this;
    }

    /// <summary>
    /// 判定某个路径能不能被某条命令访问。
    /// </summary>
    /// <param name="commandName">命令名（如 <c>fs.readTextFile</c>）。</param>
    /// <param name="absolutePath">宿主上的绝对路径。</param>
    /// <param name="appIdentifier">展开 <c>$APP*</c> 变量用。</param>
    /// <param name="reason">被拒时给出原因。</param>
    /// <remarks>
    /// 命中多条 scope 时：**任意一条允许 且 没有任何一条拒绝**。
    /// 拒绝跨条目生效是刻意的——写了两条 scope、只在其中一条加 deny 时，
    /// 另一条不该绕过它（那正是"先写宽的、再挖例外"这种写法的常见翻车点）。
    /// </remarks>
    internal bool IsPathAllowed(string commandName, string absolutePath, string appIdentifier, out string? reason)
    {
        reason = null;

        // 两遍扫描，**先 deny 再 allow**：
        //
        // 单遍扫描（逐条 "IsMatch 成功就放行"）会让"先写宽的、再挖例外"这种写法失效——
        // 宽的那条放行了，窄的那条里的 deny 就白写了。而那种写法恰恰是最常见的。
        // 所以 deny 必须跨条目先生效。
        var matched = new List<OrielPathScope>();

        foreach ((string pattern, OrielPathScope scope) in PathScopes)
        {
            if (OrielCapabilityRules.IsAllowed(commandName, [pattern], []))
            {
                matched.Add(scope);
            }
        }

        if (matched.Count == 0)
        {
            reason = "该命令没有声明任何路径 scope（空 scope 表示不允许任何路径，不是不限制）。" +
                     $"请用 Allow(\"{commandName}\", s => s.AllowPath(\"$APPDATA/**\")) 声明。";
            return false;
        }

        // 第一遍：任一命中的 scope 里有 deny 命中 → 拒绝
        foreach (OrielPathScope scope in matched)
        {
            if (scope.MatchesAnyDeny(absolutePath, appIdentifier, out string? deniedBy))
            {
                reason = $"路径落在拒绝名单里（命中「{deniedBy}」）。";
                return false;
            }
        }

        // 第二遍：任一命中的 scope 里有 allow 命中 → 允许
        string? allowReason = null;
        foreach (OrielPathScope scope in matched)
        {
            if (scope.MatchesAnyAllow(absolutePath, appIdentifier, out string? scopeReason))
            {
                return true;
            }

            allowReason ??= scopeReason;
        }

        reason = allowReason;
        return false;
    }

    /// <summary>拒绝页面调用这些命令（<b>优先于授权</b>）。可多次调用。</summary>
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
