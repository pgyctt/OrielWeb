// 移植自 tauri/examples/commands 的页面部分。
//
// 与原文的差别只有 import 前缀：`@tauri-apps/api/core` → `@orielweb/api/core`。
// 示例没有打包器，所以写成相对路径、直接指向内嵌进来的包源码；真实项目
// 有打包器就用裸标识符（见 packages/api/README.md）。
//
// 命令名一个都没改：`greet` / `async_simple_command` / `simple_command_with_result` /
// `command_arguments_struct` / `renamed_command_new` 与 Tauri 侧逐字相同。
// 参数名也是 camelCase，与 C# 的参数名逐字对应（本库按参数名取值）。

import { invoke } from "./vendor/orielweb-api/core.js";
import { getCurrentWindow } from "./vendor/orielweb-api/window.js";

const logEl = document.getElementById("log");

/** 把回执格式化成一行：null 要显式写出来，空字符串要能看出来是空字符串。 */
function format(value) {
    if (value === null) {
        return "null";
    }
    if (typeof value === "string") {
        return JSON.stringify(value);
    }
    return JSON.stringify(value);
}

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

/**
 * 统一的调用壳：成功记回执与耗时，失败记 message + code。
 *
 * 原文是每条命令各写一遍 `invoke(...).then(console.log)`（在 Rust 侧 println!）。
 * 这里改成页面内可见的日志——示例是 WinExe，没有控制台可看。
 */
async function call(label, run) {
    const started = performance.now();
    try {
        const value = await run();
        append(`${label} → ${format(value)}  (${Math.round(performance.now() - started)} ms)`, "ok");
    } catch (error) {
        const code = error.code ? ` [code=${error.code}]` : "";
        append(`${label} → 失败：${error.message}${code}`, "err");
    }
}

const value = (id) => document.getElementById(id).value;

document.getElementById("greet-run").addEventListener("click", () =>
    call("greet", () => invoke("greet", { name: value("greet-name") })));

document.getElementById("async-run").addEventListener("click", () =>
    call("async_simple_command", () =>
        invoke("async_simple_command", { theArgument: value("async-arg") })));

document.getElementById("result-ok").addEventListener("click", () =>
    call("simple_command_with_result", () =>
        invoke("simple_command_with_result", { theArgument: value("result-arg") })));

document.getElementById("result-err").addEventListener("click", () =>
    call("simple_command_with_result(空值)", () =>
        invoke("simple_command_with_result", { theArgument: "" })));

document.getElementById("person-run").addEventListener("click", () => {
    const age = Number.parseInt(value("person-age"), 10);

    // **形状差异**：Tauri 把整个 args 反序列化进结构体参数（{ name, age }）；
    // 本库按参数名取，所以 DTO 要包在参数名 person 下面。
    const args = {
        person: {
            name: value("person-name"),
            age: Number.isNaN(age) ? 0 : age,
        },
    };

    return call("command_arguments_struct", () => invoke("command_arguments_struct", args));
});

document.getElementById("renamed-run").addEventListener("click", () =>
    call("renamed_command_new", () => invoke("renamed_command_new")));

// 原文的 window_label 命令靠注入 Window 取 label；本库不做参数注入，
// 页面侧本来就有稳定标识（对标 Tauri 的 Window.label）。
document.getElementById("label-run").addEventListener("click", () => {
    append(`getCurrentWindow().label → ${JSON.stringify(getCurrentWindow().label)}`, "ok");
});

document.getElementById("log-clear").addEventListener("click", () => {
    logEl.replaceChildren();
});

append("就绪。上方每张卡片对应 tauri/examples/commands 里的一条命令。", "empty");
