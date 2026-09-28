// OrielWeb 三平台共用的注入式桥接脚本模板（EmbeddedResource，见 OrielWeb.csproj）。
//
// 由 OrielBridgeTemplate 在创建窗口时替换两个占位符后注入：
//   __ORIEL_PLATFORM__ → 'windows' | 'macos' | 'linux'
//   __ORIEL_POST__     → 平台投递表达式（obj 为待发送对象）
//
// 三平台此前各自手抄一份，已导致缺陷同步传播（ready 的 TDZ、orielready 的时序
// 都曾三份全中）。任何修改都会同时作用于三平台——这正是合并的目的。
//
// 平台差异一览：
//   * 投递通道：Windows 为 chrome.webview.postMessage(对象)；
//     macOS/Linux 为 webkit.messageHandlers.oriel.postMessage(JSON 字符串)。
//   * 回执通道：Windows 经 chrome.webview 的 message 事件；
//     macOS/Linux 由宿主 evaluateJavaScript 调用 _onResult。
//   * ExecuteScript 回环：仅 macOS/Linux 使用 _evalScriptDone（宿主求值回传）；
//     Windows 走 COM 完成回调，该方法闲置但无害。

(() => {
    if (window.__orielBridgeInstalled) return;
    window.__orielBridgeInstalled = true;

    const pending = new Map();
    let seq = 0;

    const post = (obj) => __ORIEL_POST__;

    // 先建 Promise、再建对象：Promise 构造器会同步执行 executor，
    // 若在对象字面量内引用 oriel，会落进暂时性死区（ReferenceError）
    // → ready 变成 rejected Promise 且 _resolveReady 永不赋值。
    let resolveReady;
    const ready = new Promise((resolve) => { resolveReady = resolve; });

    const oriel = {
        platform: __ORIEL_PLATFORM__,
        version: '0.1.0',
        ready: ready,
        // 命令回执超时（毫秒），可在页面侧改写为其他值；<=0 表示不启用超时。
        // 没有它时，命令永不回执（如宿主侧异常导致 sink 未投递）会让 Promise
        // 永不 settle、pending 条目永驻，前端内存持续泄漏。
        timeout: 30000,
        invoke(name, args) {
            return new Promise((resolve, reject) => {
                if (typeof name !== 'string' || !name) {
                    reject(new Error('oriel.invoke: name 必须为非空字符串'));
                    return;
                }
                const id = ++seq;
                const timeoutMs = typeof oriel.timeout === 'number' && oriel.timeout > 0 ? oriel.timeout : 0;
                const timer = timeoutMs > 0
                    ? setTimeout(() => {
                        if (pending.delete(id)) {
                            reject(new Error('oriel.invoke 超时：命令 "' + name + '" 在 ' + timeoutMs + 'ms 内没有回执'));
                        }
                    }, timeoutMs)
                    : null;
                pending.set(id, { resolve: resolve, reject: reject, timer: timer });
                post({ __oriel: 'invoke', id: id, name: name, args: args === undefined ? null : args });
            });
        },
        _onResult(id, ok, payload) {
            const entry = pending.get(id);
            if (!entry) return;
            pending.delete(id);
            if (entry.timer !== null) clearTimeout(entry.timer);
            if (ok) entry.resolve(payload);
            else entry.reject(new Error(payload || ('oriel 命令失败 (id=' + id + ')')));
        },
        // 宿主 ExecuteScriptAsync 的页面回环（见 DECISIONS.md：不使用 ObjC block /
        // GAsyncReadyCallback，改由页面回传完成 TCS）
        _evalScriptDone(id, json) {
            post({ __oriel: 'evalResult', id: id, json: json });
        }
    };

    window.oriel = oriel;

    // 接收命令回执：{ __oriel:'result', id, ok, value | error }
    // 仅 Windows（chrome.webview）有 message 事件；macOS/Linux 由宿主调用 _onResult。
    if (window.chrome && window.chrome.webview && window.chrome.webview.addEventListener) {
        window.chrome.webview.addEventListener('message', (e) => {
            const d = e.data;
            if (d && d.__oriel === 'result') {
                oriel._onResult(d.id, d.ok === true, d.ok === true ? d.value : d.error);
            }
        });
    }

    // 本脚本在 document 创建时注入（早于页面自身脚本），立即派发 orielready
    // 必然无人监听。改到 DOMContentLoaded——它晚于所有同步 / defer / type=module 脚本。
    // 注意：DOMContentLoaded 之后才动态注册的监听器仍收不到，因此文档主推
    // `await window.oriel.ready`，事件仅作为兼容性补充。
    const announceReady = () => document.dispatchEvent(new Event('orielready'));
    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', announceReady, { once: true });
    } else {
        queueMicrotask(announceReady);
    }

    resolveReady();
})();
