using System.Collections.Concurrent;
using System.Text.Json;
using OrielWeb.Ipc;

namespace OrielWeb.Platform.MacOS;

/// <summary>
/// macOS 脚本消息处理：接收 { __oriel:'invoke' }（走分发器）与
/// { __oriel:'evalResult' }（完成 ExecuteScriptAsync 的页面回环）。
/// </summary>
internal sealed class MacOSWebMessageHandler : IIpcReplySink
{
    private readonly MacOSWindowHost _host;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<string>> _pendingEvals = [];

    public MacOSWebMessageHandler(MacOSWindowHost host)
    {
        _host = host;
    }

    /// <summary>由 ObjC trampoline 调用（UI 线程）。json 为页面 postMessage 的字符串。</summary>
    public int OnScriptMessage(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return 0;
            }

            var kind = root.TryGetProperty("__oriel", out var kindElement) && kindElement.ValueKind == JsonValueKind.String
                ? kindElement.GetString()
                : null;

            switch (kind)
            {
                case "invoke":
                    // 门禁在分发器里做：它有回执通道，能把"为什么被拒"送回页面，
                    // 而不是让那个 Promise 干等到 30 秒超时。
                    _ = DispatchInvokeAsync(root.Clone());
                    break;
                case "evalResult":
                    if (Accept(root))
                    {
                        CompleteEval(root);
                    }
                    break;
                case "console":
                    // 只有窗口选项打开了 console 转发，注入的桥接脚本才会发这类消息
                    if (Accept(root))
                    {
                        _host.RaiseConsoleMessage(ReadString(root, "level"), ReadString(root, "text"));
                    }
                    break;
                case "message":
                    // 单向消息没有命令名，因此只过来源与令牌那一层；
                    // 命令授权（allow/deny）只管 invoke，原因见 OrielCapabilityOptions。
                    if (Accept(root))
                    {
                        _host.RaiseMessageReceived(
                            ReadString(root, "name"),
                            root.TryGetProperty("payload", out var payload) ? payload.GetRawText() : "null");
                    }
                    break;
            }
        }
        catch (JsonException)
        {
            // 非 OrielWeb 消息，忽略
        }
        return 0;
    }

    /// <summary>入站消息的来源 + 令牌校验（命令授权不在这里，见分发器）。</summary>
    private bool Accept(JsonElement root)
    {
        if (_host.App.Guard.TryAccept(_host.CurrentUrl, root, out string? rejection))
        {
            return true;
        }

        OrielIpcGuard.Report(rejection);
        return false;
    }

    private async Task DispatchInvokeAsync(JsonElement message)
    {
        try
        {
            await _host.App.Dispatcher.HandleInvokeAsync(message, this, _host.CurrentUrl, _host.Window).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            PostJson($"{{\"__oriel\":\"result\",\"id\":0,\"ok\":false,\"error\":{JsonText.EncodeString(ex.Message)}}}");
        }
    }

    private void CompleteEval(JsonElement root)
    {
        var id = root.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.Number
            ? idElement.GetInt32()
            : 0;
        var json = root.TryGetProperty("json", out var jsonElement) && jsonElement.ValueKind == JsonValueKind.String
            ? jsonElement.GetString()
            : null;
        if (_pendingEvals.TryRemove(id, out var completion))
        {
            if (json is not null && json.StartsWith("E:", StringComparison.Ordinal))
            {
                completion.TrySetException(new InvalidOperationException($"ExecuteScript 失败：{json[2..]}"));
            }
            else
            {
                completion.TrySetResult(json ?? "null");
            }
        }
    }

    internal void RegisterEval(int id, TaskCompletionSource<string> completion) => _pendingEvals[id] = completion;

    /// <summary>读字符串属性；缺失或类型不符时返回空串（页面数据不可信，一律按可缺席处理）。</summary>
    private static string ReadString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    // ---- IIpcReplySink（IPC 回执）：必须切回主线程（AppKit 主线程约束）----

    public void PostJson(string json)
    {
        if (_host.IsOnUiThread())
        {
            _host.PostWebMessageOnUi(json);
            return;
        }
        _host.PostToMainThread(() => _host.PostWebMessageOnUi(json));
    }
}
