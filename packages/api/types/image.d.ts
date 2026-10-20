/**
 * 把 `Image` 转成可以放进 `invoke` 参数的形式（对标 Tauri 的 `transformImage`）。
 *
 * @param {JsImage | null} image
 * @returns {any} 路径原样返回；字节编码成 base64；`Image` 取字节再编码；`null` 原样返回。
 * @remarks
 * Tauri 文档说这个函数的签名"不稳定"，本库保持同样的形状只是为了**迁移时能编译**。
 * 平时不需要它——`setIcon` 等入口自己会做转换。
 */
export function transformImage(image: JsImage | null): any;
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
    static "new"(rgba: number[] | Uint8Array | ArrayBuffer, width: number, height: number): Promise<Image>;
    /**
     * 从 PNG/ICO 字节建图（自动识别格式并解析尺寸）。
     *
     * @param {number[] | Uint8Array | ArrayBuffer} bytes
     * @returns {Promise<Image>}
     * @throws {TypeError} 不是 PNG/ICO，或头部损坏。
     */
    static fromBytes(bytes: number[] | Uint8Array | ArrayBuffer): Promise<Image>;
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
    static fromPath(path: string): Promise<Image>;
    /**
     * 由字节与尺寸构造。**不直接用**——用静态工厂（它们会解析尺寸并校验格式）。
     *
     * @param {Uint8Array} bytes 原始 PNG/ICO 字节。
     * @param {number} width
     * @param {number} height
     */
    constructor(bytes: Uint8Array, width: number, height: number);
    /** 原始编码字节（PNG/ICO）。 */
    bytes: Uint8Array<ArrayBufferLike>;
    /**
     * 标记：让 `window.setIcon` 能**不依赖 `instanceof`** 认出这是 Image（见那处的说明）。
     * @type {true}
     */
    isOrielImage: true;
    /**
     * 是否持有**编码字节**（PNG/ICO）。
     * @remarks
     * `Image.new` 用裸 RGBA 建图，那种图没有编码形式能给宿主，所以 `setIcon` 会拒绝它。
     * 这个标记用来区分两种来源（默认 false，静态工厂里改）。
     * @type {boolean}
     */
    _hasEncodedBytes: boolean;
    /** @type {ImageSize} */
    _size: ImageSize;
    /** @type {Uint8Array | null} */
    _rgba: Uint8Array | null;
    /**
     * 这张图的尺寸（像素）。
     *
     * @returns {Promise<ImageSize>}
     * @remarks 与 Tauri 同签名（那边是异步的，因为它要问宿主）。本库直接给本地值。
     */
    size(): Promise<ImageSize>;
    /**
     * 宽（像素）——同步读取，供 `setIcon` 这类内部路径用。
     *
     * @returns {number}
     */
    get width(): number;
    /**
     * 高（像素）——同步读取，供 `setIcon` 这类内部路径用。
     *
     * @returns {number}
     */
    get height(): number;
    /**
     * 是否持有**编码字节**（PNG/ICO）。
     *
     * @returns {boolean}
     * @remarks
     * `Image.new`（裸 RGBA）建的图是 `false`：那种图没有编码形式，
     * 但**照样能设成图标**——宿主侧的图标工厂本来就吃 RGBA。
     */
    get hasEncodedBytes(): boolean;
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
    get rgbaBytes(): Uint8Array;
    /**
     * 这张图的 RGBA 像素（行优先，`width * height * 4` 字节）。
     *
     * @returns {Promise<Uint8Array>}
     * @remarks
     * 与 Tauri 同签名。本库在**第一次调用时**请宿主解码一次（`window.decodeImage`），
     * 之后缓存——因为 PNG 解码不是浏览器端该干的活，而宿主那边本来就有解码器
     * （`OrielImage.FromBytes`）。
     */
    rgba(): Promise<Uint8Array>;
    /**
     * 释放资源——**补壳**（B14）：本库的 `Image` 只是页面侧的字节容器，
     * 没有任何宿主侧资源可释放，调用不报错也不做任何事（迁移代码零删改）。
     *
     * debug 模式下打一条警告——**补壳必须可观测**，否则"close 了但什么都没发生"
     * 会让人以为资源管理有了效果。
     *
     * @returns {Promise<void>}
     */
    close(): Promise<void>;
}
export type ImageSize = {
    width: number;
    height: number;
};
/**
 * Tauri 的 `JsImage`：可以传给需要图像的地方（`Window.setIcon` 等）的各种形式。
 */
export type JsImage = string | Uint8Array | ArrayBuffer | number[] | Image;
