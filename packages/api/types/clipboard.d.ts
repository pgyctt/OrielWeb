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
export function readText(): Promise<string | null>;
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
export function writeText(text: string, opts?: {
    label?: string;
}): Promise<boolean>;
/**
 * 清空系统剪贴板。
 *
 * @returns {Promise<boolean>} 是否成功。
 */
export function clear(): Promise<boolean>;
