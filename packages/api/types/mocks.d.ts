/**
 * 装一个假的 IPC 传输层。
 *
 * @param {MockHandler} invoke
 *   处理函数。返回值会被当作命令结果；抛出的异常会变成 Promise 的 reject
 *   （与真实通道一致：页面侧拿到的都是 reject 的 Error）。
 * @returns {void}
 */
export function mockIPC(invoke: MockHandler): void;
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
export function mockEmit(eventName: string, payload?: unknown): void;
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
export function mockWindows(...labels: string[]): void;
/**
 * 取当前登记的窗口 label（内部用；`window.getAllWindows` 一类查询的 mock 数据源）。
 * @returns {string[]}
 */
export function getMockWindowLabels(): string[];
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
export function mockConvertFileSrc(protocol?: string): void;
/**
 * 清掉全部 mock，恢复真实传输层。
 * @returns {void}
 */
export function clearMocks(): void;
/**
 * 当前是否装了 mock（测试与 `isTauri` 用）。
 * @returns {boolean}
 */
export function isMocked(): boolean;
export type OrielTransport = import("./types.js").OrielTransport;
export type MockHandler = (cmd: string, payload: Record<string, unknown> | null) => unknown;
