namespace OrielWeb;

/// <summary>
/// 路径 scope：一组带 <c>$VAR</c> 前缀的 glob 模式，判定某个具体路径能不能被访问。
/// </summary>
/// <remarks>
/// <para>
/// 对标 Tauri 2 的 fs scope（<c>{ "path": "$APPDATA/**" }</c> 那一套）。形状与语义都照它：
/// 变量名与 <c>BaseDirectory</c> 一一对应、glob 是 <c>glob</c> crate 的语义、deny 优先。
/// </para>
/// <para>
/// <b>为什么 scope 要单独一层，命令授权不够吗</b>：不够，而且差得很远。
/// <c>Allow("fs.readTextFile")</c> 只说"这个页面能调读文件这个动作"，没说"能读哪些文件"——
/// 少了后者，一条 XSS 就能把 <c>~/.ssh/id_rsa</c> 读走。命令授权解决"能不能做这件事"，
/// scope 解决"能做在什么上"，两者缺一不可。
/// </para>
/// <para>
/// <b>两者的错误要能分开</b>：命令没授权 → <c>not_allowed</c>；
/// 命令授权了但路径不在 scope 内 → <c>path_not_allowed</c>。
/// 混成一个码的话，排障时"我明明声明了 fs.readTextFile"会把人带向错的方向。
/// </para>
/// </remarks>
public sealed class OrielPathScope
{
    private readonly List<string> _allow = [];
    private readonly List<string> _deny = [];

    /// <summary>允许匹配的模式（至少命中一条才算允许）。</summary>
    public IReadOnlyList<string> Allow => _allow;

    /// <summary>拒绝匹配的模式（命中任意一条即拒绝，优先于允许）。</summary>
    public IReadOnlyList<string> Deny => _deny;

    /// <summary>加一条允许模式（如 <c>$APPDATA/**</c>）。</summary>
    public OrielPathScope AllowPath(string pattern)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        _allow.Add(pattern);
        return this;
    }

    /// <summary>加一条拒绝模式，优先于允许。</summary>
    /// <remarks>
    /// deny 优先是刻意的：Tauri 的 scope 也是这个顺序，而更实际的理由是
    /// "先写宽的 allow、再用 deny 挖掉例外"是最常见的写法——若 allow 优先，
    /// 那条 deny 等于没写，而这个错误是静默的。
    /// </remarks>
    public OrielPathScope DenyPath(string pattern)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        _deny.Add(pattern);
        return this;
    }

    /// <summary>批量加允许模式。</summary>
    public OrielPathScope AllowPaths(params string[] patterns)
    {
        foreach (string pattern in patterns)
        {
            AllowPath(pattern);
        }

        return this;
    }

    /// <summary>批量加拒绝模式。</summary>
    public OrielPathScope DenyPaths(params string[] patterns)
    {
        foreach (string pattern in patterns)
        {
            DenyPath(pattern);
        }

        return this;
    }

    /// <summary>是否一条模式都没有（= 什么都不允许）。</summary>
    internal bool IsEmpty => _allow.Count == 0 && _deny.Count == 0;

    /// <summary>
    /// 判定一个已解析的绝对路径能不能访问（单条 scope 内：先 deny 再看 allow）。
    /// </summary>
    /// <param name="absolutePath">宿主上的绝对路径（调用方负责先归一）。</param>
    /// <param name="appIdentifier">展开 <c>$APP*</c> 变量用。</param>
    /// <param name="reason">被拒时给出原因（含命中的那条模式）。</param>
    /// <returns>允许时 true。</returns>
    /// <remarks>
    /// 单条 scope 的判定。**跨条目的 deny 优先不在这里做**——那是
    /// <see cref="OrielCapabilityOptions.IsPathAllowed"/> 的职责，它要先扫一遍所有
    /// 匹配条目的 deny 再扫 allow。这里若顺手把 deny 结果返回给"任意一条允许"的聚合，
    /// 就会出现"B 条放行、A 条禁止 → 最终放行"这种反直觉结果。
    /// </remarks>
    internal bool IsMatch(string absolutePath, string appIdentifier, out string? reason)
    {
        reason = null;

        if (MatchesAnyDeny(absolutePath, appIdentifier, out string? deniedBy))
        {
            reason = $"路径落在拒绝名单里（命中「{deniedBy}」）。";
            return false;
        }

        return MatchesAnyAllow(absolutePath, appIdentifier, out reason);
    }

    /// <summary>deny 名单里有没有命中的；命中时给出那条模式。</summary>
    internal bool MatchesAnyDeny(string absolutePath, string appIdentifier, out string? pattern)
    {
        foreach (string candidate in _deny)
        {
            if (MatchesPattern(candidate, absolutePath, appIdentifier))
            {
                pattern = candidate;
                return true;
            }
        }

        pattern = null;
        return false;
    }

    /// <summary>allow 名单里有没有命中的；一条都没有时给出原因。</summary>
    internal bool MatchesAnyAllow(string absolutePath, string appIdentifier, out string? reason)
    {
        reason = null;

        if (_allow.Count == 0)
        {
            // **允许命令但没给 scope ≠ 允许所有路径**。这是最容易误解的一点：
            // 若空 scope 表示"不限制"，那 Allow("fs.readTextFile") 会变成"能读整台机器"。
            reason = "该命令没有声明任何路径 scope（空 scope 表示不允许任何路径，不是不限制）。" +
                     "请用 Allow(\"fs.readTextFile\", s => s.AllowPath(\"$APPDATA/**\")) 声明。";
            return false;
        }

        foreach (string candidate in _allow)
        {
            if (MatchesPattern(candidate, absolutePath, appIdentifier))
            {
                return true;
            }
        }

        reason = $"路径不在任何允许的模式内（已声明：{string.Join("、", _allow)}）。";
        return false;
    }

    /// <summary>展开变量并做 glob 匹配。</summary>
    private static bool MatchesPattern(string pattern, string absolutePath, string appIdentifier)
    {
        string? expanded = OrielPathVariables.Expand(pattern, appIdentifier);
        if (expanded is null)
        {
            // 变量名不认识——**保守拒绝**而不是把它当字面量。
            // 当字面量的后果是：`$APPDTA/**`（拼错了）会变成"匹配名为 $APPDTA 的目录"，
            // 于是永远不匹配、永远拒绝——那个行为其实也是拒绝，但错因完全不同；
            // 而如果某台机器上真有一个叫 `$APPDTA` 的目录，它就成了一个意外的放行。
            return false;
        }

        // 路径与模式都归一到 '/'，并压掉重复分隔符：scope 里的模式一律用 '/' 写
        // （Tauri 也如此），而调用方给的路径在 Windows 上带 '\'。
        return OrielGlob.IsMatch(Normalize(expanded), Normalize(absolutePath));
    }

    /// <summary>把路径归一成 <c>glob</c> crate 认识的形式：分隔符统一、无重复、无尾随。</summary>
    /// <remarks>
    /// 区分大小写只在 Linux 上开：Windows 与 macOS 的默认文件系统不区分大小写，
    /// 而在那两者上开区分会让"模式写 <c>$APPDATA/Logs/**</c>、实际目录叫 <c>logs</c>"
    /// 这类情况被静默拒绝——那种拒绝很难查（看起来像路径不对，实际是大小写）。
    /// </remarks>
    private static string Normalize(string path)
    {
        string result = path.Replace('\\', '/');

        while (result.Contains("//", StringComparison.Ordinal))
        {
            result = result.Replace("//", "/", StringComparison.Ordinal);
        }

        return result.Length > 1 ? result.TrimEnd('/') : result;
    }

    /// <summary>大小写是否敏感（Linux 敏感；Windows/macOS 的默认文件系统不敏感）。</summary>
    internal static bool CaseSensitive => OperatingSystem.IsLinux();
}
