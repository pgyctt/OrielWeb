// 移植自 tauri/examples/drag 的页面部分。
//
// 与原文最大的差别：**这个文件里没有一行拖动代码**。
// Tauri 的 drag.js 要监听 pointerdown、判断 clickable 标签与 role、再调
// plugin:window|start_dragging；本库把这些做进了桥接脚本，页面只标属性。
//
// 所以这里只剩窗口按钮——它们本身也证明了"拖动区域里的按钮不会被拖动吞掉"。

import { invoke } from "./vendor/orielweb-api/core.js";

const toastEl = document.getElementById("toast");
let toastTimer = 0;

function toast(text) {
    toastEl.textContent = text;
    toastEl.hidden = false;
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => { toastEl.hidden = true; }, 1600);
}

document.getElementById("btn-min").addEventListener("click", () => invoke("win.minimize"));
document.getElementById("btn-close").addEventListener("click", () => invoke("win.close"));

// 用例 3 的按钮：能弹出提示，说明点击没有被拖动逻辑吃掉。
// 如果这里没反应，先看是不是把按钮写进了带 data-oriel-drag-region 的元素**之外**
// 的层级——那种情况下按钮自己会变成拖动源（Tauri 的用例 12 测的正是这个）。
document.getElementById("click-proof").addEventListener("click", () => {
    toast("按钮点击生效：拖动没有吞掉它");
});
