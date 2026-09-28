using System.Text.Json;
using OrielWeb.Ipc;
using OrielWeb.Platform.Windows.Interop;

namespace OrielWeb.Platform.Windows;

/// <summary>
/// WebMessage 接收与 IPC 回执的托管状态对象。
/// 原生侧经由 Win32NativeCallbacks 的手工 CCW thunk 进入这里。
/// </summary>
internal sealed class WebMessageReceivedHandler : IIpcReplySink
{
    private readonly Win32WindowHost _host;

    public WebMessageReceivedHandler(Win32WindowHost host)
    {
        _host = host;
    }

    /// <summary>由手工 CCW thunk 调用（UI 线程）。返回 HRESULT。</summary>
    public int OnWebMessageReceived(WebView2WebMessageReceivedEventArgsPtr args)
    {
        var json = args.GetWebMessageAsJson();
        if (string.IsNullOrEmpty(json))
        {
            return 0;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("__oriel", out var kind)
                && kind.ValueKind == JsonValueKind.String
                && kind.GetString() == "invoke")
            {
                _ = DispatchAsync(root.Clone());
            }
        }
        catch (JsonException)
        {
            // 非 OrielWeb 消息（页面自定义 postMessage），忽略
        }
        return 0;
    }

    private async Task DispatchAsync(JsonElement message)
    {
        try
        {
            await _host.App.Dispatcher.HandleInvokeAsync(message, this).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 分发器内部已按命令捕获；此处兜底分发器之外的错误。
            // 用 JsonSerializer 生成字符串字面量：手写转义漏掉 \t \b \f 与全部
            // U+0000–U+001F 控制字符，异常消息含制表符时会产生非法 JSON，
            // PostWebMessageAsJson 直接失败，前端连错误都收不到。
            // Linux/macOS 版一直用的就是 JsonSerializer.Serialize，此处与之统一。
            PostJson($"{{\"__oriel\":\"result\",\"id\":0,\"ok\":false,\"error\":{JsonSerializer.Serialize(ex.Message)}}}");
        }
    }

    // IIpcReplySink：回执必须切回 UI 线程（WebView2 COM 绑定 STA）
    public void PostJson(string json)
    {
        if (_host.IsOnUiThread())
        {
            _host.PostWebMessageOnUi(json);
            return;
        }
        _host.Backend.PostToMainThread(() => _host.PostWebMessageOnUi(json));
    }
}
