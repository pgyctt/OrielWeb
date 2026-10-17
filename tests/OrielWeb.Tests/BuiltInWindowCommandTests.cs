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

        public void Minimize() => Calls.Add("minimize");

        public bool ToggleMaximize()
        {
            Calls.Add("toggleMaximize");
            return true;
        }

        public void Close() => Calls.Add("close");

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

        public void ShowContextMenu(IReadOnlyList<OrielMenuItem> items) => Calls.Add($"showContextMenu:{items.Count}");
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
}

/// <summary>
/// 应用自定义的 <c>win.minimize</c>——用来验证内建优先。
/// </summary>
/// <remarks>
/// 它能注册成功是有意的：注册表只查"路由之间是否重名"，不知道内建命令的存在
/// （内建是一个 switch，不在路由里）。所以重名不会在启动时炸掉，而是**静默被遮蔽**——
/// 这正是文档要写明 <c>win.</c> 是保留前缀的原因，也是这条用例存在的意义。
/// </remarks>
public sealed partial class ShadowWindowCommands
{
    // 故意落在保留前缀：本用例验证的就是"被内建静默遮蔽"这一行为。
    // 生成器会对 win. 前缀发 ORIELWEB106 警告（评审 P3：遮蔽不该无人知晓）——这里是有意的。
#pragma warning disable ORIELWEB106
    [OrielCommand("win.minimize")]
#pragma warning restore ORIELWEB106
    public static string Minimize() => "shadowed";
}
