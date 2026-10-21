using System.Text.Json;
using OrielWeb.Ipc;
using Xunit;

namespace OrielWeb.Tests;

/// <summary>
/// 光标六条命令（B21）：注册、门禁、参数校验，以及名字解析表本身。
/// </summary>
/// <remarks>
/// <para>
/// 正向路径（真的把光标挪了/hide 了）不在这里验：那需要真窗口与真桌面，由
/// <c>samples/OrielDemo</c> 的 WINDOW-SELFTEST 在三平台取证。这里钉的是**参数层**——
/// 名字拼错、布尔参数缺失、方向写错，这些在页面侧是编译错误、在 IPC 上却是运行期字符串，
/// 只能靠宿主校验兜住。
/// </para>
/// <para>
/// 校验刻意放在 <c>Resolve</c> 之前：拼错的图标名不该先去找窗口，
/// 否则"图标名错了"会被报成"找不到窗口"。
/// </para>
/// </remarks>
public sealed class CursorCommandTests
{
    private static (OrielCommandDispatcher Dispatcher, BuiltInWindowCommandTests.FakeWindow Window) Create(
        params string[] allowed)
    {
        OrielApp app = Oriel.CreateBuilder().Build();
        var window = new BuiltInWindowCommandTests.FakeWindow { App = app, Label = "main" };

        var guard = new OrielIpcGuard(
            new OrielCapabilityOptions().Allow(allowed),
            isDebugBuild: false,
            "app.oriel");

        return (new OrielCommandDispatcher([], null, guard), window);
    }

    // ---- 注册与门禁 ----

    [Theory]
    [InlineData("window.setCursorGrab")]
    [InlineData("window.setCursorVisible")]
    [InlineData("window.setCursorIcon")]
    [InlineData("window.setCursorPosition")]
    [InlineData("window.setIgnoreCursorEvents")]
    [InlineData("window.startResizeDragging")]
    public void CommandsAreRegistered(string name)
        => Assert.True(OrielBuiltInWindowApiCommands.TryGet(name, out _), $"未注册：{name}");

    [Theory]
    [InlineData("window.setCursorGrab")]
    [InlineData("window.setCursorVisible")]
    [InlineData("window.setCursorIcon")]
    [InlineData("window.setCursorPosition")]
    [InlineData("window.setIgnoreCursorEvents")]
    [InlineData("window.startResizeDragging")]
    public void CursorCommandsAreNotInTheTitleBarWhitelist(string name)
    {
        // 光标不是"画标题栏非要不可"的能力：被注入的页面能锁死光标或让窗口点击穿透，
        // 用户会以为应用坏了。所以这六条都要显式 Allow。
        Assert.False(OrielCapabilityRules.IsAlwaysAllowedWindowCommand(name), name);
    }

    [Fact]
    public async Task CursorCommandIsRejectedWithoutDeclaration()
    {
        (var dispatcher, var window) = Create();

        (JsonDocument reply, _) = await TestHarness.DispatchAsync(
            dispatcher, "window.setCursorVisible", new { visible = false }, window: window);

        Assert.False(reply.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(
            OrielIpcErrorCodes.NotAllowed,
            reply.RootElement.GetProperty("code").GetString());
    }

    // ---- 参数校验 ----

    [Fact]
    public async Task UnknownIconIsRejectedBeforeResolvingTheWindow()
    {
        (var dispatcher, var window) = Create("window.setCursorIcon");

        (JsonDocument reply, _) = await TestHarness.DispatchAsync(
            dispatcher, "window.setCursorIcon", new { icon = "pointer" }, window: window);

        Assert.False(reply.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(
            OrielIpcErrorCodes.InvalidArgument,
            reply.RootElement.GetProperty("code").GetString());

        // 文案要点出正确的写法（CSS 名的 camelCase），否则调用方只会去猜
        Assert.Contains("camelCase", reply.RootElement.GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingIconIsAMissingArgument()
    {
        (var dispatcher, var window) = Create("window.setCursorIcon");

        (JsonDocument reply, _) = await TestHarness.DispatchAsync(
            dispatcher, "window.setCursorIcon", new { }, window: window);

        Assert.Equal(
            OrielIpcErrorCodes.MissingArgument,
            reply.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task UnknownResizeDirectionIsRejected()
    {
        (var dispatcher, var window) = Create("window.startResizeDragging");

        (JsonDocument reply, _) = await TestHarness.DispatchAsync(
            dispatcher, "window.startResizeDragging", new { direction = "left" }, window: window);

        Assert.Equal(
            OrielIpcErrorCodes.InvalidArgument,
            reply.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task CursorPositionRejectsNonNumericCoordinates()
    {
        (var dispatcher, var window) = Create("window.setCursorPosition");

        (JsonDocument reply, _) = await TestHarness.DispatchAsync(
            dispatcher, "window.setCursorPosition", new { position = new { x = "10", y = 20 } }, window: window);

        // 参数层用 RequiredDouble（与 window.resize 等同一套助手）——它抛的异常不带 code，
        // 所以这里只断言"被拒了 + 文案点到了那个参数名"
        Assert.False(reply.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("x", reply.RootElement.GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("window.setCursorGrab", "grab")]
    [InlineData("window.setCursorVisible", "visible")]
    [InlineData("window.setIgnoreCursorEvents", "ignore")]
    public async Task BooleanCommandsRequireARealBoolean(string command, string property)
    {
        (var dispatcher, var window) = Create(command);

        // 字符串 "false" 是 truthy —— 静默接受会把"隐藏光标"变成"显示光标"
        string args = $"{{\"{property}\":\"false\"}}";
        using var doc = JsonDocument.Parse(args);

        (JsonDocument reply, _) = await TestHarness.DispatchAsync(
            dispatcher, command, doc.RootElement, window: window);

        Assert.False(reply.RootElement.GetProperty("ok").GetBoolean());
    }

    // ---- 名字表（纯函数） ----

    [Fact]
    public void EveryTauriCursorNameParses()
    {
        string[] names =
        [
            "default", "crosshair", "hand", "arrow", "move", "text", "wait", "help", "progress",
            "notAllowed", "contextMenu", "cell", "verticalText", "alias", "copy", "noDrop", "grab",
            "grabbing", "allScroll", "zoomIn", "zoomOut", "eResize", "nResize", "neResize", "nwResize",
            "sResize", "seResize", "swResize", "wResize", "ewResize", "nsResize", "neswResize",
            "nwseResize", "colResize", "rowResize",
        ];

        Assert.Equal(35, names.Length);

        foreach (string name in names)
        {
            Assert.True(OrielCursorNames.TryParse(name, out OrielCursorIcon icon), name);

            // GTK 名字必须是非空的 CSS 名（映射表漏了值会落到 default）
            Assert.False(string.IsNullOrWhiteSpace(OrielCursorNames.ToGdkName(icon)), name);
        }
    }

    [Theory]
    [InlineData("pointer")]      // CSS 叫 pointer，Tauri 叫 hand
    [InlineData("Default")]      // 大小写敏感
    [InlineData("not-allowed")]  // 那是 GTK 名，不是 Tauri 名
    [InlineData("")]
    [InlineData(null)]
    public void UnknownCursorNamesAreRejected(string? name)
        => Assert.False(OrielCursorNames.TryParse(name, out _));

    [Theory]
    [InlineData("East")]
    [InlineData("North")]
    [InlineData("NorthEast")]
    [InlineData("NorthWest")]
    [InlineData("South")]
    [InlineData("SouthEast")]
    [InlineData("SouthWest")]
    [InlineData("West")]
    public void EveryResizeDirectionParses(string name)
        => Assert.True(OrielCursorNames.TryParseDirection(name, out _), name);

    [Theory]
    [InlineData("east")]     // 大小写敏感（Tauri 的枚举值是首字母大写）
    [InlineData("e-resize")]
    [InlineData("")]
    public void UnknownResizeDirectionsAreRejected(string name)
        => Assert.False(OrielCursorNames.TryParseDirection(name, out _));

    [Theory]
    [InlineData(OrielCursorIcon.NotAllowed, "not-allowed")]
    [InlineData(OrielCursorIcon.EwResize, "ew-resize")]
    [InlineData(OrielCursorIcon.NeswResize, "nesw-resize")]
    [InlineData(OrielCursorIcon.VerticalText, "vertical-text")]
    [InlineData(OrielCursorIcon.Hand, "pointer")]
    public void GdkNamesFollowTheCssSpelling(OrielCursorIcon icon, string expected)
        => Assert.Equal(expected, OrielCursorNames.ToGdkName(icon));
}
