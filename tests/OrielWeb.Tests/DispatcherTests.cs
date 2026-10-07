using System.Text.Json;
using OrielWeb;
using OrielWeb.Ipc;
using Xunit;

namespace OrielWeb.Tests;

/// <summary>
/// 分发器端到端单测：真实源生成路由 + ModuleInitializer 注册 + 回执协议断言。
/// </summary>
/// <remarks>
/// JSON 上下文按分发器实例持有（不再是全局静态状态），因此本类与 <c>OrielJsonTests</c>
/// 不需要串行执行。
/// </remarks>
public sealed class DispatcherTests
{
    private static OrielCommandDispatcher CreateDispatcher(bool withFactory = true)
    {
        var factories = new Dictionary<Type, Func<object>>();
        if (withFactory)
        {
            factories[typeof(TestCommands)] = () => new TestCommands();
        }
        return new OrielCommandDispatcher(factories, TestJsonContext.Default, TestGuards.AllowAll());
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
        (JsonDocument Reply, TestSink _) result = await TestHarness.DispatchRawAsync(
            CreateDispatcher(), """{ "__oriel": "invoke", "id": 3 }""");
        JsonElement reply = result.Reply.RootElement;
        Assert.False(reply.GetProperty("ok").GetBoolean());
        Assert.Contains("name", reply.GetProperty("error").GetString());
    }

    // ---- id 回写（回执必须能被页面用它自己发出的那个值查回来） ----

    [Fact]
    public async Task FractionalId_IsEchoedBackVerbatim()
    {
        // 页面侧是 JS number：回执里的 id 与它发出去的一致，才能在 pending 里对上并立即 settle。
        // 以前把 id 解析成 int，1.5 会抛 FormatException，兜底回执写死 id:0——而页面的 seq 从 1 开始，
        // 永远匹配不到，那个 Promise 要一直挂到 30 秒超时（表现为"命令没反应"，而不是"参数错了"）。
        (JsonDocument Reply, TestSink _) result = await TestHarness.DispatchRawAsync(
            CreateDispatcher(),
            """{ "__oriel": "invoke", "id": 1.5, "name": "t.echo", "args": { "text": "x" } }""");

        JsonElement reply = result.Reply.RootElement;
        Assert.True(reply.GetProperty("ok").GetBoolean());
        Assert.Equal(1.5, reply.GetProperty("id").GetDouble());
    }

    [Fact]
    public async Task OutOfInt32RangeId_IsEchoedBackVerbatim()
    {
        (JsonDocument Reply, TestSink _) result = await TestHarness.DispatchRawAsync(
            CreateDispatcher(),
            """{ "__oriel": "invoke", "id": 5000000000, "name": "t.echo", "args": { "text": "x" } }""");

        JsonElement reply = result.Reply.RootElement;
        Assert.True(reply.GetProperty("ok").GetBoolean());
        Assert.Equal(5_000_000_000L, reply.GetProperty("id").GetInt64());
    }

    [Fact]
    public async Task MissingId_FallsBackToZero()
    {
        // 协议要求 id 字段始终存在，缺失时兜底写 0（桥接脚本的 seq 是自增整数，不会发这种消息）
        (JsonDocument Reply, TestSink _) result = await TestHarness.DispatchRawAsync(
            CreateDispatcher(),
            """{ "__oriel": "invoke", "name": "t.echo", "args": { "text": "x" } }""");

        JsonElement reply = result.Reply.RootElement;
        Assert.True(reply.GetProperty("ok").GetBoolean());
        Assert.Equal(0, reply.GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task CommandThrows_ErrorReplyWithMessage()
    {
        var (reply, _) = await DispatchAsync(CreateDispatcher(), "t.fail", null);
        Assert.False(reply.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("boom", reply.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task UncontrolledException_IsSanitizedBeforeReachingPage()
    {
        // API.md 的承诺：只有 OrielIpcException 的 Message 原样到达页面；其余异常的
        // Message 可能含内部路径/主机名，一律给通用文案，细节只进宿主调试输出。
        var (reply, _) = await DispatchAsync(CreateDispatcher(), "t.crash", null);
        var root = reply.RootElement;

        Assert.False(root.GetProperty("ok").GetBoolean());
        string error = root.GetProperty("error").GetString()!;
        Assert.Contains("执行失败", error, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("InvalidOperationException", error, StringComparison.OrdinalIgnoreCase);

        // 净化不能破坏协议：id 原样回写
        Assert.Equal(1, root.GetProperty("id").GetInt32());
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
        var dispatcher = new OrielCommandDispatcher([], TestJsonContext.Default, TestGuards.AllowAll());
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

    // ---- JSON 上下文按实例持有 ----

    [Fact]
    public async Task TwoDispatchers_WithDifferentContexts_DoNotInterfere()
    {
        // 这是把上下文从静态字段改成按实例持有的**目的**：以前后创建的那个会覆盖前者用的上下文，
        // 两个应用互相串——而且表现是静默地用错类型信息，不是报错。
        var withContext = new OrielCommandDispatcher(
            new Dictionary<Type, Func<object>> { [typeof(TestCommands)] = () => new TestCommands() },
            TestJsonContext.Default,
            TestGuards.AllowAll());
        var withoutContext = new OrielCommandDispatcher(
            new Dictionary<Type, Func<object>> { [typeof(TestCommands)] = () => new TestCommands() },
            new EmptyJsonContext(),
            TestGuards.AllowAll());

        object args = new { input = new { name = "n", value = 1, flag = true } };

        // 同一个 DTO 命令：有上下文的成功，没上下文的按未注册类型报错
        var (okReply, _) = await DispatchAsync(withContext, "t.dto", args);
        Assert.True(okReply.RootElement.GetProperty("ok").GetBoolean());

        var (failReply, _) = await DispatchAsync(withoutContext, "t.dto", args);
        Assert.False(failReply.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("UseJsonContext", failReply.RootElement.GetProperty("error").GetString());

        // 再问一次有上下文的那位：它没有被"后创建的那个"影响
        var (againReply, _) = await DispatchAsync(withContext, "t.dto", args);
        Assert.True(againReply.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Dispatcher_WithoutContext_StillServesPrimitiveCommands()
    {
        // 上下文只对 DTO 是必需的：没注册上下文的应用，基元命令照样要能用
        var dispatcher = new OrielCommandDispatcher(
            new Dictionary<Type, Func<object>> { [typeof(TestCommands)] = () => new TestCommands() },
            jsonContext: null,
            guard: TestGuards.AllowAll());

        var (reply, _) = await DispatchAsync(dispatcher, "t.echo", new { text = "你好" });
        Assert.True(reply.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("你好", reply.Value().GetString());
    }
}
