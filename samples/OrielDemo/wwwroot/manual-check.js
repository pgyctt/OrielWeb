"use strict";

// 手动验证操作台。
//
// 这一页存在的理由：demo 在 Windows 上是 WinExe（**没有控制台**），自检的 stdout 看不见；
// 而托管侧的回调（托盘菜单项、上下文菜单项、拖放）恰恰都是"点了才知道"的东西。
// 所以把它们统统推回页面显示——看得见的回调才算验证过了。
//
// 与 app.js 一样吃前端包（相对路径 import 的理由见那边的文件头）。
import { invoke } from "./vendor/orielweb-api/core.js";
import { listen } from "./vendor/orielweb-api/event.js";

const logEl = document.getElementById("log");

function log(text, kind) {
    const li = document.createElement("li");
    if (kind) li.className = kind;

    const ts = document.createElement("span");
    ts.className = "ts";
    ts.textContent = new Date().toLocaleTimeString("zh-CN", { hour12: false });

    li.append(ts, document.createTextNode(text));
    logEl.append(li);
    logEl.scrollTop = logEl.scrollHeight;
}

// 宿主 → 页面：托管侧的事件都经这一条通道推过来。
// payload 在 e.payload 里（包把宿主事件包成 { event, id, payload }）。
listen("manual.log", (e) => log(e.payload.text, "evt"));

document.getElementById("clear-log").addEventListener("click", () => {
    logEl.replaceChildren();
});

// 按钮 → 命令：结果（含返回值的字符串化）直接写进日志，
// 这样"命令通了但结果不对"与"命令根本没通"在页面上一眼可分
document.querySelectorAll("[data-cmd]").forEach((button) => {
    button.addEventListener("click", async () => {
        const label = button.textContent.trim();
        try {
            const result = await invoke(button.dataset.cmd);
            const text = result === undefined || result === null
                ? "（无返回值）"
                : typeof result === "object" ? JSON.stringify(result) : String(result);
            log(`${label} → ${text}`, "ok");
        } catch (error) {
            log(`${label} → 失败：${error}`, "err");
        }
    });
});

// ---- 环境信息 ----

(async () => {
    try {
        const state = await invoke("manual.state");
        const line = (label, value) => `<div>${label}：<b>${value}</b></div>`;

        // `platform` 还没有进包：它属于 os 模块（计划中的 M3），Tauri 那边在
        // @tauri-apps/plugin-os 里，不在 core。所以这里读裸传输层——那不是遗漏，
        // 是"包只提供 Tauri 形状的东西"这条原则的体现。
        const platform = /** @type {any} */ (window).oriel?.platform ?? "未知";

        document.getElementById("state").innerHTML =
            line("平台", platform) +
            line("托盘", state.trayCreated ? "已创建" : "未创建") +
            line("托盘当前可见", state.trayVisible ? "是" : "否（无头/无托盘宿主时正常）") +
            line("系统通知可用", state.notificationsSupported ? "是" : "否") +
            line("开机自启当前", state.autoStartEnabled ? "已启用" : "未启用");
    } catch (error) {
        document.getElementById("state").textContent = `读取环境信息失败：${error}`;
    }
})();

// ---- 无边框窗口：自绘标题栏 ----
//
// 拖动与双击由库接管（与 app.js 同一套机制，见 docs/API.md 第 2 节）：标题栏元素标注
// data-oriel-drag-region 之后，库处理"按下并移动 → 移动窗口"与"双击 → 最大化/还原"。
const maxBtn = document.getElementById("btn-max");

function setMaximizedIcon(maximized) {
    maxBtn.classList.toggle("is-maximized", !!maximized);
}

document.getElementById("btn-min").addEventListener("click", () => invoke("win.minimize"));
maxBtn.addEventListener("click", async () => setMaximizedIcon(await invoke("win.toggleMaximize")));
document.getElementById("btn-close").addEventListener("click", () => invoke("win.close"));

// 用户经原生路径最大化（双击标题栏、Win+↑）时页面察觉不到，靠宿主推送
listen("maximized", (e) => setMaximizedIcon(e.payload));
