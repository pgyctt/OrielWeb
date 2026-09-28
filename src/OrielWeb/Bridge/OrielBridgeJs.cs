namespace OrielWeb;

/// <summary>注入每个文档的 JS 桥：window.oriel.invoke(name, args) → Promise。</summary>
internal static class OrielBridgeJs
{
    public const string Script = """
        (() => {
            if (window.__orielBridgeInstalled) return;
            window.__orielBridgeInstalled = true;

            const pending = new Map();
            let seq = 0;

            // 先建 Promise、再建对象：Promise 构造器会同步执行 executor，
            // 若在对象字面量内引用 oriel，会落进暂时性死区（ReferenceError）
            // → ready 变成 rejected Promise 且 _resolveReady 永不赋值。
            let resolveReady;
            const ready = new Promise((resolve) => { resolveReady = resolve; });

            const oriel = {
                platform: 'windows',
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
                        window.chrome.webview.postMessage({ __oriel: 'invoke', id: id, name: name, args: args === undefined ? null : args });
                    });
                },
                _onResult(id, ok, payload) {
                    const entry = pending.get(id);
                    if (!entry) return;
                    pending.delete(id);
                    if (ok) entry.resolve(payload);
                    else entry.reject(new Error(payload || ('oriel 命令失败 (id=' + id + ')')));
                }
            };

            window.oriel = oriel;

            // 接收 C# 回执：{ __oriel:'result', id, ok, value | error }
            window.chrome.webview.addEventListener('message', (e) => {
                const d = e.data;
                if (d && d.__oriel === 'result') {
                    oriel._onResult(d.id, d.ok === true, d.ok === true ? d.value : d.error);
                }
            });

            // 本脚本在 document 创建时注入（AddScriptToExecuteOnDocumentCreated），
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
