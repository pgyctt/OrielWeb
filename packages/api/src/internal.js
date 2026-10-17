// 内部传输层：`window.oriel` 的唯一访问点。
//
// 定位与 Tauri 的 `__TAURI_INTERNALS__` 相同——**它不是公开 API**。公开 API 是
// `@orielweb/api/*` 各模块；`window.oriel` 只承担"把消息送进宿主"这一件事。
// 页面直接用它也能跑，但会绕过 JS 包提供的形状对齐（事件包装、DPI 换算、错误码），
// 因此文档不推荐。
//
// 单独成文件而不是各模块各自读 `window.oriel`，有两个具体原因：
//   1. `mocks` 模块要能把传输层整个换掉（Node 里没有 window.oriel），只有一处可换才换得干净；
//   2. 报错信息只有一份。"不在 OrielWeb 里运行"这个诊断必须说清怎么办，
//      而不是让调用方看到 `Cannot read properties of undefined`。

/** @typedef {import('./types.js').OrielTransport} OrielTransport */

/**
 * 被 `mocks` 覆盖后的传输层；`null` 表示用真实的 `window.oriel`。
 * @type {OrielTransport | null}
 */
let transportOverride = null;

/**
 * 取当前的传输层。
 * @returns {OrielTransport}
 * @throws {Error} 不在 OrielWeb 宿主里、且没有装 mock 时抛出。
 */
export function getTransport() {
    if (transportOverride !== null) {
        return transportOverride;
    }

    const candidate = globalThis.window?.oriel;
    if (!candidate || typeof candidate.invoke !== 'function') {
        throw new Error(
            'window.oriel 不存在：这段代码不在 OrielWeb 宿主里运行，' +
            '或当前文档来源不可信（桥接脚本只安装到可信来源，见 docs/API.md §4）。' +
            '在 Node 里做单测请先调用 @orielweb/api/mocks 的 mockIPC。');
    }

    return candidate;
}

/**
 * 覆盖传输层（`mocks` 用）。
 * @param {OrielTransport | null} transport `null` 表示恢复真实传输层。
 * @returns {void}
 */
export function setTransportOverride(transport) {
    transportOverride = transport;
}

/**
 * 当前是否已装 mock。
 * @returns {boolean}
 */
export function hasTransportOverride() {
    return transportOverride !== null;
}
