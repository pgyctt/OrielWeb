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
    /**
     * webview 在窗口内的偏移——本库恒为 `(0, 0)`（webview 铺满客户区）。
     *
     * @returns {Promise<LogicalPosition>}
     */
    position(): Promise<LogicalPosition>;
    /**
     * webview 在窗口内的尺寸——本库等于客户区尺寸（{@link Window.innerSize}）。
     *
     * @returns {Promise<any>}
     */
    size(): Promise<any>;
    /**
     * 开关"webview 随窗口缩放"——**补壳**：本库的 webview 永远跟着窗口变，没有可关的开关。
     * 调用不报错、不生效；debug 模式下打警告。
     *
     * @param {boolean} [_autoResize]
     * @returns {Promise<void>}
     */
    setAutoResize(_autoResize?: boolean): Promise<void>;
    /**
     * 把 webview 移到另一个窗口——**补壳**：本库没有"webview 可从窗口脱离"的模型。
     * 调用不报错、不生效；debug 模式下打警告。
     *
     * @param {any} [_window]
     * @returns {Promise<void>}
     */
    reparent(_window?: any): Promise<void>;
    /**
     * 设置缩放倍率（对标 Tauri 的 `Webview.setZoom`）。
     *
     * @param {number} scale 缩放倍率，`1.0` = 100%。必须是有限正数。
     * @returns {Promise<void>}
     * @remarks
     * 三平台落点语义一致：Windows 是 WebView2 的 `ZoomFactor`、macOS 是
     * `WKWebView.pageZoom`、Linux 是 `webkit_web_view_set_zoom_level`。
     * 改的是**布局视口**（与浏览器 Ctrl+滚轮同一机制），不是 CSS transform——
     * `100vw`、媒体查询、`devicePixelRatio` 都会跟着变。
     *
     * Windows 的引擎内部只接受 0.02–10.0，越界值会被引擎钳制（并以
     * `ZoomFactorChanged` 回报实际值）；本库不做二次钳制。
     *
     * **需要能力声明**（`window.setZoom`）。
     */
    setZoom(scale: number): Promise<void>;
    /**
     * 清除全部浏览数据（对标 Tauri 的 `Webview.clearAllBrowsingData`）：
     * 缓存、Cookie、localStorage、IndexedDB、Service Worker……
     *
     * @returns {Promise<void>}
     * @remarks
     * 三平台清的都是**本应用自己的**数据存储（Windows 是 profile 目录、
     * macOS 是 `WKWebsiteDataStore.default`、Linux 是 `WebKitWebsiteDataManager`），
     * 不涉及其他应用。
     *
     * **返回时清除已发起，不是已清完**：三平台的原生 API 都是异步的，
     * 而本库的命令是同步契约——紧随其后的读取看到旧数据（理论上可能）
     * 属于这个已知窗口。清除会**销毁登录态与本地存储**，所以
     * **需要能力声明**（`window.clearAllBrowsingData`）——不该被一个被注入的页面随手调用。
     */
    clearAllBrowsingData(): Promise<void>;
}
import { Window } from './window.js';
import { LogicalPosition } from './dpi.js';
