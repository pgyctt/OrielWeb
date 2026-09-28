using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Buffers;

[assembly: InternalsVisibleTo("OrielWeb.Tests")]

namespace OrielWeb.Ipc;

/// <summary>把回执 JSON 发回前端（由平台后端实现；实现负责切回 UI 线程）。</summary>
internal interface IIpcReplySink
{
    void PostJson(string json);
}

/// <summary>
/// IPC 分发核心：解析 invoke 消息 → 查路由 → 执行命令 → 回执。
/// 消息协议：{ "__oriel":"invoke", "id":1, "name":"todo.add", "args":{...} }
/// 回执协议：{ "__oriel":"result", "id":1, "ok":true, "value":... | "ok":false, "error":"..." }
/// </summary>
internal sealed class OrielCommandDispatcher
{
    private readonly Dictionary<Type, Func<object>> _factories;
    private readonly ConcurrentDictionary<Type, object> _targets = [];
    private readonly Dictionary<string, (IOrielCommandRouter Router, int Index)> _routes;

    public OrielCommandDispatcher(Dictionary<Type, Func<object>> factories)
    {
        _factories = factories;
        // 一次性构建「命令名 → (路由, 索引)」索引。此前是每条消息遍历全部 router：
        // O(router 数) + 每次加锁 + 一次数组分配（Snapshot 里的 [.. s_routers]），
        // README 声称的 O(1) 分发实际只成立于单个 router 内部。
        _routes = OrielCommandRegistry.BuildIndex();
    }

    public async ValueTask HandleInvokeAsync(JsonElement message, IIpcReplySink sink)
    {
        if (message.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        int id = message.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.Number
            ? idElement.GetInt32()
            : 0;
        string? name = message.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
            ? nameElement.GetString()
            : null;

        if (string.IsNullOrEmpty(name))
        {
            ReplyError(sink, id, "invoke 消息缺少 name 字段。");
            return;
        }

        JsonElement args = message.TryGetProperty("args", out var argsElement) ? argsElement : default;

        if (!_routes.TryGetValue(name, out var route))
        {
            ReplyError(sink, id, $"未知命令 '{name}'。请确认方法已标注 [OrielCommand] 且所在程序集被应用引用。");
            return;
        }

        try
        {
            object? target = route.Router.RequiresTarget ? ResolveTarget(route.Router.TargetType!) : null;
            var result = await route.Router.InvokeAsync(route.Index, target, args).ConfigureAwait(false);
            ReplyOk(sink, id, result);
        }
        catch (Exception ex)
        {
            ReplyError(sink, id, ex.Message);
        }
    }

    private object ResolveTarget(Type targetType)
    {
        // GetOrAdd 在竞态下可能重复调用工厂（多次创建、留最后一个），
        // 命令实例无单例语义要求时无害；需要精确单例的应用应自行保证工厂幂等
        return _targets.GetOrAdd(targetType, static (type, factories) =>
        {
            if (!factories.TryGetValue(type, out var factory))
            {
                throw new OrielIpcException(
                    $"命令类型 {type} 含实例命令，但未注册：请调用 OrielAppBuilder.AddCommands<{type.Name}>()。");
            }
            return factory();
        }, _factories);
    }

    private static void ReplyOk(IIpcReplySink sink, int id, object? value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("__oriel", "result");
            writer.WriteNumber("id", id);
            writer.WriteBoolean("ok", true);
            writer.WritePropertyName("value");
            OrielJson.WriteResult(writer, value);
            writer.WriteEndObject();
        }
        sink.PostJson(Encoding.UTF8.GetString(buffer.WrittenSpan));
    }

    private static void ReplyError(IIpcReplySink sink, int id, string error)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("__oriel", "result");
            writer.WriteNumber("id", id);
            writer.WriteBoolean("ok", false);
            writer.WriteString("error", error);
            writer.WriteEndObject();
        }
        sink.PostJson(Encoding.UTF8.GetString(buffer.WrittenSpan));
    }
}
