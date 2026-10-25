/**
 * 取当前的传输层。
 * @returns {OrielTransport}
 * @throws {Error} 不在 OrielWeb 宿主里、且没有装 mock 时抛出。
 */
export function getTransport(): OrielTransport;
/**
 * 覆盖传输层（`mocks` 用）。
 * @param {OrielTransport | null} transport `null` 表示恢复真实传输层。
 * @returns {void}
 */
export function setTransportOverride(transport: OrielTransport | null): void;
/**
 * 当前是否已装 mock。
 * @returns {boolean}
 */
export function hasTransportOverride(): boolean;
/** 测试钩子：开关补壳警告（双下划线命名法与 mocks 一致，不是公开 API）。 */
/** @param {boolean} enabled */
export function __setDebugWarnings(enabled: boolean): void;
/** 补壳/平台差异的可观测信号：debug 模式下 console.warn，其余静默（保证零删改）。 */
/** @param {string} message */
export function warnCompat(message: string): void;
/**
 * 记一次订阅。
 *
 * @param {string} label
 */
export function refCloseIntercept(label: string): void;
/**
 * 退一次订阅。
 *
 * @param {string} label
 * @returns {boolean} 是否**归零**（true = 调用方该去关掉宿主接管了）。
 */
export function unrefCloseIntercept(label: string): boolean;
/** 测试钩子：清空计数（`clearMocks` 会调它，避免用例之间互相污染）。 */
export function resetCloseInterceptRefs(): void;
export type OrielTransport = import("./types.js").OrielTransport;
