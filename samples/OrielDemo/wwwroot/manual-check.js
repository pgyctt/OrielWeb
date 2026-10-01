"use strict";

// 手动验证操作台。
//
// 这一页存在的理由：demo 在 Windows 上是 WinExe（**没有控制台**），自检的 stdout 看不见；
// 而托管侧的回调（托盘菜单项、上下文菜单项、拖放）恰恰都是"点了才知道"的东西。
// 所以把它们统统推回页面显示——看得见的回调才算验证过了。

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

// 宿主 → 页面：托管侧的事件都经这一条通道推过来
window.oriel.on("manual.log", (entry) => log(entry.text, "evt"));

document.getElementById("clear-log").addEventListener("click", () => {
    logEl.replaceChildren();
});

// 按钮 → 命令：结果（含返回值的字符串化）直接写进日志，
// 这样"命令通了但结果不对"与"命令根本没通"在页面上一眼可分
document.querySelectorAll("[data-cmd]").forEach((button) => {
    button.addEventListener("click", async () => {
        const label = button.textContent.trim();
        try {
            const result = await window.oriel.invoke(button.dataset.cmd);
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
        const state = await window.oriel.invoke("manual.state");
        const line = (label, value) => `<div>${label}：<b>${value}</b></div>`;
        document.getElementById("state").innerHTML =
            line("平台", window.oriel.platform) +
            line("托盘", state.trayCreated ? "已创建" : "未创建") +
            line("托盘当前可见", state.trayVisible ? "是" : "否（无头/无托盘宿主时正常）") +
            line("系统通知可用", state.notificationsSupported ? "是" : "否") +
            line("开机自启当前", state.autoStartEnabled ? "已启用" : "未启用");
    } catch (error) {
        document.getElementById("state").textContent = `读取环境信息失败：${error}`;
    }
})();

// ---- 无边框窗口：自绘标题栏 ----
// 与 app.js 同一套逻辑（两处必须同步改，否则操作台与示范页的拖动行为会不一致）：
// Windows 必须"先动起来才发起"原生拖动，否则第二次点击会被模态循环吞掉、双击最大化随之失效；
// 而"这是不是双击的第二下"由宿主注入的 window.oriel.system.doubleClickTimeMs 在 mousedown 内
// **同步**判定——详见 app.js 里的说明。

const isWindows = window.oriel.platform === "windows";
const dragRegion = document.getElementById("drag-region");
const maxBtn = document.getElementById("btn-max");
const DRAG_THRESHOLD_PX = 3;
const DOUBLE_CLICK_MS = (window.oriel.system && window.oriel.system.doubleClickTimeMs) || 500;
const DOUBLE_CLICK_DISTANCE_PX = 4;
let armedDrag = null;
let dragOrigin = null;
let lastTitlebarDown = null;

/** 这一次标题栏按下是不是"双击的第二下"。必须在 mousedown 内同步调用。 */
function isDoubleClickSecondPress(e) {
    if (e.detail >= 2) return true;
    if (!lastTitlebarDown) return false;
    return (e.timeStamp - lastTitlebarDown.t) <= DOUBLE_CLICK_MS
        && Math.abs(e.screenX - lastTitlebarDown.x) <= DOUBLE_CLICK_DISTANCE_PX
        && Math.abs(e.screenY - lastTitlebarDown.y) <= DOUBLE_CLICK_DISTANCE_PX;
}

dragRegion.addEventListener("mousedown", async (e) => {
    if (e.button !== 0) return;

    // 双击的第二下不进入拖动，让浏览器把 dblclick 正常派发出去（同步判定，见上）
    if (isDoubleClickSecondPress(e)) {
        disarmTitlebar();
        lastTitlebarDown = null;
        return;
    }
    lastTitlebarDown = { t: e.timeStamp, x: e.screenX, y: e.screenY };

    if (isWindows) {
        armedDrag = { x: e.screenX, y: e.screenY };
        document.addEventListener("mousemove", onTitlebarMove);
        document.addEventListener("mouseup", onTitlebarUp);
    } else {
        dragOrigin = { x: e.screenX, y: e.screenY };
        await window.oriel.invoke("win.dragStart", {
            px: e.screenX, py: e.screenY,
            wx: window.screenX, wy: window.screenY,
            ww: window.outerWidth, wh: window.outerHeight,
            sh: window.screen.height,
        });
        document.addEventListener("mousemove", onDragMove);
        document.addEventListener("mouseup", onDragEnd);
    }
});

function disarmTitlebar() {
    armedDrag = null;
    document.removeEventListener("mousemove", onTitlebarMove);
    document.removeEventListener("mouseup", onTitlebarUp);
}

function onTitlebarMove(e) {
    if (!armedDrag) return;
    if (Math.abs(e.screenX - armedDrag.x) <= DRAG_THRESHOLD_PX &&
        Math.abs(e.screenY - armedDrag.y) <= DRAG_THRESHOLD_PX) {
        return;
    }
    disarmTitlebar();
    window.oriel.invoke("win.drag");
}

function onTitlebarUp() {
    disarmTitlebar();
}

function onDragMove(e) {
    if (!dragOrigin) return;
    e.preventDefault();
    window.oriel.invoke("win.dragTo", { dx: e.screenX - dragOrigin.x, dy: e.screenY - dragOrigin.y });
}

function onDragEnd() {
    dragOrigin = null;
    document.removeEventListener("mousemove", onDragMove);
    document.removeEventListener("mouseup", onDragEnd);
    window.oriel.invoke("win.dragEnd");
}

function setMaximizedIcon(maximized) {
    maxBtn.classList.toggle("is-maximized", !!maximized);
}

dragRegion.addEventListener("dblclick", async () => {
    setMaximizedIcon(await window.oriel.invoke("win.toggleMaximize"));
});

document.getElementById("btn-min").addEventListener("click", () => window.oriel.invoke("win.minimize"));
maxBtn.addEventListener("click", async () => setMaximizedIcon(await window.oriel.invoke("win.toggleMaximize")));
document.getElementById("btn-close").addEventListener("click", () => window.oriel.invoke("win.close"));

// 用户经原生路径最大化（双击标题栏、Win+↑）时页面察觉不到，靠宿主推送
window.oriel.on("maximized", setMaximizedIcon);
