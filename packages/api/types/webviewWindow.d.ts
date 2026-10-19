/**
 * 当前窗口的 `WebviewWindow` 实例（对标 Tauri 的 `getCurrentWebviewWindow`）。
 *
 * @returns {WebviewWindow}
 * @remarks **不会**建新窗口：它包装的是本窗口。
 */
export function getCurrentWebviewWindow(): WebviewWindow;
/**
 * 全部存活窗口（对标 Tauri 的 `getAllWebviewWindows`）。
 *
 * @returns {Promise<WebviewWindow[]>}
 * @remarks 同样不建窗，只是把 `getAllWindows()` 的结果包成 `WebviewWindow`。
 */
export function getAllWebviewWindows(): Promise<WebviewWindow[]>;
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
     * 建一个新窗口并等它创建完成（推荐入口）。
     *
     * @param {string} label
     * @param {Record<string, any>} [options]
     * @returns {Promise<WebviewWindow>} 创建完成后的实例。
     * @remarks 这是本库的扩展（Tauri 没有静态 `create`）：把"构造 + 等结果"合成一步，
     * 避免忘记 `await created()` 就往下走。
     */
    static create(label: string, options?: Record<string, any>): Promise<WebviewWindow>;
    /** 当前窗口（不创建新窗口）。 */
    static getCurrentWebviewWindow(): WebviewWindow;
    /** 全部存活窗口（不创建新窗口）。 */
    static getAllWebviewWindows(): Promise<WebviewWindow[]>;
    /**
     * @param {string} label 新窗口的 label（应用内唯一）。
     * @param {Record<string, any>} [options] 建窗选项（见文件头）。
     * @param {symbol} [internal] 内部标记：包装已存在的窗口时不建窗。
     */
    constructor(label: string, options?: Record<string, any>, internal?: symbol);
    /** @type {Promise<void>} */
    created: Promise<void>;
}
import { Window } from './window.js';
