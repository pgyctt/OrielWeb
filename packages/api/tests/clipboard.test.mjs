// @orielweb/api/clipboard 的测试（F6）。
//
// 重点只有三处：
//   1. `readText` 的 `null` 与空串**必须区分**（剪贴板里可能是图片、文件列表，
//      也可能就是空文本——那是两回事）；
//   2. `writeText` 的 `label` 缺省时**不带那个字段**，给了就透传（宿主会明确拒绝它）；
//   3. 参数校验在**发命令之前**。
//
// 运行：node --test packages/api/tests/clipboard.test.mjs

import { test } from 'node:test';
import assert from 'node:assert/strict';

import { readText, writeText, clear } from '../src/clipboard.js';
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

test('三个命令的命令名与参数', async () => {
    clearMocks();
    const { calls, transport } = makeTransport({
        'clipboard.readText': '内容',
        'clipboard.writeText': true,
        'clipboard.clear': true,
    });
    setTransportOverride(transport);

    assert.equal(await readText(), '内容');
    assert.equal(await writeText('内容'), true);
    assert.equal(await clear(), true);

    assert.deepEqual(calls.map((c) => c[0]), [
        'clipboard.readText',
        'clipboard.writeText',
        'clipboard.clear',
    ]);

    // readText 与 clear 不带参数
    assert.equal(calls[0][1], undefined);
    assert.equal(calls[2][1], undefined);

    clearMocks();
});

test('writeText 缺省时不带 label 字段', async () => {
    clearMocks();
    const { calls, transport } = makeTransport({ 'clipboard.writeText': true });
    setTransportOverride(transport);

    await writeText('x');

    assert.deepEqual(calls[0], ['clipboard.writeText', { text: 'x' }]);
    assert.equal('label' in calls[0][1], false);

    clearMocks();
});

test('writeText 带了 label 就透传（宿主会明确拒绝它）', async () => {
    clearMocks();
    const { calls, transport } = makeTransport({ 'clipboard.writeText': true });
    setTransportOverride(transport);

    // 包不在这一层拦：那种判断属于宿主（它知道哪些平台有多个剪贴板）。
    // 这里只保证参数原样过去，否则宿主会说"缺参数"而不是"不支持 label"。
    await writeText('x', { label: 'PRIMARY' });

    assert.deepEqual(calls[0], ['clipboard.writeText', { text: 'x', label: 'PRIMARY' }]);

    clearMocks();
});

test('readText 的 null 与空串都被原样透传', async () => {
    clearMocks();

    // null = 剪贴板里没有文本（可能是图片、文件列表）
    // ''   = 剪贴板里就是空文本
    // 两者是**不同**的结论，包不能把其中一个归一成另一个
    for (const value of [null, '']) {
        clearMocks();
        setTransportOverride({ async invoke() { return value; } });
        assert.equal(await readText(), value);
    }

    clearMocks();
});

test('writeText 对非字符串抛 TypeError（在发命令之前）', async () => {
    clearMocks();
    const { calls, transport } = makeTransport();
    setTransportOverride(transport);

    assert.throws(() => writeText(/** @type {any} */ (42)), TypeError);
    assert.throws(() => writeText(/** @type {any} */ (null)), TypeError);

    // 校验必须在发出去之前：发出去了再报错会让宿主收到一条无意义的命令
    assert.deepEqual(calls, []);

    clearMocks();
});

test('writeText 接受空串（"清空成空文本"是合法内容）', async () => {
    clearMocks();
    const { calls, transport } = makeTransport({ 'clipboard.writeText': true });
    setTransportOverride(transport);

    assert.equal(await writeText(''), true);
    assert.deepEqual(calls[0], ['clipboard.writeText', { text: '' }]);

    clearMocks();
});

test('writeText 返回 false 表示写不进去（剪贴板被别的进程占着）', async () => {
    clearMocks();
    setTransportOverride({ async invoke() { return false; } });

    // 全局独占资源被占着是**正常状态**，不是调用方的错误：
    // 逼它包 try 只会让代码更难读
    assert.equal(await writeText('x'), false);

    clearMocks();
});

test('not_supported 错误码能透传到调用方', async () => {
    clearMocks();

    const error = new Error('本库不支持 clipboard 的 label');
    error.code = 'not_supported';
    setTransportOverride({ async invoke() { throw error; } });

    await assert.rejects(
        () => writeText('x', { label: 'PRIMARY' }),
        (caught) => {
            assert.equal(caught.code, 'not_supported');
            return true;
        },
    );

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
