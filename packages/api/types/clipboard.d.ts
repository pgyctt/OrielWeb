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
export function readText(): Promise<string | null>;
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
export function writeText(text: string, opts?: {
    label?: string;
}): Promise<boolean>;
/**
 * 清空系统剪贴板。
 *
 * @returns {Promise<boolean>} 是否成功。
 */
export function clear(): Promise<boolean>;
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
export function readImage(): Promise<Image>;
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
export function writeImage(image: string | Image | Uint8Array | ArrayBuffer | number[]): Promise<boolean>;
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
export function writeHtml(html: string, altText?: string): Promise<boolean>;
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
export function writeSecret(text: string): Promise<boolean>;
import { Image } from './image.js';
