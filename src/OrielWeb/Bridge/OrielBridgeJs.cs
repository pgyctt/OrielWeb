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

            const oriel = {
                platform: 'windows',
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

            document.dispatchEvent(new Event('orielready'));
            oriel._resolveReady();
        })();
        """;
}
