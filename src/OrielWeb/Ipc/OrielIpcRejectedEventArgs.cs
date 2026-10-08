namespace OrielWeb.Ipc;

/// <summary>入站 IPC 被门禁拒绝的层级（见 <see cref="OrielIpcGuard"/>）。</summary>
public enum OrielIpcRejectionLayer
{
    /// <summary>来源：发消息的文档不在可信前缀里（内嵌资源或 <c>AllowOrigin</c> 放行的来源）。</summary>
    Source,

    /// <summary>令牌：消息没带、或带错本次进程的令牌。</summary>
    Token,

    /// <summary>命令授权：来源与令牌都对，但这个命令不在 allow/deny 名单的允许侧。</summary>
    Command,
}

/// <summary>
/// 入站 IPC 消息被门禁拒绝（<see cref="OrielApp.IpcRejected"/> 的参数）。
/// </summary>
/// <remarks>
/// <para>
/// 拒绝此前只在 <c>Debug.WriteLine</c> 里留痕——Release 下宿主进程什么都看不到，
/// 而"页面的按钮点了没反应"最常见的成因正是被门禁拦下。这个事件是宿主侧唯一的观测入口
/// （评审 P3）。
/// </para>
/// <para>
/// 它与页面通道不重复：<c>invoke</c> 被拒时页面**也会**收到 <c>ok:false</c> 回执（含原因），
/// 而非 <c>invoke</c> 的消息（<c>message</c> / <c>console</c> / <c>evalResult</c>）被拒时
/// 页面没有任何反馈，只有这个事件能看到。
/// </para>
/// </remarks>
public sealed class OrielIpcRejectedEventArgs
{
    internal OrielIpcRejectedEventArgs(
        OrielIpcRejectionLayer layer, string reason, string? documentUrl, string? commandName)
    {
        Layer = layer;
        Reason = reason;
        DocumentUrl = documentUrl;
        CommandName = commandName;
    }

    /// <summary>被拒的层级。</summary>
    public OrielIpcRejectionLayer Layer { get; }

    /// <summary>可排查的原因：与写进调试输出、以及回给页面的文案一致。</summary>
    public string Reason { get; }

    /// <summary>消息所在文档的 URL；场景或平台取不到时为 <c>null</c>。</summary>
    public string? DocumentUrl { get; }

    /// <summary>被拒的命令名；只有 <see cref="OrielIpcRejectionLayer.Command"/> 层非空。</summary>
    public string? CommandName { get; }
}
