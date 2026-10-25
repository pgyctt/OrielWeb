// mocks 模块自身的用例：2026-10-25 评审发现它的行为只被其它测试间接经过，
// 而 mockWindows 曾是死占位（报错文案还把用户引向它）。这里直接钉住它的承诺。
import assert from 'node:assert/strict';
import { test } from 'node:test';

import {
    clearMocks, mockIPC, mockWindows, mockEmit, mockConvertFileSrc, getMockWindowLabels, isMocked,
} from '../src/mocks.js';
import { getCurrentWindow } from '../src/window.js';
import { convertFileSrc } from '../src/core.js';
import { getTransport } from '../src/internal.js';

test('mockWindows 的第一个 label 决定 getCurrentWindow 的句柄', async () => {
    clearMocks();
    mockWindows('settings', 'main');
    mockIPC(() => null);

    const win = getCurrentWindow();
    assert.equal(win.label, 'settings');

    clearMocks();
});

test('mockWindows 在 mockIPC 之后调用同样生效（getter 而非快照）', async () => {
    clearMocks();
    mockIPC(() => null);
    mockWindows('editor');

    // 如果 mockIPC 装的是 label 快照，这里会是默认 'main'——那正是修掉的陷阱：
    // 测试里常见顺序是先 mockIPC 再补窗口设置，快照值会静默不生效。
    assert.equal(getCurrentWindow().label, 'editor');

    clearMocks();
});

test('mockIPC 的 transport 有 on()，事件路径在纯 mock 下可用', async () => {
    clearMocks();
    mockIPC(() => null);

    assert.equal(typeof getTransport().on, 'function');

    clearMocks();
});

test('mockConvertFileSrc：未登记时 convertFileSrc 如实报不支持，登记后返回 oriel:// URL', async () => {
    clearMocks();
    mockIPC(() => null);

    // 未登记 = 模拟旧宿主：core 侧应拿到明确的"不支持"而不是静默错误
    assert.throws(() => convertFileSrc('C:/tmp/a b.png'), /不支持/);

    mockConvertFileSrc();
    assert.equal(
        convertFileSrc('C:/tmp/a b.png'),
        'oriel://file.oriel/C%3A%2Ftmp%2Fa%20b.png');

    clearMocks();
});

test('clearMocks 重置 label 与协议登记', async () => {
    clearMocks();
    mockWindows('x');
    mockConvertFileSrc();
    mockIPC(() => null);
    clearMocks();

    assert.deepEqual(getMockWindowLabels(), ['main']);
    mockIPC(() => null);
    assert.equal(getCurrentWindow().label, 'main');
    assert.throws(() => convertFileSrc('C:/x'), /不支持/);

    clearMocks();
});

test('clearMocks 后 mockEmit 不再派发（订阅表也被清掉）', async () => {
    clearMocks();
    let calls = 0;
    mockIPC(() => null);

    // 经 transport.on 建立的订阅（同 listen 的底层路径）
    const off = getTransport().on('ping', () => { calls += 1; });
    void off;
    mockEmit('ping', null);
    assert.equal(calls, 1);

    clearMocks();
    mockIPC(() => null);
    mockEmit('ping', null);
    assert.equal(calls, 1, 'clearMocks 后上一条用例的订阅不该再被派发');

    clearMocks();
});

test('mockEmit 派发时 payload 缺省成 null（与真实通道一致）', async () => {
    clearMocks();
    let seen = 'unset';
    mockIPC(() => null);
    getTransport().on('evt', (payload) => { seen = payload; });

    mockEmit('evt');
    assert.equal(seen, null, '真实通道缺省 payload 是 null，不是 undefined');

    clearMocks();
});

test('参数校验：mockWindows/mockConvertFileSrc 拒绝空入参', async () => {
    clearMocks();
    assert.throws(() => mockWindows(), /至少给一个/);
    assert.throws(() => mockWindows(''), /非空字符串/);
    assert.throws(() => mockConvertFileSrc(''), /非空字符串/);
    assert.equal(isMocked(), false);

    clearMocks();
});
