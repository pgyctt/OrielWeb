// 对标 `@tauri-apps/api/event`。
//
// 与 Tauri 的差别（如实标注）：
//   * **底层通道是 `oriel.on`，它给的是裸 payload**。这里在包内包成 `{ event, id, payload }`，
//     所以包外（直接调 `window.oriel.on` 的既有页面）不受影响——两套并存是刻意的。
//   * `listen` 返回的是**函数**（Tauri 返回 `Promise<UnlistenFn>`），但这个函数**同时是
//     thenable**：`const un = listen(...)`、`const un = await listen(...)`、
//     `listen(...).then(fn => ...)` 三种写法都成立。差别只剩
//     `un instanceof Promise === false`——而那个判断在实际迁移代码里几乎不存在。
//   * `emit` / `emitTo` 需要宿主侧有一个广播命令（`event.emit`），且**要过能力门禁**
//     ——它不是 `win.*` 那种"始终放行"的库内建命令。
//   * `TauriEvent` 常量里，本库目前只实现了 `RESIZED` / `MOVED` / `FOCUS_CHANGED` /
//     `CLOSE_REQUESTED` / `DESTROYED` 的一部分（见 §状态）。**没有 `DRAG_*`**（文件拖放走
//     `FileDropped` 宿主事件，不在页面事件通道上）。

import { invoke } from './core.js';
import { getTransport } from './internal.js';

/**
 * Tauri 内置事件名 → 本库宿主事件名的映射，以及**是否需要打开页面转发**。
 *
 * @remarks
 * 为什么要有这张表而不是让宿主直接发 Tauri 名：
 *   * 宿主事件名（`resize` / `close-requested`）是既有契约，改了会破坏已经用它的页面；
 *   * 映射只在包内做一次，页面看到的仍是 Tauri 名——迁移代码不必改。
 *
 * `forwarding` 那一列是**性能开关**：尺寸/位置事件在拖动时每个像素来一次，
 * 而推给页面在 macOS/Linux 上是一次 JS 求值。所以宿主默认不推，
 * 由这里在页面真的订阅时打开（见 `enableForwarding`）。缺省表示无需开关
 * （低频事件本就该推，或由别的机制决定）。
 *
 * `tauri://destroyed` **不在表里**：窗口销毁时页面本身正在被拆掉，
 * 那条事件送不到（Tauri 的 JS 侧同样如此，它是给 Rust 侧用的）。
 * 需要"窗口要关了"的通知请用 `tauri://close-requested`。
 *
 * @type {Record<string, { host: string, forwarding?: 'resize' | 'move' | 'focus' | 'scale' }>}
 */
const HOST_EVENT_MAP = Object.freeze({
    'tauri://resize': { host: 'resize', forwarding: 'resize' },
    'tauri://move': { host: 'move', forwarding: 'move' },
    'tauri://focus': { host: 'focus', forwarding: 'focus' },
    'tauri://blur': { host: 'blur', forwarding: 'focus' },
    // 关闭请求由宿主侧的关闭接管开关直接决定推送时机（挂起时必须推），
    // 所以它不需要"订阅了才推"的中间态。
    'tauri://close-requested': { host: 'close-requested' },
    // 主题变化是**应用级广播**：宿主在检测到系统主题变了之后主动推给每个窗口
    // （见 OrielApp.PushTheme），所以也不需要 forwarding 那套"订阅了才推"。
    // 顺带一提，宿主推的载荷就是裸的 "dark" / "light"，与 Tauri 的 Theme 形状一致，
    // 不需要在这一层翻译。
    'tauri://theme-changed': { host: 'theme.changed' },
    // 缩放变化：与 resize/move/focus 同类，需要 forwarding（页面订阅了宿主才推）。
    'tauri://scale-change': { host: 'scale.changed', forwarding: 'scale' },
});

/**
 * 把 Tauri 事件名翻译成宿主事件名。
 *
 * @param {string} eventName
 * @returns {string}
 */
export function toHostEventName(eventName) {
    return HOST_EVENT_MAP[eventName]?.host ?? eventName;
}

/**
 * 已打开转发的项 → 还有几个订阅者在用（包内记账，避免每次 listen 都发一次 IPC）。
 *
 * **用计数而不是 Set**：同一个 kind 可能被多个订阅共享（`tauri://focus` 与
 * `tauri://blur` 都映射到 `focus`），所以"最后一个订阅者走了才关"需要计数。
 * 用 Set 的话第一个退订就会把转发关掉，另一个订阅者从此收不到事件。
 *
 * @type {Map<'resize' | 'move' | 'focus' | 'scale', number>}
 */
const forwardingRefCount = new Map();

/**
 * 按需打开宿主→页面的转发。
 *
 * @param {'resize' | 'move' | 'focus' | 'scale' | undefined} kind
 * @returns {void}
 * @remarks
 * **失败必须让人看见**：命令被能力门禁拒掉时事件永远不会到，
 * 而静默的后果是"我明明订阅了却收不到"——那是最难排查的一类问题
 * （页面代码看起来完全正确）。所以这里把拒绝原因打到 console.error，
 * 并说清要怎么修。
 */
function enableForwarding(kind) {
    if (!kind) {
        return;
    }

    const count = forwardingRefCount.get(kind) ?? 0;
    forwardingRefCount.set(kind, count + 1);

    // 已经开着（count > 1）：不需要再发 IPC
    if (count > 0) {
        return;
    }

    // 不等回执（listen 是同步的），但失败要报出来
    invoke('window.setEventForwarding', { [kind]: true }).catch((error) => {
        // 打不开就把计数清零：否则后续的订阅者会以为"已经开着"而不再尝试，
        // 而这个 kind 永远不会到。清零之后下一次订阅会重新试一次。
        forwardingRefCount.delete(kind);
        console.error(
            `@orielweb/api/event: 无法打开「${kind}」事件转发，相关事件不会到达页面。` +
            `原因：${error && error.message ? error.message : error}。` +
            '请在宿主侧允许 `window.setEventForwarding`（或用 OrielWindowOptions.EventForwarding 静态开启）。');
    });
}

/**
 * 一个订阅者走了：最后一个走的时候把宿主侧的转发关掉。
 *
 * @param {'resize' | 'move' | 'focus' | 'scale' | undefined} kind
 * @returns {void}
 * @remarks
 * **为什么要关**：不关的话，一次 `listen` 会让宿主在整个窗口生命期里持续推送
 * 该事件（拖动时每像素一条）。页面已经没人听了，那是纯浪费——而且与
 * `onCloseRequested` 的"退订时关掉接管"不一致，两处收尾策略应当统一。
 *
 * 失败**不报错也不 console.error**：关不掉只意味着多推几条事件（宿主那边仍是
 * 应用配置的值），不影响任何正确性。这与"打不开必须报"是两回事——打不开会让
 * 页面收不到事件，而关不掉只会多收。
 */
function disableForwarding(kind) {
    if (!kind) {
        return;
    }

    const count = forwardingRefCount.get(kind) ?? 0;
    if (count > 1) {
        forwardingRefCount.set(kind, count - 1);
        return;
    }

    forwardingRefCount.delete(kind);

    // count === 0 说明这个 kind 本来就没打开过（比如打不开时已清零）：
    // 此时不必发命令，发了也只是把宿主的值从 false 再设成 false。
    if (count === 0) {
        return;
    }

    invoke('window.setEventForwarding', { [kind]: false }).catch(() => {
        // 见上：关不掉只多推几条事件，不值得打扰调用方
    });
}

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
 * 把退订函数包成 **thenable 函数**。
 *
 * @param {() => void} unlisten
 * @returns {(() => void) & PromiseLike<() => void>}
 * @remarks
 * Tauri 的 `listen()` 返回 `Promise<UnlistenFn>`，本库历史上一律同步返回退订函数。
 * 迁移代码里 `await listen(...)` 很常见，而 `await` 一个普通函数只会得到它本身——
 * 于是 `const un = await listen(...)` 恰好也能用，但 `listen(...).then(...)` 会炸。
 *
 * 这里给函数挂一个 `then`：三种写法同时成立——
 * <list type="bullet">
 *   <item><c>const un = listen(...)</c>（既有写法，零改动）</item>
 *   <item><c>const un = await listen(...)</c></item>
 *   <item><c>listen(...).then(fn =&gt; ...)</c></item>
 * </list>
 *
 * **为什么不直接返回 Promise**：那会让全仓 `const un = listen(...)` 变成
 * `un` 是 Promise、调用 `un()` 抛 TypeError，是破坏性变更；而收益只是
 * `instanceof Promise` 这个几乎没人写的判断。
 *
 * **`then` 里 resolve 的是 `plain` 而不是 `unlisten`**：函数自己已经是 thenable，
 * 用它 resolve 会形成 thenable 自我吸收的无限递归。
 */
function asThenable(unlisten) {
    // 一个不含 `then` 的同义函数：await 之后拿到它，调用行为完全一样。
    const plain = () => unlisten();

    /** @param {any} [onFulfilled] @param {any} [onRejected] @returns {Promise<any>} */
    const thenFn = (onFulfilled, onRejected) => Promise.resolve(plain).then(onFulfilled, onRejected);

    /** @param {any} [onRejected] @returns {Promise<any>} */
    const catchFn = (onRejected) => Promise.resolve(plain).catch(onRejected);

    /** @param {any} [onFinally] @returns {Promise<any>} */
    const finallyFn = (onFinally) => Promise.resolve(plain).finally(onFinally);

    Object.defineProperties(unlisten, {
        then: { value: thenFn, enumerable: false, configurable: true },
        catch: { value: catchFn, enumerable: false, configurable: true },
        finally: { value: finallyFn, enumerable: false, configurable: true },
    });

    return /** @type {(() => void) & PromiseLike<() => void>} */ (unlisten);
}

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
export function listen(eventName, handler, options) {
    void options;
    return asThenable(subscribe(eventName, handler, false));
}

/**
 * 订阅一个事件，收到**一次**后自动退订（对标 Tauri 的 `once`）。
 *
 * @param {string} eventName
 * @param {(event: { event: string, id: number, payload: any }) => void} handler
 * @param {{ target?: string | { label: string } }} [options]
 * @returns {(() => void) & PromiseLike<() => void>} 退订函数；在事件到达前调用它可以取消订阅。
 *   与 {@link listen} 一样是 thenable，`await once(...)` 也成立。
 */
export function once(eventName, handler, options) {
    void options;
    return asThenable(subscribe(eventName, handler, true));
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

    // Tauri 名 → 宿主名。页面看到的 `event.event` 仍是**它订阅时用的那个名字**
    // （下面 toEventObject 用 eventName），这样迁移代码读 `e.event === TauriEvent.WINDOW_RESIZED` 成立。
    const mapping = HOST_EVENT_MAP[eventName];
    const hostName = mapping?.host ?? eventName;

    // 高频事件（尺寸/位置/焦点）在宿主侧默认不推，这里按需打开。
    // 注意：打开是按 kind 计数的，退订时要对称地减一次（见下面的 unlisten）。
    enableForwarding(mapping?.forwarding);

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
    const unlistenRaw = transport.on(hostName, entry.raw);

    // 退订：同时清包内表与底层通道。**幂等**——once 触发时调一次，调用方之后
    // 再调一次也不该出问题（重复删同一个元素是无害的，重复调底层退订也一样）。
    //
    // 转发计数只在**第一次**退订时减一次：用一个标志位，否则幂等的第二条要求
    // 会把计数多减一次，导致"另一个还在听的订阅者突然收不到事件"。
    let forwardingReleased = false;
    unlisten = () => {
        set.delete(entry);
        if (set.size === 0) {
            subscriptions.delete(eventName);
        }

        if (!forwardingReleased) {
            forwardingReleased = true;
            disableForwarding(mapping?.forwarding);
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
 * | `DRAG_ENTER` / `DRAG_OVER` / `DRAG_DROP` / `DRAG_LEAVE` | ⛔ 不做：文件拖放走宿主 `WebviewWindow.FileDropped` 事件，不在页面事件通道上（页面拿不到文件路径，浏览器安全模型如此） |
 *
 * **为什么把收不到的那几个也列出来**：`TauriEvent.WINDOW_SUSPENDED` 若是 `undefined`，
 * `listen(undefined)` 会在离原因很远的地方炸；常量在、文档标 ⛔，至少 import 处能跑、
 * 读到名字就知道它不会来。
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
    // 下面三个桌面端不会触发，只为"名字在"而存在（理由见上方表格）。
    WINDOW_SUSPENDED: 'tauri://suspended',
    WINDOW_RESUMED: 'tauri://resumed',
    WEBVIEW_CREATED: 'tauri://webview-created',
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

    // 兜底：上面每一条 unlisten 都会减计数，但若中途有异常或计数与实际脱节，
    // 这里直接清空，免得下一个测试从"以为转发还开着"的假状态开始。
    forwardingRefCount.clear();
}
