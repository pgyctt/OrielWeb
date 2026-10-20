export { __setDebugWarnings };
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
     * 按 id 取回句柄（不存在时返回 null）。
     *
     * @param {string} id
     * @returns {Promise<TrayIcon | null>}
     */
    static getById(id: string): Promise<TrayIcon | null>;
    /**
     * 按 id 移除托盘（图标随之消失）。
     *
     * @param {string} id
     * @returns {Promise<boolean>} 是否确实移除了一个。
     */
    static removeById(id: string): Promise<boolean>;
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
    static "new"(options?: {
        id?: string;
        menu?: any;
        icon?: any;
        tooltip?: string;
        title?: string;
        tempDirPath?: string;
        iconAsTemplate?: boolean;
        menuOnLeftClick?: boolean;
        showMenuOnLeftClick?: boolean;
        action?: (event: any) => void;
    }): Promise<TrayIcon>;
    /**
     * @param {string} id 宿主侧的托盘 id（页面持有的唯一标识）。
     * @internal 请用 {@linkcode TrayIcon.new}。
     */
    constructor(id: string);
    /** 本托盘的 id。 */
    id: string;
    /**
     * 资源句柄的**补壳**：本库没有资源表，`rid` 就是 `id`。
     *
     * 迁移代码里的 `tray.rid` 不会报错，但它不是 Rust 侧句柄——
     * debug 模式下调用会打一条警告（补壳必须可观测）。
     */
    get rid(): string;
    /**
     * 订阅本托盘的事件（对标 Tauri 的 `TrayIconOptions.action`，也可在 `new` 后随时加）。
     *
     * handler 收到 `{ id, type, button, buttonState, position, rect }`；
     * 平台未提供的字段为 `null`（Linux 的 button、macOS 的 position 等）。
     *
     * @param {(event: any) => void} handler
     * @returns {() => void} 取消订阅函数。
     */
    onEvent(handler: (event: any) => void): () => void;
    /**
     * 设置新图标；`null` 移除图标。
     *
     * @param {any} icon
     * @returns {Promise<void>}
     */
    setIcon(icon: any): Promise<void>;
    /**
     * 设置托盘菜单。
     *
     * @param {any} menu
     * @returns {Promise<void>}
     *
     * #### 平台差异
     * - **Linux**：菜单一旦设置就移除不掉，`null` 只清空项（Tauri 同）。
     */
    setMenu(menu: any): Promise<void>;
    /**
     * 设置悬停提示。
     *
     * @param {string | null} tooltip
     * @returns {Promise<void>}
     *
     * #### 平台差异
     * - **Linux**：不支持（GtkStatusIcon 无 tooltip 之外的等价物时行为各异）。
     */
    setTooltip(tooltip: string | null): Promise<void>;
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
    setTitle(title: string | null): Promise<void>;
    /**
     * 显示/隐藏本托盘图标。
     *
     * @param {boolean} visible
     * @returns {Promise<void>}
     */
    setVisible(visible: boolean): Promise<void>;
    /**
     * 把当前图标设为 macOS 的模板图像（随菜单栏明暗自动着色）。
     *
     * @param {boolean} asTemplate
     * @returns {Promise<void>}
     *
     * #### 平台差异
     * - **macOS 专属**；其他平台接受参数但不生效（与 Tauri 一致）。
     */
    setIconAsTemplate(asTemplate: boolean): Promise<void>;
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
    setIconWithAsTemplate(icon: any, asTemplate: boolean): Promise<void>;
    /**
     * 开关"左键点击弹出菜单"（`setShowMenuOnLeftClick` 的旧名，Tauri 已弃用同款）。
     *
     * @param {boolean} onLeft
     * @returns {Promise<void>}
     * @deprecated 用 {@linkcode TrayIcon.setShowMenuOnLeftClick}。
     */
    setMenuOnLeftClick(onLeft: boolean): Promise<void>;
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
    setShowMenuOnLeftClick(onLeft: boolean): Promise<void>;
    /**
     * 设置图标临时目录。**Linux 专属补壳**：本库不落盘图标，调用不报错但不生效。
     *
     * @param {string | null} _path
     * @returns {Promise<void>}
     */
    setTempDirPath(_path: string | null): Promise<void>;
    /**
     * 移除本托盘（等价 `TrayIcon.removeById(this.id)`）。
     *
     * @returns {Promise<boolean>}
     */
    close(): Promise<boolean>;
}
import { __setDebugWarnings } from './internal.js';
