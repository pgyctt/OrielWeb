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
 * @remarks
 * <b>迁移时用这个函数替换 `__TAURI_INTERNALS__` 探测</b>。
 *
 * B7 的迁移演练里 3 个真实应用全都在做环境判断（`"__TAURI_INTERNALS__" in window`，
 * 有的还进一步探 `transformCallback` 决定要不要 `listen`）。本库**不提供**这个标记，
 * 漏改的后果不是报错而是**静默走进浏览器降级分支**：假数据、被禁用的按钮、
 * 没注册的监听——编译器一句话都不会说。所以这条规则写在这里，而不是只写进文档。
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
/**
 * 命令执行期间接收宿主推送的通道（对标 Tauri 的 `Channel`）。
 *
 * @remarks
 * <b>解决的是"一次请求只能有一个回执"这个限制</b>：普通命令的返回值只能有一个，
 * 而流式场景（模型逐字输出、批量导入进度）要在命令返回**之前**多次推送。
 *
 * <b>用法</b>与 Tauri 一致：构造 → 挂 `onmessage`（或直接传给构造函数）→ 作为 `invoke` 的
 * 一个参数发出去。
 *
 * ```js
 * import { Channel, invoke } from '@orielweb/api/core';
 *
 * const channel = new Channel((line) => console.log(line));
 * await invoke('chat.stream', { question, channel });
 * // 命令返回后通道自动关闭：宿主在命令结束时发一条结束事件，这里收到就退订。
 * ```
 *
 * <b>线格式</b>：`toJSON()` 把实例变成一个 `__ORIEL_CHANNEL__:<id>` 字符串。所以它
 * 只是"一个长得像字符串的参数"——桥接脚本与三个平台后端都不认识通道这个概念，
 * 也就不存在"某个平台没实现"的问题。
 *
 * <b>与 Tauri 的差别</b>：Tauri 用 `transformCallback` 把回调登记进
 * `__TAURI_INTERNALS__.callbacks`（全局回调表）；本库没有那张表，改用两个事件名
 * （`oriel://channel/<id>` 与 `oriel://channel-end/<id>`）。差别对调用方不可见，
 * 但**顺序保证也同样没有**：多线程推送时到达顺序以宿主投递为准。
 */
export class Channel {
    /**
     * @param {(message: any) => void} [onmessage] 收到一条增量时调用；之后也可以改 `onmessage`。
     */
    constructor(onmessage?: (message: any) => void);
    /** 通道 id（页面内唯一）。宿主回推时原样带上。 */
    id: number;
    /** @type {(message: any) => void} */
    _onmessage: (message: any) => void;
    /** @type {boolean} */
    _closed: boolean;
    /** @type {Array<() => void>} */
    _offs: Array<() => void>;
    /** @param {(message: any) => void} handler */
    set onmessage(handler: (message: any) => void);
    /** @returns {(message: any) => void} */
    get onmessage(): (message: any) => void;
    /** 是否已关闭（命令返回后为 `true`）。 */
    get closed(): boolean;
    /**
     * 退订并停止接收。
     *
     * @remarks 一般不用手动调：宿主在命令结束时（正常返回、抛异常、参数绑定失败都算）
     * 会发一条结束事件，构造函数里为它挂的监听会自动调到这里。幂等。
     * 手动调只用于"提前不要了，但命令还要继续跑"。
     */
    close(): void;
    /**
     * 序列化成宿主认识的字符串形态。
     *
     * @returns {string}
     * @remarks Tauri 也是这么做的（`__CHANNEL__:<id>`）。走 `toJSON` 而不是给
     * `invoke` 加一层专门处理，是因为桥接层用 `JSON.stringify` 发参数——那样通道
     * 在宿主眼里就是一个普通字符串参数，不需要任何新协议。
     */
    toJSON(): string;
    /**
     * 投递一条增量给回调。
     *
     * @param {unknown} payload
     * @private
     */
    private _deliver;
}
