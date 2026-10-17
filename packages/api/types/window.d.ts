/**
 * 取当前窗口的句柄（对标 Tauri 的 `getCurrentWindow`）。
 *
 * @returns {Window}
 * @remarks
 * **同步**返回（与 Tauri 一致）。它只读 `oriel.label`，不需要问宿主。
 */
export function getCurrentWindow(): Window;
/**
 * 列出所有存活窗口（对标 Tauri 的 `getAllWindows`）。
 *
 * @returns {Promise<Window[]>}
 * @remarks 需要 `window.getAll` 被允许——枚举窗口是跨窗口信息。
 */
export function getAllWindows(): Promise<Window[]>;
/**
 * 按 label 取窗口（对标 Tauri 的 `Window.getByLabel`）。
 *
 * @param {string} label
 * @returns {Promise<Window | null>} 不存在时 `null`（与 Tauri 一致，不抛）。
 */
export function getByLabel(label: string): Promise<Window | null>;
/**
 * 有焦点的窗口。
 *
 * @returns {Promise<Window | null>}
 * @remarks
 * **目前恒返回 `null`**：三平台都还没有"哪个窗口有焦点"的**查询**能力
 * （宿主的 `FocusChanged` 只上报变化，不记录状态）。保留签名是为了让迁移代码
 * 能原样编译，但要明确它现在给不出答案——而不是假装"没有窗口有焦点"。
 */
export function getFocusedWindow(): Promise<Window | null>;
/**
 * 一个窗口的句柄（对标 Tauri 2 的 `Window`）。
 *
 * @remarks
 * 它是**轻量句柄**而不是状态容器：每个方法都现算 label、现发命令。
 * 原因是窗口可能被别的路径关掉——缓存状态会与真实情况脱节，
 * 而脱节的表现是"对一个已关闭的窗口调用方法却没有报错"。
 */
export class Window {
    /**
     * @param {string} label
     */
    constructor(label: string);
    /** @type {string} */
    label: string;
    /** 显示窗口。需要 `window.show` 被允许。 */
    show(): Promise<any>;
    /** 隐藏窗口。需要 `window.hide` 被允许。 */
    hide(): Promise<any>;
    /** 关闭窗口（可被 `Closing` 与页面接管拦下）。**无条件放行**。 */
    close(): Promise<any>;
    /** 强制销毁窗口（不被任何东西拦下）。**无条件放行**。 */
    destroy(): Promise<any>;
    /** 聚焦窗口。需要 `window.focus` 被允许。 */
    setFocus(): Promise<any>;
    /** 最小化。**无条件放行**。 */
    minimize(): Promise<any>;
    /** 最大化。需要 `window.maximize` 被允许。 */
    maximize(): Promise<any>;
    /** 还原（对标 Tauri 的 `unmaximize`）。需要 `window.restore` 被允许。 */
    unmaximize(): Promise<any>;
    /** 最大化/还原切换，返回切换后是否最大化。**无条件放行**。 */
    toggleMaximize(): Promise<any>;
    /** 当前是否最大化。**无条件放行**（只读查询）。 */
    isMaximized(): Promise<any>;
    /** 当前是否显示中（最小化仍算显示中）。**无条件放行**（只读查询）。 */
    isVisible(): Promise<any>;
    /** 窗口所在显示器的缩放比（1.0 = 100%）。需要 `window.scaleFactor` 被允许。 */
    scaleFactor(): Promise<any>;
    /**
     * 当前标题（对标 Tauri 的 `title()`）。**无条件放行**（只读查询）。
     *
     * @returns {Promise<string>}
     * @remarks
     * 与标题变化事件无关：那个只在**页面文档标题**变化时触发（标题栏跟随页面），
     * 程序调 `setTitle()` 不会触发它。要"标题现在是什么"调这个方法，别等事件。
     */
    title(): Promise<string>;
    /**
     * 设置标题。需要 `window.setTitle` 被允许。
     * @param {string} title
     */
    setTitle(title: string): Promise<any>;
    /**
     * 设置是否可调整大小。需要 `window.setResizable` 被允许。
     * @param {boolean} resizable
     */
    setResizable(resizable: boolean): Promise<any>;
    /**
     * 设置全屏。需要 `window.setFullscreen` 被允许。
     * @param {boolean} fullscreen
     */
    setFullscreen(fullscreen: boolean): Promise<any>;
    /** 全屏切换，返回切换后是否全屏。**无条件放行**。 */
    toggleFullscreen(): Promise<any>;
    /**
     * 设置置顶（对标 Tauri 的 `setAlwaysOnTop`）。需要 `window.setOnTop` 被允许。
     * @param {boolean} alwaysOnTop
     */
    setAlwaysOnTop(alwaysOnTop: boolean): Promise<any>;
    /** 置顶切换，返回切换后是否置顶。**无条件放行**。 */
    toggleAlwaysOnTop(): Promise<any>;
    /**
     * 设置**客户区**尺寸（对标 Tauri 的 `setSize`）。
     *
     * @param {{ type?: string, width: number, height: number } | [number, number]} size
     *   `LogicalSize` / `PhysicalSize` / 裸对象 / 数字对。物理尺寸会用当前缩放比换算。
     * @returns {Promise<void>}
     * @remarks
     * 语义是**逻辑像素的客户区**（与 `WebviewWindow.Resize` 一致，见 API.md 的尺寸说明）。
     * Tauri 的 `setSize` 也是客户区，所以迁移过来是对的。
     */
    setSize(size: {
        type?: string;
        width: number;
        height: number;
    } | [number, number]): Promise<void>;
    /**
     * 设置窗口左上角位置（对标 Tauri 的 `setPosition`）。需要 `window.moveTo` 被允许。
     *
     * @param {{ type?: string, x: number, y: number } | [number, number]} position
     */
    setPosition(position: {
        type?: string;
        x: number;
        y: number;
    } | [number, number]): Promise<void>;
    /**
     * 设置**客户区**最小尺寸（对标 Tauri 的 `setMinSize`）。
     *
     * @param {{ type?: string, width: number, height: number } | [number, number] | null} size
     *   `null` 表示取消限制（本库用 0 表示"不限制"，与 `OrielWindowOptions.MinWidth` 一致）。
     */
    setMinSize(size: {
        type?: string;
        width: number;
        height: number;
    } | [number, number] | null): Promise<void>;
    /** 居中。需要 `window.center` 被允许。 */
    center(): Promise<any>;
    /** 客户区尺寸（逻辑像素）。需要 `window.innerSize` 被允许。 */
    innerSize(): Promise<LogicalSize>;
    /**
     * 整窗尺寸（逻辑像素）。
     *
     * @remarks
     * Linux/Wayland 下可能与 `innerSize()` 相等——合成器不告诉客户端装饰尺寸。
     * 需要 `window.outerSize` 被允许。
     */
    outerSize(): Promise<LogicalSize>;
    /** 窗口左上角（屏幕逻辑坐标）。需要 `window.outerPosition` 被允许。 */
    outerPosition(): Promise<LogicalPosition>;
    /**
     * 客户区左上角（屏幕逻辑坐标）。
     *
     * @remarks
     * Linux 上目前恒等于 `outerPosition()`（GTK 不暴露装饰偏移）。
     * 需要 `window.innerPosition` 被允许。
     */
    innerPosition(): Promise<LogicalPosition>;
    /**
     * 开始拖动窗口（对标 Tauri 的 `startDragging`）。
     *
     * @remarks
     * 库自己接管的标题栏拖动**不走这里**——它在注入脚本里内联完成（因为要在 `mousedown`
     * 之内同步判断双击）。这个方法是给"页面自己实现拖动"用的。**无条件放行**。
     */
    startDragging(): Promise<any>;
    /**
     * 订阅本窗口的关闭请求（对标 Tauri 的 `onCloseRequested`）。
     *
     * @param {(event: { event: string, id: number, payload: any, preventDefault: () => void }) => void} handler
     * @returns {Promise<() => void>} 退订函数（`await` 拿到，与 Tauri 一致）。
     * @remarks
     * **与 Tauri 的关键差别**：Tauri 在回调里调 `event.preventDefault()` 就能阻止关闭；
     * 本库是**反向**的——宿主挂起关闭、等页面决定，页面**同意**才调 `destroy()`：
     *
     * ```js
     * const un = await getCurrentWindow().onCloseRequested(async () => {
     *   if (await confirmDiscard()) {
     *     await getCurrentWindow().destroy();   // 同意关闭
     *   }
     *   // 不同意：什么都不做，窗口留在打开状态
     * });
     * ```
     *
     * 为什么不做成 Tauri 那样：Windows 的 `WM_CLOSE` 是**同步消息**、页面回执是异步 IPC，
     * 宿主没法"等你回调跑完再决定"。所以只能让页面主动补上关闭那一步。
     *
     * 传进来的 `event.preventDefault` 是个**空操作**——它存在只是为了让
     * `event.preventDefault()` 这种写法不报错。真正的"阻止"是"什么都不做"。
     * 这一点必须写清楚，否则迁移代码会以为调了它就等于阻止了。
     *
     * 前置条件：宿主必须开了关闭接管（`OrielWindowOptions.InterceptClose`
     * 或 `window.setCloseIntercept`），否则点关闭会直接关掉、回调永远不来。
     * 本方法会**自动**帮你打开它（需要 `window.setCloseIntercept` 被允许）。
     */
    onCloseRequested(handler: (event: {
        event: string;
        id: number;
        payload: any;
        preventDefault: () => void;
    }) => void): Promise<() => void>;
    /**
     * 订阅本窗口的尺寸变化（对标 Tauri 的 `onResized`）。
     * @param {(event: { event: string, id: number, payload: { width: number, height: number } }) => void} handler
     * @returns {Promise<() => void>}
     */
    onResized(handler: (event: {
        event: string;
        id: number;
        payload: {
            width: number;
            height: number;
        };
    }) => void): Promise<() => void>;
    /**
     * 订阅本窗口的位置变化（对标 Tauri 的 `onMoved`）。
     * @param {(event: { event: string, id: number, payload: { x: number, y: number } }) => void} handler
     * @returns {Promise<() => void>}
     */
    onMoved(handler: (event: {
        event: string;
        id: number;
        payload: {
            x: number;
            y: number;
        };
    }) => void): Promise<() => void>;
    /**
     * 订阅本窗口的焦点变化（对标 Tauri 的 `onFocusChanged`）。
     * @param {(event: { event: string, id: number, payload: null }) => void} handler
     * @returns {Promise<() => void>}
     */
    onFocusChanged(handler: (event: {
        event: string;
        id: number;
        payload: null;
    }) => void): Promise<() => void>;
}
export { LogicalSize, LogicalPosition, PhysicalSize, PhysicalPosition } from "./dpi.js";
