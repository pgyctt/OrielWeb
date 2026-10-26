// 覆盖总览：把每个模块的可点动作数汇总出来，并列出刻意没搬的 API。
//
// 这张表由 panes 自身计算（app.js 传进来），不是手写的数字——
// 新增一条动作，这里会自己变。

import { h } from "../ui.js";

const OMISSIONS = [
    {
        api: "mocks.*（mockIPC / mockEmit / mockWindows / clearMocks …）",
        why: "会替换传输层：开启后回执不再来自宿主，主验证台会立刻失真。它属于前端单测夹具，"
            + "已在 packages/api/tests 覆盖。",
    },
    {
        api: "core 的插件运行时（Resource / PluginListener / transformCallback / checkPermissions）",
        why: "本库没有\"宿主侧对象表 + 插件注册协议\"这个模型；core 模块里也没有这些导出，"
            + "不是漏搬。见 docs/TAURI2-GAP-2026-10-25.md §2.2。",
    },
    {
        api: "webview 模块（getCurrentWebview / Webview）",
        why: "本库窗口即 webview，没有 webview 层。需要 getCurrentWebview() 的代码换 getCurrentWindow()。",
    },
    {
        api: "fs 的 macOS security-scoped bookmark（start/stopAccessingSecurityScopedResource）",
        why: "需要 open 面板签发的 bookmark 与沙箱 entitlement；本库不做应用打包，给不出那个上下文。",
    },
    {
        api: "notification 的 Android 渠道/等级（channels / Importance / registerActionTypes）",
        why: "桌面没有对应概念。代码里调用会得到 not_supported，而不是静默忽略。",
    },
    {
        api: "menu.setIcon / IconMenuItem 的图标",
        why: "本库没有菜单图标的资源通道：setIcon 明确抛错，IconMenuItem 按普通文本项绘制。"
            + "构造与加入菜单的路径已覆盖。",
    },
    {
        api: "event.__clearEventSubscriptions / internal / image-bytes",
        why: "内部模块或测试钩子，不是公开 API 面。",
    },
];

export default {
    id: "coverage",
    title: "覆盖总览",
    module: "samples/ApiDemo",
    blurb:
        "这个应用对标 Tauri 的 examples/api：每个已实现的方法至少有一条可点的路径。"
        + "红色回执不一定是缺陷——标了「预期失败」的项本来就是用来展示错误形状的。",
    render(root, panes, counts) {
        const rows = panes.filter((pane) => pane.id !== "coverage");
        const total = rows.reduce((sum, pane) => sum + (counts.get(pane.id) ?? 0), 0);

        const table = h("table", { class: "coverage-table" });
        table.append(
            h("thead", {}, h("tr", {},
                h("th", {}, "模块"),
                h("th", {}, "包路径"),
                h("th", {}, "可运行动作"),
            )),
        );

        const body = h("tbody");
        for (const pane of rows) {
            body.append(h("tr", {},
                h("td", {}, pane.title),
                h("td", {}, h("code", {}, pane.module ?? "—")),
                h("td", { class: "num" }, String(counts.get(pane.id) ?? 0)),
            ));
        }
        table.append(body);

        root.append(
            h("section", { class: "group" },
                h("header", {}, h("h2", {}, "模块与动作数"), h("span", { class: "group-count" }, `合计 ${total} 项`)),
                h("div", { class: "block" }, table),
            ),
        );

        const list = h("ul", { class: "omissions" });
        for (const entry of OMISSIONS) {
            list.append(h("li", {}, h("code", {}, entry.api), " —— ", entry.why));
        }

        root.append(
            h("section", { class: "group" },
                h("header", {}, h("h2", {}, "刻意没搬的 API"), h("span", { class: "group-count" }, `${OMISSIONS.length} 条`)),
                h("div", { class: "block" }, list),
            ),
        );

        root.append(
            h("section", { class: "group" },
                h("header", {}, h("h2", {}, "怎么读回执")),
                h("div", { class: "block" },
                    h("ul", { class: "omissions" },
                        h("li", {}, h("code", {}, "绿色"), "：调用成功，后面是返回值与耗时。"),
                        h("li", {}, h("code", {}, "红色"), "：调用失败，带 message 与可选的 [code=…] / data=…；"
                            + "标了「预期失败」的项是刻意展示错误形状。"),
                        h("li", {}, h("code", {}, "平台差异"), "：换个平台会得到 not_supported——那也是有效结论，"
                            + "本库宁可报错也不静默忽略。"),
                        h("li", {}, "标「自动复原」的危险项会在几秒后自己恢复，否则验证台会把自己禁用掉。"),
                    ),
                ),
            ),
        );
    },
};
