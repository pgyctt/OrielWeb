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
import { getTransport } from './internal.js';
import { listen, once, emit, emitTo, TauriEvent } from './event.js';
import {
    LogicalSize,
    LogicalPosition,
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
 * 把宿主返回的尺寸对象包成 {@link LogicalSize} 实例。
 *
 * @param {unknown} raw
 * @returns {LogicalSize}
 * @remarks
 * 宿主给的是 `{ type, width, height }` 的普通对象；包成类实例是为了让 `instanceof LogicalSize`
 * 成立、并能原样喂回 `setSize()`（那里按 `type` 分派）。两者形状本就一致，所以这里只做包装不回读校验——
 * 真拿到畸形值会在调用点立刻炸，比在这里静默补默认值好。
 */
function toLogicalSize(raw) {
    const value = /** @type {{ width?: number, height?: number }} */ (raw ?? {});
    return new LogicalSize(Number(value.width ?? 0), Number(value.height ?? 0));
}

/**
 * 把宿主返回的位置对象包成 {@link LogicalPosition} 实例；理由同 {@link toLogicalSize}。
 *
 * @param {unknown} raw
 * @returns {LogicalPosition}
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
            'getCurrentWindow: 当前传输层没有 label。它由宿主注入（0.4.0+）；' +
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
     * 而本库目前只有"跟随系统"这一种来源，没有"按窗口覆写主题"的能力。
     */
    theme() {
        return invoke('window.theme', { label: this.label });
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
     * @param {string} icon 图像文件路径（PNG / ICO）。
     * @returns {Promise<void>}
     * @remarks
     * 需要 `Allow("window.setIcon")`。
     *
     * **只支持路径**：Tauri 的 `setIcon` 还收 `Uint8Array` / `ArrayBuffer` / `number[]`
     * （ICO/PNG 原始字节）以及 `Image` 对象（资源表 handle）。前三种本库暂未支持，
     * 第四种要先实现 `@orielweb/api/image`；传这几种会**当场抛 TypeError 并说明原因**，
     * 而不是发一条注定失败的 IPC。
     *
     * **平台差异（两处）**：
     * - macOS 上没有窗口级图标，这里设的是**应用（Dock）图标**，会影响整个应用；
     * - Linux 的 Wayland 下窗口图标由合成器决定，通常忽略本调用。
     */
    setIcon(icon) {
        if (typeof icon !== 'string') {
            throw new TypeError(
                'setIcon: 目前只支持图像文件路径（string）。字节数组与 Image 对象的形式还未支持' +
                '——见本方法的文档。');
        }

        if (icon === '') {
            throw new TypeError('setIcon: 路径不能是空字符串');
        }

        return invoke('window.setIcon', { label: this.label, path: icon });
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
     * @param {{minWidth?: number|null, minHeight?: number|null, maxWidth?: number|null, maxHeight?: number|null}} constraints
     *   省略或给 `null` 的项**保持不动**（不会把它清成 0）。
     * @returns {Promise<void>}
     * @remarks
     * 需要 `Allow("window.setMinSize")` 与/或 `Allow("window.setMaxSize")`——
     * 这条在页面上**分派**到那两条命令，宿主没有"组合命令"。
     * 只给了一侧时只会发一条命令，所以不会出现"另一条失败导致半套约束"。
     */
    async setSizeConstraints(constraints = {}) {
        const min = [constraints.minWidth, constraints.minHeight];
        const max = [constraints.maxWidth, constraints.maxHeight];

        // 两侧都给才发对应命令：省略的项表示"这一次不动它"
        if (min.some((value) => value !== null && value !== undefined)) {
            await invoke('window.setMinSize', {
                label: this.label,
                width: min[0] ?? 0,
                height: min[1] ?? 0,
            });
        }

        if (max.some((value) => value !== null && value !== undefined)) {
            await invoke('window.setMaxSize', {
                label: this.label,
                value: { width: max[0] ?? 0, height: max[1] ?? 0 },
            });
        }
    }

    // ---- 请求用户注意（M5-6）----

    /**
     * 请求用户注意（任务栏闪烁 / Dock 弹跳）。
     *
     * @param {number | null} [requestType] `UserAttentionType.Critical` /
     *   `UserAttentionType.Informational`；**不传或给 `null` 表示取消**已发出的请求。
     * @returns {Promise<void>}
     * @remarks **macOS 上两档的区别最明显**（Critical 会一直弹跳到用户来看）；
     * **Linux 上两档没有区别**（GTK 只有开/关）。
     */
    requestUserAttention(requestType = null) {
        return invoke('window.requestUserAttention', {
            label: this.label,
            type: requestType === UserAttentionType.Critical
                ? 'critical'
                : requestType === UserAttentionType.Informational
                    ? 'informational'
                    : null,
        });
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
     * @returns {() => void} 退订函数（**同步**返回，比 Tauri 的 Promise 更方便：
     * `await` 一个非 Promise 值同样可用，所以迁移代码不用改）。
     */
    listen(event, handler) {
        return listen(event, handler);
    }

    /**
     * 订阅一次；收到第一条后自动退订。
     *
     * @param {string} event
     * @param {(event: { event: string, id: number, payload: any }) => void} handler
     * @returns {() => void} 退订函数（在触发前调用可以取消；**同步**返回，同上）。
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

    /** 客户区尺寸（逻辑像素）。需要 `window.innerSize` 被允许。 */
    async innerSize() {
        return toLogicalSize(await invoke('window.innerSize', { label: this.label }));
    }

    /**
     * 整窗尺寸（逻辑像素）。
     *
     * @remarks
     * Linux/Wayland 下可能与 `innerSize()` 相等——合成器不告诉客户端装饰尺寸。
     * 需要 `window.outerSize` 被允许。
     */
    async outerSize() {
        return toLogicalSize(await invoke('window.outerSize', { label: this.label }));
    }

    /** 窗口左上角（屏幕逻辑坐标）。需要 `window.outerPosition` 被允许。 */
    async outerPosition() {
        return toLogicalPosition(await invoke('window.outerPosition', { label: this.label }));
    }

    /**
     * 客户区左上角（屏幕逻辑坐标）。
     *
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
     * @param {(event: { event: string, id: number, payload: any, preventDefault: () => void }) => void} handler
     * @returns {Promise<() => void>} 退订函数（`await` 拿到，与 Tauri 一致）。
     * @remarks
     * **与 Tauri 的关键差别**：Tauri 在回调里调 `event.preventDefault()` 就能阻止关闭；
     * 本库是**反向**的——宿主挂起关闭、等页面决定，页面**同意**才调 `destroy()`：
     *
     * ```js
     * const un = await getCurrentWindow().onCloseRequested(async () => {
     *   if (await confirmDiscard()) {
     *     await getCurrentWindow().destroy();   // 同意关闭
     *   }
     *   // 不同意：什么都不做，窗口留在打开状态
     * });
     * ```
     *
     * 为什么不做成 Tauri 那样：Windows 的 `WM_CLOSE` 是**同步消息**、页面回执是异步 IPC，
     * 宿主没法"等你回调跑完再决定"。所以只能让页面主动补上关闭那一步。
     *
     * 传进来的 `event.preventDefault` 是个**空操作**——它存在只是为了让
     * `event.preventDefault()` 这种写法不报错。真正的"阻止"是"什么都不做"。
     * 这一点必须写清楚，否则迁移代码会以为调了它就等于阻止了。
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
        try {
            await invoke('window.setCloseIntercept', { label: this.label, enabled: true });
        } catch (error) {
            console.error(
                '@orielweb/api/window: 无法打开关闭接管，onCloseRequested 的回调不会触发。' +
                `原因：${errorText(error)}。` +
                '请在宿主侧允许 `window.setCloseIntercept`，或用 OrielWindowOptions.InterceptClose 静态开启。');
        }

        const unlisten = listen(TauriEvent.WINDOW_CLOSE_REQUESTED, (event) => {
            handler({
                event: event.event,
                id: event.id,
                payload: event.payload,
                // 空操作：见上面的说明。保留它是为了让迁移代码的 `event.preventDefault()`
                // 不抛 TypeError——那会让一个本来只是"语义不同"的差异变成崩溃。
                preventDefault: () => {},
            });
        });

        return async () => {
            unlisten();

            // 退订最后一个监听者时把接管关掉：留着它会让窗口在没人处理的情况下
            // 点关闭没反应（挂起后没人 destroy）。这是"订阅驱动"该有的收尾。
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
 * 有焦点的窗口。
 *
 * @returns {Promise<Window | null>}
 * @remarks
 * **目前恒返回 `null`**：三平台都还没有"哪个窗口有焦点"的**查询**能力
 * （宿主的 `FocusChanged` 只上报变化，不记录状态）。保留签名是为了让迁移代码
 * 能原样编译，但要明确它现在给不出答案——而不是假装"没有窗口有焦点"。
 */
export async function getFocusedWindow() {
    return null;
}
