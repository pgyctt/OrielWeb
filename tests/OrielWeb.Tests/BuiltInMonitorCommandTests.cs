using System.Text.Json;
using OrielWeb.Ipc;
using Xunit;

namespace OrielWeb.Tests;

/// <summary>
/// 显示器命令族（<c>window.currentMonitor</c> / <c>primaryMonitor</c> / <c>availableMonitors</c> /
/// <c>monitorFromPoint</c> / <c>cursorPosition</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 这一组的断言重点不是"门禁放没放行"（它们全在白名单里，单独的用例钉住），而是
/// **页面拿到的 JSON 形状**：键名、<c>type: 'Physical'</c> 标记、<c>name: null</c>
/// 写成 null 而不是省略键。这些细节错了不会抛异常，只会让迁移代码读到 undefined，
/// 是最难在联调时发现的一类偏差。
/// </para>
/// <para>
/// <c>window.currentMonitor</c> 的**正向**路径不在单测里：它要经 <c>Resolve</c> 拿到
/// 一个已注册的真实 <see cref="WebviewWindow"/>，而测试里没有平台后端就建不出窗口
/// （与 <c>window.setTitle</c> 等既有命令同一处境）。那条路径由
/// <c>--selftest monitors</c> 在三平台真机上取证。
/// </para>
/// </remarks>
public sealed class BuiltInMonitorCommandTests
{
    /// <summary>测试用显示器后端：返回固定的一组数据，并记录最近一次查询方式。</summary>
    private sealed class FakeMonitorBackend(params OrielMonitor[] monitors) : IMonitorBackend
    {
        public List<string> Calls { get; } = [];

        public double LastX { get; private set; }

        public double LastY { get; private set; }

        public IReadOnlyList<OrielMonitor> GetMonitors()
        {
            Calls.Add("getMonitors");
            return monitors;
        }

        public OrielMonitor? GetPrimaryMonitor()
        {
            Calls.Add("getPrimaryMonitor");
            return Array.Find(monitors, m => m.IsPrimary);
        }

        public OrielMonitor? MonitorFromPoint(double x, double y)
        {
            Calls.Add("monitorFromPoint");
            LastX = x;
            LastY = y;

            foreach (OrielMonitor monitor in monitors)
            {
                if (x >= monitor.Position.X && x < monitor.Position.X + monitor.Size.Width &&
                    y >= monitor.Position.Y && y < monitor.Position.Y + monitor.Size.Height)
                {
                    return monitor;
                }
            }

            return null;
        }

        public OrielMonitor? MonitorFromWindow(nint nativeWindowHandle)
        {
            Calls.Add("monitorFromWindow");
            return null;
        }

        public OrielPosition GetCursorPosition()
        {
            Calls.Add("getCursorPosition");
            return new OrielPosition(321, 654);
        }
    }

    private static OrielMonitor Monitor(
        string? name, double x, double y, double width, double height, double scale, bool primary = false)
        => new(name, new OrielPosition(x, y), new OrielSize(width, height), scale) { IsPrimary = primary };

    private static (OrielCommandDispatcher Dispatcher, BuiltInWindowCommandTests.FakeWindow Window) Create(
        IMonitorBackend? monitors = null)
    {
        OrielApp app = Oriel.CreateBuilder().Build();
        app.MonitorBackend = monitors ?? EmptyMonitorBackend.Instance;

        var window = new BuiltInWindowCommandTests.FakeWindow { App = app, Label = "main" };

        // Release 语义、零能力声明：白名单里的命令仍应可达。
        // 这正好同时验证"它们确实是始终放行的那一组"。
        var guard = new OrielIpcGuard(
            new OrielCapabilityOptions(),
            isDebugBuild: false,
            "app.oriel");

        return (new OrielCommandDispatcher([], null, guard), window);
    }

    // ---- 白名单 ----

    [Theory]
    [InlineData("window.currentMonitor")]
    [InlineData("window.primaryMonitor")]
    [InlineData("window.availableMonitors")]
    [InlineData("window.monitorFromPoint")]
    [InlineData("window.cursorPosition")]
    public void MonitorCommands_AreAlwaysAllowed(string command)
    {
        // 理由：屏幕布局与光标位置是页面本来就能感知的环境信息（浏览器给得出当前屏尺寸与
        // 缩放），而自绘标题栏要按"拖到哪块屏"换配色。它们不读任何窗口内容。
        Assert.True(
            OrielCapabilityRules.IsAlwaysAllowedWindowCommand(command),
            $"'{command}' 应当在白名单里——自绘标题栏要靠它在多屏间换配色。");

        // 不是靠 win.* 那条整族后门进来的
        Assert.False(OrielCapabilityRules.IsBuiltInCommand(command));
    }

    // ---- 形状 ----

    [Fact]
    public async Task AvailableMonitors_ShapeMatchesTauriMonitor()
    {
        var (dispatcher, window) = Create(new FakeMonitorBackend(
            Monitor(@"\\.\DISPLAY1", 0, 0, 1920, 1080, 1.0, primary: true),
            Monitor(null, 1920, 0, 2560, 1440, 2.0)));

        var (reply, _) = await TestHarness.DispatchAsync(
            dispatcher, "window.availableMonitors", null, window: window);

        JsonElement monitors = reply.Value();
        Assert.Equal(JsonValueKind.Array, monitors.ValueKind);
        Assert.Equal(2, monitors.GetArrayLength());

        JsonElement first = monitors[0];
        var keys = first.EnumerateObject().Select(p => p.Name).ToArray();

        // **恰好四个键**。多一个 isPrimary 会让页面里 assert.deepEqual(Object.keys(...)) 变红。
        Assert.Equal(["name", "position", "size", "scaleFactor"], keys);

        Assert.Equal(@"\\.\DISPLAY1", first.GetProperty("name").GetString());
        Assert.Equal(0, first.GetProperty("position").GetProperty("Physical").GetProperty("x").GetDouble());
        Assert.Equal(1920, first.GetProperty("size").GetProperty("Physical").GetProperty("width").GetDouble());
        Assert.Equal(1.0, first.GetProperty("scaleFactor").GetDouble());

        // 位置/尺寸必须标成 Physical：页面据此决定要不要除以缩放比。
        // B14 起是 Tauri 2 的外部标记形状 `{ Physical: { x, y } }`（不是旧的 `{ type, x, y }`）。
        Assert.True(first.GetProperty("size").TryGetProperty("Physical", out _));
        Assert.True(first.GetProperty("position").TryGetProperty("Physical", out _));

        JsonElement second = monitors[1];

        // name 为 null 时必须是 JSON null（而不是省略键）：Tauri 的类型是 string | null，
        // 页面里最常见的写法是 monitor.name === null。
        Assert.Equal(JsonValueKind.Null, second.GetProperty("name").ValueKind);
        Assert.Equal(1920, second.GetProperty("position").GetProperty("Physical").GetProperty("x").GetDouble());
        Assert.Equal(2.0, second.GetProperty("scaleFactor").GetDouble());
    }

    [Fact]
    public async Task PrimaryMonitor_PicksTheFlaggedOne_NotTheFirst()
    {
        // 故意把副屏放第一位：主屏"不一定是枚举的第一项"是真实情况
        // （macOS 的 [NSScreen screens] 顺序与主屏无关，Windows 也不保证）。
        var (dispatcher, window) = Create(new FakeMonitorBackend(
            Monitor("second", 1920, 0, 2560, 1440, 2.0),
            Monitor("main", 0, 0, 1920, 1080, 1.0, primary: true)));

        var (reply, _) = await TestHarness.DispatchAsync(
            dispatcher, "window.primaryMonitor", null, window: window);

        Assert.Equal("main", reply.Value().GetProperty("name").GetString());
    }

    [Fact]
    public async Task PrimaryMonitor_ReturnsNull_WhenPlatformCannotTell()
    {
        var (dispatcher, window) = Create(new FakeMonitorBackend(
            Monitor("only", 0, 0, 1920, 1080, 1.0)));

        var (reply, _) = await TestHarness.DispatchAsync(
            dispatcher, "window.primaryMonitor", null, window: window);

        Assert.Equal(JsonValueKind.Null, reply.Value().ValueKind);
    }

    [Fact]
    public async Task MonitorFromPoint_PassesCoordinatesThrough_AndReturnsNullOutside()
    {
        var backend = new FakeMonitorBackend(Monitor("main", 0, 0, 1920, 1080, 1.0, primary: true));
        var (dispatcher, window) = Create(backend);

        var (inside, _) = await TestHarness.DispatchAsync(
            dispatcher, "window.monitorFromPoint", new { x = 100, y = 200 }, window: window);

        Assert.Equal("main", inside.Value().GetProperty("name").GetString());
        Assert.Equal(100, backend.LastX);
        Assert.Equal(200, backend.LastY);

        var (outside, _) = await TestHarness.DispatchAsync(
            dispatcher, "window.monitorFromPoint", new { x = 9999, y = 9999 }, window: window);

        Assert.Equal(JsonValueKind.Null, outside.Value().ValueKind);
    }

    [Fact]
    public async Task MonitorFromPoint_RequiresBothCoordinates()
    {
        var (dispatcher, window) = Create(new FakeMonitorBackend(
            Monitor("main", 0, 0, 1920, 1080, 1.0, primary: true)));

        var (reply, _) = await TestHarness.DispatchAsync(
            dispatcher, "window.monitorFromPoint", new { x = 10 }, window: window);

        Assert.False(reply.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("y", reply.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task CursorPosition_IsPhysicalPosition()
    {
        var (dispatcher, window) = Create(new FakeMonitorBackend(
            Monitor("main", 0, 0, 1920, 1080, 1.0, primary: true)));

        var (reply, _) = await TestHarness.DispatchAsync(
            dispatcher, "window.cursorPosition", null, window: window);

        JsonElement position = reply.Value();
        // B14 起是外部标记形状 `{ Physical: { x, y } }`
        Assert.True(position.TryGetProperty("Physical", out JsonElement physical));
        Assert.Equal(321, physical.GetProperty("x").GetDouble());
        Assert.Equal(654, physical.GetProperty("y").GetDouble());
    }

    // ---- 未运行时 ----

    [Fact]
    public void NotRunning_QueriesDegradeInsteadOfThrowing()
    {
        OrielApp app = Oriel.CreateBuilder().Build();

        // 默认就是空实现：问一句"有几块屏"不该在 Run 之前炸掉调用方。
        Assert.Empty(app.AvailableMonitors);
        Assert.Null(app.PrimaryMonitor);
        Assert.Null(app.GetMonitorFromPoint(10, 10));
        Assert.Equal(new OrielPosition(0, 0), app.CursorPosition);
    }

    [Fact]
    public async Task AvailableMonitors_IsEmptyArray_WhenBackendHasNone()
    {
        var (dispatcher, window) = Create();

        var (reply, _) = await TestHarness.DispatchAsync(
            dispatcher, "window.availableMonitors", null, window: window);

        // 空数组而不是 null：Tauri 的类型是 Monitor[]，null 会让 .map/.length 直接抛
        Assert.Equal(JsonValueKind.Array, reply.Value().ValueKind);
        Assert.Equal(0, reply.Value().GetArrayLength());
    }
}
