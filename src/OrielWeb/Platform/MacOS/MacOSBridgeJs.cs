namespace OrielWeb.Platform.MacOS;

/// <summary>
/// macOS 桥接脚本（document 创建时经 WKUserScript 注入）。
/// 脚本内容来自三平台共用模板；本类只提供平台差异：
/// platform 字段，以及经 <c>webkit.messageHandlers.oriel</c> 以 JSON 字符串投递。
/// 回执由宿主求值 <c>_onResult</c>；ExecuteScriptAsync 结果经 <c>_evalScriptDone</c> 回环。
/// </summary>
internal static class MacOSBridgeJs
{
    /// <summary>按窗口选项生成注入脚本（<paramref name="forwardConsole"/> 见 <see cref="OrielWindowOptions.ConsoleForwarding"/>）。</summary>
    public static string Build(
        bool forwardConsole,
        string token,
        IReadOnlyList<string> trustedPrefixes,
        OrielSystemSnapshot system)
        => OrielBridgeTemplate.Create(
            "'macos'",
            "window.webkit.messageHandlers.oriel.postMessage(JSON.stringify(obj))",
            forwardConsole,
            token,
            trustedPrefixes,
            system);
}
