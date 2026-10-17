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
    /// 走 <c>win.*</c>，托盘操作走 <c>tray.*</c>。应用侧的页面代码与库文档都按这两组引用它们。
    /// </summary>
    /// <remarks>
    /// <b>这两组始终放行，不受能力配置约束。</b>理由：它们只操作"本应用自己的窗口与托盘"，
    /// 不触及文件系统、注册表、网络或外部程序，因此不是"能力"——
    /// 而 Release 下未配置即拒绝这条规则若把它们一起拒掉，
    /// 无边框窗口会直接变成"关不掉的窗口"（自绘标题栏的按钮全废，又没有系统标题栏可替代）。
    ///
    /// 代价是 <c>win.</c> 与 <c>tray.</c> 成了保留前缀：应用自己的命令不要用这两个前缀开头，
    /// 否则会绕过能力配置。库不阻止你这么做（那是应用自己的选择），但文档里写明了。
    /// </remarks>
    internal const string BuiltInCommandPrefix = "win.";

    /// <summary>托盘的库保留前缀（见 <see cref="BuiltInCommandPrefix"/> 的说明）。</summary>
    internal const string BuiltInTrayCommandPrefix = "tray.";

    internal static bool IsBuiltInCommand(string commandName)
    {
        if (string.IsNullOrEmpty(commandName))
        {
            return false;
        }

        return commandName.StartsWith(BuiltInCommandPrefix, StringComparison.Ordinal)
            || commandName.StartsWith(BuiltInTrayCommandPrefix, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>window.*</c> 里的标题栏必需命令：**即使能力没声明也放行**。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么 <c>window.*</c> 不像 <c>win.*</c> 那样整族放行</b>：<c>win.*</c> 是早期的库内建命令，
    /// 当时只有"标题栏那几个按钮"；而 <c>window.*</c> 是为对齐 Tauri 2 新建的族，
    /// 里面有 <c>window.setSkipTaskbar</c>、<c>window.setIcon</c> 这类真正的能力。
    /// 整族放行等于给一个比 <c>win.*</c> 大得多的能力面开后门。
    /// </para>
    /// <para>
    /// 但**完全不放行也不行**：<c>win.*</c> 后门存在的理由（无边框窗口没有系统标题栏，
    /// 拒掉关闭按钮会变成关不掉的窗口）在 <c>window.*</c> 上同样成立——迁移到新族的应用
    /// 会用 <c>window.close</c> 而不是 <c>win.close</c>。所以只留**那一组**。
    /// </para>
    /// <para>
    /// 名单刻意写死而不是"按前缀放行"：前缀放行等于放行整族，就退回了 <c>win.*</c> 的老问题。
    /// 每加一条都要先问"拒绝它会不会让窗口变得不可用"。
    /// </para>
    /// </remarks>
    internal static bool IsAlwaysAllowedWindowCommand(string commandName)
    {
        if (string.IsNullOrEmpty(commandName))
        {
            return false;
        }

        foreach (string allowed in AlwaysAllowedWindowCommands)
        {
            if (string.Equals(commandName, allowed, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>见 <see cref="IsAlwaysAllowedWindowCommand"/>。</summary>
    /// <remarks>
    /// 逐条理由：
    /// <list type="bullet">
    ///   <item><description><c>minimize</c> / <c>toggleMaximize</c> / <c>toggleFullscreen</c> /
    ///     <c>toggleOnTop</c> / <c>close</c> / <c>destroy</c>：标题栏按钮本身。
    ///     <c>destroy</c> 必须在——它同时是"关闭接管"里页面同意关闭的出口，
    ///     拒掉它会让确认后的关闭永远走不完。</description></item>
    ///   <item><description><c>drag*</c>：无边框窗口的拖动，没有它窗口根本挪不动。</description></item>
    ///   <item><description><c>pickFile</c> / <c>contextMenu</c>：与 <c>win.*</c> 侧同源的两个入口。
    ///     保留是为了不制造"同一个动作在新旧族里权限不同"的意外。</description></item>
    ///   <item><description><c>isVisible</c> / <c>isMaximized</c> / <c>title</c>：**只读查询自己窗口的状态**，
    ///     不泄露任何跨窗口信息，而自绘标题栏要靠它渲染图标与文字。
    ///     这几条是对计划里那次安全红线的**有意扩展**（计划只列了写操作）——理由写在这里，
    ///     以免日后被当成"漏配了 deny"。</description></item>
    /// </list>
    /// </remarks>
    internal static readonly string[] AlwaysAllowedWindowCommands =
    [
        "window.minimize",
        "window.toggleMaximize",
        "window.toggleFullscreen",
        "window.toggleOnTop",
        "window.close",
        "window.destroy",
        "window.drag",
        "window.dragStart",
        "window.dragTo",
        "window.dragEnd",
        "window.pickFile",
        "window.contextMenu",
        "window.isVisible",
        "window.isMaximized",
        "window.title",
    ];

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
