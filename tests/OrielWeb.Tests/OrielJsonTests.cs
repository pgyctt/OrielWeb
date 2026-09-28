using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using OrielWeb.Ipc;
using Xunit;

namespace OrielWeb.Tests;

/// <summary>
/// OrielJson 参数提取与返回值写入的单测（不经分发器）。
/// OrielJson.Use 是全局静态状态，与 DispatcherTests 同集合串行执行。
/// </summary>
[Collection("IpcSerial")]
public sealed class OrielJsonTests
{
    public OrielJsonTests() => OrielJson.Use(TestJsonContext.Default);

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static string Write(object? value)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        OrielJson.WriteResult(writer, value);
        writer.Flush();
        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    // ---- GetRequiredArg ----

    [Fact]
    public void Required_String_ReturnsValue()
        => Assert.Equal("hi", OrielJson.GetRequiredArg<string>(Args("""{"text":"hi"}"""), "text"));

    [Fact]
    public void Required_Int_ReturnsValue()
        => Assert.Equal(42, OrielJson.GetRequiredArg<int>(Args("""{"id":42}"""), "id"));

    [Fact]
    public void Required_Primitives_ReturnValues()
    {
        var args = Args("""{"l":9007199254740993,"d":1.5,"b":true,"m":3.25,"f":0.25}""");
        Assert.Equal(9007199254740993L, OrielJson.GetRequiredArg<long>(args, "l"));
        Assert.Equal(1.5, OrielJson.GetRequiredArg<double>(args, "d"));
        Assert.True(OrielJson.GetRequiredArg<bool>(args, "b"));
        Assert.Equal(3.25m, OrielJson.GetRequiredArg<decimal>(args, "m"));
        Assert.Equal(0.25f, OrielJson.GetRequiredArg<float>(args, "f"));
    }

    [Fact]
    public void Required_GuidDateTime_ReturnValues()
    {
        var args = Args("""{"g":"7ddfc3c9-1c6b-4d8f-9a4e-9f0a2f1b3c55","t":"2026-09-27T10:00:00Z"}""");
        Assert.Equal(new Guid("7ddfc3c9-1c6b-4d8f-9a4e-9f0a2f1b3c55"), OrielJson.GetRequiredArg<Guid>(args, "g"));
        Assert.Equal(
            DateTime.Parse("2026-09-27T10:00:00Z").ToUniversalTime(),
            OrielJson.GetRequiredArg<DateTime>(args, "t").ToUniversalTime());
    }

    [Fact]
    public void Required_Missing_ThrowsWithName()
    {
        var ex = Assert.Throws<OrielIpcException>(() => OrielJson.GetRequiredArg<string>(Args("{}"), "text"));
        Assert.Contains("text", ex.Message);
    }

    [Fact]
    public void Required_NullForNonNullable_Throws()
        => Assert.Throws<OrielIpcException>(() => OrielJson.GetRequiredArg<string>(Args("""{"text":null}"""), "text"));

    [Fact]
    public void Required_WrongKind_Throws()
    {
        Assert.Throws<OrielIpcException>(() => OrielJson.GetRequiredArg<int>(Args("""{"id":"abc"}"""), "id"));
        Assert.Throws<OrielIpcException>(() => OrielJson.GetRequiredArg<bool>(Args("""{"b":1}"""), "b"));
    }

    [Fact]
    public void Required_NonObjectArgs_Throws()
        => Assert.Throws<OrielIpcException>(() => OrielJson.GetRequiredArg<string>(Args("[1,2,3]"), "text"));

    // ---- GetOptionalArg ----

    [Fact]
    public void Optional_Missing_ReturnsDefault()
    {
        Assert.Null(OrielJson.GetOptionalArg<string?>(Args("{}"), "a"));
        Assert.Null(OrielJson.GetOptionalArg<int?>(Args("{}"), "b"));
    }

    [Fact]
    public void Optional_Present_ReturnsValue()
    {
        var args = Args("""{"a":"x","b":7}""");
        Assert.Equal("x", OrielJson.GetOptionalArg<string?>(args, "a"));
        Assert.Equal(7, OrielJson.GetOptionalArg<int?>(args, "b"));
    }

    [Fact]
    public void Optional_Null_ReturnsDefault()
        => Assert.Null(OrielJson.GetOptionalArg<string?>(Args("""{"a":null}"""), "a"));

    // ---- DTO（经注册上下文）----

    [Fact]
    public void Required_Dto_Deserializes()
    {
        var dto = OrielJson.GetRequiredArg<TestDto>(Args("""{"d":{"name":"n","value":5,"flag":true}}"""), "d");
        Assert.Equal("n", dto.Name);
        Assert.Equal(5, dto.Value);
        Assert.True(dto.Flag);
    }

    [Fact]
    public void Required_Dto_WithoutContext_ThrowsWithGuidance()
    {
        OrielJson.Use(new EmptyContext());
        try
        {
            var args = Args("""{"d":{"name":"n"}}""");
            var ex = Assert.Throws<OrielIpcException>(() => OrielJson.GetRequiredArg<TestDto>(args, "d"));
            Assert.Contains("UseJsonContext", ex.Message);
        }
        finally
        {
            OrielJson.Use(TestJsonContext.Default);
        }
    }

    private sealed class EmptyContext : JsonSerializerContext
    {
        public EmptyContext() : base(null) { }
        protected override JsonSerializerOptions? GeneratedSerializerOptions => null;
        public override JsonTypeInfo? GetTypeInfo(Type type) => null;
    }

    // ---- WriteResult ----

    [Fact]
    public void WriteResult_Primitives_ProduceExpectedJson()
    {
        Assert.Equal("null", Write(null));
        Assert.Equal("\"s\"", Write("s"));
        Assert.Equal("true", Write(true));
        Assert.Equal("false", Write(false));
        Assert.Equal("42", Write(42));
        Assert.Equal("9007199254740993", Write(9007199254740993L));
        Assert.Equal("1.5", Write(1.5));
        Assert.Equal("3.25", Write(3.25m));
        var guid = new Guid("7ddfc3c9-1c6b-4d8f-9a4e-9f0a2f1b3c55");
        Assert.Equal($"\"{guid}\"", Write(guid));
    }

    [Fact]
    public void WriteResult_Dto_UsesRegisteredContext()
    {
        var parsed = JsonDocument.Parse(Write(new TestDto("n", 5, true))).RootElement;
        Assert.Equal("n", parsed.GetProperty("name").GetString());
        Assert.Equal(5, parsed.GetProperty("value").GetInt32());
        Assert.True(parsed.GetProperty("flag").GetBoolean());
    }

    [Fact]
    public void WriteResult_UnregisteredType_ThrowsWithGuidance()
    {
        OrielJson.Use(new EmptyContext());
        try
        {
            var buffer = new System.Buffers.ArrayBufferWriter<byte>();
            using var writer = new Utf8JsonWriter(buffer);
            Assert.Throws<OrielIpcException>(() => OrielJson.WriteResult(writer, new UnregisteredDto("x")));
        }
        finally
        {
            OrielJson.Use(TestJsonContext.Default);
        }
    }
}
