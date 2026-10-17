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

// ---------------------------------------------------------------------------
// 目录解析（对标 Tauri 的 appDataDir / homeDir / … 那一整组）
//
// **命令名与 Tauri 的函数名一一对应**（path.appDataDir ↔ appDataDir()），
// 所以下面这些函数体全是同一形状，不必逐个写实现。
//
// 三平台的解析规则各格不同（Linux 走 XDG / macOS 走 ~/Library / Windows 走 FOLDERID_*），
// 由**宿主**算——理由与上面那些路径运算一样，而且这里更强：XDG 的用户目录
// 是用户可配置的（中文系统上"桌面"常常就是 ~/桌面），Windows 的已知文件夹
// 也可以被重定向到别的盘。在 JS 里拼字符串必然在某个用户的机器上错。
// ---------------------------------------------------------------------------

/**
 * 生成一个目录函数：命令名 = `path.` + name + `Dir`。
 *
 * @param {string} name 目录名（与 Tauri 的 BaseDirectory 取值同名，首字母大写）。
 * @returns {() => Promise<string>}
 */
function dirFunction(name) {
    // 目录名的首字母在 JS 里取：`name[0]` 在 noUncheckedIndexedAccess 下是 string|undefined，
    // 而这些名字是模块内的字面量，不会空。用 charAt 表达"一定有一个字符"更直白，
    // 也免了一个只为了哄类型检查器的非空断言。
    const command = `path.${name.charAt(0).toLowerCase()}${name.slice(1)}Dir`;
    return () => invoke(command);
}

/**
 * 应用配置文件目录：`${configDir}/${identifier}`。对标 Tauri 的 `appConfigDir()`。
 *
 * @type {() => Promise<string>}
 */
export const appConfigDir = dirFunction('AppConfig');

/** 应用数据目录：`${dataDir}/${identifier}`。 */
export const appDataDir = dirFunction('AppData');

/** 应用本地数据目录：`${localDataDir}/${identifier}`。 */
export const appLocalDataDir = dirFunction('AppLocalData');

/** 应用缓存目录：`${cacheDir}/${identifier}`。 */
export const appCacheDir = dirFunction('AppCache');

/**
 * 应用日志目录。
 *
 * @remarks
 * 三平台规则里唯一一个全都不同的：macOS 是 `~/Library/Logs/{identifier}`（**不在**
 * 应用数据目录下），另两个平台是 `${localDataDir}/${identifier}/logs`。
 */
export const appLogDir = dirFunction('AppLog');

/** 用户的音乐目录。Linux `XDG_MUSIC_DIR` / macOS `~/Music` / Windows `{FOLDERID_Music}`。 */
export const audioDir = dirFunction('Audio');

/** 用户的缓存目录。Linux `$XDG_CACHE_HOME` 或 `~/.cache` / macOS `~/Library/Caches` / Windows `{FOLDERID_LocalAppData}`。 */
export const cacheDir = dirFunction('Cache');

/** 用户的配置目录。Linux `$XDG_CONFIG_HOME` 或 `~/.config` / macOS `~/Library/Application Support` / Windows `{FOLDERID_RoamingAppData}`。 */
export const configDir = dirFunction('Config');

/** 用户的数据目录。Linux `$XDG_DATA_HOME` 或 `~/.local/share` / macOS `~/Library/Application Support` / Windows `{FOLDERID_RoamingAppData}`。 */
export const dataDir = dirFunction('Data');

/** 用户的本地数据目录。Windows 上是 `{FOLDERID_LocalAppData}`（与 `dataDir` **不同**）。 */
export const localDataDir = dirFunction('LocalData');

/** 桌面目录。 */
export const desktopDir = dirFunction('Desktop');

/** 文档目录。 */
export const documentDir = dirFunction('Document');

/** 下载目录。 */
export const downloadDir = dirFunction('Download');

/**
 * 可执行文件目录。
 *
 * @remarks
 * **只有 Linux 支持**（`$XDG_BIN_HOME` 或 `~/.local/bin`）；macOS 与 Windows 上调用会
 * 以 `not_supported` 被拒——这一点与 Tauri 一致。请按平台分支。
 */
export const executableDir = dirFunction('Executable');

/**
 * 字体目录。
 *
 * @remarks
 * Linux `$XDG_DATA_HOME/fonts`、macOS `~/Library/Fonts`；**Windows 上不支持**（与 Tauri 一致）。
 */
export const fontDir = dirFunction('Font');

/** 家目录。 */
export const homeDir = dirFunction('Home');

/** 图片目录。 */
export const pictureDir = dirFunction('Picture');

/** 公共目录。Windows 上是 `{FOLDERID_Public}`（`C:\Users\Public`，**不是** `...\Documents`）。 */
export const publicDir = dirFunction('Public');

/**
 * 资源目录（随应用分发的额外文件，如数据库模板、脚本）。
 *
 * @remarks
 * Windows 是主可执行文件所在目录；macOS 是 `${exe_dir}/../Resources`；
 * Linux 是 `$APPDIR/usr/lib/{exe}`（AppImage）或 `/usr/lib/{exe}`。
 *
 * 要拼具体文件用 {@link resolveResource}——它有越界校验。
 */
export const resourceDir = dirFunction('Resource');

/**
 * 运行时目录。
 *
 * @remarks
 * **只有 Linux 支持**（`$XDG_RUNTIME_DIR`）；macOS 与 Windows 上以 `not_supported` 被拒。
 * Linux 上若该环境变量缺失也会被拒——**不会**退到 `/tmp`：
 * `/tmp` 是全局共享的，不是"本用户的运行时目录"那个语义，给个错值比明确失败糟得多。
 */
export const runtimeDir = dirFunction('Runtime');

/** 临时目录。 */
export const tempDir = dirFunction('Temp');

/**
 * 模板目录。
 *
 * @remarks Linux `XDG_TEMPLATES_DIR`、Windows `{FOLDERID_Templates}`；**macOS 上不支持**（与 Tauri 一致）。
 */
export const templateDir = dirFunction('Template');

/** 视频目录。 */
export const videoDir = dirFunction('Video');

/**
 * 把一段相对路径解析到资源目录下（对标 Tauri 的 `resolveResource()`）。
 *
 * @param {string} resourcePath 相对资源目录的路径；子目录与 `..` 都允许，但不能跑出资源目录。
 * @returns {Promise<string>}
 * @remarks
 * 与 `join(await resourceDir(), p)` 的差别是**这里有校验**：绝对路径被拒
 * （那等于"读文件系统任意位置"，而函数名是 resolve**Resource**），
 * 拼完跑出资源目录的也拒。判据是"拼完在不在根之内"而不是"字符串里有没有 `..`"——
 * `assets/../logo.svg` 是合法的、也很常见。
 */
export function resolveResource(resourcePath) {
    return invoke('path.resolveResource', { path: resourcePath });
}
