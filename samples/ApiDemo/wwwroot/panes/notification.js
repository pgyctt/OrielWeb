// `@orielweb/api/notification`：权限、发送、登记表、排期。
//
// 与 Tauri 桌面端最大的差别：active/pending/cancel 是真的（宿主侧登记表），
// 排期也是真的（宿主进程内的调度器），onNotificationReceived 是真事件；
// 而 onAction 订阅时直接抛 not_supported（三平台都拿不到点击）。

import {
    Schedule,
    active,
    cancel,
    cancelAll,
    isPermissionGranted,
    onAction,
    onNotificationReceived,
    pending,
    removeActive,
    removeAllActive,
    requestPermission,
    sendNotification,
} from "../vendor/orielweb-api/notification.js";
import { log } from "../ui.js";

let receivedOff = null;

export default {
    id: "notification",
    title: "Notification 通知",
    module: "@orielweb/api/notification",
    blurb:
        "发送、登记表、排期、事件。onAction 在桌面端拿不到点击，订阅即抛 not_supported——"
        + "那条错误本身就是对齐结论。",
    groups: [
        {
            title: "权限与发送",
            items: [
                { name: "isPermissionGranted()", run: () => isPermissionGranted() },
                { name: "requestPermission()", run: () => requestPermission() },
                {
                    name: "sendNotification('标题')",
                    run: () => sendNotification("来自 Oriel API Demo"),
                },
                {
                    name: "sendNotification({ title, body })",
                    run: () => sendNotification({
                        title: "Oriel API Demo",
                        body: "这是一条带正文的系统通知。",
                    }),
                },
                {
                    name: "sendNotification({ schedule: 3s 后 })",
                    note: "桌面端真的排期（调度器在宿主进程内，应用退出即止）。",
                    run: async () => {
                        const schedule = Schedule.at(new Date(Date.now() + 3000));
                        const ok = await sendNotification({
                            title: "排期通知",
                            body: "3 秒前排的队。",
                            schedule,
                        });
                        return ok ? "已排期，3 秒后到点（看系统通知）" : "系统没有接受";
                    },
                },
                {
                    name: "Schedule.at / interval / every 常量",
                    render(container) {
                        const list = document.createElement("dl");
                        list.className = "kv";
                        const rows = {
                            "Schedule.at(date)": "固定时刻；必须是将来",
                            "Schedule.interval({...})": "按日历字段匹配（weekday: 1=周日 … 7=周六）",
                            "Schedule.every('minute', n)": "固定周期",
                        };
                        for (const [key, value] of Object.entries(rows)) {
                            const dt = document.createElement("dt");
                            dt.textContent = key;
                            const dd = document.createElement("dd");
                            dd.textContent = value;
                            list.append(dt, dd);
                        }
                        container.append(list);
                    },
                },
            ],
        },
        {
            title: "登记表（本应用发过的通知）",
            items: [
                { name: "active()", run: () => active() },
                { name: "pending()", run: () => pending() },
                {
                    name: "cancel(第一条 pending)",
                    run: async () => {
                        const list = await pending();
                        if (list.length === 0) {
                            return "没有待发通知（先排一条 3 秒后的）";
                        }
                        return `已取消 ${await cancel([list[0].id])} 条`;
                    },
                },
                { name: "cancelAll()", run: () => cancelAll() },
                {
                    name: "removeActive(第一条)",
                    note: "只移除本库的记账，不撤下系统里已显示的那条。",
                    run: async () => {
                        const list = await active();
                        if (list.length === 0) {
                            return "活动列表为空";
                        }
                        return `已移除 ${await removeActive([list[0].id])} 条`;
                    },
                },
                { name: "removeAllActive()", run: () => removeAllActive() },
            ],
        },
        {
            title: "事件",
            items: [
                {
                    name: "onNotificationReceived(handler)",
                    note: "宿主每次成功投递（含排期到点）都会触发。",
                    run: async () => {
                        if (receivedOff !== null) {
                            return "已订阅";
                        }
                        receivedOff = await onNotificationReceived((notification) => {
                            log(`notification received → ${JSON.stringify(notification)}`, "info");
                        });
                        return "已订阅（再发一条通知就能看到）";
                    },
                },
                {
                    name: "onAction(handler)（预期失败）",
                    badge: "预期失败",
                    run: () => onAction(() => {}),
                },
            ],
        },
    ],
};
