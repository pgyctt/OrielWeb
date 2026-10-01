"use strict";

const listEl = document.getElementById("todo-list");
const countEl = document.getElementById("count-label");
const badgeEl = document.getElementById("ipc-badge");
const formEl = document.getElementById("add-form");
const inputEl = document.getElementById("todo-input");

// ---- 无边框窗口控制 ----

const isWindows = window.oriel.platform === "windows";
const dragRegion = document.getElementById("drag-region");
const maxBtn = document.getElementById("btn-max");
let dragOrigin = null;

// 系统双击间隔（毫秒）。宿主在建窗时取好并注入（window.oriel.system），因此 mousedown 里
// **同步**可读——这正是它存在的理由：判断"这次按下是不是双击的第二下"必须在 mousedown 内完成，
// 而 oriel.invoke 是异步的，await 回来时那次按下已经过去了。浏览器只给 event.detail、
// 不给间隔本身，所以这个值只能由宿主给。
const DOUBLE_CLICK_MS = (window.oriel.system && window.oriel.system.doubleClickTimeMs) || 500;

// 双击的"位置容差"（屏坐标像素）：浏览器判定双击时同样有距离限制（Windows 默认 4 像素见方）。
// 取同量级的值；两边结论不一致时以浏览器给的 event.detail 为准。
const DOUBLE_CLICK_DISTANCE_PX = 4;

// 上一次标题栏按下的时间与位置，用于上面的同步判定。
let lastTitlebarDown = null;

// Windows 的拖动必须"先动起来才发起"。
// win.drag 走的是程序发起的 WM_NCLBUTTONDOWN + HTCAPTION，它会进入原生模态循环并阻塞消息处理：
//   * 若在 mousedown 时立刻发起，第二次点击会被该循环吞掉，浏览器永远得不到 dblclick，
//     标题栏双击最大化随之失效；
//   * 改为等指针移动超过阈值再发起，则"原地双击"根本不会进入拖动，双击语义得以保留，
//     而真正的拖动只是晚了几像素才接管——模态循环以当前光标为基点，窗口不会跳。
// 下面 isDoubleClickSecondPress 的同步判定是更靠前的一道：它按**系统**双击间隔直接认出第二下点击，
// 这个阈值因此退化成兜底（判定漏掉时仍然保证"按下不动"不会进模态循环）。
const DRAG_THRESHOLD_PX = 3;
let armedDrag = null;

/** 这一次标题栏按下是不是"双击的第二下"。必须在 mousedown 内同步调用。 */
function isDoubleClickSecondPress(e) {
    // 浏览器自己的判定优先
    if (e.detail >= 2) return true;

    // 浏览器没给 detail 时自己按系统间隔 + 位置容差判：两者都符合才算双击的第二下
    if (!lastTitlebarDown) return false;
    return (e.timeStamp - lastTitlebarDown.t) <= DOUBLE_CLICK_MS
        && Math.abs(e.screenX - lastTitlebarDown.x) <= DOUBLE_CLICK_DISTANCE_PX
        && Math.abs(e.screenY - lastTitlebarDown.y) <= DOUBLE_CLICK_DISTANCE_PX;
}

dragRegion.addEventListener("mousedown", async (e) => {
    if (e.button !== 0) return;

    // 双击的第二下**不进入拖动**，让浏览器把 dblclick 正常派发出去。
    // 注意这条判定在 mousedown 内同步完成——这就是 window.oriel.system 的用途（见上）。
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
        // macOS/Linux：宿主不阻塞消息循环，可以立即开始流式拖动
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
    window.oriel.invoke("win.drag"); // 原生模态拖动，阻塞到松开鼠标
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

dragRegion.addEventListener("dblclick", () => window.oriel.invoke("win.toggleMaximize"));

document.getElementById("btn-min").addEventListener("click", () => window.oriel.invoke("win.minimize"));
maxBtn.addEventListener("click", async () => setMaximizedIcon(await window.oriel.invoke("win.toggleMaximize")));
document.getElementById("btn-close").addEventListener("click", () => window.oriel.invoke("win.close"));

// 图标随最大化状态切换。用户经原生路径最大化（双击标题栏、拖到屏幕顶端、Win+↑）时
// 页面无法自行察觉，依赖宿主推送的 maximized 事件。
function setMaximizedIcon(maximized) {
    maxBtn.classList.toggle("is-maximized", !!maximized); // 由 CSS 切换 maximize/restore 字形
    const label = maximized ? "还原" : "最大化";
    maxBtn.title = label;
    maxBtn.setAttribute("aria-label", label);
}

// 标题栏字形按"物理像素"绘制：Windows 的标题栏图标线宽恒为 1 物理像素，而 1 CSS 像素在
// 125%/150% 缩放下会是 1.25/1.5 物理像素（线条变粗、发虚）。把设备像素比交给 CSS，
// 由它把 stroke-width 折算回 1 物理像素。
function updateDevicePixelRatio() {
    document.documentElement.style.setProperty("--dpr", String(window.devicePixelRatio || 1));
}

updateDevicePixelRatio();
window.addEventListener("resize", updateDevicePixelRatio);

window.oriel.on("maximized", setMaximizedIcon);

let onTop = false;
document.getElementById("btn-ontop").addEventListener("click", async () => {
    onTop = await window.oriel.invoke("win.toggleOnTop");
    document.getElementById("btn-ontop").classList.toggle("active", onTop);
});

let fullscreen = false;
document.getElementById("btn-fullscreen").addEventListener("click", async () => {
    fullscreen = await window.oriel.invoke("win.toggleFullscreen");
    document.getElementById("btn-fullscreen").classList.toggle("active", fullscreen);
});

document.getElementById("btn-dialog").addEventListener("click", async () => {
    const file = await window.oriel.invoke("win.pickFile");
    if (file) {
        await window.oriel.invoke("todo.add", { text: "文件：" + file.split("\\").pop() });
        await refresh();
    }
});

// ---- Todo ----

let items = [];

async function refresh() {
    items = await window.oriel.invoke("todo.list");
    render();
}

function render() {
    listEl.innerHTML = "";
    for (const item of items) {
        const li = document.createElement("li");
        li.className = "todo-item" + (item.done ? " done" : "");

        const checkbox = document.createElement("input");
        checkbox.type = "checkbox";
        checkbox.checked = item.done;
        checkbox.addEventListener("change", async () => {
            await window.oriel.invoke("todo.toggle", { id: item.id });
            await refresh();
        });

        const text = document.createElement("span");
        text.className = "text";
        text.textContent = item.text;
        text.title = `创建于 ${item.createdAt}`;

        const time = document.createElement("time");
        time.textContent = item.createdAt;

        const del = document.createElement("button");
        del.className = "delete";
        del.textContent = "×";
        del.addEventListener("click", async () => {
            await window.oriel.invoke("todo.remove", { id: item.id });
            await refresh();
        });

        li.append(checkbox, text, time, del);
        listEl.appendChild(li);
    }
    countEl.textContent = `共 ${items.length} 项，完成 ${items.filter(i => i.done).length} 项`;
}

formEl.addEventListener("submit", async (event) => {
    event.preventDefault();
    const text = inputEl.value.trim();
    if (!text) return;
    await window.oriel.invoke("todo.add", { text });
    inputEl.value = "";
    await refresh();
});

// 主题：宿主在系统主题变化时、以及每次导航完成后都会推 theme.changed（payload 是 "light" / "dark"）。
// 这里把它落到 <html data-theme>（页面按需换配色），并回显一条 postMessage——宿主的
// --selftest theme 正是靠这条回显确认"宿主 → 页面 → 宿主"整条通道是通的。
window.oriel.on("theme.changed", (theme) => {
    document.documentElement.dataset.theme = theme;
    window.oriel.postMessage("theme-echo", theme);
});

(async () => {
    try {
        await window.oriel.ready;
        await refresh();
        badgeEl.textContent = "IPC 已连接";
        badgeEl.classList.add("ok");
    } catch (error) {
        badgeEl.textContent = "IPC 失败：" + error.message;
        badgeEl.classList.add("error");
    }
})();
