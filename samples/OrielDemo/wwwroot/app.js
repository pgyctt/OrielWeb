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

// Windows 的拖动必须"先动起来才发起"。
// win.drag 走的是程序发起的 WM_NCLBUTTONDOWN + HTCAPTION，它会进入原生模态循环并阻塞消息处理：
//   * 若在 mousedown 时立刻发起，第二次点击会被该循环吞掉，浏览器永远得不到 dblclick，
//     标题栏双击最大化随之失效；
//   * 改为等指针移动超过阈值再发起，则"原地双击"根本不会进入拖动，双击语义得以保留，
//     而真正的拖动只是晚了几像素才接管——模态循环以当前光标为基点，窗口不会跳。
const DRAG_THRESHOLD_PX = 3;
let armedDrag = null;

dragRegion.addEventListener("mousedown", async (e) => {
    if (e.button !== 0) return;

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
    maxBtn.textContent = maximized ? "\u2750" : "\u25A1"; // ❐ 还原 / □ 最大化
    maxBtn.title = maximized ? "还原" : "最大化";
}

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
