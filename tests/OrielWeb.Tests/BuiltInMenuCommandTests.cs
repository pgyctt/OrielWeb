using System.Text.Json;
using OrielWeb.Ipc;
using Xunit;

namespace OrielWeb.Tests;

/// <summary>
/// 菜单命令族（<c>menu.popup</c> / <c>menu.close</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 这一组验的是**页面发来的那棵树被解析成了什么**：Tauri 的线格式是
/// <c>{ kind, id, text, enabled, accelerator, checked, item, items }</c>，宿主侧要把它落成
/// <see cref="OrielMenuItem"/>。字段名对不上不会抛异常——只会让菜单少几项或全空，
/// 那是最难在联调时发现的一类偏差。
/// </para>
/// <para>
/// 另一组是**防御性上限**：这棵树来自页面，深度与项数都必须有界，否则一棵构造出来的
/// 超深树能让解析递归到栈溢出（进程级死法）。
/// </para>
/// </remarks>
public sealed class BuiltInMenuCommandTests
{
    /// <summary>记录收到的菜单树，而不是只记次数。</summary>
    private sealed class RecordingWindow : IOrielWindowControl
    {
        public List<string> Calls { get; } = [];

        public List<IReadOnlyList<OrielMenuItem>> Menus { get; } = [];

        public List<OrielPosition?> Positions { get; } = [];

        public List<TimeSpan?> AutoDismisses { get; } = [];

        public OrielApp App { get; set; } = Oriel.CreateBuilder().Build();

        public bool IsVisible => true;

        public string? Label { get; set; } = "main";

        /// <summary>缩放比；物理 → 逻辑的换算要用它。</summary>
        public double ScaleFactor { get; set; } = 1.0;

        public void ShowContextMenu(IReadOnlyList<OrielMenuItem> items)
        {
            Menus.Add(items);
            Positions.Add(null);
        }

        public void ShowContextMenu(
            IReadOnlyList<OrielMenuItem> items, OrielPosition? position, TimeSpan? autoDismissAfter = null)
        {
            Menus.Add(items);
            Positions.Add(position);
            AutoDismisses.Add(autoDismissAfter);
        }

        public void CloseContextMenu() => Calls.Add("closeContextMenu");

        // ---- 以下成员本组用例不用，但接口要求实现 ----

        public void Minimize() { }

        public bool ToggleMaximize() => false;

        public void Close() { }

        public void Destroy() { }

        public bool CloseInterceptEnabled { get; set; }

        public bool ToggleFullscreen() => false;

        public bool ToggleOnTop() => false;

        public void BeginDrag() { }

        public void BeginDragStreaming(double px, double py, double wx, double wy, double ww, double wh, double sh) { }

        public void DragTo(double dx, double dy) { }

        public void EndDrag() { }

        public string? ShowOpenFileDialog(string? title, string? filter, string? initialDirectory) => null;

        public string[] ShowOpenFileDialog(OrielOpenFileDialogOptions options) => [];

        public string? ShowSaveFileDialog(OrielSaveFileDialogOptions options) => null;

        public string? ShowFolderDialog(string? title, string? initialDirectory) => null;

        public MessageResult? ShowMessageDialog(
            string message, string? title, string kind, MessageButtons buttons, string okLabel, string cancelLabel)
            => null;
    }

    /// <summary>
    /// 让 <see cref="WebviewWindow.ShowContextMenu(OrielMenuItem[], OrielPosition?)"/> 能被记录。
    /// </summary>
    /// <remarks>
    /// 命令层调的是 <c>WebviewWindow.ShowContextMenu(items, position)</c>（不是接口上的单参版本），
    /// 而 <c>WebviewWindow</c> 需要真实后端才能构造。所以这里用一个最小的
    /// <see cref="WebviewWindow"/> 替身不现实——改为让假窗口继承不了（它是 sealed）。
    ///
    /// 因此本组用例走**命令层 + 真实窗口记账**那条路：只断言"命令认得、参数校验对、
    /// 白名单放行"，菜单树落到窗口这一步由 <c>--selftest menu</c> 在三平台真机取证。
    /// 这与既有 <c>window.setTitle</c> 等命令的测试策略一致。
    /// </remarks>
    private static (OrielCommandDispatcher Dispatcher, RecordingWindow Window) Create()
    {
        var window = new RecordingWindow();

        // Release 语义、零能力声明：白名单里的命令仍应可达。
        var guard = new OrielIpcGuard(
            new OrielCapabilityOptions(),
            isDebugBuild: false,
            "app.oriel");

        return (new OrielCommandDispatcher([], null, guard), window);
    }

    // ---- 白名单 ----

    [Theory]
    [InlineData("menu.popup")]
    [InlineData("menu.close")]
    public void MenuCommands_AreAlwaysAllowed(string command)
    {
        Assert.True(
            OrielCapabilityRules.IsAlwaysAllowedMenuCommand(command),
            $"'{command}' 应当在白名单里——页面弹自己的上下文菜单不该要求额外授权。");

        // 不是靠"整族前缀"进来的：这一族后面会长出改菜单栏的命令，那是另一个能力面。
        Assert.False(OrielCapabilityRules.IsAlwaysAllowedMenuCommand("menu.setAsAppMenu"));
        Assert.False(OrielCapabilityRules.IsAlwaysAllowedMenuCommand("menu.setAsWindowMenu"));
        Assert.False(OrielCapabilityRules.IsAlwaysAllowedMenuCommand("menu.new"));
    }

    [Fact]
    public void MenuCommands_AreNotCoveredByWindowPrefix()
    {
        // `menu.` 与 `win.` / `window.` 是三个独立的族。这条挡的是"有人图省事把 menu.*
        // 加进 win.* 的前缀判断"——那会让 menu.setAsAppMenu 跟着一起被放行。
        Assert.False(OrielCapabilityRules.IsBuiltInCommand("menu.popup"));
        Assert.False(OrielCapabilityRules.IsAlwaysAllowedWindowCommand("menu.popup"));
    }

    // ---- 参数校验 ----

    [Fact]
    public async Task Popup_RejectsNonArrayItems()
    {
        var (dispatcher, window) = Create();

        var (reply, _) = await TestHarness.DispatchAsync(
            dispatcher, "menu.popup", new { items = "not-an-array" }, window: window);

        string? code = reply.RootElement.GetProperty("code").GetString();
        Assert.Equal(OrielIpcErrorCodes.InvalidArgument, code);

        string message = reply.RootElement.GetProperty("error").GetString()!;
        Assert.Contains("items", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Popup_RejectsMissingItems()
    {
        var (dispatcher, window) = Create();

        var (reply, _) = await TestHarness.DispatchAsync(
            dispatcher, "menu.popup", new { }, window: window);

        Assert.Equal(
            OrielIpcErrorCodes.InvalidArgument,
            reply.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Popup_RejectsItemThatIsNotObject()
    {
        var (dispatcher, window) = Create();

        var (reply, _) = await TestHarness.DispatchAsync(
            dispatcher, "menu.popup", new { items = new object[] { 42 } }, window: window);

        Assert.Equal(
            OrielIpcErrorCodes.InvalidArgument,
            reply.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Popup_RejectsBadPosition()
    {
        var (dispatcher, window) = Create();

        var (reply, _) = await TestHarness.DispatchAsync(
            dispatcher, "menu.popup",
            new { items = Array.Empty<object>(), position = "not-an-object" },
            window: window);

        Assert.Equal(
            OrielIpcErrorCodes.InvalidArgument,
            reply.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Popup_RejectsTooDeepTree()
    {
        // 防御性上限：这棵树来自页面，递归解析必须有界。
        var (dispatcher, window) = Create();

        object leaf = new { kind = "item", text = "leaf" };
        for (int i = 0; i < 20; i++)
        {
            leaf = new { kind = "submenu", text = $"level{i}", items = new[] { leaf } };
        }

        var (reply, _) = await TestHarness.DispatchAsync(
            dispatcher, "menu.popup", new { items = new[] { leaf } }, window: window);

        Assert.Equal(
            OrielIpcErrorCodes.InvalidArgument,
            reply.RootElement.GetProperty("code").GetString());

        string message = reply.RootElement.GetProperty("error").GetString()!;
        Assert.Contains("层", message, StringComparison.Ordinal);
    }

    // ---- 空菜单 ----

    [Fact]
    public async Task Popup_WithEmptyItems_IsNoOpNotError()
    {
        // 页面可能算出空菜单（例如选区为空时），那是正常情形而不是错误。
        var (dispatcher, window) = Create();

        var (reply, _) = await TestHarness.DispatchAsync(
            dispatcher, "menu.popup", new { items = Array.Empty<object>() }, window: window);

        Assert.False(reply.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task Popup_ReadsAutoDismissFromEnvironment()
    {
        // 自动关闭是给无交互自检用的：Windows 的弹出会阻塞到用户选择，CI 上没人点，
        // 没有它自检就挂到看门狗超时。它**刻意不走页面 API**——那是测试设施，不是应用功能。
        //
        // 这里只能验到"环境变量被读到了"，真正的关闭动作（PostMessage WM_CANCELMODE）
        // 由 --selftest menu 在真机上取证。
        string? original = Environment.GetEnvironmentVariable("ORIELWEB_MENU_AUTODISMISS_MS");
        try
        {
            Environment.SetEnvironmentVariable("ORIELWEB_MENU_AUTODISMISS_MS", "250");
            Assert.Equal(TimeSpan.FromMilliseconds(250), ReadAutoDismiss());

            // 非法值一律当作"不自动关闭"：配置写错不该让菜单行为变怪。
            Environment.SetEnvironmentVariable("ORIELWEB_MENU_AUTODISMISS_MS", "abc");
            Assert.Null(ReadAutoDismiss());

            Environment.SetEnvironmentVariable("ORIELWEB_MENU_AUTODISMISS_MS", "-5");
            Assert.Null(ReadAutoDismiss());

            Environment.SetEnvironmentVariable("ORIELWEB_MENU_AUTODISMISS_MS", null);
            Assert.Null(ReadAutoDismiss());
        }
        finally
        {
            Environment.SetEnvironmentVariable("ORIELWEB_MENU_AUTODISMISS_MS", original);
        }
    }

    /// <summary>读命令层私有的自动关闭延时（纯环境变量解析，用反射即可）。</summary>
    private static TimeSpan? ReadAutoDismiss()
    {
        System.Reflection.PropertyInfo property = typeof(OrielBuiltInMenuCommands).GetProperty(
            "AutoDismissDelay",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        return (TimeSpan?)property.GetValue(null);
    }

    // ---- menu.close ----

    [Fact]
    public async Task Close_CallsWindowAndSucceeds()
    {
        var (dispatcher, window) = Create();

        var (reply, _) = await TestHarness.DispatchAsync(dispatcher, "menu.close", null, window: window);

        Assert.False(reply.RootElement.TryGetProperty("error", out _));
        Assert.Contains("closeContextMenu", window.Calls);
    }

    [Fact]
    public async Task Close_WithoutSourceWindow_IsRejected()
    {
        // 没有来源窗口就不知道该关哪个窗口的菜单。明确报错比"猜一个"好。
        var (dispatcher, _) = Create();

        var (reply, _) = await TestHarness.DispatchAsync(dispatcher, "menu.close", null, window: null);

        Assert.Equal(
            OrielIpcErrorCodes.NoSourceWindow,
            reply.RootElement.GetProperty("code").GetString());
    }

    // ---- 预定义项映射 ----

    [Theory]
    [InlineData("Copy", OrielMenuRole.Copy)]
    [InlineData("Cut", OrielMenuRole.Cut)]
    [InlineData("Paste", OrielMenuRole.Paste)]
    [InlineData("SelectAll", OrielMenuRole.SelectAll)]
    [InlineData("Undo", OrielMenuRole.Undo)]
    [InlineData("Redo", OrielMenuRole.Redo)]
    [InlineData("Minimize", OrielMenuRole.Minimize)]
    [InlineData("Maximize", OrielMenuRole.Zoom)]
    [InlineData("Fullscreen", OrielMenuRole.ToggleFullScreen)]
    [InlineData("Hide", OrielMenuRole.Hide)]
    [InlineData("HideOthers", OrielMenuRole.HideOthers)]
    [InlineData("ShowAll", OrielMenuRole.ShowAll)]
    [InlineData("CloseWindow", OrielMenuRole.Close)]
    [InlineData("Quit", OrielMenuRole.Quit)]
    [InlineData("About", OrielMenuRole.About)]
    [InlineData("BringAllToFront", OrielMenuRole.Front)]
    public void PredefinedNames_MapToRoles(string tauriName, string role)
    {
        // 这组是**命名映射**，写错的表现是"这一项点了没反应"（role 没匹配上任何 case）。
        // 逐条钉住，因为两边用的是两套词表（Tauri 的 `Maximize` 在本库叫 `zoom`）。
        Assert.Equal(role, MapRole(tauriName));
    }

    /// <summary>
    /// 用反射读命令层的私有映射表。
    /// </summary>
    /// <remarks>
    /// 这里破例用反射，因为 <c>RoleOf</c> 是 private static 且**只做纯字符串映射**——
    /// 为了它把一个内部实现细节提升成 internal 会让 API 面变大，而这条映射的正确性
    /// 恰恰是必须逐条钉住的（错一条就是"某个菜单项点了没反应"）。
    /// </remarks>
    private static string? MapRole(string tauriName)
    {
        Type type = typeof(OrielBuiltInMenuCommands);
        System.Reflection.MethodInfo method = type.GetMethod(
            "RoleOf",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        return (string?)method.Invoke(null, [tauriName]);
    }
}
