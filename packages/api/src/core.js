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
//   * **没有 Resource / transformCallback**：本库无句柄表（菜单树、图标都是"每次整棵发"）。
//     流式回传有 `Channel`，但它的实现是"一个字符串参数 + 两条事件"，不是回调注册表。

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
 *   `missing_argument` / `path_not_allowed`）；`data` 是可选的附加数据。两个字段
 *   **只在宿主给了时才存在**，所以写 `if (e.code === 'not_allowed')` 就够了，不必先判 `in`。
 *   `timeout`（超时）由桥接层产生，同样带 `code`——重试可能有用，值得和"参数错了"分开。
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
 * @remarks
 * <b>迁移时用这个函数替换 `__TAURI_INTERNALS__` 探测</b>。
 *
 * B7 的迁移演练里 3 个真实应用全都在做环境判断（`"__TAURI_INTERNALS__" in window`，
 * 有的还进一步探 `transformCallback` 决定要不要 `listen`）。本库**不提供**这个标记，
 * 漏改的后果不是报错而是**静默走进浏览器降级分支**：假数据、被禁用的按钮、
 * 没注册的监听——编译器一句话都不会说。所以这条规则写在这里，而不是只写进文档。
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

// ---------------------------------------------------------------------------
// 流式回传（Channel）
// ---------------------------------------------------------------------------

/** 通道 id 计数。页面内唯一即可——宿主只会把它原样回推给**同一个窗口**。 */
let nextChannelId = 1;

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
 *
 * <b>`closed` 可能比 `await invoke(...)` 晚一拍</b>：结束事件与命令回执走的是两条投递路径，
 * 在 Windows 上回执可以先进页面（实测如此）。所以**不要在 `await invoke(...)` 之后立刻断言
 * `channel.closed === true`**——要么等一个事件（自己推最后一条"完成"增量），要么轮询。
 * 这不是缺陷，是"关闭通知"与"命令完成"本来就是两件事。
 */
export class Channel {
    /**
     * @param {(message: any) => void} [onmessage] 收到一条增量时调用；之后也可以改 `onmessage`。
     */
    constructor(onmessage) {
        /** 通道 id（页面内唯一）。宿主回推时原样带上。 */
        this.id = nextChannelId++;

        /** @type {(message: any) => void} */
        this._onmessage = typeof onmessage === 'function' ? onmessage : () => {};

        /** @type {boolean} */
        this._closed = false;

        /** @type {Array<() => void>} */
        this._offs = [];

        const transport = getTransport();
        if (typeof transport.on !== 'function') {
            throw new Error('Channel: 当前传输层没有 on()，无法接收增量。');
        }

        // **构造即订阅**：调用方通常在 `invoke` 之前构造 Channel，而宿主在命令执行期间
        // 随时可能推第一条——等 `invoke` 的 promise 落地再订阅就已经漏了。
        this._offs.push(transport.on(`oriel://channel/${this.id}`, (payload) => this._deliver(payload)));
        this._offs.push(transport.on(`oriel://channel-end/${this.id}`, () => this.close()));
    }

    /** @param {(message: any) => void} handler */
    set onmessage(handler) {
        this._onmessage = typeof handler === 'function' ? handler : () => {};
    }

    /** @returns {(message: any) => void} */
    get onmessage() {
        return this._onmessage;
    }

    /** 是否已关闭（命令返回后为 `true`）。 */
    get closed() {
        return this._closed;
    }

    /**
     * 退订并停止接收。
     *
     * @remarks 一般不用手动调：宿主在命令结束时（正常返回、抛异常、参数绑定失败都算）
     * 会发一条结束事件，构造函数里为它挂的监听会自动调到这里。幂等。
     * 手动调只用于"提前不要了，但命令还要继续跑"。
     */
    close() {
        if (this._closed) {
            return;
        }

        this._closed = true;
        for (const off of this._offs.splice(0)) {
            try {
                off();
            } catch {
                // 退订失败不影响"已经关了"这个事实
            }
        }
    }

    /**
     * 序列化成宿主认识的字符串形态。
     *
     * @returns {string}
     * @remarks Tauri 也是这么做的（`__CHANNEL__:<id>`）。走 `toJSON` 而不是给
     * `invoke` 加一层专门处理，是因为桥接层用 `JSON.stringify` 发参数——那样通道
     * 在宿主眼里就是一个普通字符串参数，不需要任何新协议。
     */
    toJSON() {
        return `__ORIEL_CHANNEL__:${this.id}`;
    }

    /**
     * 投递一条增量给回调。
     *
     * @param {unknown} payload
     * @private
     */
    _deliver(payload) {
        if (this._closed) {
            return;
        }

        try {
            this._onmessage(payload);
        } catch (err) {
            // 一个回调抛错不该让后面的增量全部丢失（事件分发链上还有别的监听者）。
            // 异步重抛：错误仍然以"未捕获异常"的形式出现，而不是被静默吞掉。
            queueMicrotask(() => {
                throw err;
            });
        }
    }
}
