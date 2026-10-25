// 对标 `@tauri-apps/api/mocks`：让前端能在 Node 里跑单测，不必起真实窗口。
//
// 为什么它必须与包同时提供（差异分析 §2.19）：没有它，前端测试只能起真窗口，
// 而真窗口在 CI 里跑不动。这不是"锦上添花"，是"这个包能不能被正经使用"的前提。
//
// 与 Tauri 的差别：
//   * Tauri 的 `mockIPC` 回调签名是 `(cmd, payload) => ...`；这里一致。
//   * `mockWindows` 在本库有真实作用：它决定 mock 传输层上报的 `label`
//     （第一个参数），进而决定 `getCurrentWindow()` 返回哪个窗口句柄。

import { setTransportOverride, hasTransportOverride, resetCloseInterceptRefs } from './internal.js';

/**
 * @typedef {import('./types.js').OrielTransport} OrielTransport
 *
 * @typedef {(cmd: string, payload: Record<string, unknown> | null) => unknown} MockHandler
 */

/**
 * 当前 mock 的命令处理函数；`null` 表示没有装 mock。
 * @type {MockHandler | null}
 */
let handler = null;

/**
 * `mockWindows` 登记的 label 列表；第一个元素是 mock 传输层上报的"本窗口"。
 * @type {string[]}
 */
let mockWindowLabels = ['main'];

/**
 * `mockConvertFileSrc` 登记的协议名；`null` 表示没登记——mock 传输层就
 * 不提供 `convertFileSrc`（与旧宿主同语义，core.convertFileSrc 会如实报"不支持"）。
 * @type {string | null}
 */
let mockFileProtocol = null;

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

    /** @type {OrielTransport} */
    const transport = {
        // 声明支持 per-call timeout。
        //
        // 不声明的话，页面在测试里写 `invoke(cmd, args, { timeout })` 会被 core 的显式检查
        // 拒掉——而那个检查的用意是"老宿主上别静默失效"，不是"测试里也要拒"。
        // mock 代表的是当前版本的宿主，它就该具备当前版本的能力。
        supportsPerCallTimeout: true,
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
    };

    // label / convertFileSrc 用 getter 挂：mockWindows / mockConvertFileSrc 在
    // mockIPC **之后**调用也要能生效（测试里常见顺序是先 mockIPC 再补窗口设置），
    // 快照值会把后面的调用变成静默不生效——正是本模块要消灭的那类陷阱。
    const dynamic = /** @type {OrielTransport} */ (/** @type {unknown} */ (
        Object.defineProperties(transport, {
            label: {
                get: () => mockWindowLabels[0],
            },
            convertFileSrc: {
                get: () => (mockFileProtocol === null
                    ? undefined
                    : (/** @param {string} filePath */ (filePath) =>
                        `${mockFileProtocol}://file.oriel/${encodeURIComponent(filePath)}`)),
            },
        })
    ));

    setTransportOverride(dynamic);
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
 * 登记"有哪些窗口"（对标 Tauri 的 `mockWindows`）。
 *
 * 第一个 label 是 mock 传输层上报的**本窗口**——`getCurrentWindow()` 会返回
 * 它的句柄，后续改成别的 label 也会实时生效（getter，见 `mockIPC`）。
 * 其余 label 供 `getAllWindows` 一类查询读取（`getMockWindowLabels`，内部用）。
 *
 * 在 `mockIPC` 之前或之后调用都可以。
 *
 * @param {...string} labels
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
 * 取当前登记的窗口 label（内部用；`window.getAllWindows` 一类查询的 mock 数据源）。
 * @returns {string[]}
 */
export function getMockWindowLabels() {
    return [...mockWindowLabels];
}

/**
 * 让 mock 传输层支持 `convertFileSrc`（对标 Tauri 的 `mockConvertFileSrc`）。
 *
 * 登记后 `core.convertFileSrc` 在 mock 下返回
 * `<protocol>://file.oriel/<encodeURIComponent(path)>`；未登记时 mock 传输层
 * 不提供该能力（`convertFileSrc` 如实抛"当前宿主不支持"，与旧宿主同语义）。
 *
 * 在 `mockIPC` 之前或之后调用都可以。注意 protocol 只影响 URL 前缀——
 * 本库的文件内容固定走 `file.oriel` 这个 host，与真实宿主一致。
 *
 * @param {string} protocol
 * @returns {void}
 */
export function mockConvertFileSrc(protocol = 'oriel') {
    if (typeof protocol !== 'string' || protocol === '') {
        throw new Error('mockConvertFileSrc: protocol 必须是非空字符串');
    }

    mockFileProtocol = protocol;
}

/**
 * 清掉全部 mock，恢复真实传输层。
 * @returns {void}
 */
export function clearMocks() {
    handler = null;
    mockWindowLabels = ['main'];
    mockFileProtocol = null;
    // 事件订阅表也要清：不清的话上一条用例订阅的处理器会在下一条用例的 mockEmit 里
    // 继续被调用，而那两个用例本该互不干扰——这种污染最难查（症状是"单跑通过、连跑失败"）。
    mockListeners = new Map();
    // 关闭接管的订阅计数也要清：它是按窗口 label 记的，留着会让下一条用例的
    // 退订逻辑以为"还有别人在订阅"，于是不去关宿主接管。
    resetCloseInterceptRefs();
    setTransportOverride(null);
}

/**
 * 当前是否装了 mock（测试与 `isTauri` 用）。
 * @returns {boolean}
 */
export function isMocked() {
    return hasTransportOverride();
}
