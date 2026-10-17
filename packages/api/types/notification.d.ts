/**
 * 宿主是否允许发送通知。
 *
 * @returns {Promise<boolean>}
 * @remarks
 * 桌面上没有"通知权限"这个概念（能显示就能显示），所以这里回的是
 * **宿主是否支持通知**——与 Tauri 的语义一致（它在移动端才真的查权限）。
 */
export function isPermissionGranted(): Promise<boolean>;
/**
 * 请求通知权限。
 *
 * @returns {Promise<'granted' | 'denied'>}
 * @remarks
 * 桌面端**不弹任何对话框**（没有可弹的东西），直接回结果。
 * 返回字符串而不是布尔是为了与 Tauri 以及 Web Notification 规范一致。
 */
export function requestPermission(): Promise<"granted" | "denied">;
/**
 * 发一条系统通知。
 *
 * @param {string | { title: string, body?: string }} options 标题，或 `{ title, body }`。
 * @returns {Promise<boolean>} 通知系统是否接受了它。
 * @remarks
 * 返回 `false` 而不是抛错，用于"用户关掉了这个应用的通知"这类**正常状态**——
 * 那不是调用方的错误，逼它包 try 只会让代码更难读。
 *
 * 形状与 Tauri 的 `sendNotification` 一致（收字符串或 `{ title, body }`），
 * 但**是异步的**（理由见文件头）。
 */
export function sendNotification(options: string | {
    title: string;
    body?: string;
}): Promise<boolean>;
