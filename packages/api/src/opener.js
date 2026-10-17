// 对标 `@tauri-apps/plugin-opener`。
//
// 三个函数的共同点：**它们在外面的世界里产生副作用**（拉起浏览器、用默认程序打开文件、
// 弹文件管理器）。所以它们都**需要能力声明**，不像 `path.*` 那样整族放行：
//
//     c.Allow("opener.openUrl")   // 能拉起任意 URL（含自定义协议处理器）
//     c.Allow("opener.openPath")  // 能用默认程序打开任意文件——双击一个 .exe 就是执行它
//
// 与 Tauri 的差别：**不支持 `openWith`（用指定程序打开）**，传了会明确报错而不是
// 静默降级成"用默认程序打开"。理由见宿主侧 OrielBuiltInPlatformCommands 的说明：
// 那个参数等于任意程序执行，需要一套"允许哪些程序"的授权模型。

import { invoke } from './core.js';

/**
 * 用系统默认方式打开一个 URL（通常是浏览器）。
 *
 * @param {string} url **绝对 URL**（含协议）。相对 URL 会被拒——系统不知道该拿它怎么办。
 * @param {string} [openWith] **不支持**，传了会抛错。
 * @returns {Promise<boolean>} 是否成功拉起。
 * @throws {Error} `url` 不是绝对 URL、或传了 `openWith` 时抛出（`code` 分别是
 *   `invalid_argument` / `not_supported`）。
 * @remarks
 * **宿主还会按协议白名单过滤一次**（`OrielShellOptions.AllowedSchemes`，
 * 默认只放 `http` / `https` / `mailto`）：`file:`、`javascript:`、裸路径与含控制字符的目标
 * 会被拒绝并返回 `false`（不抛错——"这个协议不在白名单里"是配置决定，不是调用错误）。
 * 要放行自定义协议（如 `myapp:`）请在宿主侧配置白名单，而不是在页面侧拼字符串绕过。
 */
export function openUrl(url, openWith) {
    return invoke('opener.openUrl', openWith === undefined ? { url } : { url, with: openWith });
}

/**
 * 用系统默认程序打开一个文件或目录。
 *
 * @param {string} path
 * @param {string} [openWith] **不支持**，传了会抛错。
 * @returns {Promise<void>}
 * @remarks
 * **这条命令的授权等于"可以用默认程序打开任意文件"**。对一个可执行文件来说，
 * 那就是"执行它"——所以它的 scope 就是"应用信任自己的页面到这个程度"。
 * 只在自己拼路径、不从用户输入取路径时声明它。
 */
export function openPath(path, openWith) {
    return invoke('opener.openPath', openWith === undefined ? { path } : { path, with: openWith });
}

/**
 * 在系统文件管理器里选中若干文件（Windows 资源管理器 / macOS 访达 / Linux 文件管理器）。
 *
 * @param {string | string[]} path 一个路径或一组路径。
 * @returns {Promise<boolean>} 是否**全部**定位成功。
 * @remarks
 * 返回布尔而不是 void（Tauri 是 void）：一个失败时调用方会以为"都定位到了"。
 * 全部成功才算成功。
 */
export function revealItemInDir(path) {
    // **先校验再包装**：反过来的话空字符串会被包成 `['']`（长度 1），
    // 于是一条本该在页面侧就被挡下的空路径会一路发到宿主——
    // 而宿主的报错是"缺失参数"或"路径不存在"，指不到"你传了个空串"。
    if (typeof path === 'string') {
        if (path === '') {
            throw new TypeError('revealItemInDir: path 不能为空字符串');
        }

        return invoke('opener.revealItemInDir', { paths: [path] });
    }

    if (!Array.isArray(path) || path.length === 0) {
        throw new TypeError('revealItemInDir: path 必须是非空字符串或非空数组');
    }

    return invoke('opener.revealItemInDir', { paths: path });
}
