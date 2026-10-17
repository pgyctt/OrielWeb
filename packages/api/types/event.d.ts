/**
 * 订阅一个事件。
 *
 * @param {string} eventName 事件名（如 `resize`、`tauri://resize`、自定义名）。
 * @param {(event: { event: string, id: number, payload: any }) => void} handler
 * @param {{ target?: string | { label: string } }} [options]
 *   `target` 是 Tauri 的过滤参数（只在那个窗口里生效）。本库**目前忽略它**——
 *   事件本来就只发给本窗口，跨窗口定向要用 `emitTo`。传了不会报错，但也不改变行为。
 * @returns {() => void} 退订函数（同步返回，见文件头说明）。
 */
export function listen(eventName: string, handler: (event: {
    event: string;
    id: number;
    payload: any;
}) => void, options?: {
    target?: string | {
        label: string;
    };
}): () => void;
/**
 * 订阅一个事件，收到**一次**后自动退订（对标 Tauri 的 `once`）。
 *
 * @param {string} eventName
 * @param {(event: { event: string, id: number, payload: any }) => void} handler
 * @param {{ target?: string | { label: string } }} [options]
 * @returns {() => void} 退订函数；在事件到达前调用它可以取消订阅。
 */
export function once(eventName: string, handler: (event: {
    event: string;
    id: number;
    payload: any;
}) => void, options?: {
    target?: string | {
        label: string;
    };
}): () => void;
/**
 * 向**所有**窗口广播一个事件（对标 Tauri 的 `emit`）。
 *
 * @param {string} eventName
 * @param {unknown} [payload]
 * @returns {Promise<void>}
 * @remarks
 * 需要宿主侧内建命令 `event.emit` 在能力名单里（`Allow("event.emit")` 或 `Allow("event.*")`）。
 * 它**不是** `win.*` 那种始终放行的命令——广播是真正的能力（能让别的窗口执行动作）。
 */
export function emit(eventName: string, payload?: unknown): Promise<void>;
/**
 * 向**指定窗口**发送一个事件（对标 Tauri 的 `emitTo`）。
 *
 * @param {string | { label: string }} target 目标窗口的 label，或带 `label` 字段的对象。
 * @param {string} eventName
 * @param {unknown} [payload]
 * @returns {Promise<void>}
 * @remarks
 * 依赖 P0-2 的窗口 label。目标窗口不存在时宿主**静默丢弃**（与 Tauri 一致）——
 * 多窗口应用里窗口随时可能被关掉，"发的时候还在、到的时候没了"是正常时序，
 * 让它抛异常只会逼调用方到处包 try。
 */
export function emitTo(target: string | {
    label: string;
}, eventName: string, payload?: unknown): Promise<void>;
/**
 * 清掉全部包内订阅（仅供测试用；真实页面不该调它）。
 *
 * @returns {void}
 */
export function __clearEventSubscriptions(): void;
/**
 * Tauri 2 的内置事件名（`TauriEvent`）。
 *
 * @remarks
 * **本库目前只实现了其中一部分**，如实标注：
 *
 * | 常量 | 状态 |
 * |---|---|
 * | `RESIZED` / `MOVED` | ✅ 宿主侧 `Resized` / `Moved` 事件已就位（P0-3 C2），但**尚未推到页面通道** |
 * | `FOCUS_CHANGED` | 同上（`FocusChanged` 已就位，未推到页面） |
 * | `CLOSE_REQUESTED` / `DESTROYED` | ⬜ 未实现（C5） |
 * | `DRAG_ENTER` / `DRAG_OVER` / `DRAG_DROP` / `DRAG_LEAVE` | ⛔ 不做（文件拖放走宿主 `FileDropped`，不在页面事件通道上） |
 * | `SCALE_CHANGED` / `THEME_CHANGED` / `WINDOW_CREATED` | ⬜ 未实现 |
 *
 * 常量先定义出来是为了让迁移代码能原样编译（`TauriEvent.RESIZED` 不会变成 undefined），
 * 但**订阅它们现在收不到事件**——这一点必须写清楚，否则又是一个"配了没反应"。
 */
export const TauriEvent: Readonly<{
    WINDOW_RESIZED: "tauri://resize";
    WINDOW_MOVED: "tauri://move";
    WINDOW_CLOSE_REQUESTED: "tauri://close-requested";
    WINDOW_DESTROYED: "tauri://destroyed";
    WINDOW_FOCUS: "tauri://focus";
    WINDOW_BLUR: "tauri://blur";
    WINDOW_SCALE_FACTOR_CHANGED: "tauri://scale-change";
    WINDOW_THEME_CHANGED: "tauri://theme-changed";
    WINDOW_CREATED: "tauri://window-created";
    DRAG_ENTER: "tauri://drag-enter";
    DRAG_OVER: "tauri://drag-over";
    DRAG_DROP: "tauri://drag-drop";
    DRAG_LEAVE: "tauri://drag-leave";
}>;
export type Subscription = {
    handler: (event: any) => void;
    raw: (payload: unknown) => void;
    unlisten: () => void;
};
