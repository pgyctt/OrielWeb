// `@orielweb/api/event`：listen / once / emit / emitTo，以及 TauriEvent 常量表。
//
// 事件名在宿主侧不校验，所以验证台用自定义的 `demo.tick`；
// `emit` / `emitTo` 需要能力放行（本应用 Allow("*")，因此可用）。

import { TauriEvent, emit, emitTo, listen, once } from "../vendor/orielweb-api/event.js";
import { log } from "../ui.js";

const active = new Map();

export default {
    id: "event",
    title: "Event 事件",
    module: "@orielweb/api/event",
    blurb:
        "listen 的 payload 被包成 { event, id, payload }；emit 走宿主广播，emitTo 按 label 定向。"
        + "TauriEvent 里标 ⛔ 的名字能 import，但永远不会来。",
    groups: [
        {
            title: "订阅",
            items: [
                {
                    name: "listen('demo.tick', handler)",
                    run: () => subscribe("listen", () => listen("demo.tick", (event) => {
                        log(`listen demo.tick → ${JSON.stringify(event.payload)}`, "info");
                    })),
                },
                {
                    name: "once('demo.tick.once', handler)",
                    run: () => subscribe("once", () => once("demo.tick.once", (event) => {
                        log(`once demo.tick.once → ${JSON.stringify(event.payload)}（已自动退订）`, "info");
                    })),
                },
                {
                    name: "退订 listen",
                    run: () => {
                        const off = active.get("listen");
                        if (!off) {
                            return "没有订阅";
                        }
                        off();
                        active.delete("listen");
                        return "已退订 demo.tick";
                    },
                },
            ],
        },
        {
            title: "发送",
            items: [
                {
                    name: "emit('demo.tick', payload)",
                    note: "广播给所有窗口；本窗口的 listen 也会收到。",
                    run: async (v) => {
                        await emit("demo.tick", { text: v.text || "tick", at: new Date().toISOString() });
                        return "已广播";
                    },
                    inputs: [{ key: "text", label: "text", value: "tick", width: 120 }],
                },
                {
                    name: "emit('demo.tick.once', payload)",
                    run: async () => {
                        await emit("demo.tick.once", { at: new Date().toISOString() });
                        return "已广播（once 只会收到一次）";
                    },
                },
                {
                    name: "emitTo('main', 'demo.tick', payload)",
                    note: "按 label 定向；目标不存在时静默丢弃（与 Tauri 一致）。",
                    run: async (v) => {
                        await emitTo(v.label || "main", "demo.tick", { text: "to", at: new Date().toISOString() });
                        return `已发送到「${v.label || "main"}」`;
                    },
                    inputs: [{ key: "label", label: "label", value: "main", width: 100 }],
                },
                {
                    name: "emitTo(自己的 label, ...)",
                    run: async () => {
                        await emitTo(globalThis.oriel?.label ?? "main", "demo.tick", { text: "self" });
                        return "已发送给自己";
                    },
                },
            ],
        },
        {
            title: "TauriEvent 常量",
            note: "只是名字表；标 ⛔ 的在桌面端不会触发。",
            render(container) {
                const list = document.createElement("dl");
                list.className = "kv";

                const notes = {
                    "tauri://destroyed": "⛔ 页面正在被拆掉，送不到",
                    "tauri://webview-created": "⛔ 本库没有 webview 层",
                    "tauri://window-created": "⛔ 桌面端不触发",
                    "tauri://suspended": "⛔ 桌面端不会来",
                    "tauri://resumed": "⛔ 桌面端不会来",
                };

                for (const [key, value] of Object.entries(TauriEvent)) {
                    const dt = document.createElement("dt");
                    dt.textContent = key;
                    const dd = document.createElement("dd");
                    dd.textContent = `${value} ${notes[value] ?? ""}`.trim();
                    list.append(dt, dd);
                }

                container.append(list);
            },
        },
    ],
};

async function subscribe(key, factory) {
    if (active.has(key)) {
        return `已订阅（${key}）`;
    }
    // listen / once 返回 thenable 函数：直接调用或 await 都成立。
    const off = await factory();
    active.set(key, off);
    return `已订阅 ${key}`;
}
