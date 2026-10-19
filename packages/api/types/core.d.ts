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
 *   `missing_argument` / `path_not_allowed`）；`data` 是可选的附加数据。两个字段
 *   **只在宿主给了时才存在**，所以写 `if (e.code === 'not_allowed')` 就够了，不必先判 `in`。
 *   `timeout`（超时）由桥接层产生，同样带 `code`——重试可能有用，值得和"参数错了"分开。
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
 * @param {string} filePath 宿主上的路径（绝对路径，或相对**进程当前目录**的路径）。
 * @param {string} [protocol] 仅接受缺省值。见下面的说明。
 * @returns {string} 形如 `oriel://file.oriel/<路径>`。
 * @throws {TypeError | Error} 参数类型不对、或传了不支持的 `protocol` 时抛出。
 * @remarks
 * <para>
 * **同步**（与 Tauri 一致）：这一步只拼字符串，不发 IPC、也不查任何权限。
 * </para>
 * <para>
 * **真正的强制点在宿主侧**：页面把 URL 放进 `<img src>` / `fetch()` 时，
 * webview 会去请求它，宿主在那时查 **`asset.read` 的 scope**，不在 scope 内就回 403。
 * 在这边提前拦没有意义——页面自己也能拼出一个同样的 URL。
 * </para>
 * <para>
 * **`asset.read` 是独立的 scope，不共用 `fs.*` 的**（照 Tauri 的 `assetProtocol.scope`）：
 * "把图片显示出来"与"以程序的方式读写文件"是两种意图，共用一个 scope 会让
 * 只想显示一张图的应用顺带获得读任意文本的能力。
 * </para>
 * <para>
 * **文件内容与页面不同源**：URL 的 host 是 `file.oriel` 而不是应用的 host，
 * 所以磁盘上的 HTML 文件被导航到也**拿不到桥接**（桥接只注入到可信来源）。
 * 少了这一步，"下载一个 .html 到应用数据目录、再打开它"就是一条提权路径。
 * </para>
 * <para>
 * **`protocol` 参数**：Tauri 用它指定自定义协议名（可配置多个资产协议）。
 * 本库只登记了一个自定义 scheme，文件固定走 `file.oriel`，所以传非缺省值会**明确报错**
 * 而不是被静默忽略——静默忽略的后果是"我在配置里指定了协议名，但页面加载不出来"，
 * 而那个现象与协议名无关。
 * </para>
 */
export function convertFileSrc(filePath: string, protocol?: string): string;
