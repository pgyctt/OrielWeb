// 对标 `@tauri-apps/api/app`。
//
// 分两类：
//
//   * **只读元信息**（getName / getVersion / getIdentifier / getTauriVersion /
//     supportsMultipleWindows）——宿主侧整组始终放行。
//   * **会改变应用状态的能力**（exit / show / hide）——宿主侧**不在** always-allowed
//     名单里，应用必须显式 `Allow("app.exit")` 之类才放行。名字在同一个模块里，
//     门禁姿态不同：Tauri 也是同名模块，但它没有本库这套命令级 allow/deny。
//
// B23 之后，Tauri `app` 模块的导出面**一条不缺**。其中四项有平台事实要记住：
//
//   * `setDockVisibility` —— 只有 macOS 有（Dock 是 macOS 的应用级概念）。
//     Windows 的任务栏按钮属于单个窗口（`window.setSkipTaskbar`），Linux 的任务条目
//     由窗口管理器按窗口生成。那两个平台抛 `not_supported`。
//   * `getBundleType` / `BundleType` —— 返回"这个应用被什么形态打包/安装的"。
//     本库不是打包器（打包交给 Velopack），所以只能**观察**：在 `.app` 里 → `app`、
//     旁边有 Velopack 的 `Update.exe` → `nsis`、跑在 AppImage 里 → `appimage`，
//     都观察不到就是 `null`（开发期/手工解压的情形，与 Tauri 的 `null` 同义）。
//     `msi` / `deb` / `rpm` 运行期彼此不可区分，要精确回答得用程序集元数据
//     `OrielBundleType` 显式声明——详见 docs/API.md。
//   * `defaultWindowIcon` —— 图标来源是**可执行文件旁边的 app.png / app.ico**（没有资源表）。
//   * `fetchDataStoreIdentifiers` / `removeDataStore` —— 只有 macOS 12+ 有
//     （`WKWebsiteDataStore` 的按 UUID 隔离）；另两个平台抛 `not_supported`。
//   * `onBackButtonPress` —— 桌面端没有返回键，所以**订阅是真的、事件永不触发**
//     （与 Tauri 在桌面端的行为一致：它也只是 `listen('back-button', …)`）。

import { invoke } from './core.js';
import { listen } from './event.js';
import { Image } from './image.js';
import { base64ToBytes, bytesToBase64 } from './image-bytes.js';

/**
 * 数据存储标识符（对标 Tauri 的 `DataStoreIdentifier`）：**16 个 0–255 的整数**。
 *
 * @remarks Tauri 用的是定长元组而不是 `Uint8Array`，因为它是从 Rust 的 `[u8; 16]`
 * 直接序列化过来的。本库照它的形状走——迁移代码可能写 `id[0]` 或把它当数组下标访问。
 *
 * @typedef {[number, number, number, number, number, number, number, number,
 *   number, number, number, number, number, number, number, number]} DataStoreIdentifier
 */

/**
 * `onBackButtonPress` 的回调参数（对标 Tauri 的 `OnBackButtonPressPayload`）。
 *
 * @typedef {object} OnBackButtonPressPayload
 * @property {boolean} canGoBack 页面是否还有历史可回退。**桌面端永远不会收到这个回调**。
 */

/**
 * 应用的产物类型（对标 Tauri 的 `BundleType`）。
 *
 * 取值与 Tauri 逐键一致。`getBundleType()` 返回其中之一或 `null`。
 *
 * @type {Readonly<{
 *   Nsis: 'nsis', Msi: 'msi', Deb: 'deb', Rpm: 'rpm', AppImage: 'appimage', App: 'app'
 * }>}
 */
export const BundleType = Object.freeze({
    Nsis: 'nsis',
    Msi: 'msi',
    Deb: 'deb',
    Rpm: 'rpm',
    AppImage: 'appimage',
    App: 'app',
});

/**
 * 应用显示名（对标 Tauri 的 `tauri.conf.json > productName`）。
 *
 * @returns {Promise<string>}
 * @remarks **无需能力声明**（只读元信息，整组放行）。下同。
 */
export function getName() {
    return invoke('app.getName');
}

/**
 * 应用版本（对标 Tauri 的 `tauri.conf.json > version`）。
 *
 * @returns {Promise<string>}
 * @remarks
 * 默认取自入口程序集的版本信息，所以它跟随 csproj 的 `<Version>`；
 * 显式配置见宿主侧的 `WithAppInfo`。**构建元数据（`+` 之后那一段）会被去掉**——
 * 那里面通常是 git 提交号，参与版本比较时会让同一个发布版本看起来"不相等"。
 */
export function getVersion() {
    return invoke('app.getVersion');
}

/**
 * 应用标识符，形如 `com.example.app`（对标 Tauri 的 `tauri.conf.json > identifier`）。
 *
 * @returns {Promise<string>}
 * @remarks
 * 它是**应用数据目录的组成部分**（见文档里的"应用数据目录"一节）。
 * 没显式配置时从程序集名推断，正式应用应当显式给——等数据已经写进去之后再改就要做迁移。
 */
export function getIdentifier() {
    return invoke('app.getIdentifier');
}

/**
 * 库版本（对标 Tauri 的 `getTauriVersion()`）。
 *
 * @returns {Promise<string>}
 * @remarks
 * **语义提醒**：Tauri 的这个函数返回**框架**版本（`2.x.y`），本库返回**库**版本
 * （`0.4.0` 这一类）。数字放在一起比较没有意义——迁移时如果代码里有
 * `getTauriVersion()` 的版本号判断，那一段必须重写，不能照搬。
 *
 * 与 `window.oriel.version` 同源（都来自程序集版本），所以两处做能力探测不会得到矛盾答案。
 */
export function getTauriVersion() {
    return invoke('app.getTauriVersion');
}

/**
 * 宿主是否支持多窗口。
 *
 * @returns {Promise<boolean>}
 * @remarks
 * 三平台都支持，所以**恒为 `true`**。保留它是因为 Tauri 上它是个真查询
 * （移动端返回 `false`），迁移代码里常有 `if (await supportsMultipleWindows())` 这类分支；
 * 删掉会让那些代码编译不过。没有谎报：桌面端本来就是 true。
 */
export function supportsMultipleWindows() {
    return invoke('app.supportsMultipleWindows');
}

/**
 * 这个应用被什么形态打包/安装的（对标 Tauri 的 `getBundleType()`）。
 *
 * @returns {Promise<'nsis' | 'msi' | 'deb' | 'rpm' | 'appimage' | 'app' | null>}
 * @remarks
 * **无需能力声明**（只读元信息）。
 *
 * `null` 表示"不是已知的打包产物"（开发期直接跑、或手工解压），与 Tauri 在
 * 未打包时的返回值同义。本库的推断只看运行期事实（见文件头），所以 `msi` / `deb` /
 * `rpm` 永远不会由推断得出——要精确回答请在 csproj 里声明：
 * `<AssemblyMetadata Include="OrielBundleType" Value="msi" />`。
 */
export function getBundleType() {
    return invoke('app.getBundleType');
}

/**
 * 应用默认窗口图标（对标 Tauri 的 `defaultWindowIcon()`）。
 *
 * @returns {Promise<import('./image.js').Image | null>} 没有配置图标时为 `null`。
 * @remarks
 * **无需能力声明**（只读元信息）。
 *
 * 图标来源是**可执行文件旁边的 `app.png`（或 `app.ico`）**——本库没有 Tauri 那样的
 * 资源表。文件不存在或不是 PNG/ICO 时返回 `null`，并在宿主侧记一条诊断。
 *
 * 结果在宿主进程内只读一次：启动后再把文件放进去需要重启才能看到。
 */
export async function defaultWindowIcon() {
    const encoded = await invoke('app.defaultWindowIcon');

    // 线格式是 base64 字符串（不是对象）：只读的这一层没有 JSON 上下文，
    // 而 `Image` 在页面侧本来就是字节容器，包一层对象只是多一次解包。
    return encoded === null || encoded === undefined
        ? null
        : Image.fromBytes(base64ToBytes(encoded));
}

/**
 * 退出应用（对标 Tauri 的 `exit(code?)`）。
 *
 * @param {number} [code] 进程退出码；省略时保持进程原有的退出码。
 * @returns {Promise<void>}
 * @remarks
 * **需要能力声明**（`app.exit`）——它能终止应用，不属于"读元信息"那一类。
 *
 * 与 Tauri 的差别只在"怎么退"：Tauri 直接 `process::exit(code)`，本库走
 * `Environment.ExitCode` + 正常的退出流程，所以 `Exiting` / `ExitCompleted` 这些
 * 宿主侧事件照常触发。若退出被看门狗强制结束，用的是 `UseForceExitCode`
 * （默认 2），**不是**这里传的值。
 *
 * 这个 Promise 通常**不会 resolve**：应用在回执写回之前就退出了。别 `await` 它再做事，
 * 也不要把它放在"失败就降级"的分支里。
 */
export function exit(code) {
    return invoke('app.exit', code === undefined ? {} : { code });
}

/**
 * 显示全部窗口（对标 Tauri 的 `show()`）。
 *
 * @returns {Promise<void>}
 * @remarks
 * **需要能力声明**（`app.show`）。语义是"显示所有窗口"，**不是** macOS 的
 * "取消应用隐藏"（`NSApp.unhide:`）——三平台一致，不按平台分叉。
 */
export function show() {
    return invoke('app.show');
}

/**
 * 隐藏全部窗口（对标 Tauri 的 `hide()`）。
 *
 * @returns {Promise<void>}
 * @remarks **需要能力声明**（`app.hide`）。语义是"隐藏所有窗口"，理由同 {@link show}。
 */
export function hide() {
    return invoke('app.hide');
}

/**
 * 显示 / 隐藏 macOS 的 Dock 图标（对标 Tauri 的 `setDockVisibility()`）。
 *
 * @param {boolean} visible `false` = 隐藏 Dock 图标（同时没有菜单栏、不出现在 Cmd-Tab 里）。
 * @returns {Promise<void>}
 * @remarks
 * **需要能力声明**（`app.setDockVisibility`）——它改变应用在系统里的呈现方式。
 *
 * **只有 macOS 有**：另两个平台抛 `not_supported`。Windows 上"隐藏任务栏按钮"是
 * **窗口级**的（`Window.setSkipTaskbar`），Linux 的任务条目由窗口管理器按窗口生成。
 *
 * 隐藏之后窗口照常显示——这正是托盘弹窗、HUD 这类无 Dock 应用的用法。
 */
export function setDockVisibility(visible) {
    if (typeof visible !== 'boolean') {
        throw new TypeError(`setDockVisibility: visible 必须是布尔值，收到 ${typeof visible}`);
    }

    return invoke('app.setDockVisibility', { value: visible });
}

/**
 * 覆写**应用默认**的主题（对标 Tauri 的 `App.setTheme`）。
 *
 * @param {'light' | 'dark' | null} [theme] `null` = 跟随系统
 * @returns {Promise<void>}
 * @remarks
 * **三级模型**：窗口覆写（`window.setTheme`）> 应用覆写（本条）> 系统主题。
 * 这一条只改"应用默认"，**不动已经自己设过 `window.setTheme` 的窗口**——
 * 那些窗口已经明确表态"我就要这个主题"。
 *
 * **平台落点见 `Window.setTheme` 的说明**：Windows 只改标题栏 / 边框那圈，
 * macOS 改 `NSWindow.appearance`，**Linux 上落到应用级**（GTK 没有"按窗口选主题"
 * 的入口）——所以 Linux 上 `window.setTheme` 与 `app.setTheme` 落到同一个地方、
 * 按调用次序后者赢。
 *
 * **需要能力声明**（`app.setTheme`）——它改变应用外观，不在只读元信息那组里。
 */
export function setTheme(theme) {
    if (theme !== undefined && theme !== null && theme !== 'light' && theme !== 'dark') {
        throw new TypeError(`setTheme: theme 必须是 'light' / 'dark' / null，收到 ${JSON.stringify(theme)}`);
    }

    return invoke('app.setTheme', { value: theme ?? null });
}

/**
 * 列出本应用用过的数据存储标识符（对标 Tauri 的 `fetchDataStoreIdentifiers()`）。
 *
 * @returns {Promise<DataStoreIdentifier[]>} 每个元素是 16 个整数的 UUID（与 Tauri 同形状）。
 * @remarks
 * **无需能力声明**（只读元信息）。
 *
 * **只有 macOS 12+ 有**：那边的 `WKWebsiteDataStore` 支持"按 UUID 隔离一份浏览数据"，
 * 标识符由 `OrielWindowOptions.DataStoreIdentifier` 指定。另两个平台抛 `not_supported`。
 *
 * 表里是**本应用创建过的**标识符（宿主落在应用数据目录的 `data-stores.json` 里），
 * 不是 WebKit 的全局枚举——WebKit 的枚举是异步回调接口，与同步的 IPC 请求-回执接不上。
 */
export async function fetchDataStoreIdentifiers() {
    /** @type {string[]} */
    const identifiers = await invoke('app.fetchDataStoreIdentifiers');

    // 宿主发的是 base64（16 字节），这里转成 Tauri 的"16 个数字"形状。
    // 用 Array.from 而不是直接返回 Uint8Array：`id[0]`、`id.length`、JSON 化三处行为
    // 都与 Tauri 一致，而 Uint8Array 在 JSON 化时会变成对象。
    return identifiers.map((encoded) => /** @type {DataStoreIdentifier} */ (
        /** @type {any} */ (Array.from(base64ToBytes(encoded)))));
}

/**
 * 删除一份按标识符隔离的浏览数据（对标 Tauri 的 `removeDataStore()`）。
 *
 * @param {DataStoreIdentifier} identifier 16 个整数的 UUID（来自 {@link fetchDataStoreIdentifiers}）。
 * @returns {Promise<void>}
 * @remarks
 * **需要能力声明**（`app.removeDataStore`）——它会删数据。
 *
 * **只有 macOS 12+ 有**，另两个平台抛 `not_supported`。
 *
 * 还有窗口在使用这个标识符时会**拒绝删除**（宿主抛 `invalid_argument` 一类的错误）：
 * 那种情况下 WebKit 会把数据立刻写回来，"删了但没删"比报错更糟。先关掉那些窗口。
 */
export function removeDataStore(identifier) {
    // 形参类型是 Tauri 的定长元组；运行时也接受 Uint8Array（同样是 16 字节的常见形态）。
    const raw = /** @type {any} */ (identifier);
    const bytes = Array.isArray(raw) || raw instanceof Uint8Array ? Array.from(raw) : null;

    if (bytes === null || bytes.length !== 16) {
        throw new TypeError(
            'removeDataStore: identifier 必须是 16 个整数的数组（UUID），'
            + `收到 ${bytes === null ? typeof identifier : `${bytes.length} 项`}`);
    }

    // 逐项校验 0–255：`[300, …]` 之类的输入在 Uint8Array 里会**静默回绕**，
    // 于是删掉的是另一个存储——那是不可逆的错误，所以在入口就拒。
    for (const value of bytes) {
        if (!Number.isInteger(value) || value < 0 || value > 255) {
            throw new TypeError(`removeDataStore: identifier 的每一项必须是 0–255 的整数，收到 ${value}`);
        }
    }

    return invoke('app.removeDataStore', { value: bytesToBase64(Uint8Array.from(bytes)) });
}

/**
 * 监听"系统返回键"（对标 Tauri 的 `onBackButtonPress()`）。
 *
 * @param {(payload: OnBackButtonPressPayload) => void} handler
 * @returns {Promise<{ unregister: () => Promise<void> }>} 监听器句柄；调 `unregister()` 退订。
 * @remarks
 * **桌面端没有返回键，所以这个回调永远不会被调用。** 订阅本身是真的（`listen('back-button')`），
 * `unregister()` 也照常工作——只是宿主永远不会发那个事件。
 *
 * 保留它是为了让"同一份代码同时跑桌面与移动端"的迁移项目不必在 import 处就分叉；
 * Tauri 在桌面端也是这么做的（它的实现同样只是 `listen('back-button', …)`）。
 *
 * 返回的是 `{ unregister }` 而不是裸函数：Tauri 那边返回 `PluginListener`（一个句柄类），
 * 迁移代码写的是 `await listener.unregister()`。本库没有插件体系，所以这里给一个
 * **结构上兼容**的最小对象，而不是把 `PluginListener` 整个造出来。
 */
export async function onBackButtonPress(handler) {
    if (typeof handler !== 'function') {
        throw new TypeError('onBackButtonPress: handler 必须是函数');
    }

    const unlisten = listen('back-button', (event) => {
        // 桌面端走不到这里；形状按 Tauri 的 payload 给（`canGoBack` 来自事件 payload）。
        handler({ canGoBack: Boolean(event && event.payload && event.payload.canGoBack) });
    });

    return {
        unregister: async () => {
            unlisten();
        },
    };
}
