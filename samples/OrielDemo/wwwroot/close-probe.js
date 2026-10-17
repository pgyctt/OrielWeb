"use strict";

// 关闭接管自检的探针页面（--selftest close）。
//
// 直接用 window.oriel（裸传输层）：这个自检验的是**宿主侧关闭管线**，
// 而页面侧的包装语义（@orielweb/api 的 onCloseRequested）由 JS 包单测覆盖。两者分工。
//
// 回显协议（宿主侧 CloseSelfTest 消费）：
//   close.probe.ready      → { label }   页面装好了
//   close.probe.requested  → { label, mode }  收到了关闭请求

window.__closeMode = "prevent";

const oriel = window.oriel;

if (!oriel) {
    document.getElementById("status").textContent = "no bridge";
} else {
    oriel.on("close-requested", () => {
        const mode = window.__closeMode;
        oriel.postMessage("close.probe.requested", { label: oriel.label, mode: mode });

        if (mode === "allow") {
            // 模拟"用户在确认框里点了确认"：走**不可拦**的销毁路径。
            // 用 win.destroy 而不是 win.close —— 后者又会走一遍关闭管线，
            // 变成"页面放行 → 再问页面"的无限循环。
            oriel.invoke("win.destroy").catch((error) => {
                oriel.postMessage("close.probe.error", { label: oriel.label, error: String(error) });
            });
        }

        // mode === 'prevent' 时什么都不做：窗口必须留在打开状态。
    });

    oriel.postMessage("close.probe.ready", { label: oriel.label });
}
