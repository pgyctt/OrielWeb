"use strict";

const listEl = document.getElementById("todo-list");
const countEl = document.getElementById("count-label");
const badgeEl = document.getElementById("ipc-badge");
const formEl = document.getElementById("add-form");
const inputEl = document.getElementById("todo-input");

// ---- 无边框窗口控制 ----

const isWindows = window.oriel.platform === "windows";
const dragRegion = document.getElementById("drag-region");
let dragOrigin = null;

dragRegion.addEventListener("mousedown", async (e) => {
    if (e.button !== 0) return;
    if (isWindows) {
        // Windows：原生模态拖动（调用后阻塞到松开鼠标）
        await window.oriel.invoke("win.drag");
    } else {
        // macOS/Linux：流式拖动（指针起点 + 窗口几何，随后 dragTo 增量）
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
document.getElementById("btn-max").addEventListener("click", () => window.oriel.invoke("win.toggleMaximize"));
document.getElementById("btn-close").addEventListener("click", () => window.oriel.invoke("win.close"));

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
