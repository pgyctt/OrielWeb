// `@orielweb/api/webviewWindow`：页面侧建窗与列举。
//
// 本库"窗口即 webview"，所以 WebviewWindow 与 Window 的行为相同，
// 差别只在构造：`new WebviewWindow(...)` 会真的让宿主建一个窗口。

import {
    WebviewWindow,
    getAllWebviewWindows,
    getCurrentWebviewWindow,
} from "../vendor/orielweb-api/webviewWindow.js";
import { getByLabel } from "../vendor/orielweb-api/window.js";

const current = getCurrentWebviewWindow();

export default {
    id: "webviewWindow",
    title: "WebviewWindow 多窗口",
    module: "@orielweb/api/webviewWindow",
    blurb:
        "建窗与列举。与 Tauri 唯一的调用形状差异：`new WebviewWindow()` 的结果在 `created` 上，"
        + "本库不发 `tauri://created`。",
    groups: [
        {
            title: "当前与全部",
            items: [
                { name: "getCurrentWebviewWindow().label", run: () => current.label },
                {
                    name: "getAllWebviewWindows()",
                    run: async () => (await getAllWebviewWindows()).map((w) => w.label),
                },
            ],
        },
        {
            title: "建窗",
            note: "新窗口加载同一份 index.html，因此也是完整的验证台；label 重复会被宿主拒绝。",
            items: [
                {
                    name: "new WebviewWindow(label, options)",
                    button: "创建",
                    inputs: [
                        { key: "label", label: "label", value: "second", width: 100 },
                        { key: "title", label: "title", value: "第二个窗口", width: 130 },
                        { key: "frameless", label: "frameless", type: "checkbox" },
                    ],
                    run: async (v) => {
                        const win = new WebviewWindow(v.label || "second", {
                            title: v.title || "第二个窗口",
                            width: 760,
                            height: 560,
                            frameless: Boolean(v.frameless),
                        });
                        // 与 Tauri 的形状差异：结果在 created 上（没有 tauri://created 事件）。
                        await win.created;
                        return `已创建「${win.label}」`;
                    },
                },
            ],
        },
        {
            title: "关闭",
            items: [
                {
                    name: "getByLabel(label).close()",
                    button: "关闭",
                    inputs: [{ key: "label", label: "label", value: "second", width: 100 }],
                    run: async (v) => {
                        const label = v.label || "second";
                        const target = await getByLabel(label);
                        if (target === null) {
                            return `没有 label 为「${label}」的窗口`;
                        }
                        if (label === current.label) {
                            return "拒绝关闭自己（会丢掉验证台）";
                        }
                        await target.close();
                        return `已关闭「${label}」`;
                    },
                },
            ],
        },
    ],
};
