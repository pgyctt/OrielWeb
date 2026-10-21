using System.Text.Json;
using OrielWeb.Ipc;
using Xunit;

namespace OrielWeb.Tests;

/// <summary>
/// 平台开关 / 特效 / 任务栏（B22）：注册、门禁、参数校验与名字解析表。
/// </summary>
/// <remarks>
/// <para>
/// 与 <see cref="CursorCommandTests"/> 同一姿态：**正向路径不在这里验**——"真的把窗口
/// 压到底了"需要真窗口与真桌面，由 <c>samples/OrielDemo</c> 的 WINDOW-SELFTEST 在三平台取证。
/// 这里钉的是参数层：材质名拼错、状态名写错、布尔参数给了字符串，这些在页面侧是编译错误、
/// 到了 IPC 上却是运行期字符串。
/// </para>
/// <para>
/// 校验刻意放在 <c>Resolve</c> 之前：拼错的材质名不该先去找窗口，否则"材质名错了"
/// 会被报成"找不到窗口"——两个完全不同的问题，修法也不同。
/// </para>
/// </remarks>
public sealed class WindowVisualCommandTests
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

    /// <summary>B22 的十九条命令（十条平台开关 + 两条特效 + 四条任务栏 + 三条带类型参数的）。</summary>
    private static readonly string[] Commands =
    [
        "window.setSkipTaskbar",
        "window.setVisibleOnAllWorkspaces",
        "window.setContentProtected",
        "window.setSimpleFullscreen",
        "window.setFullscreenOnMonitor",
        "window.setTitleBarStyle",
        "window.setShadow",
        "window.setAlwaysOnBottom",
        "window.setFocusable",
        "window.setDecorations",
        "window.setEffects",
        "window.clearEffects",
        "window.setProgressBar",
        "window.setOverlayIcon",
        "window.setBadgeCount",
        "window.setBadgeLabel",
    ];

    // ---- 注册与门禁 ----

    [Theory]
    [MemberData(nameof(CommandNames))]
    public void CommandsAreRegistered(string name)
        => Assert.True(OrielBuiltInWindowApiCommands.TryGet(name, out _), $"未注册：{name}");

    [Theory]
    [MemberData(nameof(CommandNames))]
    public void CommandsAreNotInTheTitleBarWhitelist(string name)
    {
        // 没有一条是"画标题栏非要不可"的能力：隐藏任务栏按钮、改截屏保护、把窗口压到底、
        // 在任务栏上画进度条——被注入的页面若能随手改这些，用户会以为应用坏了。
        Assert.False(OrielCapabilityRules.IsAlwaysAllowedWindowCommand(name), name);
    }

    public static TheoryData<string> CommandNames()
    {
        var data = new TheoryData<string>();
        foreach (string name in Commands)
        {
            data.Add(name);
        }

        return data;
    }

    [Fact]
    public async Task EffectCommandIsRejectedWithoutDeclaration()
    {
        (var dispatcher, var window) = Create();

        (JsonDocument reply, _) = await TestHarness.DispatchAsync(
            dispatcher, "window.setEffects", new { value = new { effects = new[] { "mica" } } }, window: window);

        Assert.False(reply.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(
            OrielIpcErrorCodes.NotAllowed,
            reply.RootElement.GetProperty("code").GetString());
    }

    // ---- 参数校验 ----

    [Fact]
    public async Task UnknownEffectIsRejectedBeforeResolvingTheWindow()
    {
        (var dispatcher, var window) = Create("window.setEffects");

        (JsonDocument reply, _) = await TestHarness.DispatchAsync(
            dispatcher,
            "window.setEffects",
            new { value = new { effects = new[] { "vibrancy" } } },
            window: window);

        Assert.False(reply.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(
            OrielIpcErrorCodes.InvalidArgument,
            reply.RootElement.GetProperty("code").GetString());

        // 文案要点出"去哪看取值"，否则调用方只能猜
        Assert.Contains("docs/API.md", reply.RootElement.GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptyEffectListIsRejected()
    {
        (var dispatcher, var window) = Create("window.setEffects");

        (JsonDocument reply, _) = await TestHarness.DispatchAsync(
            dispatcher, "window.setEffects", new { value = new { effects = Array.Empty<string>() } }, window: window);

        Assert.Equal(
            OrielIpcErrorCodes.InvalidArgument,
            reply.RootElement.GetProperty("code").GetString());
        Assert.Contains("clearEffects", reply.RootElement.GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownEffectStateIsRejected()
    {
        (var dispatcher, var window) = Create("window.setEffects");

        (JsonDocument reply, _) = await TestHarness.DispatchAsync(
            dispatcher,
            "window.setEffects",
            new { value = new { effects = new[] { "mica" }, state = "always" } },
            window: window);

        Assert.Equal(
            OrielIpcErrorCodes.InvalidArgument,
            reply.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task UnknownTitleBarStyleIsRejected()
    {
        (var dispatcher, var window) = Create("window.setTitleBarStyle");

        (JsonDocument reply, _) = await TestHarness.DispatchAsync(
            dispatcher, "window.setTitleBarStyle", new { value = "hidden" }, window: window);

        Assert.Equal(
            OrielIpcErrorCodes.InvalidArgument,
            reply.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task UnknownProgressStatusIsRejected()
    {
        (var dispatcher, var window) = Create("window.setProgressBar");

        (JsonDocument reply, _) = await TestHarness.DispatchAsync(
            dispatcher, "window.setProgressBar", new { value = new { status = "done" } }, window: window);

        Assert.Equal(
            OrielIpcErrorCodes.InvalidArgument,
            reply.RootElement.GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("window.setSkipTaskbar", "skip")]
    [InlineData("window.setVisibleOnAllWorkspaces", "visible")]
    [InlineData("window.setContentProtected", "contentProtected")]
    [InlineData("window.setSimpleFullscreen", "fullscreen")]
    [InlineData("window.setShadow", "enable")]
    [InlineData("window.setAlwaysOnBottom", "alwaysOnBottom")]
    [InlineData("window.setFocusable", "focusable")]
    [InlineData("window.setDecorations", "decorations")]
    public async Task BooleanCommandsRequireARealBoolean(string command, string property)
    {
        (var dispatcher, var window) = Create(command);

        // 字符串 "false" 是 truthy：静默接受会把"隐藏任务栏"变成"显示任务栏"
        string args = $"{{\"value\":\"false\",\"{property}\":\"false\"}}";
        using var doc = JsonDocument.Parse(args);

        (JsonDocument reply, _) = await TestHarness.DispatchAsync(
            dispatcher, command, doc.RootElement, window: window);

        // 布尔层用 BoolValue（与既有 setMaximizable 等同一套助手）——它抛的异常不带 code，
        // 所以这里只断言"被拒了 + 文案点到了那个参数名"
        Assert.False(reply.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains(
            property,
            reply.RootElement.GetProperty("error").GetString(),
            StringComparison.Ordinal);
    }

    // ---- 名字表（纯函数，与平台无关） ----

    [Fact]
    public void EveryTauriEffectNameParses()
    {
        string[] names =
        [
            "appearanceBased", "light", "dark", "mediumLight", "ultraDark", "titlebar", "selection",
            "menu", "popover", "sidebar", "headerView", "sheet", "windowBackground", "hudWindow",
            "fullScreenUI", "tooltip", "contentBackground", "underWindowBackground",
            "underPageBackground", "liquidGlassRegular", "liquidGlassClear", "mica", "blur",
            "acrylic", "tabbed", "tabbedDark", "tabbedLight",
        ];

        // 与 Tauri 的 Effect 枚举逐项对齐（27 个）。数量写死是为了让"漏了一个"必然红。
        Assert.Equal(27, names.Length);

        foreach (string name in names)
        {
            Assert.True(OrielEffectNames.TryParseEffect(name, out _), name);
        }
    }

    [Theory]
    [InlineData("Mica")]          // 大小写敏感（Tauri 的取值是 camelCase）
    [InlineData("micaDark")]      // Tauri 没有这个值
    [InlineData("vibrancy")]
    [InlineData("")]
    [InlineData(null)]
    public void UnknownEffectNamesAreRejected(string? name)
        => Assert.False(OrielEffectNames.TryParseEffect(name, out _));

    [Theory]
    [InlineData("followsWindowActiveState", OrielEffectState.FollowsWindowActiveState)]
    [InlineData("active", OrielEffectState.Active)]
    [InlineData("inactive", OrielEffectState.Inactive)]
    public void EveryTauriEffectStateParses(string name, OrielEffectState expected)
    {
        Assert.True(OrielEffectNames.TryParseEffectState(name, out OrielEffectState state));
        Assert.Equal(expected, state);
    }

    [Theory]
    [InlineData("visible", OrielTitleBarStyle.Visible)]
    [InlineData("transparent", OrielTitleBarStyle.Transparent)]
    [InlineData("overlay", OrielTitleBarStyle.Overlay)]
    public void EveryTauriTitleBarStyleParses(string name, OrielTitleBarStyle expected)
    {
        Assert.True(OrielEffectNames.TryParseTitleBarStyle(name, out OrielTitleBarStyle style));
        Assert.Equal(expected, style);
    }

    [Theory]
    [InlineData("none", OrielProgressBarStatus.None)]
    [InlineData("normal", OrielProgressBarStatus.Normal)]
    [InlineData("indeterminate", OrielProgressBarStatus.Indeterminate)]
    [InlineData("paused", OrielProgressBarStatus.Paused)]
    [InlineData("error", OrielProgressBarStatus.Error)]
    public void EveryTauriProgressBarStatusParses(string name, OrielProgressBarStatus expected)
    {
        Assert.True(OrielEffectNames.TryParseProgressBarStatus(name, out OrielProgressBarStatus status));
        Assert.Equal(expected, status);
    }

    [Fact]
    public void ProgressBarStatusMatchesTheConstObjectExportedToPages()
    {
        // 页面侧的常量对象（packages/api/src/window.js 的 ProgressBarStatus）与这里的枚举
        // 是**两份**定义：页面侧发字符串、宿主侧解析。两者漂移的表现是"某个状态在页面上写得出、
        // 到宿主变成 invalid_argument"，而那条错误在真机上很难归因。
        string[] names = ["none", "normal", "indeterminate", "paused", "error"];
        Assert.Equal(names.Length, Enum.GetValues<OrielProgressBarStatus>().Length);

        foreach (string name in names)
        {
            Assert.True(OrielEffectNames.TryParseProgressBarStatus(name, out _), name);
        }
    }
}
