"use strict";

// 事件自检的探针页面（--selftest event）。
//
// 直接用 window.oriel（裸传输层）而不是 @orielweb/api：
//   * 这个自检验的是**宿主侧投递**——命令能不能发出去、按 label 定向对不对；
//   * JS 包的包装语义由 packages/api/tests/event.test.mjs 覆盖（那里能造任意时序）。
// 两者分工，不重复。
//
// 回显协议（宿主侧 EventSelfTest 消费）：
//   probe.ready     → { label }            页面装好了
//   probe.received  → { label, payload }   收到了 probe.event

const oriel = window.oriel;

if (!oriel) {
    // 不可信来源根本不装桥接（见 API.md §4）。自检页面不该走到这里——
    // 走到说明内嵌资源被当成了不可信来源，那本身就是个缺陷。
    document.getElementById("status").textContent = "no bridge";
} else {
    oriel.on("probe.event", (payload) => {
        oriel.postMessage("probe.received", { label: oriel.label, payload: payload });
    });

    oriel.postMessage("probe.ready", { label: oriel.label });
}
