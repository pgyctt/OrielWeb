namespace OrielWeb;

/// <summary>
/// 一次窗口关闭请求的判定结果。
/// </summary>
/// <remarks>
/// 三平台的原生关闭入口各不相同（Windows <c>WM_CLOSE</c>、GTK <c>delete-event</c>、
/// Cocoa <c>windowShouldClose:</c>），但它们要回答的是同一个问题：
/// <b>"这次关闭，现在就走，还是拦下来？"</b> 这个枚举就是那个答案的口径。
/// </remarks>
internal enum OrielCloseDecision
{
    /// <summary>放行：真正销毁窗口。</summary>
    Proceed,

    /// <summary>宿主侧拒绝（<see cref="WebviewWindow.Closing"/> 里置了 <c>Cancel</c>）。</summary>
    Cancel,

    /// <summary>
    /// 页面接管：窗口**不销毁**，已把 <c>close-requested</c> 推给页面，等它决定。
    /// </summary>
    /// <remarks>
    /// 页面若同意关闭，调 <c>win.destroy</c>（见 <see cref="WebviewWindow.Destroy"/>）；
    /// 若不同意，什么都不做——窗口就停在打开状态。
    /// <b>没有超时，也不会有</b>：这是页面自己的决定，库替它设一个"多久不回就当同意"
    /// 会让确认框在用户思考时被自动关掉。
    /// </remarks>
    Suspend,
}

/// <summary>
/// 关闭判定的共享管线：**宿主否决 → 页面接管 → 放行**。
/// </summary>
/// <remarks>
/// <para>
/// 抽成一处而不是三平台各写一遍，理由与桥接脚本合并相同：这三份逻辑一旦漂移，
/// 症状是"某个平台的关闭行为不一样"，而那种差异极难在单平台上发现。
/// </para>
/// <para>
/// <b>顺序不能颠倒</b>：宿主的 <see cref="WebviewWindow.Closing"/> 先判。应用说"不许关"
/// （未保存的数据、正在跑的工序）就该立刻生效，不必先问页面、再被页面拒绝——
/// 那样页面会收到一个它无权决定的通知，还会把宿主的否决误报成"用户在确认框点了取消"。
/// </para>
/// <para>
/// <b>为什么必须"挂起"而不是"等待"</b>：Windows 的 <c>WM_CLOSE</c> 是**同步消息**，
/// 页面回执却是异步的 IPC。在消息处理里等回执等于把 UI 线程锁死，而回执的处理本身
/// 也要 UI 线程——必然死锁。所以只能"当场不销毁 + 推事件"，由页面稍后主动调
/// <c>win.destroy</c> 补上那一步。这也是 Tauri 的做法。
/// </para>
/// </remarks>
internal static class OrielClosePipeline
{
    /// <summary>页面侧的接管事件名（页面用 <c>oriel.on('close-requested', …)</c> 订阅）。</summary>
    internal const string CloseRequestedEventName = "close-requested";

    /// <summary>
    /// 判定一次关闭请求。
    /// </summary>
    /// <param name="closing">
    /// 宿主的否决入口（<see cref="WebviewWindow.Closing"/>）；<c>null</c> 表示没有订阅者。
    /// </param>
    /// <param name="pageCanDecide">
    /// 页面是否有能力接管。只有它为 <c>true</c> 时才可能返回 <see cref="OrielCloseDecision.Suspend"/>。
    /// </param>
    /// <param name="emitCloseRequested">把 <c>close-requested</c> 推给页面的动作。</param>
    /// <returns>本次关闭的判定。</returns>
    /// <remarks>
    /// <paramref name="pageCanDecide"/> 由调用方算出来（通常是
    /// "页面开启了接管 **且** 页面已经加载完成"）。**"页面已加载"那一半不能省**：
    /// 页面还没加载就挂起，那条事件永远送不到，窗口会变成点 X 没反应——
    /// 而用户唯一的出路是任务管理器。这与 <c>win.*</c> 后门要防的是同一类事故。
    /// </remarks>
    internal static OrielCloseDecision Decide(
        Action<OrielCloseRequestEventArgs>? closing,
        bool pageCanDecide,
        Action emitCloseRequested)
    {
        var args = new OrielCloseRequestEventArgs();
        closing?.Invoke(args);

        if (args.Cancel)
        {
            return OrielCloseDecision.Cancel;
        }

        if (!pageCanDecide)
        {
            return OrielCloseDecision.Proceed;
        }

        // 先推事件再返回：调用方拿到 Suspend 后只是"不销毁"，
        // 不会有第二次机会通知页面，所以通知必须在这里发出去。
        emitCloseRequested();
        return OrielCloseDecision.Suspend;
    }
}
