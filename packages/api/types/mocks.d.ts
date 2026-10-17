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
 * 登记"有哪些窗口"（供将来 window 模块的 `getAllWindows` 读取）。
 *
 * 现在调用不改变任何可观测行为——本包还没有 window 模块。提前提供是为了让
 * 迁移过来的测试代码不必等到那时才写。
 *
 * @param {string[]} labels
 * @returns {void}
 */
export function mockWindows(...labels: string[]): void;
/**
 * 取当前登记的窗口 label（内部用；window 模块落地后由它消费）。
 * @returns {string[]}
 */
export function getMockWindowLabels(): string[];
/**
 * 占位：对标 Tauri 的 `mockConvertFileSrc`。
 *
 * 本库还没有文件 asset 协议（见 core.js 的 `convertFileSrc`），所以这里只记录
 * 自定义协议名，供 `convertFileSrc` 实现时读取。
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
export type OrielTransport = {
    invoke: (name: string, args?: Record<string, unknown>) => Promise<unknown>;
    supportsPerCallTimeout?: boolean;
};
export type MockHandler = (cmd: string, payload: Record<string, unknown> | null) => unknown;
