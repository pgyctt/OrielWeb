"use strict";

// 这一页是"怎么用本库写页面"的示范，所以它吃**前端包** `@orielweb/api`，
// 而不是注入的 `window.oriel`（那个是传输层，等价于 Tauri 的 `__TAURI_INTERNALS__`，不是公开 API）。
//
// 这里用相对路径 import 是因为 demo **没有打包器**——浏览器不认 node_modules 解析。
// 真实项目有两条路：
//   * 有打包器（Vite / esbuild / …）：`import { invoke } from '@orielweb/api/core'`，裸标识符由它解析；
//   * 没有打包器：配一个 import map 把 `@orielweb/api/` 指到包源码的路径（或直接照抄下面的相对路径）。
// 两条路的取舍见 packages/api/README.md。
//
// 包源码是**直接内嵌**进来的（见 OrielDemo.csproj 的 vendor 那一段）：不复制、不提交副本，
// 所以这里 import 的就是包的源码本身，不存在"副本过期"的漂移。
import { invoke, isTauri } from "./vendor/orielweb-api/core.js";
import { listen } from "./vendor/orielweb-api/event.js";

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

document.getElementById("btn-min").addEventListener("click", () => invoke("win.minimize"));
maxBtn.addEventListener("click", async () => setMaximizedIcon(await invoke("win.toggleMaximize")));
document.getElementById("btn-close").addEventListener("click", () => invoke("win.close"));

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

// **迁移要点：payload 的位置变了。**
// 包的 listen 把宿主事件包成 `{ event, id, payload }`（Tauri 形状），所以要的是 `e.payload`；
// 裸通道 `window.oriel.on` 给的是裸值。这是"改 import 之外"唯一必须动的形状差异。
//
// `maximized` 是 **OrielWeb 专有**的事件名（Tauri 没有对应物——它靠 onResized 自己判断）。
// 保留它是因为它比"每次 resize 都问一次 isMaximized"省得多。
listen("maximized", (e) => setMaximizedIcon(e.payload));

// 宿主告诉本窗口"你是第几个窗口"（多窗口验证时靠它区分）。走事件而不是 URL 参数：
// Linux/macOS 上内嵌资源走 file://，宿主改写 URL 时 query 不保留，只有事件这条路三平台都通。
// data-base 记下标题原文，重复收到同一个标记也不会越接越长。
const titleEl = document.querySelector(".app-title");
listen("demo.windowLabel", (e) => {
    const value = e.payload;
    if (titleEl && value && value.text) {
        titleEl.dataset.base ??= titleEl.textContent;
        titleEl.textContent = `${titleEl.dataset.base} — ${value.text}`;
    }
});

let onTop = false;
document.getElementById("btn-ontop").addEventListener("click", async () => {
    onTop = await invoke("win.toggleOnTop");
    document.getElementById("btn-ontop").classList.toggle("active", onTop);
});

let fullscreen = false;
document.getElementById("btn-fullscreen").addEventListener("click", async () => {
    fullscreen = await invoke("win.toggleFullscreen");
    document.getElementById("btn-fullscreen").classList.toggle("active", fullscreen);
});

document.getElementById("btn-dialog").addEventListener("click", async () => {
    const file = await invoke("win.pickFile");
    if (file) {
        await invoke("todo.add", { text: "文件：" + file.split("\\").pop() });
        await refresh();
    }
});

// ---- Todo ----

let items = [];

async function refresh() {
    items = await invoke("todo.list");
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
            await invoke("todo.toggle", { id: item.id });
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
            await invoke("todo.remove", { id: item.id });
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
    await invoke("todo.add", { text });
    inputEl.value = "";
    await refresh();
});

// 主题：宿主在系统主题变化时、以及每次导航完成后都会推 theme.changed（payload 是 "light" / "dark"）。
// 这里把它落到 <html data-theme>（页面按需换配色）。
//
// 这里用底层 listen + 宿主事件名，是因为本页通篇都是这个风格（见上面的 maximized / demo.windowLabel）。
// **迁移来的 Tauri 代码可以走 Tauri 那个入口**：`getCurrentWindow().onThemeChanged(handler)`——
// 它内部就是映射到这条 theme.changed 上的（见 packages/api/src/event.js 的 HOST_EVENT_MAP）。
//
// 早先这里还会 postMessage 回显一条给宿主做自检；迁移时**去掉了**——宿主改为直接读这个
// data-theme 属性（见 ThemeSelfTest）。两处好处：示例页不再夹带测试钩子；
// 而且自检验的是"页面真的应用了主题"这个可见效果，比验一条旁路回显更接近用户看到的东西。
listen("theme.changed", (e) => {
    document.documentElement.dataset.theme = e.payload;
});

(async () => {
    // 迁移自 `await window.oriel.ready`：包没有 ready（Tauri 也没有）——
    // 桥接脚本在 document 创建时就同步装好了，页面脚本运行时它已经就绪。
    // 真正该问的是"我在不在宿主里"，那是 isTauri()。
    if (!isTauri()) {
        badgeEl.textContent = "IPC 不可用：不在 OrielWeb 宿主里";
        badgeEl.classList.add("error");
        return;
    }

    try {
        await refresh();
        badgeEl.textContent = "IPC 已连接";
        badgeEl.classList.add("ok");
    } catch (error) {
        badgeEl.textContent = "IPC 失败：" + error.message;
        badgeEl.classList.add("error");
    }
})();
