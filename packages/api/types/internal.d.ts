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
export type OrielTransport = import("./types.js").OrielTransport;
