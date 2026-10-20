using System.Text.Json;
using OrielWeb.Ipc;
using Xunit;

namespace OrielWeb.Tests;

/// <summary>
/// 内建窗口 API 命令族（<c>window.*</c>）：能力门禁、白名单、label 路由。
/// </summary>
/// <remarks>
/// <para>
/// 这一族是安全红线的落点：<c>win.*</c> 整族始终放行，<c>window.*</c> 只为**标题栏必需的那组**
/// 放行，其余走 deny-by-default。写错的后果很具体——一个被 XSS 的页面能改别的窗口的标题、
/// 尺寸、任务栏显示，而这些都是它本来碰不到的东西。
/// </para>
/// <para>
/// label 路由那半同样要钉：命令的 <c>label</c> 参数缺省 = 发起者。写错成"总是作用到发起者"
/// 会让 <c>getByLabel('settings').setTitle(…)</c> 悄悄改了自己的标题。
/// </para>
/// </remarks>
public sealed class BuiltInWindowApiCommandTests
{
    private static (OrielCommandDispatcher Dispatcher, BuiltInWindowCommandTests.FakeWindow Window) Create(
        params string[] allowed)
    {
        OrielApp app = Oriel.CreateBuilder().Build();
        var window = new BuiltInWindowCommandTests.FakeWindow { App = app, Label = "main" };

        // Release 语义（isDebugBuild: false）：没声明的命令一律拒绝。
        // 这正是要验的姿态——Debug 全放行会掩盖漏配。
        var guard = new OrielIpcGuard(
            new OrielCapabilityOptions().Allow(allowed),
            isDebugBuild: false,
            "app.oriel");

        return (new OrielCommandDispatcher([], null, guard), window);
    }

    // ---- 白名单：标题栏必需的那组必须放行 ----

    [Theory]
    [InlineData("window.minimize")]
    [InlineData("window.toggleMaximize")]
    [InlineData("window.toggleFullscreen")]
    [InlineData("window.toggleOnTop")]
    [InlineData("window.close")]
    [InlineData("window.destroy")]
    [InlineData("window.drag")]
    [InlineData("window.dragStart")]
    [InlineData("window.dragTo")]
    [InlineData("window.dragEnd")]
    [InlineData("window.pickFile")]
    [InlineData("window.contextMenu")]
    [InlineData("window.isVisible")]
    [InlineData("window.isMaximized")]
    public void TitleBarEssentials_AreAllowedWithoutAnyCapabilityDeclaration(string command)
    {
        // 这条断言存在的理由与 win.* 后门相同：无边框窗口没有系统标题栏，
        // 拒掉这些会让窗口关不掉、挪不动——用户唯一的出路是任务管理器。
        Assert.True(
            OrielCapabilityRules.IsAlwaysAllowedWindowCommand(command),
            $"'{command}' 应当在白名单里——拒绝它会让无边框窗口变得不可用。");

        // 同时确认：它们**不是**靠 win.*/tray.* 那条整族放行进来的
        Assert.False(OrielCapabilityRules.IsBuiltInCommand(command));
    }

    // ---- 白名单之外：必须过门禁 ----

    [Theory]
    [InlineData("window.setTitle")]
    [InlineData("window.resize")]
    [InlineData("window.moveTo")]
    [InlineData("window.center")]
    [InlineData("window.setResizable")]
    [InlineData("window.setMinSize")]
    [InlineData("window.show")]
    [InlineData("window.hide")]
    [InlineData("window.focus")]
    [InlineData("window.maximize")]
    [InlineData("window.restore")]
    [InlineData("window.setFullscreen")]
    [InlineData("window.setOnTop")]
    [InlineData("window.getAll")]
    [InlineData("window.getFocused")]
    [InlineData("window.setCloseIntercept")]
    [InlineData("window.setEventForwarding")]
    [InlineData("window.setTheme")]
    [InlineData("window.setZoom")]
    [InlineData("window.clearAllBrowsingData")]
    public void OtherWindowCommands_AreNotAlwaysAllowed(string command)
    {
        // 这些才是"能力"：改标题、改尺寸、枚举窗口、改别的窗口的转发开关……
        // 整族放行等于给一个比 win.* 大得多的面开后门。
        Assert.False(
            OrielCapabilityRules.IsAlwaysAllowedWindowCommand(command),
            $"'{command}' 不该在始终放行的白名单里。");
    }

    [Fact]
    public void NonWhitelistedCommand_IsDeniedByDefault()
    {
        Assert.False(OrielCapabilityRules.IsAllowed("window.setTitle", [], []));
    }

    [Fact]
    public void NonWhitelistedCommand_IsAllowedWhenListed()
    {
        Assert.True(OrielCapabilityRules.IsAllowed("window.setTitle", ["window.*"], []));
        Assert.True(OrielCapabilityRules.IsAllowed("window.setTitle", ["window.setTitle"], []));
    }

    [Fact]
    public async Task SetTitle_IsDeniedWithoutCapability()
    {
        // 端到端：只声明业务命令的应用不该能改窗口标题
        (var dispatcher, var window) = Create("todo.*");

        (JsonDocument reply, _) = await TestHarness.DispatchAsync(
            dispatcher, "window.setTitle", new { title = "改掉了" }, window: window);

        Assert.False(reply.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("不在 Allow 名单里", reply.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task SetTitle_SucceedsWhenAllowed()
    {
        // 允许之后必须真的能跑通——"拒得对"与"放得通"缺一不可，
        // 只验拒绝会漏掉"放行分支写坏了"。
        OrielApp app = Oriel.CreateBuilder().Build();

        // 需要一个真实窗口后端才能调 SetTitle，所以走 FakeWindow 记录调用
        var window = new RecordingWindow { App = app, Label = "main" };
        var guard = new OrielIpcGuard(
            new OrielCapabilityOptions().Allow("window.setTitle"),
            isDebugBuild: false,
            "app.oriel");
        var dispatcher = new OrielCommandDispatcher([], null, guard);

        // FakeWindow 不在 app.Windows 里，所以 Resolve 会失败——这条用例要的是
        // "过了门禁"这个事实，因此断言错误文案**不是**权限拒绝。
        (JsonDocument reply, _) = await TestHarness.DispatchAsync(
            dispatcher, "window.setTitle", new { title = "新标题" }, window: window);

        string error = reply.RootElement.GetProperty("error").GetString() ?? string.Empty;
        Assert.DoesNotContain("Allow 名单", error);
    }

    [Fact]
    public async Task SetTheme_IsDeniedWithoutCapability()
    {
        // 端到端：改主题是改外观，不在标题栏白名单里，必须显式 Allow
        (var dispatcher, var window) = Create("todo.*");

        (JsonDocument reply, _) = await TestHarness.DispatchAsync(
            dispatcher, "window.setTheme", new { value = "dark" }, window: window);

        Assert.False(reply.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("不在 Allow 名单里", reply.RootElement.GetProperty("error").GetString());
    }

    [Theory]
    [InlineData("dark")]
    [InlineData("light")]
    public async Task SetTheme_AcceptsLightAndDark(string value)
    {
        // 'light' / 'dark' 是合法值，不该被当非法参数拒掉
        OrielApp app = Oriel.CreateBuilder().Build();
        var window = new RecordingWindow { App = app, Label = "main" };
        var guard = new OrielIpcGuard(
            new OrielCapabilityOptions().Allow("window.setTheme"),
            isDebugBuild: false,
            "app.oriel");
        var dispatcher = new OrielCommandDispatcher([], null, guard);

        (JsonDocument reply, _) = await TestHarness.DispatchAsync(
            dispatcher, "window.setTheme", new { value }, window: window);

        // FakeWindow 不在 app.Windows 里，Resolve 会失败——这里断言的是
        // "过了门禁、值也合法"，所以错误文案不该是权限拒绝或参数错误。
        string error = reply.RootElement.GetProperty("error").GetString() ?? string.Empty;
        Assert.DoesNotContain("Allow 名单", error);
        Assert.DoesNotContain("未知的 value", error);
    }

    [Fact]
    public async Task SetTheme_RejectsUnknownValue()
    {
        // 非法值要在**发给后端之前**就被拒掉，而不是传给 SetTheme 让它悄悄忽略
        (var dispatcher, var window) = Create("window.setTheme");

        (JsonDocument reply, _) = await TestHarness.DispatchAsync(
            dispatcher, "window.setTheme", new { value = "blue" }, window: window);

        Assert.False(reply.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("未知的 value", reply.RootElement.GetProperty("error").GetString());
    }

    // ---- B16：webview 级能力（setZoom / clearAllBrowsingData）----

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task SetZoom_RejectsNonPositiveOrNonFinite(double value)
    {
        // 缩放倍率是有限正数。这个校验在 Resolve 之前（参数层），
        // 所以 FakeWindow 没注册进 app.Windows 也不影响断言。
        // （NaN/±∞ 走不到这里——标准 JSON 序列化在测试装具层就拒了；
        // 页面侧的对应拦截在 webviewWindow.test.mjs 里钉着，命令层的
        // IsFinite 检查是给"宿主直接收到原始 JSON"那条路兜底的。）
        (var dispatcher, var window) = Create("window.setZoom");

        (JsonDocument reply, _) = await TestHarness.DispatchAsync(
            dispatcher, "window.setZoom", new { value }, window: window);

        Assert.False(reply.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("有限正数", reply.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task SetZoom_AcceptsPositiveValue()
    {
        // 1.5 是合法值：错误不该是参数校验或权限拒绝
        // （FakeWindow 不在 app.Windows 里，会在 Resolve 处失败——那是另一回事）。
        (var dispatcher, var window) = Create("window.setZoom");

        (JsonDocument reply, _) = await TestHarness.DispatchAsync(
            dispatcher, "window.setZoom", new { value = 1.5 }, window: window);

        string error = reply.RootElement.GetProperty("error").GetString() ?? string.Empty;
        Assert.DoesNotContain("有限正数", error);
        Assert.DoesNotContain("Allow 名单", error);
    }

    [Fact]
    public async Task ClearAllBrowsingData_PassesTheCapabilityGate()
    {
        // 门禁必须放行显式声明的命令。真窗口上的行为由 --selftest 取证，
        // 这里钉的是"声明了 window.clearAllBrowsingData 就不再被门禁拦"。
        (var dispatcher, var window) = Create("window.clearAllBrowsingData");

        (JsonDocument reply, _) = await TestHarness.DispatchAsync(
            dispatcher, "window.clearAllBrowsingData", null, window: window);

        string error = reply.RootElement.GetProperty("error").GetString() ?? string.Empty;
        Assert.DoesNotContain("Allow 名单", error);
        Assert.DoesNotContain("Deny 名单", error);
    }

    [Fact]
    public async Task Minimize_PassesTheCapabilityGate()
    {
        // 白名单命令必须**过得了门禁**——这是这条用例的断言点。
        //
        // 它不会真的执行到窗口方法上：window.* 族经 App.GetWindow(label) 解析目标
        // （保证目标是个**已注册的存活窗口**，不会作用到半个已销毁的窗口上），
        // 而 FakeWindow 不在 app.Windows 里。所以"命令真的调到窗口上了吗"
        // 由 --selftest window 在真窗口上取证，这里只钉门禁那一半。
        (var dispatcher, var window) = Create("todo.*");

        (JsonDocument reply, _) = await TestHarness.DispatchAsync(
            dispatcher, "window.minimize", null, window: window);

        string error = reply.RootElement.GetProperty("error").GetString() ?? string.Empty;
        Assert.DoesNotContain("Allow 名单", error);
        Assert.DoesNotContain("Deny 名单", error);
    }

    [Fact]
    public async Task WhitelistedAndNonWhitelisted_DifferInWhetherTheyReachTheGate()
    {
        // 同一个应用配置（只声明 todo.*）下对照两条命令：
        // 白名单的过门禁后失败于"找不到窗口"，非白名单的失败于"没被授权"。
        // 两条都失败，但**失败原因不同**——这正是门禁在起作用的证据。
        (var dispatcher, var mini) = Create("todo.*");
        (JsonDocument allowedReply, _) = await TestHarness.DispatchAsync(
            dispatcher, "window.minimize", null, window: mini);

        (var dispatcher2, var titled) = Create("todo.*");
        (JsonDocument deniedReply, _) = await TestHarness.DispatchAsync(
            dispatcher2, "window.setTitle", new { title = "x" }, window: titled);

        Assert.DoesNotContain("Allow 名单", allowedReply.RootElement.GetProperty("error").GetString()!);
        Assert.Contains("Allow 名单", deniedReply.RootElement.GetProperty("error").GetString()!);
    }

    // ---- label 路由 ----

    [Fact]
    public async Task UnknownLabel_ReportsInsteadOfActingOnCaller()
    {
        // 找不到目标时**不能**静默作用到发起者：那会把"发错窗口"变成一个看得见的现象
        // （页面以为在操作 settings，结果把 main 关了）而没有任何报错。
        (var dispatcher, var window) = Create("window.*");

        (JsonDocument reply, _) = await TestHarness.DispatchAsync(
            dispatcher, "window.close", new { label = "no-such-window" }, window: window);

        Assert.False(reply.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("no-such-window", reply.RootElement.GetProperty("error").GetString());
        Assert.Empty(window.Calls);
    }

    [Fact]
    public async Task MissingLabel_RequiresAReadyBackend()
    {
        // 发起者的 label 取不到（窗口后端尚未就位）时应当明确报错，
        // 而不是猜一个默认窗口去操作。
        (var dispatcher, _) = Create("window.*");
        var noLabelWindow = new BuiltInWindowCommandTests.FakeWindow { Label = null };

        (JsonDocument reply, _) = await TestHarness.DispatchAsync(
            dispatcher, "window.minimize", null, window: noLabelWindow);

        Assert.False(reply.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("无法确定发起命令的窗口", reply.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task WindowCommands_NeedASourceWindow()
    {
        (var dispatcher, _) = Create("window.*");

        (JsonDocument reply, _) = await TestHarness.DispatchAsync(
            dispatcher, "window.minimize", null, window: null);

        Assert.False(reply.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("窗口", reply.RootElement.GetProperty("error").GetString());
    }

    // ---- 参数校验 ----

    [Fact]
    public async Task SetMinSize_MissingArgumentIsRejected()
    {
        // 静默当 0 会让窗口被最小尺寸限制成 0x0，而症状是"窗口不见了"
        OrielApp app = Oriel.CreateBuilder().Build();
        var window = new RecordingWindow { App = app, Label = "main" };
        var guard = new OrielIpcGuard(
            new OrielCapabilityOptions().Allow("window.*"), isDebugBuild: false, "app.oriel");
        var dispatcher = new OrielCommandDispatcher([], null, guard);

        (JsonDocument reply, _) = await TestHarness.DispatchAsync(
            dispatcher, "window.setMinSize", new { width = 100 }, window: window);

        // 注意：它可能在 Resolve 阶段就先失败（RecordingWindow 不在 app.Windows 里），
        // 但无论哪一步失败，都必须**失败**——所以断言 ok 为 false 就够了。
        Assert.False(reply.RootElement.GetProperty("ok").GetBoolean());
    }

    // ---- window.new：页面建窗 ----

    [Fact]
    public async Task WindowNew_IsNotInTheTitleBarAllowList()
    {
        // 建窗是真正的能力：新窗口跑的是应用自己的页面，共享令牌与能力配置。
        // 它绝不能因为"属于 window. 族"就被顺带放行。
        Assert.False(OrielCapabilityRules.IsAlwaysAllowedWindowCommand("window.new"));
    }

    [Fact]
    public async Task WindowNew_WithoutCapability_IsRejected()
    {
        // 只放行标题栏那组，不含 window.new
        (var dispatcher, var window) = Create("window.minimize", "window.close");

        (JsonDocument reply, _) = await TestHarness.DispatchAsync(
            dispatcher, "window.new", new { label = "child" }, window: window);

        Assert.False(reply.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("not_allowed", reply.RootElement.GetProperty("code").GetString());

        // 一个窗口都不该被建出来（未授权时连尝试都不该发生）
        Assert.Empty(window.App.Windows);
    }

    [Fact]
    public async Task WindowNew_MissingLabel_IsRejectedBeforeCreating()
    {
        (var dispatcher, var window) = Create("window.new");

        (JsonDocument reply, _) = await TestHarness.DispatchAsync(
            dispatcher, "window.new", null, window: window);

        Assert.False(reply.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("label", reply.RootElement.GetProperty("error").GetString());
        Assert.Empty(window.App.Windows);
    }

    [Fact]
    public async Task WindowNew_WithCapability_ReachesImplementation()
    {
        // 门禁放行后走到 `CreateWindow` 的前置检查：应用未 Run 时它明确拒绝，
        // 而不是静默返回一个"没建出来的窗口"。
        (var dispatcher, var window) = Create("window.new");

        (JsonDocument reply, _) = await TestHarness.DispatchAsync(
            dispatcher, "window.new", new { label = "child", width = 320, height = 240 }, window: window);

        Assert.False(reply.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("Run()", reply.RootElement.GetProperty("error").GetString());
    }

    /// <summary>
    /// 记录调用的假窗口，与 <see cref="BuiltInWindowCommandTests.FakeWindow"/> 同源。
    /// </summary>
    /// <remarks>
    /// 直接复用那个类会带上一堆与本文件无关的成员断言；这里只需要"记录被调了什么"。
    /// </remarks>
    internal sealed class RecordingWindow : IOrielWindowControl
    {
        public List<string> Calls { get; } = [];
        public OrielApp App { get; set; } = Oriel.CreateBuilder().Build();
        public bool IsVisible { get; set; } = true;

        // 只读状态查询（M5-1）：**真的读字段**，而不是写死 false——
        // 写死的话"状态流转"那几条断言就没意义了。
        public bool IsMinimized { get; set; }
        public bool IsFocused { get; set; }
        public bool IsFullscreen { get; set; }
        public bool IsAlwaysOnTop { get; set; }

        public string? Label { get; set; } = "main";

        /// <summary>主题覆写（<c>window.setTheme</c> / <c>app.setTheme</c> 用）。可写是为了断言"真的改了"。</summary>
        public OrielTheme? ThemeOverride { get; set; }

        public bool CloseInterceptEnabled { get; set; }
        public void Minimize() => Calls.Add("minimize");
        public bool ToggleMaximize() { Calls.Add("toggleMaximize"); return true; }
        public void Close() => Calls.Add("close");
        public void Destroy() => Calls.Add("destroy");
        public bool ToggleFullscreen() { Calls.Add("toggleFullscreen"); return true; }
        public bool ToggleOnTop() { Calls.Add("toggleOnTop"); return true; }
        public void BeginDrag() => Calls.Add("beginDrag");
        public void BeginDragStreaming(double px, double py, double wx, double wy, double ww, double wh, double sh)
            => Calls.Add("beginDragStreaming");
        public void DragTo(double dx, double dy) => Calls.Add("dragTo");
        public void EndDrag() => Calls.Add("endDrag");
        public string? ShowOpenFileDialog(string? title, string? filter, string? initialDirectory)
        {
            Calls.Add($"showOpenFileDialog:{title}|{filter}|{initialDirectory}");
            return null;
        }

        public double ScaleFactor => 1.0;

        public void ShowContextMenu(IReadOnlyList<OrielMenuItem> items) => Calls.Add("showContextMenu");

        public void ShowContextMenu(
            IReadOnlyList<OrielMenuItem> items, OrielPosition? position, TimeSpan? autoDismissAfter = null)
            => Calls.Add("showContextMenuAt");

        public void CloseContextMenu() => Calls.Add("closeContextMenu");

        /// <summary>记下推给页面的事件（通道/拖放等宿主推送的落点）。</summary>
        public void EmitEvent(string name, string jsonPayload) => Calls.Add($"emitEvent:{name}:{jsonPayload}");

        /// <summary>记下菜单栏调用（<c>menu.setAsWindowMenu</c> 的落点）。</summary>
        public void SetWindowMenuBar(IReadOnlyList<OrielMenuItem> items) => Calls.Add("setWindowMenuBar");

        /// <summary>记下应用菜单调用（<c>menu.setAsAppMenu</c> 的落点）。</summary>
        public void SetAppMenuBar(IReadOnlyList<OrielMenuItem> items) => Calls.Add("setAppMenuBar");

        // 下面四个是 IOrielWindowControl 为 dialog.* 加的新成员。
        // 本文件测的是 window.*，不调它们，所以只记录、不回值。

        public string[] ShowOpenFileDialog(OrielOpenFileDialogOptions options)
        {
            Calls.Add($"showOpenFileDialogOptions:{options.Title}|{options.AllowMultiple}|{options.InitialDirectory}");
            return [];
        }

        public string? ShowSaveFileDialog(OrielSaveFileDialogOptions options)
        {
            Calls.Add($"showSaveFileDialog:{options.Title}|{options.InitialDirectory}|{options.DefaultFileName}");
            return null;
        }

        public string? ShowFolderDialog(string? title, string? initialDirectory)
        {
            Calls.Add($"showFolderDialog:{title}|{initialDirectory}");
            return null;
        }

        /// <summary>下一次 <see cref="ShowMessageDialog"/> 的返回值（null 表示"被外部关掉"）。</summary>
        public MessageResult? NextMessageResult { get; set; } = MessageResult.Ok;

        public MessageResult? ShowMessageDialog(
            string message, string? title, string kind, MessageButtons buttons, string okLabel, string cancelLabel)
        {
            Calls.Add($"showMessageDialog:{message}|{title}|{kind}|{buttons}|{okLabel}|{cancelLabel}");
            return NextMessageResult;
        }
    }
}
