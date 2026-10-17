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
export function openUrl(url: string, openWith?: string): Promise<boolean>;
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
export function openPath(path: string, openWith?: string): Promise<void>;
/**
 * 在系统文件管理器里选中若干文件（Windows 资源管理器 / macOS 访达 / Linux 文件管理器）。
 *
 * @param {string | string[]} path 一个路径或一组路径。
 * @returns {Promise<boolean>} 是否**全部**定位成功。
 * @remarks
 * 返回布尔而不是 void（Tauri 是 void）：一个失败时调用方会以为"都定位到了"。
 * 全部成功才算成功。
 */
export function revealItemInDir(path: string | string[]): Promise<boolean>;
