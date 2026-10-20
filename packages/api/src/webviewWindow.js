// 对标 `@tauri-apps/api/webviewWindow` 的**最小可用面**：页面能建窗、能列举。
//
// 与 Tauri 的差别（如实标注）：
//
//   * **创建是异步的，显式可见**。Tauri 的 `new WebviewWindow(label, options)` 同步返回实例，
//     然后靠 `once('tauri://created')` / `once('tauri://error')` 通知结果。本库没有
//     宿主→页面的"窗口创建"事件（`WEBVIEW_CREATED` 在桌面端不会触发），所以改成：
//     构造时立刻发起 `window.new`，结果通过 `created()` 暴露。
//     迁移代码里 `new WebviewWindow(...)` + `once('tauri://created')` 的写法**需要改**——
//     这是本模块唯一一处调用形状差异，写在这里而不是让它静默不触发。
//   * **没有"多 webview"**：本库窗口即 webview，`WebviewWindow` 只是 `Window` 的同义词。
//     `Webview` 类不单独提供（`getAllWebviews()` 这类也没有）。
//   * 只支持建窗选项的最小集：label / title / url / width / height / resizable / frameless。
//     其余选项依赖本库还没有的能力（特效、图标、父子窗口…），给了也不会生效，所以不收。

import { invoke } from './core.js';
import { Window, getAllWindows, getCurrentWindow } from './window.js';
import { LogicalPosition } from './dpi.js';
import { warnCompat } from './internal.js';

/** 内部标记：带它的构造表示"包装一个已存在的窗口"，不发 `window.new`。 */
const EXISTING = Symbol('oriel.webviewWindow.existing');

/**
 * 从同名选项里挑出 `window.new` 认识的字段。
 *
 * @param {Record<string, any> | undefined} options
 * @returns {Record<string, unknown>}
 * @remarks 只挑认识的键，**不原样透传**：宿主不认识的键会被静默忽略，
 * 而"我传了父窗口/特效却没生效"比"这个选项不支持"更难查。
 */
function toCreateArgs(options) {
    /** @type {Record<string, unknown>} */
    const args = {};
    if (!options || typeof options !== 'object') {
        return args;
    }

    for (const key of ['title', 'url', 'width', 'height', 'resizable', 'frameless']) {
        if (options[key] !== undefined) {
            args[key] = options[key];
        }
    }

    return args;
}

/**
 * 一个可创建的窗口（对标 Tauri 的 `WebviewWindow`）。
 *
 * @extends Window
 * @remarks
 * 本库窗口即 webview，所以它与 {@link Window} 的行为完全相同；区别只在**构造时**：
 * `new WebviewWindow(label, options)` 会让宿主建一个新窗口。
 *
 * 创建结果用 `created` 属性取（一个 Promise）。构造时挂了一个空 catch 防止"没人 await"
 * 变成未处理拒绝——真正要处理失败时 `await w.created`，它仍然会 reject。
 */
export class WebviewWindow extends Window {
    /**
     * @param {string} label 新窗口的 label（应用内唯一）。
     * @param {Record<string, any>} [options] 建窗选项（见文件头）。
     * @param {symbol} [internal] 内部标记：包装已存在的窗口时不建窗。
     */
    constructor(label, options, internal) {
        super(label);

        /** @type {Promise<void>} */
        this.created = internal === EXISTING
            ? Promise.resolve()
            : invoke('window.new', { label, ...toCreateArgs(options) }).then(() => undefined);

        // 没人 await 时不要让 Node/浏览器把它报成 unhandled rejection：
        // 错误仍然留在 this.created 上，`await w.created` 照样抛出。
        this.created.catch(() => {});
    }

    /**
     * 建一个新窗口并等它创建完成（推荐入口）。
     *
     * @param {string} label
     * @param {Record<string, any>} [options]
     * @returns {Promise<WebviewWindow>} 创建完成后的实例。
     * @remarks 这是本库的扩展（Tauri 没有静态 `create`）：把"构造 + 等结果"合成一步，
     * 避免忘记 `await created()` 就往下走。
     */
    static async create(label, options) {
        const window = new WebviewWindow(label, options);
        await window.created;
        return window;
    }

    /** 当前窗口（不创建新窗口）。 */
    static getCurrentWebviewWindow() {
        return getCurrentWebviewWindow();
    }

    /** 全部存活窗口（不创建新窗口）。 */
    static getAllWebviewWindows() {
        return getAllWebviewWindows();
    }

    // ---- webview 级方法（B14 补壳）----
    //
    // Tauri 的 WebviewWindow = Webview + Window，"webview 在窗口内的布局"是独立一层；
    // 本库**窗口即 webview**（一个窗口一个 webview，铺满客户区），没有"窗口内偏移"这个概念。
    // 这四个方法让迁移代码零删改：能给出正确答案的给真值（position/size），
    // 没有对应物的 no-op + debug 警告（setAutoResize/reparent）——补壳必须可观测。

    /**
     * webview 在窗口内的偏移——本库恒为 `(0, 0)`（webview 铺满客户区）。
     *
     * @returns {Promise<LogicalPosition>}
     */
    async position() {
        return new LogicalPosition(0, 0);
    }

    /**
     * webview 在窗口内的尺寸——本库等于客户区尺寸（{@link Window.innerSize}）。
     *
     * @returns {Promise<any>}
     */
    async size() {
        return this.innerSize();
    }

    /**
     * 开关"webview 随窗口缩放"——**补壳**：本库的 webview 永远跟着窗口变，没有可关的开关。
     * 调用不报错、不生效；debug 模式下打警告。
     *
     * @param {boolean} [_autoResize]
     * @returns {Promise<void>}
     */
    setAutoResize(_autoResize) {
        warnCompat('WebviewWindow.setAutoResize 是补壳：本库的 webview 永远随窗口缩放，该调用不生效。');
        return Promise.resolve();
    }

    /**
     * 把 webview 移到另一个窗口——**补壳**：本库没有"webview 可从窗口脱离"的模型。
     * 调用不报错、不生效；debug 模式下打警告。
     *
     * @param {any} [_window]
     * @returns {Promise<void>}
     */
    reparent(_window) {
        warnCompat('WebviewWindow.reparent 是补壳：本库的 webview 属于它的窗口，不能移动。');
        return Promise.resolve();
    }
}

/**
 * 当前窗口的 `WebviewWindow` 实例（对标 Tauri 的 `getCurrentWebviewWindow`）。
 *
 * @returns {WebviewWindow}
 * @remarks **不会**建新窗口：它包装的是本窗口。
 */
export function getCurrentWebviewWindow() {
    const current = getCurrentWindow();
    return new WebviewWindow(current.label, undefined, EXISTING);
}

/**
 * 全部存活窗口（对标 Tauri 的 `getAllWebviewWindows`）。
 *
 * @returns {Promise<WebviewWindow[]>}
 * @remarks 同样不建窗，只是把 `getAllWindows()` 的结果包成 `WebviewWindow`。
 */
export async function getAllWebviewWindows() {
    const windows = await getAllWindows();
    return windows.map((window) => new WebviewWindow(window.label, undefined, EXISTING));
}
