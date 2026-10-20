// `Channel`（流式回传，B9）的单元测试。
//
// 钉住的是**调用方看到的形状**——与 Tauri 一致的那部分：
//   * 构造即订阅（先 new 再 invoke，第一条增量可能在 invoke 落地之前就到）；
//   * `onmessage` 可传构造器、也可后赋值；
//   * 当 invoke 参数时序列化成一个字符串（`toJSON`）；
//   * 结束后自动退订，且退订是幂等的。
//
// 宿主那一侧（C# 的 OrielChannel）由 FileDrop… 之外的 OrielChannelTests 覆盖；
// 这条链路两端的接缝就是那个字符串前缀，两边各钉一次。

import { test } from 'node:test';
import assert from 'node:assert/strict';

import { Channel } from '../src/core.js';
import { clearMocks } from '../src/mocks.js';
import { setTransportOverride } from '../src/internal.js';

/**
 * 装一个能记录订阅的传输层。
 *
 * @returns {Map<string, (payload: unknown) => void>} 事件名 → 监听器。
 * @remarks 用真的 `transport.on` 而不是 mock 事件总线：通道订阅的就是**底层通道**，
 * 走 `listen` 只会把 `oriel://...` 的事件名映射再包一层，测不出真实形状。
 */
function installChannelTransport() {
    /** @type {Map<string, (payload: unknown) => void>} */
    const raw = new Map();
    setTransportOverride({
        label: 'main',
        async invoke() { return null; },
        on(name, listener) {
            raw.set(name, listener);
            return () => raw.delete(name);
        },
    });
    return raw;
}

test('Channel：toJSON 产出宿主认识的字符串，id 互不相同', () => {
    clearMocks();
    installChannelTransport();

    const a = new Channel();
    const b = new Channel();

    assert.match(a.toJSON(), /^__ORIEL_CHANNEL__:\d+$/);
    assert.notEqual(a.id, b.id, '两个通道不能拿到同一个 id（否则增量会串台）');

    // 桥接层就是这么发参数的：JSON.stringify 会调用 toJSON
    assert.equal(JSON.stringify({ channel: a }), `{"channel":"${a.toJSON()}"}`);

    a.close();
    b.close();
    clearMocks();
});

test('Channel：构造即订阅，收到增量回调；结束事件自动退订', () => {
    clearMocks();
    const raw = installChannelTransport();

    /** @type {unknown[]} */
    const got = [];
    const channel = new Channel((msg) => got.push(msg));

    assert.ok(raw.has(`oriel://channel/${channel.id}`), '构造时就该订阅数据事件');
    assert.ok(raw.has(`oriel://channel-end/${channel.id}`), '同时订阅结束事件');

    raw.get(`oriel://channel/${channel.id}`)({ index: 0, text: 'hello' });
    raw.get(`oriel://channel/${channel.id}`)({ index: 1, text: 'world' });

    assert.deepEqual(got, [{ index: 0, text: 'hello' }, { index: 1, text: 'world' }]);

    // 宿主在命令结束时发结束事件 → 这里退订，两条监听都摘掉
    raw.get(`oriel://channel-end/${channel.id}`)(null);

    assert.equal(channel.closed, true);
    assert.equal(raw.size, 0, '结束之后不该留下任何监听（否则每次调用都漏一个）');

    clearMocks();
});

test('Channel：onmessage 可替换；close 幂等；关闭后不再回调', () => {
    clearMocks();
    const raw = installChannelTransport();

    const first = [];
    const second = [];
    const channel = new Channel((msg) => first.push(msg));

    channel.onmessage = (msg) => second.push(msg);
    raw.get(`oriel://channel/${channel.id}`)(1);

    assert.deepEqual(first, [], '旧回调不该再被调用');
    assert.deepEqual(second, [1]);

    channel.close();
    channel.close(); // 幂等

    // 关闭之后即使还有残留消息到达，也不该再回调
    channel._deliver(2);
    assert.deepEqual(second, [1]);

    clearMocks();
});

test('Channel：回调抛错不会把后续增量吞掉', () => {
    clearMocks();
    installChannelTransport();

    /** @type {number[]} */
    const got = [];
    const channel = new Channel((msg) => {
        if (msg === 1) {
            throw new Error('回调自己炸了');
        }
        got.push(msg);
    });

    // 实现用 `queueMicrotask(() => { throw err; })` 把错误异步重抛（与 Tauri 一致）。
    // 测试里把 queueMicrotask 换掉，接住那次重抛——否则它会变成 test runner 级别的
    // uncaughtException，把"回调没把后续增量吞掉"这个真结论淹掉。
    const original = globalThis.queueMicrotask;
    /** @type {unknown[]} */
    const rethrown = [];
    globalThis.queueMicrotask = (fn) => {
        try {
            fn();
        } catch (err) {
            rethrown.push(err);
        }
    };

    try {
        channel._deliver(1);
        channel._deliver(2);
    } finally {
        globalThis.queueMicrotask = original;
    }

    assert.deepEqual(got, [2], '一次回调失败不该让后面的增量全部丢失');
    assert.equal(rethrown.length, 1, '错误仍然要露出来（异步重抛），不能被静默吞掉');

    channel.close();
    clearMocks();
});
