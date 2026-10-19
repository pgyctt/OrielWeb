// @orielweb/api/webviewWindow 的测试。
//
// 这个模块只有一件事与 Tauri 不同：**创建是异步的、结果要走 `created`**。
// 所以测试的重点是"这条差异是显式可见的"，而不是"命令名传对了"：
//   * 构造即发起 `window.new`，`await w.created` 才算建好；
//   * 失败时 `created` reject（而不是静默成功），且错误码原样透传；
//   * 不认识的选项**不往下传**（传了不生效比报错更难查）；
//   * `getCurrentWebviewWindow` / `getAllWebviewWindows` **不建窗**。
//
// 运行：node --test packages/api/tests/webviewWindow.test.mjs

import { test } from 'node:test';
import assert from 'node:assert/strict';

import { WebviewWindow, getCurrentWebviewWindow, getAllWebviewWindows } from '../src/webviewWindow.js';
import { clearMocks } from '../src/mocks.js';
import { setTransportOverride } from '../src/internal.js';

/**
 * 装一个记录命令的传输层。
 *
 * @param {Record<string, unknown>} [responses]
 * @param {string} [label]
 * @returns {{ calls: Array<[string, any]>, transport: any }}
 */
function makeTransport(responses = {}, label = 'main') {
    /** @type {Array<[string, any]>} */
    const calls = [];

    return {
        calls,
        transport: {
            label,
            async invoke(name, args) {
                calls.push([name, args]);
                if (Object.prototype.hasOwnProperty.call(responses, name)) {
                    const value = responses[name];
                    if (value instanceof Error) {
                        throw value;
                    }
                    return value;
                }
                return null;
            },
        },
    };
}

test('new WebviewWindow 发 window.new，created 解析后算建好', async () => {
    clearMocks();
    const { calls, transport } = makeTransport();
    setTransportOverride(transport);

    const window = new WebviewWindow('settings', { title: '设置', width: 640, height: 480 });
    await window.created;

    assert.equal(window.label, 'settings', '实例的 label 是构造参数');
    assert.deepEqual(calls, [['window.new', { label: 'settings', title: '设置', width: 640, height: 480 }]]);

    clearMocks();
});

test('不认识的选项不往下传（传了不生效比报错更难查）', async () => {
    clearMocks();
    const { calls, transport } = makeTransport();
    setTransportOverride(transport);

    const window = new WebviewWindow('w2', {
        title: 'T',
        parent: 'main',          // Tauri 有、本库没有
        effects: { blur: true }, // 同上
        unknown: 1,
    });
    await window.created;

    assert.deepEqual(calls[0][1], { label: 'w2', title: 'T' });

    clearMocks();
});

test('WebviewWindow.create 是"构造 + 等结果"的合成，返回已建好的实例', async () => {
    clearMocks();
    const { calls, transport } = makeTransport();
    setTransportOverride(transport);

    const window = await WebviewWindow.create('w3', { url: 'about:blank' });

    assert.equal(window.label, 'w3');
    assert.deepEqual(calls[0], ['window.new', { label: 'w3', url: 'about:blank' }]);

    clearMocks();
});

test('建窗失败时 created reject，错误码原样透传', async () => {
    clearMocks();

    const error = new Error("命令 'window.new' 的目标 label 已存在。");
    error.code = 'invalid_argument';
    setTransportOverride({
        label: 'main',
        async invoke() { throw error; },
    });

    const window = new WebviewWindow('dup');

    await assert.rejects(
        () => window.created,
        (caught) => {
            // 未授权 / label 重复 / 非 UI 线程，都要能在页面侧区分
            assert.equal(caught.code, 'invalid_argument');
            return true;
        },
    );

    clearMocks();
});

test('getCurrentWebviewWindow 不建窗，只包装当前 label', () => {
    clearMocks();
    const { calls, transport } = makeTransport({}, 'main');
    setTransportOverride(transport);

    const window = getCurrentWebviewWindow();

    assert.equal(window.label, 'main');
    assert.ok(window instanceof WebviewWindow);
    assert.deepEqual(calls, [], '取当前窗口不该发任何命令');

    clearMocks();
});

test('getAllWebviewWindows 映射 window.getAll 的结果，不建窗', async () => {
    clearMocks();
    const { calls, transport } = makeTransport({ 'window.getAll': ['main', 'settings'] }, 'main');
    setTransportOverride(transport);

    const windows = await getAllWebviewWindows();

    assert.deepEqual(windows.map((w) => w.label), ['main', 'settings']);
    assert.ok(windows.every((w) => w instanceof WebviewWindow));
    assert.deepEqual(calls.map((c) => c[0]), ['window.getAll'], '只查列表，不该有 window.new');

    clearMocks();
});
