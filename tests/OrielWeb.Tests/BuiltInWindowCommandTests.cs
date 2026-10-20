using OrielWeb;
using OrielWeb.Ipc;
using Xunit;

namespace OrielWeb.Tests;

/// <summary>
/// 内建 <c>win.*</c> 窗口命令：应用一行不注册也能用，且**先于**应用路由命中。
/// </summary>
/// <remarks>
/// 这里用轻量假窗口（只记录调用）而不是真窗口：这些用例要断言的是"消息 → 哪个窗口方法、
/// 参数有没有走样"，与三平台后端无关——真窗口那部分只能在各自平台上人眼验证（见 ROADMAP）。
/// </remarks>
public sealed class BuiltInWindowCommandTests
{
    /// <summary>只记录调用的假窗口。internal 是为了给 DispatcherTests 的"内建命令不计入命令观测"复用。</summary>
    internal sealed class FakeWindow : IOrielWindowControl
    {
        public List<string> Calls { get; } = [];

        /// <summary>
        /// 所属应用。
        /// </summary>
        /// <remarks>
        /// <c>win.*</c> 用例不碰它，但 <c>tray.*</c> 用例需要（托盘是应用级资源）——
        /// 那里传入一个真实构建的 <see cref="OrielApp"/>（托盘注册表在它上面）。
        /// 默认给一个未运行的空应用：任何误用都会立刻抛"应用尚未运行"，而不是静默走错。
        /// </remarks>
        public OrielApp App { get; set; } = Oriel.CreateBuilder().Build();

        /// <summary>可写是为了让 <c>win.isVisible</c> 用例断言"读到的是当前值"。</summary>
        public bool IsVisible { get; set; } = true;

        /// <summary>
        /// 本窗口的 label。
        /// </summary>
        /// <remarks>
        /// <c>window.*</c> 族用它算**默认目标**（命令的 label 参数缺省时作用于发起者）。
        /// 默认给 <c>"main"</c> 而不是 null：多数用例只关心"命令有没有作用到这个窗口"，
        /// 而 null 会让它们撞上"无法确定发起窗口"的报错——那是另一条单独的用例。
        /// </remarks>
        public string? Label { get; set; } = "main";

        public void Minimize() => Calls.Add("minimize");

        public bool ToggleMaximize()
        {
            Calls.Add("toggleMaximize");
            return true;
        }

        public void Close() => Calls.Add("close");

        /// <summary>强制关闭。与 <see cref="Close"/> 分开记录——两者语义不同，混在一起就断言不了"调的是哪一个"。</summary>
        public void Destroy() => Calls.Add("destroy");

        /// <summary>关闭接管开关；可写是为了断言 <c>win.setCloseIntercept</c> 真的改了它。</summary>
        public bool CloseInterceptEnabled { get; set; }

        public bool ToggleFullscreen()
        {
            Calls.Add("toggleFullscreen");
            return true;
        }

        public bool ToggleOnTop()
        {
            Calls.Add("toggleOnTop");
            return true;
        }

        public void BeginDrag() => Calls.Add("beginDrag");

        public void BeginDragStreaming(double px, double py, double wx, double wy, double ww, double wh, double sh)
            => Calls.Add($"beginDragStreaming:{px},{py},{wx},{wy},{ww},{wh},{sh}");

        public void DragTo(double dx, double dy) => Calls.Add($"dragTo:{dx},{dy}");

        public void EndDrag() => Calls.Add("endDrag");

        public string? ShowOpenFileDialog(string? title, string? filter, string? initialDirectory)
        {
            Calls.Add($"showOpenFileDialog:{title}|{filter}");
            return null;
        }

        /// <summary>记录多选/过滤器那几项，供 dialog.open 的测试断言。</summary>
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

        /// <summary>消息框：默认回"用户点了确认"，测试可以改写 <see cref="MessageResult"/>。</summary>
        public MessageResult? ShowMessageDialog(
            string message, string? title, string kind, MessageButtons buttons, string okLabel, string cancelLabel)
        {
            Calls.Add($"showMessageDialog:{message}|{title}|{kind}|{buttons}|{okLabel}|{cancelLabel}");
            return NextMessageResult;
        }

        /// <summary>下一次 <see cref="ShowMessageDialog"/> 的返回值（null 表示"被外部关掉"）。</summary>
        public MessageResult? NextMessageResult { get; set; } = MessageResult.Ok;

        /// <summary>缩放比：菜单命令要把物理坐标换成逻辑坐标时读它。</summary>
        public double ScaleFactor { get; set; } = 1.0;

        public void ShowContextMenu(IReadOnlyList<OrielMenuItem> items) => Calls.Add($"showContextMenu:{items.Count}");

        /// <summary>带位置的弹出（<c>menu.popup</c> 用）：把位置一起记下来，便于断言换算。</summary>
        public void ShowContextMenu(
            IReadOnlyList<OrielMenuItem> items, OrielPosition? position, TimeSpan? autoDismissAfter = null)
            => Calls.Add($"showContextMenuAt:{items.Count}:{position?.X},{position?.Y}:{autoDismissAfter?.TotalMilliseconds}");

        /// <summary>记录"关掉在屏菜单"（<c>menu.close</c> 用）。与 showContextMenu 分开记，两者语义相反。</summary>
        public void CloseContextMenu() => Calls.Add("closeContextMenu");

        /// <summary>记下推给页面的事件（通道/拖放等宿主推送的落点）。</summary>
        public void EmitEvent(string name, string jsonPayload) => Calls.Add($"emitEvent:{name}:{jsonPayload}");

        /// <summary>记下菜单栏调用（<c>menu.setAsWindowMenu</c> 的落点）。</summary>
        public void SetWindowMenuBar(IReadOnlyList<OrielMenuItem> items)
            => Calls.Add($"setWindowMenuBar:{items.Count}");

        /// <summary>记下应用菜单调用（<c>menu.setAsAppMenu</c> 的落点）。</summary>
        public void SetAppMenuBar(IReadOnlyList<OrielMenuItem> items)
            => Calls.Add($"setAppMenuBar:{items.Count}");
    }

    private static OrielCommandDispatcher CreateDispatcher()
        => new([], TestJsonContext.Default, TestGuards.AllowAll());

    [Fact]
    public async Task 应用不注册也能调用内建窗口命令()
    {
        var window = new FakeWindow();
        var (reply, _) = await TestHarness.DispatchAsync(
            CreateDispatcher(), "win.minimize", null, window: window);

        Assert.True(reply.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(["minimize"], window.Calls);
    }

    [Fact]
    public async Task 有返回值的命令把结果交给页面()
    {
        // 页面用 toggleMaximize 的返回值驱动图标，所以这里必须是布尔而不是 null
        var (reply, _) = await TestHarness.DispatchAsync(
            CreateDispatcher(), "win.toggleMaximize", null, window: new FakeWindow());

        Assert.True(reply.Value().GetBoolean());
    }

    [Fact]
    public async Task win_isVisible_把当前可见性交给页面()
    {
        // 只读查询：页面/宿主据此确认窗口是不是真被藏住了（内建里唯一的查询类命令）。
        // 用 false 而不是默认 true 来断言，是为了证明它读的是**当前值**而不是恒真。
        var (reply, _) = await TestHarness.DispatchAsync(
            CreateDispatcher(), "win.isVisible", null, window: new FakeWindow { IsVisible = false });

        Assert.True(reply.RootElement.GetProperty("ok").GetBoolean());
        Assert.False(reply.Value().GetBoolean());
    }

    [Fact]
    public async Task 拖动起点把七个坐标原样透传()
    {
        var window = new FakeWindow();
        var (reply, _) = await TestHarness.DispatchAsync(
            CreateDispatcher(), "win.dragStart",
            new { px = 10.5, py = 20.5, wx = 100, wy = 60, ww = 1024, wh = 768, sh = 1080 },
            window: window);

        Assert.True(reply.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(["beginDragStreaming:10.5,20.5,100,60,1024,768,1080"], window.Calls);
    }

    [Fact]
    public async Task 拖动增量缺少参数时报错且不调用窗口()
    {
        // 缺 dx 时若静默当 0，窗口会横向跳到别处——报错比猜一个值安全
        var window = new FakeWindow();
        var (reply, _) = await TestHarness.DispatchAsync(
            CreateDispatcher(), "win.dragTo", new { dy = 5 }, window: window);

        Assert.False(reply.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("dx", reply.RootElement.GetProperty("error").GetString());
        Assert.Empty(window.Calls);
    }

    [Fact]
    public async Task 没有来源窗口时报错而不是静默成功()
    {
        var (reply, _) = await TestHarness.DispatchAsync(CreateDispatcher(), "win.minimize", null);

        Assert.False(reply.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("窗口", reply.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task 内建命令压过应用注册的同名命令()
    {
        // win. 是保留前缀：应用自己写的 win.minimize（见 ShadowWindowCommands）在这里不可达，
        // 命中的是内建那个——所以 demo 里那份 WindowCommands 已经被删掉。
        var window = new FakeWindow();
        var (reply, _) = await TestHarness.DispatchAsync(
            CreateDispatcher(), "win.minimize", null, window: window);

        Assert.True(reply.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(["minimize"], window.Calls);
    }

    [Fact]
    public async Task 不认识的win命令仍走应用路由并报未知命令()
    {
        var (reply, _) = await TestHarness.DispatchAsync(
            CreateDispatcher(), "win.nonexistent", null, window: new FakeWindow());

        Assert.False(reply.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("未知命令", reply.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task 内建上下文菜单含标准编辑项()
    {
        var window = new FakeWindow();
        await TestHarness.DispatchAsync(CreateDispatcher(), "win.contextMenu", null, window: window);

        // 剪切/复制/粘贴 + 全选 + 关闭 + 两条分隔线 = 7 项
        Assert.Equal(["showContextMenu:7"], window.Calls);
    }

    // ---- 关闭接管（C5）----

    [Fact]
    public async Task win_destroy_调用强制关闭而不是可拦的关闭()
    {
        // 两者语义不同，混在一起会让"确认后关闭"变成"永远关不掉"：
        // Close 会被 Closing 与页面接管拦下，Destroy 不会。
        var window = new FakeWindow();
        var (reply, _) = await TestHarness.DispatchAsync(
            CreateDispatcher(), "win.destroy", null, window: window);

        Assert.True(reply.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(["destroy"], window.Calls);
    }

    [Fact]
    public async Task win_setCloseIntercept_打开接管()
    {
        var window = new FakeWindow { CloseInterceptEnabled = false };
        var (reply, _) = await TestHarness.DispatchAsync(
            CreateDispatcher(), "win.setCloseIntercept", new { enabled = true }, window: window);

        Assert.True(reply.RootElement.GetProperty("ok").GetBoolean());
        Assert.True(window.CloseInterceptEnabled);
        Assert.True(reply.Value().GetBoolean(), "返回值应当是设置后的状态，便于页面确认");
    }

    [Fact]
    public async Task win_setCloseIntercept_关闭接管()
    {
        var window = new FakeWindow { CloseInterceptEnabled = true };
        var (reply, _) = await TestHarness.DispatchAsync(
            CreateDispatcher(), "win.setCloseIntercept", new { enabled = false }, window: window);

        Assert.False(window.CloseInterceptEnabled);
        Assert.False(reply.Value().GetBoolean());
    }

    [Fact]
    public async Task win_setCloseIntercept_缺enabled时默认打开()
    {
        // 省略参数按"打开"算：页面调用它几乎总是为了"我要接管"，
        // 而漏写字段时打开接管是可排查的（窗口不关 → 去查页面），
        // 静默关掉接管则是"确认框没弹就直接关了"，那是数据丢失。
        var window = new FakeWindow { CloseInterceptEnabled = false };
        await TestHarness.DispatchAsync(
            CreateDispatcher(), "win.setCloseIntercept", null, window: window);

        Assert.True(window.CloseInterceptEnabled);
    }
}

/// <summary>
/// 应用自定义的 <c>win.minimize</c>——用来验证"内建优先"这条**运行期**行为。
/// </summary>
/// <remarks>
/// <para>
/// 生成器对 <c>win.</c> 前缀报 ORIELWEB106（Error 级，2026-10-19 起），
/// 所以正常情况下**没有任何应用能写出这个类**。这个类靠 <c>#pragma warning disable</c>
/// 显式压制，唯一目的是保留"内建压过路由"的回归覆盖——诊断本身另有
/// <see cref="GeneratorDiagnosticsTests"/> 钉住。
/// </para>
/// <para>
/// 压制是刻意的、局部的，而且一旦内建表与路由的相对优先级变了，这里的用例会红。
/// </para>
/// </remarks>
public sealed partial class ShadowWindowCommands
{
    // 故意落在保留前缀：本用例验证的就是"内建优先"这一行为（诊断已在别处覆盖）。
#pragma warning disable ORIELWEB106
    [OrielCommand("win.minimize")]
#pragma warning restore ORIELWEB106
    public static string Minimize() => "shadowed";
}
