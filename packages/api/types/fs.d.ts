/**
 * 读文本文件（UTF-8）。
 *
 * @param {string} path 绝对路径，或相对进程当前目录的路径。
 * @returns {Promise<string>}
 */
export function readTextFile(path: string): Promise<string>;
/**
 * 写文本文件（UTF-8）。
 *
 * @param {string} path
 * @param {string} data
 * @returns {Promise<void>}
 * @remarks **父目录会自动创建**：不自动建的话每次写都要先 `mkdir`，
 * 而漏建的症状是 `DirectoryNotFoundException`——报错说"目录不存在"，调用方以为在写文件。
 */
export function writeTextFile(path: string, data: string): Promise<void>;
/**
 * 读二进制文件。
 *
 * @param {string} path
 * @returns {Promise<Uint8Array>}
 */
export function readFile(path: string): Promise<Uint8Array>;
/**
 * 写二进制文件。
 *
 * @param {string} path
 * @param {Uint8Array | number[]} data
 * @returns {Promise<void>}
 * @remarks
 * 传 `Uint8Array` 时这里会转成普通数组再发：JSON 不认 `Uint8Array`，
 * 直接塞进 `JSON.stringify` 会得到一个**对象**（`{"0":1,"1":2}`）而不是数组，
 * 而宿主侧期望数组、会报类型不符。
 */
export function writeFile(path: string, data: Uint8Array | number[]): Promise<void>;
/**
 * 路径是否存在。
 *
 * @param {string} path
 * @returns {Promise<boolean>}
 * @remarks **也要过 scope**：不过的话它就成了一个"这台机器上有哪些文件"的探测器，
 * 而那种信息在攻防里很有用（先枚举、再决定读什么）。
 */
export function exists(path: string): Promise<boolean>;
/**
 * 文件大小（字节）。目录返回宿主定义的值（通常 0）。
 *
 * @param {string} path
 * @returns {Promise<number>}
 */
export function size(path: string): Promise<number>;
/**
 * 目录项列表。
 *
 * @param {string} path
 * @returns {Promise<Array<{ name: string, isFile: boolean, isDirectory: boolean, isSymlink: boolean }>>}
 */
export function readDir(path: string): Promise<Array<{
    name: string;
    isFile: boolean;
    isDirectory: boolean;
    isSymlink: boolean;
}>>;
/**
 * 文件元信息（跟随符号链接）。
 *
 * @param {string} path
 * @returns {Promise<{ isFile: boolean, isDirectory: boolean, isSymlink: boolean,
 *   size: number, mtime: number, atime: number, birthtime: number }>}
 * @remarks 时间是**毫秒时间戳**（Unix epoch，UTC），不是 `Date`。
 * 与 Tauri 的差别：那边给 `Date` 对象。用时间戳是因为它跨 JSON 无损，
 * 而 `Date` 要走 `new Date(x)` 转换——构造一杯都行，但时间戳更少一层意外。
 */
export function stat(path: string): Promise<{
    isFile: boolean;
    isDirectory: boolean;
    isSymlink: boolean;
    size: number;
    mtime: number;
    atime: number;
    birthtime: number;
}>;
/**
 * 文件元信息（**不跟随**符号链接）。
 *
 * @param {string} path
 * @returns {Promise<object>}
 * @remarks 当前与 {@link stat} 返回同样的元数据（只是 `isSymlink` 如实报告）。
 * .NET 跟随符号链接，编出链接自身的长度/时间要额外做系统调用，本库目前不需要那个精度。
 */
export function lstat(path: string): Promise<object>;
/**
 * 创建目录。
 *
 * @param {string} path
 * @param {{ recursive?: boolean }} [options]
 * @returns {Promise<void>}
 * @remarks 非递归且目录已存在 → 报错（与 Tauri / Node 一致）；递归时已存在是正常的。
 */
export function mkdir(path: string, options?: {
    recursive?: boolean;
}): Promise<void>;
/**
 * 删除文件或目录。
 *
 * @param {string} path
 * @param {{ recursive?: boolean }} [options] 删目录时必须为 `true`（否则非空目录会报错）。
 * @returns {Promise<void>}
 */
export function remove(path: string, options?: {
    recursive?: boolean;
}): Promise<void>;
/**
 * 重命名 / 移动。
 *
 * @param {string} oldPath
 * @param {string} newPath
 * @returns {Promise<void>}
 * @remarks **两个路径都要在 scope 内**：只查源的话，页面能把允许目录里的文件
 * "搬"到 scope 之外（那是一次写）。
 */
export function rename(oldPath: string, newPath: string): Promise<void>;
/**
 * 复制文件。
 *
 * @param {string} fromPath
 * @param {string} toPath
 * @returns {Promise<void>}
 * @remarks 同 {@link rename}：两个路径都要过 scope。目标已存在时**覆盖**。
 */
export function copyFile(fromPath: string, toPath: string): Promise<void>;
/**
 * 截断（或扩展）文件到指定长度。
 *
 * @param {string} path
 * @param {number} len 目标长度（字节）；大于当前长度时用零填充。
 * @returns {Promise<void>}
 */
export function truncate(path: string, len: number): Promise<void>;
