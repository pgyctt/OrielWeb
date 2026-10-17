// @orielweb/api/dialog 的测试（F3）。
//
// 这个模块的测试重点是**语义与文案的分离**：
//
//   命令层返回的是语义（`ok` / `cancel` / `yes` / `no`），而 `message()` 要把它映射回
//   **文案**（与 Tauri 一致）。之所以不直接让命令层返回文案：Windows 的 MessageBoxW
//   只能显示系统文案，若返回"实际显示的文案"，页面拿它与期望标签比对时会**静默得到
//   错误答案**（用户点了"是"，显示的却是"确定"）。所以这层映射必须被测到。
//
// 运行：node --test packages/api/tests/dialog.test.mjs

import { test } from 'node:test';
import assert from 'node:assert/strict';

import { open, save, message, ask, confirm } from '../src/dialog.js';
import { clearMocks } from '../src/mocks.js';
import { setTransportOverride } from '../src/internal.js';

/**
 * 装一个记录命令的传输层。
 *
 * @param {Record<string, unknown>} [responses]
 * @returns {{ calls: Array<[string, any]>, transport: any }}
 */
function makeTransport(responses = {}) {
    /** @type {Array<[string, any]>} */
    const calls = [];

    return {
        calls,
        transport: {
            async invoke(name, args) {
                calls.push([name, args]);
                return Object.prototype.hasOwnProperty.call(responses, name) ? responses[name] : null;
            },
        },
    };
}

// ---- open ----

test('open 单选时把数组摊平成字符串', async () => {
    clearMocks();
    const { calls, transport } = makeTransport({ 'dialog.open': ['/tmp/a.txt'] });
    setTransportOverride(transport);

    // 命令层统一给数组（返回值形状稳定），摊平在这一层做
    assert.equal(await open({}), '/tmp/a.txt');
    assert.deepEqual(calls[0], ['dialog.open', { options: {} }]);

    clearMocks();
});

test('open 多选时给数组', async () => {
    clearMocks();
    setTransportOverride({ async invoke() { return ['/a', '/b']; } });

    assert.deepEqual(await open({ multiple: true }), ['/a', '/b']);

    clearMocks();
});

test('open 取消时给 null（单选与多选都是）', async () => {
    clearMocks();
    setTransportOverride({ async invoke() { return []; } });

    // 空数组 → null，与 Tauri 一致
    assert.equal(await open({}), null);
    assert.equal(await open({ multiple: true }), null);

    clearMocks();
});

test('open 把 options 原样透传（title / filters / multiple / directory）', async () => {
    clearMocks();
    const { calls, transport } = makeTransport({ 'dialog.open': [] });
    setTransportOverride(transport);

    await open({ title: '选图', multiple: true, filters: [{ name: '图片', extensions: ['png'] }] });

    assert.deepEqual(calls[0][1].options, {
        title: '选图',
        multiple: true,
        filters: [{ name: '图片', extensions: ['png'] }],
    });

    clearMocks();
});

test('open 不传参数时给空 options（Tauri 的默认参数就是空对象）', async () => {
    clearMocks();
    const { calls, transport } = makeTransport({ 'dialog.open': [] });
    setTransportOverride(transport);

    await open();

    assert.deepEqual(calls[0], ['dialog.open', { options: {} }]);

    clearMocks();
});

// ---- save ----

test('save 直接透传返回值（字符串或 null）', async () => {
    clearMocks();
    setTransportOverride({ async invoke() { return '/tmp/out.txt'; } });
    assert.equal(await save({ title: '另存为' }), '/tmp/out.txt');

    clearMocks();
    setTransportOverride({ async invoke() { return null; } });
    assert.equal(await save(), null);

    clearMocks();
});

// ---- message：语义 → 文案 ----

test('message 把语义映射回默认文案', async () => {
    clearMocks();

    const cases = [
        ['ok', '确定'],
        ['cancel', '取消'],
        ['yes', '是'],
        ['no', '否'],
    ];

    for (const [semantic, label] of cases) {
        clearMocks();
        setTransportOverride({ async invoke() { return semantic; } });

        // 命令层给语义，这一层映射成文案——所以调用方能拿它跟自己期望的标签比
        assert.equal(await message('x'), label);
    }

    clearMocks();
});

test('message 把语义映射回**自定义**文案', async () => {
    clearMocks();
    setTransportOverride({ async invoke() { return 'yes'; } });

    const clicked = await message('x', { buttons: { yes: '好', no: '算了' } });

    // 自定义文案是这一层的事，命令层只认语义——这样 Windows 显示系统文案时
    // 判断仍然正确
    assert.equal(clicked, '好');

    clearMocks();
});

test('message 被外部关掉时给 null', async () => {
    clearMocks();
    setTransportOverride({ async invoke() { return null; } });

    assert.equal(await message('x'), null);

    clearMocks();
});

test('message 接受"第二参直接给标题字符串"（Tauri 的写法）', async () => {
    clearMocks();
    const { calls, transport } = makeTransport({ 'dialog.message': 'ok' });
    setTransportOverride(transport);

    await message('正文', '标题');

    assert.equal(calls[0][1].title, '标题');

    clearMocks();
});

test('message 把字符串按钮集合原样发给命令层', async () => {
    clearMocks();
    const { calls, transport } = makeTransport({ 'dialog.message': 'ok' });
    setTransportOverride(transport);

    await message('x', { buttons: 'YesNoCancel' });

    assert.equal(calls[0][1].buttons, 'YesNoCancel');

    clearMocks();
});

test('message 不传 buttons 时给 Ok（单个确定按钮）', async () => {
    clearMocks();
    const { calls, transport } = makeTransport({ 'dialog.message': 'ok' });
    setTransportOverride(transport);

    await message('x');

    assert.equal(calls[0][1].buttons, 'Ok');

    clearMocks();
});

// ---- ask / confirm ----

test('ask 默认用 Yes/No，并在点"是"时为 true', async () => {
    clearMocks();
    const { calls, transport } = makeTransport({ 'dialog.message': 'yes' });
    setTransportOverride(transport);

    assert.equal(await ask('继续吗？'), true);

    // 默认标签与 Tauri 一致（'Yes' / 'No'）——迁移代码里若写了
    // `await ask(msg)` 且没给标签，行为要对得上
    assert.deepEqual(calls[0][1].buttons.yes, 'Yes');
    assert.deepEqual(calls[0][1].buttons.no, 'No');

    clearMocks();
});

test('ask 在点"否"时为 false', async () => {
    clearMocks();
    setTransportOverride({ async invoke() { return 'no'; } });

    assert.equal(await ask('继续吗？'), false);

    clearMocks();
});

test('ask 支持自定义标签，且判断用自定义的那一个', async () => {
    clearMocks();
    setTransportOverride({ async invoke() { return 'yes'; } });

    assert.equal(await ask('x', { okLabel: '好', cancelLabel: '算了' }), true);

    clearMocks();
});

test('confirm 默认用 Ok/Cancel', async () => {
    clearMocks();
    const { calls, transport } = makeTransport({ 'dialog.message': 'ok' });
    setTransportOverride(transport);

    assert.equal(await confirm('确定吗？'), true);
    assert.equal(calls[0][1].buttons.ok, 'Ok');
    assert.equal(calls[0][1].buttons.cancel, 'Cancel');

    clearMocks();
});

test('confirm 在点取消时为 false', async () => {
    clearMocks();
    setTransportOverride({ async invoke() { return 'cancel'; } });

    assert.equal(await confirm('确定吗？'), false);

    clearMocks();
});

test('ask / confirm 在对话框没出结果时为 false（不当成"是"）', async () => {
    clearMocks();
    setTransportOverride({ async invoke() { return null; } });

    // **默认必须是 false**：把"没有结果"当成"用户同意了"会让一个确认框
    // 在用户直接关掉窗口时静默放行
    assert.equal(await ask('x'), false);
    assert.equal(await confirm('x'), false);

    clearMocks();
});

test('模块顶层导入不发命令', async () => {
    clearMocks();
    const { calls, transport } = makeTransport();
    setTransportOverride(transport);

    await new Promise((resolve) => setTimeout(resolve, 0));

    assert.deepEqual(calls, []);
    clearMocks();
});
