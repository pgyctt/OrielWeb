// @orielweb/api/core 的单元测试（Node 内置 test runner）。
//
// 覆盖四类行为，每一类都对应一个"错了会静默"的具体故障：
//   1. 不在宿主里时给出可排查的报错（而不是 `Cannot read properties of undefined`）；
//   2. 装 mock 后 `invoke` 真的走 mock、参数原样到达；
//   3. per-call timeout 未支持时**明确拒绝**（静默忽略会表现为"30 秒后莫名超时"）；
//   4. `isTauri` 在 mock 下为 true（前端单测应当走真实分支）。
//
// 运行：node --test packages/api/tests/core.test.mjs

import { test } from 'node:test';
import assert from 'node:assert/strict';

import { invoke, isTauri, convertFileSrc } from '../src/core.js';
import { mockIPC, clearMocks } from '../src/mocks.js';
import { setTransportOverride } from '../src/internal.js';

test('没有宿主也没有 mock 时，invoke 给出可排查的报错', async () => {
    clearMocks();

    await assert.rejects(
        () => invoke('todo.list'),
        (error) => {
            assert.match(error.message, /window\.oriel 不存在/);
            // 必须指向解决办法，而不是只说"不存在"
            assert.match(error.message, /mockIPC/);
            return true;
        },
    );
});

test('cmd 必须是非空字符串', async () => {
    clearMocks();
    mockIPC(() => null);

    await assert.rejects(() => invoke(''), /cmd 必须为非空字符串/);
    await assert.rejects(() => invoke(/** @type {any} */ (null)), /cmd 必须为非空字符串/);

    clearMocks();
});

test('装 mock 后 invoke 走 mock，cmd 与 args 原样到达', async () => {
    clearMocks();

    /** @type {Array<[string, unknown]>} */
    const calls = [];
    mockIPC((cmd, payload) => {
        calls.push([cmd, payload]);
        return { ok: true };
    });

    const result = await invoke('todo.add', { text: '买牛奶' });

    assert.deepEqual(result, { ok: true });
    assert.deepEqual(calls, [['todo.add', { text: '买牛奶' }]]);

    clearMocks();
});

test('args 省略时 mock 收到 null（与真实桥接脚本一致）', async () => {
    clearMocks();

    /** @type {unknown[]} */
    const received = [];
    mockIPC((_cmd, payload) => {
        received.push(payload);
        return null;
    });

    await invoke('todo.list');
    assert.deepEqual(received, [null]);

    clearMocks();
});

test('mock 里抛的异常变成 reject，且原样带出文案', async () => {
    clearMocks();
    mockIPC(() => {
        throw new Error('不存在 id=42 的待办事项。');
    });

    await assert.rejects(() => invoke('todo.toggle', { id: 42 }), /不存在 id=42/);

    clearMocks();
});

test('mock 里返回 Promise 时 await 它（不把 Promise 当结果）', async () => {
    clearMocks();
    mockIPC(async (cmd) => {
        await Promise.resolve();
        return `done:${cmd}`;
    });

    assert.equal(await invoke('x'), 'done:x');

    clearMocks();
});

test('per-call timeout 未支持时明确拒绝，不静默忽略', async () => {
    clearMocks();

    let called = false;
    mockIPC(() => {
        called = true;
        return null;
    });

    await assert.rejects(
        () => invoke('slow.command', {}, { timeout: 300000 }),
        (error) => {
            assert.match(error.message, /不支持 per-call timeout/);
            // 必须给出替代方案：只说"不支持"等于把人堵在这里
            assert.match(error.message, /oriel\.timeout/);
            return true;
        },
    );

    // 关键：拒绝发生在发送之前——不能"先发出去再报错"，那等于超时已经设错了
    assert.equal(called, false, '拒绝时必须没有真的发出命令');

    clearMocks();
});

test('timeout 传 undefined/null 时按"没给"处理，正常放行', async () => {
    clearMocks();
    mockIPC(() => 'ok');

    assert.equal(await invoke('a', {}, { timeout: undefined }), 'ok');
    assert.equal(await invoke('a', {}, { timeout: /** @type {any} */ (null) }), 'ok');

    clearMocks();
});

test('isTauri：没有宿主也没有 mock 时为 false', () => {
    clearMocks();
    assert.equal(isTauri(), false);
});

test('isTauri：装 mock 后为 true（单测应走真实分支）', () => {
    clearMocks();
    mockIPC(() => null);

    assert.equal(isTauri(), true);

    clearMocks();
    assert.equal(isTauri(), false);
});

test('isTauri：真实宿主存在时为 true', () => {
    clearMocks();

    const host = { invoke: async () => null };
    const g = /** @type {any} */ (globalThis);
    g.window = { oriel: host };

    try {
        assert.equal(isTauri(), true);
    } finally {
        delete g.window;
    }
});

test('convertFileSrc 尚未实现时必须抛错，而不是返回看似可用的 URL', () => {
    assert.throws(
        () => convertFileSrc('/tmp/a.png'),
        /尚未实现/,
    );
});

test('传输层 override 可恢复：clearMocks 之后回到"没有宿主"', async () => {
    clearMocks();
    setTransportOverride({ invoke: async () => 'x' });
    assert.equal(await invoke('a'), 'x');

    clearMocks();
    await assert.rejects(() => invoke('a'), /window\.oriel 不存在/);
});
