// `@orielweb/api/app`：应用级元信息、全局显隐、主题、Dock、数据存储登记表。
//
// 注意 `hide()` 是**隐藏所有窗口**——点完页面自己也看不见。这里统一在 1.5 秒后
// 自动 `show()`，否则验证台会把自己藏没，只剩任务栏图标。

import {
    BundleType,
    defaultWindowIcon,
    exit,
    fetchDataStoreIdentifiers,
    getBundleType,
    getIdentifier,
    getName,
    getTauriVersion,
    getVersion,
    hide,
    onBackButtonPress,
    removeDataStore,
    setDockVisibility,
    setTheme as setAppTheme,
    show,
    supportsMultipleWindows,
} from "../vendor/orielweb-api/app.js";
import { getCurrentWindow } from "../vendor/orielweb-api/window.js";
import { h } from "../ui.js";

const win = getCurrentWindow();
let lastStoreIds = [];

export default {
    id: "app",
    title: "App 应用",
    module: "@orielweb/api/app",
    blurb:
        "应用级能力：元信息、全部窗口的显隐、主题、macOS Dock、数据存储登记表，以及退出。"
        + "带「平台差异」标记的项在部分平台上会返回 not_supported——那也是验证结果。",
    groups: [
        {
            title: "元信息",
            items: [
                { name: "getName()", run: () => getName() },
                { name: "getVersion()", run: () => getVersion() },
                { name: "getIdentifier()", run: () => getIdentifier() },
                { name: "getTauriVersion()", note: "对标 Tauri 的同名函数；这里给的是本库版本。", run: () => getTauriVersion() },
                { name: "supportsMultipleWindows()", run: () => supportsMultipleWindows() },
                {
                    name: "getBundleType()",
                    note: "只有从 Velopack 打包产物启动时才有值；开发期是 null。",
                    run: () => getBundleType(),
                },
                {
                    name: "defaultWindowIcon()",
                    note: "返回 Image 或 null；顺手把它设成窗口图标，证明拿到的是可用对象。",
                    run: async () => {
                        const icon = await defaultWindowIcon();
                        if (icon === null) {
                            return "null（没有默认图标）";
                        }
                        await win.setIcon(icon);
                        return `Image ${icon.width}×${icon.height}，已设为窗口图标`;
                    },
                },
            ],
        },
        {
            title: "全局显隐",
            note: "hide() 隐藏所有窗口；这里 1.5 秒后自动 show()，否则验证台会把自己藏没。",
            items: [
                {
                    name: "hide() → 1.5s → show()",
                    run: async () => {
                        await hide();
                        setTimeout(() => { void show(); }, 1500);
                        return "已隐藏全部窗口，1.5 秒后自动恢复";
                    },
                },
                {
                    name: "show()",
                    run: () => show(),
                },
            ],
        },
        {
            title: "主题",
            items: [
                { name: "setTheme('light')", run: () => setAppTheme("light") },
                { name: "setTheme('dark')", run: () => setAppTheme("dark") },
                {
                    name: "setTheme(null)（跟随系统）",
                    run: () => setAppTheme(null),
                },
                {
                    name: "getCurrentWindow().theme()",
                    note: "窗口自己的当前主题（与上面是两件事：一个是应用请求，一个是实际生效）。",
                    run: () => win.theme(),
                },
            ],
        },
        {
            title: "Dock 与数据存储",
            items: [
                {
                    name: "setDockVisibility(true)",
                    badge: "平台差异",
                    note: "macOS 专属；Windows/Linux 抛 not_supported。",
                    run: () => setDockVisibility(true),
                },
                {
                    name: "setDockVisibility(false)",
                    badge: "平台差异",
                    run: () => setDockVisibility(false),
                },
                {
                    name: "fetchDataStoreIdentifiers()",
                    note: "本应用创建过的数据存储标识符（落在 AppData/data-stores.json）。",
                    run: async () => {
                        lastStoreIds = await fetchDataStoreIdentifiers();
                        return lastStoreIds.length === 0
                            ? "[]（本应用还没创建过数据存储）"
                            : lastStoreIds;
                    },
                },
                {
                    name: "removeDataStore(第一条)",
                    badge: "破坏性",
                    confirm: "删除登记表里的第一条记录？这只删登记，不删磁盘上的数据。",
                    run: async () => {
                        if (lastStoreIds.length === 0) {
                            lastStoreIds = await fetchDataStoreIdentifiers();
                        }
                        if (lastStoreIds.length === 0) {
                            return "登记表为空，先跑一次 fetchDataStoreIdentifiers()";
                        }
                        const id = lastStoreIds[0];
                        await removeDataStore(id);
                        lastStoreIds = lastStoreIds.slice(1);
                        return `已移除 ${JSON.stringify(id)}`;
                    },
                },
            ],
        },
        {
            title: "移动端语义",
            items: [
                {
                    name: "onBackButtonPress(handler)",
                    badge: "桌面不触发",
                    note: "订阅是真的，但桌面端没有返回键，永远不会回调。保留它是为了让迁移代码不必删两行。",
                    run: async () => {
                        const unlisten = await onBackButtonPress(() => {
                            // 桌面端不会走到这里
                        });
                        return `已订阅（${typeof unlisten}），桌面端永不触发`;
                    },
                },
            ],
        },
        {
            title: "退出",
            items: [
                {
                    name: "exit(0)",
                    badge: "危险",
                    danger: true,
                    confirm: "退出的就是你自己。继续？",
                    note: "走的是宿主侧的完整退出流程（含托盘清理与看门狗），不是 window.close()。",
                    run: () => exit(0),
                },
            ],
        },
    ],
    // BundleType 常量表放在页面底部展示，避免混进动作列表
    render(root) {
        const list = h("dl", { class: "kv" });
        for (const [key, value] of Object.entries(BundleType)) {
            list.append(h("dt", {}, key), h("dd", {}, value));
        }

        root.append(
            h(
                "section",
                { class: "group" },
                h("header", {}, h("h2", {}, "BundleType 常量")),
                h("div", { class: "block" }, list),
            ),
        );
    },
};
