// 对标 `@tauri-apps/api/event`。
//
// 与 Tauri 的差别（如实标注）：
//   * **底层通道是 `oriel.on`，它给的是裸 payload**。这里在包内包成 `{ event, id, payload }`，
//     所以包外（直接调 `window.oriel.on` 的既有页面）不受影响——两套并存是刻意的。
//   * `listen` 返回的是**同步函数**（Tauri 返回 `Promise<UnlistenFn>`）。`await` 一个非 Promise
//     也成立，所以迁移代码 `const un = await listen(...)` 照样能跑。
//   * `emit` / `emitTo` 需要宿主侧有一个广播命令（`event.emit`），且**要过能力门禁**
//     ——它不是 `win.*` 那种"始终放行"的库内建命令。
//   * `TauriEvent` 常量里，本库目前只实现了 `RESIZED` / `MOVED` / `FOCUS_CHANGED` /
//     `CLOSE_REQUESTED` / `DESTROYED` 的一部分（见 §状态）。**没有 `DRAG_*`**（文件拖放走
//     `FileDropped` 宿主事件，不在页面事件通道上）。

import { invoke } from './core.js';
import { getTransport } from './internal.js';

/**
 * 事件 id 的自增序号。
 *
 * Tauri 用它做"取消订阅时告诉后端是哪一个"的标识；本库的退订在页面侧完成
 * （从 Set 里删掉处理器），所以 id 只用于**回传给回调**——与 Tauri 的形状一致，
 * 便于迁移代码在回调里读 `event.id`。
 */
let nextEventId = 1;

/**
 * @typedef {{ handler: (event: any) => void, raw: (payload: unknown) => void, unlisten: () => void }} Subscription
 */

/**
 * 每个事件名的订阅表。
 *
 * 为什么在包内再维护一份而不是直接复用 `oriel.on` 的 Set：
 * 包内需要知道"某个处理器是不是 once"，而那个信息在底层通道里没有地方放。
 * 底层 Set 只存"收到事件就调它"的函数，once 的语义由这里包一层实现。
 *
 * @type {Map<string, Set<Subscription>>}
 */
const subscriptions = new Map();

/**
 * 把一个裸 payload 包成 Tauri 形状的事件对象。
 *
 * @param {string} eventName
 * @param {unknown} payload
 * @returns {{ event: string, id: number, payload: unknown }}
 */
function toEventObject(eventName, payload) {
    return { event: eventName, id: nextEventId++, payload };
}

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
export function listen(eventName, handler, options) {
    void options;
    return subscribe(eventName, handler, false);
}

/**
 * 订阅一个事件，收到**一次**后自动退订（对标 Tauri 的 `once`）。
 *
 * @param {string} eventName
 * @param {(event: { event: string, id: number, payload: any }) => void} handler
 * @param {{ target?: string | { label: string } }} [options]
 * @returns {() => void} 退订函数；在事件到达前调用它可以取消订阅。
 */
export function once(eventName, handler, options) {
    void options;
    return subscribe(eventName, handler, true);
}

/**
 * 订阅实现（listen 与 once 共用）。
 *
 * @param {string} eventName
 * @param {(event: any) => void} handler
 * @param {boolean} isOnce
 * @returns {() => void}
 */
function subscribe(eventName, handler, isOnce) {
    if (typeof eventName !== 'string' || eventName === '') {
        throw new Error('listen: eventName 必须为非空字符串');
    }

    if (typeof handler !== 'function') {
        throw new Error('listen: handler 必须为函数');
    }

    const transport = getTransport();
    if (typeof transport.on !== 'function') {
        throw new Error(
            'listen: 当前传输层没有 on()。本库的桥接脚本应当提供它；' +
            '如果你在 Node 里跑，请用 mocks 的 mockIPC 或直接构造传输层。');
    }

    let set = subscriptions.get(eventName);
    if (!set) {
        set = new Set();
        subscriptions.set(eventName, set);
    }

    // unlisten 先声明后赋值：raw 里要引用它（once 在调用处理器之前退订），
    // 而 raw 又要在 unlisten 赋值之前交给底层通道。用一个可变槽位打断这个循环。
    /** @type {() => void} */
    let unlisten = () => {};

    /** @type {Subscription} */
    const entry = {
        handler,
        raw: (payload) => {
            // once 必须在**调用处理器之前**退订：处理器里如果又触发同名事件，
            // 它不该再被叫一次（那是 once 的定义）。
            if (isOnce) {
                unlisten();
            }

            handler(toEventObject(eventName, payload));
        },
        unlisten: () => {},
    };

    // 底层通道的订阅（返回的是它自己的退订函数）
    const unlistenRaw = transport.on(eventName, entry.raw);

    // 退订：同时清包内表与底层通道。**幂等**——once 触发时调一次，调用方之后
    // 再调一次也不该出问题（重复删同一个元素是无害的，重复调底层退订也一样）。
    unlisten = () => {
        set.delete(entry);
        if (set.size === 0) {
            subscriptions.delete(eventName);
        }

        if (typeof unlistenRaw === 'function') {
            unlistenRaw();
        }
    };

    entry.unlisten = unlisten;
    set.add(entry);
    return unlisten;
}

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
export async function emit(eventName, payload) {
    if (typeof eventName !== 'string' || eventName === '') {
        throw new Error('emit: eventName 必须为非空字符串');
    }

    await invoke('event.emit', { event: eventName, payload: payload ?? null, target: null });
}

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
export async function emitTo(target, eventName, payload) {
    if (typeof eventName !== 'string' || eventName === '') {
        throw new Error('emitTo: eventName 必须为非空字符串');
    }

    const label = typeof target === 'string' ? target : target?.label;
    if (typeof label !== 'string' || label === '') {
        throw new Error('emitTo: target 必须是窗口 label（字符串）或带 label 字段的对象');
    }

    await invoke('event.emit', { event: eventName, payload: payload ?? null, target: label });
}

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
export const TauriEvent = Object.freeze({
    WINDOW_RESIZED: 'tauri://resize',
    WINDOW_MOVED: 'tauri://move',
    WINDOW_CLOSE_REQUESTED: 'tauri://close-requested',
    WINDOW_DESTROYED: 'tauri://destroyed',
    WINDOW_FOCUS: 'tauri://focus',
    WINDOW_BLUR: 'tauri://blur',
    WINDOW_SCALE_FACTOR_CHANGED: 'tauri://scale-change',
    WINDOW_THEME_CHANGED: 'tauri://theme-changed',
    WINDOW_CREATED: 'tauri://window-created',
    DRAG_ENTER: 'tauri://drag-enter',
    DRAG_OVER: 'tauri://drag-over',
    DRAG_DROP: 'tauri://drag-drop',
    DRAG_LEAVE: 'tauri://drag-leave',
});

/**
 * 清掉全部包内订阅（仅供测试用；真实页面不该调它）。
 *
 * @returns {void}
 */
export function __clearEventSubscriptions() {
    for (const set of subscriptions.values()) {
        for (const entry of Array.from(set)) {
            entry.unlisten();
        }
    }

    subscriptions.clear();
}
