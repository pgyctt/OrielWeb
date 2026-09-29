namespace OrielWeb;

/// <summary>
/// Windows 注入桥：<c>window.oriel.invoke(name, args)</c> → Promise。
/// 脚本内容来自三平台共用模板，本类只提供平台差异（platform 字段与投递通道）。
/// </summary>
internal static class OrielBridgeJs
{
    /// <summary>按窗口选项生成注入脚本（<paramref name="forwardConsole"/> 见 <see cref="OrielWindowOptions.ConsoleForwarding"/>）。</summary>
    public static string Build(bool forwardConsole) => OrielBridgeTemplate.Create(
        "'windows'",
        "window.chrome.webview.postMessage(obj)",
        forwardConsole);
}
