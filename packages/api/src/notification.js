// 对标 `@tauri-apps/plugin-notification` 的**桌面**实现。
//
// 与 Tauri 的差别（如实标注，不假装一致）：
//
//   * **函数都是异步的**（`isPermissionGranted` / `requestPermission` / `sendNotification`）。
//     Tauri 的 `sendNotification` 在桌面端直接 `new window.Notification(...)`（走浏览器的通知
//     API），所以是同步的；而本库走宿主命令，因为 WebView 的浏览器通知 API 三平台并不可靠
//     （WKWebView 上基本没有），而 `OrielApp.ShowNotification` 三平台都是原生的。
//   * **`active()` / `pending()` 是"本应用发过的通知"**，不是系统通知中心里的全部：
//     三平台都没有列出通知中心的 API。`removeActive()` 只从这份记账里移除，
//     **不会撤下系统里已显示的那条**（投递走的是短命子进程，拿不到系统侧的句柄）——
//     要"点了就消失"请用系统通知自己的生命周期，而不是这个 API。
//   * **`Schedule` 在桌面端真的排期**。Tauri 的桌面端忽略 `schedule`、立即显示
//     （"Scheduling is only supported on mobile"），本库的调度器在宿主进程里，所以是真排期。
//     代价：**应用退出后就不再响**（没有系统级调度器）；要跨进程可靠请用系统的计划任务 /
//     systemd timer / launchd。
//   * **`onAction` 在桌面上不可用**：三平台的投递机制都拿不到"用户点了通知"这件事
//     （未打包的 Windows toast 要 AUMID + COM 激活器，macOS 的 osascript 与 Linux 的
//     notify-send 都没有回调入口）。订阅时**立刻抛 `not_supported`**，而不是给一个永不触发的
//     订阅——后者不会报错，调用方只会在运行时才发现收不到。`onNotificationReceived` 是
//     真事件：宿主每次成功投递（含排期到点）都会触发。
//   * 移动端专有的 8 个导出（`Importance` / `Visibility` / `channels` / `createChannel` /
//     `deleteChannel` / `listChannels` / `registerActionTypes`）**不提供**：它们是 Android / iOS
//     的通知模型，桌面上没有对应概念。

import { invoke } from './core.js';
import { getTransport } from './internal.js';

/** 宿主推"这条通知已投递"时用的事件名。 */
const RECEIVED_EVENT = 'notification.received';

/** 宿主推"用户对通知做了动作"时用的事件名（当前三平台都不会来，见 onAction）。 */
const ACTION_EVENT = 'notification.action';

/** 页面侧订阅编号（`PluginListener.handlerId`；不是宿主侧 id）。 */
let nextHandlerId = 1;

/**
 * 通知重复的周期单位（对标 Tauri 的 `ScheduleEvery`）。
 *
 * @readonly
 * @remarks
 * 取值与 Tauri 的 TS 枚举**逐字一致**（字符串枚举），所以直接把它交给
 * {@link Schedule.every} 即可，不需要再翻译。
 *
 * 年/月按固定天数近似（365 / 30 天）——本库的调度器是"隔 N 天"，不是"每年 1 月 1 日"；
 * 要按日历字段匹配请用 {@link Schedule.interval}。
 */
export const ScheduleEvery = Object.freeze({
    Year: 'year',
    Month: 'month',
    TwoWeeks: 'twoWeeks',
    Week: 'week',
    Day: 'day',
    Hour: 'hour',
    Minute: 'minute',
    Second: 'second',
});

/**
 * 通知的排期（对标 Tauri 的 `Schedule`）。
 *
 * @remarks
 * 用三个静态构造器造它，三个字段**互斥**（同时给两个会被宿主拒绝）：
 *
 * ```js
 * import { Schedule, ScheduleEvery, sendNotification } from '@orielweb/api/notification';
 *
 * // 一分钟后一次
 * await sendNotification({ title: '提醒', schedule: Schedule.at(new Date(Date.now() + 60_000)) });
 *
 * // 每天 9:00（按日历匹配）
 * await sendNotification({ title: '早报', schedule: Schedule.interval({ hour: 9, minute: 0 }) });
 *
 * // 每两小时一次
 * await sendNotification({ title: '喝水', schedule: Schedule.every(ScheduleEvery.Hour, 2) });
 * ```
 *
 * **应用退出后排期不再触发**（调度器在宿主进程里）。
 */
export class Schedule {
    constructor() {
        /**
         * 固定时刻触发（{@link Schedule.at} 设定）。
         *
         * @type {{ date: Date | string | number, repeating: boolean, allowWhileIdle: boolean } | undefined}
         */
        this.at = undefined;

        /**
         * 按日历字段匹配（{@link Schedule.interval} 设定）。
         *
         * @type {{ interval: Record<string, number>, allowWhileIdle: boolean } | undefined}
         */
        this.interval = undefined;

        /**
         * 固定周期（{@link Schedule.every} 设定）。
         *
         * @type {{ interval: string, count: number, allowWhileIdle: boolean } | undefined}
         */
        this.every = undefined;
    }

    /**
     * 在某个时刻触发（对标 Tauri 的 `Schedule.at`）。
     *
     * @param {Date | string | number} date 触发时刻；**必须是将来的时刻**（宿主会拒绝过去的时刻）。
     *   `Date` 最直观；字符串按 ISO 8601 解释，数字按 Unix 毫秒解释。
     * @param {boolean} [repeating] 是否重复：周期 = "排期时刻 → `date`"的距离。
     * @param {boolean} [allowWhileIdle] Android 的低电量待机开关（桌面上接受并忽略）。
     * @returns {Schedule}
     * @throws {TypeError} `date` 不是 Date / 字符串 / 有限数字时。
     */
    static at(date, repeating = false, allowWhileIdle = false) {
        const valid = date instanceof Date
            || typeof date === 'string'
            || (typeof date === 'number' && Number.isFinite(date));
        if (!valid) {
            throw new TypeError('Schedule.at: date 必须是 Date、ISO 8601 字符串或 Unix 毫秒数。');
        }

        const schedule = new Schedule();
        schedule.at = { date, repeating: repeating === true, allowWhileIdle: allowWhileIdle === true };
        return schedule;
    }

    /**
     * 按日历字段匹配（对标 Tauri 的 `Schedule.interval`）。
     *
     * @param {{ year?: number, month?: number, day?: number, weekday?: number, hour?: number,
     *   minute?: number, second?: number }} interval 至少要给一个字段；**给了的字段都必须相等**。
     *   `weekday` 按 Tauri 的编号：1 = 周日 … 7 = 周六。
     * @param {boolean} [allowWhileIdle] Android 的低电量待机开关（桌面上接受并忽略）。
     * @returns {Schedule}
     * @throws {TypeError} 一个字段都没给、给了不认识的字段，或字段类型不对时。
     * @remarks 不认识的字段**当场拒绝**（比 Tauri 严）：`{ munite: 30 }` 这种拼写错误在
     * Tauri 那边等于"每秒都匹配"，而这里会直接报错——静默变成"每秒响一次"没有任何好处。
     */
    static interval(interval, allowWhileIdle = false) {
        if (interval === null || typeof interval !== 'object') {
            throw new TypeError('Schedule.interval: interval 必须是对象。');
        }

        const known = ['year', 'month', 'day', 'weekday', 'hour', 'minute', 'second'];
        /** @type {Record<string, number>} */
        const fields = {};
        for (const [key, value] of Object.entries(interval)) {
            if (!known.includes(key)) {
                throw new TypeError(
                    `Schedule.interval: 不认识的字段 '${key}'（可用：${known.join(' / ')}）。`);
            }

            if (typeof value !== 'number' || !Number.isInteger(value)) {
                throw new TypeError(`Schedule.interval: '${key}' 必须是整数。`);
            }

            fields[key] = value;
        }

        if (Object.keys(fields).length === 0) {
            throw new TypeError(
                'Schedule.interval: 至少要给一个字段（year / month / day / weekday / hour / minute / second）。');
        }

        const schedule = new Schedule();
        schedule.interval = { interval: fields, allowWhileIdle: allowWhileIdle === true };
        return schedule;
    }

    /**
     * 固定周期重复（对标 Tauri 的 `Schedule.every`）。
     *
     * @param {string} kind 单位，取 {@link ScheduleEvery} 的值（如 `ScheduleEvery.Hour`）。
     * @param {number} [count] 周期倍数：`every(Hour, 2)` = 每两小时一次。
     * @param {boolean} [allowWhileIdle] Android 的低电量待机开关（桌面上接受并忽略）。
     * @returns {Schedule}
     * @throws {TypeError} 单位不认识，或 `count` 不是 ≥ 1 的整数时。
     */
    static every(kind, count = 1, allowWhileIdle = false) {
        if (!/** @type {string[]} */ (Object.values(ScheduleEvery)).includes(kind)) {
            throw new TypeError(
                `Schedule.every: 未知的周期单位 '${kind}'（用 ScheduleEvery 里的值）。`);
        }

        if (!Number.isInteger(count) || count < 1) {
            throw new TypeError('Schedule.every: count 必须是 ≥ 1 的整数。');
        }

        const schedule = new Schedule();
        schedule.every = { interval: kind, count, allowWhileIdle: allowWhileIdle === true };
        return schedule;
    }
}

/**
 * 把 `Schedule`（或同形的裸对象）归一成要发的载荷。
 *
 * @param {any} schedule
 * @returns {Record<string, unknown>}
 */
function schedulePayload(schedule) {
    if (schedule === null || typeof schedule !== 'object') {
        throw new TypeError('sendNotification: schedule 必须是 Schedule 实例（或 at / interval / every 同形对象）。');
    }

    const keys = ['at', 'interval', 'every'].filter((key) => schedule[key] !== undefined && schedule[key] !== null);
    const key = keys[0];
    if (keys.length !== 1 || key === undefined) {
        throw new TypeError(
            `sendNotification: schedule 必须且只能给 at / interval / every 三者之一（实际给了 ${keys.length} 个）。`);
    }

    return { [key]: schedule[key] };
}

/**
 * 把"编号数组"归一成整数数组。
 *
 * @param {any} notifications
 * @param {string} name
 * @returns {number[]}
 */
function idList(notifications, name) {
    if (!Array.isArray(notifications)) {
        throw new TypeError(`notification.${name}: notifications 必须是数组。`);
    }

    return notifications.map((item) => {
        const value = item !== null && typeof item === 'object' ? item.id : item;
        if (!Number.isInteger(value)) {
            throw new TypeError(
                `notification.${name}: 通知编号必须是整数（编号来自 pending() / active() 的返回值）。`);
        }

        return value;
    });
}

/**
 * 宿主是否允许发送通知。
 *
 * @returns {Promise<boolean>}
 * @remarks
 * 桌面上没有"通知权限"这个概念（能显示就能显示），所以这里回的是
 * **宿主是否支持通知**——与 Tauri 的语义一致（它在移动端才真的查权限）。
 */
export function isPermissionGranted() {
    return invoke('notification.isPermissionGranted');
}

/**
 * 请求通知权限。
 *
 * @returns {Promise<'granted' | 'denied'>}
 * @remarks
 * 桌面端**不弹任何对话框**（没有可弹的东西），直接回结果。
 * 返回字符串而不是布尔是为了与 Tauri 以及 Web Notification 规范一致。
 */
export function requestPermission() {
    return invoke('notification.requestPermission');
}

/**
 * 发一条系统通知（带 `schedule` 时是**排期**）。
 *
 * @param {string | { title: string, body?: string, schedule?: Schedule }} options 标题，
 *   或 `{ title, body, schedule }`。
 * @returns {Promise<boolean>} 系统是否接受了它（带 `schedule` 时表示"已排期"）。
 * @throws {TypeError} 参数形状不对、或 `schedule` 的配置互斥/字段非法时（**在发 IPC 之前**）。
 * @remarks
 * 返回 `false` 而不是抛错，用于"用户关掉了这个应用的通知"这类**正常状态**——
 * 那不是调用方的错误，逼它包 try 只会让代码更难读。
 *
 * 形状与 Tauri 的 `sendNotification` 一致（收字符串或 `{ title, body }`），
 * 但**是异步的**（理由见文件头）。排期通知的编号经 {@link pending} 取。
 */
export function sendNotification(options) {
    if (typeof options === 'string') {
        return invoke('notification.sendNotification', { title: options });
    }

    if (options === null || typeof options !== 'object' || typeof options.title !== 'string') {
        throw new TypeError('sendNotification: 参数必须是字符串或 { title, body }');
    }

    /** @type {Record<string, unknown>} */
    const args = {
        title: options.title,
        body: options.body,
    };

    if (options.schedule !== undefined && options.schedule !== null) {
        args.schedule = schedulePayload(options.schedule);
    }

    return invoke('notification.sendNotification', args);
}

/**
 * 本应用发出、仍在登记表里的通知（对标 Tauri 的 `active`）。
 *
 * @returns {Promise<Array<{ id: number, title?: string, body?: string, tag?: string,
 *   groupSummary: boolean, data: Record<string, string>, extra: Record<string, unknown>,
 *   attachments: unknown[] }>>}
 * @remarks
 * **不是"系统通知中心里的全部"**：别的应用发的、用户手动清掉的，本库都看不到
 * （三平台都没有列出通知中心的 API）。
 *
 * `groupSummary` / `data` / `extra` / `attachments` 在 Tauri 的类型里是必填字段，
 * 而它们分别是 Android / iOS 的概念——桌面上恒为空值（`false` / `{}` / `{}` / `[]`）。
 */
export async function active() {
    const records = await invoke('notification.active');
    return Array.isArray(records) ? records : [];
}

/**
 * 已排期、尚未到点的通知（对标 Tauri 的 `pending`）。
 *
 * @returns {Promise<Array<{ id: number, title?: string, body?: string, schedule: object }>>}
 * @remarks `schedule` 的形状与 {@link Schedule} 序列化之后一致（`{ at }` / `{ interval }` / `{ every }`）。
 */
export async function pending() {
    const records = await invoke('notification.pending');
    return Array.isArray(records) ? records : [];
}

/**
 * 取消待发通知（**只对待发有效**，对标 Tauri 的 `cancel`）。
 *
 * @param {Array<number | { id: number }>} notifications 编号（来自 {@link pending}）。
 *   元素写成 `{ id }` 也接受——Tauri 的 `cancel` 收数字、`removeActive` 收对象，
 *   混用是迁移里的常见错误。
 * @returns {Promise<number>} 真正取消掉的条数（Tauri 返回 void，这里多给一个数；
 *   编号不对/已经投递出去的那些不算在内）。
 * @remarks 取消是**可靠**的：定时器在宿主进程里，取消就是取消。
 */
export async function cancel(notifications) {
    return invoke('notification.cancel', { notifications: idList(notifications, 'cancel') });
}

/**
 * 取消全部待发通知（对标 Tauri 的 `cancelAll`）。
 *
 * @returns {Promise<number>} 取消掉的条数。
 */
export async function cancelAll() {
    return invoke('notification.cancelAll');
}

/**
 * 从活动列表里移除通知（对标 Tauri 的 `removeActive`）。
 *
 * @param {Array<{ id: number } | number>} notifications 编号（来自 {@link active}）。
 * @returns {Promise<number>} 移除掉的条数。
 * @remarks
 * **只移除本库的记账，不撤下系统里已显示的那条**：投递走的是短命子进程
 * （Windows PowerShell / macOS osascript / Linux notify-send），投递完就拿不到
 * 系统侧的句柄了。详见文件头。
 */
export async function removeActive(notifications) {
    const ids = idList(notifications, 'removeActive');
    return invoke('notification.removeActive', { notifications: ids.map((id) => ({ id })) });
}

/**
 * 清空活动列表（对标 Tauri 的 `removeAllActive`）。
 *
 * @returns {Promise<number>} 移除掉的条数。
 * @remarks 同 {@link removeActive}：只清记账，不撤系统里那些。
 */
export async function removeAllActive() {
    return invoke('notification.removeAllActive');
}

/**
 * 订阅"通知已投递"（对标 Tauri 的 `onNotificationReceived`）。
 *
 * @param {(notification: { id: number, title: string, body: string | null }) => void} handler
 * @returns {Promise<{ event: string, handlerId: number, unregister: () => Promise<void> }>}
 * @throws {TypeError} `handler` 不是函数时。
 * @throws {Error} 传输层不支持事件订阅时（不是运行在宿主里）。
 * @remarks
 * **"收到"= 宿主成功投递**（立即投递或排期到点触发），不是"用户看到了"。
 * 这条事件是真事件；Tauri 的桌面端没有对应实现（它只有移动端）。
 */
export async function onNotificationReceived(handler) {
    return subscribe(RECEIVED_EVENT, handler, 'onNotificationReceived');
}

/**
 * 订阅"用户对通知做了动作"（对标 Tauri 的 `onAction`）。
 *
 * @param {Function} handler
 * @returns {Promise<{ event: string, handlerId: number, unregister: () => Promise<void> }>}
 * @throws {TypeError} `handler` 不是函数时。
 * @throws {Error} **当前桌面宿主一律抛 `not_supported`**（见下）。
 * @remarks
 * 三平台的投递机制都拿不到"用户点了通知"：未打包的 Windows toast 激活需要开始菜单快捷方式
 * 携带 AUMID 并注册 COM 激活器（打包器的职责），macOS 的 osascript 没有回调入口，
 * Linux 的 notify-send 同样没有。
 *
 * 与其给一个**永不触发**的订阅（不会报错，调用方只会在运行时才发现收不到），不如在订阅时
 * 就失败——`error.code === 'not_supported'`，调用方可以据此降级。
 * 真要点击回传得先进 .app bundle / AUMID 打包，那是另一个量级的改动。
 */
export async function onAction(handler) {
    if (typeof handler !== 'function') {
        throw new TypeError('notification.onAction: 需要一个函数。');
    }

    const supported = await invoke('notification.actionSupport');
    if (supported !== true) {
        const error = new Error(
            'notification.onAction: 当前宿主拿不到通知点击（三平台投递机制都没有回调入口）。' +
            '要点击回传需打包并注册系统级通知激活器；在此之前请在页面里自行提供入口。');
        // 与 invoke 的错误一样带机器可判别的 code
        Reflect.defineProperty(error, 'code', { value: 'not_supported', enumerable: true });
        throw error;
    }

    return subscribe(ACTION_EVENT, handler, 'onAction');
}

/**
 * 订阅一个宿主事件并包成 Tauri 的 `PluginListener` 形状。
 *
 * @param {string} event 宿主事件名。
 * @param {Function} handler 页面回调。
 * @param {string} name 出错时用于文案的命令名。
 * @returns {Promise<{ event: string, handlerId: number, unregister: () => Promise<void> }>}
 */
async function subscribe(event, handler, name) {
    if (typeof handler !== 'function') {
        throw new TypeError(`notification.${name}: 需要一个函数。`);
    }

    const transport = getTransport();
    if (typeof transport.on !== 'function') {
        throw new Error(`notification.${name}: 传输层不支持 on()——无法订阅通知事件。`);
    }

    const off = transport.on(event, (/** @type {any} */ value) => {
        try {
            handler(value);
        } catch (error) {
            // 订阅者自己的异常不该让整条事件通道断掉（与 tray 的处理器同一个姿态）
            console.error(`notification.${name} 处理器抛出`, error);
        }
    });

    const handlerId = nextHandlerId++;
    return {
        event,
        handlerId,
        unregister: async () => {
            off();
        },
    };
}
