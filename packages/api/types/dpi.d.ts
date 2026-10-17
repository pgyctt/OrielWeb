/**
 * 把任意"看起来像尺寸"的入参归一成尺寸对象。
 *
 * 接受四种形态，与 Tauri 的宽松度一致：
 * `{width, height}`（缺 `type` 时按逻辑算）、`LogicalSize`/`PhysicalSize` 实例、以及裸数字对。
 *
 * @param {SizeObject | { width: number, height: number } | [number, number]} size
 * @param {SizeType} [fallbackType] 入参缺 `type` 时用的类型，默认 `'Logical'`。
 * @returns {SizeObject}
 * @throws {Error} 入参不可解释时抛出（不静默当成 0，那会得到一个 0x0 的窗口）。
 */
export function normalizeSize(size: SizeObject | {
    width: number;
    height: number;
} | [number, number], fallbackType?: SizeType): SizeObject;
/**
 * 把任意"看起来像位置"的入参归一成位置对象。
 *
 * @param {PositionObject | { x: number, y: number } | [number, number]} position
 * @param {SizeType} [fallbackType] 入参缺 `type` 时用的类型，默认 `'Logical'`。
 * @returns {PositionObject}
 * @throws {Error} 入参不可解释时抛出。
 */
export function normalizePosition(position: PositionObject | {
    x: number;
    y: number;
} | [number, number], fallbackType?: SizeType): PositionObject;
/**
 * 逻辑尺寸 → 物理尺寸。
 *
 * @param {number} logical 逻辑像素。
 * @param {number} scaleFactor 缩放因子（1 = 100%）。
 * @returns {number} 物理像素（四舍五入到整像素，与宿主侧 LogicalPixels.ToPhysical 一致）。
 */
export function logicalToPhysical(logical: number, scaleFactor: number): number;
/**
 * 物理尺寸 → 逻辑尺寸。
 *
 * @param {number} physical 物理像素。
 * @param {number} scaleFactor 缩放因子（1 = 100%）。
 * @returns {number} 逻辑像素（保留小数：除以缩放比通常不是整数，取整会逐次累积偏差）。
 */
export function physicalToLogical(physical: number, scaleFactor: number): number;
/**
 * @typedef {'Logical' | 'Physical'} SizeType
 * @typedef {{ type: SizeType, width: number, height: number }} SizeObject
 * @typedef {{ type: SizeType, x: number, y: number }} PositionObject
 */
/**
 * 逻辑像素尺寸（对标 `LogicalSize`）。
 */
export class LogicalSize {
    /**
     * @param {number} width
     * @param {number} height
     */
    constructor(width: number, height: number);
    /** @type {SizeType} */
    type: SizeType;
    width: number;
    height: number;
}
/**
 * 物理像素尺寸（对标 `PhysicalSize`）。
 */
export class PhysicalSize {
    /**
     * @param {number} width
     * @param {number} height
     */
    constructor(width: number, height: number);
    /** @type {SizeType} */
    type: SizeType;
    width: number;
    height: number;
}
/**
 * 逻辑坐标位置（对标 `LogicalPosition`）。
 */
export class LogicalPosition {
    /**
     * @param {number} x
     * @param {number} y
     */
    constructor(x: number, y: number);
    /** @type {SizeType} */
    type: SizeType;
    x: number;
    y: number;
}
/**
 * 物理坐标位置（对标 `PhysicalPosition`）。
 */
export class PhysicalPosition {
    /**
     * @param {number} x
     * @param {number} y
     */
    constructor(x: number, y: number);
    /** @type {SizeType} */
    type: SizeType;
    x: number;
    y: number;
}
export type SizeType = "Logical" | "Physical";
export type SizeObject = {
    type: SizeType;
    width: number;
    height: number;
};
export type PositionObject = {
    type: SizeType;
    x: number;
    y: number;
};
