// 对标 `@tauri-apps/plugin-clipboard-manager`。
//
// 提供 `readText` / `writeText` / `clear` / `readImage` / `writeImage` / `writeHtml` /
// `writeSecret`——参照实现的全部 7 个导出都做了。
//
// 与 Tauri 的差别（如实标注）：
//
//   * `writeText` 的 `label`（X11 的多个剪贴板）**明确报错**而不是忽略：
//     静默忽略会让调用方以为"我写到了指定剪贴板"，实际写的是默认那个。
//   * `readImage()` 在剪贴板里没有图片时 **reject**（与 Tauri 一致——那边 Rust 侧
//     返回 ContentNotAvailable 也是 reject），而不是给一个空 Image。
//   * `writeImage` 只认**路径**与**编码字节（PNG/ICO）**，不认裸 RGBA：
//     裸像素没有随行尺寸，猜一个只会得到一张错图。`Uint8Array` / `ArrayBuffer` /
//     `number[]` 都按"编码字节"解释（与 `Image.fromBytes` 同一口径）。
//   * `writeSecret` 只是**请求**：Windows 的剪贴板历史、macOS/Linux 的剪贴板管理器
//     都可以不理这个标记（Tauri 的文档也是这么说的）。
//
// 图片通道的形态：页面侧 `Image` 持有编码字节（见 image.js 开头），宿主读到的剪贴板图片
// 也统一编码成 PNG 再回来——两端都是"编码字节"，不需要在中间传裸像素。

import { invoke } from './core.js';
import { Image } from './image.js';
import { toImageBytes, bytesToBase64, base64ToBytes } from './image-bytes.js';

/**
 * 读系统剪贴板里的文本。
 *
 * @returns {Promise<string | null>} 没有文本内容时是 `null`。
 * @remarks
 * **`null` 与空串是两回事**：剪贴板里可能是图片、文件列表，或者根本是空的。
 * 而空串是一个合法的文本内容。调用方要能区分"没有文本"与"文本是空的"。
 *
 * ⚠️ **Linux 上这个调用可能阻塞**：GTK 的剪贴板是异步 API，读它要跑一个内嵌的 main loop
 * 等所有者应答，而对方不响应时会一直等（表现为命令等到前端超时）。
 * 这不是本库能绕的——GTK 没有"非阻塞取一次"的入口。
 */
export function readText() {
    return invoke('clipboard.readText');
}

/**
 * 把文本写进系统剪贴板。
 *
 * @param {string} text
 * @param {{ label?: string }} [opts] **不支持** `label`，传了会抛错。
 * @returns {Promise<boolean>} 是否写成功。
 * @remarks
 * 失败返回 `false` 而不是抛错：剪贴板是**全局独占**资源（Windows 上一次只有一个进程
 * 能打开它），别的进程正开着时写不进去是**正常状态**，不是调用方的错误。
 */
export function writeText(text, opts) {
    if (typeof text !== 'string') {
        throw new TypeError('writeText: text 必须是字符串');
    }

    return invoke('clipboard.writeText', opts?.label === undefined
        ? { text }
        : { text, label: opts.label });
}

/**
 * 清空系统剪贴板。
 *
 * @returns {Promise<boolean>} 是否成功。
 */
export function clear() {
    return invoke('clipboard.clear');
}

/**
 * 读剪贴板里的图片。
 *
 * @returns {Promise<Image>} 一张 `Image`（`await image.rgba()` 拿像素，`await image.size()` 拿尺寸）。
 * @throws {Error} **剪贴板里没有图片时 reject**（与 Tauri 一致，那边也是 reject 而不是给空值）。
 * @throws {Error} 图片格式解不出来时 reject。
 * @remarks
 * ```js
 * const image = await readImage();
 * const { width, height } = await image.size();
 * const rgba = await image.rgba();
 * ```
 *
 * 宿主只认**位图**：文本、文件列表、"从浏览器拖进来的 HTML"都不算。Windows 读 `CF_DIB`
 * （BI_RGB 的 24/32 位），macOS 优先 `public.png`、否则把 `public.tiff` 转 PNG，
 * Linux 走 `GtkClipboard` 的 image 目标。
 */
export async function readImage() {
    const base64 = await invoke('clipboard.readImage');
    if (typeof base64 !== 'string' || base64.length === 0) {
        throw new Error('clipboard.readImage: 剪贴板里没有图片。');
    }

    return Image.fromBytes(base64ToBytes(base64));
}

/**
 * 把图片写进剪贴板。
 *
 * @param {string | Image | Uint8Array | ArrayBuffer | number[]} image
 *   文件路径、`Image` 实例，或**编码字节**（PNG / ICO）。
 * @returns {Promise<boolean>} 是否写成功。
 * @throws {TypeError} 参数类型不对时（**在发 IPC 之前**）。
 * @remarks
 * **裸 RGBA 不受支持**：`Uint8Array` / `number[]` 一律按"PNG/ICO 编码字节"解释——
 * 裸像素没有随行尺寸，猜一个只会得到一张错图。要从像素造图请先 `Image.new(width, height, rgba)`，
 * 再把它传给这里（`Image` 自带尺寸）。
 *
 * 路径的相对解析规则与图标一致：**相对可执行文件所在目录**，不是当前工作目录。
 *
 * ```js
 * await writeImage('C:/shots/a.png');
 * await writeImage(await Image.fromPath('icons/app.png'));
 * await writeImage(pngBytes);            // 编码字节
 * ```
 */
export function writeImage(image) {
    if (image === null || image === undefined) {
        throw new TypeError('writeImage: 需要路径、Image 或 PNG/ICO 的编码字节');
    }

    if (typeof image === 'string') {
        return invoke('clipboard.writeImage', { image: { path: image } });
    }

    // 鸭子类型而不是 instanceof：页面可能来自不同的打包副本（与 window.setIcon 同一理由）
    if (typeof image === 'object'
        && /** @type {any} */ (image).isOrielImage === true
        && /** @type {any} */ (image).bytes instanceof Uint8Array) {
        return invoke('clipboard.writeImage', {
            image: { bytes: bytesToBase64(/** @type {Image} */ (image).bytes) },
        });
    }

    const bytes = toImageBytes(image);
    if (bytes === null) {
        throw new TypeError('writeImage: 需要路径、Image 或 PNG/ICO 的编码字节（不支持裸 RGBA）');
    }

    return invoke('clipboard.writeImage', { image: { bytes: bytesToBase64(bytes) } });
}

/**
 * 把 HTML 写进剪贴板（同时给一份纯文本回退）。
 *
 * @param {string} html HTML 片段（不需要 `<html>` 外壳）。
 * @param {string} [altText] 不支持 HTML 的粘贴目标拿到的纯文本；省略时用 `html` 原文。
 * @returns {Promise<boolean>} 是否写成功。
 * @throws {TypeError} `html` 不是字符串时。
 * @remarks
 * Windows 上这是 `CF_HTML`——一个带 **UTF-8 字节偏移头**的自描述格式。偏移写错的后果是
 * **静默的**：写入方一切正常，粘贴出来却是乱码或被截断。本库的头部由
 * `OrielClipboardHtml` 统一构造，并有单测把产物按偏移切回去逐段比对。
 *
 * 读回来只有 `readText()`（拿到纯文本）——与 Tauri 一样没有 `readHtml()`：
 * 富文本的读取在各平台都没有稳定接口。
 */
export function writeHtml(html, altText) {
    if (typeof html !== 'string') {
        throw new TypeError('writeHtml: html 必须是字符串');
    }

    return invoke('clipboard.writeHtml', altText === undefined ? { html } : { html, altText });
}

/**
 * 把一段**敏感文本**（密码、验证码、密钥）写进剪贴板。
 *
 * @param {string} text
 * @returns {Promise<boolean>} 是否写成功。
 * @throws {TypeError} `text` 不是字符串时。
 * @remarks
 * 文本照常可以粘贴，同时向系统声明"别记进历史、别同步到别的设备"：
 * Windows 用 `CanIncludeInClipboardHistory=0` / `CanUploadToCloudClipboard=0` /
 * `ExcludeClipboardContentFromMonitorProcessing` 三个标记；macOS 写
 * `org.nspasteboard.ConcealedType`；Linux 写 `x-kde-passwordManagerHint=secret`。
 *
 * **这只是请求，不是保证**：不认这些约定的剪贴板管理器照样会把内容存下来。
 */
export function writeSecret(text) {
    if (typeof text !== 'string') {
        throw new TypeError('writeSecret: text 必须是字符串');
    }

    return invoke('clipboard.writeSecret', { text });
}
