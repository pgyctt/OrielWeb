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
        // 只读状态查询：自绘标题栏靠它们渲染按钮的当前状态与"窗口是否激活"的配色。
        // 与上面两条同一理由——只报告自己窗口的状态，不泄露跨窗口信息。
        "window.isMinimized",
        "window.isFocused",
        "window.isFullscreen",
        "window.isAlwaysOnTop",
        // 又四条：标题栏靠它们决定"要不要显示自绘标题栏"（decorated）与
        // "哪个按钮该出现"（maximizable / minimizable / closable），theme 决定配色。
        "window.isDecorated",
        "window.isResizable",
        "window.isMaximizable",
        "window.isMinimizable",
        "window.isClosable",
        "window.isEnabled",
        "window.theme",
        "window.title",
        // 显示器查询：屏幕布局与光标位置是页面**本来就能感知**的环境信息
        // （浏览器给得出当前屏尺寸与缩放），而且自绘标题栏要按"拖到哪块屏"换配色/图标。
        // 它们不读任何窗口内容，所以与上面那组只读查询同一姿态。
        "window.currentMonitor",
        "window.primaryMonitor",
        "window.availableMonitors",
        "window.monitorFromPoint",
        "window.cursorPosition",
    ];

    /// <summary>
    /// 始终放行的 <c>path.*</c> 命令（整族）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么整族放行</b>，而 <c>window.*</c> 只放行一小撮：本族的每个命令都只对
    /// **页面自己传进来的字符串**做运算，不读宿主状态、不写任何东西、不涉及别的窗口。
    /// 换句话说，放行 <c>path.join</c> 不会让页面多做到任何一件它本来做不到的事
    /// （它自己拼字符串也能得到同样的结果，只是可能拼错）。
    /// </para>
    /// <para>
    /// 这条判断的边界要说清楚：
    /// </para>
    /// <list type="bullet">
    ///   <item><description><c>normalize</c> / <c>resolve</c> 会**拼上当前工作目录**，
    ///     所以严格说泄露了一点宿主状态（cwd）。但那既不是秘密（同进程的应用代码本来就
    ///     知道自己在哪），也远轻于我们已经无条件放行的 <c>window.destroy</c>——
    ///     对 cwd 斤斤计较而放行销毁窗口是不自洽的。</description></item>
    ///   <item><description>真正需要 I/O 的路径能力（<c>fs</c> 模块的读写、<c>convertFileSrc</c>）
    ///     **不在本族**，它们照旧走 scope + 能力声明。这一条是刻意的：
    ///     本族之所以能整族放行，前提就是它不做 I/O。</description></item>
    /// </list>
    /// <para>
    /// 对齐口径：Tauri 的路径命令属于 <c>core:path:default</c>，**默认开启**。
    /// 若这里要求逐条声明，应用的实际反应会是 <c>Allow("path.*")</c>——
    /// 那和现在这样整族放行没有区别，只是多了一层容易写错的配置。
    /// </para>
    /// </remarks>
    private static readonly string[] AlwaysAllowedPathCommands =
    [
        "path.join",
        "path.dirname",
        "path.extname",
        "path.basename",
        "path.isAbsolute",
        "path.normalize",
        "path.resolve",

        // 目录解析（对标 Tauri 的 appDataDir / homeDir / … 那一整组）。
        //
        // 也是"不做 I/O、不写任何东西"的：读的是环境变量与操作系统的已知文件夹。
        // 严格说它泄露了一点宿主信息（用户名会出现在 homeDir 里），但那与 path.normalize
        // 泄露 cwd 是同一量级，而我们在上面已经论证过那个可以放行。
        //
        // 与 path.join 的差别只是"值从哪来"——从参数变成从宿主环境读。既然
        // Tauri 的 core:path:default 默认开着这一整组，这里也照做。
        "path.resolveResource",
        .. DirectoryCommandNames(),
    ];

    /// <summary>
    /// 目录解析的命令名：由目录清单派生，**不手抄**。
    /// </summary>
    /// <remarks>
    /// 手抄两份清单的失败模式是"加了目录忘了加放行"——症状为某个 <c>path.xxxDir</c>
    /// 在 Release 下被拒，而那看起来像"这个目录不存在"。派生就没有这个缝。
    /// 命名规则与 <c>OrielBuiltInPathCommands.TryGet(string, string?, out …)</c> 用的是同一条
    /// （目录名首字母小写 + <c>Dir</c> 后缀）。
    /// </remarks>
    private static string[] DirectoryCommandNames()
    {
        var names = new string[OrielKnownDirs.AllDirectories.Length];

        for (int i = 0; i < names.Length; i++)
        {
            string directory = OrielKnownDirs.AllDirectories[i];
            names[i] = $"path.{char.ToLowerInvariant(directory[0])}{directory[1..]}Dir";
        }

        return names;
    }

    /// <summary>判断某个 <c>path.*</c> 命令是否属于"始终放行"的一族。</summary>
    internal static bool IsAlwaysAllowedPathCommand(string name)
    {
        foreach (string allowed in AlwaysAllowedPathCommands)
        {
            if (string.Equals(allowed, name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 始终放行的 <c>app.*</c> 命令（只读元信息）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 放行的理由与 <c>path.*</c> 同类：这几条读的都是**宿主本来就会告诉页面**的东西。
    /// 页面已经知道自己的来源 URL、窗口 label、平台标识；再加上应用名/版本/标识符，
    /// 没有任何一条是"应用不该知道"的信息。
    /// </para>
    /// <para>
    /// 反过来说，<b>有状态的那几条刻意不在本族</b>：<c>exit</c> / <c>show</c> / <c>hide</c>
    /// 会改变应用状态，它们是能力而不是元信息。Tauri 把两者放在同一个 <c>app</c> 模块里，
    /// 但门禁是按命令名判的，所以在这一点上分得开——将来加它们时各自单独判断。
    /// </para>
    /// <para>
    /// <b>2026-10-19 起这三条已经实现</b>（<c>app.exit</c> / <c>app.show</c> / <c>app.hide</c>），
    /// 并且**仍然不在下面这个名单里**：页面能退出应用、能隐藏主窗口，这是需要应用点头的能力，
    /// 不该因为"名字在 app 族里"就默认拥有。应用要放行得显式写：
    /// <c>Allow("app.exit")</c> / <c>Allow("app.*")</c>（按需）。Debug 构建全放行，不受影响。
    /// </para>
    /// </remarks>
    private static readonly string[] AlwaysAllowedAppCommands =
    [
        "app.getName",
        "app.getVersion",
        "app.getIdentifier",
        "app.getTauriVersion",
        "app.supportsMultipleWindows",
    ];

    /// <summary>判断某个 <c>app.*</c> 命令是否属于"始终放行"的那几条只读元信息。</summary>
    internal static bool IsAlwaysAllowedAppCommand(string name)
    {
        foreach (string allowed in AlwaysAllowedAppCommands)
        {
            if (string.Equals(allowed, name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 始终放行的 <c>menu.*</c> 命令。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>不按 <c>menu.</c> 前缀整族放行</b>：这一族里的命令作用面差得远，按前缀放行等于
    /// 把作用面最大的那条一并开了。目前分成两档：
    /// </para>
    /// <list type="bullet">
    ///   <item><description><b>无条件放行</b>：<c>menu.popup</c> / <c>menu.close</c> /
    ///     <c>menu.setAsWindowMenu</c> —— 三条都只影响<b>发起调用的那个窗口</b>。</description></item>
    ///   <item><description><b>要应用点头</b>：<c>menu.setAsAppMenu</c> —— 它作用到所有没显式
    ///     设过菜单栏的窗口（含此后新建的），是跨窗口的。</description></item>
    /// </list>
    /// <para>
    /// <b>为什么 <c>setAsWindowMenu</c> 与 <c>popup</c> 同档</b>，尽管它比弹出更"持久"：
    /// 能力面要看"页面因此能做到什么本来做不到的事"，而不是看效果持续多久。菜单栏与弹出
    /// 装的是**同一份东西**（同一套 <c>OrielMenuItem</c>、同一个解析器、同样的 role），
    /// 页面给弹出菜单塞一个 <c>Quit</c> role 与给菜单栏塞一个是等价的——所以持久与否
    /// 不构成新的能力，只是把已有的能力摆在那里更久。
    /// </para>
    /// <para>
    /// <b>为什么 <c>setAsAppMenu</c> 单独一档</b>：它是唯一一条**越过调用方窗口**的命令。
    /// 一个不受信任的页面装上应用菜单，等于在别的窗口顶部放了一条看起来像宿主自己的菜单栏
    /// （"文件 → 退出"点下去可能是别的事）。这与 <c>app.exit</c> / <c>app.show</c> 同一类
    /// ——见 <see cref="AlwaysAllowedAppCommands"/> 的说明：改变应用状态、作用面超出自己
    /// 窗口的能力，要应用显式点头。要放行得写 <c>Allow("menu.setAsAppMenu")</c>；
    /// Debug 构建全放行，不受影响。
    /// </para>
    /// </remarks>
    internal static readonly string[] AlwaysAllowedMenuCommands =
    [
        "menu.popup",
        "menu.close",
        "menu.setAsWindowMenu",
    ];

    /// <summary>判断某个 <c>menu.*</c> 命令是否属于"始终放行"的那几条。</summary>
    internal static bool IsAlwaysAllowedMenuCommand(string name)
    {
        foreach (string allowed in AlwaysAllowedMenuCommands)
        {
            if (string.Equals(allowed, name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

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
