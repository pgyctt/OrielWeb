/**
 * 把 Tauri `JsImage` 里的三种"字节"写法归一成 `Uint8Array`；不是字节形式就返回 `null`。
 *
 * @param {any} value
 * @returns {Uint8Array | null}
 * @remarks
 * `number[]` 的每一项都要校验成 0..255 的整数：`new Uint8Array([999])` 会**静默截断**
 * 成 231，于是页面拿到一个"没报错但图标不对"的结果。这类输入通常来自
 * `Array.from(fileReader.result)` 或手写的像素数组，写错一位就会被截断。
 */
export function toImageBytes(value: any): Uint8Array | null;
/**
 * `Uint8Array` → base64。
 *
 * @param {Uint8Array} bytes
 * @returns {string}
 * @remarks
 * 不用 `btoa(String.fromCharCode(...bytes))`：那样每个字节都变成一个实参，
 * 图标这种几十 KB 的输入会**爆栈**。分块是这里唯一需要小心的地方
 * （0x4000 留了余量，V8 的实参上限约 65535）。
 */
export function bytesToBase64(bytes: Uint8Array): string;
/**
 * base64 → `Uint8Array`。
 *
 * @param {string} base64
 * @returns {Uint8Array}
 * @throws {TypeError} 不是合法的 base64。
 */
export function base64ToBytes(base64: string): Uint8Array;
/**
 * 猜图像格式：只认 PNG 与 ICO（与 Tauri 的 `image-png` / `image-ico` 两个 feature 对应）。
 *
 * @param {Uint8Array} bytes
 * @returns {'png' | 'ico' | null} 认不出时返回 `null`。
 * @remarks
 * 只读文件头，不做完整解码——解码在宿主侧（`OrielImage.FromBytes`），
 * 页面这边提前判一次是为了让 `Image.fromBytes(乱数据)` 能**当场**给出可排查的错误，
 * 而不是等一次 IPC 往返之后再拿到一个语焉不详的失败。
 */
export function sniffImageFormat(bytes: Uint8Array): "png" | "ico" | null;
/**
 * 校验一段字节是不是可识别的图像；不是就抛。
 *
 * @param {Uint8Array} bytes
 * @param {string} context 出错信息里的前缀（例如 `setIcon`），便于定位调用方。
 * @returns {void}
 */
export function assertImageFormat(bytes: Uint8Array, context: string): void;
