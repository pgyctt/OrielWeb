/**
 * 把 Tauri 的 rid 元组还原成菜单项实例（对标 Tauri 从 menu 模块转出的同名函数）。
 *
 * @param {[number, string, string]} tuple `[rid, id, kind]`
 * @returns {MenuItemBase}
 * @remarks
 * **本库没有 rid**，所以这个函数只按 `kind` 造一个空项（只有 id，没有文本）。
 * Tauri 自己的 `Menu.items()` 用它把 Rust 侧的结果还原成实例；本库的 `items()`
 * 直接返回本地实例，不需要它。保留这个导出是为了让 `import { itemFromKind }` 不炸。
 */
export function itemFromKind([, id, kind]: [number, string, string]): MenuItemBase;
/**
 * 普通菜单项（对标 `MenuItem`）。
 */
export class MenuItem extends MenuItemBase {
    /**
     * @param {{ id?: string, text: string, enabled?: boolean, accelerator?: string, action?: (id: string) => void }} [options]
     * @returns {Promise<MenuItem>}
     */
    static "new"(options?: {
        id?: string;
        text: string;
        enabled?: boolean;
        accelerator?: string;
        action?: (id: string) => void;
    }): Promise<MenuItem>;
    /**
     * @param {{ id?: string, text: string, enabled?: boolean, accelerator?: string, action?: (id: string) => void }} options
     */
    constructor(options: {
        id?: string;
        text: string;
        enabled?: boolean;
        accelerator?: string;
        action?: (id: string) => void;
    });
    /** @type {string | null} */
    _accelerator: string | null;
    /**
     * 显示文本（对标 `MenuItem.text`）。
     *
     * @returns {Promise<string>}
     * @remarks 这些方法**不在基类上**：Tauri 的 `Menu` 是个容器，没有 `text()`；
     * `PredefinedMenuItem` 只有 `text`/`setText`。放在基类上会让它们凭空多出
     * "看起来能用、语义却不成立"的方法（对 `Menu` 调 `setEnabled()` 是什么意思？）。
     */
    text(): Promise<string>;
    /**
     * 设置显示文本（对标 `MenuItem.setText`）。
     *
     * @param {string} text
     * @returns {Promise<void>}
     */
    setText(text: string): Promise<void>;
    /**
     * 是否可用（对标 `MenuItem.isEnabled`）。
     *
     * @returns {Promise<boolean>}
     */
    isEnabled(): Promise<boolean>;
    /**
     * 设置是否可用（对标 `MenuItem.setEnabled`）。
     *
     * @param {boolean} enabled
     * @returns {Promise<void>}
     */
    setEnabled(enabled: boolean): Promise<void>;
    /**
     * 设置加速键（对标 `MenuItem.setAccelerator`）。
     *
     * @param {string | null} accelerator `null` 表示移除。
     * @returns {Promise<void>}
     * @remarks Tauri 没有 `accelerator()` 这个读接口——只有 setter。
     * 所以本库也不给：多一个 getter 就是一处自造的 API 面。
     */
    setAccelerator(accelerator: string | null): Promise<void>;
}
/**
 * 勾选菜单项（对标 `CheckMenuItem`）。
 */
export class CheckMenuItem extends MenuItem {
    /**
     * @override
     * @param {{ id?: string, text: string, enabled?: boolean, checked?: boolean, accelerator?: string, action?: (id: string) => void }} [options]
     * @returns {Promise<CheckMenuItem>}
     */
    static override "new"(options?: {
        id?: string;
        text: string;
        enabled?: boolean;
        checked?: boolean;
        accelerator?: string;
        action?: (id: string) => void;
    }): Promise<CheckMenuItem>;
    /**
     * @param {{ id?: string, text: string, enabled?: boolean, checked?: boolean, accelerator?: string, action?: (id: string) => void }} options
     */
    constructor(options: {
        id?: string;
        text: string;
        enabled?: boolean;
        checked?: boolean;
        accelerator?: string;
        action?: (id: string) => void;
    });
    /** @type {boolean} */
    _checked: boolean;
    /** @returns {Promise<boolean>} 是否勾选。 */
    isChecked(): Promise<boolean>;
    /**
     * @param {boolean} checked
     * @returns {Promise<void>}
     */
    setChecked(checked: boolean): Promise<void>;
}
/**
 * 带图标的菜单项（对标 `IconMenuItem`）。
 *
 * @remarks **图标不会显示**：本库没有图像资源表，`setIcon` 也没实现。
 * 保留这个类是为了让 `import { IconMenuItem }` 与 `new IconMenuItem({...})` 能跑，
 * 菜单项按普通文本项画出来。
 */
export class IconMenuItem extends MenuItem {
    /**
     * @override
     * @param {{ id?: string, text: string, enabled?: boolean, accelerator?: string, icon?: unknown, action?: (id: string) => void }} [options]
     * @returns {Promise<IconMenuItem>}
     */
    static override "new"(options?: {
        id?: string;
        text: string;
        enabled?: boolean;
        accelerator?: string;
        icon?: unknown;
        action?: (id: string) => void;
    }): Promise<IconMenuItem>;
    /**
     * @param {{ id?: string, text: string, enabled?: boolean, accelerator?: string, icon?: unknown, action?: (id: string) => void }} [options]
     */
    constructor(options?: {
        id?: string;
        text: string;
        enabled?: boolean;
        accelerator?: string;
        icon?: unknown;
        action?: (id: string) => void;
    });
    /** @type {unknown} */
    _icon: unknown;
    /**
     * @param {unknown} _icon
     * @returns {Promise<void>}
     * @throws {Error} 本库暂不支持菜单图标（没有图像资源表）。
     */
    setIcon(_icon: unknown): Promise<void>;
}
/**
 * 预定义（系统标准）菜单项（对标 `PredefinedMenuItem`）。
 */
export class PredefinedMenuItem extends MenuItemBase {
    /**
     * @param {PredefinedMenuItemOptions} [options]
     * @returns {Promise<PredefinedMenuItem>}
     */
    static "new"(options?: PredefinedMenuItemOptions): Promise<PredefinedMenuItem>;
    /**
     * @param {PredefinedMenuItemOptions} [options]
     */
    constructor(options?: PredefinedMenuItemOptions);
    /** @type {string | { About: unknown }} */
    _item: string | {
        About: unknown;
    };
    /**
     * 显示文本（对标 `PredefinedMenuItem.text`）。
     *
     * @returns {Promise<string>}
     * @remarks 预定义项**只有** `text` / `setText`——没有 `isEnabled` / `setEnabled`。
     * 这不是漏了：Tauri 那边也没有（预定义项的可用性由平台自己决定，
     * 例如没有选区时 `Copy` 自动灰掉）。
     */
    text(): Promise<string>;
    /**
     * 设置显示文本（对标 `PredefinedMenuItem.setText`）。
     *
     * @param {string} text
     * @returns {Promise<void>}
     */
    setText(text: string): Promise<void>;
}
/**
 * @typedef {object} PredefinedMenuItemOptions
 * @property {string} [text] 显示文本；省略时用平台自己的标准文案。
 * @property {string | { About: unknown }} [item] 预定义项类型。
 * @property {boolean} [enabled]
 * @property {string} [id]
 * @property {((id: string) => void)} [action]
 */
/**
 * 子菜单（对标 `Submenu`）。
 */
export class Submenu extends MenuItemBase {
    /**
     * @param {{ id?: string, text: string, enabled?: boolean, items?: any[] }} [options]
     * @returns {Promise<Submenu>}
     */
    static "new"(options?: {
        id?: string;
        text: string;
        enabled?: boolean;
        items?: any[];
    }): Promise<Submenu>;
    /**
     * @param {{ id?: string, text: string, enabled?: boolean, items?: any[] }} options
     */
    constructor(options: {
        id?: string;
        text: string;
        enabled?: boolean;
        items?: any[];
    });
    /** @type {MenuItemBase[]} */
    _items: MenuItemBase[];
    /** @returns {Promise<string>} 显示文本。 */
    text(): Promise<string>;
    /**
     * @param {string} text
     * @returns {Promise<void>}
     */
    setText(text: string): Promise<void>;
    /** @returns {Promise<boolean>} 是否可用。 */
    isEnabled(): Promise<boolean>;
    /**
     * @param {boolean} enabled
     * @returns {Promise<void>}
     */
    setEnabled(enabled: boolean): Promise<void>;
    /**
     * @param {any} items 一项或多项。
     * @returns {Promise<void>}
     */
    append(items: any): Promise<void>;
    /**
     * @param {any} items
     * @returns {Promise<void>}
     */
    prepend(items: any): Promise<void>;
    /**
     * 在指定位置插入（对标 `Submenu.insert`）。
     *
     * @param {any} items 一项或多项。
     * @param {number} position 插入位置；越界按两端处理。
     * @returns {Promise<void>}
     * @remarks 注意参数顺序是 **(items, position)**——Tauri 就是这么定义的。
     * 反过来写不会报错（数字会被当成一项菜单项），但会在 `toItem` 里抛 TypeError，
     * 所以顺序错了至少是当场炸，而不是静默插到奇怪的地方。
     */
    insert(items: any, position: number): Promise<void>;
    /**
     * 移除一项（对标 `Submenu.remove`）。
     *
     * @param {any} item 要移除的项**对象**（不是 id）。
     * @returns {Promise<void>}
     * @remarks Tauri 收的是项对象，因为它能按 rid 认。本库按**对象身份**认
     * （`indexOf`），传一个"内容相同但不是同一个实例"的对象不会命中——
     * 与 Tauri 的 rid 语义一致：换一个实例就是换了一个菜单项。
     */
    remove(item: any): Promise<void>;
    /**
     * 移除指定位置的项（对标 `Submenu.removeAt`）。
     *
     * @param {number} position
     * @returns {Promise<MenuItemBase | null>} 被移除的项；位置越界时 `null`。
     */
    removeAt(position: number): Promise<MenuItemBase | null>;
    /** @returns {Promise<MenuItemBase[]>} 子项（副本，改它不影响这里）。 */
    items(): Promise<MenuItemBase[]>;
    /**
     * 按 id 取项（对标 `Submenu.get`）。
     *
     * @param {string} id
     * @returns {Promise<MenuItemBase | null>}
     * @remarks 只找**直接子项**，不递归——Tauri 的 `get` 也是查它自己那一层。
     */
    get(id: string): Promise<MenuItemBase | null>;
    /**
     * 把这个子菜单设为 macOS 的 Window 菜单（对标 `Submenu.setAsWindowsMenuForNSApp`）。
     *
     * @returns {Promise<never>}
     * @throws {Error} 未实现（属于菜单栏批次 B6b）。
     */
    setAsWindowsMenuForNSApp(): Promise<never>;
    /**
     * 把这个子菜单设为 macOS 的 Help 菜单（对标 `Submenu.setAsHelpMenuForNSApp`）。
     *
     * @returns {Promise<never>}
     * @throws {Error} 未实现（属于菜单栏批次 B6b）。
     */
    setAsHelpMenuForNSApp(): Promise<never>;
    /**
     * 设置子菜单图标（对标 `Submenu.setIcon`）。
     *
     * @param {unknown} _icon
     * @returns {Promise<never>}
     * @throws {Error} 本库没有菜单图标的资源通道。
     */
    setIcon(_icon: unknown): Promise<never>;
    /**
     * @param {{ type?: string, x: number, y: number } | { x: number, y: number } | [number, number] | null} [at]
     * @param {{ label?: string } | null} [window]
     * @returns {Promise<void>}
     */
    popup(at?: {
        type?: string;
        x: number;
        y: number;
    } | {
        x: number;
        y: number;
    } | [number, number] | null, window?: {
        label?: string;
    } | null): Promise<void>;
    /**
     * 关掉当前正在显示的菜单（**本库扩展**，Tauri 没有这个方法）。
     *
     * @param {{ label?: string } | null} [window] 菜单弹在哪个窗口上；省略时是发起者。
     * @returns {Promise<void>}
     * @remarks
     * <p>为什么必须给：<b>Windows 的 `popup()` 会阻塞到用户选择</b>，UI 线程被占着时页面上的
     * 按钮点不到。没有这条命令，页面就<b>没有办法用代码关掉自己弹的菜单</b>
     * （Tauri 那边菜单是资源，Rust 侧可以在别的线程上结束它）。
     * <p>没有菜单在屏上是空操作，不报错——重复调用是安全的。
     */
    close(window?: {
        label?: string;
    } | null): Promise<void>;
}
/**
 * 菜单（对标 `Menu`）：一个菜单项的容器，可 popup，也可装成菜单栏。
 *
 * @remarks
 * `setAsAppMenu` / `setAsWindowMenu` 装的是**整棵树的快照**：装完之后再改这个 `Menu`
 * 不会影响已经在屏上的菜单栏，要改得再设一次（本库没有资源表，无法增量改宿主那份）。
 */
export class Menu extends MenuItemBase {
    /**
     * @param {{ id?: string, items?: any[] }} [options]
     * @returns {Promise<Menu>}
     */
    static "new"(options?: {
        id?: string;
        items?: any[];
    }): Promise<Menu>;
    /**
     * 默认应用菜单（对标 `Menu.default`）。
     *
     * @returns {Promise<Menu>}
     * @remarks
     * **本库给的是"内容对得上、但只能弹出来"的版本**：Tauri 的 `Menu.default()` 造的是
     * 三平台各自的**标准菜单栏**（macOS 有以应用名命名的子菜单与 View 菜单，
     * Windows 有 File 菜单，Linux 没有 File）。那些结构属于菜单栏（B6b），
     * 本批没有 `setAsAppMenu` / `setAsWindowMenu`，所以这里返回的菜单
     * 只能用于 `popup()`。
     *
     * 内容按 Tauri 文档描述的那三组造：Edit / Window / Help。
     * 应用名那一项要用 `OrielApp` 的信息，页面侧拿不到——所以 macOS 的应用子菜单
     * 这里不造，等 B6b 由宿主补。
     */
    static default(): Promise<Menu>;
    /**
     * @param {{ id?: string, items?: any[] }} [options]
     */
    constructor(options?: {
        id?: string;
        items?: any[];
    });
    /** @type {MenuItemBase[]} */
    _items: MenuItemBase[];
    /**
     * @param {any} items
     * @returns {Promise<void>}
     */
    append(items: any): Promise<void>;
    /**
     * @param {any} items
     * @returns {Promise<void>}
     */
    prepend(items: any): Promise<void>;
    /**
     * 在指定位置插入（对标 `Menu.insert`）。
     *
     * @param {any} items 一项或多项。
     * @param {number} position 插入位置；越界按两端处理。
     * @returns {Promise<void>}
     */
    insert(items: any, position: number): Promise<void>;
    /**
     * 移除一项（对标 `Menu.remove`）。
     *
     * @param {any} item 要移除的项**对象**（不是 id）；按对象身份匹配。
     * @returns {Promise<void>}
     */
    remove(item: any): Promise<void>;
    /**
     * 移除指定位置的项（对标 `Menu.removeAt`）。
     *
     * @param {number} position
     * @returns {Promise<MenuItemBase | null>} 被移除的项；位置越界时 `null`。
     */
    removeAt(position: number): Promise<MenuItemBase | null>;
    /** @returns {Promise<MenuItemBase[]>} 子项（副本）。 */
    items(): Promise<MenuItemBase[]>;
    /**
     * 按 id 取项（对标 `Menu.get`）。
     *
     * @param {string} id
     * @returns {Promise<MenuItemBase | null>}
     */
    get(id: string): Promise<MenuItemBase | null>;
    /**
     * 设为应用菜单栏（对标 `Menu.setAsAppMenu`）。
     *
     * @returns {Promise<Menu | null>} 之前那份应用菜单；没有则是 `null`。
     * @remarks
     * <p><b>与 Tauri 的两处差别</b>：
     * <ol>
     *   <li>Tauri 返回的是 Rust 侧那个菜单资源（可以 `close()` 掉）。本库没有资源表，
     *       所以返回的是**页面侧记的**上一次传进来的 `Menu`——它仍是纯数据，
     *       `close()` 对它没有意义（本库的 Menu 也没有 close）。</li>
     *   <li>这个"上一次"是**本页面**的视角。多个页面都调过 `setAsAppMenu` 的话，
     *       各自只看得到自己那次之前的那份。单窗口应用完全一致。</li>
     * </ol>
     * <p><b>作用面比 `setAsWindowMenu` 大</b>：它会套用到所有没显式设过菜单栏的窗口
     * （含此后新建的），所以门禁上更严——Release 构建下可能要应用显式放行
     * `menu.setAsAppMenu`。`setAsWindowMenu` 只碰调用方那一个窗口，无条件放行。
     * <p>macOS 上这是**唯一**能设菜单的路（菜单在 macOS 是应用级的，
     * `setAsWindowMenu` 会抛错）。
     */
    setAsAppMenu(): Promise<Menu | null>;
    /**
     * 设为某个窗口的菜单栏（对标 `Menu.setAsWindowMenu`）。
     *
     * @param {{ label?: string } | null} [window] 目标窗口；省略时是**发起调用的那个窗口**。
     * @returns {Promise<Menu | null>} 之前装在那个窗口上的菜单；没有则是 `null`。
     * @throws {Error} **macOS 上抛**：菜单在 macOS 是应用级的，没有"某个窗口的菜单栏"这回事
     *   （与 Tauri 的行为一致——它在 macOS 上也是 Unsupported）。
     * @remarks
     * 传空菜单（`items: []`）是合法的，表示"这个窗口不要菜单栏"——不是错误。
     */
    setAsWindowMenu(window?: {
        label?: string;
    } | null): Promise<Menu | null>;
    /**
     * 弹出这个菜单（对标 `Menu.popup`）。
     *
     * @param {{ type?: string, x: number, y: number } | { x: number, y: number } | [number, number] | null} [at]
     *   弹出位置。物理与逻辑坐标都收（`type: 'Physical' | 'Logical'`），省略时跟随鼠标。
     * @param {{ label?: string } | null} [window]
     *   弹到哪个窗口上；省略时是**发起调用的那个窗口**。
     * @returns {Promise<void>}
     * @remarks
     * <p><b>与 Tauri 的完成时机不同，这是本库的已知差异</b>：Tauri 的文档说"菜单一显示就
     * resolve"，本库 <b>Windows 上会等到用户选完或取消</b>（`TrackPopupMenuEx` 自带模态
     * 消息循环）与 **macOS**（AppKit 的 `popUpMenuPositioningItem:` 同样跟踪到用户选择），
     * 只有 **Linux 立即 resolve**（GTK 的菜单是异步的）。所以：<b>别在 `await menu.popup()` 之后
     * 假设"用户已经选过了"</b>，可靠的完成信号是各项的 `action`。
     * <p>把整棵菜单树一次性发给宿主，之后改这个菜单<b>不影响</b>已经弹出的那个（它是快照）。
     */
    popup(at?: {
        type?: string;
        x: number;
        y: number;
    } | {
        x: number;
        y: number;
    } | [number, number] | null, window?: {
        label?: string;
    } | null): Promise<void>;
    /**
     * 关掉当前正在显示的菜单（**本库扩展**，Tauri 没有这个方法）。
     *
     * @param {{ label?: string } | null} [window] 菜单弹在哪个窗口上；省略时是发起者。
     * @returns {Promise<void>}
     * @remarks
     * 与 `Submenu.close()` 同一条理由：Windows 的 `popup()` 阻塞，页面必须有
     * 一条"用代码关掉它"的路。没有菜单在屏上是空操作。
     */
    close(window?: {
        label?: string;
    } | null): Promise<void>;
}
/**
 * 系统原生图标名（macOS 的 NSImage 名称），取值与参照实现逐键一致。
 *
 * @remarks
 * <p><b>本库不显示菜单图标</b>（没有图像资源表），这里保留常量表只是为了
 * 让 `NativeIcon.Folder` 这类写法能编译、能传进 `setIcon`（后者会明确抛错）。
 * <p><b>键集必须与 Tauri 完全一致</b>：这张表由
 * <code>tauri-alignment.test.mjs</code> 的常量对象比对逐键盯着——多一个键
 * 就是一处自造的名字，少一个键就是一处静默落后。
 */
export const NativeIcon: Readonly<{
    Add: "Add";
    Advanced: "Advanced";
    Bluetooth: "Bluetooth";
    Bookmarks: "Bookmarks";
    Caution: "Caution";
    ColorPanel: "ColorPanel";
    ColumnView: "ColumnView";
    Computer: "Computer";
    EnterFullScreen: "EnterFullScreen";
    Everyone: "Everyone";
    ExitFullScreen: "ExitFullScreen";
    FlowView: "FlowView";
    Folder: "Folder";
    FolderBurnable: "FolderBurnable";
    FolderSmart: "FolderSmart";
    FollowLinkFreestanding: "FollowLinkFreestanding";
    FontPanel: "FontPanel";
    GoLeft: "GoLeft";
    GoRight: "GoRight";
    Home: "Home";
    IChatTheater: "IChatTheater";
    IconView: "IconView";
    Info: "Info";
    InvalidDataFreestanding: "InvalidDataFreestanding";
    LeftFacingTriangle: "LeftFacingTriangle";
    ListView: "ListView";
    LockLocked: "LockLocked";
    LockUnlocked: "LockUnlocked";
    MenuMixedState: "MenuMixedState";
    MenuOnState: "MenuOnState";
    MobileMe: "MobileMe";
    MultipleDocuments: "MultipleDocuments";
    Network: "Network";
    Path: "Path";
    PreferencesGeneral: "PreferencesGeneral";
    QuickLook: "QuickLook";
    Refresh: "Refresh";
    RefreshFreestanding: "RefreshFreestanding";
    Remove: "Remove";
    RevealFreestanding: "RevealFreestanding";
    RightFacingTriangle: "RightFacingTriangle";
    Share: "Share";
    Slideshow: "Slideshow";
    SmartBadge: "SmartBadge";
    StatusAvailable: "StatusAvailable";
    StatusNone: "StatusNone";
    StatusPartiallyAvailable: "StatusPartiallyAvailable";
    StatusUnavailable: "StatusUnavailable";
    StopProgress: "StopProgress";
    StopProgressFreestanding: "StopProgressFreestanding";
    TrashEmpty: "TrashEmpty";
    TrashFull: "TrashFull";
    User: "User";
    UserAccounts: "UserAccounts";
    UserGroup: "UserGroup";
    UserGuest: "UserGuest";
}>;
export type PredefinedMenuItemOptions = {
    /**
     * 显示文本；省略时用平台自己的标准文案。
     */
    text?: string | undefined;
    /**
     * 预定义项类型。
     */
    item?: string | {
        About: unknown;
    } | undefined;
    enabled?: boolean | undefined;
    id?: string | undefined;
    action?: ((id: string) => void) | undefined;
};
/**
 * 所有菜单类型的共同基类：只持有 id 与 action。
 *
 * @remarks
 * <p>与 Tauri 的 `MenuItemBase` 对应，但**不导出**（Tauri 也没有从 menu 模块转出它）。
 * <p><b>这里刻意只放 id 与 action，不放 text / enabled</b>：Tauri 的 `Menu` 没有
 * `text()` / `isEnabled()`（它是个容器，不是"一项"），而 `PredefinedMenuItem` 只有
 * `text()` / `setText()`。把这些方法放在基类上，`Menu` 与 `PredefinedMenuItem` 就会
 * 凭空多出几个方法——那是"看起来能用、语义却不成立"的假 API（对 `Menu` 调
 * `setEnabled()` 是什么意思？）。
 * <p>所以文本与可用性下沉到 <see cref="MenuItem"/>，各自需要什么就在各自那层加。
 */
declare class MenuItemBase {
    /**
     * @param {{ id?: string, text?: string, enabled?: boolean, action?: (id: string) => void }} [options]
     */
    constructor(options?: {
        id?: string;
        text?: string;
        enabled?: boolean;
        action?: (id: string) => void;
    });
    /** @type {string} */
    id: string;
    /** @type {string} */
    _text: string;
    /** @type {boolean} */
    _enabled: boolean;
    /** @type {((id: string) => void) | undefined} */
    _action: ((id: string) => void) | undefined;
    /**
     * 序列化成宿主认识的形状。
     *
     * @param {Map<string, (id: string) => void>} actions 本次弹出的 action 表（会被填充）。
     * @param {string} token 本次弹出的令牌。
     * @returns {object}
     * @internal
     */
    _wire(actions: Map<string, (id: string) => void>, token: string): object;
    /**
     * 序列化成宿主认识的形状（各类菜单项在这里补自己的字段）。
     *
     * @param {string} id 已经处理过 action 前缀的 id。
     * @param {Map<string, (id: string) => void>} actions 本次弹出的 action 表。
     * @param {string} token 本次弹出的令牌。
     * @returns {object}
     * @internal
     * @remarks 三个参数全都要收：子菜单要拿 actions/token 递归序列化子项。
     * 叶子类型用不到后两个，但签名必须一致（否则子类覆盖时 TS 会报参数个数不符）。
     */
    _wireCore(id: string, actions: Map<string, (id: string) => void>, token: string): object;
}
export {};
