/**
 * 平台路径分隔符：Windows 上是 `\`，POSIX 上是 `/`。
 *
 * @returns {string}
 * @remarks **同步**（读宿主注入的常量），所以不必 await。Tauri 亦然。
 */
export function sep(): string;
/**
 * 平台路径分隔**符串**（PATH 环境变量里那种）：Windows 上是 `;`，POSIX 上是 `:`。
 *
 * @returns {string}
 * @remarks
 * 名字容易与 `sep()` 混：`sep` 是**一个路径内**的分隔符，`delimiter` 是**多个路径之间**的分隔符。
 * 同步，理由同 `sep()`。
 */
export function delimiter(): string;
/**
 * 把若干段拼成一个路径，用平台分隔符连接。
 *
 * @param {...string} paths
 * @returns {Promise<string>}
 */
export function join(...paths: string[]): Promise<string>;
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
export function normalize(path: string): Promise<string>;
/**
 * 相对 `path` 的父目录；结尾的分隔符被忽略。
 *
 * @param {string} path
 * @returns {Promise<string>}
 * @remarks
 * 已经是根（`/` 或 `C:\`）时返回**空串**（Tauri 的 `Path::parent()` 给 `None`，
 * 序列化成空串）。调用方据此判断"到头了"。
 */
export function dirname(path: string): Promise<string>;
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
export function extname(path: string): Promise<string>;
/**
 * 末段文件名。
 *
 * @param {string} path
 * @param {string} [ext] 要从结果里去掉的扩展名（**区分大小写**，通常别带前导点）。
 * @returns {Promise<string>}
 */
export function basename(path: string, ext?: string): Promise<string>;
/**
 * 是否绝对路径。
 *
 * @param {string} path
 * @returns {Promise<boolean>}
 * @remarks
 * 判据是**平台相关**的：Windows 上 `/foo` 算绝对（有根无盘符），
 * 而 `foo\bar` 不算。所以这里也交给宿主——JS 里判这个必然在某个平台上判错。
 */
export function isAbsolute(path: string): Promise<boolean>;
/**
 * 把若干段解析成一个绝对路径。
 *
 * @param {...string} paths
 * @returns {Promise<string>}
 * @remarks
 * 与 `join()` 的差别：本函数**保证结果是绝对路径**（相对路径会拼上当前工作目录），
 * `join()` 则可能给出相对路径。同理不做符号链接解析（见 `normalize`）。
 */
export function resolve(...paths: string[]): Promise<string>;
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
export function resolveResource(resourcePath: string): Promise<string>;
/**
 * 应用配置文件目录：`${configDir}/${identifier}`。对标 Tauri 的 `appConfigDir()`。
 *
 * @type {() => Promise<string>}
 */
export const appConfigDir: () => Promise<string>;
/** 应用数据目录：`${dataDir}/${identifier}`。 */
export const appDataDir: () => Promise<string>;
/** 应用本地数据目录：`${localDataDir}/${identifier}`。 */
export const appLocalDataDir: () => Promise<string>;
/** 应用缓存目录：`${cacheDir}/${identifier}`。 */
export const appCacheDir: () => Promise<string>;
/**
 * 应用日志目录。
 *
 * @remarks
 * 三平台规则里唯一一个全都不同的：macOS 是 `~/Library/Logs/{identifier}`（**不在**
 * 应用数据目录下），另两个平台是 `${localDataDir}/${identifier}/logs`。
 */
export const appLogDir: () => Promise<string>;
/** 用户的音乐目录。Linux `XDG_MUSIC_DIR` / macOS `~/Music` / Windows `{FOLDERID_Music}`。 */
export const audioDir: () => Promise<string>;
/** 用户的缓存目录。Linux `$XDG_CACHE_HOME` 或 `~/.cache` / macOS `~/Library/Caches` / Windows `{FOLDERID_LocalAppData}`。 */
export const cacheDir: () => Promise<string>;
/** 用户的配置目录。Linux `$XDG_CONFIG_HOME` 或 `~/.config` / macOS `~/Library/Application Support` / Windows `{FOLDERID_RoamingAppData}`。 */
export const configDir: () => Promise<string>;
/** 用户的数据目录。Linux `$XDG_DATA_HOME` 或 `~/.local/share` / macOS `~/Library/Application Support` / Windows `{FOLDERID_RoamingAppData}`。 */
export const dataDir: () => Promise<string>;
/** 用户的本地数据目录。Windows 上是 `{FOLDERID_LocalAppData}`（与 `dataDir` **不同**）。 */
export const localDataDir: () => Promise<string>;
/** 桌面目录。 */
export const desktopDir: () => Promise<string>;
/** 文档目录。 */
export const documentDir: () => Promise<string>;
/** 下载目录。 */
export const downloadDir: () => Promise<string>;
/**
 * 可执行文件目录。
 *
 * @remarks
 * **只有 Linux 支持**（`$XDG_BIN_HOME` 或 `~/.local/bin`）；macOS 与 Windows 上调用会
 * 以 `not_supported` 被拒——这一点与 Tauri 一致。请按平台分支。
 */
export const executableDir: () => Promise<string>;
/**
 * 字体目录。
 *
 * @remarks
 * Linux `$XDG_DATA_HOME/fonts`、macOS `~/Library/Fonts`；**Windows 上不支持**（与 Tauri 一致）。
 */
export const fontDir: () => Promise<string>;
/** 家目录。 */
export const homeDir: () => Promise<string>;
/** 图片目录。 */
export const pictureDir: () => Promise<string>;
/** 公共目录。Windows 上是 `{FOLDERID_Public}`（`C:\Users\Public`，**不是** `...\Documents`）。 */
export const publicDir: () => Promise<string>;
/**
 * 资源目录（随应用分发的额外文件，如数据库模板、脚本）。
 *
 * @remarks
 * Windows 是主可执行文件所在目录；macOS 是 `${exe_dir}/../Resources`；
 * Linux 是 `$APPDIR/usr/lib/{exe}`（AppImage）或 `/usr/lib/{exe}`。
 *
 * 要拼具体文件用 {@link resolveResource}——它有越界校验。
 */
export const resourceDir: () => Promise<string>;
/**
 * 运行时目录。
 *
 * @remarks
 * **只有 Linux 支持**（`$XDG_RUNTIME_DIR`）；macOS 与 Windows 上以 `not_supported` 被拒。
 * Linux 上若该环境变量缺失也会被拒——**不会**退到 `/tmp`：
 * `/tmp` 是全局共享的，不是"本用户的运行时目录"那个语义，给个错值比明确失败糟得多。
 */
export const runtimeDir: () => Promise<string>;
/** 临时目录。 */
export const tempDir: () => Promise<string>;
/**
 * 模板目录。
 *
 * @remarks Linux `XDG_TEMPLATES_DIR`、Windows `{FOLDERID_Templates}`；**macOS 上不支持**（与 Tauri 一致）。
 */
export const templateDir: () => Promise<string>;
/** 视频目录。 */
export const videoDir: () => Promise<string>;
