// 对标 `@tauri-apps/api/dpi`。
//
// 纯形状层：只负责"多态尺寸/位置对象"的构造与归一，**不含任何 IPC**。
// 真正的换算发生在宿主侧（Windows 按窗口 DPI 折物理像素、macOS/Linux 用各自逻辑像素），
// 这里不重复一遍——两份换算必然漂移，而漂移的表现是"某个平台上差一圈"。
//
// 与 Tauri 的差别：
//   * Tauri 的 `LogicalSize` 等是**类**（带 `type` 字段供 Rust 侧反序列化）；
//     这里同样是类 + `type` 字段，形状一致。
//   * 本库的宿主命令目前只吃 int 逻辑像素（`win.*`），`PhysicalSize` 会被
//     `window.setSize` 换算成逻辑尺寸再发（见 window 模块，P0-5/E3）。

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
    constructor(width, height) {
        /** @type {SizeType} */
        this.type = 'Logical';
        this.width = width;
        this.height = height;
    }

    /**
     * 换算成物理像素尺寸（对标 Tauri 的 `LogicalSize.toPhysical(scaleFactor)`）。
     *
     * @param {number} scaleFactor
     * @returns {PhysicalSize}
     */
    toPhysical(scaleFactor) {
        return new PhysicalSize(logicalToPhysical(this.width, scaleFactor), logicalToPhysical(this.height, scaleFactor));
    }
}

/**
 * 物理像素尺寸（对标 `PhysicalSize`）。
 */
export class PhysicalSize {
    /**
     * @param {number} width
     * @param {number} height
     */
    constructor(width, height) {
        /** @type {SizeType} */
        this.type = 'Physical';
        this.width = width;
        this.height = height;
    }

    /**
     * 换算成逻辑像素尺寸（对标 Tauri 的 `PhysicalSize.toLogical(scaleFactor)`）。
     *
     * @param {number} scaleFactor
     * @returns {LogicalSize}
     */
    toLogical(scaleFactor) {
        return new LogicalSize(physicalToLogical(this.width, scaleFactor), physicalToLogical(this.height, scaleFactor));
    }
}

/**
 * 逻辑坐标位置（对标 `LogicalPosition`）。
 */
export class LogicalPosition {
    /**
     * @param {number} x
     * @param {number} y
     */
    constructor(x, y) {
        /** @type {SizeType} */
        this.type = 'Logical';
        this.x = x;
        this.y = y;
    }

    /**
     * 换算成物理坐标（对标 Tauri 的 `LogicalPosition.toPhysical(scaleFactor)`）。
     *
     * @param {number} scaleFactor
     * @returns {PhysicalPosition}
     */
    toPhysical(scaleFactor) {
        return new PhysicalPosition(logicalToPhysical(this.x, scaleFactor), logicalToPhysical(this.y, scaleFactor));
    }
}

/**
 * 物理坐标位置（对标 `PhysicalPosition`）。
 */
export class PhysicalPosition {
    /**
     * @param {number} x
     * @param {number} y
     */
    constructor(x, y) {
        /** @type {SizeType} */
        this.type = 'Physical';
        this.x = x;
        this.y = y;
    }

    /**
     * 换算成逻辑坐标（对标 Tauri 的 `PhysicalPosition.toLogical(scaleFactor)`）。
     *
     * @param {number} scaleFactor
     * @returns {LogicalPosition}
     */
    toLogical(scaleFactor) {
        return new LogicalPosition(physicalToLogical(this.x, scaleFactor), physicalToLogical(this.y, scaleFactor));
    }
}

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
export function normalizeSize(size, fallbackType = 'Logical') {
    if (Array.isArray(size)) {
        const [width, height] = size;
        return { type: fallbackType, width: requireFinite(width, 'width'), height: requireFinite(height, 'height') };
    }

    if (size && typeof size === 'object') {
        const candidate = /** @type {Partial<SizeObject>} */ (size);
        const type = candidate.type === 'Physical' ? 'Physical'
            : candidate.type === 'Logical' ? 'Logical'
            : fallbackType;
        return {
            type,
            width: requireFinite(candidate.width, 'width'),
            height: requireFinite(candidate.height, 'height'),
        };
    }

    throw new Error('normalizeSize: 需要 {width, height} 形式的对象（或 [width, height]）');
}

/**
 * 把任意"看起来像位置"的入参归一成位置对象。
 *
 * @param {PositionObject | { x: number, y: number } | [number, number]} position
 * @param {SizeType} [fallbackType] 入参缺 `type` 时用的类型，默认 `'Logical'`。
 * @returns {PositionObject}
 * @throws {Error} 入参不可解释时抛出。
 */
export function normalizePosition(position, fallbackType = 'Logical') {
    if (Array.isArray(position)) {
        const [x, y] = position;
        return { type: fallbackType, x: requireFinite(x, 'x'), y: requireFinite(y, 'y') };
    }

    if (position && typeof position === 'object') {
        const candidate = /** @type {Partial<PositionObject>} */ (position);
        const type = candidate.type === 'Physical' ? 'Physical'
            : candidate.type === 'Logical' ? 'Logical'
            : fallbackType;
        return {
            type,
            x: requireFinite(candidate.x, 'x'),
            y: requireFinite(candidate.y, 'y'),
        };
    }

    throw new Error('normalizePosition: 需要 {x, y} 形式的对象（或 [x, y]）');
}

/**
 * 校验是有限数字。
 *
 * 为什么不允许 NaN/Infinity：它们传到宿主会被序列化成 `null`，
 * 而宿主侧的"参数缺失"与"参数是 0"看起来一样——症状是窗口莫名变成 0x0 或跳到左上角。
 *
 * @param {unknown} value
 * @param {string} field
 * @returns {number}
 */
function requireFinite(value, field) {
    if (typeof value !== 'number' || !Number.isFinite(value)) {
        throw new Error(`尺寸/位置字段 '${field}' 必须是有限数字（收到 ${String(value)}）`);
    }

    return value;
}

/**
 * 逻辑尺寸 → 物理尺寸。
 *
 * @param {number} logical 逻辑像素。
 * @param {number} scaleFactor 缩放因子（1 = 100%）。
 * @returns {number} 物理像素（四舍五入到整像素，与宿主侧 LogicalPixels.ToPhysical 一致）。
 */
export function logicalToPhysical(logical, scaleFactor) {
    requireFinite(logical, 'logical');
    requireFinite(scaleFactor, 'scaleFactor');
    if (scaleFactor <= 0) {
        throw new Error(`scaleFactor 必须是正数（收到 ${scaleFactor}）`);
    }

    return roundToEven(logical * scaleFactor);
}

/**
 * 物理尺寸 → 逻辑尺寸。
 *
 * @param {number} physical 物理像素。
 * @param {number} scaleFactor 缩放因子（1 = 100%）。
 * @returns {number} 逻辑像素（保留小数：除以缩放比通常不是整数，取整会逐次累积偏差）。
 */
export function physicalToLogical(physical, scaleFactor) {
    requireFinite(physical, 'physical');
    requireFinite(scaleFactor, 'scaleFactor');
    if (scaleFactor <= 0) {
        throw new Error(`scaleFactor 必须是正数（收到 ${scaleFactor}）`);
    }

    return physical / scaleFactor;
}

/**
 * 四舍五入到整像素，**.5 时取偶数**（银行家舍入）。
 *
 * 为什么不直接用 `Math.round`：宿主侧是 .NET 的 `Math.Round(logical * dpi / 96)`，
 * 它默认 `MidpointRounding.ToEven`——`Math.Round(4.5)` 给 **4**，而 JS 的
 * `Math.round(4.5)` 给 **5**。两边不一致的后果是"同一份前端代码在 150% 屏上
 * 比宿主算的尺寸大 1 像素"，而这种偏差只在恰好落在 .5 的输入上出现，
 * 靠人眼看不出、靠随机测试也撞不到。
 *
 * 复刻规则（与 .NET 一致）：先取 `Math.floor`，若小数部分恰为 0.5，
 * 则"取偶"——即向下取整的结果为偶数时保持，奇数时进一。
 *
 * @param {number} value
 * @returns {number}
 */
function roundToEven(value) {
    const floored = Math.floor(value);
    const fraction = value - floored;

    if (fraction > 0.5) {
        return floored + 1;
    }

    if (fraction < 0.5) {
        return floored;
    }

    // 恰为 .5：取偶。floored 是偶数则留它，否则进一。
    return floored % 2 === 0 ? floored : floored + 1;
}
