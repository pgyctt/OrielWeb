// `@orielweb/api/menu`：页面侧菜单（纯数据，不发 IPC 直到 popup / setAs*）。
//
// 与 Tauri 的结构性差异：菜单不是资源句柄，`items()` 给的是本地实例；
// `popup()` 在 Windows/macOS 上会阻塞到用户选择（所以这里不 await）。

import {
    CheckMenuItem,
    IconMenuItem,
    Menu,
    MenuItem,
    NativeIcon,
    PredefinedMenuItem,
    Submenu,
} from "../vendor/orielweb-api/menu.js";
import { log } from "../ui.js";

let menu = null;
let checkItem = null;
let plainItem = null;
let submenu = null;

function onPick(id) {
    log(`menu action → ${id}`, "info");
}

async function ensureMenu() {
    if (menu !== null) {
        return menu;
    }

    checkItem = await CheckMenuItem.new({
        id: "demo-check",
        text: "勾选项",
        checked: true,
        action: onPick,
    });

    submenu = await Submenu.new({
        id: "demo-sub",
        text: "子菜单",
        items: [
            { id: "demo-sub-1", text: "子项 1", action: onPick },
            { id: "demo-sub-2", text: "子项 2", action: onPick },
        ],
    });

    menu = await Menu.new({
        items: [
            { id: "demo-item", text: "普通项", accelerator: "CmdOrCtrl+1", action: onPick },
            { item: "Separator" },
            checkItem,
            { item: "Copy" },
            { item: "Paste" },
            { item: "Separator" },
            submenu,
        ],
    });

    plainItem = (await menu.items()).find((entry) => entry.id === "demo-item") ?? null;
    return menu;
}

export default {
    id: "menu",
    title: "Menu 菜单",
    module: "@orielweb/api/menu",
    blurb:
        "弹出菜单与菜单栏。Windows 上 popup() 会阻塞到用户选择——所以按钮只负责弹出，"
        + "回执不会等你选完；可靠的信号是 action 回调（看日志）。",
    groups: [
        {
            title: "构造",
            items: [
                {
                    name: "Menu.new({ items })",
                    note: "普通项 / 分隔符 / 勾选项 / 预定义项 / 子菜单。",
                    run: async () => {
                        await ensureMenu();
                        return `已构造，${(await menu.items()).length} 项`;
                    },
                },
                {
                    name: "Menu.default()",
                    note: "平台标准菜单（Edit / Window / Help）。",
                    run: async () => {
                        const standard = await Menu.default();
                        return `已构造，${(await standard.items()).length} 个顶级项`;
                    },
                },
                {
                    name: "MenuItem.new + text()/setText()",
                    run: async () => {
                        const item = await MenuItem.new({ id: "tmp", text: "原名" });
                        const before = await item.text();
                        await item.setText("改过的名字");
                        return { before, after: await item.text() };
                    },
                },
                {
                    name: "MenuItem.isEnabled()/setEnabled()",
                    run: async () => {
                        const item = await MenuItem.new({ id: "tmp2", text: "可切换" });
                        const before = await item.isEnabled();
                        await item.setEnabled(false);
                        const after = await item.isEnabled();
                        await item.setEnabled(true);
                        return { before, after };
                    },
                },
                {
                    name: "MenuItem.setAccelerator('CmdOrCtrl+Shift+K')",
                    run: async () => {
                        const item = await MenuItem.new({ id: "tmp3", text: "带加速键" });
                        await item.setAccelerator("CmdOrCtrl+Shift+K");
                        return "已设置（本库没有对应的读接口，与 Tauri 一致）";
                    },
                },
                {
                    name: "CheckMenuItem.isChecked()/setChecked()",
                    run: async () => {
                        const item = await CheckMenuItem.new({ id: "tmp4", text: "勾选", checked: false });
                        const before = await item.isChecked();
                        await item.setChecked(true);
                        return { before, after: await item.isChecked() };
                    },
                },
                {
                    name: "IconMenuItem.new(...)",
                    badge: "图标不显示",
                    note: "本库没有菜单图标的资源通道，按普通文本项绘制。",
                    run: async () => {
                        const item = await IconMenuItem.new({ id: "tmp5", text: "带图标项" });
                        return `已构造（${item.constructor.name}）`;
                    },
                },
                {
                    name: "PredefinedMenuItem.new({ item: 'Copy' })",
                    run: async () => {
                        const item = await PredefinedMenuItem.new({ item: "Copy" });
                        return `已构造，text=${await item.text()}`;
                    },
                },
                {
                    name: "Submenu.new(...)",
                    run: async () => {
                        const sub = await Submenu.new({
                            text: "动态子菜单",
                            items: [{ id: "nested", text: "嵌套项", action: onPick }],
                        });
                        return `已构造，${(await sub.items()).length} 项`;
                    },
                },
            ],
        },
        {
            title: "容器操作（本地，不发 IPC）",
            items: [
                {
                    name: "menu.append(...)",
                    run: async () => {
                        const target = await ensureMenu();
                        await target.append(await MenuItem.new({ id: "appended", text: "追加项", action: onPick }));
                        return `现在 ${(await target.items()).length} 项`;
                    },
                },
                {
                    name: "menu.insert(item, 0)",
                    run: async () => {
                        const target = await ensureMenu();
                        await target.insert(await MenuItem.new({ id: "inserted", text: "插入项" }), 0);
                        return `现在 ${(await target.items()).length} 项`;
                    },
                },
                {
                    name: "menu.removeAt(0)",
                    run: async () => {
                        const target = await ensureMenu();
                        const removed = await target.removeAt(0);
                        return removed === null ? "位置 0 没有项" : `移除了 ${removed.id}`;
                    },
                },
                {
                    name: "menu.items()",
                    run: async () => (await (await ensureMenu()).items()).map((entry) => entry.id ?? "(无 id)"),
                },
                {
                    name: "menu.remove(item)",
                    run: async () => {
                        const target = await ensureMenu();
                        const items = await target.items();
                        const last = items[items.length - 1];
                        if (!last) {
                            return "菜单为空";
                        }
                        await target.remove(last);
                        return `移除了 ${last.id ?? "(无 id)"}，剩 ${(await target.items()).length} 项`;
                    },
                },
            ],
        },
        {
            title: "弹出",
            items: [
                {
                    name: "menu.popup({ x: 240, y: 240 })",
                    note: "Windows/macOS 阻塞到选择；这里不 await，看日志里的 action。",
                    run: async () => {
                        const target = await ensureMenu();
                        target.popup({ x: 240, y: 240 }).catch((error) => log(`popup 失败：${error.message}`, "err"));
                        return "已请求弹出（坐标 240,240）";
                    },
                },
                {
                    name: "menu.close()",
                    note: "本库扩展：关掉在屏菜单（Windows 上 popup 阻塞时唯一的代码出口）。",
                    run: async () => {
                        if (menu === null) {
                            return "还没有构造菜单";
                        }
                        await menu.close();
                        return "已请求关闭";
                    },
                },
            ],
        },
        {
            title: "菜单栏",
            items: [
                {
                    name: "menu.setAsWindowMenu()",
                    note: "Windows/Linux 占客户区高度；macOS 抛 not_supported。",
                    run: async () => {
                        await (await ensureMenu()).setAsWindowMenu();
                        return "已装到本窗口（看窗口顶部）";
                    },
                },
                {
                    name: "清空窗口菜单",
                    run: async () => {
                        const empty = await Menu.new({ items: [] });
                        await empty.setAsWindowMenu();
                        return "已移除菜单栏";
                    },
                },
                {
                    name: "menu.setAsAppMenu()",
                    badge: "应用级",
                    note: "macOS 的主菜单；Windows/Linux 装到所有没显式设过菜单栏的窗口。",
                    run: async () => {
                        await (await ensureMenu()).setAsAppMenu();
                        return "已尝试装成应用菜单";
                    },
                },
            ],
        },
        {
            title: "NativeIcon 常量",
            render(container) {
                const keys = Object.keys(NativeIcon);
                const list = document.createElement("dl");
                list.className = "kv";
                const dt = document.createElement("dt");
                dt.textContent = `共 ${keys.length} 个`;
                const dd = document.createElement("dd");
                dd.textContent = keys.slice(0, 8).join(", ") + " …";
                list.append(dt, dd);
                container.append(list);
            },
        },
    ],
};
