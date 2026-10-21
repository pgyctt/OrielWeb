// @orielweb/api/notification 的测试（B18：句柄族 + 排期 + 回传）。
//
// 重点在**形状与参数名**，而不是"通知真的弹出来了没有"（那是 smoke 自检的事）：
//
//   1. `Schedule` 三个静态构造器的产物必须与 Tauri 的 `Schedule` 类序列化结果**同形**——
//      差一层嵌套，宿主就会说"schedule 必须恰好给一个"。
//   2. `cancel` 收数字、`removeActive` 收 `{ id }`（Tauri 的形状），这里两边都宽容，
//      但要保证**发出去的参数键**是 `notifications`。
//   3. `onAction` 在当前宿主一律抛 `not_supported`——它不能是"永不触发的订阅"。
//
// 运行：node --test packages/api/tests/notification.test.mjs

import { test } from 'node:test';
import assert from 'node:assert/strict';

import {
    Schedule,
    ScheduleEvery,
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
} from '../src/notification.js';
import { clearMocks } from '../src/mocks.js';
import { setTransportOverride } from '../src/internal.js';

/**
 * 装一个既能记命令、也能发事件的传输层。
 *
 * @param {Record<string, unknown>} [responses]
 */
function makeTransport(responses = {}) {
    /** @type {Array<[string, any]>} */
    const calls = [];
    /** @type {Map<string, Set<Function>>} */
    const listeners = new Map();

    return {
        calls,
        /** 模拟宿主推事件。 */
        emit(name, value) {
            for (const handler of listeners.get(name) ?? []) {
                handler(value);
            }
        },
        transport: {
            async invoke(name, args) {
                calls.push([name, args]);
                return Object.prototype.hasOwnProperty.call(responses, name) ? responses[name] : null;
            },
            on(name, handler) {
                let set = listeners.get(name);
                if (!set) {
                    set = new Set();
                    listeners.set(name, set);
                }

                set.add(handler);
                return () => set.delete(handler);
            },
        },
    };
}

// ---- ScheduleEvery / Schedule ----

test('ScheduleEvery 的键值与 Tauri 的字符串枚举一致', () => {
    assert.deepEqual(Object.keys(ScheduleEvery), [
        'Year', 'Month', 'TwoWeeks', 'Week', 'Day', 'Hour', 'Minute', 'Second',
    ]);
    assert.equal(ScheduleEvery.Year, 'year');
    assert.equal(ScheduleEvery.TwoWeeks, 'twoWeeks');
    assert.equal(ScheduleEvery.Second, 'second');
    assert.ok(Object.isFrozen(ScheduleEvery), '常量对象应当冻结（防止被就地改写）');
});

test('Schedule.at 产出 { at: { date, repeating, allowWhileIdle } }', () => {
    const date = new Date('2026-12-01T09:00:00Z');
    const schedule = Schedule.at(date);

    assert.deepEqual(schedule.at, { date, repeating: false, allowWhileIdle: false });
    assert.equal(schedule.interval, undefined);
    assert.equal(schedule.every, undefined);

    const repeating = Schedule.at(date, true, true);
    assert.deepEqual(repeating.at, { date, repeating: true, allowWhileIdle: true });
});

test('Schedule.at 拒绝不是日期/字符串/数字的入参', () => {
    for (const bad of [null, undefined, {}, Symbol('x'), NaN, Infinity]) {
        assert.throws(() => Schedule.at(/** @type {any} */ (bad)), TypeError, String(bad));
    }
});

test('Schedule.interval 只带给了的字段，并保留 allowWhileIdle', () => {
    const schedule = Schedule.interval({ hour: 9, minute: 0 }, true);

    assert.deepEqual(schedule.interval, { interval: { hour: 9, minute: 0 }, allowWhileIdle: true });
});

test('Schedule.interval 拒绝空对象 / 未知字段 / 非整数', () => {
    assert.throws(() => Schedule.interval({}), /至少要给一个字段/);
    // 拼写错误必须当场报错：否则它等于"每秒都匹配"，而调用方毫不知情
    assert.throws(() => Schedule.interval(/** @type {any} */ ({ munite: 30 })), /不认识的字段/);
    assert.throws(() => Schedule.interval(/** @type {any} */ ({ hour: 9.5 })), /必须是整数/);
    assert.throws(() => Schedule.interval(/** @type {any} */ ({ hour: '9' })), /必须是整数/);
});

test('Schedule.every 产出 { every: { interval, count, allowWhileIdle } }', () => {
    assert.deepEqual(Schedule.every(ScheduleEvery.Hour, 2).every, {
        interval: 'hour',
        count: 2,
        allowWhileIdle: false,
    });

    // count 缺省是 1（每次一个单位）
    assert.deepEqual(Schedule.every(ScheduleEvery.Day).every, {
        interval: 'day',
        count: 1,
        allowWhileIdle: false,
    });
});

test('Schedule.every 拒绝未知单位与非法 count', () => {
    assert.throws(() => Schedule.every(/** @type {any} */ ('fortnight')), /未知的周期单位/);
    assert.throws(() => Schedule.every(ScheduleEvery.Hour, 0), /count 必须是/);
    assert.throws(() => Schedule.every(ScheduleEvery.Hour, 1.5), /count 必须是/);
});

// ---- sendNotification ----

test('sendNotification 带 schedule 时把排期原样带过去', async () => {
    clearMocks();
    const { calls, transport } = makeTransport({ 'notification.sendNotification': true });
    setTransportOverride(transport);

    const at = new Date('2026-12-01T09:00:00Z');
    const ok = await sendNotification({ title: '提醒', body: '该走了', schedule: Schedule.at(at) });

    assert.equal(ok, true);
    assert.deepEqual(calls, [[
        'notification.sendNotification',
        {
            title: '提醒',
            body: '该走了',
            schedule: { at: { date: at, repeating: false, allowWhileIdle: false } },
        },
    ]]);

    clearMocks();
});

test('sendNotification 的 schedule 互斥项不是恰好一个时**不发 IPC**', () => {
    clearMocks();
    const { calls, transport } = makeTransport({ 'notification.sendNotification': true });
    setTransportOverride(transport);

    const both = Schedule.at(new Date(Date.now() + 1000));
    both.every = { interval: 'hour', count: 1, allowWhileIdle: false };

    assert.throws(() => sendNotification({ title: 'x', schedule: both }), /三者之一/);
    assert.throws(() => sendNotification({ title: 'x', schedule: new Schedule() }), /三者之一/);
    assert.throws(() => sendNotification({ title: 'x', schedule: /** @type {any} */ ('soon') }), TypeError);
    assert.deepEqual(calls, [], '参数错时不该发出任何命令');

    clearMocks();
});

test('sendNotification 收字符串时包成 { title }（命令字段保持稳定）', async () => {
    clearMocks();
    const { calls, transport } = makeTransport({ 'notification.sendNotification': true });
    setTransportOverride(transport);

    await sendNotification('纯标题');

    assert.deepEqual(calls, [['notification.sendNotification', { title: '纯标题' }]]);

    clearMocks();
});

test('isPermissionGranted / requestPermission 的命令名', async () => {
    clearMocks();
    const { calls, transport } = makeTransport({
        'notification.isPermissionGranted': true,
        'notification.requestPermission': 'granted',
    });
    setTransportOverride(transport);

    assert.equal(await isPermissionGranted(), true);
    assert.equal(await requestPermission(), 'granted');
    assert.deepEqual(calls, [
        ['notification.isPermissionGranted', undefined],
        ['notification.requestPermission', undefined],
    ]);

    clearMocks();
});

// ---- 登记表 ----

test('active / pending 在宿主返回 null 时给空数组（而不是 null）', async () => {
    clearMocks();
    const { transport } = makeTransport({ 'notification.active': null, 'notification.pending': null });
    setTransportOverride(transport);

    assert.deepEqual(await active(), []);
    assert.deepEqual(await pending(), []);

    clearMocks();
});

test('active / pending 原样返回宿主给的记录', async () => {
    clearMocks();
    const records = [{ id: 3, title: 'a', body: 'b', groupSummary: false, data: {}, extra: {}, attachments: [] }];
    const pendingRecords = [{ id: 4, title: 'c', schedule: { every: { interval: 'hour', count: 1, allowWhileIdle: false } } }];
    const { transport } = makeTransport({
        'notification.active': records,
        'notification.pending': pendingRecords,
    });
    setTransportOverride(transport);

    assert.deepEqual(await active(), records);
    assert.deepEqual(await pending(), pendingRecords);

    clearMocks();
});

test('cancel / cancelAll 的命令与参数键', async () => {
    clearMocks();
    const { calls, transport } = makeTransport({
        'notification.cancel': 2,
        'notification.cancelAll': 1,
    });
    setTransportOverride(transport);

    assert.equal(await cancel([1, 2]), 2);
    assert.equal(await cancelAll(), 1);

    assert.deepEqual(calls, [
        ['notification.cancel', { notifications: [1, 2] }],
        ['notification.cancelAll', undefined],
    ]);

    clearMocks();
});

test('cancel 也接受 { id } 元素（Tauri 两处形状不同，混用是常见错误）', async () => {
    clearMocks();
    const { calls, transport } = makeTransport({ 'notification.cancel': 1 });
    setTransportOverride(transport);

    await cancel([{ id: 7 }]);

    assert.deepEqual(calls, [['notification.cancel', { notifications: [7] }]]);

    clearMocks();
});

test('cancel 对非整数编号当场拒（不静默取消错对象）', async () => {
    clearMocks();
    const { calls, transport } = makeTransport();
    setTransportOverride(transport);

    await assert.rejects(() => cancel([/** @type {any} */ ('1')]), TypeError);
    await assert.rejects(() => cancel(/** @type {any} */ (1)), TypeError);
    await assert.rejects(() => cancel(/** @type {any} */ ([{ title: 'x' }])), TypeError);
    assert.deepEqual(calls, []);

    clearMocks();
});

test('removeActive 发 { id } 对象（Tauri 的形状）', async () => {
    clearMocks();
    const { calls, transport } = makeTransport({ 'notification.removeActive': 2 });
    setTransportOverride(transport);

    assert.equal(await removeActive([{ id: 5 }, 6]), 2);

    assert.deepEqual(calls, [['notification.removeActive', { notifications: [{ id: 5 }, { id: 6 }] }]]);

    clearMocks();
});

test('removeAllActive 不带参数', async () => {
    clearMocks();
    const { calls, transport } = makeTransport({ 'notification.removeAllActive': 3 });
    setTransportOverride(transport);

    assert.equal(await removeAllActive(), 3);
    assert.deepEqual(calls, [['notification.removeAllActive', undefined]]);

    clearMocks();
});

// ---- 回传 ----

test('onNotificationReceived 订阅 notification.received，并能取消订阅', async () => {
    clearMocks();
    const { transport, emit } = makeTransport();
    setTransportOverride(transport);

    /** @type {any[]} */
    const seen = [];
    const listener = await onNotificationReceived((notification) => seen.push(notification));

    assert.equal(listener.event, 'notification.received');
    assert.equal(typeof listener.handlerId, 'number');

    emit('notification.received', { id: 1, title: 't', body: null });
    assert.deepEqual(seen, [{ id: 1, title: 't', body: null }]);

    await listener.unregister();
    emit('notification.received', { id: 2, title: 't2', body: null });
    assert.equal(seen.length, 1, '取消订阅之后不再收到');

    clearMocks();
});

test('onNotificationReceived 的 handler 抛出不会中断事件通道', async () => {
    clearMocks();
    const { transport, emit } = makeTransport();
    setTransportOverride(transport);

    /** @type {number[]} */
    const seen = [];
    const originalError = console.error;
    console.error = () => { };

    try {
        await onNotificationReceived(() => {
            throw new Error('boom');
        });
        await onNotificationReceived((value) => seen.push(value.id));

        emit('notification.received', { id: 9, title: 't', body: null });
        assert.deepEqual(seen, [9]);
    } finally {
        console.error = originalError;
        clearMocks();
    }
});

test('onNotificationReceived 对非函数当场拒', async () => {
    clearMocks();
    const { transport } = makeTransport();
    setTransportOverride(transport);

    await assert.rejects(() => onNotificationReceived(/** @type {any} */ ('nope')), TypeError);

    clearMocks();
});

test('onAction 在当前宿主抛 not_supported（不能是永不触发的订阅）', async () => {
    clearMocks();
    const { calls, transport } = makeTransport({ 'notification.actionSupport': false });
    setTransportOverride(transport);

    await assert.rejects(
        () => onAction(() => { }),
        (/** @type {any} */ error) => {
            assert.equal(error.code, 'not_supported');
            assert.match(error.message, /拿不到通知点击/);
            return true;
        });

    assert.deepEqual(calls, [['notification.actionSupport', undefined]]);

    clearMocks();
});

test('onAction 对非函数当场拒（在问宿主之前）', async () => {
    clearMocks();
    const { calls, transport } = makeTransport({ 'notification.actionSupport': false });
    setTransportOverride(transport);

    await assert.rejects(() => onAction(/** @type {any} */ (null)), TypeError);
    assert.deepEqual(calls, []);

    clearMocks();
});
