// 移植自 tauri/examples/multiwindow 的页面部分。
//
// 原文用的是全局注入的 `window.__TAURI__.webviewWindow` / `.event`；
// 这里换成 `@orielweb/api` 的同名导出（示例无打包器，用相对路径）。
// **函数名与调用形状一字未改**——这是本组示例里最接近"只改 import 前缀"的一份。
//
// 与 Tauri 的差别只有一处，写在 new WebviewWindow(...) 那一行下面。

import { WebviewWindow, getCurrentWebviewWindow } from "./vendor/orielweb-api/webviewWindow.js";
import { emitTo } from "./vendor/orielweb-api/event.js";

const current = getCurrentWebviewWindow();
document.getElementById("current-label").textContent = current.label;

const messagesEl = document.getElementById("messages");
const statusEl = document.getElementById("status");

function setStatus(text, isError = false) {
    statusEl.textContent = text;
    statusEl.classList.toggle("err", isError);
}

function appendMessage(text) {
    if (messagesEl.classList.contains("empty")) {
        messagesEl.classList.remove("empty");
        messagesEl.textContent = "";
    }

    const time = new Date().toLocaleTimeString();
    messagesEl.textContent += `[${time}] ${text}\n`;
    messagesEl.scrollTop = messagesEl.scrollHeight;
}

// 空态提示。用一段文字而不是 CSS ::before，是为了让"有没有消息"在 DOM 里也读得出来。
messagesEl.classList.add("empty");
messagesEl.textContent = "（还没有收到消息）";

// 对标原文的 currentWindow.listen('message', ...)：所有窗口都监听同一个事件名，
// 于是任何窗口 emitTo 过来都能在这里看到。payload 是包解开后的裸值。
current.listen("message", (event) => {
    appendMessage(event.payload);
});

document.getElementById("send-form").addEventListener("submit", async (event) => {
    event.preventDefault();

    const message = document.getElementById("send-message").value;
    const label = document.getElementById("send-label").value.trim();

    try {
        // emitTo 只表示"宿主接受了这次广播"，不代表目标窗口一定收到（它可能已经关了）。
        // 这与 Tauri 的语义一致，所以这里不假装能确认送达。
        await emitTo(label, "message", message);
        setStatus(`已向「${label}」发送。`);
        document.getElementById("send-message").value = "";
    } catch (error) {
        setStatus(`发送失败：${error.message}`, true);
    }
});

document.getElementById("new-form").addEventListener("submit", (event) => {
    event.preventDefault();

    const label = document.getElementById("new-label").value.trim();
    const title = document.getElementById("new-title").value.trim();

    // 与 Tauri 的唯一一处形状差异：那边 new WebviewWindow(...) 同步返回实例，
    // 用 once('tauri://created') 等结果；本库改为构造时发起 window.new，
    // 结果在 w.created 上。多出来的这一行处理是必须的——label 重复会在宿主侧被拒，
    // 而"点了创建但什么都没发生"最难查。
    const win = new WebviewWindow(label, { title });
    win.created.then(
        () => setStatus(`已创建窗口「${label}」。`),
        (error) => setStatus(`创建失败：${error.message}`, true),
    );
});
