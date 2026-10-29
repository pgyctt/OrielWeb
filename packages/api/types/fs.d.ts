/**
 * 读文本文件（UTF-8）。
 *
 * @param {string} path 绝对路径，或相对进程当前目录的路径。
 * @param {{ baseDir?: number }} [options] `baseDir` 用 `BaseDirectory` 枚举把相对路径解析到对应目录（绝对路径不参与）
 * @returns {Promise<string>}
 */
export function readTextFile(path: string, options?: {
    baseDir?: number;
}): Promise<string>;
/**
 * 写文本文件（UTF-8）。
 *
 * @param {string} path
 * @param {string} data
 * @param {{ baseDir?: number }} [options]
 * @returns {Promise<void>}
 * @remarks **父目录会自动创建**：不自动建的话每次写都要先 `mkdir`，
 * 而漏建的症状是 `DirectoryNotFoundException`——报错说"目录不存在"，调用方以为在写文件。
 */
export function writeTextFile(path: string, data: string, options?: {
    baseDir?: number;
}): Promise<void>;
/**
 * 读二进制文件。
 *
 * @param {string} path
 * @param {{ baseDir?: number }} [options]
 * @returns {Promise<Uint8Array>}
 */
export function readFile(path: string, options?: {
    baseDir?: number;
}): Promise<Uint8Array>;
/**
 * 写二进制文件。
 *
 * @param {string} path
 * @param {Uint8Array | number[]} data
 * @param {{ baseDir?: number }} [options]
 * @returns {Promise<void>}
 * @remarks
 * 传 `Uint8Array` 时这里会转成普通数组再发：JSON 不认 `Uint8Array`，
 * 直接塞进 `JSON.stringify` 会得到一个**对象**（`{"0":1,"1":2}`）而不是数组，
 * 而宿主侧期望数组、会报类型不符。
 */
export function writeFile(path: string, data: Uint8Array | number[], options?: {
    baseDir?: number;
}): Promise<void>;
/**
 * 路径是否存在。
 *
 * @param {string} path
 * @param {{ baseDir?: number }} [options]
 * @returns {Promise<boolean>}
 * @remarks **也要过 scope**：不过的话它就成了一个"这台机器上有哪些文件"的探测器，
 * 而那种信息在攻防里很有用（先枚举、再决定读什么）。
 */
export function exists(path: string, options?: {
    baseDir?: number;
}): Promise<boolean>;
/**
 * 文件大小（字节）。目录返回宿主定义的值（通常 0）。
 *
 * @param {string} path
 * @param {{ baseDir?: number }} [options]
 * @returns {Promise<number>}
 */
export function size(path: string, options?: {
    baseDir?: number;
}): Promise<number>;
/**
 * 目录项列表。
 *
 * @param {string} path
 * @param {{ baseDir?: number }} [options]
 * @returns {Promise<Array<{ name: string, isFile: boolean, isDirectory: boolean, isSymlink: boolean }>>}
 */
export function readDir(path: string, options?: {
    baseDir?: number;
}): Promise<Array<{
    name: string;
    isFile: boolean;
    isDirectory: boolean;
    isSymlink: boolean;
}>>;
/**
 * 文件元信息（跟随符号链接）。
 *
 * @param {string} path
 * @param {{ baseDir?: number }} [options]
 * @returns {Promise<{ isFile: boolean, isDirectory: boolean, isSymlink: boolean,
 *   size: number, mtime: number, atime: number, birthtime: number }>}
 * @remarks 时间是**毫秒时间戳**（Unix epoch，UTC），不是 `Date`。
 * 与 Tauri 的差别：那边给 `Date` 对象。用时间戳是因为它跨 JSON 无损，
 * 而 `Date` 要走 `new Date(x)` 转换——构造一杯都行，但时间戳更少一层意外。
 *
 * `isSymlink` 说的是**入参本身**是不是链接（在解析之前取的），不是目标的属性。
 * 链接指向链接时它仍是 `true`，指向的东西是什么看 `isFile` / `isDirectory`。
 */
export function stat(path: string, options?: {
    baseDir?: number;
}): Promise<{
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
 * @param {{ baseDir?: number }} [options]
 * @returns {Promise<{ isFile: boolean, isDirectory: boolean, isSymlink: boolean,
 *   size: number, mtime: number, atime: number, birthtime: number }>}
 * @remarks 与 {@link stat} 的**唯一差别在断链**：`stat` 跟随 → 目标不存在就报错；
 * `lstat` 不跟随 → 链接自身存在就成功，返回 `isSymlink: true`、`isFile` / `isDirectory`
 * 为 `false`、数值字段为 `0`（链接自身的长度与时间拿不到，不编假数据）。
 * 有效链接两者返回同一份（目标的）元数据——这是 .NET 的能力边界，不是漏实现。
 *
 * **scope 按解析后的目标判**：指向 scope 外的链接在授权层就被拒，`lstat` 也一样——
 * 否则"scope 外有什么"会变成可探测的信息。
 */
export function lstat(path: string, options?: {
    baseDir?: number;
}): Promise<{
    isFile: boolean;
    isDirectory: boolean;
    isSymlink: boolean;
    size: number;
    mtime: number;
    atime: number;
    birthtime: number;
}>;
/**
 * 创建目录。
 *
 * @param {string} path
 * @param {{ recursive?: boolean, baseDir?: number }} [options]
 * @returns {Promise<void>}
 * @remarks 非递归且目录已存在 → 报错（与 Tauri / Node 一致）；递归时已存在是正常的。
 */
export function mkdir(path: string, options?: {
    recursive?: boolean;
    baseDir?: number;
}): Promise<void>;
/**
 * 删除文件或目录。
 *
 * @param {string} path
 * @param {{ recursive?: boolean, baseDir?: number }} [options] 删目录时必须为 `true`（否则非空目录会报错）。
 * @returns {Promise<void>}
 */
export function remove(path: string, options?: {
    recursive?: boolean;
    baseDir?: number;
}): Promise<void>;
/**
 * 重命名 / 移动。
 *
 * @param {string} oldPath
 * @param {string} newPath
 * @param {{ baseDir?: number }} [options]
 * @returns {Promise<void>}
 * @remarks **两个路径都要在 scope 内**：只查源的话，页面能把允许目录里的文件
 * "搬"到 scope 之外（那是一次写）。
 */
export function rename(oldPath: string, newPath: string, options?: {
    baseDir?: number;
}): Promise<void>;
/**
 * 复制文件。
 *
 * @param {string} fromPath
 * @param {string} toPath
 * @param {{ baseDir?: number }} [options]
 * @returns {Promise<void>}
 * @remarks 同 {@link rename}：两个路径都要过 scope。目标已存在时**覆盖**。
 */
export function copyFile(fromPath: string, toPath: string, options?: {
    baseDir?: number;
}): Promise<void>;
/**
 * 截断（或扩展）文件到指定长度。
 *
 * @param {string} path
 * @param {number} [len] 目标长度（字节）；大于当前长度时用零填充。省略时宿主按 0 处理。
 * @param {{ baseDir?: number }} [options] **第 3 参**：第 2 参是 `len`，不是 options
 * @returns {Promise<void>}
 */
export function truncate(path: string, len?: number, options?: {
    baseDir?: number;
}): Promise<void>;
/**
 * 打开一个已存在的文件（或按选项创建），拿到句柄（对标 Tauri 的 `open`）。
 *
 * @param {string} path
 * @param {{ read?: boolean, write?: boolean, append?: boolean, truncate?: boolean,
 *   create?: boolean, createNew?: boolean, baseDir?: number }} [options]
 * @returns {Promise<FileHandle>}
 * @remarks
 * 选项语义与 Tauri（Rust 的 `OpenOptions`）一致：
 * `read` / `write` / `append` **至少要有一个为 `true`**（全 false 打开没有意义，宿主会报错）；
 * `truncate` 需要写权限；`createNew`（必须不存在）与 `create`（允许已存在）互斥。
 * 冲突组合**当场报错**而不是猜一个意图——猜错会截断数据或意外建文件。
 *
 * 需要能力声明（`fs.open`）与**路径 scope**。
 */
export function open(path: string, options?: {
    read?: boolean;
    write?: boolean;
    append?: boolean;
    truncate?: boolean;
    create?: boolean;
    createNew?: boolean;
    baseDir?: number;
}): Promise<FileHandle>;
/**
 * 创建一个文件（已存在则清空）并打开句柄（对标 Tauri 的 `create`）。
 *
 * @param {string} path
 * @param {{ baseDir?: number }} [options]
 * @returns {Promise<FileHandle>}
 * @remarks 等价于 `open(path, { write: true, create: true, truncate: true, read: true })`。
 * **父目录会自动创建**——与本库 `writeTextFile` 的既有约定一致。
 *
 * 需要能力声明（`fs.create`）与**路径 scope**。
 */
export function create(path: string, options?: {
    baseDir?: number;
}): Promise<FileHandle>;
/**
 * 逐行读一个文本文件（UTF-8），返回异步迭代器（对标 Tauri 的 `readTextFileLines`）。
 *
 * @param {string} path
 * @param {{ baseDir?: number }} [options]
 * @returns {Promise<AsyncIterableIterator<string>>}
 * @remarks
 * ```js
 * for await (const line of await readTextFileLines('app.log')) {
 *     console.log(line);
 * }
 * ```
 * **迭代契约与 Tauri 一致，但不是流式**：宿主侧整文件读入再切行（本库的 IPC
 * 是纯 JSON，真流式要占一条 Channel）。代价是内存 = 文件大小——大文件请用
 * {@link open} + 分块 {@link FileHandle.read} 自己处理。
 *
 * 切分规则：按 `\n` 切、去掉紧随的 `\r`（CRLF/LF 都支持）；末尾换行不产生空行，
 * 文件中间的空行照常保留。空文件给零行。
 *
 * 需要能力声明（`fs.readTextFileLines`）与**路径 scope**。
 */
export function readTextFileLines(path: string, options?: {
    baseDir?: number;
}): Promise<AsyncIterableIterator<string>>;
/**
 * 监视一批路径（对标 Tauri 的 `watch`）。
 *
 * @param {string | URL | Array<string | URL>} paths 要监视的路径
 * @param {(event: WatchEvent) => void} cb 收到变化时调用
 * @param {DebouncedWatchOptions} [options]
 * @returns {Promise<UnwatchFn>} 停止监视的函数
 * @remarks
 * <b>合并窗口</b>：默认 `delayMs = 2000`（与 Tauri 一致）。窗口内的变化先攒着，
 * 窗口结束再发；<b>按路径合并、后来的种类覆盖先前的</b>（"先建后改"最终报 `modify`），
 * 重命名按 `(旧, 新)` 单独成条。这不是 `notify-debouncer-full` 的逐字段合并算法，
 * 但保证"一个路径不会在窗口里报两次"。
 *
 * ```js
 * const unwatch = await watch('logs', (event) => {
 *     console.log(event.type, event.paths);
 * }, { baseDir: BaseDirectory.AppLog, recursive: true });
 * // 用完停掉
 * unwatch();
 * ```
 *
 * <b>事件只回给发起监视的那个窗口</b>（Tauri 的 Channel 同样绑在调用方 webview 上）。
 * 窗口关闭后监视器仍然存在（事件没有收件人），直到 `unwatch()` 或应用退出——
 * 监视不该因为页面导航这类与用户意图无关的事情被悄悄取消。
 *
 * 需要能力声明（`fs.watch`）与**每一个路径的 scope**。
 */
export function watch(paths: string | URL | Array<string | URL>, cb: (event: WatchEvent) => void, options?: DebouncedWatchOptions): Promise<UnwatchFn>;
/**
 * 监视一批路径，变化**立刻**上报（对标 Tauri 的 `watchImmediate`）。
 *
 * @param {string | URL | Array<string | URL>} paths
 * @param {(event: WatchEvent) => void} cb
 * @param {WatchOptions} [options]
 * @returns {Promise<UnwatchFn>}
 * @remarks
 * 与 {@link watch} 的唯一差别是**没有合并窗口**：一次保存被系统拆成三条事件，这里就收到三条
 * （Windows 上很常见：`Changed` 会来两次）。要"一次保存只处理一次"就用 {@link watch}。
 *
 * 需要能力声明（`fs.watch`）与**每一个路径的 scope**。
 */
export function watchImmediate(paths: string | URL | Array<string | URL>, cb: (event: WatchEvent) => void, options?: WatchOptions): Promise<UnwatchFn>;
export { BaseDirectory } from "./path.js";
/**
 * 定位方式（对标 Tauri 的 `SeekMode`）。
 *
 * @readonly
 * @remarks 取值与 Tauri 的 TS 枚举逐键一致（`Start` = 0 / `Current` = 1 / `End` = 2）。
 * 反向映射（`0: 'Start'` 这类）也一并给：Tauri 的数值枚举在运行期就有它们，
 * 迁移代码里两种写法都可能出现（与 `BaseDirectory` 同一理由）。
 */
export const SeekMode: Readonly<{
    Start: 0;
    Current: 1;
    End: 2;
    0: "Start";
    1: "Current";
    2: "End";
}>;
/**
 * 一个打开的文件句柄（对标 Tauri 的 `FileHandle`）。
 *
 * @remarks
 * 用 {@link open} / {@link create} 取得。**用完要 `close()`**——句柄占着操作系统的
 * 文件描述符，应用退出时会由宿主统一关闭（`OrielApp.Dispose` 里的句柄表清理），
 * 但长跑的应用自己关才是正确的做法。
 *
 * 本类没有 `disk` 之类的属性；`rid` 是**补壳**（返回句柄 id）——Tauri 那边它是
 * 资源表里的编号，本库没有通用资源表，id 就是句柄号，语义上正好对得上。
 */
export class FileHandle {
    /**
     * @param {number} id 宿主分配的句柄 id（内部用；页面侧从 open/create 得到实例）。
     */
    constructor(id: number);
    /** @type {number} */
    id: number;
    /**
     * 句柄编号（对标 `Resource.rid`）。
     *
     * @returns {number}
     */
    get rid(): number;
    /**
     * 从当前读写位置读一段字节，**填进传入的缓冲区**（与 Tauri 一致）。
     *
     * @param {Uint8Array} buffer 要填的缓冲区；读多少由它的长度决定。
     * @returns {Promise<number | null>} 实际读到的字节数；**已到文件末尾返回 `null`**。
     * @remarks
     * 返回 `null`（而不是 0）是为了与"读到了 0 字节"区分开——Tauri 同样是
     * `number | null`。单次最多 8 MiB（本库 IPC 是纯 JSON，一次读一大块没有意义；
     * 需要更大范围请分块循环）。
     *
     * 缓冲区长度为 0 时直接返回 0，不发 IPC。
     */
    read(buffer: Uint8Array): Promise<number | null>;
    /**
     * 从当前读写位置写入一段字节。
     *
     * @param {Uint8Array | number[]} data
     * @returns {Promise<number>} 写入的字节数。
     * @remarks
     * **不逐次落盘**：缓冲交给操作系统（Tauri 的 `write_all` 同样不 flush）。
     * 同一个句柄里的 write → seek → read 是缓冲一致的；要确保别的进程/句柄
     * 立刻看到内容，请 `close()`（关闭时会落盘）。
     */
    write(data: Uint8Array | number[]): Promise<number>;
    /**
     * 移动读写位置。
     *
     * @param {number} offset 偏移量（字节，可为负——负值从 End/Current 往回退）。
     * @param {number} [whence] 定位方式，见 {@link SeekMode}；缺省 `SeekMode.Start`。
     * @returns {Promise<number>} 新的绝对位置（从文件头算）。
     * @remarks 移到文件头之前是错误（宿主会抛，而不是静默钳到 0）。
     */
    seek(offset: number, whence?: number): Promise<number>;
    /**
     * 这个打开文件的元信息（形状与 {@link stat} 一致）。
     *
     * @returns {Promise<{ isFile: boolean, isDirectory: boolean, isSymlink: boolean,
     *   size: number, mtime: number, atime: number, birthtime: number }>}
     * @remarks 取的是**描述符**上的元信息：文件被改名/挪走之后仍然给得出正确值
     * （`fs.stat` 按路径查，那种场景会报"找不到文件"）。
     * `isSymlink` 恒为 `false`——句柄已经跟随过链接，与 Rust 的 `File::metadata()` 一致。
     * `size` 是句柄看到的**当前**长度。
     */
    stat(): Promise<{
        isFile: boolean;
        isDirectory: boolean;
        isSymlink: boolean;
        size: number;
        mtime: number;
        atime: number;
        birthtime: number;
    }>;
    /**
     * 把文件截断（或扩展到）指定长度。
     *
     * @param {number} [len] 目标长度；**省略时截到当前读写位置**（与 Tauri 一致）。
     * @returns {Promise<void>}
     */
    truncate(len?: number): Promise<void>;
    /**
     * 关闭句柄（触发落盘并释放文件描述符）。
     *
     * @returns {Promise<void>}
     * @remarks **不可重复关闭**：第二次 `close()` 会报"句柄不存在或已关闭"
     * （与 Tauri 的 Resource 语义一致）。关闭后的读写同样报这个错。
     */
    close(): Promise<void>;
}
/**
 * 一次变化里"访问"的种类（对标 Tauri 的 `WatchEventKindAccess`）。
 */
export type WatchEventKindAccess = {
    kind: "any";
} | {
    kind: "close";
    mode: WatchEventMode;
} | {
    kind: "open";
    mode: WatchEventMode;
} | {
    kind: "other";
};
/**
 * 访问模式（对标 Tauri 的 `WatchEventKindClose`/`Open` 的 `mode`）。
 */
export type WatchEventMode = "any" | "execute" | "read" | "write" | "other";
/**
 * 新建了什么（对标 Tauri 的 `WatchEventKindCreate`）。
 */
export type WatchEventKindCreate = {
    kind: "any";
} | {
    kind: "file";
} | {
    kind: "folder";
} | {
    kind: "other";
};
/**
 * 改了什么（对标 Tauri 的 `WatchEventKindModify`）。
 */
export type WatchEventKindModify = {
    kind: "any";
} | {
    kind: "data";
    mode: "any" | "size" | "content" | "other";
} | {
    kind: "metadata";
    mode: "any" | "access-time" | "write-time" | "permissions" | "ownership" | "extended" | "other";
} | {
    kind: "rename";
    mode: "any" | "to" | "from" | "both" | "other";
} | {
    kind: "other";
};
/**
 * 删掉了什么（对标 Tauri 的 `WatchEventKindRemove`）。
 */
export type WatchEventKindRemove = {
    kind: "any";
} | {
    kind: "file";
} | {
    kind: "folder";
} | {
    kind: "other";
};
/**
 * 变化的种类（对标 Tauri 的 `WatchEventKind`）。
 */
export type WatchEventKind = "any" | "other" | {
    access: WatchEventKindAccess;
} | {
    create: WatchEventKindCreate;
} | {
    modify: WatchEventKindModify;
} | {
    remove: WatchEventKindRemove;
};
/**
 * 一次文件系统变化（对标 Tauri 的 `WatchEvent`）。
 */
export type WatchEvent = {
    /**
     * 变化的种类
     */
    type: WatchEventKind;
    /**
     * 受影响的路径（**绝对路径**）
     */
    paths: string[];
    /**
     * 底层 watcher 的附加属性
     */
    attrs: unknown;
};
/**
 * 停止监视（对标 Tauri 的 `UnwatchFn`）。同步调用，幂等。
 */
export type UnwatchFn = () => void;
/**
 * `watchImmediate` 的选项（对标 Tauri 的 `WatchOptions`）。
 */
export type WatchOptions = {
    /**
     * 目录是否递归（对单文件无意义）
     */
    recursive?: boolean | undefined;
    /**
     * 用 `BaseDirectory` 枚举把相对路径解析到对应目录
     */
    baseDir?: number | undefined;
};
/**
 * `watch` 的选项（对标 Tauri 的 `DebouncedWatchOptions`）。
 */
export type DebouncedWatchOptions = WatchOptions & {
    delayMs?: number;
};
