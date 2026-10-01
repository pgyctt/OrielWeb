namespace OrielWeb.Ipc;

/// <summary>
/// 命令名与能力模式的匹配。**纯函数**，好让它能被逐例单测——
/// "这个命令该不该放行"一旦只能靠肉眼看，配错就会变成"静默放行了一切"。
/// </summary>
/// <remarks>
/// 支持两种模式，刻意不支持正则：
/// <list type="bullet">
///   <item><description>精确名：<c>todo.add</c> 只匹配 <c>todo.add</c>。</description></item>
///   <item><description>前缀通配：<c>todo.*</c> 匹配 <c>todo.add</c>、<c>todo.list</c>，
///     不匹配 <c>todo</c>（末尾那个 <c>.</c> 是模式的一部分）。</description></item>
///   <item><description><c>*</c> 匹配一切。</description></item>
/// </list>
/// 比较一律用 <see cref="StringComparison.Ordinal"/>：命令名是标识符，
/// 按文化规则比较会让 <c>TODO.add</c> 在某些区域下意外命中，而那正是安全判据里不能有的行为。
/// </remarks>
internal static class OrielCapabilityMatch
{
    /// <summary>匹配一切的模式。</summary>
    internal const string MatchAll = "*";

    /// <summary>前缀通配的后缀：<c>todo.*</c>。</summary>
    internal const string PrefixSuffix = ".*";

    /// <summary>判定模式是否为"前缀通配"（以 <c>.*</c> 结尾）。</summary>
    internal static bool IsPrefixPattern(string pattern)
        => pattern.Length > PrefixSuffix.Length
           && pattern.EndsWith(PrefixSuffix, StringComparison.Ordinal);

    /// <summary>模式是否匹配命令名。</summary>
    internal static bool Matches(string pattern, string commandName)
    {
        if (string.IsNullOrEmpty(pattern) || string.IsNullOrEmpty(commandName))
        {
            return false;
        }

        if (string.Equals(pattern, MatchAll, StringComparison.Ordinal))
        {
            return true;
        }

        if (IsPrefixPattern(pattern))
        {
            // 去掉末尾的 '*'，留下的 'todo.' 就是前缀（末尾的 '.' 让它不会误匹配 'todo' 本身）
            return commandName.StartsWith(pattern[..^1], StringComparison.Ordinal);
        }

        return string.Equals(pattern, commandName, StringComparison.Ordinal);
    }

    /// <summary>
    /// 在一组模式里找第一个匹配的（顺序无关，命中即返回）。
    /// </summary>
    internal static bool AnyMatches(IReadOnlyList<string> patterns, string commandName)
    {
        foreach (string pattern in patterns)
        {
            if (Matches(pattern, commandName))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// 能力判定的规则层（纯函数）。
/// </summary>
internal static class OrielCapabilityRules
{
    /// <summary>
    /// 库保留的命令前缀：无边框窗口的标题栏按钮（最小化 / 最大化 / 关闭 / 上下文菜单 / 拖动）
    /// 走的就是这一组，应用侧的页面代码与库文档都按 <c>win.*</c> 引用它们。
    /// </summary>
    /// <remarks>
    /// <b>这一组始终放行，不受能力配置约束。</b>理由：它们只操作"自己那个窗口"，
    /// 不触及文件系统、注册表、网络或外部程序，因此不是"能力"——
    /// 而 Release 下未配置即拒绝这条规则若把它们一起拒掉，
    /// 无边框窗口会直接变成"关不掉的窗口"（自绘标题栏的按钮全废，又没有系统标题栏可替代）。
    ///
    /// 代价是 <c>win.</c> 成了保留前缀：应用自己的命令不要用这个前缀开头，
    /// 否则会绕过能力配置。库不阻止你这么做（那是应用自己的选择），但文档里写明了。
    /// </remarks>
    internal const string BuiltInCommandPrefix = "win.";

    internal static bool IsBuiltInCommand(string commandName)
        => !string.IsNullOrEmpty(commandName)
           && commandName.StartsWith(BuiltInCommandPrefix, StringComparison.Ordinal);

    /// <summary>
    /// 按 allow / deny 两组模式判定一个命令名。
    /// </summary>
    /// <remarks>
    /// <b>deny 优先</b>：两条都命中时按拒绝算。这不是偏好——
    /// "先写宽泛的 allow、再用 deny 挖掉个别例外"是最常见的写法，
    /// 若 allow 优先，那条 deny 就等于没写，而这个错误是静默的。
    /// 两条都不命中则拒绝（deny-by-default：不在名单上就是没有这个能力）。
    /// </remarks>
    internal static bool IsAllowed(string commandName, IReadOnlyList<string> allow, IReadOnlyList<string> deny)
    {
        if (OrielCapabilityMatch.AnyMatches(deny, commandName))
        {
            return false;
        }

        return OrielCapabilityMatch.AnyMatches(allow, commandName);
    }
}
