using System.Text.Json;
using System.Text.Json.Serialization;
using OrielWeb.Ipc;
using Xunit;

namespace OrielWeb.Tests;

/// <summary>
/// OrielJson 参数提取与返回值写入的单测（不经分发器）。
/// </summary>
/// <remarks>
/// JSON 上下文现在按调用显式传入，**不再有全局静态状态**——所以这个类与 <c>DispatcherTests</c>
/// 不再需要串行执行（以前它们靠 <c>OrielJson.Use</c> 共享一个静态字段，必须同集合串行）。
/// </remarks>
public sealed class OrielJsonTests
{
    /// <summary>应用注册的上下文（相当于应用入口调 <c>UseJsonContext</c> 之后拿到的那个）。</summary>
    private static JsonSerializerContext Ctx => TestJsonContext.Default;

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static string Write(object? value, JsonSerializerContext? context)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        OrielJson.WriteResult(writer, value, context);
        writer.Flush();
        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    // ---- GetRequiredArg ----

    [Fact]
    public void Required_String_ReturnsValue()
        => Assert.Equal("hi", OrielJson.GetRequiredArg<string>(Args("""{"text":"hi"}"""), "text", Ctx));

    [Fact]
    public void Required_Int_ReturnsValue()
        => Assert.Equal(42, OrielJson.GetRequiredArg<int>(Args("""{"id":42}"""), "id", Ctx));

    [Fact]
    public void Required_Primitives_ReturnValues()
    {
        var args = Args("""{"l":9007199254740993,"d":1.5,"b":true,"m":3.25,"f":0.25}""");
        Assert.Equal(9007199254740993L, OrielJson.GetRequiredArg<long>(args, "l", Ctx));
        Assert.Equal(1.5, OrielJson.GetRequiredArg<double>(args, "d", Ctx));
        Assert.True(OrielJson.GetRequiredArg<bool>(args, "b", Ctx));
        Assert.Equal(3.25m, OrielJson.GetRequiredArg<decimal>(args, "m", Ctx));
        Assert.Equal(0.25f, OrielJson.GetRequiredArg<float>(args, "f", Ctx));
    }

    [Fact]
    public void Required_Primitives_WorkWithoutAnyContext()
    {
        // 基元类型不经过上下文，传 null 也必须能用——上下文只对 DTO 是必需的
        Assert.Equal(42, OrielJson.GetRequiredArg<int>(Args("""{"id":42}"""), "id", null));
        Assert.Equal("hi", OrielJson.GetRequiredArg<string>(Args("""{"text":"hi"}"""), "text", null));
    }

    [Fact]
    public void Required_GuidDateTime_ReturnValues()
    {
        var args = Args("""{"g":"7ddfc3c9-1c6b-4d8f-9a4e-9f0a2f1b3c55","t":"2026-09-27T10:00:00Z"}""");
        Assert.Equal(new Guid("7ddfc3c9-1c6b-4d8f-9a4e-9f0a2f1b3c55"), OrielJson.GetRequiredArg<Guid>(args, "g", Ctx));
        Assert.Equal(
            DateTime.Parse("2026-09-27T10:00:00Z").ToUniversalTime(),
            OrielJson.GetRequiredArg<DateTime>(args, "t", Ctx).ToUniversalTime());
    }

    [Fact]
    public void Required_Missing_ThrowsWithName()
    {
        var ex = Assert.Throws<OrielIpcException>(() => OrielJson.GetRequiredArg<string>(Args("{}"), "text", Ctx));
        Assert.Contains("text", ex.Message);
    }

    [Fact]
    public void Required_NullForNonNullable_Throws()
        => Assert.Throws<OrielIpcException>(() => OrielJson.GetRequiredArg<string>(Args("""{"text":null}"""), "text", Ctx));

    [Fact]
    public void Required_WrongKind_Throws()
    {
        Assert.Throws<OrielIpcException>(() => OrielJson.GetRequiredArg<int>(Args("""{"id":"abc"}"""), "id", Ctx));
        Assert.Throws<OrielIpcException>(() => OrielJson.GetRequiredArg<bool>(Args("""{"b":1}"""), "b", Ctx));
    }

    [Fact]
    public void Required_NonObjectArgs_Throws()
        => Assert.Throws<OrielIpcException>(() => OrielJson.GetRequiredArg<string>(Args("[1,2,3]"), "text", Ctx));

    // ---- GetOptionalArg ----

    [Fact]
    public void Optional_Missing_ReturnsDefault()
    {
        Assert.Null(OrielJson.GetOptionalArg<string?>(Args("{}"), "a", Ctx));
        Assert.Null(OrielJson.GetOptionalArg<int?>(Args("{}"), "b", Ctx));
    }

    [Fact]
    public void Optional_Present_ReturnsValue()
    {
        var args = Args("""{"a":"x","b":7}""");
        Assert.Equal("x", OrielJson.GetOptionalArg<string?>(args, "a", Ctx));
        Assert.Equal(7, OrielJson.GetOptionalArg<int?>(args, "b", Ctx));
    }

    [Fact]
    public void Optional_Null_ReturnsDefault()
        => Assert.Null(OrielJson.GetOptionalArg<string?>(Args("""{"a":null}"""), "a", Ctx));

    // ---- DTO（经应用传入的上下文）----

    [Fact]
    public void Required_Dto_Deserializes()
    {
        var dto = OrielJson.GetRequiredArg<TestDto>(Args("""{"d":{"name":"n","value":5,"flag":true}}"""), "d", Ctx);
        Assert.Equal("n", dto.Name);
        Assert.Equal(5, dto.Value);
        Assert.True(dto.Flag);
    }

    [Fact]
    public void Required_Dto_WithoutContext_ThrowsWithGuidance()
    {
        var args = Args("""{"d":{"name":"n"}}""");
        var ex = Assert.Throws<OrielIpcException>(
            () => OrielJson.GetRequiredArg<TestDto>(args, "d", new EmptyJsonContext()));

        Assert.Contains("UseJsonContext", ex.Message);
    }

    [Fact]
    public void Required_Dto_WithNullContext_ThrowsWithGuidance()
    {
        var args = Args("""{"d":{"name":"n"}}""");
        var ex = Assert.Throws<OrielIpcException>(() => OrielJson.GetRequiredArg<TestDto>(args, "d", null));

        Assert.Contains("UseJsonContext", ex.Message);
    }

    // ---- WriteResult ----

    [Fact]
    public void WriteResult_Primitives_ProduceExpectedJson()
    {
        Assert.Equal("null", Write(null, Ctx));
        Assert.Equal("\"s\"", Write("s", Ctx));
        Assert.Equal("true", Write(true, Ctx));
        Assert.Equal("false", Write(false, Ctx));
        Assert.Equal("42", Write(42, Ctx));
        Assert.Equal("9007199254740993", Write(9007199254740993L, Ctx));
        Assert.Equal("1.5", Write(1.5, Ctx));
        Assert.Equal("3.25", Write(3.25m, Ctx));
        var guid = new Guid("7ddfc3c9-1c6b-4d8f-9a4e-9f0a2f1b3c55");
        Assert.Equal($"\"{guid}\"", Write(guid, Ctx));
    }

    [Fact]
    public void WriteResult_Dto_UsesGivenContext()
    {
        var parsed = JsonDocument.Parse(Write(new TestDto("n", 5, true), Ctx)).RootElement;
        Assert.Equal("n", parsed.GetProperty("name").GetString());
        Assert.Equal(5, parsed.GetProperty("value").GetInt32());
        Assert.True(parsed.GetProperty("flag").GetBoolean());
    }

    [Fact]
    public void WriteResult_UnregisteredType_ThrowsWithGuidance()
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);

        Assert.Throws<OrielIpcException>(
            () => OrielJson.WriteResult(writer, new UnregisteredDto("x"), new EmptyJsonContext()));
    }
}
