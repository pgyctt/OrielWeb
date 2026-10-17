using OrielWeb;
using Xunit;

namespace OrielWeb.Tests;

/// <summary>
/// 关闭判定管线：**宿主否决 → 页面接管 → 放行**。
/// </summary>
/// <remarks>
/// <para>
/// 这是 C5 的核心逻辑，也是最该被钉死的一处：三平台的原生关闭入口各不相同
/// （Windows <c>WM_CLOSE</c>、GTK <c>delete-event</c>、Cocoa <c>windowShouldClose:</c>），
/// 但都必须给出同一个答案。逻辑漂移的症状是"某个平台关不掉窗口"或"某个平台拦不住"，
/// 而那种差异在单一平台上根本发现不了。
/// </para>
/// <para>
/// 真机行为由 <c>OrielDemo --selftest close</c> 取证（含真实的 X 按钮路径）；
/// 这里只保证判定本身与顺序正确。
/// </para>
/// </remarks>
public sealed class ClosePipelineTests
{
    /// <summary>记录 <c>close-requested</c> 事件是否被推送。</summary>
    private static (OrielCloseDecision Decision, int Emitted, bool CancelSeen) Decide(
        bool cancel, bool pageCanDecide)
    {
        int emitted = 0;
        var decision = OrielClosePipeline.Decide(
            closing: cancel ? args => args.Cancel = true : null,
            pageCanDecide: pageCanDecide,
            emitCloseRequested: () => emitted++);

        return (decision, emitted, cancel);
    }

    [Fact]
    public void NeitherVeto_Proceeds()
    {
        (OrielCloseDecision decision, int emitted, _) = Decide(cancel: false, pageCanDecide: false);

        Assert.Equal(OrielCloseDecision.Proceed, decision);
        Assert.Equal(0, emitted);
    }

    [Fact]
    public void HostVeto_Cancels()
    {
        (OrielCloseDecision decision, int emitted, _) = Decide(cancel: true, pageCanDecide: false);

        Assert.Equal(OrielCloseDecision.Cancel, decision);
        Assert.Equal(0, emitted);
    }

    [Fact]
    public void PageIntercept_SuspendsAndEmits()
    {
        (OrielCloseDecision decision, int emitted, _) = Decide(cancel: false, pageCanDecide: true);

        Assert.Equal(OrielCloseDecision.Suspend, decision);
        Assert.True(emitted == 1, "挂起时必须把 close-requested 推出去——不会有第二次机会通知页面");
    }

    [Fact]
    public void HostVeto_WinsOverPageIntercept()
    {
        // **顺序不能颠倒**：应用说"不许关"就该立刻生效，不必先问页面再被页面拒绝。
        // 颠倒了的话，页面会收到一个它无权决定的通知，还会把宿主的否决误报成
        // "用户在确认框里点了取消"。
        (OrielCloseDecision decision, int emitted, _) = Decide(cancel: true, pageCanDecide: true);

        Assert.Equal(OrielCloseDecision.Cancel, decision);
        Assert.True(emitted == 0, "宿主已经否决，不该再打扰页面");
    }

    [Fact]
    public void PageNotReady_ProceedsInsteadOfSuspending()
    {
        // 页面还没加载完成就挂起，那条 close-requested 永远送不到，
        // 窗口会变成点 X 没反应——用户唯一的出路是任务管理器。
        // 这与 win.* 后门要防的是同一类事故。
        (OrielCloseDecision decision, int emitted, _) = Decide(cancel: false, pageCanDecide: false);

        Assert.Equal(OrielCloseDecision.Proceed, decision);
        Assert.Equal(0, emitted);
    }

    [Fact]
    public void NoClosingSubscriber_DoesNotThrow()
    {
        // Closing 没有订阅者时 delegate 是 null，管线必须容忍
        // （每个 `Closing?.Invoke` 都写成 `closing?.Invoke` 的原因）。
        Assert.Null(Record.Exception(
            () => OrielClosePipeline.Decide(null, pageCanDecide: false, emitCloseRequested: () => { })));
    }

    [Fact]
    public void ClosingSubscriberException_Propagates()
    {
        // 订阅者抛出时**不吞**：静默吞掉会让"应用里那段未保存检查炸了"表现为
        // "窗口关不掉"，而原因完全看不出来。让异常冒到平台上，
        // 各平台自己的 try/catch（ObjC trampoline、GTK trampoline）会记诊断。
        Assert.Throws<InvalidOperationException>(() => OrielClosePipeline.Decide(
            _ => throw new InvalidOperationException("未保存检查失败"),
            pageCanDecide: false,
            emitCloseRequested: () => { }));
    }

    [Fact]
    public void CloseRequestedEventName_IsStable()
    {
        // 页面侧按这个名字订阅；改名等于破坏兼容，所以钉住它。
        Assert.Equal("close-requested", OrielClosePipeline.CloseRequestedEventName);
    }

    [Fact]
    public void CancelledFlagIsPerCall()
    {
        // 每次判定都用**新的**参数对象：复用会让上一次的 Cancel 泄漏到下一次，
        // 症状是"取消过一次之后，这个窗口再也关不掉"。
        var args1 = new OrielCloseRequestEventArgs();
        args1.Cancel = true;

        var decision = OrielClosePipeline.Decide(
            _ => { },   // 这一次不置 Cancel
            pageCanDecide: false,
            emitCloseRequested: () => { });

        Assert.Equal(OrielCloseDecision.Proceed, decision);
        Assert.NotSame(args1, new OrielCloseRequestEventArgs());
    }
}
