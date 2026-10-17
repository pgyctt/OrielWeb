using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;

namespace OrielWeb.Tests;

/// <summary>
/// 把匿名对象编成 JSON——给直接调内建命令处理函数的测试用。
/// </summary>
/// <remarks>
/// <para>
/// 测试项目不开源生成上下文，所以不能走 <c>JsonSerializer.Serialize&lt;T&gt;</c>
/// （AOT 分析下会报 IL2026/IL3050），于是这里手写一个只认测试夹具用到的那几种类型的
/// 写出器：字符串、数字、布尔、<c>byte[]</c>、<c>object[]</c>、匿名对象。
/// </para>
/// <para>
/// <b>为什么可以在这里用反射</b>：这是**测试程序集**，永远不以 AOT 发布，
/// 而且传进来的都是各测试文件里的匿名类型——生产代码里没有这条路径。
/// 之所以抑制告警而不是改成手拼 JSON 字符串：后者要改几十个调用点，
/// 而调用点的可读性（<c>new { path = file, data = "x" }</c>）正是它值得保留的地方。
/// </para>
/// <para>
/// 属性名统一**首字母小写**（<c>OldPath</c> → <c>oldPath</c>）：命令的参数字段是
/// camelCase（见各命令族的 <c>GetRequiredArg</c>）。C# 匿名类型的属性名若照抄成
/// <c>OldPath</c> 就会与宿主侧对不上，而那个错误看起来像"缺参数"。
/// </para>
/// </remarks>
internal static class TestArgs
{
    internal static string Serialize(object args)
    {
        using var stream = new MemoryStream();

        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteValue(writer, args);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2075",
        Justification = "测试夹具专用：对象都是测试文件里的匿名类型，且测试程序集不以 AOT 发布。")]
    private static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                return;
            case string s:
                writer.WriteStringValue(s);
                return;
            case bool b:
                writer.WriteBooleanValue(b);
                return;
            case int i:
                writer.WriteNumberValue(i);
                return;
            case long l:
                writer.WriteNumberValue(l);
                return;
            case double d:
                writer.WriteNumberValue(d);
                return;
            case byte[] bytes:
                writer.WriteStartArray();
                foreach (byte item in bytes)
                {
                    writer.WriteNumberValue(item);
                }

                writer.WriteEndArray();
                return;
            case object[] items:
                writer.WriteStartArray();
                foreach (object item in items)
                {
                    WriteValue(writer, item);
                }

                writer.WriteEndArray();
                return;
        }

        // 匿名对象：属性逐个写（首字母小写，见类型注释）
        writer.WriteStartObject();

        foreach (System.Reflection.PropertyInfo property in value.GetType().GetProperties())
        {
            writer.WritePropertyName(char.ToLowerInvariant(property.Name[0]) + property.Name[1..]);
            WriteValue(writer, property.GetValue(value));
        }

        writer.WriteEndObject();
    }
}
