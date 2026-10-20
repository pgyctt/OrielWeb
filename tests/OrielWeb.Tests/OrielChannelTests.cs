using System.Text.Json;
using OrielWeb.Ipc;
using Xunit;

namespace OrielWeb.Tests;

/// <summary>
/// 流式回传通道（<see cref="OrielChannel"/>，B9）的单测。
/// </summary>
/// <remarks>
/// <para>
/// 覆盖三段，每段对应一种"错了会静默"的故障：
/// </para>
/// <list type="number">
///   <item><description><b>绑定</b>：字符串 → 通道。页面传错形状时的报错必须指向"用 new Channel()"，
///     而不是一个 JSON 反序列化错误。</description></item>
///   <item><description><b>推送</b>：走的是与命令返回值**同一套**序列化规则（含应用上下文），
///     事件名带 id。</description></item>
///   <item><description><b>关闭</b>：命令正常返回、抛异常、参数绑定失败——三种退出路径都要发结束事件，
///     否则页面侧每次调用漏一个监听。</description></item>
/// </list>
/// </remarks>
public sealed class OrielChannelTests
{
    private const int ChannelId = 7;
    private const string Wire = "__ORIEL_CHANNEL__:7";

    private static OrielCommandDispatcher CreateDispatcher()
    {
        // 与 DispatcherTests 同一套：生成器已把 TestCommands 注册进全局路由表，
        // 这里只补"怎么造实例"——否则分发器会以"未注册实例命令"拒绝（TestCommands 是实例类）。
        var factories = new Dictionary<Type, Func<object>>
        {
            [typeof(TestCommands)] = () => new TestCommands(),
        };
        return new OrielCommandDispatcher(factories, TestJsonContext.Default, TestGuards.AllowAll());
    }

    private static async Task<(JsonDocument Reply, BuiltInWindowCommandTests.FakeWindow Window)> DispatchAsync(
        string name, object? args, bool withWindow = true)
    {
        var window = new BuiltInWindowCommandTests.FakeWindow();
        (JsonDocument Reply, TestSink Sink) result = await TestHarness.DispatchAsync(
            CreateDispatcher(), name, args, window: withWindow ? window : null);
        return (result.Reply, window);
    }

    /// <summary>只看与通道有关的那几条记录，顺序保留。</summary>
    private static List<string> ChannelCalls(BuiltInWindowCommandTests.FakeWindow window)
        => window.Calls.Where(c => c.Contains("oriel://channel")).ToList();

    // ---- 绑定 ----

    [Fact]
    public async Task Bind_WireStringBecomesChannel()
    {
        using JsonDocument reply = (await DispatchAsync(
            "t.stream", new { count = 1, channel = Wire })).Reply;

        Assert.True(reply.RootElement.GetProperty("ok").GetBoolean(), reply.RootElement.ToString());
        Assert.Equal(1, reply.RootElement.GetProperty("value").GetInt32());
    }

    [Fact]
    public async Task Bind_RejectsNonStringWithActionableMessage()
    {
        // 最常见的迁移错误：把 Tauri 那边的 id（数字）或干脆把对象直接传过来。
        // 文案必须指向"用 new Channel()"，否则调用方会去查 JSON 序列化。
        using JsonDocument reply = (await DispatchAsync(
            "t.stream", new { count = 1, channel = 42 })).Reply;

        Assert.False(reply.RootElement.GetProperty("ok").GetBoolean());
        string error = reply.RootElement.GetProperty("error").GetString()!;
        Assert.Contains("new Channel()", error, StringComparison.Ordinal);
        Assert.Equal("invalid_argument", reply.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Bind_RejectsMalformedPrefix()
    {
        using JsonDocument reply = (await DispatchAsync(
            "t.stream", new { count = 1, channel = "__ORIEL_CHANNEL__:not-a-number" })).Reply;

        Assert.False(reply.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("invalid_argument", reply.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Bind_WithoutSourceWindow_FailsWithNoSourceWindow()
    {
        // 没有来源窗口时不能"退而求其次"推给当前活动窗口——多窗口下那会推到别人家里。
        using JsonDocument reply = (await DispatchAsync(
            "t.stream", new { count = 1, channel = Wire }, withWindow: false)).Reply;

        Assert.False(reply.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("no_source_window", reply.RootElement.GetProperty("code").GetString());
    }

    // ---- 推送 ----

    [Fact]
    public async Task Send_EmitsEachValueUnderChannelEventName()
    {
        var (reply, window) = await DispatchAsync("t.stream", new { count = 3, channel = Wire });
        using JsonDocument _ = reply;

        List<string> calls = ChannelCalls(window);

        // 三条数据 + 一条结束；顺序必须是"数据在前，结束在后"，否则页面会先退订再收尾。
        Assert.Equal(
            [
                $"emitEvent:oriel://channel/{ChannelId}:0",
                $"emitEvent:oriel://channel/{ChannelId}:1",
                $"emitEvent:oriel://channel/{ChannelId}:2",
                $"emitEvent:oriel://channel-end/{ChannelId}:null",
            ],
            calls);
    }

    [Fact]
    public async Task Send_UsesSameSerializationAsCommandResults()
    {
        // DTO 走应用注册的上下文：键是 camelCase（TestJsonContext 的 PropertyNamingPolicy）。
        // 这条钉住"通道不自己另立一套序列化"——否则页面拿到的形状会与命令返回值不一致。
        var (reply, window) = await DispatchAsync("t.streamDto", new { channel = Wire });
        using JsonDocument _ = reply;

        Assert.Contains(
            $"emitEvent:oriel://channel/{ChannelId}:{{\"name\":\"a\",\"value\":1,\"flag\":true}}",
            window.Calls);
    }

    // ---- 关闭 ----

    [Fact]
    public async Task Close_StillHappensWhenCommandThrows()
    {
        // 推到一半炸了：数据已经到页面，通道必须照样关——否则页面的监听留到下次导航。
        var (reply, window) = await DispatchAsync("t.streamFail", new { channel = Wire });
        using JsonDocument _ = reply;

        Assert.False(reply.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(
            [
                $"emitEvent:oriel://channel/{ChannelId}:\"before\"",
                $"emitEvent:oriel://channel-end/{ChannelId}:null",
            ],
            ChannelCalls(window));
    }

    [Fact]
    public void Close_IsIdempotentAndSendAfterwardsIsDropped()
    {
        // 直接构造（不经生成器）：关两次只发一条结束事件；关掉之后的 Send 不再推。
        var window = new BuiltInWindowCommandTests.FakeWindow();
        OrielChannel channel = OrielChannel.Bind(
            Args($$"""{"ch":"{{Wire}}"}"""), "ch", window.EmitEvent, TestJsonContext.Default);

        channel.SendJson("1");
        channel.Close();
        channel.Close();
        channel.SendJson("2"); // 已关闭：丢弃（不抛，但会留一条诊断）

        Assert.Equal(
            [
                $"emitEvent:oriel://channel/{ChannelId}:1",
                $"emitEvent:oriel://channel-end/{ChannelId}:null",
            ],
            ChannelCalls(window));
        Assert.True(channel.IsClosed);
    }

    [Fact]
    public void BindOptional_MissingArgGivesNull()
    {
        // 可选通道：没传就是 null，不该抛"缺少必需参数"。
        OrielChannel? channel = OrielChannel.BindOptional(
            Args("{}"), "ch", new BuiltInWindowCommandTests.FakeWindow().EmitEvent, TestJsonContext.Default);

        Assert.Null(channel);
    }

    /// <summary>把一段 JSON 解析成 <see cref="JsonElement"/>（生命周期绑在文档上，需调用方持有）。</summary>
    private static JsonElement Args(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
