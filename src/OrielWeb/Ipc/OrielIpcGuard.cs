using System.Diagnostics;
using System.Text.Json;

namespace OrielWeb.Ipc;

/// <summary>入站 / 出站消息的协议字段名（桥接脚本与 C# 侧共用，改一处即可）。</summary>
internal static class OrielIpcProtocol
{
    internal const string TokenField = "token";

    internal const string KindField = "__oriel";
}

/// <summary>
/// 入站 IPC 的门禁：来源、令牌、命令授权三层。
/// </summary>
/// <remarks>
/// 三层各管一件事，缺任何一层都有绕过的路：
/// <list type="number">
///   <item><description><b>来源</b>：<c>IsTrustedUrl</c>。只有内嵌资源（与显式放行的来源）
///     算可信；远程页面根本拿不到桥接脚本（脚本自己会先退出），所以那不是"装上了再拦"。</description></item>
///   <item><description><b>令牌</b>：<c>TokenMatches</c>。每个应用实例一个随机串，随桥接脚本注入页面，
///     每条入站消息回带。挡的是"不是本应用注入的脚本也往消息通道里塞东西"。</description></item>
///   <item><description><b>命令授权</b>：<c>TryAuthorize</c>。按能力配置判定这个命令名
///     能不能被调用（只管 invoke；单向消息没有命令名，由前两层覆盖）。</description></item>
/// </list>
/// </remarks>
internal sealed class OrielIpcGuard
{
    private readonly OrielCapabilityOptions? _options;
    private readonly bool _isDebugBuild;
    private readonly List<string> _trustedPrefixes = [];

    /// <param name="options">能力配置；null 表示应用没有调用 UseCapabilities。</param>
    /// <param name="isDebugBuild">消费方是否为 Debug 构建（见 <see cref="OrielBuildConfiguration"/>）。</param>
    /// <param name="assetHost">内嵌资源的 host（<c>UseEmbeddedAssets</c> 的 host 参数）。</param>
    internal OrielIpcGuard(OrielCapabilityOptions? options, bool isDebugBuild, string assetHost)
    {
        _options = options;
        _isDebugBuild = isDebugBuild;
        Token = OrielIpcToken.Generate();

        // 内嵌资源自动可信：页面本来就是应用自己的那一份。
        // 三平台的来源都是自定义 scheme oriel://<host>/（见 Assets/AssetUrl.cs）——
        // 以前是"Windows 的 https 虚拟主机 + Linux/macOS 解压目录的 file:// 前缀"两条，
        // 现在一条。前缀用 AssetUrl 生成，避免手拼与页面实际 URL 出现逐字节差异
        // （2026-10-02 的 macOS 冒烟就是被"空格 vs %20"这种差异搞红的）。
        AddTrustedPrefix(AssetUrl.TrustedPrefix(assetHost));

        if (options is { } configured)
        {
            foreach (string origin in configured.AllowedOrigins)
            {
                AddTrustedPrefix(origin);
            }
        }
    }

    /// <summary>本次进程运行的 IPC 令牌（随桥接脚本注入页面）。</summary>
    internal string Token { get; }

    /// <summary>可信来源的 URL 前缀（注入桥接脚本时一并写进去，让不可信文档不安装桥接）。</summary>
    internal IReadOnlyList<string> TrustedPrefixes => _trustedPrefixes;

    /// <summary>是否已显式配置过能力。</summary>
    internal bool IsConfigured => _options is not null;

    /// <summary>
    /// 判定某个 <c>fs.*</c> 命令能不能访问某个具体路径。
    /// </summary>
    /// <param name="commandName">命令名（如 <c>fs.readTextFile</c>）。</param>
    /// <param name="absolutePath">**已经归一**的绝对路径。</param>
    /// <param name="appIdentifier">展开 <c>$APP*</c> 变量用。</param>
    /// <param name="reason">被拒时给出原因。</param>
    /// <remarks>
    /// <para>
    /// <b>没配能力（<see cref="_options"/> 为 null）时的姿态与命令授权一致</b>：
    /// Debug 构建放行、Release 拒绝。不这样做的话会出现"命令被拒但路径放行"或反过来的
    /// 不一致——同一个配置项在两处的解释不同是最难解释的一类问题。
    /// </para>
    /// <para>
    /// 调用方**必须**先归一路径（解析掉 <c>..</c>）再进来：否则
    /// <c>$APPDATA/../../etc/passwd</c> 这类会绕过 scope。那条归一在
    /// <c>OrielBuiltInFsCommands</c> 里做，不在这里——因为这里拿不到"相对谁解析"的上下文。
    /// </para>
    /// </remarks>
    internal bool TryAuthorizePath(string commandName, string absolutePath, string appIdentifier, out string? reason)
    {
        if (_options is null)
        {
            if (_isDebugBuild)
            {
                reason = null;
                return true;
            }

            reason = "未配置能力（Release 构建默认拒绝一切文件访问）。" +
                     "请在应用入口调用 UseCapabilities(...) 并声明 fs 命令的路径 scope。";
            return false;
        }

        return _options.IsPathAllowed(commandName, absolutePath, appIdentifier, out reason);
    }

    /// <summary>
    /// 应用有没有为某条命令声明过路径 scope（**不管放行与否**）。
    /// </summary>
    /// <param name="commandName">命令名。</param>
    /// <remarks>
    /// <para>
    /// 存在的理由是一个语义区分：<see cref="TryAuthorizePath"/> 在"没有声明"时也返回
    /// <c>false</c>（空 scope = 不允许任何路径），所以调用方无法用它区分
    /// "声明了但这条路径不在内"与"压根没声明"。
    /// </para>
    /// <para>
    /// 而有些命令需要后者的答案：<c>clipboard.writeImage</c> 的路径 scope 是
    /// **声明了才强制**的（理由见该命令的 remarks）——它要在"没声明"时维持既有行为
    /// 并告警，而不是把既有配置直接判死。
    /// </para>
    /// <para>
    /// 没配能力（<c>_options</c> 为 null）时返回 <c>false</c>：那时没有 scope 可言。
    /// </para>
    /// </remarks>
    internal bool HasPathScopeFor(string commandName)
        => _options?.HasPathScopeFor(commandName) ?? false;

    /// <summary>追加一个可信来源（URL 前缀）。<c>UseCapabilities(AllowOrigin)</c> 放行的开发期来源由此进来。</summary>
    internal void AddTrustedPrefix(string prefix)
    {
        if (!string.IsNullOrEmpty(prefix))
        {
            _trustedPrefixes.Add(prefix);
        }
    }

    /// <summary>该 URL 是否来自可信来源（逐字节前缀匹配）。</summary>
    /// <remarks>
    /// 逐消息来源可能是不带路径的 **origin**（macOS 的 <c>frameInfo.securityOrigin</c>）：
    /// 由调用方补上 <c>/</c> 后走进这同一条判定（authority 命中即可信，子路径级前缀保守拒绝），
    /// 见 <c>MacOSWebMessageHandler.OriginUrl</c>。刻意只留这一条判定——单一入口胜过
    /// "origin 一版、URL 一版"两套并行实现（2026-10-08 审核发现 3：此前的
    /// <c>IsTrustedOrigin</c> 因此没有任何生产调用方）。
    /// </remarks>
    internal bool IsTrustedUrl(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return false;
        }

        foreach (string prefix in _trustedPrefixes)
        {
            if (url.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>页面回带的令牌是否与本次进程的令牌一致。</summary>
    internal bool TokenMatches(string? presented) => OrielIpcToken.Equals(Token, presented);

    /// <summary>
    /// 判定一个命令名能否被页面调用。
    /// </summary>
    /// <param name="commandName">命令名。</param>
    /// <param name="args">命令参数：放行集要读其中的 <c>label</c> 才能知道"目标是不是发起者自己"。</param>
    /// <param name="sender">发起这次调用的窗口（没有来源窗口时传 null）。</param>
    /// <param name="denialReason">被拒绝时的**可排查**理由（放行时为 null）。</param>
    /// <remarks>
    /// <para>
    /// <b>放行集的边界 = 发起窗口。</b> 放行名单里的理由全是"只操作自己那个窗口"，
    /// 而 <c>window.*</c> / <c>menu.*</c> 支持按 <c>label</c> 寻址任意已注册窗口——
    /// 只看命令名就放行，等于让零配置的 Release 应用就能销毁、关闭、改标题到别的窗口上，
    /// 或者给他窗装一个含 <c>Quit</c> role 的菜单（那绕过了 <c>app.exit</c> 的显式授权）。
    /// 所以 <c>label</c> 指向别人的命令**不走放行分支**，落入正常能力判定（deny 优先照旧）。
    /// </para>
    /// <para>
    /// <b>缺省 <c>label</c> 与"自己的 <c>label</c>"行为不变</b>：自绘标题栏的按钮不带 label，
    /// 那是零配置可用的前提（拒掉会造出"关不掉的窗口"，见
    /// <see cref="OrielCapabilityRules.AlwaysAllowedWindowCommands"/> 的红线记录）。
    /// </para>
    /// </remarks>
    internal bool TryAuthorize(
        string commandName, JsonElement args, IOrielWindowControl? sender, out string? denialReason)
    {
        // 目标不是发起者自己时，下面这些"只影响自己窗口"的放行理由全部不成立。
        bool targetsAnotherWindow = TargetsAnotherWindow(args, sender);

        // 内建窗口命令（win.* / tray.*）始终放行：它们只操作自己那个窗口与本应用的托盘，
        // 不是"能力"。拒掉它们会让无边框窗口连关闭按钮都失效（自绘标题栏，没有系统标题栏可替代）。
        // 这一族**不接受 label 寻址**（操作对象由来源窗口或托盘 id 决定），故不受上面的判定影响。
        if (OrielCapabilityRules.IsBuiltInCommand(commandName))
        {
            denialReason = null;
            return true;
        }

        // window.* 族里的标题栏必需命令同样放行，但**只在目标是发起者自己时**
        // （逐条理由见 IsAlwaysAllowedWindowCommand）。
        // 这一族**不整族放行**：它比 win.* 大得多，里面有真正的能力。
        if (!targetsAnotherWindow && OrielCapabilityRules.IsAlwaysAllowedWindowCommand(commandName))
        {
            denialReason = null;
            return true;
        }

        // menu.* 里只放行"只影响调用方自己窗口"的那几条，同样限自身 label
        // （逐条理由见 IsAlwaysAllowedMenuCommand）。
        // 不整族放行的理由：setAsAppMenu 跨窗口（套用到所有没显式设过菜单栏的窗口），
        // 那是另一个能力面，要应用显式点头。
        if (!targetsAnotherWindow && OrielCapabilityRules.IsAlwaysAllowedMenuCommand(commandName))
        {
            denialReason = null;
            return true;
        }

        // path.* 族**整族放行**（逐条理由见 IsAlwaysAllowedPathCommand）。
        // 它之所以能整族放行、而 window.* 不能：本族只对页面自己给的字符串做运算，
        // 不做 I/O、不读宿主状态、不涉及别的窗口。
        if (OrielCapabilityRules.IsAlwaysAllowedPathCommand(commandName))
        {
            denialReason = null;
            return true;
        }

        // app.* 里那几条只读元信息同样整族放行（逐条理由见 IsAlwaysAllowedAppCommand）。
        // 注意放行的是**列举出来的那几条**，不是 app.* 前缀——将来加 exit/show/hide 时
        // 它们不在名单里，会照旧走能力校验。
        if (OrielCapabilityRules.IsAlwaysAllowedAppCommand(commandName))
        {
            denialReason = null;
            return true;
        }

        if (_options is null)
        {
            if (_isDebugBuild)
            {
                denialReason = null;
                return true;
            }

            denialReason =
                $"命令 '{commandName}' 被拒绝：Release 构建下没有声明能力时一律拒绝（fail-closed）。" +
                "请调用 UseCapabilities 声明页面需要用到的命令。";
            return false;
        }

        if (OrielCapabilityRules.IsAllowed(
                commandName, _options.AllowedCommands, _options.DeniedCommands))
        {
            denialReason = null;
            return true;
        }

        bool denied = OrielCapabilityMatch.AnyMatches(_options.DeniedCommands, commandName);
        denialReason = denied
            ? $"命令 '{commandName}' 落进了 Deny 名单（deny 优先于 allow）。"
            : targetsAnotherWindow
                ? $"命令 '{commandName}' 的目标是另一个窗口（label 与发起者不一致）：" +
                  "「始终放行」只覆盖发起者自己的窗口，跨窗口操作需要显式 " +
                  $"Allow(\"{commandName}\") 授权。"
                : $"命令 '{commandName}' 不在 Allow 名单里（deny-by-default：没声明就是没有这个能力）。";
        return false;
    }

    /// <summary>
    /// 命令参数里的 <c>label</c> 是不是指向**发起者以外**的窗口。
    /// </summary>
    /// <param name="args">命令参数。</param>
    /// <param name="sender">发起调用的窗口；身份未知（后端未就位）时为 null。</param>
    /// <remarks>
    /// 三种情况都算"不是自己"：给了别人的 label；给了 label 但发起者身份未知
    /// （无从证明那是自己）；发起者身份未知却给了 label。
    /// 缺省 label 才是"自己"——那是自绘标题栏的写法，也是本判定唯一放行的形态。
    /// 复用 <see cref="OrielCommandTarget.OptionalText"/> 读参数，保证"label 缺省 = 发起者"
    /// 这条规则在授权层与执行层是同一套（两处各写一份的话，收紧的那一边迟早会漂移）。
    /// </remarks>
    private static bool TargetsAnotherWindow(JsonElement args, IOrielWindowControl? sender)
    {
        string? label = OrielCommandTarget.OptionalText(args, "label");
        if (label is null)
        {
            return false;
        }

        string? ownLabel = sender?.Label;
        return ownLabel is null || !string.Equals(label, ownLabel, StringComparison.Ordinal);
    }

    /// <summary>
    /// 一条入站消息是否被接受：先查来源，再查令牌。命令授权不在这里——
    /// 只有 invoke 有命令名，那是分发器的事（见 <see cref="TryAuthorize"/>）。
    /// </summary>
    /// <remarks>
    /// 被拒时在这里就上报（调试输出 + <see cref="Rejected"/>），调用方只需把
    /// <paramref name="rejection"/> 回给页面——三平台与分发器因此不必各自补一次上报。
    /// </remarks>
    internal bool TryAccept(string? documentUrl, JsonElement message, out string? rejection)
    {
        if (!IsTrustedUrl(documentUrl))
        {
            rejection = $"不信任当前文档来源，已丢弃该消息（来源：{Truncate(documentUrl)}）。" +
                        "页面必须来自内嵌资源，或用 UseCapabilities(AllowOrigin) 显式放行。";
            Report(OrielIpcRejectionLayer.Source, rejection, documentUrl, null);
            return false;
        }

        if (!TokenMatches(ReadToken(message)))
        {
            rejection = "IPC 令牌不匹配，已丢弃该消息。";
            Report(OrielIpcRejectionLayer.Token, rejection, documentUrl, null);
            return false;
        }

        rejection = null;
        return true;
    }

    private static string? ReadToken(JsonElement message)
        => message.ValueKind == JsonValueKind.Object
           && message.TryGetProperty(OrielIpcProtocol.TokenField, out JsonElement token)
           && token.ValueKind == JsonValueKind.String
            ? token.GetString()
            : null;

    private static string Truncate(string? value)
        => value is null ? "(无)" : value.Length <= 80 ? value : value[..80] + "…";

    /// <summary>被拒消息的观测点（由 <see cref="OrielApp.IpcRejected"/> 转发给应用）。</summary>
    internal event Action<OrielIpcRejectedEventArgs>? Rejected;

    /// <summary>
    /// 记一笔被拒的消息：写调试输出 + 触发 <see cref="Rejected"/>。
    /// </summary>
    /// <remarks>
    /// 来源与令牌两层由 <see cref="TryAccept"/> 在这里上报；命令授权层由分发器上报——
    /// 只有它同时知道命令名与当前文档（<see cref="TryAuthorize"/> 拿不到文档 URL）。
    /// </remarks>
    internal void Report(OrielIpcRejectionLayer layer, string? reason, string? documentUrl, string? commandName)
    {
        if (string.IsNullOrEmpty(reason))
        {
            return;
        }

        OrielDiagnostics.Report($"[OrielWeb] IPC 已拒绝（{layer}）：{reason}");
        Rejected?.Invoke(new OrielIpcRejectedEventArgs(layer, reason, documentUrl, commandName));
    }
}
