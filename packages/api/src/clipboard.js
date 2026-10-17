// 对标 `@tauri-apps/plugin-clipboard-manager` 的**纯文本**部分。
//
// 只提供三个函数：`readText` / `writeText` / `clear`。Tauri 那边还有：
//
//   * `writeHtml(html, altText)` —— HTML 剪贴板格式在 Windows 上是 CF_HTML，
//     一个带字节偏移头的私有格式，而那个头必须按**目标**平台重算。偏移写错会让
//     粘贴目标是乱码或截断，而它在"看起来成功了"的情况下静默发生。
//     收益（一次粘贴的格式）远小于风险。
//   * `writeImage` / `readImage` —— 需要一套图像句柄模型，本库没有
//     （见 fs 模块的同一条说明：没有 rid 表）。
//   * `writeSecret(text)` —— macOS 专有的"不记录到剪贴板历史"标记，其余平台无对应物。
//   * `writeText(text, { label })` 的 `label`（X11 的多个剪贴板）—— 传了会**明确报错**，
//     见宿主侧 RejectClipboardLabel。
//
// 缺的那些记在 docs/TAURI2-PLAN.md 里，不是遗漏。

import { invoke } from './core.js';

/**
 * 读系统剪贴板里的文本。
 *
 * @returns {Promise<string | null>} 没有文本内容时是 `null`。
 * @remarks
 * **`null` 与空串是两回事**：剪贴板里可能是图片、文件列表，或者根本是空的。
 * 而空串是一个合法的文本内容。调用方要能区分"没有文本"与"文本是空的"。
 *
 * ⚠️ **Linux 上这个调用可能阻塞**：GTK 的剪贴板是异步 API，读它要跑一个内嵌的 main loop
 * 等所有者应答，而对方不响应时会一直等（表现为命令等到前端超时）。
 * 这不是本库能绕的——GTK 没有"非阻塞取一次"的入口。
 */
export function readText() {
    return invoke('clipboard.readText');
}

/**
 * 把文本写进系统剪贴板。
 *
 * @param {string} text
 * @param {{ label?: string }} [opts] **不支持** `label`，传了会抛错。
 * @returns {Promise<boolean>} 是否写成功。
 * @remarks
 * 失败返回 `false` 而不是抛错：剪贴板是**全局独占**资源（Windows 上一次只有一个进程
 * 能打开它），别的进程正开着时写不进去是**正常状态**，不是调用方的错误。
 */
export function writeText(text, opts) {
    if (typeof text !== 'string') {
        throw new TypeError('writeText: text 必须是字符串');
    }

    return invoke('clipboard.writeText', opts?.label === undefined
        ? { text }
        : { text, label: opts.label });
}

/**
 * 清空系统剪贴板。
 *
 * @returns {Promise<boolean>} 是否成功。
 */
export function clear() {
    return invoke('clipboard.clear');
}
