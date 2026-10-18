// 对标 `@tauri-apps/api/core`。
//
// 与 Tauri 的差别（如实标注，不假装一致）：
//   * **参数命名不做自动转换**。Tauri 把 JS 的 camelCase 转成 Rust 的 snake_case；
//     本库的 `[OrielCommand]` 按方法参数名原样取值（见 Ipc/OrielJson.RequireArgElement），
//     所以这里**也不转**——转了反而会让两边对不上。要迁移的话，命令参数名自己写成 camelCase。
//   * **失败值是 `Error`**：`message` 是宿主文案，另挂可选的 `code` / `data`
//     （只在宿主给了的时候才挂）。Tauri 的失败值是 Rust `Result::Err` 的序列化结果，
//     形状由应用自己定；本库统一成 Error 是因为那是最不容易写错的形态。
//   * **有默认 30 秒超时**（Tauri 没有）。长任务必须显式放宽，否则会被静默超时；
//     也可以逐次调用给 `options.timeout`。
//   * **没有 Channel / Resource / transformCallback**：本库无流式回传与句柄表。

import { getTransport, hasTransportOverride } from './internal.js';

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
export async function invoke(cmd, args, options) {
    if (typeof cmd !== 'string' || cmd === '') {
        throw new Error('invoke: cmd 必须为非空字符串');
    }

    const transport = getTransport();
    const timeout = options?.timeout;

    if (timeout === undefined || timeout === null) {
        return /** @type {any} */ (transport.invoke)(cmd, args);
    }

    // **显式声明**而不是探测：传输层上有个 `supportsPerCallTimeout: true` 字段。
    // 探测（`invoke.length`）在包装或压缩之后会给出错误答案，而错误答案的表现是
    // "设了超时却没生效"——那是最难查的一类。老宿主没这个字段，于是这里的明确拒绝
    // 就是它的降级行为（而不是静默按 30 秒超时）。
    if (transport.supportsPerCallTimeout !== true) {
        throw new Error(
            'invoke: 当前宿主不支持 per-call timeout（需要 OrielWeb 0.3.6+）。' +
            '请改全局超时：window.oriel.timeout = 300000；或省略 options.timeout。');
    }

    return /** @type {any} */ (transport.invoke)(cmd, args, { timeout });
}

/**
 * 当前是否运行在 OrielWeb 宿主里（对标 `isTauri()`）。
 *
 * 判定口径与 Tauri 略有不同：Tauri 查 `__TAURI_INTERNALS__`，这里查 `window.oriel`。
 * **装了 mock 时返回 `true`**——前端单测应当走"真实分支"，而不是被环境判断
 * 带去另一条没有测试覆盖的路。
 *
 * @returns {boolean}
 */
export function isTauri() {
    if (hasTransportOverride()) {
        return true;
    }

    return typeof globalThis.window?.oriel?.invoke === 'function';
}

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
export function convertFileSrc(filePath, protocol) {
    if (typeof filePath !== 'string' || filePath === '') {
        throw new TypeError('convertFileSrc: filePath 必须为非空字符串');
    }

    // Tauri 的默认值是 'asset'。缺省时接受，显式传 'asset' 也接受（迁移时可能照抄了）。
    if (protocol !== undefined && protocol !== null && protocol !== 'asset') {
        throw new Error(
            `convertFileSrc: 不支持自定义协议名「${protocol}」——本库的文件内容固定走 oriel://file.oriel/。` +
            '请省略第二个参数。');
    }

    // **走桥接的同步实现**：与 Tauri 一样只拼字符串、不发 IPC。
    // 不在包内自己拼一份：那样会出现两份必须严格对称的编码实现
    // （宿主侧的 AssetUrl.FileUrl 与这里），而它们一旦不一致，
    // 症状是"某些路径就是取不到"——且往往只在特定平台/特定字符下出现。
    const transport = getTransport();
    if (typeof transport.convertFileSrc !== 'function') {
        throw new Error(
            'convertFileSrc: 当前宿主不支持（需要 OrielWeb 0.5.0+）。' +
            '旧宿主上没有文件 asset 协议。');
    }

    return transport.convertFileSrc(filePath);
}
