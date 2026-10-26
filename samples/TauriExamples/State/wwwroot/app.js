// 移植自 tauri/examples/state 的页面部分。
//
// 原文：`invoke('increment').then(setValue)` 这一类写法。
// 改动只有 import 前缀；命令名（increment / decrement / reset / get）与参数（无）都没变。

import { invoke } from "./vendor/orielweb-api/core.js";

const valueEl = document.getElementById("value");
const logEl = document.getElementById("log");

function append(text, kind) {
    const item = document.createElement("li");

    const time = document.createElement("span");
    time.className = "time";
    time.textContent = new Date().toLocaleTimeString();

    const body = document.createElement("span");
    body.className = kind;
    body.textContent = text;

    item.append(time, body);
    logEl.append(item);
    logEl.scrollTop = logEl.scrollHeight;
}

/** 调一条命令，把返回值写进大字区，并记一行日志。 */
async function call(command) {
    try {
        const value = await invoke(command);
        valueEl.textContent = String(value);
        append(`${command} → ${value}`, "ok");
    } catch (error) {
        append(`${command} → 失败：${error.message}`, "err");
    }
}

for (const command of ["increment", "decrement", "reset", "get"]) {
    document.getElementById(command).addEventListener("click", () => call(command));
}

// 进页面先同步一次真实状态：页面刷新不重置计数器，状态在宿主侧。
call("get");
