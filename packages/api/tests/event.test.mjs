// @orielweb/api/event 的单元测试。
//
// 覆盖的是 `event` 模块与 Tauri 的形状对齐，以及"包装在包内做"这个关键约束：
//   1. listen 退订后不再收到；once 只收一次（且在处理器内自触发也不会被叫第二次）；
//   2. **回调收到的是 { event, id, payload }**，而底层 oriel.on 仍是裸 payload
//      ——两套并存是刻意的，任一侧回归都会让迁移代码或既有页面出问题；
//   3. emit / emitTo 落到 `event.emit` 命令，参数形状与 target 解析正确；
//   4. 参数校验与错误文案（可排查）；
//   5. TauriEvent 常量与 Tauri 同名同值。
//
// 运行：node --test packages/api/tests/event.test.mjs

import { test } from 'node:test';
import assert from 'node:assert/strict';

import { listen, once, emit, emitTo, TauriEvent, __clearEventSubscriptions } from '../src/event.js';
import { mockIPC, mockEmit, clearMocks } from '../src/mocks.js';
import { setTransportOverride, getTransport } from '../src/internal.js';

/** 每个用例前重置：mock + 包内订阅表。 */
function reset() {
    __clearEventSubscriptions();
    clearMocks();
}

test('listen：收到 { event, id, payload } 形状的事件对象', () => {
    reset();
    mockIPC(() => null);

    /** @type {any[]} */
    const received = [];
    listen('my-event', (e) => received.push(e));

    mockEmit('my-event', { n: 1 });

    assert.equal(received.length, 1);
    assert.equal(received[0].event, 'my-event');
    assert.equal(typeof received[0].id, 'number');
    assert.deepEqual(received[0].payload, { n: 1 });

    reset();
});

test('listen：payload 为 undefined 时给 null（与宿主的 args 缺省语义一致）', () => {
    reset();
    mockIPC(() => null);

    /** @type {any[]} */
    const received = [];
    listen('e', (e) => received.push(e));
    mockEmit('e');

    assert.equal(received[0].payload, null);

    reset();
});

test('listen：多个处理器按注册顺序都收到', () => {
    reset();
    mockIPC(() => null);

    /** @type {string[]} */
    const order = [];
    listen('e', () => order.push('a'));
    listen('e', () => order.push('b'));

    mockEmit('e', 1);

    assert.deepEqual(order, ['a', 'b']);

    reset();
});

test('listen：不同事件名互不串台', () => {
    reset();
    mockIPC(() => null);

    /** @type {string[]} */
    const received = [];
    listen('a', () => received.push('a'));
    listen('b', () => received.push('b'));

    mockEmit('a', 1);
    mockEmit('b', 2);

    assert.deepEqual(received, ['a', 'b']);

    reset();
});

test('listen：返回的退订函数是同步函数，调用后不再收到', () => {
    reset();
    mockIPC(() => null);

    let count = 0;
    const unlisten = listen('e', () => { count++; });

    // 同步函数：Tauri 返回 Promise<UnlistenFn>，但 await 一个非 Promise 也成立，
    // 所以迁移代码 `const un = await listen(...)` 照样能跑。
    assert.equal(typeof unlisten, 'function');
    assert.ok(!(unlisten instanceof Promise));

    mockEmit('e', 1);
    unlisten();
    mockEmit('e', 2);

    assert.equal(count, 1, '退订后不该再收到');

    reset();
});

test('listen：退订是幂等的（重复调用不抛）', () => {
    reset();
    mockIPC(() => null);

    const unlisten = listen('e', () => {});
    unlisten();
    assert.doesNotThrow(() => unlisten());

    reset();
});

test('listen：处理器内部退订不影响同一次派发的其它处理器', () => {
    reset();
    mockIPC(() => null);

    /** @type {string[]} */
    const order = [];
    /** @type {() => void} */
    let unlistenA = () => {};
    unlistenA = listen('e', () => {
        order.push('a');
        unlistenA();
    });
    listen('e', () => order.push('b'));

    mockEmit('e', 1);

    // 桥接脚本与 mock 都"复制一份再遍历"，所以 a 退订自己不影响 b
    assert.deepEqual(order, ['a', 'b']);

    reset();
});

test('once：只收到一次，之后自动退订', () => {
    reset();
    mockIPC(() => null);

    let count = 0;
    once('e', () => { count++; });

    mockEmit('e', 1);
    mockEmit('e', 2);
    mockEmit('e', 3);

    assert.equal(count, 1, 'once 只该触发一次');

    reset();
});

test('once：处理器内部再触发同名事件也不会被叫第二次', () => {
    reset();
    mockIPC(() => null);

    let count = 0;
    once('e', () => {
        count++;
        // once 在调用处理器**之前**就退订了，所以这里再发也不会重入
        mockEmit('e', 'again');
    });

    mockEmit('e', 1);

    assert.equal(count, 1);

    reset();
});

test('once：在事件到达前退订可以取消订阅', () => {
    reset();
    mockIPC(() => null);

    let count = 0;
    const unlisten = once('e', () => { count++; });
    unlisten();

    mockEmit('e', 1);

    assert.equal(count, 0);

    reset();
});

test('once 与 listen 订阅同一事件时互不干扰', () => {
    reset();
    mockIPC(() => null);

    let listenCount = 0;
    let onceCount = 0;
    listen('e', () => { listenCount++; });
    once('e', () => { onceCount++; });

    mockEmit('e', 1);
    mockEmit('e', 2);

    assert.equal(listenCount, 2);
    assert.equal(onceCount, 1);

    reset();
});

test('listen：事件 id 递增且互不相同（便于迁移代码在回调里区分）', () => {
    reset();
    mockIPC(() => null);

    /** @type {number[]} */
    const ids = [];
    listen('e', (evt) => ids.push(evt.id));

    mockEmit('e', 1);
    mockEmit('e', 2);

    assert.equal(ids.length, 2);
    assert.ok(ids[1] > ids[0], `id 应递增，实际 ${ids.join(',')}`);

    reset();
});

test('listen：eventName 与 handler 的参数校验', () => {
    reset();
    mockIPC(() => null);

    assert.throws(() => listen('', () => {}), /eventName 必须为非空字符串/);
    assert.throws(() => listen(/** @type {any} */ (null), () => {}), /eventName 必须为非空字符串/);
    assert.throws(() => listen('e', /** @type {any} */ (null)), /handler 必须为函数/);

    reset();
});

test('listen：传输层没有 on() 时给出可排查的报错', () => {
    reset();
    // 只给 invoke，不给 on——模拟"传输层不完整"
    setTransportOverride({ invoke: async () => null });

    assert.throws(
        () => listen('e', () => {}),
        (error) => {
            assert.match(error.message, /没有 on\(\)/);
            assert.match(error.message, /mockIPC/);
            return true;
        },
    );

    reset();
});

test('listen：options.target 目前被忽略，但不报错', () => {
    reset();
    mockIPC(() => null);

    let count = 0;
    // 传了 target 不该改变任何行为——事件本来就只发给本窗口
    assert.doesNotThrow(() => listen('e', () => { count++; }, { target: 'main' }));
    assert.doesNotThrow(() => once('e', () => {}, { target: { label: 'main' } }));

    mockEmit('e', 1);
    assert.equal(count, 1);

    reset();
});

// ---- emit / emitTo ----

test('emit：落到 event.emit 命令，target 为 null（广播）', async () => {
    reset();

    /** @type {any[]} */
    const calls = [];
    mockIPC((cmd, payload) => {
        calls.push([cmd, payload]);
        return null;
    });

    await emit('my-event', { a: 1 });

    assert.equal(calls.length, 1);
    assert.equal(calls[0][0], 'event.emit');
    assert.deepEqual(calls[0][1], { event: 'my-event', payload: { a: 1 }, target: null });

    reset();
});

test('emit：payload 省略时传 null（不传 undefined）', async () => {
    reset();

    /** @type {any[]} */
    const calls = [];
    mockIPC((cmd, payload) => {
        calls.push([cmd, payload]);
        return null;
    });

    await emit('my-event');

    assert.equal(calls[0][1].payload, null);

    reset();
});

test('emitTo：target 为字符串时作为 label 发出去', async () => {
    reset();

    /** @type {any[]} */
    const calls = [];
    mockIPC((cmd, payload) => {
        calls.push([cmd, payload]);
        return null;
    });

    await emitTo('settings', 'my-event', 42);

    assert.deepEqual(calls[0][1], { event: 'my-event', payload: 42, target: 'settings' });

    reset();
});

test('emitTo：target 为 { label } 对象时取 label', async () => {
    reset();

    /** @type {any[]} */
    const calls = [];
    mockIPC((cmd, payload) => {
        calls.push([cmd, payload]);
        return null;
    });

    await emitTo({ label: 'settings' }, 'my-event', 42);

    assert.equal(calls[0][1].target, 'settings');

    reset();
});

test('emitTo：target 不合法时报可排查的错', async () => {
    reset();
    mockIPC(() => null);

    await assert.rejects(
        () => emitTo(/** @type {any} */ (null), 'e'),
        /target 必须是窗口 label/,
    );

    await assert.rejects(
        () => emitTo(/** @type {any} */ ({}), 'e'),
        /target 必须是窗口 label/,
    );

    await assert.rejects(
        () => emitTo(/** @type {any} */ (''), 'e'),
        /target 必须是窗口 label/,
    );

    reset();
});

test('emit / emitTo：eventName 为空时报错', async () => {
    reset();
    mockIPC(() => null);

    await assert.rejects(() => emit(''), /eventName 必须为非空字符串/);
    await assert.rejects(() => emitTo('main', ''), /eventName 必须为非空字符串/);

    reset();
});

test('emit：宿主拒绝（不在能力名单里）时 reject 出宿主文案', async () => {
    reset();
    mockIPC((cmd) => {
        throw new Error(`命令 '${cmd}' 不在 Allow 名单里（deny-by-default：没声明就是没有这个能力）。`);
    });

    await assert.rejects(
        () => emit('e', 1),
        /不在 Allow 名单里/,
    );

    reset();
});

// ---- TauriEvent 常量 ----

test('TauriEvent：与 Tauri 2 同名同值', () => {
    assert.equal(TauriEvent.WINDOW_RESIZED, 'tauri://resize');
    assert.equal(TauriEvent.WINDOW_MOVED, 'tauri://move');
    assert.equal(TauriEvent.WINDOW_CLOSE_REQUESTED, 'tauri://close-requested');
    assert.equal(TauriEvent.WINDOW_DESTROYED, 'tauri://destroyed');
    assert.equal(TauriEvent.WINDOW_FOCUS, 'tauri://focus');
    assert.equal(TauriEvent.WINDOW_BLUR, 'tauri://blur');
    assert.equal(TauriEvent.WINDOW_SCALE_FACTOR_CHANGED, 'tauri://scale-change');
    assert.equal(TauriEvent.WINDOW_THEME_CHANGED, 'tauri://theme-changed');
    assert.equal(TauriEvent.WINDOW_CREATED, 'tauri://window-created');
    assert.equal(TauriEvent.DRAG_ENTER, 'tauri://drag-enter');
    assert.equal(TauriEvent.DRAG_OVER, 'tauri://drag-over');
    assert.equal(TauriEvent.DRAG_DROP, 'tauri://drag-drop');
    assert.equal(TauriEvent.DRAG_LEAVE, 'tauri://drag-leave');
});

test('TauriEvent：是冻结对象（迁移代码不该在上面写入）', () => {
    assert.ok(Object.isFrozen(TauriEvent));
});

// ---- 两套 API 并存 ----

test('包内包装不影响底层 on 的裸 payload 语义（既有页面不受影响）', () => {
    reset();
    mockIPC(() => null);

    /** @type {unknown[]} */
    const rawReceived = [];
    /** @type {any[]} */
    const wrappedReceived = [];

    // 底层通道：直接调 transport.on（等价于既有页面调 window.oriel.on）
    getTransport().on('e', (payload) => rawReceived.push(payload));

    // 包内通道：listen 应当把它包成 { event, id, payload }
    listen('e', (evt) => wrappedReceived.push(evt));

    mockEmit('e', { x: 1 });

    // 同一次派发，两条路径各自拿到自己该拿的形状——这正是"两套并存"的含义
    assert.deepEqual(rawReceived, [{ x: 1 }], '底层必须仍是裸 payload');
    assert.equal(wrappedReceived.length, 1);
    assert.deepEqual(wrappedReceived[0].payload, { x: 1 });
    assert.equal(wrappedReceived[0].event, 'e');

    reset();
});
