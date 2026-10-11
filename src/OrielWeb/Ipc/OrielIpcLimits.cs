namespace OrielWeb.Ipc;

/// <summary>
/// 入站 IPC 消息的规模闸。
/// </summary>
/// <remarks>
/// <para>
/// 威胁模型把"可信页面"放在攻击面之外，但页面自己写错、或第三方脚本失控，足以一次
/// <c>postMessage</c> 丢进来几十兆 JSON：三平台的入站路径都是「整串读进托管内存 →
/// <c>JsonDocument.Parse</c>」，没有闸就意味着一条消息能把宿主的内存与主线程一起拖住。
/// </para>
/// <para>
/// 闸只装在**入站**方向（页面 → 宿主）。出站（宿主 → 页面）的 JSON 由宿主自己构造、
/// 规模由宿主负责；JS 求值的脚本长度也是宿主 API 的入参，同样不在此列。
/// </para>
/// </remarks>
internal static class OrielIpcLimits
{
    /// <summary>单条入站消息的字符数上限（1 MiB）。</summary>
    /// <remarks>
    /// 正常 IPC 负载是命令参数与返回值，量级在 KB；真超过 1 MiB 的通信本来就该走
    /// <c>oriel://</c> 资源或文件，而不是塞进一条消息里。
    /// </remarks>
    internal const int MaxInboundMessageChars = 1024 * 1024;

    /// <summary>字符串是否超过入站上限（<c>null</c>、空串不算超）。</summary>
    internal static bool ExceedsLimit(string? json)
        => json is not null && json.Length > MaxInboundMessageChars;

    /// <summary>超限时写进门禁上报（<c>IpcRejected</c>）的原因文案。</summary>
    internal static string OversizeReason(int length)
        => $"入站消息 {length} 字符，超过 {MaxInboundMessageChars} 字符上限，未解析即丢弃。";
}
