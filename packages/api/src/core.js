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
            'invoke: 当前宿主不支持 per-call timeout（需要 OrielWeb 0.4.0+）。' +
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
 * **尚未实现**（P1-2）。这里显式抛错而不是返回一个看似可用的字符串：
 * 一个 404 的 `<img>` 表现为"图片不显示"，而原因（没有 asset 协议）在页面侧看不见。
 *
 * @param {string} filePath
 * @param {string} [protocol]
 * @returns {string}
 */
export function convertFileSrc(filePath, protocol) {
    void filePath;
    void protocol;
    throw new Error(
        'convertFileSrc 尚未实现（计划中的 P1-2）：本库还没有文件 asset 协议与路径 scope。' +
        '目前请把文件读进内存（宿主命令）或用内嵌资源。');
}
