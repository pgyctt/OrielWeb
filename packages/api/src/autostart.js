// 对标 `@tauri-apps/plugin-autostart`。
//
// 三个函数，与 Tauri 一一对应。**都影响系统状态**（开机自启是一个留在系统里的注册项），
// 所以都需要能力声明——`c.Allow("autostart.enable")` 这类。
//
// 一处值得注意的语义：`enable()` 装上的是**当前这个可执行文件**的开机自启。
// 开发期跑的是 `dotnet run` 或 bin 目录里的临时产物，那条自启项在换构建之后会指向
// 一个不存在的路径。所以"打开自启 → 重启 → 没反应"这类现象先要排除这一条。
//
// 与 Tauri 的差别：`enable()` 在本库接受一个可选的启动参数（`OrielApp.EnableAutoStart`
// 支持），但**这里没有暴露**——Tauri 的 `enable()` 也不收参数。要传参数请用宿主侧的 API。

import { invoke } from './core.js';

/**
 * 当前是否已开启开机自启。
 *
 * @returns {Promise<boolean>}
 */
export function isEnabled() {
    return invoke('autostart.isEnabled');
}

/**
 * 开启开机自启。
 *
 * @returns {Promise<boolean>} 是否成功。
 * @remarks
 * 宿主侧的实现按平台不同（Windows 注册表 Run 项 / macOS LaunchAgent plist /
 * Linux XDG autostart .desktop）。返回 `false` 表示那一步没成功（权限、路径不可写等），
 * 而不是"已经开着了"。
 */
export function enable() {
    return invoke('autostart.enable');
}

/**
 * 关闭开机自启。
 *
 * @returns {Promise<boolean>} 是否有过那条自启项并被移除。
 * @remarks
 * 本来就没开时返回 `false`——调用方据此区分"我刚把它关掉了"与"它本来就没开"，
 * 而这两种情况下"现在是关的"都成立。
 */
export function disable() {
    return invoke('autostart.disable');
}
