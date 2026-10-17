using System.Text.Json;
using OrielWeb.Ipc;
using Xunit;

namespace OrielWeb.Tests;

/// <summary>
/// 结构化错误（F7）：错误码与附加数据如何从宿主走到页面。
/// </summary>
/// <remarks>
/// <para>
/// 这一层的价值在**分类**：页面拿到一句 "命令失败" 与拿到
/// <c>code: 'not_allowed'</c>，能做的事完全不同（后者可以提示"没有权限"而不是"出错了"）。
/// 分不清"命令没授权"与"命令自己炸了"是排障时最费时间的一件事。
/// </para>
/// <para>
/// 形状本身（回执里有没有 code 字段）也要钉：它只在有值时才写——
/// 写了 <c>"code":null</c> 会让页面侧的 <c>if (e.code)</c> 与 <c>'code' in e</c>
/// 给出不同答案，而那是个容易写错的判断。
/// </para>
/// </remarks>
public sealed class StructuredErrorTests
{
    [Fact]
    public async Task UnknownCommand_CarriesUnknownCommandCode()
    {
        (JsonDocument reply, _) = await TestHarness.DispatchAsync(
            CreateDispatcher(), "nope.not.registered", null, window: new BuiltInWindowCommandTests.FakeWindow());

        Assert.False(reply.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(
            OrielIpcErrorCodes.UnknownCommand,
            reply.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task NotAllowed_CarriesNotAllowedCode()
    {
        // 只声明 todo.*，于是 window.setTitle 被拒——页面据此可以提示"没有权限"
        OrielApp app = Oriel.CreateBuilder().Build();
        var window = new BuiltInWindowCommandTests.FakeWindow { App = app };
        var guard = new OrielIpcGuard(
            new OrielCapabilityOptions().Allow("todo.*"), isDebugBuild: false, "app.oriel");
        var dispatcher = new OrielCommandDispatcher([], null, guard);

        (JsonDocument reply, _) = await TestHarness.DispatchAsync(
            dispatcher, "window.setTitle", new { title = "x" }, window: window);

        Assert.False(reply.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(OrielIpcErrorCodes.NotAllowed, reply.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task MissingArgument_CarriesMissingArgumentCode()
    {
        // t.add 需要 a 与 b
        (JsonDocument reply, _) = await TestHarness.DispatchAsync(
            CreateDispatcher(), "t.add", new { a = 1 });

        Assert.False(reply.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(OrielIpcErrorCodes.MissingArgument, reply.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task InvalidArgumentKind_CarriesInvalidArgumentCode()
    {
        // a 声明为 int，传字符串
        (JsonDocument reply, _) = await TestHarness.DispatchAsync(
            CreateDispatcher(), "t.add", new { a = "不是数字", b = 2 });

        Assert.False(reply.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(OrielIpcErrorCodes.InvalidArgument, reply.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task InvalidMessage_CarriesInvalidMessageCode()
    {
        (JsonDocument reply, _) = await TestHarness.DispatchRawAsync(
            CreateDispatcher(), """{ "__oriel": "invoke", "id": 1, "args": {} }""");

        Assert.False(reply.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(OrielIpcErrorCodes.InvalidMessage, reply.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task TargetNotFound_CarriesTargetNotFoundCode()
    {
        OrielApp app = Oriel.CreateBuilder().Build();
        var window = new BuiltInWindowCommandTests.FakeWindow { App = app, Label = "main" };
        var guard = new OrielIpcGuard(
            new OrielCapabilityOptions().Allow("window.*"), isDebugBuild: false, "app.oriel");
        var dispatcher = new OrielCommandDispatcher([], null, guard);

        (JsonDocument reply, _) = await TestHarness.DispatchAsync(
            dispatcher, "window.title", new { label = "no-such-window" }, window: window);

        Assert.False(reply.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(OrielIpcErrorCodes.TargetNotFound, reply.RootElement.GetProperty("code").GetString());
    }

    // ---- 形状：只在有值时才写 ----

    [Fact]
    public async Task ErrorWithoutCode_OmitsTheCodeField()
    {
        // 应用命令抛的异常不带码——那是应用的错误分类，库不该替它编。
        // 所以这里断言的是"字段**不存在**"，而不是"字段是 null"。
        (JsonDocument reply, _) = await TestHarness.DispatchAsync(
            CreateDispatcher(), "t.fail", null);

        Assert.False(reply.RootElement.GetProperty("ok").GetBoolean());
        Assert.False(
            reply.RootElement.TryGetProperty("code", out _),
            "没有码时不该写 code 字段——写了 null 会让页面侧的 if (e.code) 与 'code' in e 给出不同答案");
    }

    [Fact]
    public async Task Error_OmitsDataWhenAbsent()
    {
        (JsonDocument reply, _) = await TestHarness.DispatchAsync(
            CreateDispatcher(), "t.fail", null);

        Assert.False(reply.RootElement.TryGetProperty("data", out _));
    }

    [Fact]
    public async Task InternalFailure_CarriesInternalCodeAndDoesNotLeakTheMessage()
    {
        // t.crash 抛的是 InvalidOperationException（带内部路径），
        // 页面应当拿到通用文案 + internal 码——**不是**那条内部路径。
        (JsonDocument reply, _) = await TestHarness.DispatchAsync(
            CreateDispatcher(), "t.crash", null);

        Assert.False(reply.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(OrielIpcErrorCodes.Internal, reply.RootElement.GetProperty("code").GetString());

        string error = reply.RootElement.GetProperty("error").GetString()!;
        Assert.DoesNotContain("secret", error);
        Assert.DoesNotContain("db.sqlite", error);
    }

    [Fact]
    public async Task SuccessReply_HasNoErrorFields()
    {
        (JsonDocument reply, _) = await TestHarness.DispatchAsync(
            CreateDispatcher(), "t.echo", new { text = "hi" });

        Assert.True(reply.RootElement.GetProperty("ok").GetBoolean());
        Assert.False(reply.RootElement.TryGetProperty("code", out _));
        Assert.False(reply.RootElement.TryGetProperty("error", out _));
    }

    // ---- 应用可以自带码与数据 ----

    [Fact]
    public void ApplicationExceptionCanCarryCodeAndDetails()
    {
        using var details = JsonDocument.Parse("""{ "id": 42 }""");
        var ex = new OrielIpcException("不存在 id=42 的待办事项。")
        {
            Code = "todo_not_found",
            Details = details.RootElement.Clone(),
        };

        Assert.Equal("todo_not_found", ex.Code);
        Assert.Equal(42, ex.Details!.Value.GetProperty("id").GetInt32());
        Assert.Equal("不存在 id=42 的待办事项。", ex.Message);
    }

    [Fact]
    public void ApplicationExceptionWithoutCodeHasNullCode()
    {
        // 旧写法的构造器仍然只有文案——不能强迫应用分类它自己的错误
        Assert.Null(new OrielIpcException("boom").Code);
        Assert.Null(new OrielIpcException("boom").Details);
    }

    [Fact]
    public async Task ApplicationExceptionCodeAndDetailsReachTheReply()
    {
        (JsonDocument reply, _) = await TestHarness.DispatchAsync(
            CreateDispatcher(), "t.failWithCode", null);

        Assert.False(reply.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("todo_not_found", reply.RootElement.GetProperty("code").GetString());
        Assert.Equal(42, reply.RootElement.GetProperty("data").GetProperty("id").GetInt32());
    }

    private static OrielCommandDispatcher CreateDispatcher()
    {
        // 实例命令要有工厂才能解析——`TestCommands.Echo` 之类是实例方法。
        // 早先这里传了空字典，于是 `t.echo` 直接失败（表现为"ok 是 false"），
        // 而断言看起来像是错误码没写对。这个坑值得留一行注释。
        var factories = new Dictionary<Type, Func<object>>
        {
            [typeof(TestCommands)] = () => new TestCommands(),
        };

        return new OrielCommandDispatcher(factories, TestJsonContext.Default, TestGuards.AllowAll());
    }
}
