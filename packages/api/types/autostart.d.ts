/**
 * 当前是否已开启开机自启。
 *
 * @returns {Promise<boolean>}
 */
export function isEnabled(): Promise<boolean>;
/**
 * 开启开机自启。
 *
 * @returns {Promise<boolean>} 是否成功。
 * @remarks
 * 宿主侧的实现按平台不同（Windows 注册表 Run 项 / macOS LaunchAgent plist /
 * Linux XDG autostart .desktop）。返回 `false` 表示那一步没成功（权限、路径不可写等），
 * 而不是"已经开着了"。
 */
export function enable(): Promise<boolean>;
/**
 * 关闭开机自启。
 *
 * @returns {Promise<boolean>} 是否有过那条自启项并被移除。
 * @remarks
 * 本来就没开时返回 `false`——调用方据此区分"我刚把它关掉了"与"它本来就没开"，
 * 而这两种情况下"现在是关的"都成立。
 */
export function disable(): Promise<boolean>;
