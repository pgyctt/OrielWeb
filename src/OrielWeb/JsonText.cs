using System.Buffers;
using System.Text;
using System.Text.Json;

namespace OrielWeb;

/// <summary>JSON 文本编码辅助（平台层构造回执与页内求值脚本时使用）。</summary>
internal static class JsonText
{
    /// <summary>
    /// 把字符串编码为完整的 JSON 字符串字面量（含外层引号与全部必要转义）；
    /// <paramref name="value"/> 为 null 时返回 <c>null</c> 字面量。
    /// </summary>
    /// <remarks>
    /// 不使用 <c>JsonSerializer.Serialize(value)</c>：其反射重载需要动态代码，
    /// 在 AOT/裁剪下会产生 IL2026/IL3050 警告。也不使用字符串 Replace 手写转义——
    /// 那样极易漏掉 \t \b \f 与全部 U+0000–U+001F 控制字符，产出的非法 JSON
    /// 会让前端连错误都收不到。
    /// </remarks>
    internal static string EncodeString(string? value)
    {
        var buffer = new ArrayBufferWriter<byte>(value is null ? 8 : (value.Length * 2) + 2);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStringValue(value);
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
