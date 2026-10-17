/**
 * 调用宿主命令。
 *
 * @template T
 * @param {string} cmd 命令名（如 `todo.add`）。
 * @param {Record<string, unknown>} [args] 参数对象；省略等同于"不带参数"。
 * @param {{ timeout?: number }} [options] `timeout` 单位毫秒，`<= 0` 表示**这次调用不超时**。
 *   省略时用全局默认值（`oriel.timeout`，默认 30000）。
 * @returns {Promise<T>}
 * @throws {Error} 失败时 reject：`message` 是宿主文案；`code` 是可选的机器可判别错误码
 *   （库自己的那些见 `OrielIpcErrorCodes`，如 `not_allowed` / `unknown_command` /
 *   `missing_argument`）；`data` 是可选的附加数据。两个字段**只在宿主给了时才存在**，
 *   所以写 `if (e.code === 'not_allowed')` 就够了，不必先判 `in`。
 */
export function invoke<T>(cmd: string, args?: Record<string, unknown>, options?: {
    timeout?: number;
}): Promise<T>;
/**
 * 当前是否运行在 OrielWeb 宿主里（对标 `isTauri()`）。
 *
 * 判定口径与 Tauri 略有不同：Tauri 查 `__TAURI_INTERNALS__`，这里查 `window.oriel`。
 * **装了 mock 时返回 `true`**——前端单测应当走"真实分支"，而不是被环境判断
 * 带去另一条没有测试覆盖的路。
 *
 * @returns {boolean}
 */
export function isTauri(): boolean;
/**
 * 把本地文件路径转成页面可直接用的 URL（对标 `convertFileSrc`）。
 *
 * **尚未实现**（P1-2）。这里显式抛错而不是返回一个看似可用的字符串：
 * 一个 404 的 `<img>` 表现为"图片不显示"，而原因（没有 asset 协议）在页面侧看不见。
 *
 * @param {string} filePath
 * @param {string} [protocol]
 * @returns {string}
 */
export function convertFileSrc(filePath: string, protocol?: string): string;
