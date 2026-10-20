/**
 * 把 Tauri 事件名翻译成宿主事件名。
 *
 * @param {string} eventName
 * @returns {string}
 */
export function toHostEventName(eventName: string): string;
/**
 * 订阅一个事件。
 *
 * @param {string} eventName 事件名（如 `resize`、`tauri://resize`、自定义名）。
 * @param {(event: { event: string, id: number, payload: any }) => void} handler
 * @param {{ target?: string | { label: string } }} [options]
 *   `target` 是 Tauri 的过滤参数（只在那个窗口里生效）。本库**目前忽略它**——
 *   事件本来就只发给本窗口，跨窗口定向要用 `emitTo`。传了不会报错，但也不改变行为。
 * @returns {(() => void) & PromiseLike<() => void>} 退订函数；它同时是 thenable，
 *   所以 `await listen(...)` 也成立（见 {@link asThenable}）。
 */
export function listen(eventName: string, handler: (event: {
    event: string;
    id: number;
    payload: any;
}) => void, options?: {
    target?: string | {
        label: string;
    };
}): (() => void) & PromiseLike<() => void>;
/**
 * 订阅一个事件，收到**一次**后自动退订（对标 Tauri 的 `once`）。
 *
 * @param {string} eventName
 * @param {(event: { event: string, id: number, payload: any }) => void} handler
 * @param {{ target?: string | { label: string } }} [options]
 * @returns {(() => void) & PromiseLike<() => void>} 退订函数；在事件到达前调用它可以取消订阅。
 *   与 {@link listen} 一样是 thenable，`await once(...)` 也成立。
 */
export function once(eventName: string, handler: (event: {
    event: string;
    id: number;
    payload: any;
}) => void, options?: {
    target?: string | {
        label: string;
    };
}): (() => void) & PromiseLike<() => void>;
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
 * **本库的支持状态**（如实标注，别让"常量存在"被误读成"事件会来"）：
 *
 * | 常量 | 状态 |
 * |---|---|
 * | `WINDOW_RESIZED` / `WINDOW_MOVED` | ✅ 可用。宿主侧 `Resized` / `Moved` 会在页面订阅时自动打开转发 |
 * | `WINDOW_FOCUS` / `WINDOW_BLUR` | ✅ 可用（同上） |
 * | `WINDOW_CLOSE_REQUESTED` | ✅ 可用，但**前提是宿主开了关闭接管**（`OrielWindowOptions.InterceptClose`）——没开接管时宿主直接关闭，不会有通知 |
 * | `WINDOW_DESTROYED` | ⛔ **永远收不到**：窗口销毁时页面正在被拆掉，事件送不出去（Tauri 的 JS 侧同样如此，那个事件是给 Rust 用的）。要"即将关闭"的通知请用 `WINDOW_CLOSE_REQUESTED` |
 * | `WINDOW_SCALE_FACTOR_CHANGED` / `WINDOW_THEME_CHANGED` / `WINDOW_CREATED` | ⬜ 未实现 |
 * | `WINDOW_SUSPENDED` / `WINDOW_RESUMED` | ⛔ 桌面端不会触发（移动端生命周期事件） |
 * | `WEBVIEW_CREATED` | ⛔ 桌面端不会触发：本库没有"一个窗口里挂多个 webview"的概念，窗口创建即 webview 创建，用 `WINDOW_CREATED` |
 * | `DRAG_ENTER` / `DRAG_OVER` / `DRAG_DROP` / `DRAG_LEAVE` | ✅ 四个都会来（B10 补齐）。`position` 是**客户区物理像素**；`over` 不带 `paths`、`leave` 只有 `type`。**Linux 的 `enter.paths` 是空数组**——GTK 在 `drag-motion` 阶段拿不到路径。见 `Window.onDragDropEvent` |
 *
 * > 这一行此前记的是"不做：页面拿不到文件路径（浏览器安全模型如此）"。**那个前提只对
 * > HTML5 拖放成立**：本库走的是 OS 级投放，路径一直在宿主手里（`FileDropped`）。
 * > B7 演练里 pier-dock 正是用 `onDragDropEvent` 接"把 .exe 拖到 dock 上固定"，
 * > 于是补齐了 `drop`。剩下的三个仍然没做，原因是原生路径只给一次回调。
 *
 * **为什么把收不到的那几个也列出来**：`TauriEvent.WINDOW_SUSPENDED` 若是 `undefined`，
 * `listen(undefined)` 会在离原因很远的地方炸；常量在、文档标 ⛔，至少 import 处能跑、
 * 读到名字就知道它不会来。
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
    WINDOW_SUSPENDED: "tauri://suspended";
    WINDOW_RESUMED: "tauri://resumed";
    WEBVIEW_CREATED: "tauri://webview-created";
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
