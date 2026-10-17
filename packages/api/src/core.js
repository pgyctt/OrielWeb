// 对标 `@tauri-apps/api/core`。
//
// 与 Tauri 的差别（如实标注，不假装一致）：
//   * **参数命名不做自动转换**。Tauri 把 JS 的 camelCase 转成 Rust 的 snake_case；
//     本库的 `[OrielCommand]` 按方法参数名原样取值（见 Ipc/OrielJson.RequireArgElement），
//     所以这里**也不转**——转了反而会让两边对不上。要迁移的话，命令参数名自己写成 camelCase。
//   * **失败值是字符串**（`Error.message`），不是序列化的结构化 `E`。错误码见 P1-7（尚未实现）。
//   * **有默认 30 秒超时**（Tauri 没有）。长任务必须显式放宽，否则会被静默超时。
//   * **没有 Channel / Resource / transformCallback**：本库无流式回传与句柄表。

import { getTransport, hasTransportOverride } from './internal.js';

/**
 * 传输层是否已支持 `invoke(name, args, { timeout })` 三参形态。
 *
 * 这是**编译期常量而不是运行时探测**：探测（例如看 `invoke.length`）在压缩或包装之后
 * 会给出错误答案，而错误答案的表现是"设了超时却没生效"——那正是要避免的静默失败。
 * F7（P1-7）落地时把它改成 `true`，同时给桥接脚本的 `invoke` 加第三个形参。
 */
const PER_CALL_TIMEOUT_SUPPORTED = false;

/**
 * 调用宿主命令。
 *
 * @template T
 * @param {string} cmd 命令名（如 `todo.add`）。
 * @param {Record<string, unknown>} [args] 参数对象；省略等同于"不带参数"。
 * @param {{ timeout?: number }} [options] `timeout` 单位毫秒，`<= 0` 表示不超时。
 *   省略时用全局默认值（`oriel.timeout`，默认 30000）。
 * @returns {Promise<T>}
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

    if (!PER_CALL_TIMEOUT_SUPPORTED) {
        // 静默忽略会让"我设了 5 分钟"变成"30 秒后失败"，而且失败文案是超时——
        // 排查的人会去查命令实现，而不是查这里。所以明确拒绝。
        throw new Error(
            'invoke: 当前版本不支持 per-call timeout（计划中的 P1-7）。' +
            '请改全局超时：window.oriel.timeout = 300000，或省略 options.timeout。');
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
