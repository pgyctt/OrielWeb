using System.Text.Json;

namespace OrielWeb.Ipc;

/// <summary>
/// 处理页面发来的 <c>{ __oriel:'cancel', id }</c>：把取消信号送进对应的在飞命令。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么有这条消息</b>：页面侧的超时是<b>它自己</b>的 <c>setTimeout</c>（<c>oriel.timeout</c>
/// 或 per-call 覆盖），宿主完全不知道对端配了多久——所以宿主不能自己定超时去对齐。
/// 唯一能对齐的时机是页面放弃的**那一刻**，由它告诉宿主。
/// </para>
/// <para>
/// <b>为什么单独一个类型</b>：三个平台各有自己的消息处理器（Windows 在
/// <c>Win32WindowHost.Content</c>、macOS/Linux 各有 WebMessageHandler），
/// 而这段逻辑一模一样。复制三份意味着"id 读法"这种细节会漂移——而它一旦漂移，
/// 表现是"某个平台上取消失效"，且不会有任何报错。
/// </para>
/// <para>
/// <b>门禁由调用方先过</b>：本类型只负责"读 id + 转给分发器"。来源与令牌校验在调用方
/// （与 <c>message</c> / <c>console</c> 那两条一样只过前两层，命令授权只管 <c>invoke</c>）。
/// 取消天然只能作用于**本窗口自己**的在飞调用：分发器的键是（窗口, id），
/// 页面伪造一个别人的 id 也匹配不到本窗口的条目。
/// </para>
/// </remarks>
internal static class IpcCancelMessage
{
    /// <summary>
    /// 从一条 <c>cancel</c> 消息里取 id 并请求取消。
    /// </summary>
    /// <param name="dispatcher">命令分发器。</param>
    /// <param name="window">消息来源窗口（必须与登记在飞调用时是同一个引用）。</param>
    /// <param name="root">消息根元素。</param>
    /// <returns>真的取消到了返回 <c>true</c>。</returns>
    internal static bool TryCancel(
        OrielCommandDispatcher dispatcher, IOrielWindowControl? window, JsonElement root)
    {
        // 没有 id 就什么都不做：这条消息本来也匹配不到任何在飞调用。
        // 不报错、不回执——页面放弃时不需要宿主确认，多一条回执只是噪音。
        if (!root.TryGetProperty("id", out JsonElement id))
        {
            return false;
        }

        // 用**原文**而不是解析成整数：与登记侧（HandleInvokeAsync）保持同一口径。
        // 解析会在 1.5 / 1e30 这类值上撞车或抛异常，而原文永远能对上。
        return dispatcher.CancelInvoke(window, id.GetRawText());
    }
}
