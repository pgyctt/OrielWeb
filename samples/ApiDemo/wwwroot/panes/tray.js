// `@orielweb/api/tray`：系统托盘。
//
// 本库的 TrayIcon 不是资源句柄：id 就是句柄，方法都是命令。
// 图标必须给——macOS 的 NSStatusItem 没有 image 也没有 title 时宽度为 0，
// 状态项存在但用户看不见（Tauri 同样如此）。

import { Menu } from "../vendor/orielweb-api/menu.js";
import { TrayIcon } from "../vendor/orielweb-api/tray.js";
import { log } from "../ui.js";
import { demoPngPath } from "./common.js";

const ID = "api-demo-tray";
let tray = null;
let eventOff = null;

function requireTray() {
    if (tray === null) {
        throw new Error("先执行 TrayIcon.new(...)");
    }
    return tray;
}

export default {
    id: "tray",
    title: "Tray 托盘",
    module: "@orielweb/api/tray",
    blurb:
        "创建、取回、改图标/提示/标题/可见性、换菜单、移除。托盘事件在日志里；"
        + "Linux 上部分 setter 是空操作，Windows 上 setTitle 不支持——都是平台事实。",
    groups: [
        {
            title: "创建与取回",
            items: [
                {
                    name: "TrayIcon.new({ icon, tooltip, menu })",
                    run: async () => {
                        if (tray !== null) {
                            return `已存在（${tray.id}）`;
                        }
                        const menu = await Menu.new({
                            items: [
                                { id: "tray-hello", text: "托盘菜单项", action: (id) => log(`tray menu → ${id}`, "info") },
                                { item: "Separator" },
                                { id: "tray-close", text: "移除托盘", action: () => { void tray?.close(); tray = null; } },
                            ],
                        });
                        tray = await TrayIcon.new({
                            id: ID,
                            icon: await demoPngPath(),
                            tooltip: "Oriel API Demo",
                            menu,
                            action: (event) => log(`tray action → ${event.type}`, "info"),
                        });
                        return `已创建 ${tray.id}（看任务栏/菜单栏）`;
                    },
                },
                {
                    name: "TrayIcon.getById('api-demo-tray')",
                    run: async () => {
                        const found = await TrayIcon.getById(ID);
                        return found === null ? "null（还没创建）" : `找到 ${found.id}`;
                    },
                },
            ],
        },
        {
            title: "属性",
            items: [
                { name: "setTooltip('新提示')", run: () => requireTray().setTooltip("新提示 · " + new Date().toLocaleTimeString()) },
                {
                    name: "setTitle('API')",
                    badge: "平台差异",
                    note: "Windows 不支持；Linux 只有同时有图标才显示。",
                    run: () => requireTray().setTitle("API"),
                },
                {
                    name: "setVisible(false) → 2s → true",
                    badge: "自动复原",
                    run: async () => {
                        await requireTray().setVisible(false);
                        setTimeout(() => void tray?.setVisible(true), 2000);
                        return "已隐藏，2 秒后恢复";
                    },
                },
                { name: "setVisible(true)", run: () => requireTray().setVisible(true) },
                { name: "setIcon('app.png')", run: async () => requireTray().setIcon(await demoPngPath()) },
                {
                    name: "setIconAsTemplate(true)",
                    badge: "macOS 专属",
                    note: "其他平台接受参数但不生效。",
                    run: () => requireTray().setIconAsTemplate(true),
                },
                {
                    name: "setShowMenuOnLeftClick(false)",
                    badge: "平台差异",
                    note: "只有 Windows 接通；Linux/macOS 抛 not_supported。",
                    run: () => requireTray().setShowMenuOnLeftClick(false),
                },
                { name: "setShowMenuOnLeftClick(true)", badge: "平台差异", run: () => requireTray().setShowMenuOnLeftClick(true) },
                {
                    name: "setMenu(新菜单)",
                    run: async () => {
                        const menu = await Menu.new({
                            items: [{ id: "tray-new", text: "换过的菜单项", action: (id) => log(`tray menu → ${id}`, "info") }],
                        });
                        return requireTray().setMenu(menu);
                    },
                },
            ],
        },
        {
            title: "事件与移除",
            items: [
                {
                    name: "tray.onEvent(handler)",
                    run: async () => {
                        if (eventOff !== null) {
                            return "已订阅";
                        }
                        eventOff = requireTray().onEvent((event) => {
                            log(`tray event → ${JSON.stringify(event)}`, "info");
                        });
                        return "已订阅（点托盘图标看日志）";
                    },
                },
                {
                    name: "tray.close()",
                    run: async () => {
                        const current = requireTray();
                        const removed = await current.close();
                        tray = null;
                        eventOff = null;
                        return removed ? "已移除托盘" : "没有可移除的托盘";
                    },
                },
                {
                    name: "TrayIcon.removeById('api-demo-tray')",
                    run: async () => {
                        const removed = await TrayIcon.removeById(ID);
                        tray = null;
                        eventOff = null;
                        return removed ? "已移除" : "没有这个 id 的托盘";
                    },
                },
            ],
        },
    ],
};
