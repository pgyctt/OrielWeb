using System.Text.Json;
using OrielWeb.Ipc;
using Xunit;

namespace OrielWeb.Tests;

/// <summary>
/// 内建事件命令（<c>event.emit</c>）：路由、参数校验、目标解析，以及**能力门禁**。
/// </summary>
/// <remarks>
/// <para>
/// 这是本库唯一一处"库内建命令不始终放行"的地方，所以两类断言都要有：
/// 功能层面（参数怎么解析、目标怎么找）与安全层面（不在 allow 名单时必须被拒）。
/// </para>
/// <para>
/// 广播的实际投递（页面真的收到事件）需要真实窗口后端，那部分由
/// <c>OrielDemo --selftest event</c> 在真机上取证——这里只覆盖"没有窗口"的边界与参数形状。
/// </para>
/// </remarks>
public sealed class BuiltInEventCommandTests
{
    /// <summary>造一个分发器：<paramref name="allowEvent"/> 决定 event.emit 是否在能力名单里。</summary>
    private static (OrielCommandDispatcher Dispatcher, BuiltInWindowCommandTests.FakeWindow Window) Create(
        bool allowEvent, bool isDebugBuild = false)
    {
        // 用一个未运行的空应用：它有 Windows 表（空），所以广播会走"发不到任何窗口"的分支，
        // 而这正是我们要断言的边界之一。
        OrielApp app = Oriel.CreateBuilder().Build();
        var window = new BuiltInWindowCommandTests.FakeWindow { App = app };

        var guard = allowEvent
            ? new OrielIpcGuard(new OrielCapabilityOptions().Allow("event.*"), isDebugBuild, "app.oriel")
            : new OrielIpcGuard(new OrielCapabilityOptions().Allow("todo.*"), isDebugBuild, "app.oriel");

        var dispatcher = new OrielCommandDispatcher([], null, guard);
        return (dispatcher, window);
    }

    [Fact]
    public async Task EventEmit_IsNotUnknownCommand()
    {
        // 未接线时它会走到"未知命令"，而这个用例钉住"内建认得它"
        (var dispatcher, var window) = Create(allowEvent: true);

        (JsonDocument reply, _) = await TestHarness.DispatchAsync(
            dispatcher, "event.emit", new { @event = "my-event", payload = new { a = 1 } }, window: window);

        // 空应用没有窗口，广播投递 0 个——但**命令本身必须成功**（不是 "未知命令"）
        Assert.True(reply.RootElement.GetProperty("ok").GetBoolean(), reply.RootElement.ToString());
        Assert.Equal(0, reply.Value().GetInt32());
    }

    [Fact]
    public async Task EventEmit_ReturnsDeliveryCountForBroadcast()
    {
        (var dispatcher, var window) = Create(allowEvent: true);

        (JsonDocument reply, _) = await TestHarness.DispatchAsync(
            dispatcher, "event.emit", new { @event = "e" }, window: window);

        // 0 个窗口 → 投递 0。返回值让页面能观测"到底送到没有"，
        // 而不是只有一个永远成功的 Promise。
        Assert.Equal(0, reply.Value().GetInt32());
    }

    [Fact]
    public async Task EventEmit_UnknownTargetDeliversZeroInsteadOfThrowing()
    {
        (var dispatcher, var window) = Create(allowEvent: true);

        (JsonDocument reply, _) = await TestHarness.DispatchAsync(
            dispatcher, "event.emit", new { @event = "e", target = "no-such-window" }, window: window);

        // 目标窗口不存在时**静默丢弃**（与 Tauri 的 emitTo 一致）：多窗口应用里窗口随时可能被关掉，
        // "发的时候还在、到的时候没了"是正常时序。返回 0 而不是抛，让页面能观测但不被迫包 try。
        Assert.True(reply.RootElement.GetProperty("ok").GetBoolean(), reply.RootElement.ToString());
        Assert.Equal(0, reply.Value().GetInt32());
    }

    [Fact]
    public async Task EventEmit_MissingEventNameIsRejected()
    {
        (var dispatcher, var window) = Create(allowEvent: true);

        (JsonDocument reply, _) = await TestHarness.DispatchAsync(
            dispatcher, "event.emit", new { payload = 1 }, window: window);

        Assert.False(reply.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("event", reply.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task EventEmit_EmptyEventNameIsRejected()
    {
        (var dispatcher, var window) = Create(allowEvent: true);

        (JsonDocument reply, _) = await TestHarness.DispatchAsync(
            dispatcher, "event.emit", new { @event = "" }, window: window);

        Assert.False(reply.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task EventEmit_NeedsAWindow()
    {
        (var dispatcher, _) = Create(allowEvent: true);

        // 没有来源窗口时不猜"该发给谁"，直接拒绝（与 win.* / tray.* 一致）
        (JsonDocument reply, _) = await TestHarness.DispatchAsync(
            dispatcher, "event.emit", new { @event = "e" }, window: null);

        Assert.False(reply.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("窗口", reply.RootElement.GetProperty("error").GetString());
    }

    // ---- 安全红线：必须过能力门禁 ----

    [Fact]
    public async Task EventEmit_IsDeniedWhenNotInAllowList()
    {
        // Release 语义（isDebugBuild: false）+ 只声明了 todo.*
        (var dispatcher, var window) = Create(allowEvent: false);

        (JsonDocument reply, _) = await TestHarness.DispatchAsync(
            dispatcher, "event.emit", new { @event = "e" }, window: window);

        Assert.False(
            reply.RootElement.GetProperty("ok").GetBoolean(),
            "event.emit 不在 allow 名单里时必须被拒——这正是它与 win.* 的区别。");

        string error = reply.RootElement.GetProperty("error").GetString()!;
        Assert.Contains("不在 Allow 名单里", error);
    }

    [Fact]
    public async Task EventEmit_IsDeniedUnderASelectiveAllowList()
    {
        // 模拟一个真实应用：只声明业务命令。它不该意外获得跨窗口广播能力。
        OrielApp app = Oriel.CreateBuilder().Build();
        var window = new BuiltInWindowCommandTests.FakeWindow { App = app };
        var guard = new OrielIpcGuard(
            new OrielCapabilityOptions().Allow("todo.*", "sys.info", "manual.*"),
            isDebugBuild: false,
            "app.oriel");

        var dispatcher = new OrielCommandDispatcher([], null, guard);

        (JsonDocument reply, _) = await TestHarness.DispatchAsync(
            dispatcher, "event.emit", new { @event = "e" }, window: window);

        Assert.False(reply.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task EventEmit_CanBeExplicitlyDeniedEvenWhenPrefixIsAllowed()
    {
        // "先宽泛 allow、再 deny 挖例外"必须生效（deny 优先）
        OrielApp app = Oriel.CreateBuilder().Build();
        var window = new BuiltInWindowCommandTests.FakeWindow { App = app };
        var guard = new OrielIpcGuard(
            new OrielCapabilityOptions().Allow("event.*").Deny("event.emit"),
            isDebugBuild: false,
            "app.oriel");

        var dispatcher = new OrielCommandDispatcher([], null, guard);

        (JsonDocument reply, _) = await TestHarness.DispatchAsync(
            dispatcher, "event.emit", new { @event = "e" }, window: window);

        Assert.False(reply.RootElement.GetProperty("ok").GetBoolean());
    }
}
