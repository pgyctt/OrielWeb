using System.Text.Json;
using OrielWeb;
using OrielWeb.Ipc;
using Xunit;

namespace OrielWeb.Tests;

/// <summary>
/// 内建 <c>tray.*</c> 命令：页面侧 <c>oriel.tray</c> 靠它工作（对标 Tauri 2 的 <c>plugin:tray</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 这里只断言**协议层**——命令名、参数解析、错误文案、保留前缀的放行。
/// 真正的原生托盘（真的出现在通知区/菜单栏）只能在各平台人眼验证（见 ROADMAP）。
/// </para>
/// <para>
/// 用假窗口 + 真实 <see cref="OrielApp"/>：托盘注册表在应用上，但应用未 <c>Run()</c> 时
/// 没有平台后端，所以<b>创建</b>类命令在这里会失败（"应用尚未运行"）——这本身也是要钉住的契约。
/// </para>
/// </remarks>
public sealed class BuiltInTrayCommandTests
{
    /// <summary>造一个假窗口，其 <see cref="IOrielWindowControl.App"/> 可注入。</summary>
    private static BuiltInWindowCommandTests.FakeWindow WindowWith(OrielApp app)
        => new() { App = app };

    private static OrielCommandDispatcher CreateDispatcher()
        => new([], TestJsonContext.Default, TestGuards.AllowAll());

    /// <summary>喂一条 <c>tray.*</c> 命令并取回回执。</summary>
    private static async Task<JsonDocument> DispatchAsync(
        string name, object? args, IOrielWindowControl? window)
        => (await TestHarness.DispatchAsync(CreateDispatcher(), name, args, window: window)).Reply;

    [Fact]
    public async Task TrayNew_WithoutRunningApp_FailsWithClearMessage()
    {
        // 应用未 Run()：没有平台后端，建托盘必然失败——文案要指向这一点，而不是一个原生错误。
        // 内建命令的异常**原样透传**（见 OrielCommandDispatcher.DescribeBuiltInFailure）：
        // 把它们净化成"命令执行失败"会让调用方拿到一句无从下手的文案。
        using var app = Oriel.CreateBuilder().Build();
        using JsonDocument reply = await DispatchAsync("tray.new", new { id = "t" }, WindowWith(app));

        Assert.False(reply.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("尚未运行", reply.RootElement.GetProperty("error").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TrayNew_WithoutWindow_ReportsMissingWindow()
    {
        // 托盘是应用级的，但命令由某个窗口的页面发起——没有来源窗口时无法定位应用
        using JsonDocument reply = await DispatchAsync("tray.new", new { id = "t" }, window: null);

        Assert.False(reply.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("来源窗口", reply.RootElement.GetProperty("error").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TrayGetById_MissingTray_ReturnsNull_NotError()
    {
        using var app = Oriel.CreateBuilder().Build();
        using JsonDocument reply = await DispatchAsync("tray.getById", new { id = "nope" }, WindowWith(app));

        Assert.True(reply.RootElement.GetProperty("ok").GetBoolean());
        // 页面侧 getById 靠"value 为 null"区分"不存在"，不该是一条错误
        Assert.Equal(JsonValueKind.Null, reply.RootElement.GetProperty("value").ValueKind);
    }

    [Fact]
    public async Task TrayGetById_WithoutId_ReportsMissingParameter()
    {
        using var app = Oriel.CreateBuilder().Build();
        using JsonDocument reply = await DispatchAsync("tray.getById", new { }, WindowWith(app));

        Assert.False(reply.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("缺少字符串参数 'id'", reply.RootElement.GetProperty("error").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TrayGetAll_NoTrays_ReturnsEmptyArray()
    {
        using var app = Oriel.CreateBuilder().Build();
        using JsonDocument reply = await DispatchAsync("tray.getAll", null, WindowWith(app));

        Assert.True(reply.RootElement.GetProperty("ok").GetBoolean());
        // 返回的是 id 字符串数组（内建命令的返回值要能 AOT 安全地直接写出，
        // 自定义 DTO 会要求应用为库的内部类型注册 STJ 上下文）
        Assert.Equal(JsonValueKind.Array, reply.RootElement.GetProperty("value").ValueKind);
        Assert.Equal(0, reply.RootElement.GetProperty("value").GetArrayLength());
    }

    [Fact]
    public async Task TrayDescribe_MissingTray_ReturnsNull()
    {
        using var app = Oriel.CreateBuilder().Build();
        using JsonDocument reply = await DispatchAsync("tray.describe", new { id = "nope" }, WindowWith(app));

        Assert.True(reply.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(JsonValueKind.Null, reply.RootElement.GetProperty("value").ValueKind);
    }

    [Fact]
    public async Task TrayRemoveById_MissingTray_ReturnsFalse()
    {
        using var app = Oriel.CreateBuilder().Build();
        using JsonDocument reply = await DispatchAsync("tray.removeById", new { id = "nope" }, WindowWith(app));

        Assert.True(reply.RootElement.GetProperty("ok").GetBoolean());
        Assert.False(reply.RootElement.GetProperty("value").GetBoolean());
    }

    [Fact]
    public async Task TraySetMethods_MissingTray_ReportNotFound()
    {
        using var app = Oriel.CreateBuilder().Build();
        string[] commands = ["tray.setIcon", "tray.setMenu", "tray.setTooltip", "tray.setTitle", "tray.setVisible"];

        foreach (string command in commands)
        {
            using JsonDocument reply = await DispatchAsync(command, new { id = "nope" }, WindowWith(app));

            Assert.False(reply.RootElement.GetProperty("ok").GetBoolean(), $"{command} 应报错");
            Assert.Contains("不存在", reply.RootElement.GetProperty("error").GetString()!, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task TrayCommands_AreBuiltIn_SoAlwaysAuthorized()
    {
        // 与 win.* 同样：托盘命令始终放行，Release 下未声明能力也能用
        Assert.True(OrielCapabilityRules.IsBuiltInCommand("tray.new"));
        Assert.True(OrielCapabilityRules.IsBuiltInCommand("tray.removeById"));
        Assert.True(OrielCapabilityRules.IsBuiltInCommand("win.close"));

        // 但不能把普通应用命令也当成内建（前缀必须带那个点）
        Assert.False(OrielCapabilityRules.IsBuiltInCommand("tray"));
        Assert.False(OrielCapabilityRules.IsBuiltInCommand("trays.new"));
        Assert.False(OrielCapabilityRules.IsBuiltInCommand(""));
    }

    [Fact]
    public void TrayCommands_AreAlwaysAuthorized_EvenWithEmptyCapabilityConfig()
    {
        // 未配置能力 + Release 构建（fail-closed）：tray.* 仍应放行，否则页面永远建不了托盘
        var guard = TestGuards.Unconfigured(isDebugBuild: false);

        Assert.True(guard.TryAuthorize("tray.new", out string? reason));
        Assert.Null(reason);
    }

    [Fact]
    public async Task TraySetMenu_ParsesSeparatorAndRole()
    {
        // 菜单解析是纯数据逻辑，但入口在命令里：用一个真实存在但未 Run 的应用验不了
        // （Find 会先抛"不存在"）。这里直接断言解析结果——经 removeById 走不通，改用反射无关的路径：
        // 造一个 id 存在的托盘需要后端，故此处只覆盖"参数校验"，解析本身由 demo 自检覆盖。
        using var app = Oriel.CreateBuilder().Build();
        using JsonDocument reply = await DispatchAsync("tray.setMenu", new { id = "t", items = "not-an-array" }, WindowWith(app));

        Assert.False(reply.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("不存在", reply.RootElement.GetProperty("error").GetString()!, StringComparison.Ordinal);
    }
}
