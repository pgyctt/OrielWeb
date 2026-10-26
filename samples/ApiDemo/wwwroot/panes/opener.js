// `@orielweb/api/opener`：用系统默认程序打开 URL / 文件，或在文件管理器里定位。
//
// 三个都是"能拉起外部程序"的能力，必须显式授权。openUrl 还会再过一道
// OrielShellOptions 的 scheme 白名单（默认 http / https / mailto）。

import { openPath, openUrl, revealItemInDir } from "../vendor/orielweb-api/opener.js";
import { demoPngPath, sandbox } from "./common.js";

export default {
    id: "opener",
    title: "Opener 打开外部",
    module: "@orielweb/api/opener",
    blurb:
        "会真的拉起浏览器/文件管理器。最后一条用未在白名单里的 scheme，"
        + "预期被宿主拒绝——那是第二道闸。",
    groups: [
        {
            title: "URL",
            items: [
                { name: "openUrl('https://tauri.app')", run: () => openUrl("https://tauri.app") },
                { name: "openUrl('mailto:hello@example.com')", run: () => openUrl("mailto:hello@example.com") },
                {
                    name: "openUrl('ftp://example.com')（预期失败）",
                    badge: "预期失败",
                    note: "默认白名单只有 http/https/mailto。",
                    run: () => openUrl("ftp://example.com"),
                },
                {
                    name: "openUrl(url, openWith)",
                    badge: "预期失败",
                    note: "本库明确拒绝 openWith：那是任意程序执行，需要另一套授权模型。",
                    run: () => openUrl("https://tauri.app", "firefox"),
                },
            ],
        },
        {
            title: "文件",
            items: [
                {
                    name: "openPath(demo-icon.png)",
                    note: "用默认图片查看器打开沙箱里写出的图标。",
                    run: async () => openPath(await demoPngPath()),
                },
                {
                    name: "revealItemInDir(demo-icon.png)",
                    run: async () => revealItemInDir(await demoPngPath()),
                },
                {
                    name: "revealItemInDir(sandbox 目录)",
                    note: "目录也可以；返回 boolean（全部成功才为 true）。",
                    run: async () => revealItemInDir(await sandbox()),
                },
            ],
        },
    ],
};
