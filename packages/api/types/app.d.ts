/**
 * 应用显示名（对标 Tauri 的 `tauri.conf.json > productName`）。
 *
 * @returns {Promise<string>}
 * @remarks **无需能力声明**（只读元信息，整组放行）。下同。
 */
export function getName(): Promise<string>;
/**
 * 应用版本（对标 Tauri 的 `tauri.conf.json > version`）。
 *
 * @returns {Promise<string>}
 * @remarks
 * 默认取自入口程序集的版本信息，所以它跟随 csproj 的 `<Version>`；
 * 显式配置见宿主侧的 `WithAppInfo`。**构建元数据（`+` 之后那一段）会被去掉**——
 * 那里面通常是 git 提交号，参与版本比较时会让同一个发布版本看起来"不相等"。
 */
export function getVersion(): Promise<string>;
/**
 * 应用标识符，形如 `com.example.app`（对标 Tauri 的 `tauri.conf.json > identifier`）。
 *
 * @returns {Promise<string>}
 * @remarks
 * 它是**应用数据目录的组成部分**（见文档里的"应用数据目录"一节）。
 * 没显式配置时从程序集名推断，正式应用应当显式给——等数据已经写进去之后再改就要做迁移。
 */
export function getIdentifier(): Promise<string>;
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
export function getTauriVersion(): Promise<string>;
/**
 * 宿主是否支持多窗口。
 *
 * @returns {Promise<boolean>}
 * @remarks
 * 三平台都支持，所以**恒为 `true`**。保留它是因为 Tauri 上它是个真查询
 * （移动端返回 `false`），迁移代码里常有 `if (await supportsMultipleWindows())` 这类分支；
 * 删掉会让那些代码编译不过。没有谎报：桌面端本来就是 true。
 */
export function supportsMultipleWindows(): Promise<boolean>;
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
export function getBundleType(): Promise<"nsis" | "msi" | "deb" | "rpm" | "appimage" | "app" | null>;
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
export function defaultWindowIcon(): Promise<import("./image.js").Image | null>;
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
export function exit(code?: number): Promise<void>;
/**
 * 显示全部窗口（对标 Tauri 的 `show()`）。
 *
 * @returns {Promise<void>}
 * @remarks
 * **需要能力声明**（`app.show`）。语义是"显示所有窗口"，**不是** macOS 的
 * "取消应用隐藏"（`NSApp.unhide:`）——三平台一致，不按平台分叉。
 */
export function show(): Promise<void>;
/**
 * 隐藏全部窗口（对标 Tauri 的 `hide()`）。
 *
 * @returns {Promise<void>}
 * @remarks **需要能力声明**（`app.hide`）。语义是"隐藏所有窗口"，理由同 {@link show}。
 */
export function hide(): Promise<void>;
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
export function setDockVisibility(visible: boolean): Promise<void>;
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
export function setTheme(theme?: "light" | "dark" | null): Promise<void>;
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
export function fetchDataStoreIdentifiers(): Promise<DataStoreIdentifier[]>;
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
export function removeDataStore(identifier: DataStoreIdentifier): Promise<void>;
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
export function onBackButtonPress(handler: (payload: OnBackButtonPressPayload) => void): Promise<{
    unregister: () => Promise<void>;
}>;
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
export const BundleType: Readonly<{
    Nsis: "nsis";
    Msi: "msi";
    Deb: "deb";
    Rpm: "rpm";
    AppImage: "appimage";
    App: "app";
}>;
/**
 * 数据存储标识符（对标 Tauri 的 `DataStoreIdentifier`）：**16 个 0–255 的整数**。
 */
export type DataStoreIdentifier = [number, number, number, number, number, number, number, number, number, number, number, number, number, number, number, number];
/**
 * `onBackButtonPress` 的回调参数（对标 Tauri 的 `OnBackButtonPressPayload`）。
 */
export type OnBackButtonPressPayload = {
    /**
     * 页面是否还有历史可回退。**桌面端永远不会收到这个回调**。
     */
    canGoBack: boolean;
};
