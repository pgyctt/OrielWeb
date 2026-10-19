// 对标 `@tauri-apps/plugin-fs` 的**整文件**部分。
//
// 本模块每个函数都要过**两道关**：宿主侧的命令授权（`Allow("fs.readTextFile")`）
// 与**路径 scope**（`Allow("fs.readTextFile", s => s.AllowPath("$APPDATA/**"))`）。
// 少了后者，一条 XSS 就能把 ~/.ssh/id_rsa 读走——所以本模块与 `path` 有本质差别：
// `path` 只做运算不碰文件系统，整族无需声明；`fs` 碰文件系统，**必须逐条声明 scope**。
//
// 与 Tauri 的差别（如实标注，不假装一致）：
//
//   * **没有句柄式 API**：`open` / `create` / `FileHandle` / `seek` / `read` / `write` /
//     `readTextFileLines` / `watch` 都不提供。那些靠一张 `rid` 句柄表，而本库没有句柄表——
//     加它是一整套新机制（分配/回收/跨调用生命周期），不该混在"文件操作"里做。
//     整文件读写覆盖了 webview 界面里绝大多数场景。
//   * **二进制走 JSON 数字数组**：`readFile` 给 `Uint8Array`、`writeFile` 收 `Uint8Array`，
//     但通道是 JSON，所以一个字节要变成两三个字符。几十 MB 级别的文件不适用。
//     Tauri 用 `invoke` 的二进制 body 传，本库的 IPC 是纯 JSON，没有那条路。
//   * **`lstat` 与 `stat` 的差别只在"链接自身算不算"**：.NET 对符号链接会跟随，
//     所以两者返回的元数据相同、只是 `isSymlink` 如实报告。

import { invoke } from './core.js';

/**
 * 校验 Tauri 签名里的 `options` 位置。
 *
 * @param {string} fnName 调用方函数名（报错里要能定位到是哪个函数）
 * @param {unknown} options 调用方传进来的 options
 * @param {string[]} allowedKeys 本库**真正支持**的键（`mkdir`/`remove` 的 `recursive`）
 * @returns {Error | null} 有问题时返回错误对象，由调用方 reject
 * @remarks
 * 为什么不是"忽略未知选项"：Tauri 的 `baseDir` 语义是"配合 `BaseDirectory` 枚举
 * 把相对路径解析到应用目录"。本库没有那个枚举，路径在宿主侧按**绝对路径 + 已授权
 * scope** 处理——页面传 `baseDir` 而相对路径时，`path.resolve` 会按**进程当前目录**
 * 解析，于是读到的是另一个文件，而且**不报错**。这种错最难查：文件存在、命令成功、
 * 内容不对。
 *
 * 所以这里一律 reject，并在文案里给出替代写法。
 */
function checkOptions(fnName, options, allowedKeys) {
    if (options === undefined || options === null) {
        return null;
    }

    if (typeof options !== 'object' || Array.isArray(options)) {
        return new TypeError(`${fnName}: options 必须是对象，收到 ${Array.isArray(options) ? 'array' : typeof options}`);
    }

    // 到这里已经确认是对象；`unknown` 上读不到属性，所以显式收窄一次。
    const opts = /** @type {Record<string, unknown>} */ (options);

    if (opts.baseDir !== undefined && opts.baseDir !== null) {
        return new Error(
            `${fnName}: 不支持 baseDir 选项。\n` +
            'Tauri 用它配合 BaseDirectory 枚举把相对路径解析到应用目录；本库没有那套枚举，' +
            '相对路径会按**进程当前目录**解析——静默忽略会让你读到错的文件，所以这里直接报错。\n' +
            '替代写法：先取绝对目录，再拼路径。\n' +
            `  const dir = await appDataDir();            // from '@orielweb/api/path'\n` +
            `  await ${fnName}(\`\${dir}/config.json\`);`);
    }

    const unknown = Object.keys(opts).filter((key) => !allowedKeys.includes(key));
    if (unknown.length > 0) {
        return new Error(
            `${fnName}: 不支持的选项 ${unknown.map((k) => `\`${k}\``).join(', ')}。` +
            `本库该函数只接受 ${allowedKeys.length > 0 ? allowedKeys.map((k) => `\`${k}\``).join('/') : '无选项'}。`);
    }

    return null;
}

/**
 * 带 options 校验的 `invoke`。
 *
 * @param {string} fnName
 * @param {string} command
 * @param {Record<string, unknown>} args
 * @param {unknown} options
 * @param {string[]} [allowedKeys]
 * @returns {Promise<any>}
 */
function invokeWithOptions(fnName, command, args, options, allowedKeys = []) {
    const problem = checkOptions(fnName, options, allowedKeys);
    return problem === null ? invoke(command, args) : Promise.reject(problem);
}

/**
 * 读文本文件（UTF-8）。
 *
 * @param {string} path 绝对路径，或相对进程当前目录的路径。
 * @param {{ baseDir?: never }} [options] **只接受空对象**：本库不支持 `baseDir`（见 {@link checkOptions}）
 * @returns {Promise<string>}
 */
export function readTextFile(path, options) {
    return invokeWithOptions('readTextFile', 'fs.readTextFile', { path }, options);
}

/**
 * 写文本文件（UTF-8）。
 *
 * @param {string} path
 * @param {string} data
 * @param {{ baseDir?: never }} [options]
 * @returns {Promise<void>}
 * @remarks **父目录会自动创建**：不自动建的话每次写都要先 `mkdir`，
 * 而漏建的症状是 `DirectoryNotFoundException`——报错说"目录不存在"，调用方以为在写文件。
 */
export function writeTextFile(path, data, options) {
    return invokeWithOptions('writeTextFile', 'fs.writeTextFile', { path, data }, options);
}

/**
 * 读二进制文件。
 *
 * @param {string} path
 * @param {{ baseDir?: never }} [options]
 * @returns {Promise<Uint8Array>}
 */
export async function readFile(path, options) {
    const bytes = await invokeWithOptions('readFile', 'fs.readFile', { path }, options);
    return new Uint8Array(bytes);
}

/**
 * 写二进制文件。
 *
 * @param {string} path
 * @param {Uint8Array | number[]} data
 * @param {{ baseDir?: never }} [options]
 * @returns {Promise<void>}
 * @remarks
 * 传 `Uint8Array` 时这里会转成普通数组再发：JSON 不认 `Uint8Array`，
 * 直接塞进 `JSON.stringify` 会得到一个**对象**（`{"0":1,"1":2}`）而不是数组，
 * 而宿主侧期望数组、会报类型不符。
 */
export function writeFile(path, data, options) {
    return invokeWithOptions('writeFile', 'fs.writeFile', { path, data: Array.from(data) }, options);
}

/**
 * 路径是否存在。
 *
 * @param {string} path
 * @param {{ baseDir?: never }} [options]
 * @returns {Promise<boolean>}
 * @remarks **也要过 scope**：不过的话它就成了一个"这台机器上有哪些文件"的探测器，
 * 而那种信息在攻防里很有用（先枚举、再决定读什么）。
 */
export function exists(path, options) {
    return invokeWithOptions('exists', 'fs.exists', { path }, options);
}

/**
 * 文件大小（字节）。目录返回宿主定义的值（通常 0）。
 *
 * @param {string} path
 * @param {{ baseDir?: never }} [options]
 * @returns {Promise<number>}
 */
export function size(path, options) {
    return invokeWithOptions('size', 'fs.size', { path }, options);
}

/**
 * 目录项列表。
 *
 * @param {string} path
 * @param {{ baseDir?: never }} [options]
 * @returns {Promise<Array<{ name: string, isFile: boolean, isDirectory: boolean, isSymlink: boolean }>>}
 */
export function readDir(path, options) {
    return invokeWithOptions('readDir', 'fs.readDir', { path }, options);
}

/**
 * 文件元信息（跟随符号链接）。
 *
 * @param {string} path
 * @param {{ baseDir?: never }} [options]
 * @returns {Promise<{ isFile: boolean, isDirectory: boolean, isSymlink: boolean,
 *   size: number, mtime: number, atime: number, birthtime: number }>}
 * @remarks 时间是**毫秒时间戳**（Unix epoch，UTC），不是 `Date`。
 * 与 Tauri 的差别：那边给 `Date` 对象。用时间戳是因为它跨 JSON 无损，
 * 而 `Date` 要走 `new Date(x)` 转换——构造一杯都行，但时间戳更少一层意外。
 */
export function stat(path, options) {
    return invokeWithOptions('stat', 'fs.stat', { path }, options);
}

/**
 * 文件元信息（**不跟随**符号链接）。
 *
 * @param {string} path
 * @param {{ baseDir?: never }} [options]
 * @returns {Promise<object>}
 * @remarks 当前与 {@link stat} 返回同样的元数据（只是 `isSymlink` 如实报告）。
 * .NET 跟随符号链接，编出链接自身的长度/时间要额外做系统调用，本库目前不需要那个精度。
 */
export function lstat(path, options) {
    return invokeWithOptions('lstat', 'fs.lstat', { path }, options);
}

/**
 * 创建目录。
 *
 * @param {string} path
 * @param {{ recursive?: boolean, baseDir?: never }} [options]
 * @returns {Promise<void>}
 * @remarks 非递归且目录已存在 → 报错（与 Tauri / Node 一致）；递归时已存在是正常的。
 */
export function mkdir(path, options) {
    const problem = checkOptions('mkdir', options, ['recursive']);
    if (problem !== null) {
        return Promise.reject(problem);
    }

    return invoke('fs.mkdir', options?.recursive === true ? { path, recursive: true } : { path });
}

/**
 * 删除文件或目录。
 *
 * @param {string} path
 * @param {{ recursive?: boolean, baseDir?: never }} [options] 删目录时必须为 `true`（否则非空目录会报错）。
 * @returns {Promise<void>}
 */
export function remove(path, options) {
    const problem = checkOptions('remove', options, ['recursive']);
    if (problem !== null) {
        return Promise.reject(problem);
    }

    return invoke('fs.remove', options?.recursive === true ? { path, recursive: true } : { path });
}

/**
 * 重命名 / 移动。
 *
 * @param {string} oldPath
 * @param {string} newPath
 * @param {{ baseDir?: never }} [options]
 * @returns {Promise<void>}
 * @remarks **两个路径都要在 scope 内**：只查源的话，页面能把允许目录里的文件
 * "搬"到 scope 之外（那是一次写）。
 */
export function rename(oldPath, newPath, options) {
    return invokeWithOptions('rename', 'fs.rename', { oldPath, newPath }, options);
}

/**
 * 复制文件。
 *
 * @param {string} fromPath
 * @param {string} toPath
 * @param {{ baseDir?: never }} [options]
 * @returns {Promise<void>}
 * @remarks 同 {@link rename}：两个路径都要过 scope。目标已存在时**覆盖**。
 */
export function copyFile(fromPath, toPath, options) {
    return invokeWithOptions('copyFile', 'fs.copyFile', { oldPath: fromPath, newPath: toPath }, options);
}

/**
 * 截断（或扩展）文件到指定长度。
 *
 * @param {string} path
 * @param {number} [len] 目标长度（字节）；大于当前长度时用零填充。省略时宿主按 0 处理。
 * @param {{ baseDir?: never }} [options] **第 3 参**：第 2 参是 `len`，不是 options
 * @returns {Promise<void>}
 */
export function truncate(path, len, options) {
    // 注意参数位置：Tauri 的签名是 `truncate(path, len?, options?)`。
    // 若照"第二个参数就是 options"的印象写校验，会把长度当选项拒掉。
    return invokeWithOptions('truncate', 'fs.truncate', { path, len }, options);
}
