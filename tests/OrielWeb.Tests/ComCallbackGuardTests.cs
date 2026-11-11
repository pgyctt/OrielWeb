using OrielWeb.Platform.Windows.WebView2.Handlers;
using OrielWeb.Platform.Windows.WebView2.Utilities;
using Xunit;

namespace OrielWeb.Tests;

/// <summary>
/// WebView2 回调的异常边界（2026-11-11 评审 H3）。
/// </summary>
/// <remarks>
/// <para>
/// 修之前：每个 <c>Invoke</c> 都是 <c>=&gt; callback(...)</c> 直传，文件头注释却写着
/// "所有 Invoke 都把异常吞掉并返回 HRESULT"——注释与实现相反。异常穿过 COM 边界 =
/// 进程 fail-fast，而 WebView2 的回调跑在它自己的消息泵里，没有调用方可以接住：
/// 应用代码一个空引用就能带走整个宿主。
/// </para>
/// <para>
/// 这里钉三层：回调异常变成错误 HRESULT（不是抛出）、正常返回值原样透传、
/// 用户事件订阅者抛异常既不离开边界也不影响后续事件。
/// </para>
/// </remarks>
public sealed class ComCallbackGuardTests
{
    [Fact]
    public void CallbackExceptionBecomesErrorHResultInsteadOfEscaping()
    {
        var handler = new EnvironmentCompletedHandler(
            (_, _) => throw new InvalidOperationException("应用代码里的空引用"));

        int result = handler.Invoke(0, 0);

        Assert.Equal(ComCallbackGuard.EFail, result);
    }

    [Fact]
    public void CallbackReturnValueIsPassedThrough()
    {
        // 兜底不能顺手把正常路径也改成失败
        var handler = new EnvironmentCompletedHandler((_, _) => 42);

        Assert.Equal(42, handler.Invoke(0, 0));
    }

    [Theory]
    [InlineData("ExecuteScriptCompleted")]
    [InlineData("WebMessageReceived")]
    [InlineData("NavigationStarting")]
    public void EveryHandlerFamilyIsGuarded(string family)
    {
        // 逐族抽一条：异常都被挡下（若哪天有人新增回调时忘了走 Guard，对应的断言会漏，
        // 所以这里同时验证"名字进了诊断"，见下一条）
        Func<int> invoke = family switch
        {
            "ExecuteScriptCompleted" => () => new ExecuteScriptCompletedHandler(
                (_, _) => throw new InvalidOperationException("x")).Invoke(0, string.Empty),
            "WebMessageReceived" => () => new WebMessageReceivedHandler(
                (_, _) => throw new InvalidOperationException("x")).Invoke(0, 0),
            _ => () => new NavigationStartingHandler(
                (_, _) => throw new InvalidOperationException("x")).Invoke(0, 0),
        };

        Assert.Equal(ComCallbackGuard.EFail, invoke());
    }

    [Fact]
    public void DiagnosticsNameTheFailingCallback()
    {
        // 诊断文案要能定位是哪一条回调出的错——否则"宿主没崩但功能不对"将无从下手
        using var collector = new DiagnosticCollector();
        var handler = new WebMessageReceivedHandler(
            (_, _) => throw new InvalidOperationException("boom"));

        handler.Invoke(0, 0);

        Assert.True(collector.Contains("WebMessageReceived"), "诊断里应当出现出错的回调名");
    }

    [Fact]
    public void EventSubscriberExceptionDoesNotEscape()
    {
        // 事件订阅者抛异常同样是应用代码的问题，但不该穿过 COM 边界
        ComCallbackGuard.Raise(
            () => throw new InvalidOperationException("订阅者炸了"), "TestEvent");
    }

    [Fact]
    public void EventSubscriberExceptionDoesNotStopLaterSubscribers()
    {
        // "一个订阅者抛异常不影响后续事件"：异常没有机会离开这一层，后续调用照常执行
        int reached = 0;

        ComCallbackGuard.Raise(() => throw new InvalidOperationException("first"), "TestEvent");
        ComCallbackGuard.Raise(() => reached++, "TestEvent");

        Assert.Equal(1, reached);
    }
}
