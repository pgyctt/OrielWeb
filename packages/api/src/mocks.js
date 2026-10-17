// 对标 `@tauri-apps/api/mocks`：让前端能在 Node 里跑单测，不必起真实窗口。
//
// 为什么它必须与包同时提供（差异分析 §2.19）：没有它，前端测试只能起真窗口，
// 而真窗口在 CI 里跑不动。这不是"锦上添花"，是"这个包能不能被正经使用"的前提。
//
// 与 Tauri 的差别：
//   * Tauri 的 `mockIPC` 回调签名是 `(cmd, payload) => ...`；这里一致。
//   * 本库**没有 window 概念**（尚未有 window 模块），所以 `mockWindows` 只做占位——
//     它记录 label 列表，等 window 模块落地后由它读。现在调用不会报错，但也不影响任何行为。

import { setTransportOverride, hasTransportOverride } from './internal.js';

/**
 * @typedef {{
 *   invoke: (name: string, args?: Record<string, unknown>) => Promise<unknown>,
 *   supportsPerCallTimeout?: boolean
 * }} OrielTransport
 *
 * @typedef {(cmd: string, payload: Record<string, unknown> | null) => unknown} MockHandler
 */

/**
 * 当前 mock 的命令处理函数；`null` 表示没有装 mock。
 * @type {MockHandler | null}
 */
let handler = null;

/** `mockWindows` 登记的 label 列表（供将来 window 模块读取）。 */
let mockWindowLabels = ['main'];

/**
 * mock 传输层的事件订阅表：事件名 → 处理器集合。
 *
 * 真实传输层由桥接脚本提供 `on()`；mock 侧必须自己实现一份，否则
 * `listen`/`once` 在 Node 里根本没法测——而那正是 `mocks` 存在的理由。
 *
 * @type {Map<string, Set<(payload: unknown) => void>>}
 */
let mockListeners = new Map();

/**
 * 装一个假的 IPC 传输层。
 *
 * @param {MockHandler} invoke
 *   处理函数。返回值会被当作命令结果；抛出的异常会变成 Promise 的 reject
 *   （与真实通道一致：页面侧拿到的都是 reject 的 Error）。
 * @returns {void}
 */
export function mockIPC(invoke) {
    if (typeof invoke !== 'function') {
        throw new Error('mockIPC: 需要一个 (cmd, payload) => unknown 形式的函数');
    }

    handler = invoke;
    setTransportOverride({
        async invoke(name, args) {
            // 真实通道里 `args` 缺省时宿主收到的是 `null`（见 oriel-bridge.js 的 invoke）。
            // 这里保持一致，免得测试里 `args === undefined` 的分支与真机不同。
            const current = handler;
            if (current === null) {
                throw new Error('mockIPC: 处理器已被 clearMocks 清掉');
            }

            return current(name, args ?? null);
        },
        on(name, listener) {
            let set = mockListeners.get(name);
            if (!set) {
                set = new Set();
                mockListeners.set(name, set);
            }

            set.add(listener);
            return () => {
                set.delete(listener);
                if (set.size === 0) {
                    mockListeners.delete(name);
                }
            };
        },
    });
}

/**
 * 模拟一次**宿主 → 页面**的事件（测试专用）。
 *
 * 对标真实宿主推事件的路径：`oriel._onEvent(name, payload)` 把裸 payload
 * 交给订阅者。`@orielweb/api/event` 的 `listen` 再把它包成 `{ event, id, payload }`。
 *
 * @param {string} eventName
 * @param {unknown} [payload]
 * @returns {void}
 */
export function mockEmit(eventName, payload) {
    if (typeof eventName !== 'string' || eventName === '') {
        throw new Error('mockEmit: eventName 必须为非空字符串');
    }

    const set = mockListeners.get(eventName);
    if (!set) {
        return;
    }

    // 复制一份再遍历：处理器内部退订不会影响本次派发（与桥接脚本一致）
    for (const listener of Array.from(set)) {
        try {
            listener(payload === undefined ? null : payload);
        } catch {
            // 单个处理器抛出不该中断同一次派发里其它处理器——与桥接脚本的隔离策略一致
        }
    }
}

/**
 * 登记"有哪些窗口"（供将来 window 模块的 `getAllWindows` 读取）。
 *
 * 现在调用不改变任何可观测行为——本包还没有 window 模块。提前提供是为了让
 * 迁移过来的测试代码不必等到那时才写。
 *
 * @param {string[]} labels
 * @returns {void}
 */
export function mockWindows(...labels) {
    if (labels.length === 0) {
        throw new Error('mockWindows: 至少给一个窗口 label');
    }

    for (const label of labels) {
        if (typeof label !== 'string' || label === '') {
            throw new Error('mockWindows: label 必须是非空字符串');
        }
    }

    mockWindowLabels = [...labels];
}

/**
 * 取当前登记的窗口 label（内部用；window 模块落地后由它消费）。
 * @returns {string[]}
 */
export function getMockWindowLabels() {
    return [...mockWindowLabels];
}

/**
 * 占位：对标 Tauri 的 `mockConvertFileSrc`。
 *
 * 本库还没有文件 asset 协议（见 core.js 的 `convertFileSrc`），所以这里只记录
 * 自定义协议名，供 `convertFileSrc` 实现时读取。
 *
 * @param {string} protocol
 * @returns {void}
 */
export function mockConvertFileSrc(protocol = 'oriel') {
    if (typeof protocol !== 'string' || protocol === '') {
        throw new Error('mockConvertFileSrc: protocol 必须是非空字符串');
    }
}

/**
 * 清掉全部 mock，恢复真实传输层。
 * @returns {void}
 */
export function clearMocks() {
    handler = null;
    mockWindowLabels = ['main'];
    // 事件订阅表也要清：不清的话上一条用例订阅的处理器会在下一条用例的 mockEmit 里
    // 继续被调用，而那两个用例本该互不干扰——这种污染最难查（症状是"单跑通过、连跑失败"）。
    mockListeners = new Map();
    setTransportOverride(null);
}

/**
 * 当前是否装了 mock（测试与 `isTauri` 用）。
 * @returns {boolean}
 */
export function isMocked() {
    return hasTransportOverride();
}
