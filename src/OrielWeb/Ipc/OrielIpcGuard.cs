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
    /// <param name="denialReason">被拒绝时的**可排查**理由（放行时为 null）。</param>
    internal bool TryAuthorize(string commandName, out string? denialReason)
    {
        // 内建窗口命令（win.*）始终放行：它们只操作自己那个窗口，不是"能力"。
        // 拒掉它们会让无边框窗口连关闭按钮都失效（自绘标题栏，没有系统标题栏可替代）。
        if (OrielCapabilityRules.IsBuiltInCommand(commandName))
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
            : $"命令 '{commandName}' 不在 Allow 名单里（deny-by-default：没声明就是没有这个能力）。";
        return false;
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

        Debug.WriteLine($"[OrielWeb] IPC 已拒绝（{layer}）：{reason}");
        Rejected?.Invoke(new OrielIpcRejectedEventArgs(layer, reason, documentUrl, commandName));
    }
}
