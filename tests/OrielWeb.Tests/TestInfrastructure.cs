using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using OrielWeb.Ipc;
using Xunit;

namespace OrielWeb.Tests;

/// <summary>测试 DTO。</summary>
public sealed record TestDto(string Name, int Value, bool Flag);

/// <summary>未注册进上下文的 DTO（用于验证未注册类型的错误路径）。</summary>
public sealed record UnregisteredDto(string X);

/// <summary>测试用 STJ 源生成上下文。</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(TestDto))]
internal sealed partial class TestJsonContext : JsonSerializerContext;

/// <summary>
/// 空 JSON 上下文：对任何类型都返回 null。
/// </summary>
/// <remarks>
/// 两个用途：验证"DTO 类型未注册"的错误路径；以及验证**两个应用各持一个上下文时互不干扰**
/// （这正是把上下文从静态字段改成按实例持有的目的）。
/// </remarks>
internal sealed class EmptyJsonContext : JsonSerializerContext
{
    public EmptyJsonContext() : base(null) { }

    protected override JsonSerializerOptions? GeneratedSerializerOptions => null;

    public override JsonTypeInfo? GetTypeInfo(Type type) => null;
}

/// <summary>测试命令类（源生成器会为其生成路由并经 ModuleInitializer 自动注册）。</summary>
public sealed partial class TestCommands
{
    private static int _counter;

    [OrielCommand("t.echo")]
    public string Echo(string text) => text;

    [OrielCommand("t.add")]
    public int Add(int a, int b) => a + b;

    [OrielCommand("t.opt")]
    public string Opt(string? a, int? b) => $"{a ?? "null"}|{b?.ToString() ?? "null"}";

    [OrielCommand("t.dto")]
    public TestDto Dto(TestDto input) => input with { Value = input.Value + 1 };

    [OrielCommand("t.async")]
    public async Task<string> Async(int delayMs)
    {
        await Task.Delay(delayMs);
        return "done";
    }

    [OrielCommand("t.void")]
    public void DoVoid()
    {
    }

    [OrielCommand("t.fail")]
    public void Fail() => throw new OrielIpcException("boom");

    [OrielCommand("t.counter")]
    public int Counter() => Interlocked.Increment(ref _counter);

    [OrielCommand("t.raw")]
    public string Raw(JsonElement args) => args.GetRawText();

    [OrielCommand("t.staticEcho")]
    public static string StaticEcho(string text) => "S:" + text;
}

/// <summary>只有实例命令、但不会注册工厂的命令类（验证缺工厂的错误路径）。</summary>
public sealed partial class NoFactoryCommands
{
    [OrielCommand("nf.hello")]
    public string Hello() => "hi";
}

/// <summary>捕获回执 JSON 的 IIpcReplySink 测试实现（经 InternalsVisibleTo 访问内部接口）。</summary>
internal sealed class TestSink : IIpcReplySink
{
    public List<string> Replies { get; } = [];

    public void PostJson(string json) => Replies.Add(json);
}

/// <summary>构造 invoke 消息与断言回执的工具。</summary>
internal static class TestHarness
{
    public const string Prefix = "OrielWeb.Tests.";

    /// <summary>构造 { __oriel:'invoke', id, name, args } 消息并执行分发，返回回执 JSON 文档。</summary>
    // 测试专用反射序列化（匿名对象作参数）。测试程序集不参与 Native AOT 发布，
    // 因此在此抑制，而不是把 RequiresUnreferencedCode/RequiresDynamicCode 沿调用链传播给每个用例。
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(
        "Trimming", "IL2026", Justification = "测试专用反射序列化，测试程序集不参与 AOT 发布。")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(
        "AOT", "IL3050", Justification = "测试专用反射序列化，测试程序集不参与 AOT 发布。")]
    public static async Task<(JsonDocument Reply, TestSink Sink)> DispatchAsync(
        OrielCommandDispatcher dispatcher, string name, object? args, int id = 1)
    {
        var sink = new TestSink();
        var argsJson = args is null ? "null" : JsonSerializer.Serialize(args);
        var message = $$"""{ "__oriel": "invoke", "id": {{id}}, "name": "{{name}}", "args": {{argsJson}} }""";
        using var doc = JsonDocument.Parse(message);
        await dispatcher.HandleInvokeAsync(doc.RootElement.Clone(), sink);
        Assert.Single(sink.Replies);
        return (JsonDocument.Parse(sink.Replies[0]), sink);
    }

    public static JsonElement Value(this JsonDocument reply)
    {
        if (!reply.RootElement.TryGetProperty("value", out var value))
        {
            throw new Xunit.Sdk.XunitException("回执中没有 value 字段，原始回执：" + reply.RootElement.ToString());
        }
        return value;
    }
}
