// 对标 `@tauri-apps/api/menu` 的**菜单模型与弹出**部分。
//
// 与 Tauri 的根本差别（这里必须说清楚，否则迁移时会以为"行为怪"）：
//
//   1. **菜单在页面侧是纯数据，不是资源句柄**。Tauri 的 Menu 在 Rust 侧有 rid，
//      `append` / `insert` 是对那个资源的增量修改；本库没有句柄表（README 里的结构性缺口），
//      所以这棵树完全活在 JS 里，只有 `popup()` 会把整棵树发给宿主。由此派生：
//        * `new Menu()` / `append()` / `items()` 都**不发 IPC**，因此不可能"失败"；
//        * 没有 `close()`（Tauri 的 Resource.close 用来释放 rid，这里没有 rid）；
//        * 给 `popup()` 之后改这个菜单**不会**影响已经弹出的那个（它是快照）。
//
//   2. **`action` 回调靠事件回来**。Tauri 把回调挂在 Rust 侧的资源上；本库在弹出时给
//      带 action 的项加一个**本次弹出专属的 id 前缀**，宿主点击后经 `menu.itemclicked`
//      事件把 id 送回来，这里按前缀识别并还原原始 id。所以：
//        * action 只对**有 id 的项**有效（没有 id 就无法路由——Tauri 那边 id 可由 Rust 生成，
//          我们这边生成一个你也对不上任何东西）；
//        * 事件到达前若又弹了另一个菜单，前一个菜单的 action 不再触发（一次只有一个菜单在屏上）。
//
//   3. **图标暂不支持**：IconMenuItem 存在、可构造、可加入菜单，但 `setIcon` 抛错
//      （图标要资源表，见 README）。菜单项会画成普通文本项。
//
//   4. **`popup()` 的完成时机与 Tauri 不同**：Tauri 的文档说"菜单一显示就 resolve"。
//      本库 Windows 上 `TrackPopupMenuEx` 自带模态循环，所以 `popup()` 会等到用户
//      选完或取消才 resolve；macOS / Linux 立即 resolve。**别在 `await menu.popup()`
//      之后假设"用户已经选过了"**——三个平台在这件事上不一致，action 才是可靠的回传通道。

import { invoke } from './core.js';
import { listen } from './event.js';
import { normalizePosition } from './dpi.js';
import { getTransport } from './internal.js';

/** 点击事件名（宿主侧见 OrielApp.MenuItemClickedEventName）。 */
const ITEM_CLICKED_EVENT = 'menu.itemclicked';

/**
 * 当前这次弹出的令牌与 action 表。
 *
 * @type {{ token: string | null, actions: Map<string, (id: string) => void> }}
 * @remarks
 * 模块级而不是挂在 Menu 实例上：一次只能有一个原生菜单在屏上，
 * 而点击事件不带"是哪个菜单"的信息——只能靠"最近一次弹出"来认领。
 */
const clickRouting = { token: null, actions: new Map() };

/**
 * 点击监听装在哪个传输层上（null 表示还没装）。
 *
 * @type {unknown}
 * @remarks
 * 记**传输层实例**而不是一个布尔值。布尔值会在"订阅表被清空但标志位还在"时失效——
 * `clearMocks()` 就是这种情况：它清了事件订阅表，但拿不到这里的标志位，
 * 于是监听永远装不回去，表现是"菜单能弹，点了没反应"。
 * 按实例记，换了传输层（真实宿主 ↔ mock）就自动重装，两边都对。
 */
let clickListenerOwner = null;

let clickListenerInstalled = false;

/**
 * 确保点击监听已安装（幂等）。
 *
 * @returns {Promise<void>}
 * @remarks 在 `popup()` **之前**装上：Windows 的弹出现在是阻塞的，点击可能在 `invoke`
 * 返回前就从原生回调里发出，那时监听必须已经在。
 */
async function ensureClickListener() {
    const transport = getTransport();

    if (clickListenerInstalled && clickListenerOwner === transport) {
        return;
    }

    clickListenerInstalled = true;
    clickListenerOwner = transport;

    // listen 返回的是可 await 的退订函数；这里不退订——模块级监听与页面同寿命。
    await listen(ITEM_CLICKED_EVENT, (event) => {
        const raw = event?.payload?.id;
        if (typeof raw !== 'string' || clickRouting.token === null) {
            return;
        }

        const prefix = `${clickRouting.token}:`;
        if (!raw.startsWith(prefix)) {
            // 不是这次弹出的项（例如宿主自己弹的菜单），不认领。
            return;
        }

        const id = raw.slice(prefix.length);
        const action = clickRouting.actions.get(id);
        if (typeof action !== 'function') {
            return;
        }

        try {
            action(id);
        } catch (err) {
            // 一个按钮的回调出错不该把事件分发链炸掉（其它监听者还在后面）。
            console.error('[oriel] menu action 抛错:', err);
        }
    });
}

/**
 * 把"选项对象或实例"统一成实例。
 *
 * @param {any} value
 * @returns {MenuItemBase}
 * @remarks
 * 类型推断规则（与 Tauri 的 Rust 侧一致，且这里写死成可解释的三条）：
 * 有 `item` → 预定义项；有 `items` → 子菜单；有 `checked` → 勾选项；其余是普通项。
 */
function toItem(value) {
    if (value instanceof MenuItemBase) {
        return value;
    }

    if (value === null || typeof value !== 'object') {
        throw new TypeError('菜单项必须是菜单对象或选项对象');
    }

    if ('item' in value) {
        return new PredefinedMenuItem(value);
    }

    if ('items' in value) {
        return new Submenu(value);
    }

    if ('checked' in value) {
        return new CheckMenuItem(value);
    }

    if ('icon' in value) {
        return new IconMenuItem(value);
    }

    return new MenuItem(value);
}

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
class MenuItemBase {
    /**
     * @param {{ id?: string, text?: string, enabled?: boolean, action?: (id: string) => void }} [options]
     */
    constructor(options = {}) {
        /** @type {string} */
        this.id = typeof options.id === 'string' ? options.id : '';
        /** @type {string} */
        this._text = typeof options.text === 'string' ? options.text : '';
        /** @type {boolean} */
        this._enabled = options.enabled !== false;
        /** @type {((id: string) => void) | undefined} */
        this._action = typeof options.action === 'function' ? options.action : undefined;
    }

    /**
     * 序列化成宿主认识的形状。
     *
     * @param {Map<string, (id: string) => void>} actions 本次弹出的 action 表（会被填充）。
     * @param {string} token 本次弹出的令牌。
     * @returns {object}
     * @internal
     */
    _wire(actions, token) {
        let id = this.id;

        // 只有"有 id 且挂了 action"的项才改名：其余项保持原 id，
        // 这样宿主侧订阅者看到的仍是应用自己写的名字。
        if (id !== '' && typeof this._action === 'function') {
            actions.set(id, this._action);
            id = `${token}:${id}`;
        }

        // actions / token 必须**往下传**：子菜单要拿它们递归序列化自己的子项，
        // 否则整棵子树的 items 是空的（菜单弹出来只有顶层几个空壳）。
        return this._wireCore(id, actions, token);
    }

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
    _wireCore(id, actions, token) {
        void actions;
        void token;

        return {
            kind: 'item',
            id,
            text: this._text,
            enabled: this._enabled,
        };
    }
}

/**
 * 普通菜单项（对标 `MenuItem`）。
 */
export class MenuItem extends MenuItemBase {
    /**
     * @param {{ id?: string, text: string, enabled?: boolean, accelerator?: string, action?: (id: string) => void }} options
     */
    constructor(options) {
        super(options);
        /** @type {string | null} */
        this._accelerator = typeof options?.accelerator === 'string' ? options.accelerator : null;
    }

    /**
     * @param {{ id?: string, text: string, enabled?: boolean, accelerator?: string, action?: (id: string) => void }} [options]
     * @returns {Promise<MenuItem>}
     */
    static async new(options = { text: '' }) {
        return new MenuItem(options);
    }

    /**
     * 显示文本（对标 `MenuItem.text`）。
     *
     * @returns {Promise<string>}
     * @remarks 这些方法**不在基类上**：Tauri 的 `Menu` 是个容器，没有 `text()`；
     * `PredefinedMenuItem` 只有 `text`/`setText`。放在基类上会让它们凭空多出
     * "看起来能用、语义却不成立"的方法（对 `Menu` 调 `setEnabled()` 是什么意思？）。
     */
    async text() {
        return this._text;
    }

    /**
     * 设置显示文本（对标 `MenuItem.setText`）。
     *
     * @param {string} text
     * @returns {Promise<void>}
     */
    async setText(text) {
        this._text = String(text);
    }

    /**
     * 是否可用（对标 `MenuItem.isEnabled`）。
     *
     * @returns {Promise<boolean>}
     */
    async isEnabled() {
        return this._enabled;
    }

    /**
     * 设置是否可用（对标 `MenuItem.setEnabled`）。
     *
     * @param {boolean} enabled
     * @returns {Promise<void>}
     */
    async setEnabled(enabled) {
        this._enabled = enabled !== false;
    }

    /**
     * 设置加速键（对标 `MenuItem.setAccelerator`）。
     *
     * @param {string | null} accelerator `null` 表示移除。
     * @returns {Promise<void>}
     * @remarks Tauri 没有 `accelerator()` 这个读接口——只有 setter。
     * 所以本库也不给：多一个 getter 就是一处自造的 API 面。
     */
    async setAccelerator(accelerator) {
        this._accelerator = typeof accelerator === 'string' ? accelerator : null;
    }

    /**
     * @override
     * @param {string} id
     * @param {Map<string, (id: string) => void>} actions
     * @param {string} token
     * @returns {object}
     */
    _wireCore(id, actions, token) {
        return {
            ...super._wireCore(id, actions, token),
            accelerator: this._accelerator,
        };
    }
}

/**
 * 勾选菜单项（对标 `CheckMenuItem`）。
 */
export class CheckMenuItem extends MenuItem {
    /**
     * @param {{ id?: string, text: string, enabled?: boolean, checked?: boolean, accelerator?: string, action?: (id: string) => void }} options
     */
    constructor(options) {
        super(options);
        /** @type {boolean} */
        this._checked = options?.checked === true;
    }

    /**
     * @override
     * @param {{ id?: string, text: string, enabled?: boolean, checked?: boolean, accelerator?: string, action?: (id: string) => void }} [options]
     * @returns {Promise<CheckMenuItem>}
     */
    static async new(options = { text: '' }) {
        return new CheckMenuItem(options);
    }

    /** @returns {Promise<boolean>} 是否勾选。 */
    async isChecked() {
        return this._checked;
    }

    /**
     * @param {boolean} checked
     * @returns {Promise<void>}
     */
    async setChecked(checked) {
        this._checked = checked === true;
    }

    /**
     * @override
     * @param {string} id
     * @param {Map<string, (id: string) => void>} actions
     * @param {string} token
     * @returns {object}
     */
    _wireCore(id, actions, token) {
        return {
            ...super._wireCore(id, actions, token),
            kind: 'check',
            checked: this._checked,
        };
    }
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
     * @param {{ id?: string, text: string, enabled?: boolean, accelerator?: string, icon?: unknown, action?: (id: string) => void }} [options]
     */
    constructor(options = { text: '' }) {
        super(options);
        /** @type {unknown} */
        this._icon = options?.icon;
    }

    /**
     * @override
     * @param {{ id?: string, text: string, enabled?: boolean, accelerator?: string, icon?: unknown, action?: (id: string) => void }} [options]
     * @returns {Promise<IconMenuItem>}
     */
    static async new(options = { text: '' }) {
        return new IconMenuItem(options);
    }

    /**
     * @param {unknown} _icon
     * @returns {Promise<void>}
     * @throws {Error} 本库暂不支持菜单图标（没有图像资源表）。
     */
    async setIcon(_icon) {
        throw new Error(
            'IconMenuItem.setIcon 未实现：本库没有菜单图标的资源通道（见 README 的"没有句柄表"）。');
    }

    /**
     * @override
     * @param {string} id
     * @param {Map<string, (id: string) => void>} actions
     * @param {string} token
     * @returns {object}
     */
    _wireCore(id, actions, token) {
        return {
            ...super._wireCore(id, actions, token),
            kind: 'icon',
        };
    }
}

/**
 * 预定义（系统标准）菜单项（对标 `PredefinedMenuItem`）。
 */
export class PredefinedMenuItem extends MenuItemBase {
    /**
     * @param {PredefinedMenuItemOptions} [options]
     */
    constructor(options = {}) {
        super(options);

        // Tauri 的 `item` 有两种形态：字符串枚举，或 `{ About: metadata | null }`。
        // 后者是**唯一**能带数据的一项（关于对话框要应用名/版本/作者）。
        // 把对象形态当字符串处理会让它落成默认的 Separator——一个静默的错误菜单项。
        const raw = options?.item;

        /** @type {string | { About: unknown }} */
        this._item = typeof raw === 'string'
            ? raw
            : raw !== null && typeof raw === 'object' && 'About' in raw
                ? { About: raw.About ?? null }
                : 'Separator';
    }

    /**
     * @param {PredefinedMenuItemOptions} [options]
     * @returns {Promise<PredefinedMenuItem>}
     */
    static async new(options = { item: 'Separator' }) {
        return new PredefinedMenuItem(options);
    }

    /**
     * 显示文本（对标 `PredefinedMenuItem.text`）。
     *
     * @returns {Promise<string>}
     * @remarks 预定义项**只有** `text` / `setText`——没有 `isEnabled` / `setEnabled`。
     * 这不是漏了：Tauri 那边也没有（预定义项的可用性由平台自己决定，
     * 例如没有选区时 `Copy` 自动灰掉）。
     */
    async text() {
        return this._text;
    }

    /**
     * 设置显示文本（对标 `PredefinedMenuItem.setText`）。
     *
     * @param {string} text
     * @returns {Promise<void>}
     */
    async setText(text) {
        this._text = String(text);
    }

    /**
     * @override
     * @param {string} id
     * @param {Map<string, (id: string) => void>} actions
     * @param {string} token
     * @returns {object}
     */
    _wireCore(id, actions, token) {
        void actions;
        void token;

        return {
            kind: 'predefined',
            item: this._item,
            text: this._text === '' ? null : this._text,
            enabled: this._enabled,
        };
    }
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
     * @param {{ id?: string, text: string, enabled?: boolean, items?: any[] }} options
     */
    constructor(options) {
        super(options);
        /** @type {MenuItemBase[]} */
        this._items = Array.isArray(options?.items) ? options.items.map(toItem) : [];
    }

    /**
     * @param {{ id?: string, text: string, enabled?: boolean, items?: any[] }} [options]
     * @returns {Promise<Submenu>}
     */
    static async new(options = { text: '' }) {
        return new Submenu(options);
    }

    // 以下四个方法与 `MenuItem` 上的是同一套语义，但**不能放在基类上**：
    // `Menu` 与 `PredefinedMenuItem` 在 Tauri 里没有它们（前者是容器，
    // 后者只有 text/setText）。放基类会让那两个类凭空多出方法。
    // 抄两份而不是抽 mixin：只有两组、每段四行，抽出去的间接层比重复更难读。

    /** @returns {Promise<string>} 显示文本。 */
    async text() {
        return this._text;
    }

    /**
     * @param {string} text
     * @returns {Promise<void>}
     */
    async setText(text) {
        this._text = String(text);
    }

    /** @returns {Promise<boolean>} 是否可用。 */
    async isEnabled() {
        return this._enabled;
    }

    /**
     * @param {boolean} enabled
     * @returns {Promise<void>}
     */
    async setEnabled(enabled) {
        this._enabled = enabled !== false;
    }

    /**
     * @param {any} items 一项或多项。
     * @returns {Promise<void>}
     */
    async append(items) {
        this._items.push(...asArray(items).map(toItem));
    }

    /**
     * @param {any} items
     * @returns {Promise<void>}
     */
    async prepend(items) {
        this._items.unshift(...asArray(items).map(toItem));
    }

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
    async insert(items, position) {
        this._items.splice(clampIndex(position, this._items.length), 0, ...asArray(items).map(toItem));
    }

    /**
     * 移除一项（对标 `Submenu.remove`）。
     *
     * @param {any} item 要移除的项**对象**（不是 id）。
     * @returns {Promise<void>}
     * @remarks Tauri 收的是项对象，因为它能按 rid 认。本库按**对象身份**认
     * （`indexOf`），传一个"内容相同但不是同一个实例"的对象不会命中——
     * 与 Tauri 的 rid 语义一致：换一个实例就是换了一个菜单项。
     */
    async remove(item) {
        const index = this._items.indexOf(item);
        if (index >= 0) {
            this._items.splice(index, 1);
        }
    }

    /**
     * 移除指定位置的项（对标 `Submenu.removeAt`）。
     *
     * @param {number} position
     * @returns {Promise<MenuItemBase | null>} 被移除的项；位置越界时 `null`。
     */
    async removeAt(position) {
        const index = clampIndex(position, this._items.length);
        if (index >= this._items.length) {
            return null;
        }

        const [removed] = this._items.splice(index, 1);
        return removed ?? null;
    }

    /** @returns {Promise<MenuItemBase[]>} 子项（副本，改它不影响这里）。 */
    async items() {
        return [...this._items];
    }

    /**
     * 按 id 取项（对标 `Submenu.get`）。
     *
     * @param {string} id
     * @returns {Promise<MenuItemBase | null>}
     * @remarks 只找**直接子项**，不递归——Tauri 的 `get` 也是查它自己那一层。
     */
    async get(id) {
        return this._items.find((item) => item.id === id) ?? null;
    }

    /**
     * 把这个子菜单设为 macOS 的 Window 菜单（对标 `Submenu.setAsWindowsMenuForNSApp`）。
     *
     * @returns {Promise<never>}
     * @throws {Error} 未实现（属于菜单栏批次 B6b）。
     */
    async setAsWindowsMenuForNSApp() {
        throw new Error(
            'Submenu.setAsWindowsMenuForNSApp 未实现：它属于 macOS 菜单栏（B6b），' +
            '本批只做弹出菜单。');
    }

    /**
     * 把这个子菜单设为 macOS 的 Help 菜单（对标 `Submenu.setAsHelpMenuForNSApp`）。
     *
     * @returns {Promise<never>}
     * @throws {Error} 未实现（属于菜单栏批次 B6b）。
     */
    async setAsHelpMenuForNSApp() {
        throw new Error(
            'Submenu.setAsHelpMenuForNSApp 未实现：它属于 macOS 菜单栏（B6b），' +
            '本批只做弹出菜单。');
    }

    /**
     * 设置子菜单图标（对标 `Submenu.setIcon`）。
     *
     * @param {unknown} _icon
     * @returns {Promise<never>}
     * @throws {Error} 本库没有菜单图标的资源通道。
     */
    async setIcon(_icon) {
        throw new Error(
            'Submenu.setIcon 未实现：本库没有菜单图标的资源通道（见 README 的"没有句柄表"）。');
    }

    /**
     * @param {{ type?: string, x: number, y: number } | [number, number] | null} [at]
     * @param {{ label?: string } | null} [window]
     * @returns {Promise<void>}
     */
    async popup(at = null, window = null) {
        await popupItems(this._items, at, window);
    }

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
    async close(window = null) {
        await invoke('menu.close', {
            label: typeof window?.label === 'string' ? window.label : null,
        });
    }

    /**
     * @override
     * @param {string} id
     * @param {Map<string, (id: string) => void>} actions
     * @param {string} token
     * @returns {object}
     */
    _wireCore(id, actions, token) {
        return {
            kind: 'submenu',
            id,
            text: this._text,
            enabled: this._enabled,
            // 递归序列化：actions / token 一路往下传，子项里的 action 才进得了本次弹出的路由表。
            items: this._items.map((item) => item._wire(actions, token)),
        };
    }
}

/**
 * 菜单（对标 `Menu`）：一个菜单项的容器，可 popup。
 *
 * @remarks 在 Tauri 里它还能 `setAsAppMenu` / `setAsWindowMenu`（菜单栏）。
 * 那两条要动窗口布局，属于后续批次，本批没有。
 */
export class Menu extends MenuItemBase {
    /**
     * @param {{ id?: string, items?: any[] }} [options]
     */
    constructor(options = {}) {
        super(options);
        /** @type {MenuItemBase[]} */
        this._items = Array.isArray(options.items) ? options.items.map(toItem) : [];
    }

    /**
     * @param {{ id?: string, items?: any[] }} [options]
     * @returns {Promise<Menu>}
     */
    static async new(options = {}) {
        return new Menu(options);
    }

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
    static async default() {
        return new Menu({
            items: [
                {
                    text: 'Edit',
                    items: [
                        { item: 'Undo' },
                        { item: 'Redo' },
                        { item: 'Separator' },
                        { item: 'Cut' },
                        { item: 'Copy' },
                        { item: 'Paste' },
                        { item: 'SelectAll' },
                    ],
                },
                {
                    text: 'Window',
                    items: [
                        { item: 'Minimize' },
                        { item: 'Maximize' },
                        { item: 'CloseWindow' },
                    ],
                },
                { text: 'Help', items: [] },
            ],
        });
    }

    /**
     * @param {any} items
     * @returns {Promise<void>}
     */
    async append(items) {
        this._items.push(...asArray(items).map(toItem));
    }

    /**
     * @param {any} items
     * @returns {Promise<void>}
     */
    async prepend(items) {
        this._items.unshift(...asArray(items).map(toItem));
    }

    /**
     * 在指定位置插入（对标 `Menu.insert`）。
     *
     * @param {any} items 一项或多项。
     * @param {number} position 插入位置；越界按两端处理。
     * @returns {Promise<void>}
     */
    async insert(items, position) {
        this._items.splice(clampIndex(position, this._items.length), 0, ...asArray(items).map(toItem));
    }

    /**
     * 移除一项（对标 `Menu.remove`）。
     *
     * @param {any} item 要移除的项**对象**（不是 id）；按对象身份匹配。
     * @returns {Promise<void>}
     */
    async remove(item) {
        const index = this._items.indexOf(item);
        if (index >= 0) {
            this._items.splice(index, 1);
        }
    }

    /**
     * 移除指定位置的项（对标 `Menu.removeAt`）。
     *
     * @param {number} position
     * @returns {Promise<MenuItemBase | null>} 被移除的项；位置越界时 `null`。
     */
    async removeAt(position) {
        const index = clampIndex(position, this._items.length);
        if (index >= this._items.length) {
            return null;
        }

        const [removed] = this._items.splice(index, 1);
        return removed ?? null;
    }

    /** @returns {Promise<MenuItemBase[]>} 子项（副本）。 */
    async items() {
        return [...this._items];
    }

    /**
     * 按 id 取项（对标 `Menu.get`）。
     *
     * @param {string} id
     * @returns {Promise<MenuItemBase | null>}
     */
    async get(id) {
        return this._items.find((item) => item.id === id) ?? null;
    }

    /**
     * 设为应用菜单栏（对标 `Menu.setAsAppMenu`）。
     *
     * @returns {Promise<never>}
     * @throws {Error} 未实现（B6b）。
     */
    async setAsAppMenu() {
        throw new Error(
            'Menu.setAsAppMenu 未实现：菜单栏属于 B6b（macOS 要 [NSApp setMainMenu:]，' +
            'Windows/Linux 要动窗口布局）。本批只做 Menu.popup。');
    }

    /**
     * 设为某个窗口的菜单栏（对标 `Menu.setAsWindowMenu`）。
     *
     * @param {unknown} [_window]
     * @returns {Promise<never>}
     * @throws {Error} 未实现（B6b）。
     */
    async setAsWindowMenu(_window) {
        throw new Error(
            'Menu.setAsWindowMenu 未实现：菜单栏属于 B6b（Linux 要把 GtkWindow 改成 ' +
            'GtkBox{menubar, webview}，客户区尺寸契约会变）。本批只做 Menu.popup。');
    }

    /**
     * 弹出这个菜单（对标 `Menu.popup`）。
     *
     * @param {{ type?: string, x: number, y: number } | null} [at]
     *   弹出位置。物理与逻辑坐标都收（`type: 'Physical' | 'Logical'`），省略时跟随鼠标。
     * @param {{ label?: string } | null} [window]
     *   弹到哪个窗口上；省略时是**发起调用的那个窗口**。
     * @returns {Promise<void>}
     * @remarks
     * <p><b>与 Tauri 的完成时机不同，这是本库的已知差异</b>：Tauri 的文档说"菜单一显示就
     * resolve"，本库 <b>Windows 上会等到用户选完或取消</b>（`TrackPopupMenuEx` 自带模态
     * 消息循环），macOS / Linux 立即 resolve。所以：<b>别在 `await menu.popup()` 之后
     * 假设"用户已经选过了"</b>，可靠的完成信号是各项的 `action`。
     * <p>把整棵菜单树一次性发给宿主，之后改这个菜单<b>不影响</b>已经弹出的那个（它是快照）。
     */
    async popup(at = null, window = null) {
        await popupItems(this._items, at, window);
    }

    /**
     * 关掉当前正在显示的菜单（**本库扩展**，Tauri 没有这个方法）。
     *
     * @param {{ label?: string } | null} [window] 菜单弹在哪个窗口上；省略时是发起者。
     * @returns {Promise<void>}
     * @remarks
     * 与 `Submenu.close()` 同一条理由：Windows 的 `popup()` 阻塞，页面必须有
     * 一条"用代码关掉它"的路。没有菜单在屏上是空操作。
     */
    async close(window = null) {
        await invoke('menu.close', {
            label: typeof window?.label === 'string' ? window.label : null,
        });
    }

    /**
     * @override
     * @param {string} id
     * @param {Map<string, (id: string) => void>} actions
     * @param {string} token
     * @returns {object}
     * @remarks
     * **`Menu` 被当成菜单项传进来时按子菜单序列化**。Tauri 的类型签名不允许这样
     * （`MenuOptions.items` 里没有 `Menu`），但运行期拦不住——而继承来的默认实现会把它
     * 序列化成 `kind: 'item'` 且**丢掉整棵子项**，表现是"子菜单弹出来是空的"。
     * 与其静默丢内容，不如按它实际的结构（一个装项的容器）序列化成子菜单。
     */
    _wireCore(id, actions, token) {
        return {
            kind: 'submenu',
            id,
            text: this._text,
            enabled: this._enabled,
            items: this._items.map((item) => item._wire(actions, token)),
        };
    }
}

/**
 * 把一棵菜单树发给宿主弹出。
 *
 * @param {MenuItemBase[]} items
 * @param {{ type?: string, x: number, y: number } | [number, number] | null} at
 *   弹出位置。与 Tauri 一样收"物理/逻辑坐标对象"，本库额外允许 `[x, y]`（`normalizePosition` 的既有约定）。
 * @param {{ label?: string } | null} window
 * @returns {Promise<void>}
 * @internal
 */
async function popupItems(items, at, window) {
    const token = Math.random().toString(36).slice(2, 10);
    const actions = new Map();

    const wire = items.map((item) => item._wire(actions, token));

    // 先装监听再发命令：Windows 的弹出是阻塞的，点击可能在 invoke 返回前就发生。
    await ensureClickListener();

    clickRouting.token = token;
    clickRouting.actions = actions;

    await invoke('menu.popup', {
        items: wire,
        position: at === null || at === undefined ? null : normalizePosition(at),
        label: typeof window?.label === 'string' ? window.label : null,
    });
}

/**
 * 统一成数组。
 *
 * @param {any} items
 * @returns {any[]}
 * @internal
 */
function asArray(items) {
    return Array.isArray(items) ? items : [items];
}

/**
 * 把插入/删除的位置夹到合法区间（越界按两端处理，与 DOM/数组的常规约定一致）。
 *
 * @param {number} position
 * @param {number} length
 * @returns {number}
 * @internal
 */
function clampIndex(position, length) {
    const index = Number.isFinite(position) ? Math.trunc(position) : length;
    return Math.min(Math.max(index, 0), length);
}

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
export function itemFromKind([, id, kind]) {
    const options = { id: typeof id === 'string' ? id : '' };

    switch (kind) {
        case 'Submenu':
            return new Submenu({ ...options, text: '' });
        case 'Predefined':
            return new PredefinedMenuItem(options);
        case 'Check':
            return new CheckMenuItem({ ...options, text: '' });
        case 'Icon':
            return new IconMenuItem({ ...options, text: '' });
        case 'MenuItem':
        default:
            return new MenuItem({ ...options, text: '' });
    }
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
export const NativeIcon = Object.freeze({
    Add: "Add",
    Advanced: "Advanced",
    Bluetooth: "Bluetooth",
    Bookmarks: "Bookmarks",
    Caution: "Caution",
    ColorPanel: "ColorPanel",
    ColumnView: "ColumnView",
    Computer: "Computer",
    EnterFullScreen: "EnterFullScreen",
    Everyone: "Everyone",
    ExitFullScreen: "ExitFullScreen",
    FlowView: "FlowView",
    Folder: "Folder",
    FolderBurnable: "FolderBurnable",
    FolderSmart: "FolderSmart",
    FollowLinkFreestanding: "FollowLinkFreestanding",
    FontPanel: "FontPanel",
    GoLeft: "GoLeft",
    GoRight: "GoRight",
    Home: "Home",
    IChatTheater: "IChatTheater",
    IconView: "IconView",
    Info: "Info",
    InvalidDataFreestanding: "InvalidDataFreestanding",
    LeftFacingTriangle: "LeftFacingTriangle",
    ListView: "ListView",
    LockLocked: "LockLocked",
    LockUnlocked: "LockUnlocked",
    MenuMixedState: "MenuMixedState",
    MenuOnState: "MenuOnState",
    MobileMe: "MobileMe",
    MultipleDocuments: "MultipleDocuments",
    Network: "Network",
    Path: "Path",
    PreferencesGeneral: "PreferencesGeneral",
    QuickLook: "QuickLook",
    Refresh: "Refresh",
    RefreshFreestanding: "RefreshFreestanding",
    Remove: "Remove",
    RevealFreestanding: "RevealFreestanding",
    RightFacingTriangle: "RightFacingTriangle",
    Share: "Share",
    Slideshow: "Slideshow",
    SmartBadge: "SmartBadge",
    StatusAvailable: "StatusAvailable",
    StatusNone: "StatusNone",
    StatusPartiallyAvailable: "StatusPartiallyAvailable",
    StatusUnavailable: "StatusUnavailable",
    StopProgress: "StopProgress",
    StopProgressFreestanding: "StopProgressFreestanding",
    TrashEmpty: "TrashEmpty",
    TrashFull: "TrashFull",
    User: "User",
    UserAccounts: "UserAccounts",
    UserGroup: "UserGroup",
    UserGuest: "UserGuest",
});
