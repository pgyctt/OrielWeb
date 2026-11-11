using System.Collections.Concurrent;

namespace OrielWeb.Platform;

/// <summary>
/// 主线程投递队列的公共操作。三平台各自持有一个 <see cref="ConcurrentQueue{T}"/>，
/// 但"投递失败时怎么把刚入队的那一个收回来"是同一件事——放在这里，免得两边各写一份、
/// 然后其中一份漏了某个边界。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么需要"回收"</b>：投递是"先入队，再让主循环来排空"两步。第二步失败时
/// （Linux 的 <c>g_idle_add_full</c> 返回 0、macOS 的 pump helper 不可用），
/// 那个动作永远不会被排空——留在队列里只是占位，但会让"队列长度"这个诊断信号失真，
/// 也让调用方以为"投递进去了"。
/// </para>
/// <para>
/// <b>为什么必须按引用相等比较</b>：委托的 <c>==</c> 是**值语义**（逐项比较调用列表），
/// 于是两个内容相同、彼此独立的 lambda 会被判为相等。用它做判据会误删另一个动作——
/// 那种 bug 的表现是"某个回调偶尔不执行"，取决于队列里恰好有没有长得一样的兄弟。
/// </para>
/// <para>
/// <b>为什么是"取空再塞回"</b>：<see cref="ConcurrentQueue{T}"/> 没有"删除指定元素"。
/// 这个函数在 UI 线程上被调用（各平台的投递入口本就在主线程），不存在并发入队的窗口；
/// 即便有，取空再塞回也只是保持其余元素的原顺序，不会丢别的动作。
/// </para>
/// </remarks>
internal static class MainThreadDispatchQueue
{
    /// <summary>
    /// 从队列里移除**一个**与 <paramref name="action"/> 相同的委托。
    /// </summary>
    /// <param name="queue">目标队列。</param>
    /// <param name="action">要移除的那个动作。</param>
    /// <returns>移除成功返回 <c>true</c>；队列里没有这个动作时返回 <c>false</c>。</returns>
    internal static bool TryRemoveOne(ConcurrentQueue<Action> queue, Action action)
    {
        int remaining = queue.Count;
        bool removed = false;

        for (int i = 0; i < remaining; i++)
        {
            if (!queue.TryDequeue(out Action? item))
            {
                break;
            }

            // 只丢第一个匹配的：`removed` 保证同一个委托的多个副本不会全被丢掉。
            if (!removed && ReferenceEquals(item, action))
            {
                removed = true;
                continue;
            }

            queue.Enqueue(item);
        }

        return removed;
    }
}
