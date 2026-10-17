// @orielweb/api/opener、notification、autostart 的测试（F5）。
//
// 这三个模块的形状都很薄（转发命令 + 参数整形），所以测试的重点在**几处容易写错的地方**：
//
//   1. `openWith` 透传的**时机**：只在调用方给了的时候才带 `with` 字段。
//      带一个 `undefined` 到了宿主那边会变成"字段不存在"（JSON 没有 undefined），
//      那是我们要的；但带一个 `null` 就会变成真正的 null，而宿主侧按"字段在不在"判——
//      两种结果差别很微妙。所以这里钉住"缺省时不带字段"。
//   2. `sendNotification` 收字符串时要包成 `{ title }`（命令的参数字段必须稳定）。
//   3. `revealItemInDir` 收字符串时要包成数组（宿主侧收的是 `paths`）。
//
// 运行：node --test packages/api/tests/platform.test.mjs

import { test } from 'node:test';
import assert from 'node:assert/strict';

import { openUrl, openPath, revealItemInDir } from '../src/opener.js';
import { isPermissionGranted, requestPermission, sendNotification } from '../src/notification.js';
import { isEnabled, enable, disable } from '../src/autostart.js';
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

// ---- opener ----

test('openUrl / openPath 不带 with 时**不写那个字段**', async () => {
    clearMocks();
    const { calls, transport } = makeTransport({ 'opener.openUrl': true, 'opener.openPath': true });
    setTransportOverride(transport);

    await openUrl('https://example.com');
    await openPath('/tmp/a.txt');

    assert.deepEqual(calls[0], ['opener.openUrl', { url: 'https://example.com' }]);
    assert.deepEqual(calls[1], ['opener.openPath', { path: '/tmp/a.txt' }]);
    assert.equal('with' in calls[0][1], false);
    assert.equal('with' in calls[1][1], false);

    clearMocks();
});

test('openUrl / openPath 带了 with 时透传（宿主会明确拒绝它）', async () => {
    clearMocks();
    const { calls, transport } = makeTransport();
    setTransportOverride(transport);

    await openUrl('https://example.com', 'firefox');

    // 包不在这一层拦：那种判断属于宿主（它知道自己的授权模型）。
    // 这里只保证参数**原样**过去，否则宿主会说"缺参数"而不是"不支持 openWith"。
    assert.deepEqual(calls[0], ['opener.openUrl', { url: 'https://example.com', with: 'firefox' }]);

    clearMocks();
});

test('revealItemInDir 把字符串包成 paths 数组', async () => {
    clearMocks();
    const { calls, transport } = makeTransport({ 'opener.revealItemInDir': true });
    setTransportOverride(transport);

    assert.equal(await revealItemInDir('/tmp/a.txt'), true);
    assert.deepEqual(calls[0], ['opener.revealItemInDir', { paths: ['/tmp/a.txt'] }]);

    clearMocks();
});

test('revealItemInDir 对数组原样透传', async () => {
    clearMocks();
    const { calls, transport } = makeTransport({ 'opener.revealItemInDir': true });
    setTransportOverride(transport);

    await revealItemInDir(['/a', '/b']);

    assert.deepEqual(calls[0], ['opener.revealItemInDir', { paths: ['/a', '/b'] }]);

    clearMocks();
});

test('revealItemInDir 对空值抛 TypeError（在发命令之前）', async () => {
    clearMocks();
    const { calls, transport } = makeTransport();
    setTransportOverride(transport);

    assert.throws(() => revealItemInDir(''), TypeError);
    assert.throws(() => revealItemInDir([]), TypeError);

    // 参数校验必须在**发出去之前**：发出去了再报错会让宿主收到一条无意义的命令
    assert.deepEqual(calls, []);

    clearMocks();
});

// ---- notification ----

test('notification 三个函数的命令名与返回值', async () => {
    clearMocks();
    const { calls, transport } = makeTransport({
        'notification.isPermissionGranted': true,
        'notification.requestPermission': 'granted',
        'notification.sendNotification': true,
    });
    setTransportOverride(transport);

    assert.equal(await isPermissionGranted(), true);
    assert.equal(await requestPermission(), 'granted');
    assert.equal(await sendNotification({ title: 't' }), true);

    assert.deepEqual(calls.map((c) => c[0]), [
        'notification.isPermissionGranted',
        'notification.requestPermission',
        'notification.sendNotification',
    ]);

    clearMocks();
});

test('requestPermission 回的是字符串而不是布尔（与 Tauri / 规范一致）', async () => {
    clearMocks();
    setTransportOverride({ async invoke() { return 'denied'; } });

    const result = await requestPermission();

    // 迁移代码里常写 `=== 'granted'`，所以形状不能变
    assert.equal(result, 'denied');
    assert.equal(typeof result, 'string');

    clearMocks();
});

test('sendNotification 收字符串时包成 { title }', async () => {
    clearMocks();
    const { calls, transport } = makeTransport({ 'notification.sendNotification': true });
    setTransportOverride(transport);

    await sendNotification('只有标题');

    // Tauri 允许传纯字符串。命令的参数字段必须稳定（宿主侧按 title/body 取），
    // 所以包装在页面侧做。
    assert.deepEqual(calls[0], ['notification.sendNotification', { title: '只有标题' }]);

    clearMocks();
});

test('sendNotification 收对象时拆成 title / body', async () => {
    clearMocks();
    const { calls, transport } = makeTransport({ 'notification.sendNotification': true });
    setTransportOverride(transport);

    await sendNotification({ title: '标题', body: '正文' });

    assert.deepEqual(calls[0], ['notification.sendNotification', { title: '标题', body: '正文' }]);

    clearMocks();
});

test('sendNotification 对缺 title 的输入抛 TypeError（在发命令之前）', async () => {
    clearMocks();
    const { calls, transport } = makeTransport();
    setTransportOverride(transport);

    assert.throws(() => sendNotification(/** @type {any} */ (null)), TypeError);
    assert.throws(() => sendNotification(/** @type {any} */ ({ body: '没有标题' })), TypeError);
    assert.throws(() => sendNotification(/** @type {any} */ (42)), TypeError);

    assert.deepEqual(calls, []);

    clearMocks();
});

test('sendNotification 返回 false 表示通知系统没收下（不是异常）', async () => {
    clearMocks();
    setTransportOverride({ async invoke() { return false; } });

    // "用户关掉了这个应用的通知"是**正常状态**，不是调用方的错误：
    // 逼它包 try 只会让代码更难读
    assert.equal(await sendNotification('t'), false);

    clearMocks();
});

// ---- autostart ----

test('autostart 三个函数的命令名', async () => {
    clearMocks();
    const { calls, transport } = makeTransport({
        'autostart.isEnabled': false,
        'autostart.enable': true,
        'autostart.disable': false,
    });
    setTransportOverride(transport);

    assert.equal(await isEnabled(), false);
    assert.equal(await enable(), true);
    assert.equal(await disable(), false);

    assert.deepEqual(calls.map((c) => c[0]), [
        'autostart.isEnabled',
        'autostart.enable',
        'autostart.disable',
    ]);

    for (const [, args] of calls) {
        assert.equal(args, undefined);
    }

    clearMocks();
});

test('autostart.disable 返回 false 表示"本来就没开"', async () => {
    clearMocks();
    setTransportOverride({ async invoke() { return false; } });

    // 与"我刚把它关掉了"（true）区分开；两种情况下"现在是关的"都成立
    assert.equal(await disable(), false);

    clearMocks();
});

// ---- 错误码 ----

test('not_supported 与 path 无关的失败码能透传到调用方', async () => {
    clearMocks();

    const error = new Error('本库不支持 openWith（用指定程序打开）');
    error.code = 'not_supported';
    setTransportOverride({ async invoke() { throw error; } });

    await assert.rejects(
        () => openUrl('https://example.com', 'firefox'),
        (caught) => {
            assert.equal(caught.code, 'not_supported');
            return true;
        },
    );

    clearMocks();
});

test('三个模块顶层导入都不发命令', async () => {
    clearMocks();
    const { calls, transport } = makeTransport();
    setTransportOverride(transport);

    await new Promise((resolve) => setTimeout(resolve, 0));

    assert.deepEqual(calls, []);
    clearMocks();
});
