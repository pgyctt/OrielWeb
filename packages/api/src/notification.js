// 对标 `@tauri-apps/plugin-notification` 的**桌面**子集。
//
// 只提供三个函数：`isPermissionGranted` / `requestPermission` / `sendNotification`。
// Tauri 那边还有一整套（`active` / `pending` / `cancel` / `cancelAll` /
// `removeActive` / `removeAllActive` / `channels` / `createChannel` / `deleteChannel` /
// `listChannels` / `registerActionTypes` / `onAction` / `onNotificationReceived` /
// `Schedule` / `Importance` / `Visibility`），**大部分是移动端或需要一套通知句柄表**：
//
//   * `active` / `pending` / `cancel` / `removeActive` 要记录"发过哪些通知"——
//     那是句柄表（本库没有，见 fs 模块的同一条说明）。
//   * `channels` / `Importance` / `Visibility` 是 Android 的通知渠道概念。
//   * `Schedule` 要常驻定时器。
//   * `onAction` / `onNotificationReceived` 要能把通知的点击回传到页面。
//
// 桌面端最常见的用法就是"发一条通知"，那一个函数覆盖了绝大多数场景。
// 缺的那些在 docs/TAURI2-PLAN.md 里记着，不是遗漏。
//
// 与 Tauri 的差别：**本模块的函数都是异步的**。Tauri 的 `sendNotification` 在桌面端
// 直接 `new window.Notification(...)`（走浏览器的通知 API），所以是同步的；
// 而本库走宿主命令，因为 WebView 的浏览器通知 API 三平台并不可靠（WKWebView 上基本没有），
// 而 `OrielApp.ShowNotification` 三平台都是原生的。
// 忽略返回值的调用方不受影响，但**不能**依赖"调用完通知就已经发出去了"。

import { invoke } from './core.js';

/**
 * 宿主是否允许发送通知。
 *
 * @returns {Promise<boolean>}
 * @remarks
 * 桌面上没有"通知权限"这个概念（能显示就能显示），所以这里回的是
 * **宿主是否支持通知**——与 Tauri 的语义一致（它在移动端才真的查权限）。
 */
export function isPermissionGranted() {
    return invoke('notification.isPermissionGranted');
}

/**
 * 请求通知权限。
 *
 * @returns {Promise<'granted' | 'denied'>}
 * @remarks
 * 桌面端**不弹任何对话框**（没有可弹的东西），直接回结果。
 * 返回字符串而不是布尔是为了与 Tauri 以及 Web Notification 规范一致。
 */
export function requestPermission() {
    return invoke('notification.requestPermission');
}

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
export function sendNotification(options) {
    if (typeof options === 'string') {
        return invoke('notification.sendNotification', { title: options });
    }

    if (options === null || typeof options !== 'object' || typeof options.title !== 'string') {
        throw new TypeError('sendNotification: 参数必须是字符串或 { title, body }');
    }

    return invoke('notification.sendNotification', {
        title: options.title,
        body: options.body,
    });
}
