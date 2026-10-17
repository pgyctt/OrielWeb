// 对标 `@tauri-apps/api/path` 里的**路径运算**部分。
//
// 一个反直觉的事实（读官方源码才确认）：Tauri 2 里除 `sep()` / `delimiter()` 外，
// **全部**路径运算都走 IPC，交给宿主的 `std::path`。所以本模块也是这样——
// 只有那两个常量是同步的。
//
// 这样做的理由不是照抄，而是平台路径规则的正确性：Windows 的盘符、UNC、`\\?\` 前缀、
// 大小写不敏感，POSIX 的重复斜杠与尾随斜杠…… 在 JS 里手写一份就是又造一份平台语义，
// 而两份实现必然在某个边角上不一致，症状是"只在某个平台错"。宿主有 .NET 的 Path，
// 那是这些规则唯一的权威实现。
//
// 本模块**不做文件系统 I/O**：`normalize` / `resolve` 是词法归一（见下），不解析符号链接。

import { invoke } from './core.js';
import { getTransport } from './internal.js';

/**
 * 平台路径分隔符：Windows 上是 `\`，POSIX 上是 `/`。
 *
 * @returns {string}
 * @remarks **同步**（读宿主注入的常量），所以不必 await。Tauri 亦然。
 */
export function sep() {
    return /** @type {any} */ (getTransport()).path?.sep ?? '/';
}

/**
 * 平台路径分隔**符串**（PATH 环境变量里那种）：Windows 上是 `;`，POSIX 上是 `:`。
 *
 * @returns {string}
 * @remarks
 * 名字容易与 `sep()` 混：`sep` 是**一个路径内**的分隔符，`delimiter` 是**多个路径之间**的分隔符。
 * 同步，理由同 `sep()`。
 */
export function delimiter() {
    return /** @type {any} */ (getTransport()).path?.delimiter ?? ':';
}

/**
 * 把若干段拼成一个路径，用平台分隔符连接。
 *
 * @param {...string} paths
 * @returns {Promise<string>}
 */
export function join(...paths) {
    return invoke('path.join', { paths });
}

/**
 * 归一化路径：展开 `.` / `..`、统一分隔符、拼成绝对路径。
 *
 * @param {string} path
 * @returns {Promise<string>}
 * @remarks
 * **与 Tauri 的差别（刻意，且是改进）**：Tauri 的 `normalize` 用 `std::fs::canonicalize`，
 * 它会解析符号链接，而且**路径不存在时直接报错**——"把用户输入的路径先归一一下"
 * 在路径还不存在时就会失败，是个很容易踩的坑。
 * 本库用词法归一，**不做任何文件系统访问**，所以对不存在的路径也能正确工作。
 * 需要"真实路径"（解析链接）应当是一个显式的 fs 操作，而不是 `normalize` 偷偷做的事。
 */
export function normalize(path) {
    return invoke('path.normalize', { path });
}

/**
 * 相对 `path` 的父目录；结尾的分隔符被忽略。
 *
 * @param {string} path
 * @returns {Promise<string>}
 * @remarks
 * 已经是根（`/` 或 `C:\`）时返回**空串**（Tauri 的 `Path::parent()` 给 `None`，
 * 序列化成空串）。调用方据此判断"到头了"。
 */
export function dirname(path) {
    return invoke('path.dirname', { path });
}

/**
 * 扩展名，**不带点**（`'html'`，不是 `'.html'`）。
 *
 * @param {string} path
 * @returns {Promise<string>}
 * @remarks
 * 两处与 Node 的 `path.extname` 不同，都是跟 Tauri/Rust 而不是跟 Node：
 * 不含前导点；没有扩展名时返回**空串**而不是抛错。
 * 以点开头的隐藏文件（`.gitignore`）扩展名是空串——整名算文件名。
 */
export function extname(path) {
    return invoke('path.extname', { path });
}

/**
 * 末段文件名。
 *
 * @param {string} path
 * @param {string} [ext] 要从结果里去掉的扩展名（**区分大小写**，通常别带前导点）。
 * @returns {Promise<string>}
 */
export function basename(path, ext) {
    return invoke('path.basename', ext === undefined ? { path } : { path, ext });
}

/**
 * 是否绝对路径。
 *
 * @param {string} path
 * @returns {Promise<boolean>}
 * @remarks
 * 判据是**平台相关**的：Windows 上 `/foo` 算绝对（有根无盘符），
 * 而 `foo\bar` 不算。所以这里也交给宿主——JS 里判这个必然在某个平台上判错。
 */
export function isAbsolute(path) {
    return invoke('path.isAbsolute', { path });
}

/**
 * 把若干段解析成一个绝对路径。
 *
 * @param {...string} paths
 * @returns {Promise<string>}
 * @remarks
 * 与 `join()` 的差别：本函数**保证结果是绝对路径**（相对路径会拼上当前工作目录），
 * `join()` 则可能给出相对路径。同理不做符号链接解析（见 `normalize`）。
 */
export function resolve(...paths) {
    return invoke('path.resolve', { paths });
}
