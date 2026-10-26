// `@orielweb/api/image`：页面侧的字节容器（不是资源句柄）。
//
// `Image.new` 吃裸 RGBA；`fromBytes`/`fromPath` 吃 PNG/ICO 编码字节。
// `rgba()` 第一次调用会请宿主解码一次（页面不做 PNG 解码）。

import { Image, transformImage } from "../vendor/orielweb-api/image.js";
import { getCurrentWindow } from "../vendor/orielweb-api/window.js";
import { checkerRgba, demoPngPath, tinyPng } from "./common.js";

const win = getCurrentWindow();

export default {
    id: "image",
    title: "Image 图像",
    module: "@orielweb/api/image",
    blurb:
        "RGBA 容器与编码字节两条路。Image.new 建的图没有编码字节，但照样能设为图标——"
        + "宿主侧的图标工厂本来就吃 RGBA。",
    groups: [
        {
            title: "构造",
            items: [
                {
                    name: "Image.new(rgba, 16, 16)",
                    note: "裸 RGBA 棋盘格；hasEncodedBytes 应为 false。",
                    run: async () => {
                        const image = await Image.new(checkerRgba(), 16, 16);
                        const info = await image.size();
                        return {
                            size: `${info.width}×${info.height}`,
                            hasEncodedBytes: image.hasEncodedBytes,
                            width: image.width,
                            height: image.height,
                        };
                    },
                },
                {
                    name: "Image.fromBytes(pngBytes)",
                    note: "持 PNG 编码字节；hasEncodedBytes 应为 true。",
                    run: async () => {
                        const image = await Image.fromBytes(tinyPng());
                        return {
                            size: await image.size(),
                            hasEncodedBytes: image.hasEncodedBytes,
                            bytes: image.bytes.length,
                        };
                    },
                },
                {
                    name: "Image.fromPath('app.png')",
                    note: "走 window.readImageFile 把字节读进页面，之后可反复用。",
                    run: async () => {
                        const image = await Image.fromPath(await demoPngPath());
                        const info = await image.size();
                        return { width: info.width, height: info.height, bytes: image.bytes.length };
                    },
                },
            ],
        },
        {
            title: "读取像素",
            items: [
                {
                    name: "image.size()",
                    run: async () => (await Image.fromPath(await demoPngPath())).size(),
                },
                {
                    name: "image.rgba()",
                    note: "第一次调用会请宿主解码，之后缓存。",
                    run: async () => {
                        const image = await Image.fromPath(await demoPngPath());
                        const rgba = await image.rgba();
                        return { length: rgba.length, head: rgba.slice(0, 8) };
                    },
                },
                {
                    name: "image.rgbaBytes（同步 getter）",
                    note: "只给已经解码过的图；没解码会抛 TypeError。",
                    run: async () => {
                        const image = await Image.fromPath(await demoPngPath());
                        try {
                            return image.rgbaBytes.length;
                        } catch (error) {
                            return `未解码：${error.message}`;
                        }
                    },
                },
            ],
        },
        {
            title: "转换与使用",
            items: [
                {
                    name: "transformImage('app.png')",
                    run: async () => {
                        const value = transformImage(await demoPngPath());
                        return typeof value === "string" ? value : `编码后 ${String(value).length} 字符`;
                    },
                },
                {
                    name: "transformImage(Image.fromBytes(...))",
                    run: async () => {
                        const value = transformImage(await Image.fromBytes(tinyPng()));
                        return `base64 ${String(value).length} 字符`;
                    },
                },
                {
                    name: "getCurrentWindow().setIcon(Image.new(...))",
                    note: "裸 RGBA 的 Image 也能设成窗口图标。",
                    run: async () => {
                        await win.setIcon(await Image.new(checkerRgba(), 16, 16));
                        return "已设为窗口图标";
                    },
                },
                {
                    name: "image.close()",
                    badge: "补壳",
                    note: "本库的 Image 没有宿主资源可释放；调用不报错、debug 下打一条警告。",
                    run: async () => {
                        const image = await Image.fromBytes(tinyPng());
                        await image.close();
                        return "已调用 close()（无副作用）";
                    },
                },
            ],
        },
    ],
};
