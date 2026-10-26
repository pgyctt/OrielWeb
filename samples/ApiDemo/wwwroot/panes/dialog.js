// `@orielweb/api/dialog`：五个入口。
//
// 都会弹**模态**原生对话框——所以它们必须显式授权（本应用 Allow("*")）。
// Windows 上自定义按钮文案不生效（MessageBoxW 的限制），返回的是语义映射后的文案。

import { ask, confirm, message, open, save } from "../vendor/orielweb-api/dialog.js";
import { join } from "../vendor/orielweb-api/path.js";
import { sandbox } from "./common.js";

export default {
    id: "dialog",
    title: "Dialog 对话框",
    module: "@orielweb/api/dialog",
    blurb:
        "打开/保存/消息三类。返回值形状随参数变形：单选是 string、多选是 string[]、取消是 null。"
        + "Windows 上自定义按钮文案不生效，返回的仍是语义映射后的文案。",
    groups: [
        {
            title: "文件",
            items: [
                {
                    name: "open({ filters })",
                    note: "只选一个文件；取消返回 null。",
                    run: () => open({
                        title: "选一张图片",
                        filters: [
                            { name: "图片", extensions: ["png", "jpg", "jpeg", "gif"] },
                            { name: "所有文件", extensions: ["*"] },
                        ],
                    }),
                },
                {
                    name: "open({ multiple: true })",
                    run: () => open({ title: "选多个文件", multiple: true }),
                },
                {
                    name: "open({ directory: true })",
                    note: "目录只支持单选；directory + multiple 会报错。",
                    run: () => open({ title: "选一个目录", directory: true }),
                },
                {
                    name: "open({ directory, multiple })（预期失败）",
                    badge: "预期失败",
                    run: () => open({ directory: true, multiple: true }),
                },
                {
                    name: "save({ defaultPath })",
                    run: async () => save({
                        title: "保存到…",
                        defaultPath: await join(await sandbox(), "saved.txt"),
                        filters: [{ name: "文本", extensions: ["txt"] }],
                    }),
                },
            ],
        },
        {
            title: "消息",
            note: "message 返回被点按钮的语义文案；被外部关掉时是 null。",
            items: [
                { name: "message('操作完成', { kind: 'info' })", run: () => message("操作完成。", { title: "提示", kind: "info" }) },
                { name: "message(..., { kind: 'warning' })", run: () => message("磁盘空间不足。", { title: "警告", kind: "warning" }) },
                { name: "message(..., { kind: 'error' })", run: () => message("写入失败。", { title: "错误", kind: "error" }) },
                {
                    name: "message(..., { buttons: 'YesNoCancel' })",
                    note: "返回值是 yes/no/cancel（Windows 显示系统文案）。",
                    run: () => message("要保存吗？", { title: "三选一", buttons: "YesNoCancel" }),
                },
                { name: "ask('继续吗？')", run: () => ask("继续吗？", { title: "询问" }) },
                {
                    name: "confirm('确定删除？', { okLabel: '删除' })",
                    note: "okLabel 只在 Linux/macOS 生效。",
                    run: () => confirm("确定删除？", { title: "确认", okLabel: "删除" }),
                },
            ],
        },
    ],
};
