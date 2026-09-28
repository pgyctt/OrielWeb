using System.Text.Json;
using OrielWeb;
using OrielWeb.Ipc;
using Xunit;

namespace OrielWeb.Tests;

/// <summary>
/// 分发器端到端单测：真实源生成路由 + ModuleInitializer 注册 + 回执协议断言。
/// 与 OrielJsonTests 同集合串行（共享 OrielJson 全局上下文）。
/// </summary>
[Collection("IpcSerial")]
public sealed class DispatcherTests
{
    public DispatcherTests() => OrielJson.Use(TestJsonContext.Default);

    private static OrielCommandDispatcher CreateDispatcher(bool withFactory = true)
    {
        var factories = new Dictionary<Type, Func<object>>();
        if (withFactory)
        {
            factories[typeof(TestCommands)] = () => new TestCommands();
        }
        return new OrielCommandDispatcher(factories);
    }

    private static Task<(JsonDocument Reply, TestSink Sink)> DispatchAsync(
        OrielCommandDispatcher dispatcher, string name, object? args, int id = 1)
        => TestHarness.DispatchAsync(dispatcher, name, args, id);

    // ---- 成功路径 ----

    [Fact]
    public async Task Echo_RoundTrip()
    {
        var (reply, _) = await DispatchAsync(CreateDispatcher(), "t.echo", new { text = "你好" });
        Assert.True(reply.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("你好", reply.Value().GetString());
    }

    [Fact]
    public async Task Add_TwoParams()
    {
        var (reply, _) = await DispatchAsync(CreateDispatcher(), "t.add", new { a = 2, b = 40 });
        Assert.Equal(42, reply.Value().GetInt32());
    }

    [Fact]
    public async Task OptionalParams_Missing_Defaults()
    {
        var (reply, _) = await DispatchAsync(CreateDispatcher(), "t.opt", new { });
        Assert.Equal("null|null", reply.Value().GetString());
    }

    [Fact]
    public async Task OptionalParams_Present()
    {
        var (reply, _) = await DispatchAsync(CreateDispatcher(), "t.opt", new { a = "x", b = 3 });
        Assert.Equal("x|3", reply.Value().GetString());
    }

    [Fact]
    public async Task Dto_RoundTrip_CamelCase()
    {
        var (reply, _) = await DispatchAsync(CreateDispatcher(), "t.dto",
            new { input = new { name = "n", value = 5, flag = true } });
        var dto = reply.Value();
        Assert.Equal("n", dto.GetProperty("name").GetString());
        Assert.Equal(6, dto.GetProperty("value").GetInt32());
        Assert.True(dto.GetProperty("flag").GetBoolean());
    }

    [Fact]
    public async Task AsyncCommand_Awaits()
    {
        var (reply, _) = await DispatchAsync(CreateDispatcher(), "t.async", new { delayMs = 10 });
        Assert.Equal("done", reply.Value().GetString());
    }

    [Fact]
    public async Task VoidCommand_ValueNull()
    {
        var (reply, _) = await DispatchAsync(CreateDispatcher(), "t.void", null);
        Assert.True(reply.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(JsonValueKind.Null, reply.Value().ValueKind);
    }

    [Fact]
    public async Task StaticCommand_NoTargetNeeded()
    {
        var (reply, _) = await DispatchAsync(CreateDispatcher(), "t.staticEcho", new { text = "hi" });
        Assert.Equal("S:hi", reply.Value().GetString());
    }

    [Fact]
    public async Task JsonElementParam_ReceivesWholeArgs()
    {
        var (reply, _) = await DispatchAsync(CreateDispatcher(), "t.raw", new { a = 1, b = "x" });
        var raw = JsonDocument.Parse(reply.Value().GetString()!).RootElement;
        Assert.Equal(1, raw.GetProperty("a").GetInt32());
        Assert.Equal("x", raw.GetProperty("b").GetString());
    }

    [Fact]
    public async Task InstanceState_PersistsAcrossCalls()
    {
        var dispatcher = CreateDispatcher();
        var (r1, _) = await DispatchAsync(dispatcher, "t.counter", null);
        var (r2, _) = await DispatchAsync(dispatcher, "t.counter", null);
        Assert.Equal(r1.Value().GetInt32() + 1, r2.Value().GetInt32());
    }

    // ---- 错误路径 ----

    [Fact]
    public async Task UnknownCommand_ErrorReply()
    {
        var (reply, _) = await DispatchAsync(CreateDispatcher(), "nope", null, id: 9);
        Assert.False(reply.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("未知命令", reply.RootElement.GetProperty("error").GetString());
        Assert.Equal(9, reply.RootElement.GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task MissingName_ErrorReply()
    {
        var sink = new TestSink();
        using var doc = JsonDocument.Parse("""{ "__oriel": "invoke", "id": 3 }""");
        await CreateDispatcher().HandleInvokeAsync(doc.RootElement.Clone(), sink);
        var reply = JsonDocument.Parse(sink.Replies.Single()).RootElement;
        Assert.False(reply.GetProperty("ok").GetBoolean());
        Assert.Contains("name", reply.GetProperty("error").GetString());
    }

    [Fact]
    public async Task CommandThrows_ErrorReplyWithMessage()
    {
        var (reply, _) = await DispatchAsync(CreateDispatcher(), "t.fail", null);
        Assert.False(reply.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("boom", reply.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task MissingRequiredArg_ErrorReply()
    {
        var (reply, _) = await DispatchAsync(CreateDispatcher(), "t.echo", new { });
        Assert.False(reply.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("text", reply.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task MissingFactory_ErrorReply()
    {
        // NoFactoryCommands 的路由已被生成器注册，但本分发器没有它的工厂
        var dispatcher = new OrielCommandDispatcher([]);
        var (reply, _) = await DispatchAsync(dispatcher, "nf.hello", null);
        Assert.False(reply.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("AddCommands", reply.RootElement.GetProperty("error").GetString());
    }

    // ---- 协议形状 ----

    [Fact]
    public async Task Reply_ProtocolShape()
    {
        var (reply, _) = await DispatchAsync(CreateDispatcher(), "t.echo", new { text = "x" }, id: 7);
        var root = reply.RootElement;
        Assert.Equal(4, root.EnumerateObject().Count()); // __oriel / id / ok / value
        Assert.Equal("result", root.GetProperty("__oriel").GetString());
        Assert.Equal(7, root.GetProperty("id").GetInt32());
        Assert.True(root.GetProperty("ok").GetBoolean());
    }

    // ---- 并发 ----

    [Fact]
    public async Task ConcurrentInvocations_AllSucceed()
    {
        var dispatcher = CreateDispatcher();
        const int total = 50;
        var results = await Task.WhenAll(Enumerable.Range(0, total).Select(i =>
            DispatchAsync(dispatcher, "t.counter", null, id: i)));

        Assert.All(results, r => Assert.True(r.Reply.RootElement.GetProperty("ok").GetBoolean()));

        // 静态计数器保证每次调用返回全局唯一且连续的值
        var values = results.Select(r => r.Reply.Value().GetInt32()).ToList();
        Assert.Equal(total, values.Distinct().Count());
        Assert.Equal(total - 1, values.Max() - values.Min());
    }
}
