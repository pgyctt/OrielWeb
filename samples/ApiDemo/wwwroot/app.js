// ApiDemo 的壳：侧栏路由 + 全局回执日志。
//
// 每个模块一个 pane 模块，导出一个声明式的 { id, title, module, blurb, groups }。
// 这里只负责把它们串起来——新增一组 API 不需要碰这个文件。

import { setLogSink, h, section, action, log } from "./ui.js";
import { invoke } from "./vendor/orielweb-api/core.js";

import coverage from "./panes/coverage.js";
import core from "./panes/core.js";
import appPane from "./panes/app.js";
import windowPane from "./panes/window.js";
import webviewWindow from "./panes/webviewWindow.js";
import eventPane from "./panes/event.js";
import pathPane from "./panes/path.js";
import fsPane from "./panes/fs.js";
import dialogPane from "./panes/dialog.js";
import clipboardPane from "./panes/clipboard.js";
import openerPane from "./panes/opener.js";
import notificationPane from "./panes/notification.js";
import autostartPane from "./panes/autostart.js";
import menuPane from "./panes/menu.js";
import trayPane from "./panes/tray.js";
import imagePane from "./panes/image.js";
import dpiPane from "./panes/dpi.js";
import dragDropPane from "./panes/dragdrop.js";

const panes = [
    coverage,
    core,
    appPane,
    windowPane,
    webviewWindow,
    eventPane,
    pathPane,
    fsPane,
    dialogPane,
    clipboardPane,
    openerPane,
    notificationPane,
    autostartPane,
    menuPane,
    trayPane,
    imagePane,
    dpiPane,
    dragDropPane,
];

const navEl = document.getElementById("nav");
const paneEl = document.getElementById("pane");
const logEl = document.getElementById("log-list");
const logCountEl = document.getElementById("log-count");
const logPanel = document.getElementById("log-panel");

let logCount = 0;

setLogSink(({ text, level, time }) => {
    logCount += 1;
    logCountEl.textContent = String(logCount);

    const line = h(
        "li",
        {},
        h("span", { class: "t" }, time.toLocaleTimeString()),
        h("span", { class: level }, text),
    );

    logEl.append(line);
    while (logEl.children.length > 400) {
        logEl.firstElementChild.remove();
    }
    logEl.scrollTop = logEl.scrollHeight;
});

/** 一个模块里可点的动作数（`node` 类自定义块不计）。 */
function actionCount(pane) {
    return (pane.groups ?? []).reduce(
        (total, group) => total + (group.items ?? []).filter((item) => typeof item.run === "function").length,
        0,
    );
}

const counts = new Map(panes.map((pane) => [pane.id, actionCount(pane)]));
const totalActions = [...counts.values()].reduce((sum, n) => sum + n, 0);

function buildNav() {
    for (const pane of panes) {
        const button = h(
            "button",
            {
                class: "nav-item",
                "data-id": pane.id,
                onclick: () => { location.hash = `#/${pane.id}`; },
            },
            h("span", { class: "n" }, counts.get(pane.id) || "–"),
            h("span", { class: "label" }, pane.title),
        );
        navEl.append(button);
    }
}

function markActive(id) {
    for (const button of navEl.children) {
        button.classList.toggle("active", button.dataset.id === id);
    }
}

function renderPane(pane) {
    paneEl.replaceChildren();
    document.querySelector(".main")?.scrollTo({ top: 0 });

    paneEl.append(
        h(
            "header",
            { class: "pane-head" },
            h("h1", {}, pane.title, pane.module ? h("span", { class: "module" }, pane.module) : null),
            pane.blurb ? h("p", {}, pane.blurb) : null,
        ),
    );

    for (const group of pane.groups ?? []) {
        const container = section(paneEl, group);
        for (const item of group.items ?? []) {
            if (typeof item.node === "function") {
                container.append(item.node());
            } else if (typeof item.render === "function") {
                item.render(container);
            } else {
                action(container, item);
            }
        }
    }

    // 覆盖页需要看到其它 pane 的统计，所以把 panes 传进去。
    if (typeof pane.render === "function") {
        pane.render(paneEl, panes, counts);
    }
}

function route() {
    const requested = location.hash.replace(/^#\//, "");
    const pane = panes.find((item) => item.id === requested) ?? panes[0];

    if (requested !== pane.id) {
        location.replace(`#/${pane.id}`);
    }

    markActive(pane.id);
    renderPane(pane);
}

buildNav();

document.getElementById("log-toggle").addEventListener("click", () => {
    const collapsed = logPanel.classList.toggle("collapsed");
    document.getElementById("log-toggle").setAttribute("aria-expanded", String(!collapsed));
});

document.getElementById("log-clear").addEventListener("click", () => {
    logEl.replaceChildren();
    logCount = 0;
    logCountEl.textContent = "0";
});

const platform = globalThis.oriel?.platform ?? "unknown";
const version = globalThis.oriel?.version ?? "unknown";
document.getElementById("host-platform").textContent = `platform ${platform}`;
document.getElementById("host-version").textContent = `lib ${version}`;

window.addEventListener("hashchange", route);
route();

log(`API Demo 就绪：${panes.length - 1} 个模块，${totalActions} 个可运行动作。`, "info");
log(`来源 ${location.origin}（oriel:// 下才是可信来源，桥接只注入到这里）。`, "info");

// 自报"模块图加载完"：宿主会在 stderr 打一行。没有这一行就说明前端没起来，
// 而不是"窗口没建出来"——两种白屏的排查方向完全不同。
void invoke("demo.ready", { modules: panes.length - 1, actions: totalActions })
    .catch((error) => console.error("demo.ready 失败", error));
