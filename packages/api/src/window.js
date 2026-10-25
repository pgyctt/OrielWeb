// 对标 `@tauri-apps/api/window`。
//
// 与 Tauri 的差别（如实标注）：
//   * **命令名是我们自己的**（`window.setTitle` 等），而 Tauri 是 `plugin:window|set_title`。
//     调用方不该看到这个差别——类的方法名与 Tauri 一一对应，映射在包内完成。
//   * `getCurrentWindow()` 是**同步**的（Tauri 也是）；`getAllWindows()` / `getByLabel()` 是
//     **异步**的（要问宿主有哪些窗口）。
//   * **本模块的方法都需要过能力门禁**：只有标题栏必需的那组（minimize / toggleMaximize /
//     close / destroy / drag* / toggleFullscreen / toggleOnTop / pickFile / contextMenu）
//     是无条件放行的，其余要应用显式 `Allow("window.*")` 或逐条声明。
//     Tauri 那边由 capability 文件管同一件事，这里的粒度更粗但方向一致。
//   * `title()` / `isDecorated()` 这类**只读查询**本库还没有宿主命令，暂时不提供，
//     而不是返回一个假值——返回假值的后果是页面按错误信息渲染。

import { invoke } from './core.js';
import { getTransport, refCloseIntercept, unrefCloseIntercept } from './internal.js';
import { listen, once, emit, emitTo, TauriEvent } from './event.js';
// 图像字节的共享工具：与 `image.js` 共用同一份，免得两边对"越界字节要不要拦"分叉。
import { toImageBytes, bytesToBase64, assertImageFormat } from './image-bytes.js';
import {
    LogicalSize,
    LogicalPosition,
    PhysicalSize,
    PhysicalPosition,
    normalizeSize,
    normalizePosition,
    physicalToLogical,
} from './dpi.js';

// **把 dpi 的四个类转出去**（对标 Tauri 的 window 模块）。
//
// Tauri 的 `@tauri-apps/api/window` 会 re-export 它们，所以页面里写
// `import { LogicalSize } from '@tauri-apps/api/window'` 是能跑的。
// 少了这一行，那种写法在迁移后会直接报"没有这个导出"——
// 它看起来像"包不完整"，实际只差一次转出。
//
// 这条是 `tauri-alignment` 测试机械比对出来的（见那个文件）：
// 我在 API.md 里写了"dpi 横跨 window 与 dpi 两处"，但没有真的把它转出来。
export { LogicalSize, LogicalPosition, PhysicalSize, PhysicalPosition } from './dpi.js';

/**
 * 光标形状（对标 Tauri 的 `CursorIcon`）。
 *
 * @remarks 取值是 CSS `cursor` 关键字的 camelCase 形式。**三平台各自只能给出最接近的形状**：
 * Windows 没有 `copy`、macOS 没有对角线 resize、GTK 取决于光标主题。完整的降级表见 docs/API.md。
 *
 * @typedef {'default' | 'crosshair' | 'hand' | 'arrow' | 'move' | 'text' | 'wait' | 'help'
 *   | 'progress' | 'notAllowed' | 'contextMenu' | 'cell' | 'verticalText' | 'alias' | 'copy'
 *   | 'noDrop' | 'grab' | 'grabbing' | 'allScroll' | 'zoomIn' | 'zoomOut' | 'eResize'
 *   | 'nResize' | 'neResize' | 'nwResize' | 'sResize' | 'seResize' | 'swResize' | 'wResize'
 *   | 'ewResize' | 'nsResize' | 'neswResize' | 'nwseResize' | 'colResize' | 'rowResize'} CursorIcon
 */

/**
 * 允许的光标形状。
 *
 * @type {ReadonlySet<string>}
 */
const CURSOR_ICONS = new Set([
    'default', 'crosshair', 'hand', 'arrow', 'move', 'text', 'wait', 'help', 'progress',
    'notAllowed', 'contextMenu', 'cell', 'verticalText', 'alias', 'copy', 'noDrop', 'grab',
    'grabbing', 'allScroll', 'zoomIn', 'zoomOut', 'eResize', 'nResize', 'neResize', 'nwResize',
    'sResize', 'seResize', 'swResize', 'wResize', 'ewResize', 'nsResize', 'neswResize',
    'nwseResize', 'colResize', 'rowResize',
]);

/**
 * resize 拖动的起始边/角（对标 Tauri 的 `ResizeDirection`）。
 *
 * @typedef {'East' | 'North' | 'NorthEast' | 'NorthWest' | 'South' | 'SouthEast'
 *   | 'SouthWest' | 'West'} ResizeDirection
 */

/** @type {ReadonlySet<string>} */
const RESIZE_DIRECTIONS = new Set([
    'East', 'North', 'NorthEast', 'NorthWest', 'South', 'SouthEast', 'SouthWest', 'West',
]);

/**
 * 标题栏样式（对标 Tauri 的 `TitleBarStyle`）。
 *
 * @remarks **仅 macOS 生效**：`overlay` 会把页面延伸到标题栏下方、只留红绿灯。
 * Windows/Linux 上 `setTitleBarStyle` 抛 `not_supported`。
 *
 * @typedef {'visible' | 'transparent' | 'overlay'} TitleBarStyle
 */

/**
 * `setEffects` 的参数（对标 Tauri 的 `Effects`）。
 *
 * @typedef {object} Effects
 * @property {string[]} effects 材质列表（`Effect` 的取值）。**第一个本平台支持的生效**，其余忽略。
 * @property {string} [state] 激活状态（`EffectState` 的取值）。**仅 macOS**；Windows 上被忽略（会记诊断）。
 * @property {number} [radius] 材质视图的圆角。**仅 macOS**。
 * @property {[number, number, number] | [number, number, number, number]
 *   | { red: number, green: number, blue: number, alpha?: number }} [color]
 *   特效色调。**Windows** 只影响 `blur`/`acrylic`（Win10 1903+）；macOS 只影响液态玻璃（本库未实现）。
 * @property {boolean} [interactive] 交互式玻璃（macOS 26+）。本库未实现，接受但忽略。
 */

/**
 * `setProgressBar` 的参数（对标 Tauri 的 `ProgressBarState`）。
 *
 * @typedef {object} ProgressBarState
 * @property {string} [status] `ProgressBarStatus` 的取值。
 * @property {number} [progress] 0–100 的百分比。
 */

/** @type {ReadonlySet<string>} */
const TITLE_BAR_STYLES = new Set(['visible', 'transparent', 'overlay']);

/** @type {ReadonlySet<string>} */
const EFFECTS = new Set([
    'appearanceBased', 'light', 'dark', 'mediumLight', 'ultraDark', 'titlebar', 'selection',
    'menu', 'popover', 'sidebar', 'headerView', 'sheet', 'windowBackground', 'hudWindow',
    'fullScreenUI', 'tooltip', 'contentBackground', 'underWindowBackground',
    'underPageBackground', 'liquidGlassRegular', 'liquidGlassClear', 'mica', 'blur',
    'acrylic', 'tabbed', 'tabbedDark', 'tabbedLight',
]);

/** @type {ReadonlySet<string>} */
const EFFECT_STATES = new Set(['followsWindowActiveState', 'active', 'inactive']);

/** @type {ReadonlySet<string>} */
const PROGRESS_STATUSES = new Set(['none', 'normal', 'indeterminate', 'paused', 'error']);

/**
 * 把宿主返回的尺寸对象包成 {@link LogicalSize} 实例。
 *
 * @param {unknown} raw
 * @returns {import('./dpi.js').LogicalSize}
 * @remarks
 * 宿主给的是 `{ type, width, height }` 的普通对象；包成类实例是为了让 `instanceof LogicalSize`
 * 成立、并能原样喂回 `setSize()`（那里按 `type` 分派）。两者形状本就一致，所以这里只做包装不回读校验——
 * 真拿到畸形值会在调用点立刻炸，比在这里静默补默认值好。
 *
 * 类型位置写 `import('./dpi.js').LogicalSize` 而不是裸 `LogicalSize`：本模块末尾有
 * `export { LogicalSize } from './dpi.js'`（对标 Tauri 的转出），声明发射器会认定该名字
 * 已经"在模块里"而**不再补 import**，产出的 `window.d.ts` 于是引用了一个未定义的名字
 * （消费者 `skipLibCheck: false` 时报 TS2304）。包内 `typecheck` 用的是 `skipLibCheck: true`，
 * 所以这个洞在发 npm 之前一直没被看见——是打包后装进干净目录才验出来的。
 */
function toLogicalSize(raw) {
    const value = /** @type {{ width?: number, height?: number }} */ (raw ?? {});
    return new LogicalSize(Number(value.width ?? 0), Number(value.height ?? 0));
}

/**
 * 把宿主返回的位置对象包成 {@link LogicalPosition} 实例；理由同 {@link toLogicalSize}。
 *
 * @param {unknown} raw
 * @returns {import('./dpi.js').LogicalPosition}
 */
function toLogicalPosition(raw) {
    const value = /** @type {{ x?: number, y?: number }} */ (raw ?? {});
    return new LogicalPosition(Number(value.x ?? 0), Number(value.y ?? 0));
}

/** 本窗口的 label（宿主注入，见 OrielWindowOptions.Label）。 */
function currentLabel() {
    const label = getTransport().label;
    if (typeof label !== 'string' || label === '') {
        throw new Error(
            'getCurrentWindow: 当前传输层没有 label。它由宿主注入（0.3.6+）；' +
            '在 Node 里跑请用 mocks 的 mockWindows 或直接构造带 label 的传输层。');
    }

    return label;
}

/**
 * 取目标窗口的 label：不传就是本窗口。
 * @param {string | Window | undefined} target
 * @returns {string}
 */
function labelOf(target) {
    if (target === undefined || target === null) {
        return currentLabel();
    }

    if (typeof target === 'string') {
        return target;
    }

    if (typeof target.label === 'string' && target.label !== '') {
        return target.label;
    }

    throw new Error('需要一个窗口 label（字符串）或 Window 实例');
}

/**
 * 判断是不是 `@orielweb/api/image` 的 `Image` 实例。
 *
 * @param {any} value
 * @returns {boolean}
 * @remarks
 * **鸭子类型而不是 `instanceof Image`**：`instanceof` 在"同一份包被加载了两次"时
 * 会给出 `false`（这是双入口包的经典问题——比如页面从两个路径各 import 了一次）。
 * 而这里要问的只是"它有没有 Image 该有的东西"，用形状判断既够用又更稳。
 * 判据取 `bytes`（Uint8Array）+ `isOrielImage` 标记，见 image.js。
 */
function isImageLike(value) {
    return value !== null
        && typeof value === 'object'
        && value.isOrielImage === true
        && value.bytes instanceof Uint8Array;
}

/**
 * 把 Tauri 的 `Color` 归一成宿主认的 `0xAARRGGBB`。
 *
 * 接受四种形状（与 Tauri 的类型定义一致）：
 * - `'#rgb'` / `'#rgba'` / `'#rrggbb'` / `'#rrggbbaa'`（也接受不带 `#` 的同样写法）
 * - `[r, g, b]`（此时 alpha 按 255）
 * - `[r, g, b, a]`
 * - `{ red, green, blue, alpha }`（alpha 缺省按 255）
 *
 * @param {any} color
 * @returns {number} 0xAARRGGBB 的无符号整数
 * @remarks
 * 这里**只在页面侧做一次**归一：宿主只认整数，于是"hex 记错一位""数组给三个还是四个"
 * 这类错误全在这一层被挡住并给出可排查的文案，而不是变成一个颜色不对但也不报错的窗口。
 */
function parseColorToArgb(color) {
    /**
     * @param {number} r
     * @param {number} g
     * @param {number} b
     * @param {number} a
     * @returns {number}
     */
    const fromBytes = (r, g, b, a) =>
        ((((a & 0xFF) << 24) | ((r & 0xFF) << 16) | ((g & 0xFF) << 8) | (b & 0xFF)) >>> 0);

    /**
     * @param {any} value
     * @param {string} what
     * @returns {number}
     */
    const checkByte = (value, what) => {
        if (typeof value !== 'number' || !Number.isInteger(value) || value < 0 || value > 255) {
            throw new TypeError(`setBackgroundColor: ${what} 必须是 0..255 的整数（收到 ${String(value)}）`);
        }
        return value;
    };

    if (typeof color === 'string') {
        // 短写与长写都收：Tauri 的类型是泛指的字符串，而 `#fff` 在 CSS 里太常见，
        // 直接拒掉会让从 CSS 抄过来的颜色值莫名其妙地失败。
        const hex = color.startsWith('#') ? color.slice(1) : color;
        if (!/^[0-9a-fA-F]+$/.test(hex) || ![3, 4, 6, 8].includes(hex.length)) {
            throw new TypeError(
                `setBackgroundColor: 认不出这个颜色字符串「${color}」——` +
                '支持 #rgb / #rgba / #rrggbb / #rrggbbaa。');
        }

        /** @param {string} part */
        const expand = (part) => parseInt(hex.length <= 4 ? part + part : part, 16);
        const step = hex.length <= 4 ? 1 : 2;

        const r = expand(hex.slice(0, step));
        const g = expand(hex.slice(step, step * 2));
        const b = expand(hex.slice(step * 2, step * 3));
        // 没写 alpha 就是全不透明——写成 0 会让窗口整个透明，那是"看着像没生效"的经典坑
        const a = hex.length === 4 || hex.length === 8 ? expand(hex.slice(step * 3, step * 4)) : 255;

        return fromBytes(r, g, b, a);
    }

    if (Array.isArray(color)) {
        if (color.length !== 3 && color.length !== 4) {
            throw new TypeError(
                `setBackgroundColor: 颜色数组要给 3 个（RGB）或 4 个（RGBA）分量，收到 ${color.length} 个`);
        }

        return fromBytes(
            checkByte(color[0], '红'),
            checkByte(color[1], '绿'),
            checkByte(color[2], '蓝'),
            color.length === 4 ? checkByte(color[3], 'alpha') : 255);
    }

    if (color !== null && typeof color === 'object') {
        return fromBytes(
            checkByte(color.red, 'red'),
            checkByte(color.green, 'green'),
            checkByte(color.blue, 'blue'),
            color.alpha === undefined ? 255 : checkByte(color.alpha, 'alpha'));
    }

    throw new TypeError(
        'setBackgroundColor: color 必须是 hex 字符串、[r,g,b] / [r,g,b,a] 数组，' +
        `或 { red, green, blue, alpha } 对象（收到 ${typeof color}）`);
}

/**
 * 把任意错误值取成可读文案。
 *
 * @param {unknown} error
 * @returns {string}
 * @remarks
 * 不直接 `error.message`：抛出的可能是字符串、对象或任意值，而 TypeScript 也会
 * 正确拒绝在 `unknown` 上取属性。诊断文案里出现 `undefined` 是最没用的那一种。
 */
function errorText(error) {
    if (error instanceof Error) {
        return error.message;
    }

    if (typeof error === 'string') {
        return error;
    }

    try {
        return JSON.stringify(error) ?? String(error);
    } catch {
        return String(error);
    }
}

/**
 * 求缩放比（用于把 PhysicalSize 换算成逻辑尺寸）。
 *
 * @param {string} label
 * @returns {Promise<number>}
 */
async function scaleFactorOf(label) {
    const factor = await invoke('window.scaleFactor', { label });

    // 宿主给不出（0 / 非数字）时按 1 算：那相当于"不做换算"，
    // 比让整条 setSize 抛异常好——而缩放比异常本身应当由 E2 的自检去抓。
    return typeof factor === 'number' && factor > 0 ? factor : 1;
}

/**
 * 一个窗口的句柄（对标 Tauri 2 的 `Window`）。
 *
 * @remarks
 * 它是**轻量句柄**而不是状态容器：每个方法都现算 label、现发命令。
 * 原因是窗口可能被别的路径关掉——缓存状态会与真实情况脱节，
 * 而脱节的表现是"对一个已关闭的窗口调用方法却没有报错"。
 */

/**
 * 请求用户注意时的紧急程度（对标 Tauri 的 `UserAttentionType`）。
 *
 * @remarks
 * **取值与 Tauri 完全一致**（`Critical = 1`、`Informational = 2`），
 * 而且带上反向映射（`UserAttentionType[1] === 'Critical'`）——
 * 迁移代码里可能出现任一方向，两种写法都要能对得上。
 */
export const UserAttentionType = Object.freeze({
    1: 'Critical',
    2: 'Informational',
    Critical: 1,
    Informational: 2,
});

/**
 * 任务栏/Dock 进度条状态（对标 Tauri 的 `ProgressBarStatus`）。
 *
 * 取值与 Tauri 逐键一致。**方法本身在 B22 落地**（`Window.setProgressBar`）——
 * 常量先到位，迁移代码里的 `ProgressBarStatus.Indeterminate` 不必改。
 */
export const ProgressBarStatus = Object.freeze({
    None: 'none',
    Normal: 'normal',
    Indeterminate: 'indeterminate',
    Paused: 'paused',
    Error: 'error',
});

/**
 * 窗口视觉材质（对标 Tauri 的 `Effect`，macOS vibrancy / Windows Mica 系）。
 *
 * 取值与 Tauri 逐键一致（27 个）。**方法本身在 B22 落地**（`Window.setEffects`）；
 * 不支持的平台由方法抛 `not_supported`——常量本身是跨平台的字符串。
 */
export const Effect = Object.freeze({
    AppearanceBased: 'appearanceBased',
    Light: 'light',
    Dark: 'dark',
    MediumLight: 'mediumLight',
    UltraDark: 'ultraDark',
    Titlebar: 'titlebar',
    Selection: 'selection',
    Menu: 'menu',
    Popover: 'popover',
    Sidebar: 'sidebar',
    HeaderView: 'headerView',
    Sheet: 'sheet',
    WindowBackground: 'windowBackground',
    HudWindow: 'hudWindow',
    FullScreenUI: 'fullScreenUI',
    Tooltip: 'tooltip',
    ContentBackground: 'contentBackground',
    UnderWindowBackground: 'underWindowBackground',
    UnderPageBackground: 'underPageBackground',
    LiquidGlassRegular: 'liquidGlassRegular',
    LiquidGlassClear: 'liquidGlassClear',
    Mica: 'mica',
    Blur: 'blur',
    Acrylic: 'acrylic',
    Tabbed: 'tabbed',
    TabbedDark: 'tabbedDark',
    TabbedLight: 'tabbedLight',
});

/**
 * 特效的激活状态（对标 Tauri 的 `EffectState`）。
 */
export const EffectState = Object.freeze({
    FollowsWindowActiveState: 'followsWindowActiveState',
    Active: 'active',
    Inactive: 'inactive',
});

/**
 * 关闭请求事件（对标 Tauri 的 `CloseRequestedEvent`）。
 *
 * **语义与 Tauri 一致**：`preventDefault()` 真的会阻止关闭。不调用它时，
 * `onCloseRequested` 的包装层会在 handler 跑完后调 `destroy()`——
 * 所以"要不要关"由这个调用决定，而不是"什么都不做"。
 *
 * ```js
 * await win.onCloseRequested(async (event) => {
 *   if (!(await confirmDiscard())) event.preventDefault();
 * });
 * ```
 */
export class CloseRequestedEvent {
    /**
     * @param {{ event: string, id: number, payload?: unknown }} event 事件原样（`onCloseRequested` 的回调参数）。
     */
    constructor(event) {
        /** 事件名。 */
        this.event = event.event;
        /** 事件 id（退订用）。 */
        this.id = event.id;
        /** 事件负载（Tauri 的类不暴露它，本库保留——纯加法）。 */
        this.payload = event.payload;
        this._preventDefault = false;
    }

    /** 阻止窗口关闭（阻止的是 `onCloseRequested` 包装层的默认 `destroy()`）。 */
    preventDefault() {
        this._preventDefault = true;
    }

    /** 是否调用过 {@link CloseRequestedEvent.preventDefault}。 */
    isPreventDefault() {
        return this._preventDefault;
    }
}

export class Window {


    /**
     * @param {string} label
     */
    constructor(label) {
        if (typeof label !== 'string' || label === '') {
            throw new Error('Window: label 必须是非空字符串');
        }

        /** @type {string} */
        this.label = label;
    }

    /**
     * 有焦点的窗口（对标 Tauri 的静态方法 `Window.getFocusedWindow()`）。
     *
     * @returns {Promise<Window | null>} 没有窗口在前台时 `null`。
     * @remarks
     * 与模块级 `getFocusedWindow()` 是同一个实现（那边是函数、这里是静态方法，
     * Tauri 两种写法都提供，迁移代码两种都可能出现）。
     */
    static getFocusedWindow() {
        return getFocusedWindow();
    }

    // ---- 显隐与状态 ----

    /** 显示窗口。需要 `window.show` 被允许。 */
    show() {
        return invoke('window.show', { label: this.label });
    }

    /** 隐藏窗口。需要 `window.hide` 被允许。 */
    hide() {
        return invoke('window.hide', { label: this.label });
    }

    /** 关闭窗口（可被 `Closing` 与页面接管拦下）。**无条件放行**。 */
    close() {
        return invoke('window.close', { label: this.label });
    }

    /** 强制销毁窗口（不被任何东西拦下）。**无条件放行**。 */
    destroy() {
        return invoke('window.destroy', { label: this.label });
    }

    /** 聚焦窗口。需要 `window.focus` 被允许。 */
    setFocus() {
        return invoke('window.focus', { label: this.label });
    }

    /** 最小化。**无条件放行**。 */
    minimize() {
        return invoke('window.minimize', { label: this.label });
    }

    /** 最大化。需要 `window.maximize` 被允许。 */
    maximize() {
        return invoke('window.maximize', { label: this.label });
    }

    /** 还原（对标 Tauri 的 `unmaximize`）。需要 `window.restore` 被允许。 */
    unmaximize() {
        return invoke('window.restore', { label: this.label });
    }

    /** 最大化/还原切换，返回切换后是否最大化。**无条件放行**。 */
    toggleMaximize() {
        return invoke('window.toggleMaximize', { label: this.label });
    }

    /** 当前是否最大化。**无条件放行**（只读查询）。 */
    isMaximized() {
        return invoke('window.isMaximized', { label: this.label });
    }

    /** 当前是否显示中（最小化仍算显示中）。**无条件放行**（只读查询）。 */
    isVisible() {
        return invoke('window.isVisible', { label: this.label });
    }

    /** 窗口所在显示器的缩放比（1.0 = 100%）。需要 `window.scaleFactor` 被允许。 */
    scaleFactor() {
        return invoke('window.scaleFactor', { label: this.label });
    }

    // ---- 只读状态查询（对标 Tauri 的同名方法）----
    //
    // 这四条**无条件放行**（在标题栏白名单里）：自绘标题栏要用它们渲染按钮的当前状态
    // 与"窗口是否激活"的配色，而它们只报告自己窗口的状态、不泄露跨窗口信息。

    /**
     * 是否最小化。
     *
     * @returns {Promise<boolean>}
     * @remarks **与 `isVisible()` 是两件事**：最小化的窗口仍然"可见"，
     * 它只是被收进了任务栏。混为一谈的后果是"最小化之后页面以为窗口被藏了"。
     */
    isMinimized() {
        return invoke('window.isMinimized', { label: this.label });
    }

    /**
     * 是否拥有键盘焦点。
     *
     * @returns {Promise<boolean>}
     * @remarks 按**窗口**判而不是按应用判：多窗口应用里只有一个是 key window。
     * 标题栏常用它渲染激活/非激活两套配色。
     */
    isFocused() {
        return invoke('window.isFocused', { label: this.label });
    }

    /**
     * 是否全屏。
     *
     * @returns {Promise<boolean>}
     * @remarks 取的是宿主 `toggleFullscreen()` 记住的状态，不是比对窗口与显示器矩形——
     * 后者在用户手动把窗口拖成刚好铺满屏幕时会给出与库不一致的答案。
     */
    isFullscreen() {
        return invoke('window.isFullscreen', { label: this.label });
    }

    /**
     * 是否置顶。
     *
     * @returns {Promise<boolean>}
     * @remarks 同上：取宿主记住的状态。
     */
    isAlwaysOnTop() {
        return invoke('window.isAlwaysOnTop', { label: this.label });
    }

    /**
     * 是否有系统装饰（标题栏与边框）。
     *
     * @returns {Promise<boolean>}
     * @remarks 取创建时的选项，窗口生命期内不变。无边框窗口多用它决定"要不要渲染自绘标题栏"。
     */
    isDecorated() {
        return invoke('window.isDecorated', { label: this.label });
    }

    /**
     * 是否可缩放。
     *
     * @returns {Promise<boolean>}
     * @remarks 从**原生实际状态**读回（`setResizable` 改的就是它），
     * 所以反映的是"系统接受的是什么"。
     */
    isResizable() {
        return invoke('window.isResizable', { label: this.label });
    }

    /**
     * 是否可最大化。
     *
     * @returns {Promise<boolean>}
     * @remarks **Linux 与 macOS 上等于 `isResizable()`**：那两个平台没有独立的
     * "可最大化"概念（GTK 跟着 resizable 走、AppKit 的 zoom 以 resizable 为前提）。
     * Windows 上才是独立的样式位。
     */
    isMaximizable() {
        return invoke('window.isMaximizable', { label: this.label });
    }

    /**
     * 是否可最小化。
     *
     * @returns {Promise<boolean>}
     * @remarks 同 `isMaximizable()`：Linux 上回落到 `isResizable()`。
     */
    isMinimizable() {
        return invoke('window.isMinimizable', { label: this.label });
    }

    /**
     * 是否可关闭。
     *
     * @returns {Promise<boolean>}
     * @remarks **Linux 上恒为 `true`**：GTK 没有这个概念（窗口总能收到 delete-event，
     * 本库的关闭接管是在那之上做的拦截）。
     */
    isClosable() {
        return invoke('window.isClosable', { label: this.label });
    }

    /**
     * 当前主题（`'light'` 或 `'dark'`）。
     *
     * @returns {Promise<'light' | 'dark'>}
     * @remarks
     * 对标 Tauri 的 `Window.theme()`，但有两点不同：
     * 那边可能返回 `null`（"无法确定"），本库**总是给得出值**——三平台都能读到系统主题，
     * 且三级来源（窗口覆写 > 应用覆写 > 系统）里任一级都能给出确定答案。
     * 覆写模型见 {@link Window.setTheme}。
     */
    theme() {
        return invoke('window.theme', { label: this.label });
    }

    /**
     * 覆写**这个窗口**的主题（对标 Tauri 的 `Window.setTheme`）。
     *
     * @param {'light' | 'dark' | null} [theme] `null` = 跟随系统
     * @returns {Promise<void>}
     * @remarks
     * **三级模型**：窗口覆写 > 应用覆写（`app.setTheme`） > 系统主题。
     * 这一层只改"这个窗口"，不影响别的窗口；应用级的改动走 `app.setTheme`。
     *
     * **平台落点不同（不是实现偷懒，是平台事实）**：
     * - Windows：`DwmSetWindowAttribute`（深色标题栏）+ UxTheme 非客户区重绘。
     *   **只改标题栏 / 边框那圈**，客户区（网页）的颜色由 CSS 决定——页面要
     *   自己按 `theme()` / `theme.changed` 改配色，宿主不会替你改 CSS。
     * - macOS：`NSWindow.appearance`（按窗口），`null` = 还原成系统/应用默认。
     * - Linux：**没有"按窗口选主题"的入口**（GTK 只有应用级的
     *   `gtk-application-prefer-dark-theme`），所以这里落到应用级、作用域比
     *   另两个平台大——这不是本库的缺口，是 GTK 的形状。
     *
     * **需要能力声明**（`window.setTheme`）——它改变窗口外观，不在标题栏白名单里。
     */
    setTheme(theme) {
        if (theme !== undefined && theme !== null && theme !== 'light' && theme !== 'dark') {
            throw new TypeError(`setTheme: theme 必须是 'light' / 'dark' / null，收到 ${JSON.stringify(theme)}`);
        }

        return invoke('window.setTheme', { label: this.label, value: theme ?? null });
    }

    /**
     * 是否接收输入。
     *
     * @returns {Promise<boolean>}
     * @remarks **macOS 上只挡鼠标、不挡键盘**：Cocoa 没有"禁用窗口"这个概念，
     * 那边用 `ignoresMouseEvents` 近似。需要挡键盘的应用要自己处理。
     */
    isEnabled() {
        return invoke('window.isEnabled', { label: this.label });
    }

    /**
     * 启用/禁用窗口的输入。
     *
     * @param {boolean} enabled
     * @returns {Promise<void>}
     * @remarks 需要 `Allow("window.setEnabled")`——它**不在**标题栏白名单里
     * （禁用窗口不是标题栏要做的事）。
     */
    setEnabled(enabled) {
        if (typeof enabled !== 'boolean') {
            throw new TypeError('setEnabled: enabled 必须是布尔值');
        }

        return invoke('window.setEnabled', { label: this.label, value: enabled });
    }

    // ---- 标题栏按钮的可用性（M5-6）----

    /**
     * 设置窗口是否可最大化。
     *
     * @param {boolean} enabled
     * @returns {Promise<void>}
     * @remarks
     * 需要 `Allow("window.setMaximizable")`。
     *
     * **平台差异（两处都会咬人）**：
     * - macOS 上这就是改 `NSWindowStyleMaskResizable`——AppKit 没有独立的最大化位，
     *   所以调它会**连带**影响拖拽缩放，且与 `setResizable` 互相覆盖（后调的赢）；
     * - Linux 上是**空操作**（GTK 无对应入口），`isMaximizable()` 也不会因此改变
     *   ——它继续回落 `isResizable()`，不会撒谎说"已禁止"。
     */
    setMaximizable(enabled) {
        if (typeof enabled !== 'boolean') {
            throw new TypeError('setMaximizable: enabled 必须是布尔值');
        }

        return invoke('window.setMaximizable', { label: this.label, value: enabled });
    }

    /**
     * 设置窗口是否可最小化。
     *
     * @param {boolean} enabled
     * @returns {Promise<void>}
     * @remarks 需要 `Allow("window.setMinimizable")`。Linux 上是空操作，理由同 `setMaximizable`。
     */
    setMinimizable(enabled) {
        if (typeof enabled !== 'boolean') {
            throw new TypeError('setMinimizable: enabled 必须是布尔值');
        }

        return invoke('window.setMinimizable', { label: this.label, value: enabled });
    }

    /**
     * 设置窗口是否可关闭。
     *
     * @param {boolean} enabled
     * @returns {Promise<void>}
     * @remarks
     * 需要 `Allow("window.setClosable")`。
     *
     * **三平台落点不同，而且不是同一件事**：
     * - Windows：灰化系统菜单里的"关闭"项（关闭按钮在 Win32 里属于系统菜单）；
     * - macOS：切 `NSWindowStyleMaskClosable`，红色按钮随之消失/出现；
     * - Linux：**空操作**，`isClosable()` 恒为 `true`。
     *
     * 也就是说这条命令在 Linux 上**不会**让窗口关不掉——真要"关不掉"，用
     * `onCloseRequested` 拦下并 `preventDefault()`：那是应用层策略，还能按条件拒绝。
     */
    setClosable(enabled) {
        if (typeof enabled !== 'boolean') {
            throw new TypeError('setClosable: enabled 必须是布尔值');
        }

        return invoke('window.setClosable', { label: this.label, value: enabled });
    }

    /**
     * 设置窗口图标。
     *
     * @param {import('./image.js').JsImage} icon 路径、PNG/ICO 字节，或 `Image` 实例。
     * @returns {Promise<void>}
     * @remarks
     * 需要 `Allow("window.setIcon")`。
     *
     * 接受 Tauri 的 `JsImage` 全部五种形式：`string`（文件路径）、`Uint8Array` /
     * `ArrayBuffer` / `number[]`（PNG/ICO 字节）、`Image` 实例。
     *
     * **`Image` 在本库是"字节容器"而不是资源表 handle**（见 `@orielweb/api/image` 的说明）——
     * 它只是把字节和尺寸一起拿着，方便在多次调用间复用；传到宿主时仍是字节。
     *
     * **平台差异（两处）**：
     * - macOS 上没有窗口级图标，这里设的是**应用（Dock）图标**，会影响整个应用；
     * - Linux 的 Wayland 下窗口图标由合成器决定，通常忽略本调用。
     */
    setIcon(icon) {
        if (typeof icon === 'string') {
            if (icon === '') {
                throw new TypeError('setIcon: 路径不能是空字符串');
            }

            return invoke('window.setIcon', { label: this.label, path: icon });
        }

        // Image 实例：取它的字节（见 image.js）。这里用鸭子类型而不是 instanceof，
        // 免得"从另一个副本加载的 Image"被判成不认识——那在单测与多入口打包里都发生过。
        if (isImageLike(icon)) {
            const image = /** @type {import('./image.js').Image} */ (icon);

            // Image.new 建的图**没有编码字节**，只有裸 RGBA——而宿主侧的图标工厂本来就吃
            // RGBA（OrielImage 是 RGBA 容器），所以直接送 rgba + 尺寸，
            // **不需要先把 RGBA 编码成 PNG**（本库也没有 PNG 编码器）。
            if (image.hasEncodedBytes === false) {
                return invoke('window.setIcon', {
                    label: this.label,
                    rgba: bytesToBase64(image.rgbaBytes),
                    width: image.width,
                    height: image.height,
                });
            }

            return invoke('window.setIcon', {
                label: this.label,
                bytes: bytesToBase64(image.bytes),
            });
        }

        // Tauri 的 JsImage 里，"字节"有三种写法：Uint8Array / ArrayBuffer / number[]。
        // 它们等价，这里统一成 Uint8Array 再编码。
        const bytes = toImageBytes(icon);
        if (bytes !== null) {
            assertImageFormat(bytes, 'setIcon');
            return invoke('window.setIcon', { label: this.label, bytes: bytesToBase64(bytes) });
        }

        throw new TypeError(
            'setIcon: icon 必须是文件路径（string）、Uint8Array / ArrayBuffer / number[] 形式的' +
            'PNG/ICO 字节，或 @orielweb/api/image 的 Image 实例。');
    }

    /**
     * 设置窗口底色。
     *
     * @param {string | [number, number, number] | [number, number, number, number] | { red: number, green: number, blue: number, alpha?: number }} color
     *   Tauri 的 `Color`：hex 字符串、RGB(A) 数组或对象。
     * @returns {Promise<void>}
     * @remarks
     * 需要 `Allow("window.setBackgroundColor")`。
     *
     * 这是**页面透明、或内核还没加载完时露出来**的颜色（Tauri 的文档也是这么说的）。
     * 三平台都落到 webview 自己的底色：Windows `DefaultBackgroundColor`、
     * macOS `underPageBackgroundColor`、Linux `webkit_web_view_set_background_color`。
     *
     * 四种输入形状都在这里归一成宿主认的 `0xAARRGGBB` 整数——
     * 放在这一层是因为它能在单测里穷举（宿主那边只能靠真机试）。
     */
    setBackgroundColor(color) {
        return invoke('window.setBackgroundColor', {
            label: this.label,
            argb: parseColorToArgb(color),
        });
    }

    // ---- 尺寸约束（M5-6）----

    /**
     * 设置窗口的最大尺寸。
     *
     * @param {import('./dpi.js').SizeObject | {width: number, height: number} | [number, number] | null} size
     *   与 `setSize` 收同样的形状；传 `null` 表示**取消**上限。
     * @returns {Promise<void>}
     * @remarks 需要 `Allow("window.setMaxSize")`。
     */
    setMaxSize(size) {
        if (size === null || size === undefined) {
            return invoke('window.setMaxSize', { label: this.label, value: { width: 0, height: 0 } });
        }

        const normalized = normalizeSize(size);

        return invoke('window.setMaxSize', {
            label: this.label,
            value: { width: normalized.width, height: normalized.height },
        });
    }

    /**
     * 一次设置尺寸约束的上下限（对标 Tauri 的 `Window.setSizeConstraints()`）。
     *
     * @param {{minWidth?: number|null, minHeight?: number|null, maxWidth?: number|null, maxHeight?: number|null}|null} constraints
     *   **整体替换**语义：省略或给 `null` 的项表示**取消那一项的约束**（不是"保持不动"）。
     *   传 `null` 表示取消全部约束。
     * @returns {Promise<void>}
     * @remarks
     * 需要 `Allow("window.setMinSize")` 与/或 `Allow("window.setMaxSize")`——
     * 这条在页面上**分派**到那两条命令，宿主没有"组合命令"。
     *
     * **为什么是整体替换而不是"省略即不动"**：后者要求知道"当前约束是什么"，
     * 而本库的 `Window` 是**轻量句柄**（见类的说明）——它不缓存窗口状态，
     * 因为窗口可能被别的路径改掉，缓存会与真实情况脱节。要正确实现"保持不动"
     * 只有两条路：① 包内缓存（与轻量句柄的设计冲突，且缓存会过期）；
     * ② 新增一条"读取当前约束"的宿主命令——而 Tauri 没有这个 API，加它就是
     * 偷偷发明 API（会被 `tauri-alignment.test.mjs` 的 `EXTRAS` 检查拦住）。
     *
     * 与 Tauri 一致：它的 `setSizeConstraints` 也是把四个字段整体发下去，
     * 没给的字段就是不约束。所以 `{ minHeight: 150 }` 会**同时**取消宽度约束。
     * 要只改一侧，就把另一侧的现值一起传进来。
     */
    async setSizeConstraints(constraints = {}) {
        // null 表示"取消全部约束"：两侧都按 0 下发（0 = 不限制）
        if (constraints === null || constraints === undefined) {
            constraints = {};
        }

        const min = [constraints.minWidth, constraints.minHeight];
        const max = [constraints.maxWidth, constraints.maxHeight];

        // 整侧都没给时仍然要发——"取消那一侧的全部约束"是这次调用的语义，
        // 跳过不发会让"我传了 {} 想清掉约束"变成一个静默的空操作。
        await invoke('window.setMinSize', {
            label: this.label,
            width: min[0] ?? 0,
            height: min[1] ?? 0,
        });

        await invoke('window.setMaxSize', {
            label: this.label,
            value: { width: max[0] ?? 0, height: max[1] ?? 0 },
        });
    }

    // ---- 请求用户注意（M5-6）----

    /**
     * 请求用户注意（任务栏闪烁 / Dock 弹跳）。
     *
     * @param {number | null} [requestType] `UserAttentionType.Critical` /
     *   `UserAttentionType.Informational`；**不传或给 `null` 表示取消**已发出的请求。
     * @returns {Promise<void>}
     * @throws {TypeError} 给了既不是两个常量也不是 `null`/`undefined` 的值。
     * @remarks **macOS 上两档的区别最明显**（Critical 会一直弹跳到用户来看）；
     * **Linux 上两档没有区别**（GTK 只有开/关）。
     *
     * **未知值当场抛而不是静默当成取消**：`requestUserAttention(3)` 若被当成 `null`，
     * 调用方以为"我请求了注意"，实际是"我取消了注意"——两个相反的意图，
     * 而失败现场什么都不会发生（本来就不该有可见反馈）。这类静默的反向执行
     * 比抛错难查得多。
     */
    requestUserAttention(requestType = null) {
        let type = null;

        if (requestType === UserAttentionType.Critical) {
            type = 'critical';
        } else if (requestType === UserAttentionType.Informational) {
            type = 'informational';
        } else if (requestType !== null && requestType !== undefined) {
            throw new TypeError(
                `requestUserAttention: 未知的 requestType「${String(requestType)}」——` +
                '只认 UserAttentionType.Critical（1）/ Informational（2），' +
                '不传或传 null 表示取消。');
        }

        return invoke('window.requestUserAttention', { label: this.label, type });
    }

    // ---- 事件（对标 Tauri 的 Window.listen / once / emit / emitTo）----
    //
    // 纯委托到 `event` 模块：那边已经做了 Tauri 事件名 → 宿主事件名的映射、
    // 以及"订阅高频事件时自动打开宿主转发"。在这一层重新实现一份没有意义。
    //
    // Tauri 的 `Window.listen` 对内置事件（`tauri://resize` 等）走一条本地的快路径，
    // 其余走全局事件系统；本库**统一走全局那条**——因为库里的内置事件本来就是
    // 宿主推给页面的同一条通道，分成两条只会让"为什么这个事件收得到那个收不到"变得难查。

    /**
     * 订阅一个事件。与模块级 `listen` 等价。
     *
     * @param {string} event 事件名（Tauri 名或宿主名都行，见 `event` 模块的映射表）。
     * @param {(event: { event: string, id: number, payload: any }) => void} handler
     * @returns {(() => void) & PromiseLike<() => void>} 退订函数；它同时是 thenable，
     *   所以 `await win.listen(...)` 与 `win.listen(...).then(...)` 都成立（见 `event` 模块）。
     */
    listen(event, handler) {
        return listen(event, handler);
    }

    /**
     * 订阅一次；收到第一条后自动退订。
     *
     * @param {string} event
     * @param {(event: { event: string, id: number, payload: any }) => void} handler
     * @returns {(() => void) & PromiseLike<() => void>} 退订函数（在触发前调用可以取消；同样 thenable）。
     */
    once(event, handler) {
        return once(event, handler);
    }

    /**
     * 广播一个事件给**所有**窗口。
     *
     * @param {string} event
     * @param {unknown} [payload]
     * @returns {Promise<void>}
     * @remarks 需要宿主 `Allow("event.emit")`——那是唯一一条**不在**任何白名单里的内建命令
     * （它让一个窗口的页面能驱动别的窗口，而自绘标题栏不依赖它）。见 API.md §4。
     */
    emit(event, payload) {
        return emit(event, payload);
    }

    /**
     * 把事件发给指定的那个窗口。
     *
     * @param {string} target 目标窗口的 label。
     * @param {string} event
     * @param {unknown} [payload]
     * @returns {Promise<void>}
     * @remarks 同样需要 `Allow("event.emit")`。
     */
    emitTo(target, event, payload) {
        return emitTo(target, event, payload);
    }

    /**
     * 把一个最小化的窗口恢复到正常状态（对标 Tauri 的 `Window.unminimize()`）。
     *
     * @returns {Promise<void>}
     * @remarks
     * 走宿主的 `window.restore`：三平台的"还原"本来就是**同一个原生动作**
     * （Windows `SW_RESTORE`、GTK `gtk_window_deiconify` + `unmaximize`、
     * Cocoa `deminiaturize:`）——Tauri 把 `restore` 与 `unminimize` 分成两条命令，
     * 而 Win32 的 `SW_RESTORE` 一个调用就同时解除最小化与最大化。
     * 这里如实映射到同一个动作，而不是假装它们是两件事。
     */
    unminimize() {
        return invoke('window.restore', { label: this.label });
    }

    /**
     * 当前标题（对标 Tauri 的 `title()`）。**无条件放行**（只读查询）。
     *
     * @returns {Promise<string>}
     * @remarks
     * 与标题变化事件无关：那个只在**页面文档标题**变化时触发（标题栏跟随页面），
     * 程序调 `setTitle()` 不会触发它。要"标题现在是什么"调这个方法，别等事件。
     */
    title() {
        return invoke('window.title', { label: this.label });
    }

    // ---- 外观 ----

    /**
     * 设置标题。需要 `window.setTitle` 被允许。
     * @param {string} title
     */
    setTitle(title) {
        if (typeof title !== 'string') {
            throw new Error('setTitle: title 必须是字符串');
        }

        return invoke('window.setTitle', { label: this.label, title });
    }

    /**
     * 设置是否可调整大小。需要 `window.setResizable` 被允许。
     * @param {boolean} resizable
     */
    setResizable(resizable) {
        return invoke('window.setResizable', { label: this.label, value: resizable !== false });
    }

    /**
     * 设置全屏。需要 `window.setFullscreen` 被允许。
     * @param {boolean} fullscreen
     */
    setFullscreen(fullscreen) {
        return invoke('window.setFullscreen', { label: this.label, value: fullscreen !== false });
    }

    /** 全屏切换，返回切换后是否全屏。**无条件放行**。 */
    toggleFullscreen() {
        return invoke('window.toggleFullscreen', { label: this.label });
    }

    /**
     * 设置置顶（对标 Tauri 的 `setAlwaysOnTop`）。需要 `window.setOnTop` 被允许。
     * @param {boolean} alwaysOnTop
     */
    setAlwaysOnTop(alwaysOnTop) {
        return invoke('window.setOnTop', { label: this.label, value: alwaysOnTop !== false });
    }

    /** 置顶切换，返回切换后是否置顶。**无条件放行**。 */
    toggleAlwaysOnTop() {
        return invoke('window.toggleOnTop', { label: this.label });
    }

    // ---- 几何 ----

    /**
     * 设置**客户区**尺寸（对标 Tauri 的 `setSize`）。
     *
     * @param {{ type?: string, width: number, height: number } | [number, number]} size
     *   `LogicalSize` / `PhysicalSize` / 裸对象 / 数字对。物理尺寸会用当前缩放比换算。
     * @returns {Promise<void>}
     * @remarks
     * 语义是**逻辑像素的客户区**（与 `WebviewWindow.Resize` 一致，见 API.md 的尺寸说明）。
     * Tauri 的 `setSize` 也是客户区，所以迁移过来是对的。
     */
    async setSize(size) {
        const normalized = normalizeSize(size);
        let { width, height } = normalized;

        if (normalized.type === 'Physical') {
            const factor = await scaleFactorOf(this.label);
            width = physicalToLogical(width, factor);
            height = physicalToLogical(height, factor);
        }

        await invoke('window.resize', { label: this.label, width: Math.round(width), height: Math.round(height) });
    }

    /**
     * 设置窗口左上角位置（对标 Tauri 的 `setPosition`）。需要 `window.moveTo` 被允许。
     *
     * @param {{ type?: string, x: number, y: number } | [number, number]} position
     */
    async setPosition(position) {
        const normalized = normalizePosition(position);
        let { x, y } = normalized;

        if (normalized.type === 'Physical') {
            const factor = await scaleFactorOf(this.label);
            x = physicalToLogical(x, factor);
            y = physicalToLogical(y, factor);
        }

        await invoke('window.moveTo', { label: this.label, x: Math.round(x), y: Math.round(y) });
    }

    // ---- 光标（B21）----
    //
    // 六条都需要显式 Allow（不在标题栏白名单里）。参数的严格校验放在页面侧：
    // 传错形状时给一句能看懂的 TypeError，比让宿主回 invalid_argument 更早也更具体。

    /**
     * 把光标限制在窗口内（对标 Tauri 的 `setCursorGrab`）。
     *
     * @param {boolean} grab `true` 抓取，`false` 释放。
     * @returns {Promise<void>}
     * @remarks
     * **Linux 不支持**（抛 `not_supported`）。macOS 的形态是把光标**锁死**在原地，
     * 并不是"限制在窗口矩形内"——这是平台能给的唯一形态。
     */
    async setCursorGrab(grab) {
        if (typeof grab !== 'boolean') {
            throw new TypeError(`setCursorGrab: grab 必须是布尔值，收到 ${typeof grab}。`);
        }

        await invoke('window.setCursorGrab', { label: this.label, grab });
    }

    /**
     * 显示或隐藏光标（对标 Tauri 的 `setCursorVisible`）。
     *
     * @param {boolean} visible
     * @returns {Promise<void>}
     * @remarks 隐藏是"应用到窗口/应用"的状态，窗口销毁时宿主会自动恢复。
     */
    async setCursorVisible(visible) {
        if (typeof visible !== 'boolean') {
            throw new TypeError(`setCursorVisible: visible 必须是布尔值，收到 ${typeof visible}。`);
        }

        await invoke('window.setCursorVisible', { label: this.label, visible });
    }

    /**
     * 设置光标形状（对标 Tauri 的 `setCursorIcon`）。
     *
     * @param {CursorIcon} icon
     * @returns {Promise<void>}
     * @remarks
     * **设置之后页面的 CSS `cursor` 不再生效**——这是"应用显式指定"应有的语义，
     * 但确实会盖掉文本框的 I 形光标之类的东西。三平台都没有"恢复交给 CSS"的入口，
     * 只能再设一个形状。
     */
    async setCursorIcon(icon) {
        if (typeof icon !== 'string' || !CURSOR_ICONS.has(icon)) {
            throw new TypeError(
                `setCursorIcon: 未知的 icon ${JSON.stringify(icon)}。`
                + '取值是 CSS cursor 关键字的 camelCase 形式（如 default / pointer / notAllowed / ewResize）。');
        }

        await invoke('window.setCursorIcon', { label: this.label, icon });
    }

    /**
     * 把光标移到**客户区**内的某个位置（对标 Tauri 的 `setCursorPosition`）。
     *
     * @param {import('./dpi.js').LogicalPosition | import('./dpi.js').PhysicalPosition
     *   | { type?: string, x: number, y: number } | [number, number]} position
     * @returns {Promise<void>}
     * @remarks
     * 与 Tauri 一致，坐标相对窗口左上角（不是屏幕）。物理/逻辑两种都收，
     * 换算在宿主侧做（它知道缩放比）。
     */
    async setCursorPosition(position) {
        await invoke('window.setCursorPosition', { label: this.label, position: normalizePosition(position) });
    }

    /**
     * 让窗口忽略鼠标事件（对标 Tauri 的 `setIgnoreCursorEvents`，即"点击穿透"）。
     *
     * @param {boolean} ignore
     * @returns {Promise<void>}
     * @remarks **Linux 只在 X11 下有效**（Wayland 抛 `not_supported`）。
     */
    async setIgnoreCursorEvents(ignore) {
        if (typeof ignore !== 'boolean') {
            throw new TypeError(`setIgnoreCursorEvents: ignore 必须是布尔值，收到 ${typeof ignore}。`);
        }

        await invoke('window.setIgnoreCursorEvents', { label: this.label, ignore });
    }

    /**
     * 从窗口某条边/角开始一次 resize 拖动（对标 Tauri 的 `startResizeDragging`）。
     *
     * @param {ResizeDirection} direction
     * @returns {Promise<void>}
     * @remarks
     * <b>必须在鼠标按下的处理过程中调用</b>（例如 `mousedown` 里）：Windows 会进入一个
     * 跟着鼠标的模态循环，Wayland 则依赖那次按下事件的 serial，晚一步就无效。
     * <b>macOS 不支持</b>（抛 `not_supported`）。
     */
    async startResizeDragging(direction) {
        if (typeof direction !== 'string' || !RESIZE_DIRECTIONS.has(direction)) {
            throw new TypeError(
                `startResizeDragging: 未知的 direction ${JSON.stringify(direction)}。`
                + '只认 East / North / NorthEast / NorthWest / South / SouthEast / SouthWest / West。');
        }

        await invoke('window.startResizeDragging', { label: this.label, direction });
    }

    // ---- 平台开关 / 特效 / 任务栏（B22）----
    //
    // 十六条方法 + 三个类型。**支持矩阵逐平台不同**，而且这一批的语义是统一的：
    // 能做的平台真做，做不了的抛 `not_supported`（错误码），不做静默 no-op。
    // 逐方法的平台事实见 docs/API.md 的 B22 支持矩阵。
    //
    // 参数的严格校验放在页面侧：传错形状时给一句能看懂的 TypeError，比让宿主回
    // invalid_argument 更早、也更具体（同一个理由见上面光标那组）。

    /**
     * 是否在任务栏里隐藏这个窗口（对标 Tauri 的 `setSkipTaskbar`）。
     *
     * @param {boolean} skip
     * @returns {Promise<void>}
     * @remarks
     * 需要 `Allow("window.setSkipTaskbar")`。
     *
     * **macOS 抛 `not_supported`**：Dock 里显示的是整个应用，没有"按窗口隐藏"这回事。
     * Windows 走 `ITaskbarList::DeleteTab`/`AddTab`；Linux 是
     * `gtk_window_set_skip_taskbar_hint`（Wayland 下由合成器决定）。
     */
    setSkipTaskbar(skip) {
        if (typeof skip !== 'boolean') {
            throw new TypeError('setSkipTaskbar: skip 必须是布尔值');
        }

        return invoke('window.setSkipTaskbar', { label: this.label, value: skip });
    }

    /**
     * 是否在所有工作区/虚拟桌面上可见（对标 Tauri 的 `setVisibleOnAllWorkspaces`）。
     *
     * @param {boolean} visible
     * @returns {Promise<void>}
     * @remarks
     * 需要 `Allow("window.setVisibleOnAllWorkspaces")`。
     *
     * **Windows 抛 `not_supported`**（Win32 没有公开的虚拟桌面接口）；
     * macOS 切 `collectionBehavior` 的 `canJoinAllSpaces`；Linux 是
     * `gtk_window_stick`（X11 的 `_NET_WM_STATE_STICKY`）。
     */
    setVisibleOnAllWorkspaces(visible) {
        if (typeof visible !== 'boolean') {
            throw new TypeError('setVisibleOnAllWorkspaces: visible 必须是布尔值');
        }

        return invoke('window.setVisibleOnAllWorkspaces', { label: this.label, value: visible });
    }

    /**
     * 是否阻止窗口内容被别的应用截取（对标 Tauri 的 `setContentProtected`）。
     *
     * @param {boolean} contentProtected
     * @returns {Promise<void>}
     * @remarks
     * 需要 `Allow("window.setContentProtected")`。
     *
     * Windows 是 `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)`（Win10 2004+，
     * 更早的系统退回 `WDA_MONITOR`：截屏里显示为黑块），macOS 是
     * `NSWindow.sharingType = none`。**Linux 抛 `not_supported`**。
     */
    setContentProtected(contentProtected) {
        if (typeof contentProtected !== 'boolean') {
            throw new TypeError('setContentProtected: contentProtected 必须是布尔值');
        }

        return invoke('window.setContentProtected', { label: this.label, value: contentProtected });
    }

    /**
     * 简化全屏（对标 Tauri 的 `setSimpleFullscreen`）。
     *
     * @param {boolean} fullscreen
     * @returns {Promise<void>}
     * @remarks
     * 需要 `Allow("window.setSimpleFullscreen")`。
     *
     * **只有 macOS 有独立语义**：不创建新的 Space，靠"去掉标题栏 + 铺满当前屏幕"实现
     * （Lion 之前的全屏形态）。**Windows / Linux 上等同 `setFullscreen`**——与 Tauri 的
     * 文档一致（"On other platforms, this is the same as setFullscreen"）。
     */
    setSimpleFullscreen(fullscreen) {
        if (typeof fullscreen !== 'boolean') {
            throw new TypeError('setSimpleFullscreen: fullscreen 必须是布尔值');
        }

        return invoke('window.setSimpleFullscreen', { label: this.label, value: fullscreen });
    }

    /**
     * 在包含给定**物理**坐标的那台显示器上全屏（对标 Tauri 的 `setFullscreenOnMonitor`）。
     *
     * @param {{ x: number, y: number } | [number, number]} position 屏幕物理像素坐标
     *   （通常直接传 `(await availableMonitors())[i].position`）。
     * @returns {Promise<void>}
     * @remarks
     * 需要 `Allow("window.setFullscreenOnMonitor")`。
     *
     * **该坐标不在任何显示器上时什么都不做**（与 Tauri 一致，不是错误）。
     * 落点分两步：先把窗口移到那台显示器，再进入全屏。
     */
    setFullscreenOnMonitor(position) {
        const normalized = normalizePosition(position);

        return invoke('window.setFullscreenOnMonitor', {
            label: this.label,
            value: { x: normalized.x, y: normalized.y },
        });
    }

    /**
     * 标题栏样式（对标 Tauri 的 `setTitleBarStyle`）。
     *
     * @param {TitleBarStyle} style `'visible'` / `'transparent'` / `'overlay'`。
     * @returns {Promise<void>}
     * @remarks
     * 需要 `Allow("window.setTitleBarStyle")`。**只有 macOS 实现**，Windows/Linux 抛
     * `not_supported`（那边的等价能力是"创建时 Frameless + 页面自绘标题栏"）。
     *
     * `overlay` 会把页面延伸到标题栏下方、只留红绿灯——记得给它们留位置，
     * 并用 `startDragging()` 划一块可拖动区域。
     */
    setTitleBarStyle(style) {
        if (typeof style !== 'string' || !TITLE_BAR_STYLES.has(style)) {
            throw new TypeError(
                `setTitleBarStyle: 未知的 style ${JSON.stringify(style)}（只认 visible / transparent / overlay）。`);
        }

        return invoke('window.setTitleBarStyle', { label: this.label, value: style });
    }

    /**
     * 是否给窗口画阴影（对标 Tauri 的 `setShadow`）。
     *
     * @param {boolean} enable
     * @returns {Promise<void>}
     * @remarks
     * 需要 `Allow("window.setShadow")`。
     *
     * **Linux 抛 `not_supported`**（GTK 没有阴影 API，阴影由合成器给）。
     * Windows 落点是 DWM 的非客户区渲染策略：**装饰窗口关不掉阴影**，
     * 而给无边框窗口打开阴影在 Win11 上会带来圆角与 1px 边框。
     */
    setShadow(enable) {
        if (typeof enable !== 'boolean') {
            throw new TypeError('setShadow: enable 必须是布尔值');
        }

        return invoke('window.setShadow', { label: this.label, value: enable });
    }

    /**
     * 是否永远压在别的窗口之下（对标 Tauri 的 `setAlwaysOnBottom`）。
     *
     * @param {boolean} alwaysOnBottom
     * @returns {Promise<void>}
     * @remarks
     * 需要 `Allow("window.setAlwaysOnBottom")`。
     *
     * 与 `setAlwaysOnTop` **互斥**：两者同时开时后设的那个赢（本库在设置底时会先取消置顶）。
     * 三平台都做（Linux 是 `gtk_window_set_keep_below`，Wayland 下只是提示）。
     */
    setAlwaysOnBottom(alwaysOnBottom) {
        if (typeof alwaysOnBottom !== 'boolean') {
            throw new TypeError('setAlwaysOnBottom: alwaysOnBottom 必须是布尔值');
        }

        return invoke('window.setAlwaysOnBottom', { label: this.label, value: alwaysOnBottom });
    }

    /**
     * 窗口能否获得焦点（对标 Tauri 的 `setFocusable`）。
     *
     * @param {boolean} focusable
     * @returns {Promise<void>}
     * @remarks
     * 需要 `Allow("window.setFocusable")`。
     *
     * **macOS 抛 `not_supported`**（AppKit 没有公开入口，只有私有的
     * `_setPreventsActivation`，用它等于把自己绑到未公开 API 上）。
     * Windows 切 `WS_EX_NOACTIVATE`，Linux 是 `set_accept_focus` + `can_focus`。
     */
    setFocusable(focusable) {
        if (typeof focusable !== 'boolean') {
            throw new TypeError('setFocusable: focusable 必须是布尔值');
        }

        return invoke('window.setFocusable', { label: this.label, value: focusable });
    }

    /**
     * 是否显示系统装饰（标题栏与边框，对标 Tauri 的 `setDecorations`）。
     *
     * @param {boolean} decorations
     * @returns {Promise<void>}
     * @remarks
     * 需要 `Allow("window.setDecorations")`。
     *
     * **运行期切换，三平台都做**。与本库的 `OrielWindowOptions.Frameless` 是两件事：
     * 后者决定宿主自己的命中测试与拖拽热区，创建后不变；这个方法只改原生装饰。
     */
    setDecorations(decorations) {
        if (typeof decorations !== 'boolean') {
            throw new TypeError('setDecorations: decorations 必须是布尔值');
        }

        return invoke('window.setDecorations', { label: this.label, value: decorations });
    }

    /**
     * 应用一组窗口材质（对标 Tauri 的 `setEffects`）。
     *
     * @param {Effects} effects
     * @returns {Promise<void>}
     * @remarks
     * 需要 `Allow("window.setEffects")`。
     *
     * **取列表里第一个本平台支持的那个，其余忽略**（与 Tauri 一致），所以一份代码可以
     * 同时列 Windows 与 macOS 的材质。**一个都不支持时抛 `not_supported`**——"设了没生效"
     * 必须能被看见。
     *
     * **Linux 整条命令抛 `not_supported`**：窗口材质是 DWM 与 AppKit 的概念。
     *
     * 需要页面自己是透明的（材质画在 webview 之后）；macOS 上本库会把窗口与 webview 的
     * 背景放开，Windows 上 DWM 材质由系统画在客户区之后。
     *
     * @example
     * await win.setEffects({ effects: [Effect.Mica, Effect.Sidebar], state: EffectState.Active });
     */
    setEffects(effects) {
        if (effects === null || typeof effects !== 'object') {
            throw new TypeError(
                'setEffects: 参数必须是对象 { effects: [...], state?, radius?, color?, interactive? }');
        }

        const list = effects.effects;
        if (!Array.isArray(list) || list.length === 0) {
            throw new TypeError('setEffects: effects 必须是非空数组（要清掉材质请用 clearEffects）');
        }

        for (const name of list) {
            if (typeof name !== 'string' || !EFFECTS.has(name)) {
                throw new TypeError(
                    `setEffects: 未知的材质 ${JSON.stringify(name)}。取值见 docs/API.md 的材质表。`);
            }
        }

        if (effects.state !== undefined
            && (typeof effects.state !== 'string' || !EFFECT_STATES.has(effects.state))) {
            throw new TypeError(
                `setEffects: 未知的 state ${JSON.stringify(effects.state)}`
                + '（只认 followsWindowActiveState / active / inactive）');
        }

        if (effects.radius !== undefined
            && (typeof effects.radius !== 'number' || !Number.isFinite(effects.radius) || effects.radius < 0)) {
            throw new TypeError('setEffects: radius 必须是非负有限数');
        }

        if (effects.interactive !== undefined && typeof effects.interactive !== 'boolean') {
            throw new TypeError('setEffects: interactive 必须是布尔值');
        }

        /** @type {Effects} */
        const payload = { effects: [...list] };
        if (effects.state !== undefined) {
            payload.state = effects.state;
        }

        if (effects.radius !== undefined) {
            payload.radius = effects.radius;
        }

        if (effects.interactive !== undefined) {
            payload.interactive = effects.interactive;
        }

        // 颜色三种形状原样交给宿主（数组 / 对象；字符串 hex Tauri 也不收）
        if (effects.color !== undefined) {
            payload.color = effects.color;
        }

        return invoke('window.setEffects', { label: this.label, value: payload });
    }

    /**
     * 清掉已应用的材质（对标 Tauri 的 `clearEffects`）。
     *
     * @returns {Promise<void>}
     * @remarks
     * 与 `setEffects` 共用同一个能力（`Allow("window.setEffects")`）。
     * 没设过时是空操作；**Linux 同样抛 `not_supported`**（能力整个不存在）。
     */
    clearEffects() {
        return invoke('window.clearEffects', { label: this.label });
    }

    /**
     * 任务栏进度条（对标 Tauri 的 `setProgressBar`）。
     *
     * @param {ProgressBarState | string} state `{ status, progress? }`，或直接给状态字符串。
     * @returns {Promise<void>}
     * @remarks
     * 需要 `Allow("window.setProgressBar")`。
     *
     * **只有 Windows 实现**（`ITaskbarList3`）。macOS 的 Dock 磁贴没有进度条 API，
     * Linux 需要 `libunity`（本库不链接）——两者都抛 `not_supported`。
     *
     * `progress` 是 0–100 的百分比；`none` 表示隐藏（此时 `progress` 无意义）。
     */
    setProgressBar(state) {
        const payload = typeof state === 'string' ? { status: state } : state;

        if (payload === null || typeof payload !== 'object') {
            throw new TypeError('setProgressBar: 参数必须是 { status, progress? } 或状态字符串');
        }

        if (payload.status !== undefined
            && (typeof payload.status !== 'string' || !PROGRESS_STATUSES.has(payload.status))) {
            throw new TypeError(
                `setProgressBar: 未知的 status ${JSON.stringify(payload.status)}`
                + '（只认 none / normal / indeterminate / paused / error）');
        }

        if (payload.progress !== undefined
            && (typeof payload.progress !== 'number' || !Number.isFinite(payload.progress))) {
            throw new TypeError('setProgressBar: progress 必须是有限数字（0–100）');
        }

        return invoke('window.setProgressBar', { label: this.label, value: payload });
    }

    /**
     * 任务栏图标上的角标覆盖图（对标 Tauri 的 `setOverlayIcon`）。
     *
     * @param {import('./image.js').JsImage} [icon] 路径、PNG/ICO 字节或 `Image` 实例；
     *   **不传 / `null` 表示清除**。
     * @returns {Promise<void>}
     * @remarks
     * 需要 `Allow("window.setOverlayIcon")`。**只有 Windows 有这个概念**；
     * macOS 的等价物是 Dock 徽标（`setBadgeCount` / `setBadgeLabel`），两者形状不同、不互相顶替。
     */
    setOverlayIcon(icon) {
        if (icon === undefined || icon === null) {
            return invoke('window.setOverlayIcon', { label: this.label });
        }

        if (typeof icon === 'string') {
            if (icon === '') {
                throw new TypeError('setOverlayIcon: 路径不能是空字符串');
            }

            return invoke('window.setOverlayIcon', { label: this.label, value: { path: icon } });
        }

        if (isImageLike(icon)) {
            const image = /** @type {import('./image.js').Image} */ (icon);
            if (image.hasEncodedBytes === false) {
                return invoke('window.setOverlayIcon', {
                    label: this.label,
                    value: {
                        rgba: bytesToBase64(image.rgbaBytes),
                        width: image.width,
                        height: image.height,
                    },
                });
            }

            return invoke('window.setOverlayIcon', {
                label: this.label,
                value: { bytes: bytesToBase64(image.bytes) },
            });
        }

        const bytes = toImageBytes(icon);
        if (bytes !== null) {
            assertImageFormat(bytes, 'setOverlayIcon');
            return invoke('window.setOverlayIcon', {
                label: this.label,
                value: { bytes: bytesToBase64(bytes) },
            });
        }

        throw new TypeError(
            'setOverlayIcon: icon 必须是文件路径、PNG/ICO 字节，或 @orielweb/api/image 的 Image 实例'
            + '（不传表示清除覆盖图标）。');
    }

    /**
     * Dock 徽标上的数字（对标 Tauri 的 `setBadgeCount`）。
     *
     * @param {number} [count] 要显示的数字；**不传或 0 表示清除**。
     * @returns {Promise<void>}
     * @remarks
     * 需要 `Allow("window.setBadgeCount")`。**只有 macOS 实现**，而且是**应用级**的
     * （所有窗口调它都改同一个 Dock 图标）。Windows 用 `setOverlayIcon` 代替；
     * Linux 需要 `libunity`（本库不链接）——两者都抛 `not_supported`。
     */
    setBadgeCount(count) {
        if (count === undefined || count === null) {
            return invoke('window.setBadgeCount', { label: this.label });
        }

        if (typeof count !== 'number' || !Number.isFinite(count)) {
            throw new TypeError('setBadgeCount: count 必须是有限数字（不传表示清除徽标）');
        }

        return invoke('window.setBadgeCount', { label: this.label, value: Math.round(count) });
    }

    /**
     * Dock 徽标上的短文本（对标 Tauri 的 `setBadgeLabel`）。
     *
     * @param {string} [label] 要显示的文本；**不传表示清除**。
     * @returns {Promise<void>}
     * @remarks
     * 需要 `Allow("window.setBadgeLabel")`。**只有 macOS 实现**（应用级）。
     * 与 `setBadgeCount` 是同一个落点（`NSApp.dockTile.badgeLabel`），后者只是把数字转成文本。
     */
    setBadgeLabel(label) {
        if (label === undefined || label === null) {
            return invoke('window.setBadgeLabel', { label: this.label });
        }

        if (typeof label !== 'string') {
            throw new TypeError('setBadgeLabel: label 必须是字符串（不传表示清除徽标）');
        }

        return invoke('window.setBadgeLabel', { label: this.label, value: label });
    }

    /**
     * 设置**客户区**最小尺寸（对标 Tauri 的 `setMinSize`）。
     *
     * @param {{ type?: string, width: number, height: number } | [number, number] | null} size
     *   `null` 表示取消限制（本库用 0 表示"不限制"，与 `OrielWindowOptions.MinWidth` 一致）。
     */
    async setMinSize(size) {
        if (size === null || size === undefined) {
            await invoke('window.setMinSize', { label: this.label, width: 0, height: 0 });
            return;
        }

        const normalized = normalizeSize(size);
        let { width, height } = normalized;

        if (normalized.type === 'Physical') {
            const factor = await scaleFactorOf(this.label);
            width = physicalToLogical(width, factor);
            height = physicalToLogical(height, factor);
        }

        await invoke('window.setMinSize', {
            label: this.label,
            width: Math.round(width),
            height: Math.round(height),
        });
    }

    /** 居中。需要 `window.center` 被允许。 */
    center() {
        return invoke('window.center', { label: this.label });
    }

    // ---- 几何查询 ----
    //
    // 四条都需要 `Allow`（它们不在标题栏白名单里）。
    //
    // **与 Tauri 的一处语义差别**：Tauri 的这四个方法返回 `PhysicalSize` / `PhysicalPosition`；
    // 本库返回 `LogicalSize` / `LogicalPosition`（`type: 'Logical'`）。
    // 理由是库对页面只暴露逻辑像素——混入物理像素会让"这个值是哪种单位"变成每个调用点
    // 都要想一遍的问题，而算错的症状是"在 Retina/150% 屏上偏一倍"。
    // 字段名（width/height/x/y）与 Tauri 一致，返回的对象又能直接喂回 setSize/setPosition，
    // 所以迁移代码通常感觉不到差别。

    /**
     * 客户区尺寸（逻辑像素）。需要 `window.innerSize` 被允许。
     *
     * @returns {Promise<import('./dpi.js').LogicalSize>}
     */
    async innerSize() {
        return toLogicalSize(await invoke('window.innerSize', { label: this.label }));
    }

    /**
     * 整窗尺寸（逻辑像素）。
     *
     * @returns {Promise<import('./dpi.js').LogicalSize>}
     * @remarks
     * Linux/Wayland 下可能与 `innerSize()` 相等——合成器不告诉客户端装饰尺寸。
     * 需要 `window.outerSize` 被允许。
     */
    async outerSize() {
        return toLogicalSize(await invoke('window.outerSize', { label: this.label }));
    }

    /**
     * 窗口左上角（屏幕逻辑坐标）。需要 `window.outerPosition` 被允许。
     *
     * @returns {Promise<import('./dpi.js').LogicalPosition>}
     */
    async outerPosition() {
        return toLogicalPosition(await invoke('window.outerPosition', { label: this.label }));
    }

    /**
     * 客户区左上角（屏幕逻辑坐标）。
     *
     * @returns {Promise<import('./dpi.js').LogicalPosition>}
     * @remarks
     * Linux 上目前恒等于 `outerPosition()`（GTK 不暴露装饰偏移）。
     * 需要 `window.innerPosition` 被允许。
     */
    async innerPosition() {
        return toLogicalPosition(await invoke('window.innerPosition', { label: this.label }));
    }

    // ---- 拖动 ----

    /**
     * 开始拖动窗口（对标 Tauri 的 `startDragging`）。
     *
     * @remarks
     * 库自己接管的标题栏拖动**不走这里**——它在注入脚本里内联完成（因为要在 `mousedown`
     * 之内同步判断双击）。这个方法是给"页面自己实现拖动"用的。**无条件放行**。
     */
    startDragging() {
        return invoke('window.drag', { label: this.label });
    }

    // ---- 关闭接管 ----

    /**
     * 订阅本窗口的关闭请求（对标 Tauri 的 `onCloseRequested`）。
     *
     * @param {(event: import('./window.js').CloseRequestedEvent) => void | Promise<void>} handler
     * @returns {Promise<() => void>} 退订函数（`await` 拿到，与 Tauri 一致）。
     * @remarks
     * **语义与 Tauri 一致：默认关闭，`event.preventDefault()` 才留下。**
     *
     * ```js
     * const un = await getCurrentWindow().onCloseRequested(async (event) => {
     *   if (await hasUnsavedChanges()) {
     *     event.preventDefault();          // 不关
     *   }
     *   // 不调用：handler 返回后窗口关闭
     * });
     * ```
     *
     * 关闭的默认动作是**库替你做的**：handler（含 async）执行完之后，若没调用
     * `preventDefault()`，本方法会调 `destroy()`。这与 Tauri 的实现是同一段逻辑
     * （`if (!evt.isPreventDefault()) await this.destroy()`），所以迁移代码不用改。
     *
     * 为什么能这么做：Windows 的 `WM_CLOSE` 是**同步消息**、页面回执是异步 IPC，
     * 宿主没法在中途等回调。所以宿主只负责"挂起 + 推事件"，"关不关"这一半由
     * 页面侧的这层包装补上——异步回调跑完后它自己调 `destroy()`，不占用宿主线程。
     *
     * **handler 抛错时窗口保持打开**（与 Tauri 一致：异常中断后续的 `destroy`）。
     * 差别只在于会把错误打进 console——未处理的 rejection 在页面里很难定位。
     *
     * 前置条件：宿主必须开了关闭接管（`OrielWindowOptions.InterceptClose`
     * 或 `window.setCloseIntercept`），否则点关闭会直接关掉、回调永远不来。
     * 本方法会**自动**帮你打开它（需要 `window.setCloseIntercept` 被允许）。
     */
    async onCloseRequested(handler) {
        if (typeof handler !== 'function') {
            throw new Error('onCloseRequested: 需要一个函数');
        }

        // 自动打开接管：订阅了却没有接管 = 回调永远不来，而那种"配了没反应"
        // 是最难排查的。失败要报出来（与事件转发的处理一致）。
        let interceptAcquired = false;
        try {
            await invoke('window.setCloseIntercept', { label: this.label, enabled: true });
            refCloseIntercept(this.label);
            interceptAcquired = true;
        } catch (error) {
            console.error(
                '@orielweb/api/window: 无法打开关闭接管，onCloseRequested 的回调不会触发。' +
                `原因：${errorText(error)}。` +
                '请在宿主侧允许 `window.setCloseIntercept`，或用 OrielWindowOptions.InterceptClose 静态开启。');
        }

        // async：`preventDefault()` 可能发生在 handler 的任意 await 之后，
        // 必须等它跑完才能判定。listener 的返回值会被丢弃，但内部的 await 照常执行。
        const unlisten = listen(TauriEvent.WINDOW_CLOSE_REQUESTED, async (event) => {
            const closeEvent = new CloseRequestedEvent(event);

            try {
                await handler(closeEvent);
            } catch (error) {
                console.error(
                    '@orielweb/api/window: onCloseRequested 的 handler 抛错，窗口保持打开。' +
                    `原因：${errorText(error)}`);
                return;
            }

            if (closeEvent.isPreventDefault()) {
                return;
            }

            // 默认动作。失败要说出来：静默的话现象就是"点了关闭没反应"，
            // 而那与"页面拦下了关闭"长得一模一样。
            try {
                await this.destroy();
            } catch (error) {
                console.error(
                    '@orielweb/api/window: 关闭请求已放行，但 destroy 失败，窗口仍然打开。' +
                    `原因：${errorText(error)}`);
            }
        });

        let released = false;

        return async () => {
            // 幂等：调用方可能既在卸载钩子里退订、又在别处退一次。
            if (released) {
                return;
            }

            released = true;
            unlisten();

            if (!interceptAcquired) {
                return;
            }

            // 还有别的组件在订阅时**不能**关接管：关掉之后它们的 preventDefault
            // 会静默失效（症状是"我拦了，窗口还是关了"）。只有归零才关。
            if (!unrefCloseIntercept(this.label)) {
                return;
            }

            // 归零后必须关掉：留着它会让窗口在没人处理的情况下点关闭没反应
            // （宿主挂起、没人 destroy）。这是"订阅驱动"该有的收尾。
            try {
                await invoke('window.setCloseIntercept', { label: this.label, enabled: false });
            } catch {
                // 关不掉不影响退订本身；宿主侧的默认值仍是应用配置的那个
            }
        };
    }

    /**
     * 订阅本窗口的尺寸变化（对标 Tauri 的 `onResized`）。
     * @param {(event: { event: string, id: number, payload: { width: number, height: number } }) => void} handler
     * @returns {Promise<() => void>}
     */
    onResized(handler) {
        return listenAsync(TauriEvent.WINDOW_RESIZED, handler);
    }

    /**
     * 订阅本窗口的位置变化（对标 Tauri 的 `onMoved`）。
     * @param {(event: { event: string, id: number, payload: { x: number, y: number } }) => void} handler
     * @returns {Promise<() => void>}
     */
    onMoved(handler) {
        return listenAsync(TauriEvent.WINDOW_MOVED, handler);
    }

    /**
     * 订阅系统主题变化（对标 Tauri 的 `onThemeChanged`）。
     *
     * @param {(event: { event: string, id: number, payload: 'light' | 'dark' }) => void} handler
     * @returns {Promise<() => void>}
     * @remarks
     * 三平台都由宿主检测系统主题并广播（Windows 读注册表 + `WM_SETTINGCHANGE`、
     * macOS 听 `AppleInterfaceThemeChangedNotification`、Linux 听 GSettings），
     * 概念跨平台一致，所以这条在三个平台上都能用。
     *
     * **只推"变化"，不推当前值**（与 Tauri 一致）：初始值请用 `theme()` 读——
     * 否则页面在"主题一直没变过"时会永远收不到第一个值。
     */
    onThemeChanged(handler) {
        return listenAsync(TauriEvent.WINDOW_THEME_CHANGED, handler);
    }

    /**
     * 订阅"文件被拖入本窗口"（对标 Tauri 的 `onDragDropEvent`）。
     *
     * @param {(event: { event: string, id: number, payload: { type: 'drop', paths: string[] } }) => void} handler
     * @returns {Promise<() => void>}
     * @remarks
     * **只有 `drop` 一种**：宿主只在用户松手那一刻收到原生回调（三个平台都是 OS 级投放人手一个
     * 回调），所以 `payload.type` 恒为 `'drop'`，`enter` / `over` / `leave` 永远不会来。
     * 靠 `over` 做"拖到此处高亮"的代码在本库上不会亮——这是本批**有意不假装**的那部分。
     *
     * **payload 里没有 `position`**（Tauri 有，物理像素）。读它会得到 `undefined`：
     * 宁可当场露出来，也不要给一个 0 让人误以为"拖到了左上角"。
     *
     * 在 Tauri 里 `Window` 与 `Webview` **都有**这个方法（`Window` 那份是把四种事件名
     * 归一成同一个回调）；本库没有 webview 层（窗口即 webview），所以只在 `Window` 上。
     * 迁移时 `getCurrentWebview().onDragDropEvent(...)` 改成
     * `getCurrentWindow().onDragDropEvent(...)` 即可。
     */
    async onDragDropEvent(handler) {
        // 四次订阅、一个回调：宿主把四个阶段当四条独立事件发（事件名与 Tauri 相同），
        // 这里按 Tauri 的形状归一 —— 调用方只看 `payload.type` 收窄。
        const offEnter = await listenAsync(TauriEvent.DRAG_ENTER, (event) => {
            handler({
                ...event,
                payload: {
                    type: 'enter',
                    paths: event.payload.paths ?? [],
                    position: toDragPosition(event.payload.position),
                },
            });
        });
        const offOver = await listenAsync(TauriEvent.DRAG_OVER, (event) => {
            handler({
                ...event,
                payload: { type: 'over', position: toDragPosition(event.payload.position) },
            });
        });
        const offDrop = await listenAsync(TauriEvent.DRAG_DROP, (event) => {
            handler({
                ...event,
                payload: {
                    type: 'drop',
                    paths: event.payload.paths ?? [],
                    position: toDragPosition(event.payload.position),
                },
            });
        });
        const offLeave = await listenAsync(TauriEvent.DRAG_LEAVE, (event) => {
            // leave 没有坐标也没有载荷：宿主根本不发那两个字段，这里也不造
            handler({ ...event, payload: { type: 'leave' } });
        });

        return () => {
            offEnter();
            offOver();
            offDrop();
            offLeave();
        };
    }

    /**
     * 订阅窗口所在显示器的缩放比变化（对标 Tauri 的 `onScaleChanged`）。
     *
     * @param {(event: { event: string, id: number, payload: { scaleFactor: number, size: { width: number, height: number } } }) => void} handler
     * @returns {Promise<() => void>}
     * @remarks
     * `payload.size` 是**物理像素**（Tauri 那边是 `PhysicalSize`）——迁移代码会照搬
     * `payload.size.width` 这类读法，给逻辑尺寸等于悄悄换了单位。
     *
     * 三平台都由宿主检测并上报：Windows `WM_DPICHANGED`、macOS
     * `NSWindowDidChangeBackingPropertiesNotification`、Linux GDK 的 `notify::scale-factor`。
     * 事件是**按窗口**的：同一时刻两个窗口可以待在不同缩放的显示器上。
     */
    onScaleChanged(handler) {
        return listenAsync(TauriEvent.WINDOW_SCALE_FACTOR_CHANGED, handler);
    }

    /**
     * 订阅本窗口的焦点变化（对标 Tauri 的 `onFocusChanged`）。
     * @param {(event: { event: string, id: number, payload: null }) => void} handler
     * @returns {Promise<() => void>}
     */
    onFocusChanged(handler) {
        // 焦点在宿主侧是**两个事件名**（focus / blur），这里合成一个回调：
        // 页面只关心"变了没有"，而 Tauri 的 onFocusChanged 也是这个粒度。
        const offFocus = listen(TauriEvent.WINDOW_FOCUS, (e) => handler(e));
        const offBlur = listen(TauriEvent.WINDOW_BLUR, (e) => handler(e));

        return Promise.resolve(() => {
            offFocus();
            offBlur();
        });
    }
}

/**
 * `listen` 的 Promise 包装：Tauri 的 `on*` 系列都返回 `Promise<UnlistenFn>`，
 * 而本库的 `listen` 是同步的。包一层是为了让迁移代码 `await` 得动。
 *
 * @param {string} eventName
 * @param {(event: any) => void} handler
 * @returns {Promise<() => void>}
 */
function listenAsync(eventName, handler) {
    return Promise.resolve(listen(eventName, handler));
}

/**
 * 取当前窗口的句柄（对标 Tauri 的 `getCurrentWindow`）。
 *
 * @returns {Window}
 * @remarks
 * **同步**返回（与 Tauri 一致）。它只读 `oriel.label`，不需要问宿主。
 */
export function getCurrentWindow() {
    return new Window(currentLabel());
}

/**
 * 列出所有存活窗口（对标 Tauri 的 `getAllWindows`）。
 *
 * @returns {Promise<Window[]>}
 * @remarks 需要 `window.getAll` 被允许——枚举窗口是跨窗口信息。
 */
export async function getAllWindows() {
    const labels = await invoke('window.getAll');
    return (Array.isArray(labels) ? labels : []).map((label) => new Window(String(label)));
}

/**
 * 按 label 取窗口（对标 Tauri 的 `Window.getByLabel`）。
 *
 * @param {string} label
 * @returns {Promise<Window | null>} 不存在时 `null`（与 Tauri 一致，不抛）。
 */
export async function getByLabel(label) {
    if (typeof label !== 'string' || label === '') {
        throw new Error('getByLabel: label 必须是非空字符串');
    }

    const labels = await invoke('window.getAll');
    const found = (Array.isArray(labels) ? labels : []).some((item) => String(item) === label);
    return found ? new Window(label) : null;
}

/**
 * 有焦点的窗口（对标 Tauri 的 `getFocusedWindow`）。
 *
 * @returns {Promise<Window | null>} 没有窗口在前台时 `null`。
 * @remarks
 * 需要 `window.getFocused` 被允许——"哪个窗口在前台"是跨窗口信息。
 *
 * **返回 `null` 的两种情形**：① 应用没有窗口；② 有窗口但都不在前台（用户切到了别的程序）。
 * 调用方通常不需要区分——"现在没有窗口是活动的"对两者都成立。
 * 最小化的窗口不会被判为有焦点（它已不是 key window）。
 */
export async function getFocusedWindow() {
    const label = await invoke('window.getFocused');
    return typeof label === 'string' && label !== '' ? new Window(label) : null;
}

// ---------------------------------------------------------------------------
// 显示器（对标 Tauri 的 currentMonitor / primaryMonitor / availableMonitors /
// monitorFromPoint / cursorPosition）
//
// **单位：全部是物理像素**，原点在主显示器左上角。与本模块的窗口几何不同
// （innerSize / outerSize 是逻辑像素）——显示器要跨不同缩放的屏幕比较，
// 只有物理像素是统一刻度。两者之间用 `monitor.scaleFactor` 换算。
//
// **返回的是 dpi 模块的类实例**：`monitor.size` 是 `PhysicalSize`、`monitor.position`
// 是 `PhysicalPosition`，所以 `monitor.size.toLogical(monitor.scaleFactor)` 可用
// （与 Tauri 完全一致）。测试里断言过这一点——退成裸对象会让迁移代码在
// `.toLogical is not a function` 上挂掉。
// ---------------------------------------------------------------------------

/**
 * 一台显示器（对标 Tauri 的 `Monitor`）。
 *
 * @typedef {object} Monitor
 * @property {string | null} name 人类可读的名称；平台给不出时是 `null`。
 * @property {PhysicalPosition} position 左上角（物理像素，相对主屏左上角）。
 * @property {PhysicalSize} size 分辨率（物理像素）。
 * @property {number} scaleFactor 缩放比（1.0 = 100%，2.0 = Retina / 200%）。
 */

/**
 * 把宿主返回的显示器对象包成 dpi 类实例。
 *
 * @param {{ name: string | null, position: { x: number, y: number }, size: { width: number, height: number }, scaleFactor: number }} raw
 * @returns {Monitor}
 * @internal
 */
function toMonitor(raw) {
    return {
        name: raw.name ?? null,
        position: new PhysicalPosition(raw.position.x, raw.position.y),
        size: new PhysicalSize(raw.size.width, raw.size.height),
        scaleFactor: raw.scaleFactor,
    };
}

/**
 * 宿主返回 `null` 时保持 `null`，否则包成 dpi 类实例。
 *
 * @param {Parameters<typeof toMonitor>[0] | null} raw
 * @returns {Monitor | null}
 * @internal
 */
function mapMonitor(raw) {
    return raw === null || raw === undefined ? null : toMonitor(raw);
}

/**
 * 当前窗口所在的显示器（对标 Tauri 的 `currentMonitor`）。
 *
 * @returns {Promise<Monitor | null>} 平台报不出来时 `null`（例如窗口还没显示）。
 * @remarks
 * 按"相交面积最大"判定，不是"窗口中心点落在哪块屏"——两者在窗口跨屏时给出的答案不同，
 * 而用户与系统都按前者理解。
 *
 * 问的永远是**发起调用的那个窗口**（Tauri 的 `currentMonitor()` 同样是无参模块函数）。
 */
export async function currentMonitor() {
    return mapMonitor(await invoke('window.currentMonitor'));
}

/**
 * 主显示器（对标 Tauri 的 `primaryMonitor`）。
 *
 * @returns {Promise<Monitor | null>} 平台报不出来时 `null`。
 * @remarks
 * 主屏同时是坐标原点：它的 `position` 通常是 `(0, 0)`，副屏在主屏左侧或上方时为负。
 */
export async function primaryMonitor() {
    return mapMonitor(await invoke('window.primaryMonitor'));
}

/**
 * 所有显示器（对标 Tauri 的 `availableMonitors`）。
 *
 * @returns {Promise<Monitor[]>} 顺序由平台决定，**不保证主屏在前**；没有显示器时是空数组。
 * @remarks 每次调用都重新向系统枚举，所以插拔显示器、改分辨率之后立刻可见。
 */
export async function availableMonitors() {
    const monitors = await invoke('window.availableMonitors');
    return (Array.isArray(monitors) ? monitors : []).map((raw) => toMonitor(raw));
}

/**
 * 包含指定点的显示器（对标 Tauri 的 `monitorFromPoint`）。
 *
 * @param {number} x 横坐标（物理像素，相对主屏左上角）。
 * @param {number} y 纵坐标（物理像素，向下为正）。
 * @returns {Promise<Monitor | null>} 点不在任何屏幕上时 `null`（不吸附到最近的屏幕）。
 */
export async function monitorFromPoint(x, y) {
    return mapMonitor(await invoke('window.monitorFromPoint', { x, y }));
}

/**
 * 光标位置（对标 Tauri 的 `cursorPosition`）。
 *
 * @returns {Promise<PhysicalPosition>} 物理像素，相对主屏左上角。
 * @remarks
 * 拿不到时返回 `(0, 0)` 而不是抛异常：这与"光标真的在原点"无法区分，但这不是一个
 * 需要那个精度的查询——真正在意精度的调用方（拖拽、命中测试）用的是事件里的坐标。
 *
 * **Wayland 已知限制**：合成器不给全局光标坐标，GDK 只能报应用像素，
 * 所以返回值在分数缩放下可能有取整误差（见 `OrielMonitor` 的说明）。
 */
export async function cursorPosition() {
    const raw = await invoke('window.cursorPosition');
    return new PhysicalPosition(raw?.x ?? 0, raw?.y ?? 0);
}

/**
 * 把宿主给的 `{x, y}` 包成 `PhysicalPosition`（对标 Tauri 在 `onDragDropEvent` 里的做法）。
 *
 * @param {{ x: number, y: number } | undefined} position
 * @returns {PhysicalPosition}
 * @remarks
 * 坐标是**客户区物理像素**（左上原点）。与 `cursorPosition` 不同——那个是屏幕坐标，
 * 拖放要画的是"在窗口里的哪里"，用屏幕坐标还得再减一次窗口位置。
 * @private
 */
function toDragPosition(position) {
    return new PhysicalPosition(position?.x ?? 0, position?.y ?? 0);
}
