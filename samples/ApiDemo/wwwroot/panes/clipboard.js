// `@orielweb/api/clipboard`：文本 / HTML / 图片 / 敏感文本。
//
// 读写都会**真的动系统剪贴板**——写会覆盖你当前复制的内容，读可能读到别人的东西。
// 这是验证台该做的事，但按钮文案写清楚。

import {
    clear,
    readImage,
    readText,
    writeHtml,
    writeImage,
    writeSecret,
    writeText,
} from "../vendor/orielweb-api/clipboard.js";
import { Image } from "../vendor/orielweb-api/image.js";
import { demoPngPath } from "./common.js";

export default {
    id: "clipboard",
    title: "Clipboard 剪贴板",
    module: "@orielweb/api/clipboard",
    blurb:
        "null（剪贴板里没有文本）与空字符串是两件事。图片只走 PNG；"
        + "readImage 在剪贴板里没有图片时会 reject，而不是给一张空图。",
    groups: [
        {
            title: "文本",
            items: [
                {
                    name: "writeText(text)",
                    inputs: [{ key: "text", label: "text", value: "来自 Oriel API Demo 的文本", width: 220 }],
                    run: async (v) => writeText(v.text ?? ""),
                },
                { name: "readText()", run: () => readText() },
                {
                    name: "writeText('') 后再 readText()",
                    note: "空字符串是「有文本、但为空」，与 null 不同。",
                    run: async () => {
                        await writeText("");
                        return readText();
                    },
                },
                { name: "clear()", badge: "会清空剪贴板", run: () => clear() },
            ],
        },
        {
            title: "富文本与敏感文本",
            items: [
                {
                    name: "writeHtml('<b>加粗</b>', '加粗')",
                    note: "Windows 写 CF_HTML（带字节偏移头）；读回来只有纯文本。",
                    run: () => writeHtml("<b>加粗</b> 与 <i>斜体</i>", "加粗 与 斜体"),
                },
                {
                    name: "writeSecret('correct horse …')",
                    note: "只是请求系统别记进历史/云同步，不认这个约定的管理器照样会存。",
                    run: () => writeSecret("correct horse battery staple"),
                },
            ],
        },
        {
            title: "图片",
            items: [
                {
                    name: "writeImage('app.png')",
                    note: "传路径：宿主读文件并写入剪贴板。",
                    run: async () => writeImage(await demoPngPath()),
                },
                {
                    name: "writeImage(Image.fromPath('app.png'))",
                    run: async () => {
                        const image = await Image.fromPath(await demoPngPath());
                        return writeImage(image);
                    },
                },
                {
                    name: "readImage()",
                    note: "剪贴板里没有图片时会 reject——那条错误也是有效回执。",
                    run: async () => {
                        const image = await readImage();
                        const info = await image.size();
                        return { width: info.width, height: info.height, bytes: image.bytes.length };
                    },
                },
                { name: "readText()（图片之后）", run: () => readText() },
            ],
        },
    ],
};
