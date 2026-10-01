using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace OrielWeb.Ipc;

/// <summary>
/// IPC 的 JSON 桥。源生成器生成的路由通过这里完成参数提取与返回值写入。
/// 全部基于 System.Text.Json 源生成契约，运行期零反射（Native AOT 安全）。
/// </summary>
/// <remarks>
/// <para>
/// <b>JSON 上下文按应用实例持有，这里没有任何全局静态状态。</b>需要上下文的入口都把
/// <see cref="JsonSerializerContext"/> 作为显式参数收下，由 <see cref="OrielCommandDispatcher"/>
/// （每个 <see cref="OrielApp"/> 一个）透传进来。
/// 早先它是一个 <c>static</c> 字段，于是同一进程里创建第二个应用会互相串——测试、或"一个进程
/// 托管多个宿主"的场景都会踩到，而且表现是**静默地用了别人的类型信息**。
/// </para>
/// <para>
/// 上下文由应用入口调用 <c>OrielAppBuilder.UseJsonContext(JsonSerializerContext)</c> 注册
/// （STJ 源生成产物）；基元类型不需要上下文，DTO（自定义类型）需要。
/// </para>
/// </remarks>
public static class OrielJson
{
    internal static JsonTypeInfo? Resolve(Type type, JsonSerializerContext? jsonContext)
    {
        if (jsonContext is null)
        {
            return null;
        }
        try
        {
            return jsonContext.GetTypeInfo(type);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------
    // 参数提取
    //
    // 两条路径：
    //   1) 直出路径：源生成器在编译期已知参数类型，直接生成
    //      RequireArgElement / RequireArgKind + element.GetXxx() 调用，运行期无 typeof 分派。
    //   2) 泛型路径（fallback）：char / Guid / DateTime / DateTimeOffset / DTO 等
    //      仍走 GetRequiredArg<T> / GetOptionalArg<T>，其内部分派只发生在这条路径上。
    // ------------------------------------------------------------------

    /// <summary>取出必需参数元素：属性缺失或为 null 时抛 <see cref="OrielIpcException"/>。</summary>
    public static JsonElement RequireArgElement(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var element))
        {
            throw new OrielIpcException($"命令缺少必需参数 '{name}'。");
        }
        if (element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            throw new OrielIpcException($"命令参数 '{name}' 不能为 null（声明为非可空）。");
        }
        return element;
    }

    /// <summary>取出可选参数元素；属性缺失或为 null 时返回 false（<paramref name="element"/> 为 default）。</summary>
    public static bool TryGetArgElement(JsonElement args, string name, out JsonElement element)
    {
        if (args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty(name, out element)
            && element.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            return true;
        }
        element = default;
        return false;
    }

    /// <summary>校验元素 kind 属于允许集合，否则抛 <see cref="OrielIpcException"/>。</summary>
    public static void RequireArgKind(JsonElement element, string name, string display, params JsonValueKind[] kinds)
    {
        foreach (var kind in kinds)
        {
            if (element.ValueKind == kind)
            {
                return;
            }
        }
        throw BadArgKind(name, element, display);
    }

    /// <summary>提取必需命名参数。属性缺失或为 null（对非可空类型）时抛出 <see cref="OrielIpcException"/>。</summary>
    /// <param name="args">命令的 args 元素。</param>
    /// <param name="name">参数名。</param>
    /// <param name="jsonContext">应用注册的 STJ 源生成上下文（DTO 参数需要；基元类型可传 null）。</param>
    public static T GetRequiredArg<T>(JsonElement args, string name, JsonSerializerContext? jsonContext)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var element))
        {
            throw new OrielIpcException($"命令缺少必需参数 '{name}'。");
        }
        if (element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            throw new OrielIpcException($"命令参数 '{name}' 不能为 null（声明为非可空）。");
        }
        return DeserializeArg<T>(element, name, jsonContext);
    }

    /// <summary>提取可选命名参数（可空类型）。属性缺失或为 null 时返回默认值。</summary>
    public static T? GetOptionalArg<T>(JsonElement args, string name, JsonSerializerContext? jsonContext)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var element))
        {
            return default;
        }
        if (element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return default;
        }
        return DeserializeArg<T>(element, name, jsonContext);
    }

    private static T DeserializeArg<T>(JsonElement element, string name, JsonSerializerContext? jsonContext)
    {
        // 基元类型快速路径（typeof 比较，无反射）
        switch (typeof(T))
        {
            case var t when t == typeof(string):
                if (element.ValueKind != JsonValueKind.String)
                {
                    throw BadArgKind(name, element, "string");
                }
                return (T)(object)element.GetString()!;
            case var t when t == typeof(bool):
                RequireKind(element, JsonValueKind.True, JsonValueKind.False, name, "bool");
                return (T)(object)element.GetBoolean();
            case var t when t == typeof(int):
                RequireKind(element, JsonValueKind.Number, name, "int");
                return (T)(object)element.GetInt32();
            case var t when t == typeof(long):
                RequireKind(element, JsonValueKind.Number, name, "long");
                return (T)(object)element.GetInt64();
            case var t when t == typeof(double):
                RequireKind(element, JsonValueKind.Number, name, "double");
                return (T)(object)element.GetDouble();
            case var t when t == typeof(float):
                RequireKind(element, JsonValueKind.Number, name, "float");
                return (T)(object)(float)element.GetDouble();
            case var t when t == typeof(decimal):
                RequireKind(element, JsonValueKind.Number, name, "decimal");
                return (T)(object)element.GetDecimal();
            case var t when t == typeof(short):
                RequireKind(element, JsonValueKind.Number, name, "short");
                return (T)(object)element.GetInt16();
            case var t when t == typeof(ushort):
                RequireKind(element, JsonValueKind.Number, name, "ushort");
                return (T)(object)element.GetUInt16();
            case var t when t == typeof(uint):
                RequireKind(element, JsonValueKind.Number, name, "uint");
                return (T)(object)element.GetUInt32();
            case var t when t == typeof(ulong):
                RequireKind(element, JsonValueKind.Number, name, "ulong");
                return (T)(object)element.GetUInt64();
            case var t when t == typeof(byte):
                RequireKind(element, JsonValueKind.Number, name, "byte");
                return (T)(object)element.GetByte();
            case var t when t == typeof(sbyte):
                RequireKind(element, JsonValueKind.Number, name, "sbyte");
                return (T)(object)element.GetSByte();
            case var t when t == typeof(char):
                if (element.ValueKind != JsonValueKind.String || element.GetString()!.Length != 1)
                {
                    throw BadArgKind(name, element, "char");
                }
                return (T)(object)element.GetString()![0];
            case var t when t == typeof(Guid):
                RequireKind(element, JsonValueKind.String, name, "Guid");
                if (!element.TryGetGuid(out var guid))
                {
                    throw BadArgKind(name, element, "Guid");
                }
                return (T)(object)guid;
            case var t when t == typeof(DateTime):
                RequireKind(element, JsonValueKind.String, name, "DateTime");
                return (T)(object)element.GetDateTime();
            case var t when t == typeof(DateTimeOffset):
                RequireKind(element, JsonValueKind.String, name, "DateTimeOffset");
                return (T)(object)element.GetDateTimeOffset();
            case var t when t == typeof(JsonElement):
                return (T)(object)element.Clone();
            case var t when t == typeof(bool?):
                RequireKind(element, JsonValueKind.True, JsonValueKind.False, name, "bool?");
                return (T)(object)element.GetBoolean();
            case var t when t == typeof(int?):
                RequireKind(element, JsonValueKind.Number, name, "int?");
                return (T)(object)element.GetInt32();
            case var t when t == typeof(long?):
                RequireKind(element, JsonValueKind.Number, name, "long?");
                return (T)(object)element.GetInt64();
            case var t when t == typeof(double?):
                RequireKind(element, JsonValueKind.Number, name, "double?");
                return (T)(object)element.GetDouble();
            case var t when t == typeof(decimal?):
                RequireKind(element, JsonValueKind.Number, name, "decimal?");
                return (T)(object)element.GetDecimal();
            case var t when t == typeof(float?):
                RequireKind(element, JsonValueKind.Number, name, "float?");
                return (T)(object)(float)element.GetDouble();
        }

        // DTO：走应用注册的 JsonSerializerContext
        var typeInfo = Resolve(typeof(T), jsonContext);
        if (typeInfo is null)
        {
            throw new OrielIpcException(
                $"参数 '{name}' 的类型 {typeof(T)} 需要 JSON 上下文：请在应用入口调用 " +
                $".UseJsonContext(context)，并在该 JsonSerializerContext 上为 {typeof(T)} 添加 [JsonSerializable]；" +
                "或改用基元类型/string/JsonElement 参数。");
        }
        try
        {
            // 直接对 JsonElement 反序列化：GetRawText() 会分配完整字符串副本再让 STJ 解析第二遍
            return (T)element.Deserialize(typeInfo)!;
        }
        catch (JsonException ex)
        {
            throw new OrielIpcException($"参数 '{name}' 反序列化为 {typeof(T)} 失败：{ex.Message}");
        }
    }

    private static void RequireKind(JsonElement element, JsonValueKind expected, string name, string display)
        => RequireKind(element, expected, expected, name, display);

    private static void RequireKind(JsonElement element, JsonValueKind expected1, JsonValueKind expected2, string name, string display)
    {
        if (element.ValueKind != expected1 && element.ValueKind != expected2)
        {
            throw BadArgKind(name, element, display);
        }
    }

    private static OrielIpcException BadArgKind(string name, JsonElement element, string expected)
        => new($"命令参数 '{name}' 的类型不符：期望 {expected}，实际 {element.ValueKind}。");

    // ------------------------------------------------------------------
    // 返回值写入
    // ------------------------------------------------------------------

    /// <summary>把命令返回值写入 <paramref name="writer"/>。基元类型直写；DTO 经应用注册的上下文序列化。</summary>
    /// <param name="writer">目标写入器。</param>
    /// <param name="value">命令返回值。</param>
    /// <param name="jsonContext">应用注册的 STJ 源生成上下文（DTO 返回值需要；基元类型可传 null）。</param>
    public static void WriteResult(Utf8JsonWriter writer, object? value, JsonSerializerContext? jsonContext)
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
            case float f:
                writer.WriteNumberValue(f);
                return;
            case decimal m:
                writer.WriteNumberValue(m);
                return;
            case short s16:
                writer.WriteNumberValue(s16);
                return;
            case ushort u16:
                writer.WriteNumberValue(u16);
                return;
            case uint u32:
                writer.WriteNumberValue(u32);
                return;
            case ulong u64:
                writer.WriteNumberValue(u64);
                return;
            case byte u8:
                writer.WriteNumberValue(u8);
                return;
            case sbyte s8:
                writer.WriteNumberValue(s8);
                return;
            case char c:
                writer.WriteStringValue(c.ToString());
                return;
            case Guid g:
                writer.WriteStringValue(g);
                return;
            case DateTime dt:
                writer.WriteStringValue(dt);
                return;
            case DateTimeOffset dto:
                writer.WriteStringValue(dto);
                return;
            case JsonElement je:
                je.WriteTo(writer);
                return;
        }

        var type = value.GetType();
        var typeInfo = Resolve(type, jsonContext);
        if (typeInfo is null)
        {
            throw new OrielIpcException(
                $"命令返回类型 {type} 需要 JSON 上下文：请在应用入口调用 .UseJsonContext(context)，" +
                $"并在该 JsonSerializerContext 上为 {type} 添加 [JsonSerializable]；或改用基元类型/string 返回。");
        }
        JsonSerializer.Serialize(writer, value, typeInfo);
    }
}
