// `@orielweb/api/core`：invoke / isTauri / convertFileSrc / Channel。
//
// 这一页的失败项是**故意的**：未知命令、缺参数、类型不符、被 Deny 的命令、
// 超时——每种错误的 `code` 都不一样，验证台必须能一次看全。

import { Channel, convertFileSrc, invoke, isTauri } from "../vendor/orielweb-api/core.js";
import { writeFile } from "../vendor/orielweb-api/fs.js";
import { h, kv, log } from "../ui.js";
import { sandbox, tinyPng } from "./common.js";
import { join } from "../vendor/orielweb-api/path.js";

export default {
    id: "core",
    title: "Core · IPC",
    module: "@orielweb/api/core",
    blurb:
        "invoke 的四种错误形状、Channel 流式回传、传输层事实，以及 convertFileSrc。"
        + "红色回执不一定是缺陷——下面几条本来就是用来失败给你看的。",
    groups: [
        {
            title: "invoke：正常往返",
            note: "字符串、DTO、慢命令各一条。慢命令那条会等 300ms，用来对照下面的超时。",
            items: [
                {
                    name: "invoke('demo.ping', { message })",
                    run: (v) => invoke("demo.ping", { message: v.message || "hello" }),
                    inputs: [{ key: "message", label: "message", value: "hello", width: 180 }],
                },
                {
                    name: "invoke('demo.echo', { payload })",
                    note: "DTO 进、DTO 出；返回值里的 text 会被宿主转成大写。",
                    run: (v) => invoke("demo.echo", {
                        payload: {
                            text: v.text || "hello",
                            number: Number(v.number) || 0,
                            flag: Boolean(v.flag),
                        },
                    }),
                    inputs: [
                        { key: "text", label: "text", value: "hello", width: 120 },
                        { key: "number", label: "number", type: "number", value: "42", width: 70 },
                        { key: "flag", label: "flag", type: "checkbox", checked: true },
                    ],
                },
                {
                    name: "invoke('demo.slow', { milliseconds: 300 })",
                    run: () => invoke("demo.slow", { milliseconds: 300 }),
                },
                {
                    name: "invoke('demo.info')",
                    note: "宿主运行期事实：框架、OS、架构、是否允许动态代码、库版本。",
                    run: () => invoke("demo.info"),
                },
            ],
        },
        {
            title: "invoke：错误形状",
            note: "每条都应当失败。看回执里的 [code=…] 与 data=…。",
            items: [
                {
                    name: "demo.fail（应用抛异常）",
                    badge: "预期失败",
                    run: () => invoke("demo.fail", { kind: "demo_error" }),
                    inputs: [{ key: "kind", label: "kind", value: "demo_error", width: 120 }],
                },
                {
                    name: "demo.blocked（被 Deny）",
                    badge: "预期失败",
                    note: "能力配置里 Allow(\"*\").Deny(\"demo.blocked\")，所以 code 是 not_allowed。",
                    run: () => invoke("demo.blocked"),
                },
                {
                    name: "does.not.exist（未知命令）",
                    badge: "预期失败",
                    run: () => invoke("does.not.exist"),
                },
                {
                    name: "demo.ping（缺参数）",
                    badge: "预期失败",
                    run: () => invoke("demo.ping"),
                },
                {
                    name: "demo.ping（参数类型不符）",
                    badge: "预期失败",
                    run: () => invoke("demo.ping", { message: 42 }),
                },
                {
                    name: "demo.slow + timeout: 200",
                    badge: "预期失败",
                    note: "逐次超时。注意宿主侧的 1500ms 仍在跑，只是页面不再等它。",
                    run: () => invoke("demo.slow", { milliseconds: 1500 }, { timeout: 200 }),
                },
            ],
        },
        {
            title: "Channel：命令返回前多次推送",
            note: "命令结束时宿主自动关闭通道；页面收到结束事件后退订。",
            items: [
                {
                    node: () => {
                        const bar = h("div", { class: "progress" }, h("i"));
                        const out = h("div", { class: "result" });
                        const button = h("button", { class: "run" }, "开始流式调用");

                        button.addEventListener("click", async () => {
                            button.disabled = true;
                            out.className = "result pending";
                            out.textContent = "等待第一块…";
                            bar.firstElementChild.style.width = "0%";

                            const channel = new Channel((tick) => {
                                bar.firstElementChild.style.width = `${(tick.index / tick.total) * 100}%`;
                                out.className = "result ok";
                                out.textContent = `${tick.index}/${tick.total} ${tick.text}`;
                            });

                            try {
                                const total = await invoke("demo.stream", { count: 8, channel });
                                log(`Channel 完成：宿主返回 ${total}`, "ok");
                            } catch (error) {
                                out.className = "result err";
                                out.textContent = error.message;
                                log(`Channel 失败：${error.message}`, "err");
                            } finally {
                                button.disabled = false;
                            }
                        });

                        return h(
                            "div",
                            {},
                            h("h3", {}, "demo.stream(count, channel)"),
                            h("p", { class: "action-note" }, "8 块，每块间隔 120ms；进度条由页面更新。"),
                            bar,
                            h("div", { class: "toolbar" }, button),
                            out,
                        );
                    },
                },
            ],
        },
        {
            title: "传输层事实",
            items: [
                {
                    name: "isTauri()",
                    note: "迁移时用它替换 __TAURI_INTERNALS__ 探测；装了 mock 也返回 true。",
                    run: () => isTauri(),
                },
                {
                    node: () => {
                        const oriel = globalThis.oriel ?? {};
                        const box = h(
                            "div",
                            { class: "block" },
                            h("h3", {}, "window.oriel（底层传输层，非公开 API）"),
                        );
                        kv(box, {
                            platform: oriel.platform,
                            label: oriel.label,
                            version: oriel.version,
                            timeout: `${oriel.timeout} ms`,
                            "typeof oriel.invoke": typeof oriel.invoke,
                        });
                        return box;
                    },
                },
            ],
        },
        {
            title: "convertFileSrc：把文件显示出来",
            note: "只拼 URL、不查权限；真正的判定在宿主侧（asset.read 的 scope）。当前只有 Windows 真的返回内容。",
            items: [
                {
                    node: () => {
                        const image = h("img", {
                            alt: "convertFileSrc 预览",
                            style: { width: "48px", height: "48px", imageRendering: "pixelated" },
                        });
                        const out = h("div", { class: "result" });
                        const button = h("button", { class: "run" }, "写入 PNG 并显示");

                        button.addEventListener("click", async () => {
                            out.className = "result pending";
                            out.textContent = "写入 appData/sandbox …";
                            try {
                                const path = await join(await sandbox(), "asset-demo.png");
                                await writeFile(path, tinyPng());
                                const url = `${convertFileSrc(path)}?t=${Date.now()}`;
                                image.src = url;
                                out.className = "result ok";
                                out.textContent = url;
                                log(`convertFileSrc → ${url}`, "ok");
                            } catch (error) {
                                out.className = "result err";
                                out.textContent = error.message;
                                log(`convertFileSrc 失败：${error.message}`, "err");
                            }
                        });

                        return h(
                            "div",
                            {},
                            h("h3", {}, "convertFileSrc(path)"),
                            h("p", { class: "action-note" }, "写一个 16×16 PNG 到沙箱，再用 asset 协议显示它。"),
                            h("div", { class: "toolbar" }, button, image),
                            out,
                        );
                    },
                },
            ],
        },
    ],
};
