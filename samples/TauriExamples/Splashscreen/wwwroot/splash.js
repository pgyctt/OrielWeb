// 启动画面自己的脚本：只做一件事——允许用户跳过等待。
//
// 命令行与主窗口用的是同一条 close_splashscreen：这个命令不关心是谁调的，
// 只按 label 操作窗口。

import { invoke } from "./vendor/orielweb-api/core.js";

document.getElementById("skip").addEventListener("click", async () => {
    try {
        await invoke("close_splashscreen");
    } catch (error) {
        // 启动画面没有日志区，失败就把原因写在按钮上——总比静默无反应好
        document.getElementById("skip").textContent = `失败：${error.message}`;
    }
});
