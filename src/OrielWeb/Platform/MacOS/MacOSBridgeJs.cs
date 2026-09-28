namespace OrielWeb.Platform.MacOS;

/// <summary>
/// macOS 桥接脚本（document 创建时经 WKUserScript 注入）。
/// 页面 API 与 Windows 版一致：window.oriel.invoke(name, args) → Promise。
/// 差异：请求经 webkit.messageHandlers.oriel 以 JSON 字符串 postMessage；
/// 回执由宿主 evaluateJavaScript 调用 _onResult；ExecuteScriptAsync 结果经 _evalScriptDone 回环。
/// </summary>
internal static class MacOSBridgeJs
{
    public const string Script = """
        (() => {
            if (window.__orielBridgeInstalled) return;
            window.__orielBridgeInstalled = true;

            const pending = new Map();
            let seq = 0;

            const post = (obj) => window.webkit.messageHandlers.oriel.postMessage(JSON.stringify(obj));

            const oriel = {
                platform: 'macos',
                version: '0.1.0',
                ready: new Promise((resolve) => { oriel._resolveReady = resolve; }),
                invoke(name, args) {
                    return new Promise((resolve, reject) => {
                        if (typeof name !== 'string' || !name) {
                            reject(new Error('oriel.invoke: name 必须为非空字符串'));
                            return;
                        }
                        const id = ++seq;
                        pending.set(id, { resolve, reject });
                        post({ __oriel: 'invoke', id: id, name: name, args: args === undefined ? null : args });
                    });
                },
                _onResult(id, ok, payload) {
                    const entry = pending.get(id);
                    if (!entry) return;
                    pending.delete(id);
                    if (ok) entry.resolve(payload);
                    else entry.reject(new Error(payload || ('oriel 命令失败 (id=' + id + ')')));
                },
                _evalScriptDone(id, json) {
                    post({ __oriel: 'evalResult', id: id, json: json });
                }
            };

            window.oriel = oriel;
            document.dispatchEvent(new Event('orielready'));
            oriel._resolveReady();
        })();
        """;
}
