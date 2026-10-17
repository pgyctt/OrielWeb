import assert from 'node:assert/strict';
import { test } from 'node:test';

import { UserAttentionType, Window } from '../src/window.js';
import { clearMocks } from '../src/mocks.js';
import { setTransportOverride } from '../src/internal.js';

/**
 * 装一个记录命令并给返回值的传输层。
 *
 * @param {string} label
 * @param {Record<string, any>} [responses]
 */
function makeTransport(label, responses = {}) {
    /** @type {Array<[string, any]>} */
    const calls = [];

    return {
        calls,
        transport: {
            label,
            async invoke(name, args) {
                calls.push([name, args]);
                return Object.prototype.hasOwnProperty.call(responses, name) ? responses[name] : null;
            },
            on() {
                return () => {};
            },
        },
    };
}

test('UserAttentionType 的取值与反向映射与 Tauri 一致', () => {
    // 取值必须对得上 Tauri（1/2）——迁移代码里可能写成 UserAttentionType.Critical，
    // 也可能写成裸的 1，两种都要能工作
    assert.equal(UserAttentionType.Critical, 1);
    assert.equal(UserAttentionType.Informational, 2);

    // 反向映射也要有：Tauri 的枚举对象两个方向都带
    assert.equal(UserAttentionType[1], 'Critical');
    assert.equal(UserAttentionType[2], 'Informational');

    // 冻结：这是常量表，改它会让上面两条断言在别处失效
    assert.ok(Object.isFrozen(UserAttentionType));
});

test('requestUserAttention 把常量翻成宿主的字符串，不传表示取消', async () => {
    clearMocks();
    const { calls, transport } = makeTransport('main', { 'window.requestUserAttention': null });
    setTransportOverride(transport);

    const win = new Window('main');

    await win.requestUserAttention(UserAttentionType.Critical);
    await win.requestUserAttention(UserAttentionType.Informational);
    await win.requestUserAttention();
    await win.requestUserAttention(null);

    assert.deepEqual(calls[0], [
        'window.requestUserAttention', { label: 'main', type: 'critical' },
    ]);
    assert.deepEqual(calls[1], [
        'window.requestUserAttention', { label: 'main', type: 'informational' },
    ]);

    // 不传 / 传 null → 取消（type 为 null），与 Tauri 一致
    assert.equal(calls[2][1].type, null);
    assert.equal(calls[3][1].type, null);

    clearMocks();
});

test('setMaxSize 收与 setSize 同样的形状，null 表示取消上限', async () => {
    clearMocks();
    const { calls, transport } = makeTransport('main', { 'window.setMaxSize': null });
    setTransportOverride(transport);

    const win = new Window('main');

    await win.setMaxSize({ width: 800, height: 600 });
    assert.deepEqual(calls[0][1], { label: 'main', value: { width: 800, height: 600 } });

    // 元组形状（与 setSize 一致）
    await win.setMaxSize([1024, 768]);
    assert.deepEqual(calls[1][1].value, { width: 1024, height: 768 });

    // null → 0/0（宿主侧 0 表示不限制）
    await win.setMaxSize(null);
    assert.deepEqual(calls[2][1].value, { width: 0, height: 0 });

    clearMocks();
});

test('setSizeConstraints 只发给了值的那一侧', async () => {
    clearMocks();
    const { calls, transport } = makeTransport('main', {
        'window.setMinSize': null,
        'window.setMaxSize': null,
    });
    setTransportOverride(transport);

    const win = new Window('main');

    // 只给上限：不该顺带把最小尺寸设成 0（那会把窗口卡成 0 宽）
    await win.setSizeConstraints({ maxWidth: 800, maxHeight: 600 });
    assert.equal(calls.length, 1);
    assert.equal(calls[0][0], 'window.setMaxSize');

    // 两侧都给：两条命令，顺序是先 min 后 max
    await win.setSizeConstraints({ minWidth: 200, minHeight: 150, maxWidth: 800, maxHeight: 600 });
    assert.deepEqual(calls.slice(1).map((call) => call[0]), ['window.setMinSize', 'window.setMaxSize']);

    // 什么都不给：一条命令都不发（不是"发一条把两边清零"）
    const before = calls.length;
    await win.setSizeConstraints({});
    assert.equal(calls.length, before);

    clearMocks();
});
