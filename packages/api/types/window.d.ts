/**
 * 取当前窗口的句柄（对标 Tauri 的 `getCurrentWindow`）。
 *
 * @returns {Window}
 * @remarks
 * **同步**返回（与 Tauri 一致）。它只读 `oriel.label`，不需要问宿主。
 */
export function getCurrentWindow(): Window;
/**
 * 列出所有存活窗口（对标 Tauri 的 `getAllWindows`）。
 *
 * @returns {Promise<Window[]>}
 * @remarks 需要 `window.getAll` 被允许——枚举窗口是跨窗口信息。
 */
export function getAllWindows(): Promise<Window[]>;
/**
 * 按 label 取窗口（对标 Tauri 的 `Window.getByLabel`）。
 *
 * @param {string} label
 * @returns {Promise<Window | null>} 不存在时 `null`（与 Tauri 一致，不抛）。
 */
export function getByLabel(label: string): Promise<Window | null>;
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
export function getFocusedWindow(): Promise<Window | null>;
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
export function currentMonitor(): Promise<Monitor | null>;
/**
 * 主显示器（对标 Tauri 的 `primaryMonitor`）。
 *
 * @returns {Promise<Monitor | null>} 平台报不出来时 `null`。
 * @remarks
 * 主屏同时是坐标原点：它的 `position` 通常是 `(0, 0)`，副屏在主屏左侧或上方时为负。
 */
export function primaryMonitor(): Promise<Monitor | null>;
/**
 * 所有显示器（对标 Tauri 的 `availableMonitors`）。
 *
 * @returns {Promise<Monitor[]>} 顺序由平台决定，**不保证主屏在前**；没有显示器时是空数组。
 * @remarks 每次调用都重新向系统枚举，所以插拔显示器、改分辨率之后立刻可见。
 */
export function availableMonitors(): Promise<Monitor[]>;
/**
 * 包含指定点的显示器（对标 Tauri 的 `monitorFromPoint`）。
 *
 * @param {number} x 横坐标（物理像素，相对主屏左上角）。
 * @param {number} y 纵坐标（物理像素，向下为正）。
 * @returns {Promise<Monitor | null>} 点不在任何屏幕上时 `null`（不吸附到最近的屏幕）。
 */
export function monitorFromPoint(x: number, y: number): Promise<Monitor | null>;
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
export function cursorPosition(): Promise<PhysicalPosition>;
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
export const UserAttentionType: Readonly<{
    1: "Critical";
    2: "Informational";
    Critical: 1;
    Informational: 2;
}>;
/**
 * 任务栏/Dock 进度条状态（对标 Tauri 的 `ProgressBarStatus`）。
 *
 * 取值与 Tauri 逐键一致。**方法本身在 B22 落地**（`Window.setProgressBar`）——
 * 常量先到位，迁移代码里的 `ProgressBarStatus.Indeterminate` 不必改。
 */
export const ProgressBarStatus: Readonly<{
    None: "none";
    Normal: "normal";
    Indeterminate: "indeterminate";
    Paused: "paused";
    Error: "error";
}>;
/**
 * 窗口视觉材质（对标 Tauri 的 `Effect`，macOS vibrancy / Windows Mica 系）。
 *
 * 取值与 Tauri 逐键一致（27 个）。**方法本身在 B22 落地**（`Window.setEffects`）；
 * 不支持的平台由方法抛 `not_supported`——常量本身是跨平台的字符串。
 */
export const Effect: Readonly<{
    AppearanceBased: "appearanceBased";
    Light: "light";
    Dark: "dark";
    MediumLight: "mediumLight";
    UltraDark: "ultraDark";
    Titlebar: "titlebar";
    Selection: "selection";
    Menu: "menu";
    Popover: "popover";
    Sidebar: "sidebar";
    HeaderView: "headerView";
    Sheet: "sheet";
    WindowBackground: "windowBackground";
    HudWindow: "hudWindow";
    FullScreenUI: "fullScreenUI";
    Tooltip: "tooltip";
    ContentBackground: "contentBackground";
    UnderWindowBackground: "underWindowBackground";
    UnderPageBackground: "underPageBackground";
    LiquidGlassRegular: "liquidGlassRegular";
    LiquidGlassClear: "liquidGlassClear";
    Mica: "mica";
    Blur: "blur";
    Acrylic: "acrylic";
    Tabbed: "tabbed";
    TabbedDark: "tabbedDark";
    TabbedLight: "tabbedLight";
}>;
/**
 * 特效的激活状态（对标 Tauri 的 `EffectState`）。
 */
export const EffectState: Readonly<{
    FollowsWindowActiveState: "followsWindowActiveState";
    Active: "active";
    Inactive: "inactive";
}>;
/**
 * 关闭请求事件（对标 Tauri 的 `CloseRequestedEvent`）。
 *
 * 与 Tauri 的**关键差别**：Tauri 的 `preventDefault()` 能真的阻止关闭；本库的关闭
 * 语义是"页面什么都不做窗口就不关"（见 {@link Window.onCloseRequested}），
 * `preventDefault()` 在这里是**记录意图**（`isPreventDefault()` 可查）——
 * 它不会改变任何行为，因为"没关"本来就是默认。迁移代码的
 * `event.preventDefault()` 不必删，但它在本库里不产生额外效果。
 */
export class CloseRequestedEvent {
    /**
     * @param {{ event: string, id: number, payload?: unknown }} event 事件原样（`onCloseRequested` 的回调参数）。
     */
    constructor(event: {
        event: string;
        id: number;
        payload?: unknown;
    });
    /** 事件名。 */
    event: string;
    /** 事件 id（退订用）。 */
    id: number;
    /** 事件负载（Tauri 的类不暴露它，本库保留——纯加法）。 */
    payload: unknown;
    _preventDefault: boolean;
    /** 记录"调用方不希望窗口关"（本库是记录意图，不改变行为——见类说明）。 */
    preventDefault(): void;
    /** 是否调用过 {@link CloseRequestedEvent.preventDefault}。 */
    isPreventDefault(): boolean;
}
export class Window {
    /**
     * 有焦点的窗口（对标 Tauri 的静态方法 `Window.getFocusedWindow()`）。
     *
     * @returns {Promise<Window | null>} 没有窗口在前台时 `null`。
     * @remarks
     * 与模块级 `getFocusedWindow()` 是同一个实现（那边是函数、这里是静态方法，
     * Tauri 两种写法都提供，迁移代码两种都可能出现）。
     */
    static getFocusedWindow(): Promise<Window | null>;
    /**
     * @param {string} label
     */
    constructor(label: string);
    /** @type {string} */
    label: string;
    /** 显示窗口。需要 `window.show` 被允许。 */
    show(): Promise<any>;
    /** 隐藏窗口。需要 `window.hide` 被允许。 */
    hide(): Promise<any>;
    /** 关闭窗口（可被 `Closing` 与页面接管拦下）。**无条件放行**。 */
    close(): Promise<any>;
    /** 强制销毁窗口（不被任何东西拦下）。**无条件放行**。 */
    destroy(): Promise<any>;
    /** 聚焦窗口。需要 `window.focus` 被允许。 */
    setFocus(): Promise<any>;
    /** 最小化。**无条件放行**。 */
    minimize(): Promise<any>;
    /** 最大化。需要 `window.maximize` 被允许。 */
    maximize(): Promise<any>;
    /** 还原（对标 Tauri 的 `unmaximize`）。需要 `window.restore` 被允许。 */
    unmaximize(): Promise<any>;
    /** 最大化/还原切换，返回切换后是否最大化。**无条件放行**。 */
    toggleMaximize(): Promise<any>;
    /** 当前是否最大化。**无条件放行**（只读查询）。 */
    isMaximized(): Promise<any>;
    /** 当前是否显示中（最小化仍算显示中）。**无条件放行**（只读查询）。 */
    isVisible(): Promise<any>;
    /** 窗口所在显示器的缩放比（1.0 = 100%）。需要 `window.scaleFactor` 被允许。 */
    scaleFactor(): Promise<any>;
    /**
     * 是否最小化。
     *
     * @returns {Promise<boolean>}
     * @remarks **与 `isVisible()` 是两件事**：最小化的窗口仍然"可见"，
     * 它只是被收进了任务栏。混为一谈的后果是"最小化之后页面以为窗口被藏了"。
     */
    isMinimized(): Promise<boolean>;
    /**
     * 是否拥有键盘焦点。
     *
     * @returns {Promise<boolean>}
     * @remarks 按**窗口**判而不是按应用判：多窗口应用里只有一个是 key window。
     * 标题栏常用它渲染激活/非激活两套配色。
     */
    isFocused(): Promise<boolean>;
    /**
     * 是否全屏。
     *
     * @returns {Promise<boolean>}
     * @remarks 取的是宿主 `toggleFullscreen()` 记住的状态，不是比对窗口与显示器矩形——
     * 后者在用户手动把窗口拖成刚好铺满屏幕时会给出与库不一致的答案。
     */
    isFullscreen(): Promise<boolean>;
    /**
     * 是否置顶。
     *
     * @returns {Promise<boolean>}
     * @remarks 同上：取宿主记住的状态。
     */
    isAlwaysOnTop(): Promise<boolean>;
    /**
     * 是否有系统装饰（标题栏与边框）。
     *
     * @returns {Promise<boolean>}
     * @remarks 取创建时的选项，窗口生命期内不变。无边框窗口多用它决定"要不要渲染自绘标题栏"。
     */
    isDecorated(): Promise<boolean>;
    /**
     * 是否可缩放。
     *
     * @returns {Promise<boolean>}
     * @remarks 从**原生实际状态**读回（`setResizable` 改的就是它），
     * 所以反映的是"系统接受的是什么"。
     */
    isResizable(): Promise<boolean>;
    /**
     * 是否可最大化。
     *
     * @returns {Promise<boolean>}
     * @remarks **Linux 与 macOS 上等于 `isResizable()`**：那两个平台没有独立的
     * "可最大化"概念（GTK 跟着 resizable 走、AppKit 的 zoom 以 resizable 为前提）。
     * Windows 上才是独立的样式位。
     */
    isMaximizable(): Promise<boolean>;
    /**
     * 是否可最小化。
     *
     * @returns {Promise<boolean>}
     * @remarks 同 `isMaximizable()`：Linux 上回落到 `isResizable()`。
     */
    isMinimizable(): Promise<boolean>;
    /**
     * 是否可关闭。
     *
     * @returns {Promise<boolean>}
     * @remarks **Linux 上恒为 `true`**：GTK 没有这个概念（窗口总能收到 delete-event，
     * 本库的关闭接管是在那之上做的拦截）。
     */
    isClosable(): Promise<boolean>;
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
    theme(): Promise<"light" | "dark">;
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
    setTheme(theme?: "light" | "dark" | null): Promise<void>;
    /**
     * 是否接收输入。
     *
     * @returns {Promise<boolean>}
     * @remarks **macOS 上只挡鼠标、不挡键盘**：Cocoa 没有"禁用窗口"这个概念，
     * 那边用 `ignoresMouseEvents` 近似。需要挡键盘的应用要自己处理。
     */
    isEnabled(): Promise<boolean>;
    /**
     * 启用/禁用窗口的输入。
     *
     * @param {boolean} enabled
     * @returns {Promise<void>}
     * @remarks 需要 `Allow("window.setEnabled")`——它**不在**标题栏白名单里
     * （禁用窗口不是标题栏要做的事）。
     */
    setEnabled(enabled: boolean): Promise<void>;
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
    setMaximizable(enabled: boolean): Promise<void>;
    /**
     * 设置窗口是否可最小化。
     *
     * @param {boolean} enabled
     * @returns {Promise<void>}
     * @remarks 需要 `Allow("window.setMinimizable")`。Linux 上是空操作，理由同 `setMaximizable`。
     */
    setMinimizable(enabled: boolean): Promise<void>;
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
    setClosable(enabled: boolean): Promise<void>;
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
    setIcon(icon: import("./image.js").JsImage): Promise<void>;
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
    setBackgroundColor(color: string | [number, number, number] | [number, number, number, number] | {
        red: number;
        green: number;
        blue: number;
        alpha?: number;
    }): Promise<void>;
    /**
     * 设置窗口的最大尺寸。
     *
     * @param {import('./dpi.js').SizeObject | {width: number, height: number} | [number, number] | null} size
     *   与 `setSize` 收同样的形状；传 `null` 表示**取消**上限。
     * @returns {Promise<void>}
     * @remarks 需要 `Allow("window.setMaxSize")`。
     */
    setMaxSize(size: import("./dpi.js").SizeObject | {
        width: number;
        height: number;
    } | [number, number] | null): Promise<void>;
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
    setSizeConstraints(constraints?: {
        minWidth?: number | null;
        minHeight?: number | null;
        maxWidth?: number | null;
        maxHeight?: number | null;
    } | null): Promise<void>;
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
    requestUserAttention(requestType?: number | null): Promise<void>;
    /**
     * 订阅一个事件。与模块级 `listen` 等价。
     *
     * @param {string} event 事件名（Tauri 名或宿主名都行，见 `event` 模块的映射表）。
     * @param {(event: { event: string, id: number, payload: any }) => void} handler
     * @returns {(() => void) & PromiseLike<() => void>} 退订函数；它同时是 thenable，
     *   所以 `await win.listen(...)` 与 `win.listen(...).then(...)` 都成立（见 `event` 模块）。
     */
    listen(event: string, handler: (event: {
        event: string;
        id: number;
        payload: any;
    }) => void): (() => void) & PromiseLike<() => void>;
    /**
     * 订阅一次；收到第一条后自动退订。
     *
     * @param {string} event
     * @param {(event: { event: string, id: number, payload: any }) => void} handler
     * @returns {(() => void) & PromiseLike<() => void>} 退订函数（在触发前调用可以取消；同样 thenable）。
     */
    once(event: string, handler: (event: {
        event: string;
        id: number;
        payload: any;
    }) => void): (() => void) & PromiseLike<() => void>;
    /**
     * 广播一个事件给**所有**窗口。
     *
     * @param {string} event
     * @param {unknown} [payload]
     * @returns {Promise<void>}
     * @remarks 需要宿主 `Allow("event.emit")`——那是唯一一条**不在**任何白名单里的内建命令
     * （它让一个窗口的页面能驱动别的窗口，而自绘标题栏不依赖它）。见 API.md §4。
     */
    emit(event: string, payload?: unknown): Promise<void>;
    /**
     * 把事件发给指定的那个窗口。
     *
     * @param {string} target 目标窗口的 label。
     * @param {string} event
     * @param {unknown} [payload]
     * @returns {Promise<void>}
     * @remarks 同样需要 `Allow("event.emit")`。
     */
    emitTo(target: string, event: string, payload?: unknown): Promise<void>;
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
    unminimize(): Promise<void>;
    /**
     * 当前标题（对标 Tauri 的 `title()`）。**无条件放行**（只读查询）。
     *
     * @returns {Promise<string>}
     * @remarks
     * 与标题变化事件无关：那个只在**页面文档标题**变化时触发（标题栏跟随页面），
     * 程序调 `setTitle()` 不会触发它。要"标题现在是什么"调这个方法，别等事件。
     */
    title(): Promise<string>;
    /**
     * 设置标题。需要 `window.setTitle` 被允许。
     * @param {string} title
     */
    setTitle(title: string): Promise<any>;
    /**
     * 设置是否可调整大小。需要 `window.setResizable` 被允许。
     * @param {boolean} resizable
     */
    setResizable(resizable: boolean): Promise<any>;
    /**
     * 设置全屏。需要 `window.setFullscreen` 被允许。
     * @param {boolean} fullscreen
     */
    setFullscreen(fullscreen: boolean): Promise<any>;
    /** 全屏切换，返回切换后是否全屏。**无条件放行**。 */
    toggleFullscreen(): Promise<any>;
    /**
     * 设置置顶（对标 Tauri 的 `setAlwaysOnTop`）。需要 `window.setOnTop` 被允许。
     * @param {boolean} alwaysOnTop
     */
    setAlwaysOnTop(alwaysOnTop: boolean): Promise<any>;
    /** 置顶切换，返回切换后是否置顶。**无条件放行**。 */
    toggleAlwaysOnTop(): Promise<any>;
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
    setSize(size: {
        type?: string;
        width: number;
        height: number;
    } | [number, number]): Promise<void>;
    /**
     * 设置窗口左上角位置（对标 Tauri 的 `setPosition`）。需要 `window.moveTo` 被允许。
     *
     * @param {{ type?: string, x: number, y: number } | [number, number]} position
     */
    setPosition(position: {
        type?: string;
        x: number;
        y: number;
    } | [number, number]): Promise<void>;
    /**
     * 把光标限制在窗口内（对标 Tauri 的 `setCursorGrab`）。
     *
     * @param {boolean} grab `true` 抓取，`false` 释放。
     * @returns {Promise<void>}
     * @remarks
     * **Linux 不支持**（抛 `not_supported`）。macOS 的形态是把光标**锁死**在原地，
     * 并不是"限制在窗口矩形内"——这是平台能给的唯一形态。
     */
    setCursorGrab(grab: boolean): Promise<void>;
    /**
     * 显示或隐藏光标（对标 Tauri 的 `setCursorVisible`）。
     *
     * @param {boolean} visible
     * @returns {Promise<void>}
     * @remarks 隐藏是"应用到窗口/应用"的状态，窗口销毁时宿主会自动恢复。
     */
    setCursorVisible(visible: boolean): Promise<void>;
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
    setCursorIcon(icon: CursorIcon): Promise<void>;
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
    setCursorPosition(position: import("./dpi.js").LogicalPosition | import("./dpi.js").PhysicalPosition | {
        type?: string;
        x: number;
        y: number;
    } | [number, number]): Promise<void>;
    /**
     * 让窗口忽略鼠标事件（对标 Tauri 的 `setIgnoreCursorEvents`，即"点击穿透"）。
     *
     * @param {boolean} ignore
     * @returns {Promise<void>}
     * @remarks **Linux 只在 X11 下有效**（Wayland 抛 `not_supported`）。
     */
    setIgnoreCursorEvents(ignore: boolean): Promise<void>;
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
    startResizeDragging(direction: ResizeDirection): Promise<void>;
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
    setSkipTaskbar(skip: boolean): Promise<void>;
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
    setVisibleOnAllWorkspaces(visible: boolean): Promise<void>;
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
    setContentProtected(contentProtected: boolean): Promise<void>;
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
    setSimpleFullscreen(fullscreen: boolean): Promise<void>;
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
    setFullscreenOnMonitor(position: {
        x: number;
        y: number;
    } | [number, number]): Promise<void>;
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
    setTitleBarStyle(style: TitleBarStyle): Promise<void>;
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
    setShadow(enable: boolean): Promise<void>;
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
    setAlwaysOnBottom(alwaysOnBottom: boolean): Promise<void>;
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
    setFocusable(focusable: boolean): Promise<void>;
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
    setDecorations(decorations: boolean): Promise<void>;
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
    setEffects(effects: Effects): Promise<void>;
    /**
     * 清掉已应用的材质（对标 Tauri 的 `clearEffects`）。
     *
     * @returns {Promise<void>}
     * @remarks
     * 与 `setEffects` 共用同一个能力（`Allow("window.setEffects")`）。
     * 没设过时是空操作；**Linux 同样抛 `not_supported`**（能力整个不存在）。
     */
    clearEffects(): Promise<void>;
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
    setProgressBar(state: ProgressBarState | string): Promise<void>;
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
    setOverlayIcon(icon?: import("./image.js").JsImage): Promise<void>;
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
    setBadgeCount(count?: number): Promise<void>;
    /**
     * Dock 徽标上的短文本（对标 Tauri 的 `setBadgeLabel`）。
     *
     * @param {string} [label] 要显示的文本；**不传表示清除**。
     * @returns {Promise<void>}
     * @remarks
     * 需要 `Allow("window.setBadgeLabel")`。**只有 macOS 实现**（应用级）。
     * 与 `setBadgeCount` 是同一个落点（`NSApp.dockTile.badgeLabel`），后者只是把数字转成文本。
     */
    setBadgeLabel(label?: string): Promise<void>;
    /**
     * 设置**客户区**最小尺寸（对标 Tauri 的 `setMinSize`）。
     *
     * @param {{ type?: string, width: number, height: number } | [number, number] | null} size
     *   `null` 表示取消限制（本库用 0 表示"不限制"，与 `OrielWindowOptions.MinWidth` 一致）。
     */
    setMinSize(size: {
        type?: string;
        width: number;
        height: number;
    } | [number, number] | null): Promise<void>;
    /** 居中。需要 `window.center` 被允许。 */
    center(): Promise<any>;
    /**
     * 客户区尺寸（逻辑像素）。需要 `window.innerSize` 被允许。
     *
     * @returns {Promise<import('./dpi.js').LogicalSize>}
     */
    innerSize(): Promise<import("./dpi.js").LogicalSize>;
    /**
     * 整窗尺寸（逻辑像素）。
     *
     * @returns {Promise<import('./dpi.js').LogicalSize>}
     * @remarks
     * Linux/Wayland 下可能与 `innerSize()` 相等——合成器不告诉客户端装饰尺寸。
     * 需要 `window.outerSize` 被允许。
     */
    outerSize(): Promise<import("./dpi.js").LogicalSize>;
    /**
     * 窗口左上角（屏幕逻辑坐标）。需要 `window.outerPosition` 被允许。
     *
     * @returns {Promise<import('./dpi.js').LogicalPosition>}
     */
    outerPosition(): Promise<import("./dpi.js").LogicalPosition>;
    /**
     * 客户区左上角（屏幕逻辑坐标）。
     *
     * @returns {Promise<import('./dpi.js').LogicalPosition>}
     * @remarks
     * Linux 上目前恒等于 `outerPosition()`（GTK 不暴露装饰偏移）。
     * 需要 `window.innerPosition` 被允许。
     */
    innerPosition(): Promise<import("./dpi.js").LogicalPosition>;
    /**
     * 开始拖动窗口（对标 Tauri 的 `startDragging`）。
     *
     * @remarks
     * 库自己接管的标题栏拖动**不走这里**——它在注入脚本里内联完成（因为要在 `mousedown`
     * 之内同步判断双击）。这个方法是给"页面自己实现拖动"用的。**无条件放行**。
     */
    startDragging(): Promise<any>;
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
    onCloseRequested(handler: (event: {
        event: string;
        id: number;
        payload: any;
        preventDefault: () => void;
    }) => void): Promise<() => void>;
    /**
     * 订阅本窗口的尺寸变化（对标 Tauri 的 `onResized`）。
     * @param {(event: { event: string, id: number, payload: { width: number, height: number } }) => void} handler
     * @returns {Promise<() => void>}
     */
    onResized(handler: (event: {
        event: string;
        id: number;
        payload: {
            width: number;
            height: number;
        };
    }) => void): Promise<() => void>;
    /**
     * 订阅本窗口的位置变化（对标 Tauri 的 `onMoved`）。
     * @param {(event: { event: string, id: number, payload: { x: number, y: number } }) => void} handler
     * @returns {Promise<() => void>}
     */
    onMoved(handler: (event: {
        event: string;
        id: number;
        payload: {
            x: number;
            y: number;
        };
    }) => void): Promise<() => void>;
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
    onThemeChanged(handler: (event: {
        event: string;
        id: number;
        payload: "light" | "dark";
    }) => void): Promise<() => void>;
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
    onDragDropEvent(handler: (event: {
        event: string;
        id: number;
        payload: {
            type: "drop";
            paths: string[];
        };
    }) => void): Promise<() => void>;
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
    onScaleChanged(handler: (event: {
        event: string;
        id: number;
        payload: {
            scaleFactor: number;
            size: {
                width: number;
                height: number;
            };
        };
    }) => void): Promise<() => void>;
    /**
     * 订阅本窗口的焦点变化（对标 Tauri 的 `onFocusChanged`）。
     * @param {(event: { event: string, id: number, payload: null }) => void} handler
     * @returns {Promise<() => void>}
     */
    onFocusChanged(handler: (event: {
        event: string;
        id: number;
        payload: null;
    }) => void): Promise<() => void>;
}
/**
 * 一台显示器（对标 Tauri 的 `Monitor`）。
 */
export type Monitor = {
    /**
     * 人类可读的名称；平台给不出时是 `null`。
     */
    name: string | null;
    /**
     * 左上角（物理像素，相对主屏左上角）。
     */
    position: PhysicalPosition;
    /**
     * 分辨率（物理像素）。
     */
    size: PhysicalSize;
    /**
     * 缩放比（1.0 = 100%，2.0 = Retina / 200%）。
     */
    scaleFactor: number;
};
/**
 * 光标形状（对标 Tauri 的 `CursorIcon`）。
 */
export type CursorIcon = "default" | "crosshair" | "hand" | "arrow" | "move" | "text" | "wait" | "help" | "progress" | "notAllowed" | "contextMenu" | "cell" | "verticalText" | "alias" | "copy" | "noDrop" | "grab" | "grabbing" | "allScroll" | "zoomIn" | "zoomOut" | "eResize" | "nResize" | "neResize" | "nwResize" | "sResize" | "seResize" | "swResize" | "wResize" | "ewResize" | "nsResize" | "neswResize" | "nwseResize" | "colResize" | "rowResize";
/**
 * resize 拖动的起始边/角（对标 Tauri 的 `ResizeDirection`）。
 */
export type ResizeDirection = "East" | "North" | "NorthEast" | "NorthWest" | "South" | "SouthEast" | "SouthWest" | "West";
/**
 * 标题栏样式（对标 Tauri 的 `TitleBarStyle`）。
 */
export type TitleBarStyle = "visible" | "transparent" | "overlay";
/**
 * `setEffects` 的参数（对标 Tauri 的 `Effects`）。
 */
export type Effects = {
    /**
     * 材质列表（`Effect` 的取值）。**第一个本平台支持的生效**，其余忽略。
     */
    effects: string[];
    /**
     * 激活状态（`EffectState` 的取值）。**仅 macOS**；Windows 上被忽略（会记诊断）。
     */
    state?: string | undefined;
    /**
     * 材质视图的圆角。**仅 macOS**。
     */
    radius?: number | undefined;
    /**
     * 特效色调。**Windows** 只影响 `blur`/`acrylic`（Win10 1903+）；macOS 只影响液态玻璃（本库未实现）。
     */
    color?: [number, number, number] | [number, number, number, number] | {
        red: number;
        green: number;
        blue: number;
        alpha?: number;
    } | undefined;
    /**
     * 交互式玻璃（macOS 26+）。本库未实现，接受但忽略。
     */
    interactive?: boolean | undefined;
};
/**
 * `setProgressBar` 的参数（对标 Tauri 的 `ProgressBarState`）。
 */
export type ProgressBarState = {
    /**
     * `ProgressBarStatus` 的取值。
     */
    status?: string | undefined;
    /**
     * 0–100 的百分比。
     */
    progress?: number | undefined;
};
import { PhysicalPosition } from './dpi.js';
import { PhysicalSize } from './dpi.js';
export { LogicalSize, LogicalPosition, PhysicalSize, PhysicalPosition } from "./dpi.js";
