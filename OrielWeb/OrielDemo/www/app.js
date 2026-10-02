"use strict";

const listEl = document.getElementById("todo-list");
const countEl = document.getElementById("count-label");
const badgeEl = document.getElementById("ipc-badge");
const formEl = document.getElementById("add-form");
const inputEl = document.getElementById("todo-input");

// ---- 无边框窗口控制 ----
//
// 标题栏的**拖动与双击由库接管**：页面对标题栏元素标注 data-oriel-drag-region（见 index.html），
// 库注入的脚本就会处理"按下并移动 → 移动窗口"与"双击 → 最大化/还原"，并自动让开区域内的
// 按钮/输入框。页面既不写拖动代码，也不需要知道系统双击间隔或缩放——那些值只在库内部使用。
//
// 三平台的差异由库吸收（页面不必操心）：Windows 的原生模态拖动会吞掉后续点击，
// 所以它等指针动过阈值才发起；Wayland 下窗口移动必须交给合成器，而一旦交出去指针就被 grab，
// 所以库用同一个阈值决定何时真的交出去。详见 docs/API.md 第 2 节。
const maxBtn = document.getElementById("btn-max");

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

// 宿主告诉本窗口"你是第几个窗口"（多窗口验证时靠它区分）。走事件而不是 URL 参数：
// Linux/macOS 上内嵌资源走 file://，宿主改写 URL 时 query 不保留，只有事件这条路三平台都通。
// data-base 记下标题原文，重复收到同一个标记也不会越接越长。
const titleEl = document.querySelector(".app-title");
window.oriel.on("demo.windowLabel", (value) => {
    if (titleEl && value && value.text) {
        titleEl.dataset.base ??= titleEl.textContent;
        titleEl.textContent = `${titleEl.dataset.base} — ${value.text}`;
    }
});

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
