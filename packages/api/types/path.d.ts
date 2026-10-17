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
