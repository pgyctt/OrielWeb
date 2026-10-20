// `@orielweb/api/image`：对标 `@tauri-apps/api/image`。
//
// **本库的 `Image` 与 Tauri 的有一处本质区别，写在最前面**：
//
//   Tauri 的 `Image` 是**资源表 handle**（继承 `Resource`，持有 Rust 侧的一个 `rid`），
//   字节留在 Rust 那边；它还有一个 `close()` 用来释放那条记录。
//
//   本库**没有资源表**——IPC 是一次性请求-回执，没有"把对象存到宿主那边再引用它"的
//   协议。所以这里的 `Image` 是一个**纯 JS 的字节容器**：它在页面侧持有
//   `Uint8Array`，传给宿主时仍然是完整字节（base64 走 `window.setIcon` 的 `bytes` 参数）。
//
// 这个差别带来三个可观察的后果，迁移时要心里有数：
//   1. **没有 `close()`**：没有宿主侧资源可释放，给一个空方法会让调用方误以为"不关就泄漏"；
//   2. **`rgba()` / `size()` 是同步可用的本地值**（Tauri 那边是异步的——它要往 Rust 问）。
//      签名仍保持 `Promise`，所以 `await image.rgba()` 这种写法照样能跑；
//   3. **跨 IPC 传 `Image` 时体积等于字节本身**（Tauri 只传一个 `rid`）。图标这种
//      几十 KB 的量级无所谓；几百 KB 的大图走这条路会明显变慢。
//
// 另一处细节：**`fromBytes` / `fromPath` 需要能读出尺寸**。尺寸就在 PNG 的 IHDR 头里，
// 这里直接解析那几个字节（PNG 与 ICO 都解析），因此**不需要宿主参与**——
// 这是"纯 JS 容器"能成立的关键，也是它和 Tauri 行为上最接近的地方。

import { invoke } from './core.js';
import { warnCompat } from './internal.js';
import {
    toImageBytes,
    bytesToBase64,
    base64ToBytes,
    sniffImageFormat,
    assertImageFormat,
} from './image-bytes.js';

/**
 * @typedef {{ width: number, height: number }} ImageSize
 */

/**
 * Tauri 的 `JsImage`：可以传给需要图像的地方（`Window.setIcon` 等）的各种形式。
 *
 * @typedef {string | Uint8Array | ArrayBuffer | number[] | Image} JsImage
 */

/**
 * 一张 RGBA 图像（行优先、自上而下）。
 *
 * @remarks
 * 与 Tauri 的 `Image` 不同，本库的这个类是**字节容器**而不是资源表 handle——
 * 理由见本文件开头那段。对外形状（`new` / `fromBytes` / `fromPath` / `rgba` / `size`）
 * 与 Tauri 保持一致，所以迁移代码不用改。
 *
 * @example
 * ```js
 * import { Image } from '@orielweb/api/image';
 * import { getCurrentWindow } from '@orielweb/api/window';
 *
 * const icon = await Image.fromPath('icons/icon.png');
 * await getCurrentWindow().setIcon(icon);
 * ```
 */
export class Image {
    /**
     * 由字节与尺寸构造。**不直接用**——用静态工厂（它们会解析尺寸并校验格式）。
     *
     * @param {Uint8Array} bytes 原始 PNG/ICO 字节。
     * @param {number} width
     * @param {number} height
     */
    constructor(bytes, width, height) {
        if (!(bytes instanceof Uint8Array) || bytes.length === 0) {
            throw new TypeError('Image: bytes 必须是非空的 Uint8Array');
        }

        if (!Number.isInteger(width) || width <= 0 || !Number.isInteger(height) || height <= 0) {
            throw new TypeError(`Image: 尺寸必须是正整数（收到 ${width}x${height}）`);
        }

        /** 原始编码字节（PNG/ICO）。 */
        this.bytes = bytes;

        /**
         * 标记：让 `window.setIcon` 能**不依赖 `instanceof`** 认出这是 Image（见那处的说明）。
         * @type {true}
         */
        this.isOrielImage = true;

        /**
         * 是否持有**编码字节**（PNG/ICO）。
         * @remarks
         * `Image.new` 用裸 RGBA 建图，那种图没有编码形式能给宿主，所以 `setIcon` 会拒绝它。
         * 这个标记用来区分两种来源（默认 false，静态工厂里改）。
         * @type {boolean}
         */
        this._hasEncodedBytes = false;

        /** @type {ImageSize} */
        this._size = { width, height };

        // 解码过的 RGBA 缓存。`rgba()` 可能被反复调用（比如逐帧改像素前先取一份），
        // 每次重启解码既慢又没必要。
        /** @type {Uint8Array | null} */
        this._rgba = null;
    }

    /**
     * 用**原始 RGBA 数据**建一张图（不解码任何格式，因此也不要求是 PNG/ICO）。
     *
     * @param {number[] | Uint8Array | ArrayBuffer} rgba 行优先的 RGBA，长度必须正好 `width * height * 4`。
     * @param {number} width
     * @param {number} height
     * @returns {Promise<Image>}
     * @remarks
     * 与 Tauri 一致返回 `Promise`。
     *
     * **这条返回的 Image 可以直接喂给 `setIcon`**：宿主侧的图标工厂本来就吃 RGBA
     * （`OrielImage` 是 RGBA 容器，构造时不做格式解码），所以 `setIcon` 会走
     * `rgba` + 尺寸那条参数，**不需要先把像素编码成 PNG**。
     */
    static async new(rgba, width, height) {
        const bytes = toImageBytes(rgba);
        if (bytes === null) {
            throw new TypeError('Image.new: rgba 必须是 number[]、Uint8Array 或 ArrayBuffer');
        }

        if (!Number.isInteger(width) || width <= 0 || !Number.isInteger(height) || height <= 0) {
            throw new TypeError(`Image.new: 尺寸必须是正整数（收到 ${width}x${height}）`);
        }

        const expected = width * height * 4;
        if (bytes.length !== expected) {
            throw new TypeError(
                `Image.new: 缓冲区应当是 ${width}x${height}x4 = ${expected} 字节，收到 ${bytes.length} 字节`);
        }

        const image = new Image(new Uint8Array(bytes), width, height);
        image._rgba = bytes;
        // 从 RGBA 建出来的图没有编码字节，_encoded 为 false 时 setIcon 会明确拒绝。
        image._hasEncodedBytes = false;
        return image;
    }

    /**
     * 从 PNG/ICO 字节建图（自动识别格式并解析尺寸）。
     *
     * @param {number[] | Uint8Array | ArrayBuffer} bytes
     * @returns {Promise<Image>}
     * @throws {TypeError} 不是 PNG/ICO，或头部损坏。
     */
    static async fromBytes(bytes) {
        const raw = toImageBytes(bytes);
        if (raw === null) {
            throw new TypeError('Image.fromBytes: bytes 必须是 number[]、Uint8Array 或 ArrayBuffer');
        }

        assertImageFormat(raw, 'Image.fromBytes');
        const { width, height } = readSize(raw);

        const image = new Image(new Uint8Array(raw), width, height);
        image._hasEncodedBytes = true;
        return image;
    }

    /**
     * 从**文件路径**建图（路径由宿主解析，因此是用户机器上的真实路径）。
     *
     * @param {string} path
     * @returns {Promise<Image>}
     * @remarks
     * 与 `setIcon(path)` 的区别：这条会把字节**读进页面**，之后可以反复用、
     * 也可以用 `rgba()` 看像素；而 `setIcon(path)` 只把路径交给宿主，页面拿不到字节。
     * 代价是多一次 IPC 往返与一份页面侧内存。
     */
    static async fromPath(path) {
        if (typeof path !== 'string' || path === '') {
            throw new TypeError('Image.fromPath: path 必须是非空字符串');
        }

        // 宿主返回 base64（见 window.readImageFile 命令）。
        const base64 = await invoke('window.readImageFile', { path });
        const bytes = base64ToBytes(base64);
        assertImageFormat(bytes, 'Image.fromPath');

        const { width, height } = readSize(bytes);
        const image = new Image(bytes, width, height);
        image._hasEncodedBytes = true;
        return image;
    }

    /**
     * 这张图的尺寸（像素）。
     *
     * @returns {Promise<ImageSize>}
     * @remarks 与 Tauri 同签名（那边是异步的，因为它要问宿主）。本库直接给本地值。
     */
    async size() {
        return { width: this._size.width, height: this._size.height };
    }

    /**
     * 宽（像素）——同步读取，供 `setIcon` 这类内部路径用。
     *
     * @returns {number}
     */
    get width() {
        return this._size.width;
    }

    /**
     * 高（像素）——同步读取，供 `setIcon` 这类内部路径用。
     *
     * @returns {number}
     */
    get height() {
        return this._size.height;
    }

    /**
     * 是否持有**编码字节**（PNG/ICO）。
     *
     * @returns {boolean}
     * @remarks
     * `Image.new`（裸 RGBA）建的图是 `false`：那种图没有编码形式，
     * 但**照样能设成图标**——宿主侧的图标工厂本来就吃 RGBA。
     */
    get hasEncodedBytes() {
        return this._hasEncodedBytes;
    }

    /**
     * 裸 RGBA 像素（`width * height * 4` 字节）。
     *
     * @returns {Uint8Array}
     * @throws {TypeError} 这张图只有编码字节、还没解码过。
     * @remarks
     * 与 `rgba()` 的区别：这个是**同步**的，且**不会**触发解码。它给内部路径用
     * （`setIcon` 要的是"现在就有没有像素"），而 `rgba()` 是公开 API、
     * 会按需请宿主解码一次。
     */
    get rgbaBytes() {
        if (this._rgba === null) {
            throw new TypeError(
                'Image.rgbaBytes: 这张图还没有解码过的像素（编码字节的图请用 await rgba()）');
        }

        return this._rgba;
    }

    /**
     * 这张图的 RGBA 像素（行优先，`width * height * 4` 字节）。
     *
     * @returns {Promise<Uint8Array>}
     * @remarks
     * 与 Tauri 同签名。本库在**第一次调用时**请宿主解码一次（`window.decodeImage`），
     * 之后缓存——因为 PNG 解码不是浏览器端该干的活，而宿主那边本来就有解码器
     * （`OrielImage.FromBytes`）。
     */
    async rgba() {
        if (this._rgba !== null) {
            return this._rgba;
        }

        if (this._hasEncodedBytes === false) {
            // Image.new 建的：_rgba 在构造时就填好了，走不到这里
            throw new TypeError('Image.rgba: 这张图没有编码字节');
        }

        const base64 = await invoke('window.decodeImage', {
            bytes: bytesToBase64(this.bytes),
        });

        this._rgba = base64ToBytes(base64);

        const expected = this._size.width * this._size.height * 4;
        if (this._rgba.length !== expected) {
            // 宿主解出来的长度与页面解析的尺寸对不上——说明**两边的尺寸解析有分歧**。
            // 宁可在这里报出来，也不要让调用方拿到一段错位的像素。
            throw new Error(
                `Image.rgba: 宿主解码得到 ${this._rgba.length} 字节，` +
                `与 ${this._size.width}x${this._size.height}x4 = ${expected} 不符`);
        }

        return this._rgba;
    }

    /**
     * 释放资源——**补壳**（B14）：本库的 `Image` 只是页面侧的字节容器，
     * 没有任何宿主侧资源可释放，调用不报错也不做任何事（迁移代码零删改）。
     *
     * debug 模式下打一条警告——**补壳必须可观测**，否则"close 了但什么都没发生"
     * 会让人以为资源管理有了效果。
     *
     * @returns {Promise<void>}
     */
    close() {
        warnCompat('Image.close 是补壳：本库的 Image 没有宿主资源可释放，该调用不生效。');
        return Promise.resolve();
    }
}

/**
 * 读 PNG / ICO 的尺寸（不需要解码像素）。
 *
 * @param {Uint8Array} bytes
 * @returns {ImageSize}
 * @throws {TypeError} 头部不足以读出尺寸。
 * @remarks
 * PNG：IHDR 紧跟在 8 字节签名与 4 字节长度 + 4 字节类型之后，宽高各占 4 字节大端。
 * ICO：ICONDIR（6 字节）后是第一个 ICONDIRENTRY，宽高在它的第 0、1 字节，
 * **0 表示 256**（这是 ICO 格式的一个历史坑——字段只有 1 字节）。
 */
function readSize(bytes) {
    const format = sniffImageFormat(bytes);

    if (format === 'png') {
        if (bytes.length < 24) {
            throw new TypeError('Image: PNG 头部不完整，读不出尺寸');
        }

        const width = readUInt32BigEndian(bytes, 16);
        const height = readUInt32BigEndian(bytes, 20);
        if (width === 0 || height === 0) {
            throw new TypeError(`Image: PNG 头部给出的尺寸非法（${width}x${height}）`);
        }

        return { width, height };
    }

    if (format === 'ico') {
        if (bytes.length < 6 + 16) {
            throw new TypeError('Image: ICO 头部不完整，读不出尺寸');
        }

        const count = (bytes[4] ?? 0) | ((bytes[5] ?? 0) << 8);
        if (count === 0) {
            throw new TypeError('Image: ICO 里没有任何图像条目');
        }

        // 0 在 ICO 里表示 256（字段只有 1 字节，装不下 256）
        const widthByte = bytes[6] ?? 0;
        const heightByte = bytes[7] ?? 0;
        const width = widthByte === 0 ? 256 : widthByte;
        const height = heightByte === 0 ? 256 : heightByte;
        return { width, height };
    }

    throw new TypeError('Image: 认不出图像格式（只支持 PNG 与 ICO）');
}

/**
 * 大端读 4 字节无符号整数。
 *
 * @param {Uint8Array} bytes
 * @param {number} offset
 * @returns {number}
 */
function readUInt32BigEndian(bytes, offset) {
    // `?? 0` 只是为了让类型收窄通过（`noUncheckedIndexedAccess` 把每次下标读都算成可能 undefined）。
    // 调用方已经先查过长度，所以这里不会有真的越界。
    const b0 = bytes[offset] ?? 0;
    const b1 = bytes[offset + 1] ?? 0;
    const b2 = bytes[offset + 2] ?? 0;
    const b3 = bytes[offset + 3] ?? 0;
    return ((b0 << 24) | (b1 << 16) | (b2 << 8) | b3) >>> 0;
}

/**
 * 把 `Image` 转成可以放进 `invoke` 参数的形式（对标 Tauri 的 `transformImage`）。
 *
 * @param {JsImage | null} image
 * @returns {any} 路径原样返回；字节编码成 base64；`Image` 取字节再编码；`null` 原样返回。
 * @remarks
 * Tauri 文档说这个函数的签名"不稳定"，本库保持同样的形状只是为了**迁移时能编译**。
 * 平时不需要它——`setIcon` 等入口自己会做转换。
 */
export function transformImage(image) {
    if (image === null) {
        return null;
    }

    if (typeof image === 'string') {
        return image;
    }

    if (isImageLike(image)) {
        return bytesToBase64(/** @type {Image} */ (image).bytes);
    }

    const bytes = toImageBytes(image);
    if (bytes !== null) {
        return bytesToBase64(bytes);
    }

    throw new TypeError(
        'transformImage: 需要路径、PNG/ICO 字节，或 @orielweb/api/image 的 Image 实例');
}

/**
 * 判断是不是本库的 `Image`（鸭子类型，理由见 `window.js` 里同名判断的说明）。
 *
 * @param {any} value
 * @returns {boolean}
 */
function isImageLike(value) {
    return value !== null
        && typeof value === 'object'
        && value.isOrielImage === true
        && value.bytes instanceof Uint8Array;
}
