// 移植自 tauri/examples/splashscreen 的主窗口脚本。
//
// 原文（examples/splashscreen/dist/index.html）：
//
//   document.addEventListener('DOMContentLoaded', () => {
//     setTimeout(() => invoke('close_splashscreen'), 2000)
//   })
//
// 一字未改的语义，只多了两样东西：倒计时显示（否则"2 秒"只是个说法），
// 以及失败处理——命令被能力配置拒掉时，窗口会**永远**隐藏着，
// 没有日志的话现场表现就是"点了图标没反应"。

import { invoke } from "./vendor/orielweb-api/core.js";

const countdownEl = document.getElementById("countdown");
const totalMs = 2000;
const startedAt = performance.now();

async function closeSplash() {
    try {
        await invoke("close_splashscreen");
    } catch (error) {
        countdownEl.textContent = `切换失败：${error.message}`;
        countdownEl.classList.add("err");
    }
}

const timer = setInterval(() => {
    const left = totalMs - (performance.now() - startedAt);
    if (left <= 0) {
        clearInterval(timer);
        countdownEl.textContent = "0.0s";
        void closeSplash();
        return;
    }

    countdownEl.textContent = `${(left / 1000).toFixed(1)}s`;
}, 100);
