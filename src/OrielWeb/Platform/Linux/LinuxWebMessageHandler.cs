using System.Collections.Concurrent;
using System.Text.Json;
using OrielWeb.Ipc;

namespace OrielWeb.Platform.Linux;

/// <summary>
/// Linux 脚本消息处理：接收 { __oriel:'invoke' }（走分发器）与
/// { __oriel:'evalResult' }（完成 ExecuteScriptAsync 的页面回环）。
/// </summary>
internal sealed class LinuxWebMessageHandler : IIpcReplySink
{
    private readonly LinuxWindowHost _host;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<string>> _pendingEvals = [];

    public LinuxWebMessageHandler(LinuxWindowHost host)
    {
        _host = host;
    }

    /// <summary>由 GTK 信号 trampoline 调用（UI 线程）。json 为页面 postMessage 的字符串。</summary>
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

    /// <summary>
    /// 入站消息的来源 + 令牌校验（命令授权不在这里，见分发器）。
    /// </summary>
    /// <remarks>
    /// 来源用"顶级导航 URL 快照"（<see cref="LinuxWindowHost.CurrentUrl"/>）：WebKitGTK 的
    /// 经典 script-message 信号（WebKitJavascriptResult）不带 frame 信息，逐消息来源拿不到——
    /// 这是与 Windows（WebMessageReceived.Source）/macOS（frameInfo.securityOrigin）的已知
    /// 差距：第一层防线（注入期自检，不可信页面不装桥）与第三层（令牌）仍完整，第二层退化为
    /// 快照判定。
    /// </remarks>
    private bool Accept(JsonElement root)
    {
        // 拒绝的记账在门禁内部（Debug 输出 + IpcRejected 事件），这里只返回结论。
        return _host.App.Guard.TryAccept(_host.CurrentUrl, root, out _);
    }

    private async Task DispatchInvokeAsync(JsonElement message)
    {
        try
        {
            await _host.App.Dispatcher.HandleInvokeAsync(message, this, _host.CurrentUrl, _host.Window).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 分发器内部已按命令捕获并净化；能到这里的是分发器之外的意外。
            // 同样不透传 ex.Message（可能含内部路径），文案与分发器保持同一口径。
            System.Diagnostics.Debug.WriteLine($"[OrielWeb] IPC 分发意外失败：{ex}");
            PostJson($"{{\"__oriel\":\"result\",\"id\":0,\"ok\":false,\"error\":\"命令执行失败。\"}}");
        }
    }

    private void CompleteEval(JsonElement root)
    {
        // id 是**页面可伪造**的字段：非整数（1.5、1e30）会让 GetInt32() 抛 FormatException /
        // OverflowException，而 OnScriptMessage 只接 JsonException——异常会一路逃到 GTK 的
        // trampoline，那条 eval 就永远等不到回执（表现为 30s 超时，评审 P3）。
        // 读不出合法整数就直接丢弃：它本来也匹配不上任何挂起项（桥接脚本的 seq 是自增整数）。
        if (!root.TryGetProperty("id", out var idElement) || !idElement.TryGetInt32(out int id))
        {
            return;
        }

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

    /// <summary>
    /// 把所有未完成的 ExecuteScript 回环标记失败并清空。
    /// </summary>
    /// <remarks>
    /// 回环依赖**原文档**里的 <c>_evalScriptDone</c>：页面导航离开原文档、或窗口销毁后，
    /// 回执可能永远不会再来——不清理的话 <c>ExecuteScriptAsync</c> 的 Task 永久挂起，
    /// 调用方的 await 无声无息地等死。入口见 <see cref="LinuxWindowHost.OnWindowDestroyed"/>
    /// 与 <c>OnLoadStarted</c>。
    /// </remarks>
    internal void FailPendingEvals(string reason)
    {
        // Keys 是快照；TryRemove 保证"清空"与"迟到回执"并发时只有一方拿到 completion。
        foreach (int id in _pendingEvals.Keys)
        {
            if (_pendingEvals.TryRemove(id, out var completion))
            {
                completion.TrySetException(new InvalidOperationException(reason));
            }
        }
    }

    /// <summary>读字符串属性；缺失或类型不符时返回空串（页面数据不可信，一律按可缺席处理）。</summary>
    private static string ReadString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    // ---- IIpcReplySink（IPC 回执）：GTK 主线程约束 ----

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
