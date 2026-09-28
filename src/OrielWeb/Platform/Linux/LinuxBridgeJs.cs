namespace OrielWeb.Platform.Linux;

/// <summary>
/// Linux 桥接脚本（document 创建时经 WebKitUserScript 注入，WebKitGTK 同样提供
/// webkit.messageHandlers 通道）。页面 API 与 Windows/macOS 版一致。
/// 差异：请求经 webkit.messageHandlers.oriel 以 JSON 字符串 postMessage；
/// 回执由宿主 webkit_web_view_evaluate_javascript 调用 _onResult；
/// ExecuteScriptAsync 结果经 _evalScriptDone 回环。
/// </summary>
internal static class LinuxBridgeJs
{
    public const string Script = """
        (() => {
            if (window.__orielBridgeInstalled) return;
            window.__orielBridgeInstalled = true;

            const pending = new Map();
            let seq = 0;

            const post = (obj) => window.webkit.messageHandlers.oriel.postMessage(JSON.stringify(obj));

            // 先建 Promise、再建对象：Promise 构造器会同步执行 executor，
            // 若在对象字面量内引用 oriel，会落进暂时性死区（ReferenceError）
            // → ready 变成 rejected Promise 且 _resolveReady 永不赋值。
            let resolveReady;
            const ready = new Promise((resolve) => { resolveReady = resolve; });

            const oriel = {
                platform: 'linux',
                version: '0.1.0',
                ready: ready,
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
                // 宿主 ExecuteScriptAsync 的页面回环（见 DECISIONS.md：不使用 GAsyncReadyCallback）
                _evalScriptDone(id, json) {
                    post({ __oriel: 'evalResult', id: id, json: json });
                }
            };

            window.oriel = oriel;

            // 本脚本在 document 创建时注入（WEBKIT_USER_SCRIPT_INJECT_AT_DOCUMENT_START），
            // 此刻页面自身脚本尚未执行，立即派发 orielready 必然无人监听。
            // 改到 DOMContentLoaded——它晚于所有同步 / defer / type=module 脚本。
            const announceReady = () => document.dispatchEvent(new Event('orielready'));
            if (document.readyState === 'loading') {
                document.addEventListener('DOMContentLoaded', announceReady, { once: true });
            } else {
                queueMicrotask(announceReady);
            }

            resolveReady();
        })();
        """;
}
