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

test('per-call timeout：传输层声明支持时透传（不再拒绝）', async () => {
    clearMocks();

    /** @type {any[]} */
    const calls = [];
    setTransportOverride({
        supportsPerCallTimeout: true,
        async invoke(name, args, options) {
            calls.push([name, args, options]);
            return 'ok';
        },
    });

    assert.equal(await invoke('slow.command', { a: 1 }, { timeout: 300000 }), 'ok');
    assert.deepEqual(calls[0], ['slow.command', { a: 1 }, { timeout: 300000 }]);

    clearMocks();
});

test('per-call timeout：传输层**没声明**支持时明确拒绝', async () => {
    clearMocks();

    let called = false;
    setTransportOverride({
        // 刻意不带 supportsPerCallTimeout：模拟老宿主
        async invoke() { called = true; return null; },
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

test('per-call timeout：只有 `=== true` 才算声明支持', async () => {
    clearMocks();

    // 容错写法（`if (transport.supportsPerCallTimeout)`）会让字符串 'true'、数字 1 之类
    // 也被当成"支持"，而那种值只可能来自包装或压缩后的错误产物。
    setTransportOverride({
        supportsPerCallTimeout: /** @type {any} */ ('true'),
        async invoke() { return null; },
    });

    await assert.rejects(() => invoke('x', {}, { timeout: 1 }), /不支持 per-call timeout/);

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

test('convertFileSrc 走桥接（同步，不自己拼一份编码实现）', () => {
    clearMocks();

    // **不在这里自己拼 URL**：那样会出现两份必须严格对称的编码实现
    // （宿主侧的 AssetUrl.FileUrl 与包里的），而它们一旦不一致，
    // 症状是"某些路径就是取不到"——且往往只在特定平台/特定字符下出现。
    const seen = [];
    setTransportOverride({
        convertFileSrc(path) {
            seen.push(path);
            return 'oriel://file.oriel/STUB';
        },
        async invoke() { return null; },
    });

    assert.equal(convertFileSrc('/tmp/a.png'), 'oriel://file.oriel/STUB');
    assert.deepEqual(seen, ['/tmp/a.png']);

    clearMocks();
});

test('convertFileSrc 对空路径与非缺省协议名抛错', () => {
    clearMocks();
    setTransportOverride({
        convertFileSrc: () => 'oriel://file.oriel/x',
        async invoke() { return null; },
    });

    assert.throws(() => convertFileSrc(''), TypeError);
    assert.throws(() => convertFileSrc(/** @type {any} */ (null)), TypeError);

    // 传非缺省协议名要**明确报错**而不是静默忽略——静默的后果是
    // "我在配置里指定了协议名，但页面加载不出来"，而那个现象与协议名无关
    assert.throws(() => convertFileSrc('/tmp/a.png', 'myproto'), /不支持自定义协议名/);

    // 缺省值 'asset'（Tauri 的默认）要接受：迁移时可能照抄了
    assert.equal(convertFileSrc('/tmp/a.png', 'asset'), 'oriel://file.oriel/x');

    clearMocks();
});

test('convertFileSrc 在旧宿主上明确报错（而不是抛个 undefined 调用错）', () => {
    clearMocks();
    // 老宿主没有这个成员
    setTransportOverride({ async invoke() { return null; } });

    assert.throws(() => convertFileSrc('/tmp/a.png'), /当前宿主不支持/);

    clearMocks();
});

// ---- 结构化错误（F7）：code / data 到页面 ----

test('失败时 reject 的 Error 上挂着 code 与 data', async () => {
    clearMocks();

    // 宿主侧造一个带码与数据的错误（真实路径见 bridge.test.mjs：回执里带 code/data）
    const error = new Error('找不到 label 为「nope」的窗口。');
    error.code = 'target_not_found';
    error.data = { label: 'nope' };

    setTransportOverride({
        async invoke() { throw error; },
    });

    await assert.rejects(
        () => invoke('window.title', { label: 'nope' }),
        (caught) => {
            assert.equal(caught.message, '找不到 label 为「nope」的窗口。');
            // 页面按码分支：`not_allowed` 提示权限、`target_not_found` 提示窗口没了……
            // 分不清"命令没授权"与"命令自己炸了"是排障时最费时间的一件事
            assert.equal(caught.code, 'target_not_found');
            assert.deepEqual(caught.data, { label: 'nope' });
            return true;
        },
    );

    clearMocks();
});

test('没给码时 Error 上没有 code 属性（而不是 undefined）', async () => {
    clearMocks();

    // 应用命令抛的异常不带码——那是应用的错误分类，库不该替它编
    setTransportOverride({
        async invoke() { throw new Error('boom'); },
    });

    await assert.rejects(
        () => invoke('todo.fail'),
        (caught) => {
            assert.equal(caught.message, 'boom');
            // `'code' in e` 与 `e.code === undefined` 是两回事；页面写 `if (e.code)` 时
            // 两者都能用，写 `in` 时只有前者对。所以"没给"就该真的没有。
            assert.equal('code' in caught, false);
            return true;
        },
    );

    clearMocks();
});

test('传输层 override 可恢复：clearMocks 之后回到"没有宿主"', async () => {
    clearMocks();
    setTransportOverride({ invoke: async () => 'x' });
    assert.equal(await invoke('a'), 'x');

    clearMocks();
    await assert.rejects(() => invoke('a'), /window\.oriel 不存在/);
});
