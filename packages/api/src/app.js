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
// 仍然缺失的几项，理由各不相同：
//
//   * `setTheme` / `setDockVisibility` —— 应用级主题覆写 / macOS Dock 显隐，本库还没有
//     （主题目前只读；Dock 操作是 macOS 专有）。
//   * `getBundleType` / `BundleType` —— **本库给不出这个值**。它返回的是 Tauri 打包器的
//     产物类型（nsis / msi / deb / rpm / appimage / app）；OrielWeb 是库不是打包器。
//     编一个值比不提供更糟：迁移代码会据此分支，而它会基于错信息分支。
//   * `defaultWindowIcon` / `fetchDataStoreIdentifiers` / `removeDataStore` / `onBackButtonPress`
//     —— 各自依赖图标资源表、数据存储、移动端返回键，本库暂无对应物。
//
// 这些都在 docs/TAURI2-ALIGNMENT-AUDIT 与对齐账本里记着，不是遗漏。

import { invoke } from './core.js';

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
