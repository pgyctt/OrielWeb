// 对标 `@tauri-apps/plugin-fs`：整文件族 + 句柄族（B17 起）。
//
// 本模块每个函数都要过**两道关**：宿主侧的命令授权（`Allow("fs.readTextFile")`）
// 与**路径 scope**（`Allow("fs.readTextFile", s => s.AllowPath("$APPDATA/**"))`）。
// 少了后者，一条 XSS 就能把 ~/.ssh/id_rsa 读走——所以本模块与 `path` 有本质差别：
// `path` 只做运算不碰文件系统，整族无需声明；`fs` 碰文件系统，**必须逐条声明 scope**。
//
// 与 Tauri 的差别（如实标注，不假装一致）：
//
//   * **句柄族是 B17 新增的**：`open` / `create` / `FileHandle` / `SeekMode` 已提供，
//     宿主侧有一张 id 句柄表（对标 Tauri 的资源表，见 OrielFileHandleRegistry）。
//     **scope 只在 open/create 时判**——拿到句柄后的读写不再看路径（与 Tauri 相同）。
//     句柄的参数面（read/write/seek/fstat/ftruncate/close）是三平台一致的文件 I/O，
//     没有平台落点差异。
//   * **`readTextFileLines` 不流式**：返回的是一个 async 迭代器，但宿主侧是
//     "整文件读入 → 切行"（本库 IPC 是纯 JSON，真流式要占一条 Channel）。
//     迭代契约与 Tauri 一致，代价是内存 = 文件大小；大文件请用 `open` + 分块 `read`。
//   * **二进制走 JSON 数字数组**：`readFile` 给 `Uint8Array`、`writeFile` 收 `Uint8Array`，
//     但通道是 JSON，所以一个字节要变成两三个字符。几十 MB 级别的文件不适用。
//     Tauri 用 `invoke` 的二进制 body 传，本库的 IPC 是纯 JSON，没有那条路。
//     `FileHandle.read` 的块大小因此有上限（8 MiB/次），见该方法的说明。
//   * **`lstat` 与 `stat` 的差别只在"链接自身算不算"**：.NET 对符号链接会跟随，
//     所以两者返回的元数据相同、只是 `isSymlink` 如实报告。
//   * **监视自 B20 起提供**（`watch` / `watchImmediate`）：底层是 .NET 的
//     `FileSystemWatcher`，与 Tauri 的 `notify` 落到**同一组系统设施**上
//     （ReadDirectoryChangesW / inotify / FSEvents），所以"能看到哪些变化"基本一致。
//     差别在归类与 debounce 的合并算法（见 `WatchEvent` 与 `watch` 的说明），
//     以及**事件只回给发起监视的那个窗口**（Tauri 的 Channel 也绑在调用方 webview 上）。

import { invoke } from './core.js';
import { getTransport } from './internal.js';
import * as paths from './path.js';

// `BaseDirectory` 从 path 模块 re-export（Tauri 的 plugin-fs 也这么干）：
// 迁移代码 `import { BaseDirectory } from '@tauri-apps/plugin-fs'` 能直接改前缀。
export { BaseDirectory } from './path.js';

/**
 * `BaseDirectory` 枚举值 → `path` 模块的目录函数名（键值与 Tauri 的同名枚举逐键一致）。
 *
 * @type {Record<number, string>}
 */
const BASE_DIR_GETTERS = {
    1: 'audioDir', 2: 'cacheDir', 3: 'configDir', 4: 'dataDir', 5: 'localDataDir',
    6: 'documentDir', 7: 'downloadDir', 8: 'pictureDir', 9: 'publicDir', 10: 'videoDir',
    11: 'resourceDir', 12: 'tempDir', 13: 'appConfigDir', 14: 'appDataDir',
    15: 'appLocalDataDir', 16: 'appCacheDir', 17: 'appLogDir', 18: 'desktopDir',
    19: 'executableDir', 20: 'fontDir', 21: 'homeDir', 22: 'runtimeDir', 23: 'templateDir',
};

/** 粗判"已经是绝对路径"（`/`、`X:\`、`\\`）——绝对路径不参与 baseDir 解析（Tauri 语义）。 */
/** @param {unknown} path */
function looksAbsolute(path) {
    return typeof path === 'string'
        && (path.startsWith('/') || path.startsWith('\\') || /^[A-Za-z]:[\\/]/.test(path));
}

/**
 * 把相对路径按 `options.baseDir` 解析成绝对路径（B13 与 Tauri 的 `baseDir` 语义对齐）。
 *
 * @param {string} fnName
 * @param {unknown} path
 * @param {Record<string, unknown> | null | undefined} options
 * @returns {Promise<unknown>} 没有 baseDir 时原样返回；有则返回解析后的字符串。
 */
async function resolveAgainstBase(fnName, path, options) {
    const baseDir = options?.baseDir;
    if (baseDir === undefined || baseDir === null) {
        return path;
    }

    if (typeof path !== 'string') {
        return path;
    }

    if (looksAbsolute(path)) {
        return path;
    }

    const getterName = BASE_DIR_GETTERS[/** @type {number} */ (baseDir)];
    if (getterName === undefined) {
        return Promise.reject(new Error(
            `${fnName}: 不认识的 baseDir 值 ${JSON.stringify(baseDir)}。` +
            `请用 '@orielweb/api/path' 的 BaseDirectory 枚举（与 Tauri 同值）。`));
    }

    const getters = /** @type {Record<string, () => Promise<string>>} */ (/** @type {unknown} */ (paths));
    const getter = getters[getterName];
    if (getter === undefined) {
        return Promise.reject(new Error(`${fnName}: 目录函数 ${getterName} 不存在（BaseDirectory 值与库版本不匹配）。`));
    }

    const dir = await getter();
    // 用 `/` 拼接是**跨仓隐式契约**：宿主侧 OrielPathScope.Normalize 会把混合分隔符
    // 归一后再判 scope（Windows 上这里产出 `C:\...` + `/rel` 的混合串），所以本侧
    // 不按平台选分隔符。改这里的拼接方式前，先看宿主侧的归一逻辑。
    return `${dir}/${path}`;
}

/**
 * 校验 Tauri 签名里的 `options` 位置。
 *
 * @param {string} fnName 调用方函数名（报错里要能定位到是哪个函数）
 * @param {unknown} options 调用方传进来的 options
 * @param {string[]} allowedKeys 本库**真正支持**的键（`baseDir` 恒被接受，不必列入）
 * @returns {Error | null} 有问题时返回错误对象，由调用方 reject
 * @remarks
 * `baseDir` 自 B13 起**受支持**（此前拒绝）：语义与 Tauri 一致——相对路径按
 * `BaseDirectory` 枚举解析到对应目录，绝对路径不参与。解析在页面侧完成
 * （`path` 的目录函数 + 拼接），宿主收到的始终是绝对路径。
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

    const unknown = Object.keys(opts).filter((key) => key !== 'baseDir' && !allowedKeys.includes(key));
    if (unknown.length > 0) {
        return new Error(
            `${fnName}: 不支持的选项 ${unknown.map((k) => `\`${k}\``).join(', ')}。` +
            `本库该函数只接受 ${(allowedKeys.length > 0 ? allowedKeys : ['baseDir']).map((k) => `\`${k}\``).join('/')}。`);
    }

    return null;
}

/**
 * 带 options 校验与 baseDir 解析的 `invoke`。
 *
 * @param {string} fnName
 * @param {string} command
 * @param {Record<string, unknown>} args
 * @param {unknown} options
 * @param {string[]} [allowedKeys]
 * @param {string[]} [pathKeys] 参数里**要按 baseDir 解析**的键（默认 `['path']`）
 * @returns {Promise<any>}
 */
async function invokeWithOptions(fnName, command, args, options, allowedKeys = [], pathKeys = ['path']) {
    const problem = checkOptions(fnName, options, allowedKeys);
    if (problem !== null) {
        return Promise.reject(problem);
    }

    const opts = /** @type {Record<string, unknown> | null | undefined} */ (options);
    const resolved = { ...args };
    for (const key of pathKeys) {
        resolved[key] = await resolveAgainstBase(fnName, resolved[key], opts);
    }

    return invoke(command, resolved);
}

/**
 * 读文本文件（UTF-8）。
 *
 * @param {string} path 绝对路径，或相对进程当前目录的路径。
 * @param {{ baseDir?: number }} [options] `baseDir` 用 `BaseDirectory` 枚举把相对路径解析到对应目录（绝对路径不参与）
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
 * @param {{ baseDir?: number }} [options]
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
 * @param {{ baseDir?: number }} [options]
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
 * @param {{ baseDir?: number }} [options]
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
 * @param {{ baseDir?: number }} [options]
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
 * @param {{ baseDir?: number }} [options]
 * @returns {Promise<number>}
 */
export function size(path, options) {
    return invokeWithOptions('size', 'fs.size', { path }, options);
}

/**
 * 目录项列表。
 *
 * @param {string} path
 * @param {{ baseDir?: number }} [options]
 * @returns {Promise<Array<{ name: string, isFile: boolean, isDirectory: boolean, isSymlink: boolean }>>}
 */
export function readDir(path, options) {
    return invokeWithOptions('readDir', 'fs.readDir', { path }, options);
}

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
 */
export function stat(path, options) {
    return invokeWithOptions('stat', 'fs.stat', { path }, options);
}

/**
 * 文件元信息（**不跟随**符号链接）。
 *
 * @param {string} path
 * @param {{ baseDir?: number }} [options]
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
 * @param {{ recursive?: boolean, baseDir?: number }} [options]
 * @returns {Promise<void>}
 * @remarks 非递归且目录已存在 → 报错（与 Tauri / Node 一致）；递归时已存在是正常的。
 */
export function mkdir(path, options) {
    return invokeWithOptions(
        'mkdir', 'fs.mkdir',
        options?.recursive === true ? { path, recursive: true } : { path },
        options, ['recursive']);
}

/**
 * 删除文件或目录。
 *
 * @param {string} path
 * @param {{ recursive?: boolean, baseDir?: number }} [options] 删目录时必须为 `true`（否则非空目录会报错）。
 * @returns {Promise<void>}
 */
export function remove(path, options) {
    return invokeWithOptions(
        'remove', 'fs.remove',
        options?.recursive === true ? { path, recursive: true } : { path },
        options, ['recursive']);
}

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
export function rename(oldPath, newPath, options) {
    return invokeWithOptions('rename', 'fs.rename', { oldPath, newPath }, options, [], ['oldPath', 'newPath']);
}

/**
 * 复制文件。
 *
 * @param {string} fromPath
 * @param {string} toPath
 * @param {{ baseDir?: number }} [options]
 * @returns {Promise<void>}
 * @remarks 同 {@link rename}：两个路径都要过 scope。目标已存在时**覆盖**。
 */
export function copyFile(fromPath, toPath, options) {
    return invokeWithOptions('copyFile', 'fs.copyFile', { oldPath: fromPath, newPath: toPath }, options, [], ['oldPath', 'newPath']);
}

/**
 * 截断（或扩展）文件到指定长度。
 *
 * @param {string} path
 * @param {number} [len] 目标长度（字节）；大于当前长度时用零填充。省略时宿主按 0 处理。
 * @param {{ baseDir?: number }} [options] **第 3 参**：第 2 参是 `len`，不是 options
 * @returns {Promise<void>}
 */
export function truncate(path, len, options) {
    // 注意参数位置：Tauri 的签名是 `truncate(path, len?, options?)`。
    // 若照"第二个参数就是 options"的印象写校验，会把长度当选项拒掉。
    return invokeWithOptions('truncate', 'fs.truncate', { path, len }, options);
}

// ---------------------------------------------------------------------------
// 句柄族（B17，对标 Tauri 的 open / create / FileHandle / SeekMode）
// ---------------------------------------------------------------------------
//
// 宿主侧有一张 **id 句柄表**（应用级，对标 Tauri 的资源表）。
// **scope 只在 open / create 时判**：拿到句柄后的读写不再看路径——句柄代表
// "已经过授权的那个打开的文件"，反复按路径重判既无意义（路径可能已经变了），
// 也会把"同一文件换了名字就打不开"变成常态。这与 Tauri 一致。

/**
 * 定位方式（对标 Tauri 的 `SeekMode`）。
 *
 * @readonly
 * @remarks 取值与 Tauri 的 TS 枚举逐键一致（`Start` = 0 / `Current` = 1 / `End` = 2）。
 * 反向映射（`0: 'Start'` 这类）也一并给：Tauri 的数值枚举在运行期就有它们，
 * 迁移代码里两种写法都可能出现（与 `BaseDirectory` 同一理由）。
 */
export const SeekMode = Object.freeze({
    Start: 0,
    Current: 1,
    End: 2,
    // 反向映射
    0: 'Start',
    1: 'Current',
    2: 'End',
});

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
    constructor(id) {
        /** @type {number} */
        this.id = id;
    }

    /**
     * 句柄编号（对标 `Resource.rid`）。
     *
     * @returns {number}
     */
    get rid() {
        return this.id;
    }

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
    async read(buffer) {
        if (!(buffer instanceof Uint8Array)) {
            throw new TypeError('FileHandle.read: 需要 Uint8Array 作为缓冲区。');
        }

        if (buffer.length === 0) {
            return 0;
        }

        /** @type {number[] | null} */
        const bytes = await invoke('fs.read', { id: this.id, length: buffer.length });
        if (bytes === null || bytes === undefined) {
            return null;
        }

        const chunk = bytes instanceof Uint8Array ? bytes : Uint8Array.from(bytes);
        buffer.set(chunk, 0);
        return chunk.length;
    }

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
    async write(data) {
        /** @type {number[] | null} */
        const bytes = data instanceof Uint8Array ? Array.from(data)
            : Array.isArray(data) ? data
            : null;
        if (bytes === null) {
            throw new TypeError('FileHandle.write: data 必须是 Uint8Array 或 number[]。');
        }

        return invoke('fs.write', { id: this.id, data: bytes });
    }

    /**
     * 移动读写位置。
     *
     * @param {number} offset 偏移量（字节，可为负——负值从 End/Current 往回退）。
     * @param {number} [whence] 定位方式，见 {@link SeekMode}；缺省 `SeekMode.Start`。
     * @returns {Promise<number>} 新的绝对位置（从文件头算）。
     * @remarks 移到文件头之前是错误（宿主会抛，而不是静默钳到 0）。
     */
    async seek(offset, whence = SeekMode.Start) {
        if (typeof offset !== 'number' || !Number.isFinite(offset)) {
            throw new TypeError('FileHandle.seek: offset 必须是有限数字。');
        }

        return invoke('fs.seek', { id: this.id, offset, whence });
    }

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
    stat() {
        return invoke('fs.fstat', { id: this.id });
    }

    /**
     * 把文件截断（或扩展到）指定长度。
     *
     * @param {number} [len] 目标长度；**省略时截到当前读写位置**（与 Tauri 一致）。
     * @returns {Promise<void>}
     */
    async truncate(len) {
        /** @type {Record<string, unknown>} */
        const args = { id: this.id };
        if (len !== undefined) {
            args.len = len;
        }

        await invoke('fs.ftruncate', args);
    }

    /**
     * 关闭句柄（触发落盘并释放文件描述符）。
     *
     * @returns {Promise<void>}
     * @remarks **不可重复关闭**：第二次 `close()` 会报"句柄不存在或已关闭"
     * （与 Tauri 的 Resource 语义一致）。关闭后的读写同样报这个错。
     */
    async close() {
        await invoke('fs.close', { id: this.id });
    }
}

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
export async function open(path, options) {
    const flags = ['read', 'write', 'append', 'truncate', 'create', 'createNew'];
    const problem = checkOptions('open', options, flags);
    if (problem !== null) {
        throw problem;
    }

    const opts = /** @type {Record<string, unknown> | null | undefined} */ (options);
    /** @type {Record<string, unknown>} */
    const args = { path };
    for (const key of flags) {
        const value = opts?.[key];
        if (value !== undefined) {
            args[key] = value;
        }
    }

    args.path = await resolveAgainstBase('open', path, opts);

    const id = await invoke('fs.open', args);
    return new FileHandle(id);
}

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
export async function create(path, options) {
    const id = await invokeWithOptions('create', 'fs.create', { path }, options);
    return new FileHandle(id);
}

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
export async function readTextFileLines(path, options) {
    /** @type {string[]} */
    const lines = await invokeWithOptions('readTextFileLines', 'fs.readTextFileLines', { path }, options);

    return (async function* () {
        for (const line of lines) {
            yield line;
        }
    })();
}


// ---------------------------------------------------------------------------
// 监视族（B20，对标 Tauri 的 watch / watchImmediate，需要 Rust 侧的 watch feature）
// ---------------------------------------------------------------------------
//
// 宿主侧每个监视器占一个编号（应用级登记表，见 OrielFsWatcherRegistry）；
// 事件经 `oriel://fs-watch/<id>` 只回给**发起监视的那个窗口**。
//
// 与 Tauri 的两处差别，都如实写在下面的注释与 docs/API.md 里：
//   1. 事件种类的归类有三处降级（`FileSystemWatcher` 给不出更细的信息）；
//   2. debounce 是"按路径合并、后来的种类覆盖先前的"，不是 notify-debouncer-full
//      的逐字段合并算法。

/**
 * 一次变化里"访问"的种类（对标 Tauri 的 `WatchEventKindAccess`）。
 *
 * @typedef {{ kind: 'any' }
 *   | { kind: 'close', mode: WatchEventMode }
 *   | { kind: 'open', mode: WatchEventMode }
 *   | { kind: 'other' }} WatchEventKindAccess
 */

/**
 * 访问模式（对标 Tauri 的 `WatchEventKindClose`/`Open` 的 `mode`）。
 *
 * @typedef {'any' | 'execute' | 'read' | 'write' | 'other'} WatchEventMode
 */

/**
 * 新建了什么（对标 Tauri 的 `WatchEventKindCreate`）。
 *
 * @typedef {{ kind: 'any' } | { kind: 'file' } | { kind: 'folder' } | { kind: 'other' }} WatchEventKindCreate
 */

/**
 * 改了什么（对标 Tauri 的 `WatchEventKindModify`）。
 *
 * @typedef {{ kind: 'any' }
 *   | { kind: 'data', mode: 'any' | 'size' | 'content' | 'other' }
 *   | { kind: 'metadata', mode: 'any' | 'access-time' | 'write-time' | 'permissions' | 'ownership' | 'extended' | 'other' }
 *   | { kind: 'rename', mode: 'any' | 'to' | 'from' | 'both' | 'other' }
 *   | { kind: 'other' }} WatchEventKindModify
 */

/**
 * 删掉了什么（对标 Tauri 的 `WatchEventKindRemove`）。
 *
 * @typedef {{ kind: 'any' } | { kind: 'file' } | { kind: 'folder' } | { kind: 'other' }} WatchEventKindRemove
 */

/**
 * 变化的种类（对标 Tauri 的 `WatchEventKind`）。
 *
 * @typedef {'any' | 'other'
 *   | { access: WatchEventKindAccess }
 *   | { create: WatchEventKindCreate }
 *   | { modify: WatchEventKindModify }
 *   | { remove: WatchEventKindRemove }} WatchEventKind
 */

/**
 * 一次文件系统变化（对标 Tauri 的 `WatchEvent`）。
 *
 * @typedef {object} WatchEvent
 * @property {WatchEventKind} type 变化的种类
 * @property {string[]} paths 受影响的路径（**绝对路径**）
 * @property {unknown} attrs 底层 watcher 的附加属性
 */

/**
 * 停止监视（对标 Tauri 的 `UnwatchFn`）。同步调用，幂等。
 *
 * @typedef {() => void} UnwatchFn
 */

/**
 * `watchImmediate` 的选项（对标 Tauri 的 `WatchOptions`）。
 *
 * @typedef {object} WatchOptions
 * @property {boolean} [recursive] 目录是否递归（对单文件无意义）
 * @property {number} [baseDir] 用 `BaseDirectory` 枚举把相对路径解析到对应目录
 */

/**
 * `watch` 的选项（对标 Tauri 的 `DebouncedWatchOptions`）。
 *
 * @typedef {WatchOptions & { delayMs?: number }} DebouncedWatchOptions
 */

/**
 * 把 `string | URL` 统一成路径字符串。
 *
 * @param {string} fnName
 * @param {unknown} value
 * @returns {string}
 * @remarks
 * `file:` URL 会转成路径（Windows 盘符与 UNC 都处理）。其他协议的 URL **当场拒**——
 * 与 Tauri 的 `Must be a file URL.` 同一个姿态：静默当成路径会让
 * `http://x/a` 变成当前目录下一个叫 `http:` 的目录。
 */
function watchPathOf(fnName, value) {
    if (typeof value === 'string') {
        if (value.length === 0) {
            throw new TypeError(`${fnName}: 路径不能是空字符串。`);
        }

        return value;
    }

    if (value instanceof URL) {
        if (value.protocol !== 'file:') {
            throw new TypeError(`${fnName}: 只接受 file: URL（收到 ${value.protocol}）。`);
        }

        let path;
        try {
            path = decodeURIComponent(value.pathname);
        } catch {
            throw new TypeError(`${fnName}: URL 的百分号编码不合法（${value.href}）。`);
        }

        // Windows 盘符：/C:/dir → C:/dir
        if (/^\/[A-Za-z]:/.test(path)) {
            path = path.slice(1);
        }

        // UNC：file://server/share → //server/share
        return value.host ? `//${value.host}${path}` : path;
    }

    throw new TypeError(
        `${fnName}: paths 的元素必须是字符串或 URL，收到 ${value === null ? 'null' : typeof value}。`);
}

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
export async function watch(paths, cb, options) {
    return watchInternal('watch', paths, cb, { delayMs: 2000, ...options });
}

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
export async function watchImmediate(paths, cb, options) {
    return watchInternal('watchImmediate', paths, cb, options);
}

/**
 * `watch` 与 `watchImmediate` 的共同实现。
 *
 * @param {string} fnName
 * @param {string | URL | Array<string | URL>} paths
 * @param {(event: WatchEvent) => void} cb
 * @param {Record<string, unknown> | null | undefined} options
 * @returns {Promise<UnwatchFn>}
 */
async function watchInternal(fnName, paths, cb, options) {
    if (typeof cb !== 'function') {
        throw new TypeError(`${fnName}: 第二个参数必须是回调函数。`);
    }

    const isDebounced = fnName === 'watch';
    const problem = checkOptions(fnName, options, isDebounced ? ['recursive', 'delayMs'] : ['recursive']);
    if (problem !== null) {
        throw problem;
    }

    const list = Array.isArray(paths) ? paths : [paths];
    if (list.length === 0) {
        throw new TypeError(`${fnName}: paths 至少要有一个路径。`);
    }

    /** @type {string[]} */
    const resolved = [];
    for (const entry of list) {
        // resolveAgainstBase 的返回类型是 unknown（它对"不是字符串就原样返回"），
        // 而这里入参已经保证是字符串
        resolved.push(/** @type {string} */ (await resolveAgainstBase(fnName, watchPathOf(fnName, entry), options)));
    }

    /** @type {Record<string, unknown>} */
    const args = { paths: resolved };

    const recursive = options?.recursive;
    if (recursive !== undefined) {
        args.recursive = Boolean(recursive);
    }

    if (isDebounced) {
        const delayMs = options?.delayMs;
        if (delayMs !== undefined) {
            if (typeof delayMs !== 'number' || !Number.isInteger(delayMs) || delayMs < 0) {
                throw new TypeError(`${fnName}: delayMs 必须是非负整数（毫秒），收到 ${String(delayMs)}。`);
            }

            args.delayMs = delayMs;
        }
    }

    const id = await invoke('fs.watch', args);
    const transport = getTransport();
    if (typeof transport.on !== 'function') {
        throw new Error(`${fnName}: 当前传输层没有 on()，无法接收监视事件。`);
    }

    // 与 Channel 同一条通路：事件名里带编号，页面侧天然按监视器分流
    const off = transport.on(`oriel://fs-watch/${id}`, (payload) => {
        try {
            cb(/** @type {WatchEvent} */ (payload));
        } catch (error) {
            // 回调抛错不该打断后续事件的投递（与桥接脚本对 oriel.on 的处理一致）
            console.error(`${fnName} 的回调抛出`, error);
        }
    });

    let stopped = false;

    return () => {
        if (stopped) {
            return;
        }

        stopped = true;
        off();

        // 停止命令是异步的，而 UnwatchFn 按 Tauri 的形状是同步的（返回 void）——
        // 失败只进控制台：那一刻调用方已经在收尾，抛错也没有人接
        void invoke('fs.unwatch', { id }).catch((error) => {
            console.error(`${fnName}: 停止监视失败`, error);
        });
    };
}
