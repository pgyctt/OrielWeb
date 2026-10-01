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
///   <item><description><b>令牌</b>：<c>TokenMatches</c>。每进程一个随机串，随桥接脚本注入页面，
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
    /// <param name="assetHost">内嵌资源的虚拟主机名（<c>UseEmbeddedAssets</c> 的 host 参数）。</param>
    internal OrielIpcGuard(OrielCapabilityOptions? options, bool isDebugBuild, string assetHost)
    {
        _options = options;
        _isDebugBuild = isDebugBuild;
        Token = OrielIpcToken.Generate();

        // 内嵌资源自动可信：页面本来就是应用自己的那一份。
        // Windows 上就是这个 https 虚拟主机；Linux/macOS 上会被改写成 file://，
        // 那份前缀由 OrielApp 在解压之后补进来（那时才知道目录在哪）。
        AddTrustedPrefix($"https://{assetHost}/");

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

    /// <summary>追加一个可信来源（URL 前缀）。Linux/macOS 的 <c>file://</c> 解压目录由此进来。</summary>
    internal void AddTrustedPrefix(string prefix)
    {
        if (!string.IsNullOrEmpty(prefix))
        {
            _trustedPrefixes.Add(prefix);
        }
    }

    /// <summary>该 URL 是否来自可信来源。</summary>
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
    internal bool TryAccept(string? documentUrl, JsonElement message, out string? rejection)
    {
        if (!IsTrustedUrl(documentUrl))
        {
            rejection = $"不信任当前文档来源，已丢弃该消息（来源：{Truncate(documentUrl)}）。" +
                        "页面必须来自内嵌资源，或用 UseCapabilities(AllowOrigin) 显式放行。";
            return false;
        }

        if (!TokenMatches(ReadToken(message)))
        {
            rejection = "IPC 令牌不匹配，已丢弃该消息。";
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

    /// <summary>被拒时记一笔：没有它，"点了没反应"会完全无从下手。</summary>
    internal static void Report(string? reason)
    {
        if (!string.IsNullOrEmpty(reason))
        {
            Debug.WriteLine($"[OrielWeb] IPC 已拒绝：{reason}");
        }
    }
}
