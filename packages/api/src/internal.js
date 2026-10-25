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

// ---------------------------------------------------------------------------
// 补壳警告（B14）：`rid` / `setTempDirPath` / `setAutoResize` 这类"为迁移零删改而存在、
// 但对本库没有任何作用"的成员，**必须可观测**——否则"调用不报错但什么都没发生"
// 是最难查的一类问题。默认静默（零删改），`UseDebug()` 或 __setDebugWarnings(true) 下打警告。
// ---------------------------------------------------------------------------

let debugWarningsEnabled =
    typeof globalThis !== 'undefined'
    && (/** @type {Record<string, unknown>} */ (/** @type {unknown} */ (globalThis))).__ORIEL_DEBUG__ === true;

/** 测试钩子：开关补壳警告（双下划线命名法与 mocks 一致，不是公开 API）。 */
/** @param {boolean} enabled */
export function __setDebugWarnings(enabled) {
    debugWarningsEnabled = enabled === true;
}

/** 补壳/平台差异的可观测信号：debug 模式下 console.warn，其余静默（保证零删改）。 */
/** @param {string} message */
export function warnCompat(message) {
    if (debugWarningsEnabled) {
        console.warn(`[orielweb] ${message}`);
    }
}

// ---------------------------------------------------------------------------
// 关闭接管的订阅计数（按窗口 label）
//
// 退订时**不能无脑关掉宿主接管**：两个组件各自 `onCloseRequested` 时，先卸载的那个
// 会把接管关掉，另一个的 `preventDefault()` 随之失效——症状是"我明明拦了，窗口还是关了"，
// 而且只在"组件顺序变了"的时候出现。只有归零才关。
//
// 放在 internal 而不是 window.js：`clearMocks()` 也要能清它，而 mocks 不该 import window
// （那会绕出一圈依赖）。这里两边都够得着。
// ---------------------------------------------------------------------------

/** @type {Map<string, number>} */
const closeInterceptRefs = new Map();

/**
 * 记一次订阅。
 *
 * @param {string} label
 */
export function refCloseIntercept(label) {
    closeInterceptRefs.set(label, (closeInterceptRefs.get(label) ?? 0) + 1);
}

/**
 * 退一次订阅。
 *
 * @param {string} label
 * @returns {boolean} 是否**归零**（true = 调用方该去关掉宿主接管了）。
 */
export function unrefCloseIntercept(label) {
    const next = (closeInterceptRefs.get(label) ?? 0) - 1;

    if (next > 0) {
        closeInterceptRefs.set(label, next);
        return false;
    }

    closeInterceptRefs.delete(label);
    return true;
}

/** 测试钩子：清空计数（`clearMocks` 会调它，避免用例之间互相污染）。 */
export function resetCloseInterceptRefs() {
    closeInterceptRefs.clear();
}
