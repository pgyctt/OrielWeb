// `@orielweb/api/path`：路径拼装 + 24 个已知目录。
//
// 所有函数都走宿主（`path.*` 命令），所以它们是 Promise；只有 sep() / delimiter()
// 是同步的（取传输层里的常量，不发 IPC）。

import * as path from "../vendor/orielweb-api/path.js";
import { BaseDirectory } from "../vendor/orielweb-api/path.js";
import { h } from "../ui.js";

const DIRS = [
    ["appConfigDir", "应用配置目录"],
    ["appDataDir", "应用数据目录（$APPDATA）"],
    ["appLocalDataDir", "应用本地数据目录"],
    ["appCacheDir", "应用缓存目录"],
    ["appLogDir", "应用日志目录"],
    ["audioDir", "音频目录"],
    ["cacheDir", "缓存目录"],
    ["configDir", "配置目录"],
    ["dataDir", "数据目录"],
    ["localDataDir", "本地数据目录"],
    ["desktopDir", "桌面"],
    ["documentDir", "文档"],
    ["downloadDir", "下载"],
    ["executableDir", "可执行文件所在目录"],
    ["fontDir", "字体"],
    ["homeDir", "主目录"],
    ["pictureDir", "图片"],
    ["publicDir", "公共目录"],
    ["resourceDir", "资源目录"],
    ["runtimeDir", "运行期目录"],
    ["tempDir", "临时目录"],
    ["templateDir", "模板目录"],
    ["videoDir", "视频"],
];

export default {
    id: "path",
    title: "Path 路径",
    module: "@orielweb/api/path",
    blurb:
        "路径拼装与已知目录。目录解析不出时返回 null（而不是一个猜出来的路径）——"
        + "空值比错值安全。",
    groups: [
        {
            title: "同步常量",
            items: [
                { name: "sep()", run: () => path.sep() },
                { name: "delimiter()", run: () => path.delimiter() },
            ],
        },
        {
            title: "拼装与解析",
            items: [
                {
                    name: "join('a', 'b', '..', 'c')",
                    run: () => path.join("a", "b", "..", "c"),
                },
                {
                    name: "normalize('a/./b/../c')",
                    run: (v) => path.normalize(v.value || "a/./b/../c"),
                    inputs: [{ key: "value", label: "", value: "a/./b/../c", width: 180 }],
                },
                {
                    name: "dirname('C:/work/demo/app.js')",
                    run: () => path.dirname("C:/work/demo/app.js"),
                },
                {
                    name: "extname('report.tar.gz')",
                    run: () => path.extname("report.tar.gz"),
                },
                {
                    name: "basename('C:/work/report.pdf', '.pdf')",
                    run: () => path.basename("C:/work/report.pdf", ".pdf"),
                },
                {
                    name: "isAbsolute('C:/work')",
                    run: () => path.isAbsolute("C:/work"),
                },
                {
                    name: "resolve('a', 'b')",
                    note: "相对**进程当前目录**解析；应用自己不该依赖 cwd，这里只是验证形状。",
                    run: () => path.resolve("a", "b"),
                },
                {
                    name: "resolveResource('assets/index.js')",
                    note: "本应用没有资源目录，结果取决于平台实现；报错也是有效结论。",
                    inputs: [{ key: "name", label: "", value: "assets/index.js", width: 180 }],
                    run: (v) => path.resolveResource(v.name || "assets/index.js"),
                },
            ],
        },
        {
            title: "已知目录（23 个）",
            note: "解析不出来的项会给出 null 或错误，不会编一个路径。",
            items: DIRS.map(([fn, label]) => ({
                name: `${fn}()`,
                note: label,
                run: () => path[fn](),
            })),
        },
        {
            title: "BaseDirectory 常量",
            render(container) {
                const list = h("dl", { class: "kv" });
                for (const [key, value] of Object.entries(BaseDirectory)) {
                    if (typeof value !== "number") {
                        continue;
                    }
                    list.append(h("dt", {}, key), h("dd", {}, String(value)));
                }
                container.append(h("div", { class: "block" }, list));
            },
        },
    ],
};
