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
     * 是否最小化。
     *
     * @returns {Promise<boolean>}
     * @remarks **与 `isVisible()` 是两件事**：最小化的窗口仍然"可见"，
     * 它只是被收进了任务栏。混为一谈的后果是"最小化之后页面以为窗口被藏了"。
     */
    isMinimized(): Promise<boolean>;
    /**
     * 是否拥有键盘焦点。
     *
     * @returns {Promise<boolean>}
     * @remarks 按**窗口**判而不是按应用判：多窗口应用里只有一个是 key window。
     * 标题栏常用它渲染激活/非激活两套配色。
     */
    isFocused(): Promise<boolean>;
    /**
     * 是否全屏。
     *
     * @returns {Promise<boolean>}
     * @remarks 取的是宿主 `toggleFullscreen()` 记住的状态，不是比对窗口与显示器矩形——
     * 后者在用户手动把窗口拖成刚好铺满屏幕时会给出与库不一致的答案。
     */
    isFullscreen(): Promise<boolean>;
    /**
     * 是否置顶。
     *
     * @returns {Promise<boolean>}
     * @remarks 同上：取宿主记住的状态。
     */
    isAlwaysOnTop(): Promise<boolean>;
    /**
     * 是否有系统装饰（标题栏与边框）。
     *
     * @returns {Promise<boolean>}
     * @remarks 取创建时的选项，窗口生命期内不变。无边框窗口多用它决定"要不要渲染自绘标题栏"。
     */
    isDecorated(): Promise<boolean>;
    /**
     * 是否可缩放。
     *
     * @returns {Promise<boolean>}
     * @remarks 从**原生实际状态**读回（`setResizable` 改的就是它），
     * 所以反映的是"系统接受的是什么"。
     */
    isResizable(): Promise<boolean>;
    /**
     * 是否可最大化。
     *
     * @returns {Promise<boolean>}
     * @remarks **Linux 与 macOS 上等于 `isResizable()`**：那两个平台没有独立的
     * "可最大化"概念（GTK 跟着 resizable 走、AppKit 的 zoom 以 resizable 为前提）。
     * Windows 上才是独立的样式位。
     */
    isMaximizable(): Promise<boolean>;
    /**
     * 是否可最小化。
     *
     * @returns {Promise<boolean>}
     * @remarks 同 `isMaximizable()`：Linux 上回落到 `isResizable()`。
     */
    isMinimizable(): Promise<boolean>;
    /**
     * 是否可关闭。
     *
     * @returns {Promise<boolean>}
     * @remarks **Linux 上恒为 `true`**：GTK 没有这个概念（窗口总能收到 delete-event，
     * 本库的关闭接管是在那之上做的拦截）。
     */
    isClosable(): Promise<boolean>;
    /**
     * 当前主题（`'light'` 或 `'dark'`）。
     *
     * @returns {Promise<'light' | 'dark'>}
     * @remarks
     * 对标 Tauri 的 `Window.theme()`，但有两点不同：
     * 那边可能返回 `null`（"无法确定"），本库**总是给得出值**——三平台都能读到系统主题，
     * 而本库目前只有"跟随系统"这一种来源，没有"按窗口覆写主题"的能力。
     */
    theme(): Promise<"light" | "dark">;
    /**
     * 是否接收输入。
     *
     * @returns {Promise<boolean>}
     * @remarks **macOS 上只挡鼠标、不挡键盘**：Cocoa 没有"禁用窗口"这个概念，
     * 那边用 `ignoresMouseEvents` 近似。需要挡键盘的应用要自己处理。
     */
    isEnabled(): Promise<boolean>;
    /**
     * 启用/禁用窗口的输入。
     *
     * @param {boolean} enabled
     * @returns {Promise<void>}
     * @remarks 需要 `Allow("window.setEnabled")`——它**不在**标题栏白名单里
     * （禁用窗口不是标题栏要做的事）。
     */
    setEnabled(enabled: boolean): Promise<void>;
    /**
     * 订阅一个事件。与模块级 `listen` 等价。
     *
     * @param {string} event 事件名（Tauri 名或宿主名都行，见 `event` 模块的映射表）。
     * @param {(event: { event: string, id: number, payload: any }) => void} handler
     * @returns {() => void} 退订函数（**同步**返回，比 Tauri 的 Promise 更方便：
     * `await` 一个非 Promise 值同样可用，所以迁移代码不用改）。
     */
    listen(event: string, handler: (event: {
        event: string;
        id: number;
        payload: any;
    }) => void): () => void;
    /**
     * 订阅一次；收到第一条后自动退订。
     *
     * @param {string} event
     * @param {(event: { event: string, id: number, payload: any }) => void} handler
     * @returns {() => void} 退订函数（在触发前调用可以取消；**同步**返回，同上）。
     */
    once(event: string, handler: (event: {
        event: string;
        id: number;
        payload: any;
    }) => void): () => void;
    /**
     * 广播一个事件给**所有**窗口。
     *
     * @param {string} event
     * @param {unknown} [payload]
     * @returns {Promise<void>}
     * @remarks 需要宿主 `Allow("event.emit")`——那是唯一一条**不在**任何白名单里的内建命令
     * （它让一个窗口的页面能驱动别的窗口，而自绘标题栏不依赖它）。见 API.md §4。
     */
    emit(event: string, payload?: unknown): Promise<void>;
    /**
     * 把事件发给指定的那个窗口。
     *
     * @param {string} target 目标窗口的 label。
     * @param {string} event
     * @param {unknown} [payload]
     * @returns {Promise<void>}
     * @remarks 同样需要 `Allow("event.emit")`。
     */
    emitTo(target: string, event: string, payload?: unknown): Promise<void>;
    /**
     * 把一个最小化的窗口恢复到正常状态（对标 Tauri 的 `Window.unminimize()`）。
     *
     * @returns {Promise<void>}
     * @remarks
     * 走宿主的 `window.restore`：三平台的"还原"本来就是**同一个原生动作**
     * （Windows `SW_RESTORE`、GTK `gtk_window_deiconify` + `unmaximize`、
     * Cocoa `deminiaturize:`）——Tauri 把 `restore` 与 `unminimize` 分成两条命令，
     * 而 Win32 的 `SW_RESTORE` 一个调用就同时解除最小化与最大化。
     * 这里如实映射到同一个动作，而不是假装它们是两件事。
     */
    unminimize(): Promise<void>;
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
