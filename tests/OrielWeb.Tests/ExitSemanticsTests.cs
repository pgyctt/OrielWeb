using Xunit;

namespace OrielWeb.Tests;

/// <summary>
/// 退出语义：<c>Quit()</c> 是"请求退出 + 保证进程最终结束"，而不是"建议退出"
/// （下游反馈 2026-10-14 的 P0-1：消息循环停下后宿主的清理卡住 → 进程驻留 → 现场"删不掉程序"）。
/// </summary>
/// <remarks>
/// 看门狗真的强杀进程这件事没法在测试进程里验证（<c>Environment.Exit</c> 会把测试运行器一起带走），
/// 那部分由 demo 的 <c>--selftest exit-timeout</c> 在**子进程**里取证；
/// 这里钉住配置面、默认值、以及"未运行时的前置检查"这些可以在进程内断言的契约。
/// </remarks>
public sealed class ExitSemanticsTests
{
    [Fact]
    public void ExitTimeoutDuration_DefaultsToTenSeconds()
    {
        using var app = Oriel.CreateBuilder().Build();

        Assert.Equal(TimeSpan.FromSeconds(10), app.ExitTimeoutDuration);
    }

    [Fact]
    public void ForceExitCode_DefaultsToTwo()
    {
        using var app = Oriel.CreateBuilder().Build();

        // 与正常退出（0）区分：脚本与现场排障据此判断"这次不是正常退出的"
        Assert.Equal(2, app.ForceExitCode);
    }

    [Fact]
    public void BuilderOverrides_AreHonoured()
    {
        using var app = Oriel.CreateBuilder()
            .UseExitTimeout(TimeSpan.FromSeconds(3))
            .UseForceExitCode(7)
            .Build();

        Assert.Equal(TimeSpan.FromSeconds(3), app.ExitTimeoutDuration);
        Assert.Equal(7, app.ForceExitCode);
    }

    [Fact]
    public void UseExitTimeout_NonPositive_Throws()
    {
        var builder = Oriel.CreateBuilder();

        Assert.Throws<ArgumentOutOfRangeException>(() => builder.UseExitTimeout(TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.UseExitTimeout(TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void Quit_BeforeRun_Throws()
    {
        using var app = Oriel.CreateBuilder().Build();

        // 应用未运行：后端还没建出来，没有消息循环可结束
        Assert.Throws<InvalidOperationException>(app.Quit);
    }

    [Fact]
    public void Quit_FromNonUiThread_Throws_WithThreadingExplanation()
    {
        using var app = Oriel.CreateBuilder().Build();

        // 未运行时一律抛（后端为空）；这里断言的是**文案指向线程相关性**这件事不会被丢掉。
        // 真机上的"从线程池调 Quit"由 demo 自检覆盖（见 ExitSemanticsTests 的类说明）。
        var ex = Assert.Throws<InvalidOperationException>(app.Quit);
        Assert.Contains("Run()", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void IsOnUiThread_BeforeRun_IsFalse()
    {
        using var app = Oriel.CreateBuilder().Build();

        // 未运行时没有 UI 线程可言：宿主据此判断"要不要编组"时必须拿到一个安全的 false
        Assert.False(app.IsOnUiThread);
    }

    [Fact]
    public void PostToMainThread_BeforeRun_Throws()
    {
        using var app = Oriel.CreateBuilder().Build();

        Assert.Throws<InvalidOperationException>(() => app.PostToMainThread(() => { }));
    }

    [Fact]
    public void TryPostToMainThread_BeforeRun_ReturnsFalse_AndReports()
    {
        using var app = Oriel.CreateBuilder().Build();
        // 订阅必须在 Build 之后：Build 自己会产生诊断（未注册命令提示），不该混进这里。
        using var diagnostics = new DiagnosticCollector();

        bool posted = app.TryPostToMainThread(() => { });

        // 关键：不抛异常，但也不静默——返回 false 且留下痕迹
        Assert.False(posted);
        Assert.True(diagnostics.Contains("TryPostToMainThread 失败"));
    }

    [Fact]
    public void PostToMainThread_NullAction_Throws()
    {
        using var app = Oriel.CreateBuilder().Build();

        Assert.Throws<ArgumentNullException>(() => app.PostToMainThread(null!));
        Assert.Throws<ArgumentNullException>(() => app.TryPostToMainThread(null!));
    }

    [Fact]
    public void InvokeOnUiThread_TimeoutParameterIsOptionalAndNullByDefault()
    {
        // 为什么只钉形状、不测超时行为：OrielApp 没有后端注入点，而 IPlatformBackend 有约 100 个成员——
        // 为验证一个 TimeSpan 造一个假后端不划算。这里能守住的是**默认值**：
        // 一旦默认变成非 null，所有既有调用方的行为都会从"无限等待"变成"可能超时"，
        // 那是隐蔽的破坏性变更，必须有东西拦一下。
        var appMethod = typeof(OrielApp).GetMethod(
            nameof(OrielApp.InvokeOnUiThread),
            [typeof(Action), typeof(TimeSpan?)]);
        Assert.NotNull(appMethod);

        var timeoutParameter = appMethod.GetParameters()[1];
        Assert.True(timeoutParameter.IsOptional);
        Assert.Null(timeoutParameter.DefaultValue);

        // 两处共用一套语义，签名不能漂移
        var windowMethod = typeof(WebviewWindow).GetMethod(
            nameof(WebviewWindow.InvokeOnUiThread),
            [typeof(Action), typeof(TimeSpan?)]);
        Assert.NotNull(windowMethod);
        Assert.Null(windowMethod.GetParameters()[1].DefaultValue);
    }

    [Fact]
    public void InvokeOnUiThread_BeforeRun_Throws()
    {
        using var app = Oriel.CreateBuilder().Build();

        Assert.Throws<InvalidOperationException>(() => app.InvokeOnUiThread(() => { }));
    }

    [Fact]
    public void IsExiting_BeforeRun_IsFalse()
    {
        using var app = Oriel.CreateBuilder().Build();

        Assert.False(app.IsExiting);
    }
}
