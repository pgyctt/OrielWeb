// 对标 `@tauri-apps/api/window`。
//
// 与 Tauri 的差别（如实标注）：
//   * **命令名是我们自己的**（`window.setTitle` 等），而 Tauri 是 `plugin:window|set_title`。
//     调用方不该看到这个差别——类的方法名与 Tauri 一一对应，映射在包内完成。
//   * `getCurrentWindow()` 是**同步**的（Tauri 也是）；`getAllWindows()` / `getByLabel()` 是
//     **异步**的（要问宿主有哪些窗口）。
//   * **本模块的方法都需要过能力门禁**：只有标题栏必需的那组（minimize / toggleMaximize /
//     close / destroy / drag* / toggleFullscreen / toggleOnTop / pickFile / contextMenu）
//     是无条件放行的，其余要应用显式 `Allow("window.*")` 或逐条声明。
//     Tauri 那边由 capability 文件管同一件事，这里的粒度更粗但方向一致。
//   * `title()` / `isDecorated()` 这类**只读查询**本库还没有宿主命令，暂时不提供，
//     而不是返回一个假值——返回假值的后果是页面按错误信息渲染。

import { invoke } from './core.js';
import { getTransport } from './internal.js';
import { listen, TauriEvent } from './event.js';
import {
    LogicalSize,
    LogicalPosition,
    normalizeSize,
    normalizePosition,
    physicalToLogical,
} from './dpi.js';

// **把 dpi 的四个类转出去**（对标 Tauri 的 window 模块）。
//
// Tauri 的 `@tauri-apps/api/window` 会 re-export 它们，所以页面里写
// `import { LogicalSize } from '@tauri-apps/api/window'` 是能跑的。
// 少了这一行，那种写法在迁移后会直接报"没有这个导出"——
// 它看起来像"包不完整"，实际只差一次转出。
//
// 这条是 `tauri-alignment` 测试机械比对出来的（见那个文件）：
// 我在 API.md 里写了"dpi 横跨 window 与 dpi 两处"，但没有真的把它转出来。
export { LogicalSize, LogicalPosition, PhysicalSize, PhysicalPosition } from './dpi.js';

/**
 * 把宿主返回的尺寸对象包成 {@link LogicalSize} 实例。
 *
 * @param {unknown} raw
 * @returns {LogicalSize}
 * @remarks
 * 宿主给的是 `{ type, width, height }` 的普通对象；包成类实例是为了让 `instanceof LogicalSize`
 * 成立、并能原样喂回 `setSize()`（那里按 `type` 分派）。两者形状本就一致，所以这里只做包装不回读校验——
 * 真拿到畸形值会在调用点立刻炸，比在这里静默补默认值好。
 */
function toLogicalSize(raw) {
    const value = /** @type {{ width?: number, height?: number }} */ (raw ?? {});
    return new LogicalSize(Number(value.width ?? 0), Number(value.height ?? 0));
}

/**
 * 把宿主返回的位置对象包成 {@link LogicalPosition} 实例；理由同 {@link toLogicalSize}。
 *
 * @param {unknown} raw
 * @returns {LogicalPosition}
 */
function toLogicalPosition(raw) {
    const value = /** @type {{ x?: number, y?: number }} */ (raw ?? {});
    return new LogicalPosition(Number(value.x ?? 0), Number(value.y ?? 0));
}

/** 本窗口的 label（宿主注入，见 OrielWindowOptions.Label）。 */
function currentLabel() {
    const label = getTransport().label;
    if (typeof label !== 'string' || label === '') {
        throw new Error(
            'getCurrentWindow: 当前传输层没有 label。它由宿主注入（0.4.0+）；' +
            '在 Node 里跑请用 mocks 的 mockWindows 或直接构造带 label 的传输层。');
    }

    return label;
}

/**
 * 取目标窗口的 label：不传就是本窗口。
 * @param {string | Window | undefined} target
 * @returns {string}
 */
function labelOf(target) {
    if (target === undefined || target === null) {
        return currentLabel();
    }

    if (typeof target === 'string') {
        return target;
    }

    if (typeof target.label === 'string' && target.label !== '') {
        return target.label;
    }

    throw new Error('需要一个窗口 label（字符串）或 Window 实例');
}

/**
 * 把一个 `unknown` 的抛出错值渲染成可读文案。
 *
 * @param {unknown} error
 * @returns {string}
 * @remarks
 * 不直接 `error.message`：抛出的可能是字符串、对象或任意值，而 TypeScript 也会
 * 正确拒绝在 `unknown` 上取属性。诊断文案里出现 `undefined` 是最没用的那一种。
 */
function errorText(error) {
    if (error instanceof Error) {
        return error.message;
    }

    if (typeof error === 'string') {
        return error;
    }

    try {
        return JSON.stringify(error) ?? String(error);
    } catch {
        return String(error);
    }
}

/**
 * 求缩放比（用于把 PhysicalSize 换算成逻辑尺寸）。
 *
 * @param {string} label
 * @returns {Promise<number>}
 */
async function scaleFactorOf(label) {
    const factor = await invoke('window.scaleFactor', { label });

    // 宿主给不出（0 / 非数字）时按 1 算：那相当于"不做换算"，
    // 比让整条 setSize 抛异常好——而缩放比异常本身应当由 E2 的自检去抓。
    return typeof factor === 'number' && factor > 0 ? factor : 1;
}

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
    constructor(label) {
        if (typeof label !== 'string' || label === '') {
            throw new Error('Window: label 必须是非空字符串');
        }

        /** @type {string} */
        this.label = label;
    }

    // ---- 显隐与状态 ----

    /** 显示窗口。需要 `window.show` 被允许。 */
    show() {
        return invoke('window.show', { label: this.label });
    }

    /** 隐藏窗口。需要 `window.hide` 被允许。 */
    hide() {
        return invoke('window.hide', { label: this.label });
    }

    /** 关闭窗口（可被 `Closing` 与页面接管拦下）。**无条件放行**。 */
    close() {
        return invoke('window.close', { label: this.label });
    }

    /** 强制销毁窗口（不被任何东西拦下）。**无条件放行**。 */
    destroy() {
        return invoke('window.destroy', { label: this.label });
    }

    /** 聚焦窗口。需要 `window.focus` 被允许。 */
    setFocus() {
        return invoke('window.focus', { label: this.label });
    }

    /** 最小化。**无条件放行**。 */
    minimize() {
        return invoke('window.minimize', { label: this.label });
    }

    /** 最大化。需要 `window.maximize` 被允许。 */
    maximize() {
        return invoke('window.maximize', { label: this.label });
    }

    /** 还原（对标 Tauri 的 `unmaximize`）。需要 `window.restore` 被允许。 */
    unmaximize() {
        return invoke('window.restore', { label: this.label });
    }

    /** 最大化/还原切换，返回切换后是否最大化。**无条件放行**。 */
    toggleMaximize() {
        return invoke('window.toggleMaximize', { label: this.label });
    }

    /** 当前是否最大化。**无条件放行**（只读查询）。 */
    isMaximized() {
        return invoke('window.isMaximized', { label: this.label });
    }

    /** 当前是否显示中（最小化仍算显示中）。**无条件放行**（只读查询）。 */
    isVisible() {
        return invoke('window.isVisible', { label: this.label });
    }

    /** 窗口所在显示器的缩放比（1.0 = 100%）。需要 `window.scaleFactor` 被允许。 */
    scaleFactor() {
        return invoke('window.scaleFactor', { label: this.label });
    }

    /**
     * 当前标题（对标 Tauri 的 `title()`）。**无条件放行**（只读查询）。
     *
     * @returns {Promise<string>}
     * @remarks
     * 与标题变化事件无关：那个只在**页面文档标题**变化时触发（标题栏跟随页面），
     * 程序调 `setTitle()` 不会触发它。要"标题现在是什么"调这个方法，别等事件。
     */
    title() {
        return invoke('window.title', { label: this.label });
    }

    // ---- 外观 ----

    /**
     * 设置标题。需要 `window.setTitle` 被允许。
     * @param {string} title
     */
    setTitle(title) {
        if (typeof title !== 'string') {
            throw new Error('setTitle: title 必须是字符串');
        }

        return invoke('window.setTitle', { label: this.label, title });
    }

    /**
     * 设置是否可调整大小。需要 `window.setResizable` 被允许。
     * @param {boolean} resizable
     */
    setResizable(resizable) {
        return invoke('window.setResizable', { label: this.label, value: resizable !== false });
    }

    /**
     * 设置全屏。需要 `window.setFullscreen` 被允许。
     * @param {boolean} fullscreen
     */
    setFullscreen(fullscreen) {
        return invoke('window.setFullscreen', { label: this.label, value: fullscreen !== false });
    }

    /** 全屏切换，返回切换后是否全屏。**无条件放行**。 */
    toggleFullscreen() {
        return invoke('window.toggleFullscreen', { label: this.label });
    }

    /**
     * 设置置顶（对标 Tauri 的 `setAlwaysOnTop`）。需要 `window.setOnTop` 被允许。
     * @param {boolean} alwaysOnTop
     */
    setAlwaysOnTop(alwaysOnTop) {
        return invoke('window.setOnTop', { label: this.label, value: alwaysOnTop !== false });
    }

    /** 置顶切换，返回切换后是否置顶。**无条件放行**。 */
    toggleAlwaysOnTop() {
        return invoke('window.toggleOnTop', { label: this.label });
    }

    // ---- 几何 ----

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
    async setSize(size) {
        const normalized = normalizeSize(size);
        let { width, height } = normalized;

        if (normalized.type === 'Physical') {
            const factor = await scaleFactorOf(this.label);
            width = physicalToLogical(width, factor);
            height = physicalToLogical(height, factor);
        }

        await invoke('window.resize', { label: this.label, width: Math.round(width), height: Math.round(height) });
    }

    /**
     * 设置窗口左上角位置（对标 Tauri 的 `setPosition`）。需要 `window.moveTo` 被允许。
     *
     * @param {{ type?: string, x: number, y: number } | [number, number]} position
     */
    async setPosition(position) {
        const normalized = normalizePosition(position);
        let { x, y } = normalized;

        if (normalized.type === 'Physical') {
            const factor = await scaleFactorOf(this.label);
            x = physicalToLogical(x, factor);
            y = physicalToLogical(y, factor);
        }

        await invoke('window.moveTo', { label: this.label, x: Math.round(x), y: Math.round(y) });
    }

    /**
     * 设置**客户区**最小尺寸（对标 Tauri 的 `setMinSize`）。
     *
     * @param {{ type?: string, width: number, height: number } | [number, number] | null} size
     *   `null` 表示取消限制（本库用 0 表示"不限制"，与 `OrielWindowOptions.MinWidth` 一致）。
     */
    async setMinSize(size) {
        if (size === null || size === undefined) {
            await invoke('window.setMinSize', { label: this.label, width: 0, height: 0 });
            return;
        }

        const normalized = normalizeSize(size);
        let { width, height } = normalized;

        if (normalized.type === 'Physical') {
            const factor = await scaleFactorOf(this.label);
            width = physicalToLogical(width, factor);
            height = physicalToLogical(height, factor);
        }

        await invoke('window.setMinSize', {
            label: this.label,
            width: Math.round(width),
            height: Math.round(height),
        });
    }

    /** 居中。需要 `window.center` 被允许。 */
    center() {
        return invoke('window.center', { label: this.label });
    }

    // ---- 几何查询 ----
    //
    // 四条都需要 `Allow`（它们不在标题栏白名单里）。
    //
    // **与 Tauri 的一处语义差别**：Tauri 的这四个方法返回 `PhysicalSize` / `PhysicalPosition`；
    // 本库返回 `LogicalSize` / `LogicalPosition`（`type: 'Logical'`）。
    // 理由是库对页面只暴露逻辑像素——混入物理像素会让"这个值是哪种单位"变成每个调用点
    // 都要想一遍的问题，而算错的症状是"在 Retina/150% 屏上偏一倍"。
    // 字段名（width/height/x/y）与 Tauri 一致，返回的对象又能直接喂回 setSize/setPosition，
    // 所以迁移代码通常感觉不到差别。

    /** 客户区尺寸（逻辑像素）。需要 `window.innerSize` 被允许。 */
    async innerSize() {
        return toLogicalSize(await invoke('window.innerSize', { label: this.label }));
    }

    /**
     * 整窗尺寸（逻辑像素）。
     *
     * @remarks
     * Linux/Wayland 下可能与 `innerSize()` 相等——合成器不告诉客户端装饰尺寸。
     * 需要 `window.outerSize` 被允许。
     */
    async outerSize() {
        return toLogicalSize(await invoke('window.outerSize', { label: this.label }));
    }

    /** 窗口左上角（屏幕逻辑坐标）。需要 `window.outerPosition` 被允许。 */
    async outerPosition() {
        return toLogicalPosition(await invoke('window.outerPosition', { label: this.label }));
    }

    /**
     * 客户区左上角（屏幕逻辑坐标）。
     *
     * @remarks
     * Linux 上目前恒等于 `outerPosition()`（GTK 不暴露装饰偏移）。
     * 需要 `window.innerPosition` 被允许。
     */
    async innerPosition() {
        return toLogicalPosition(await invoke('window.innerPosition', { label: this.label }));
    }

    // ---- 拖动 ----

    /**
     * 开始拖动窗口（对标 Tauri 的 `startDragging`）。
     *
     * @remarks
     * 库自己接管的标题栏拖动**不走这里**——它在注入脚本里内联完成（因为要在 `mousedown`
     * 之内同步判断双击）。这个方法是给"页面自己实现拖动"用的。**无条件放行**。
     */
    startDragging() {
        return invoke('window.drag', { label: this.label });
    }

    // ---- 关闭接管 ----

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
    async onCloseRequested(handler) {
        if (typeof handler !== 'function') {
            throw new Error('onCloseRequested: 需要一个函数');
        }

        // 自动打开接管：订阅了却没有接管 = 回调永远不来，而那种"配了没反应"
        // 是最难排查的。失败要报出来（与事件转发的处理一致）。
        try {
            await invoke('window.setCloseIntercept', { label: this.label, enabled: true });
        } catch (error) {
            console.error(
                '@orielweb/api/window: 无法打开关闭接管，onCloseRequested 的回调不会触发。' +
                `原因：${errorText(error)}。` +
                '请在宿主侧允许 `window.setCloseIntercept`，或用 OrielWindowOptions.InterceptClose 静态开启。');
        }

        const unlisten = listen(TauriEvent.WINDOW_CLOSE_REQUESTED, (event) => {
            handler({
                event: event.event,
                id: event.id,
                payload: event.payload,
                // 空操作：见上面的说明。保留它是为了让迁移代码的 `event.preventDefault()`
                // 不抛 TypeError——那会让一个本来只是"语义不同"的差异变成崩溃。
                preventDefault: () => {},
            });
        });

        return async () => {
            unlisten();

            // 退订最后一个监听者时把接管关掉：留着它会让窗口在没人处理的情况下
            // 点关闭没反应（挂起后没人 destroy）。这是"订阅驱动"该有的收尾。
            try {
                await invoke('window.setCloseIntercept', { label: this.label, enabled: false });
            } catch {
                // 关不掉不影响退订本身；宿主侧的默认值仍是应用配置的那个
            }
        };
    }

    /**
     * 订阅本窗口的尺寸变化（对标 Tauri 的 `onResized`）。
     * @param {(event: { event: string, id: number, payload: { width: number, height: number } }) => void} handler
     * @returns {Promise<() => void>}
     */
    onResized(handler) {
        return listenAsync(TauriEvent.WINDOW_RESIZED, handler);
    }

    /**
     * 订阅本窗口的位置变化（对标 Tauri 的 `onMoved`）。
     * @param {(event: { event: string, id: number, payload: { x: number, y: number } }) => void} handler
     * @returns {Promise<() => void>}
     */
    onMoved(handler) {
        return listenAsync(TauriEvent.WINDOW_MOVED, handler);
    }

    /**
     * 订阅本窗口的焦点变化（对标 Tauri 的 `onFocusChanged`）。
     * @param {(event: { event: string, id: number, payload: null }) => void} handler
     * @returns {Promise<() => void>}
     */
    onFocusChanged(handler) {
        // 焦点在宿主侧是**两个事件名**（focus / blur），这里合成一个回调：
        // 页面只关心"变了没有"，而 Tauri 的 onFocusChanged 也是这个粒度。
        const offFocus = listen(TauriEvent.WINDOW_FOCUS, (e) => handler(e));
        const offBlur = listen(TauriEvent.WINDOW_BLUR, (e) => handler(e));

        return Promise.resolve(() => {
            offFocus();
            offBlur();
        });
    }
}

/**
 * `listen` 的 Promise 包装：Tauri 的 `on*` 系列都返回 `Promise<UnlistenFn>`，
 * 而本库的 `listen` 是同步的。包一层是为了让迁移代码 `await` 得动。
 *
 * @param {string} eventName
 * @param {(event: any) => void} handler
 * @returns {Promise<() => void>}
 */
function listenAsync(eventName, handler) {
    return Promise.resolve(listen(eventName, handler));
}

/**
 * 取当前窗口的句柄（对标 Tauri 的 `getCurrentWindow`）。
 *
 * @returns {Window}
 * @remarks
 * **同步**返回（与 Tauri 一致）。它只读 `oriel.label`，不需要问宿主。
 */
export function getCurrentWindow() {
    return new Window(currentLabel());
}

/**
 * 列出所有存活窗口（对标 Tauri 的 `getAllWindows`）。
 *
 * @returns {Promise<Window[]>}
 * @remarks 需要 `window.getAll` 被允许——枚举窗口是跨窗口信息。
 */
export async function getAllWindows() {
    const labels = await invoke('window.getAll');
    return (Array.isArray(labels) ? labels : []).map((label) => new Window(String(label)));
}

/**
 * 按 label 取窗口（对标 Tauri 的 `Window.getByLabel`）。
 *
 * @param {string} label
 * @returns {Promise<Window | null>} 不存在时 `null`（与 Tauri 一致，不抛）。
 */
export async function getByLabel(label) {
    if (typeof label !== 'string' || label === '') {
        throw new Error('getByLabel: label 必须是非空字符串');
    }

    const labels = await invoke('window.getAll');
    const found = (Array.isArray(labels) ? labels : []).some((item) => String(item) === label);
    return found ? new Window(label) : null;
}

/**
 * 有焦点的窗口。
 *
 * @returns {Promise<Window | null>}
 * @remarks
 * **目前恒返回 `null`**：三平台都还没有"哪个窗口有焦点"的**查询**能力
 * （宿主的 `FocusChanged` 只上报变化，不记录状态）。保留签名是为了让迁移代码
 * 能原样编译，但要明确它现在给不出答案——而不是假装"没有窗口有焦点"。
 */
export async function getFocusedWindow() {
    return null;
}
