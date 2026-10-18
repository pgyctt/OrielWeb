// 图像字节的共享工具：`window.js`（setIcon 的三种字节写法）与 `image.js`（Image 类）都用它。
//
// **不进包的 `exports`**：这不是公开 API，页面不该 import 它。放在单独文件里是因为
// 两个模块都要用，而复制两份的话"越界字节要不要拦""分块大小取多少"这种判断
// 迟早会在两边分叉——那种分叉的症状是"同一个图标用 setIcon 传和用 Image 传效果不同"。
//
// 与 `internal.js` 分开：那个只管传输层（window.oriel），跟图像无关。

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
export function toImageBytes(value) {
    if (value instanceof Uint8Array) {
        return value;
    }

    if (value instanceof ArrayBuffer) {
        return new Uint8Array(value);
    }

    // TypedArray / DataView 一族（Tauri 的类型只写了 Uint8Array，但别的视图在运行时同样常见）
    if (ArrayBuffer.isView(value)) {
        return new Uint8Array(value.buffer, value.byteOffset, value.byteLength);
    }

    if (Array.isArray(value)) {
        for (let i = 0; i < value.length; i++) {
            const byte = value[i];
            if (typeof byte !== 'number' || !Number.isInteger(byte) || byte < 0 || byte > 255) {
                throw new TypeError(
                    `字节数组的第 ${i} 项不是 0..255 的整数（收到 ${String(byte)}）——` +
                    '它会被 Uint8Array 静默截断，所以这里直接拒。');
            }
        }

        return Uint8Array.from(value);
    }

    return null;
}

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
export function bytesToBase64(bytes) {
    const CHUNK = 0x4000;
    let binary = '';

    for (let i = 0; i < bytes.length; i += CHUNK) {
        binary += String.fromCharCode(...bytes.subarray(i, i + CHUNK));
    }

    return btoa(binary);
}

/**
 * base64 → `Uint8Array`。
 *
 * @param {string} base64
 * @returns {Uint8Array}
 * @throws {TypeError} 不是合法的 base64。
 */
export function base64ToBytes(base64) {
    let binary;
    try {
        binary = atob(base64);
    } catch {
        throw new TypeError('图像数据不是合法的 base64');
    }

    const bytes = new Uint8Array(binary.length);
    for (let i = 0; i < binary.length; i++) {
        bytes[i] = binary.charCodeAt(i);
    }

    return bytes;
}

/** PNG 的 8 字节签名。 */
const PNG_SIGNATURE = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

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
export function sniffImageFormat(bytes) {
    if (bytes.length >= 8 && PNG_SIGNATURE.every((byte, index) => bytes[index] === byte)) {
        return 'png';
    }

    // ICO/CUR：ICONDIR 的前两个字节是保留位 0，接着是类型（1 = icon，2 = cursor）。
    // 这比"看扩展名"可靠——字节形式本来就没有扩展名。
    if (bytes.length >= 6 && bytes[0] === 0 && bytes[1] === 0 && (bytes[2] === 1 || bytes[2] === 2)) {
        return 'ico';
    }

    return null;
}

/**
 * 校验一段字节是不是可识别的图像；不是就抛。
 *
 * @param {Uint8Array} bytes
 * @param {string} context 出错信息里的前缀（例如 `setIcon`），便于定位调用方。
 * @returns {void}
 */
export function assertImageFormat(bytes, context) {
    if (bytes.length === 0) {
        throw new TypeError(`${context}: 图像数据是空的`);
    }

    if (sniffImageFormat(bytes) === null) {
        throw new TypeError(
            `${context}: 认不出图像格式——本库只支持 PNG 与 ICO` +
            `（收到 ${bytes.length} 字节，开头是 ${hexPreview(bytes)}）。`);
    }
}

/**
 * 取开头几个字节的十六进制，用于错误信息。
 *
 * @param {Uint8Array} bytes
 * @returns {string}
 */
function hexPreview(bytes) {
    return Array.from(bytes.subarray(0, 8), (byte) => byte.toString(16).padStart(2, '0')).join(' ');
}
