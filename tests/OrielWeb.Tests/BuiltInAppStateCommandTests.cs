using System.Text.Json;
using OrielWeb.Ipc;
using Xunit;

namespace OrielWeb.Tests;

/// <summary>
/// 内建 <c>app.*</c> 的**有状态三条**（<c>exit</c> / <c>show</c> / <c>hide</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 这三条与同族的只读元信息（<c>app.getName</c> …）门禁姿态不同：**必须显式 Allow**。
/// 它们能退出应用、能隐藏主窗口，属于"应用得点头"的能力，不能因为名字在 <c>app.</c> 族里
/// 就顺带放行。本文件把这条姿态钉住——它是本批最容易在后续重构里被"简化掉"的东西。
/// </para>
/// <para>
/// <b>为什么不在这里断言"Quit 真的被调用了"</b>：<c>Quit</c> 的线程相关性检查与
/// <c>Environment.Exit</c> 的看门狗都要求应用真的 <c>Run</c> 起来，而一旦起来，
/// 退出会把测试运行器一起带走（与 <see cref="ExitSemanticsTests"/> 同一条理由）。
/// 因此这里断言到"门禁放行 → 进到实现的前置检查"为止；"真的退出"由 demo 的
/// <c>--selftest</c> 在**子进程**里取证。
/// </para>
/// </remarks>
public sealed class BuiltInAppStateCommandTests
{
    private static readonly string[] StateCommands = ["app.exit", "app.show", "app.hide"];

    private static OrielCommandDispatcher CreateDispatcher(bool allowAppState)
    {
        // 未放行时只允许一个无关前缀：这样"被拒"一定是因为 app.* 不在名单里，
        // 而不是因为能力表整个是空的（空表在 Release 下拒一切，测不出分族差异）。
        var options = allowAppState
            ? new OrielCapabilityOptions().Allow("app.*")
            : new OrielCapabilityOptions().Allow("todo.*");

        return new OrielCommandDispatcher([], TestJsonContext.Default, new OrielIpcGuard(options, isDebugBuild: false, "app.oriel"));
    }

    private static BuiltInWindowCommandTests.FakeWindow CreateWindow()
        // FakeWindow.App 默认是一个**未运行**的空应用：任何一条命令真往下走都会撞上
        // "应用尚未运行（未调用 Run()）"——正好用来证明"门禁放行后确实进到了实现"。
        => new();

    [Fact]
    public void StateCommands_AreNotInAlwaysAllowedAppCommands()
    {
        foreach (string name in StateCommands)
        {
            Assert.False(
                OrielCapabilityRules.IsAlwaysAllowedAppCommand(name),
                $"{name} 会改变应用状态，必须由应用显式 Allow，不能进 always-allowed 名单");
        }

        // 对照：只读元信息仍然整组放行——否则这条断言可能因为"名单整个空了"而假通过。
        Assert.True(OrielCapabilityRules.IsAlwaysAllowedAppCommand("app.getName"));
    }

    [Theory]
    [InlineData("app.exit")]
    [InlineData("app.show")]
    [InlineData("app.hide")]
    public async Task WithoutCapability_IsRejectedAsNotAllowed(string command)
    {
        var window = CreateWindow();
        var (reply, _) = await TestHarness.DispatchAsync(
            CreateDispatcher(allowAppState: false), command, null, window: window);

        Assert.False(reply.RootElement.GetProperty("ok").GetBoolean());

        // 必须是 not_allowed（能力没声明），而不是 internal（库的实现炸了）：
        // 这两者的排障方向完全相反，混成一个码会把人带错方向。
        Assert.Equal("not_allowed", reply.RootElement.GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("app.exit")]
    [InlineData("app.show")]
    [InlineData("app.hide")]
    public async Task WithCapability_ReachesImplementation(string command)
    {
        var (reply, _) = await TestHarness.DispatchAsync(
            CreateDispatcher(allowAppState: true), command, null, window: CreateWindow());

        Assert.False(reply.RootElement.GetProperty("ok").GetBoolean());

        // 到了实现里才会抛的错（应用未运行）——证明门禁已经放行，
        // 且命令没有被"参数解析"或"未知命令"挡在前面。
        Assert.Contains("Run", reply.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Exit_WithCode_ZeroIsTreatedAsGiven()
    {
        // `0` 是合法退出码，不能被"falsy 就当没给"的写法吞掉。
        // 这里断言它不会在参数解析层被拒：真到实现里仍然是因为"应用未运行"而失败。
        var (reply, _) = await TestHarness.DispatchAsync(
            CreateDispatcher(allowAppState: true), "app.exit", new { code = 0 }, window: CreateWindow());

        Assert.False(reply.RootElement.GetProperty("ok").GetBoolean());
        Assert.DoesNotContain("参数", reply.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Exit_WithNonIntegerCode_IsTreatedAsAbsent()
    {
        // Tauri 的签名是 `exit(code?: number)`。给个字符串不是"崩溃"而是"没给"——
        // 退出这件事本身应当尽可能不被参数问题挡住（否则页面传错值就退不掉了）。
        var (reply, _) = await TestHarness.DispatchAsync(
            CreateDispatcher(allowAppState: true), "app.exit", new { code = "now" }, window: CreateWindow());

        Assert.False(reply.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("Run", reply.RootElement.GetProperty("error").GetString());
    }
}
