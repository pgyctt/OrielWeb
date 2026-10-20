// 对标 `@tauri-apps/api/tray` 的**托盘模块**。
//
// 与 Tauri 的对应关系与差别（如实标注，不假装一致）：
//
//   1. **句柄是 id 字符串，不是 Resource**。Tauri 的 TrayIcon extends Resource（Rust 侧句柄表）；
//      本库没有句柄表，页面持有的就是一个 id，宿主侧的对象经 `tray.*` 命令寻址。
//      由此派生：`rid` 是 `id` 的别名（补壳——迁移代码里的 `tray.rid` 不会报错，
//      但那不是资源句柄；debug 模式下调它会打一条警告）。
//
//   2. **图标走 `window.setIcon` 同一套 JsImage 形状**：路径（string）/ PNG·ICO 字节 /
//      Image 实例（含裸 RGBA）。宿主解码（OrielImageCodec），三平台一致。
//
//   3. **`setTempDirPath` 是补壳**：那是 Tauri 在 Linux 上"图标要落盘给 AppIndicator 读"
//      的约束带来的 API；本库走 GtkStatusIcon 直吃 pixbuf，不需要临时目录。
//      调用不报错（零删改），但 debug 模式下会打警告——它对本库没有任何作用。
//
//   4. **`setMenuOnLeftClick` / `setShowMenuOnLeftClick` 只在 Windows 生效**：
//      Tauri 标注 Linux Unsupported（平台惯例左键就弹菜单）；本库 macOS 后端同样是
//      平台惯例行为，暂未接通开关，非 Windows 平台抛 `not_supported`——
//      宁可当场报错，也不让"关掉了左键菜单"变成一句空话。
//
//   5. **菜单项的 `action` 回调**复用 `menu.itemclicked` 事件通路：带 action 的项在
//      `setMenu` 时被加上本托盘专属的 id 前缀，点击回来后按前缀认领。
//      与 menu.js 的规矩一样：action 只对**有 id 的项**有效。

import { invoke } from './core.js';
import { getTransport } from './internal.js';
import { toImageBytes, bytesToBase64, assertImageFormat } from './image-bytes.js';
import { __setDebugWarnings, warnCompat } from './internal.js';

/** 托盘事件名（宿主侧 OrielApp.TrayEventName）。 */
const TRAY_EVENT = 'tray.event';

/** 菜单项点击事件名（宿主侧 OrielApp.MenuItemClickedEventName）。 */
const ITEM_CLICKED_EVENT = 'menu.itemclicked';

// 补壳警告工具统一在 internal.js（B14）：tray 的 rid/setTempDirPath 与 Image.close/
// Menu.rid/WebviewWindow.setAutoResize 共用同一套开关与文案前缀。
export { __setDebugWarnings };

/** 鸭子类型认 Image 实例（与 window.js 同一套判据：isOrielImage 标记）。 */
/** @param {unknown} value */
function isImageLike(value) {
    return value !== null
        && typeof value === 'object'
        && (/** @type {Record<string, unknown>} */ (value)).isOrielImage === true;
}

/**
 * 把 `JsImage` 编成 `tray.setIcon` 能吃的参数（与 `window.setIcon` 三形态一致）。
 * @param {unknown} icon
 * @returns {Record<string, unknown>}
 */
function encodeIcon(icon) {
    if (typeof icon === 'string') {
        if (icon === '') {
            throw new TypeError('setIcon: 路径不能是空字符串');
        }
        return { iconPath: icon };
    }

    if (isImageLike(icon)) {
        const image = /** @type {any} */ (icon);
        if (image.hasEncodedBytes === false) {
            return {
                rgba: bytesToBase64(image.rgbaBytes),
                width: image.width,
                height: image.height,
            };
        }
        return { bytes: bytesToBase64(image.bytes) };
    }

    const bytes = toImageBytes(icon);
    if (bytes !== null) {
        assertImageFormat(bytes, 'setIcon');
        return { bytes: bytesToBase64(bytes) };
    }

    throw new TypeError(
        'setIcon: icon 必须是文件路径（string）、Uint8Array / ArrayBuffer / number[] 形式的' +
        'PNG/ICO 字节，或 @orielweb/api/image 的 Image 实例。');
}

/**
 * 把一棵菜单树编成 `tray.setMenu` 的 `items`（线格式与 menu.js 的 wire 一致）。
 *
 * 带 `action` 的项会获得 `tray:<token>:<前id>` 形式的 id 前缀，点击回来后按前缀认领。
 * @param {any} menu Menu / Submenu 实例（或已经是线格式的数组）。
 * @returns {{ items: unknown[], actions: Map<string, Function>, token: string }}
 */
function wireMenu(menu) {
    const actions = new Map();
    const token = 'tray' + Math.random().toString(36).slice(2, 10);

    if (menu === null || menu === undefined) {
        return { items: [], actions, token };
    }

    // 已经是线格式数组（或 Menu 实例的 _items）
    const rawItems = Array.isArray(menu)
        ? menu
        : Array.isArray(menu._items)
            ? menu._items
            : [menu];

    const items = rawItems.map((/** @type {any} */ item) => {
        if (item && typeof item._wire === 'function') {
            return item._wire(actions, token);
        }
        return item;
    });

    return { items, actions, token };
}

/** 认领路由表：token → (原始id → 回调)。多托盘并存，按 token 索引。 */
const clickRouting = new Map();
/** 当前监听器挂在哪个 transport 上（mocks 切换后必须重装，否则点击永远不触发）。 */
/** @type {any} */
let clickListenerTransport = null;
/** @type {(() => void) | null} */
let clickListenerUnsubscribe = null;

async function ensureClickListener() {
    const transport = getTransport();
    if (clickListenerTransport === transport) {
        return;
    }
    // 换了 transport（测试里换 mock、或页面自己 setTransportOverride）：旧监听器已无意义
    if (clickListenerUnsubscribe) {
        clickListenerUnsubscribe();
    }
    clickListenerTransport = transport;
    const on = transport.on;
    if (typeof on !== 'function') {
        throw new Error('orielweb/tray: 传输层不支持 on()——无法订阅托盘菜单点击');
    }
    clickListenerUnsubscribe = on.call(transport, ITEM_CLICKED_EVENT, (/** @type {any} */ value) => {
        const id = value && typeof value === 'object' ? value.id : null;
        if (typeof id !== 'string') {
            return;
        }
        // wire 出来的 id 形如 `<token>:<原始id>`；token 由本模块生成（tray 前缀）。
        const sep = id.indexOf(':');
        if (sep <= 0) {
            return;
        }
        const token = id.slice(0, sep);
        const originalId = id.slice(sep + 1);
        const route = clickRouting.get(token);
        const handler = route ? route.get(originalId) : undefined;
        if (typeof handler === 'function') {
            try { handler(originalId); }
            catch (err) { console.error('tray 菜单 action 处理器抛出', err); }
        }
    });
}

/**
 * 系统托盘图标（对标 Tauri 2 的 `TrayIcon`）。
 *
 * 用 {@linkcode TrayIcon.new} 创建；本库中它**不是资源句柄**（见文件头说明）。
 *
 * @example
 * ```js
 * import { TrayIcon } from '@orielweb/api/tray';
 * const tray = await TrayIcon.new({ tooltip: 'awesome tray tooltip' });
 * await tray.setTooltip('new tooltip');
 * ```
 */
export class TrayIcon {
    /**
     * @param {string} id 宿主侧的托盘 id（页面持有的唯一标识）。
     * @internal 请用 {@linkcode TrayIcon.new}。
     */
    constructor(id) {
        /** 本托盘的 id。 */
        this.id = id;
    }

    /**
     * 资源句柄的**补壳**：本库没有资源表，`rid` 就是 `id`。
     *
     * 迁移代码里的 `tray.rid` 不会报错，但它不是 Rust 侧句柄——
     * debug 模式下调用会打一条警告（补壳必须可观测）。
     */
    get rid() {
        warnCompat('TrayIcon.rid 是补壳：本库没有资源表，返回值就是 id。');
        return this.id;
    }

    /**
     * 按 id 取回句柄（不存在时返回 null）。
     *
     * @param {string} id
     * @returns {Promise<TrayIcon | null>}
     */
    static async getById(id) {
        if (typeof id !== 'string' || id === '') {
            throw new TypeError('TrayIcon.getById: id 必须为非空字符串');
        }
        const found = await invoke('tray.getById', { id });
        return found === null || found === undefined ? null : new TrayIcon(found);
    }

    /**
     * 按 id 移除托盘（图标随之消失）。
     *
     * @param {string} id
     * @returns {Promise<boolean>} 是否确实移除了一个。
     */
    static async removeById(id) {
        if (typeof id !== 'string' || id === '') {
            throw new TypeError('TrayIcon.removeById: id 必须为非空字符串');
        }
        return invoke('tray.removeById', { id });
    }

    /**
     * 新建托盘图标。
     *
     * @param {{ id?: string, menu?: any, icon?: any, tooltip?: string, title?: string,
     *   tempDirPath?: string, iconAsTemplate?: boolean, menuOnLeftClick?: boolean,
     *   showMenuOnLeftClick?: boolean, action?: (event: any) => void }} [options]
     * @returns {Promise<TrayIcon>}
     *
     * #### 平台差异
     * - **Linux**：有时要设了菜单图标才可见（Tauri 同）；`showMenuOnLeftClick` 无效。
     * - **Windows**：`title` 不支持（平台无此概念）。
     * - **macOS**：`iconAsTemplate` 决定菜单栏图标的模板着色。
     */
    static async new(options = {}) {
        const opts = options ?? {};

        const args = /** @type {Record<string, unknown>} */ ({
            id: typeof opts.id === 'string' && opts.id !== '' ? opts.id : null,
            tooltip: typeof opts.tooltip === 'string' ? opts.tooltip : null,
            title: typeof opts.title === 'string' ? opts.title : null,
            iconAsTemplate: opts.iconAsTemplate !== false,
            menuOnClick: opts.showMenuOnLeftClick ?? opts.menuOnLeftClick ?? false,
        });

        if (opts.icon !== undefined && opts.icon !== null) {
            Object.assign(args, encodeIcon(opts.icon));
        }

        // 菜单：先装配 action 路由再发命令（菜单可能立刻被用户点）
        if (opts.menu !== undefined && opts.menu !== null) {
            const wired = wireMenu(opts.menu);
            await ensureClickListener();
            clickRouting.set(wired.token, wired.actions);
            args.items = wired.items;
        }

        if (opts.tempDirPath !== undefined) {
            warnCompat('TrayIconOptions.tempDirPath 是补壳：本库不落盘图标，该参数不生效。');
        }

        const id = await invoke('tray.new', args);

        const tray = new TrayIcon(id);

        if (typeof opts.action === 'function') {
            tray.onEvent(opts.action);
        }

        return tray;
    }

    /**
     * 订阅本托盘的事件（对标 Tauri 的 `TrayIconOptions.action`，也可在 `new` 后随时加）。
     *
     * handler 收到 `{ id, type, button, buttonState, position, rect }`；
     * 平台未提供的字段为 `null`（Linux 的 button、macOS 的 position 等）。
     *
     * @param {(event: any) => void} handler
     * @returns {() => void} 取消订阅函数。
     */
    onEvent(handler) {
        if (typeof handler !== 'function') {
            throw new TypeError('TrayIcon.onEvent: 需要一个函数');
        }
        const transport = getTransport();
        const on = transport.on;
        if (typeof on !== 'function') {
            throw new Error('orielweb/tray: 传输层不支持 on()——无法订阅托盘事件');
        }
        const off = on.call(transport, TRAY_EVENT, (/** @type {any} */ value) => {
            if (value && value.id === this.id) {
                try { handler(value); }
                catch (err) { console.error('TrayIcon.onEvent 处理器抛出', err); }
            }
        });
        return off;
    }

    /**
     * 设置新图标；`null` 移除图标。
     *
     * @param {any} icon
     * @returns {Promise<void>}
     */
    setIcon(icon) {
        if (icon === null || icon === undefined) {
            return invoke('tray.setIcon', { id: this.id, iconPath: null });
        }
        return invoke('tray.setIcon', { id: this.id, ...encodeIcon(icon) });
    }

    /**
     * 设置托盘菜单。
     *
     * @param {any} menu
     * @returns {Promise<void>}
     *
     * #### 平台差异
     * - **Linux**：菜单一旦设置就移除不掉，`null` 只清空项（Tauri 同）。
     */
    async setMenu(menu) {
        const wired = wireMenu(menu);
        await ensureClickListener();
        clickRouting.set(wired.token, wired.actions);
        await invoke('tray.setMenu', { id: this.id, items: wired.items });
    }

    /**
     * 设置悬停提示。
     *
     * @param {string | null} tooltip
     * @returns {Promise<void>}
     *
     * #### 平台差异
     * - **Linux**：不支持（GtkStatusIcon 无 tooltip 之外的等价物时行为各异）。
     */
    setTooltip(tooltip) {
        return invoke('tray.setTooltip', {
            id: this.id,
            tooltip: typeof tooltip === 'string' ? tooltip : null,
        });
    }

    /**
     * 设置图标旁的标题文本。
     *
     * @param {string | null} title
     * @returns {Promise<void>}
     *
     * #### 平台差异
     * - **Windows**：不支持。
     * - **Linux**：只有同时有图标时才显示。
     */
    setTitle(title) {
        return invoke('tray.setTitle', {
            id: this.id,
            title: typeof title === 'string' ? title : null,
        });
    }

    /**
     * 显示/隐藏本托盘图标。
     *
     * @param {boolean} visible
     * @returns {Promise<void>}
     */
    setVisible(visible) {
        return invoke('tray.setVisible', { id: this.id, visible: visible !== false });
    }

    /**
     * 把当前图标设为 macOS 的模板图像（随菜单栏明暗自动着色）。
     *
     * @param {boolean} asTemplate
     * @returns {Promise<void>}
     *
     * #### 平台差异
     * - **macOS 专属**；其他平台接受参数但不生效（与 Tauri 一致）。
     */
    setIconAsTemplate(asTemplate) {
        return invoke('tray.setIconAsTemplate', {
            id: this.id,
            asTemplate: asTemplate === true,
        });
    }

    /**
     * 原子地"换图标 + 改模板标记"（Tauri 为了避免两次调用之间闪一下旧模板态）。
     *
     * 本库是两次调用的组合（宿主没有原子入口），短暂闪烁理论上可能出现；
     * 与逐次调 `setIcon` + `setIconAsTemplate` 的差别只在顺序保证。
     *
     * @param {any} icon
     * @param {boolean} asTemplate
     * @returns {Promise<void>}
     */
    async setIconWithAsTemplate(icon, asTemplate) {
        await this.setIcon(icon);
        await this.setIconAsTemplate(asTemplate);
    }

    /**
     * 开关"左键点击弹出菜单"（`setShowMenuOnLeftClick` 的旧名，Tauri 已弃用同款）。
     *
     * @param {boolean} onLeft
     * @returns {Promise<void>}
     * @deprecated 用 {@linkcode TrayIcon.setShowMenuOnLeftClick}。
     */
    setMenuOnLeftClick(onLeft) {
        return this.setShowMenuOnLeftClick(onLeft);
    }

    /**
     * 开关"左键点击弹出菜单"（默认开）。
     *
     * 关掉后左键点击交给你自己的 `action` 处理器，右键仍弹菜单。
     *
     * @param {boolean} onLeft
     * @returns {Promise<void>}
     *
     * #### 平台差异
     * - **Windows**：生效。
     * - **Linux / macOS**：抛 `not_supported`——那两个平台"左键弹菜单"是平台惯例，
     *   本库后端暂未接通开关（Tauri 标注 Linux Unsupported；macOS 有 API 而我们没接）。
     */
    setShowMenuOnLeftClick(onLeft) {
        return invoke('tray.setMenuOnLeftClick', {
            id: this.id,
            onLeft: onLeft !== false,
        });
    }

    /**
     * 设置图标临时目录。**Linux 专属补壳**：本库不落盘图标，调用不报错但不生效。
     *
     * @param {string | null} _path
     * @returns {Promise<void>}
     */
    setTempDirPath(_path) {
        warnCompat('TrayIcon.setTempDirPath 是补壳：本库不落盘图标，该调用不生效。');
        return Promise.resolve();
    }

    /**
     * 移除本托盘（等价 `TrayIcon.removeById(this.id)`）。
     *
     * @returns {Promise<boolean>}
     */
    close() {
        return TrayIcon.removeById(this.id);
    }
}
