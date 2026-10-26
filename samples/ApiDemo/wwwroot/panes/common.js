// 跨面板共用的小工具：沙箱目录、随产物分发的图标路径、内联 PNG。
//
// 所有 fs 操作都落在**本应用的数据目录**（$APPDATA/sandbox）——能力配置里的
// scope 就是 $APPDATA/**，这个文件不提供任何绕过它的入口。

import { appDataDir, join } from "../vendor/orielweb-api/path.js";
import { mkdir, writeFile } from "../vendor/orielweb-api/fs.js";

/** 16x16 RGBA 圆角方块（95 字节）。托盘/图像/剪贴板演示都用它，免得依赖外部资源。 */
export const TINY_PNG_BASE64 =
    "iVBORw0KGgoAAAANSUhEUgAAABAAAAAQCAYAAAAf8/9hAAAAJklEQVR42mNgGFRAK3bDf2IwRZpxGjL4DEAHI9GAIRiNFCflAQMA3JdR+O3jmEoAAAAASUVORK5CYII=";

/** 内联 PNG 的字节形式（PNG，16x16，带 alpha）。 */
export function tinyPng() {
    const binary = atob(TINY_PNG_BASE64);
    const bytes = new Uint8Array(binary.length);
    for (let i = 0; i < binary.length; i += 1) {
        bytes[i] = binary.charCodeAt(i);
    }
    return bytes;
}

/** 16x16 的裸 RGBA（棋盘格），给 `Image.new` 用。 */
export function checkerRgba() {
    const rgba = new Uint8Array(16 * 16 * 4);
    for (let y = 0; y < 16; y += 1) {
        for (let x = 0; x < 16; x += 1) {
            const offset = (y * 16 + x) * 4;
            const light = ((x >> 2) + (y >> 2)) % 2 === 0;
            rgba[offset] = light ? 0x2a : 0xe8;
            rgba[offset + 1] = light ? 0x5d : 0xef;
            rgba[offset + 2] = light ? 0xb0 : 0xfb;
            rgba[offset + 3] = 0xff;
        }
    }
    return rgba;
}

let sandboxPromise = null;

/** 沙箱目录：`$APPDATA/sandbox`（首次调用时创建）。 */
export function sandbox() {
    sandboxPromise ??= (async () => {
        const dir = await join(await appDataDir(), "sandbox");
        await mkdir(dir, { recursive: true });
        return dir;
    })();
    return sandboxPromise;
}

let pngPathPromise = null;

/**
 * 沙箱里那份 16×16 PNG 的路径；首次调用时写出来。
 *
 * **不依赖 executableDir**：那条路径在 Windows 上按设计返回 `not_supported`
 * （与 Tauri 一致），而 `Image.fromPath` / 托盘图标 / 系统默认程序都需要一个真实路径。
 * 写进 `$APPDATA/sandbox` 既在 fs scope 内，也不受启动目录影响。
 */
export function demoPngPath() {
    pngPathPromise ??= (async () => {
        const path = await join(await sandbox(), "demo-icon.png");
        await writeFile(path, tinyPng());
        return path;
    })();
    return pngPathPromise;
}
