// @orielweb/api/app 的测试。
//
// 这个模块分两类，测试也分两类：
//   * **只读元信息**（五个函数）：重点是契约的两处陷阱——
//     ① `getTauriVersion()` 的语义与 Tauri 不同（那边是**框架**版本，这边是**库**版本），
//     数字放在一起比较没有意义；② 五个函数**都不带参数**（不是发 `null`，也不是发 `{}`）。
//   * **有状态的三条**（exit / show / hide）：重点是它们**不在** always-allowed 名单里——
//     未授权时必须是 `not_allowed`，而不是被当成元信息顺带放行。
//
// 运行：node --test packages/api/tests/app.test.mjs

import { test } from 'node:test';
import assert from 'node:assert/strict';

import {
    getName,
    getVersion,
    getIdentifier,
    getTauriVersion,
    supportsMultipleWindows,
    exit,
    show,
    hide,
} from '../src/app.js';
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

test('五个函数各自发对应命令', async () => {
    clearMocks();
    const { calls, transport } = makeTransport({
        'app.getName': '示例应用',
        'app.getVersion': '1.2.3',
        'app.getIdentifier': 'com.example.app',
        'app.getTauriVersion': '0.4.0',
        'app.supportsMultipleWindows': true,
    });
    setTransportOverride(transport);

    assert.equal(await getName(), '示例应用');
    assert.equal(await getVersion(), '1.2.3');
    assert.equal(await getIdentifier(), 'com.example.app');
    assert.equal(await getTauriVersion(), '0.4.0');
    assert.equal(await supportsMultipleWindows(), true);

    assert.deepEqual(calls.map((c) => c[0]), [
        'app.getName',
        'app.getVersion',
        'app.getIdentifier',
        'app.getTauriVersion',
        'app.supportsMultipleWindows',
    ]);

    clearMocks();
});

test('五个函数都不带参数', async () => {
    clearMocks();
    const { calls, transport } = makeTransport();
    setTransportOverride(transport);

    await getName();
    await getVersion();

    // 这几个命令不需要参数。发一个 `{}` 也无害（宿主侧不读），
    // 但发 `null` 会让宿主侧的 args 是 JsonValueKind.Null——保持"不带"最不容易出错。
    for (const [, args] of calls) {
        assert.equal(args, undefined);
    }

    clearMocks();
});

test('getTauriVersion() 返回的是**库**版本，不是 Tauri 框架版本', async () => {
    clearMocks();
    const { transport } = makeTransport({ 'app.getTauriVersion': '0.4.0' });
    setTransportOverride(transport);

    const reported = await getTauriVersion();

    // 这不是"实现细节"，是迁移正确性的核心：
    // Tauri 的 getTauriVersion() 给 '2.x.y'（框架版本），本库给 '0.4.0'（库版本）。
    // 两者放在一起比较没有意义，而照搬 Tauri 的版本判断会静默地得到错答案。
    //
    // 断言形状：本库版本是 0.x 起的三段式。若哪天有人把它改成"伪装成 Tauri 的 2.x"，
    // 这条会变红——那是有意的，因为伪装会让版本比较得出更荒谬的结论。
    assert.match(reported, /^\d+\.\d+\.\d+$/);
    assert.ok(!reported.startsWith('2.'), '不该伪装成 Tauri 的 2.x 框架版本');

    clearMocks();
});

test('supportsMultipleWindows() 恒为 true（桌面端三平台都支持）', async () => {
    clearMocks();
    const { transport } = makeTransport({ 'app.supportsMultipleWindows': true });
    setTransportOverride(transport);

    assert.equal(await supportsMultipleWindows(), true);

    // 保留这个函数是为了让迁移代码里的 `if (await supportsMultipleWindows())` 分支
    // 能原样编译；桌面端本来就没有 false 的情形，所以这里不做"两种值都测"的造作覆盖。
    clearMocks();
});

// ---- 有状态的三条（exit / show / hide） ----

test('exit / show / hide 各自发对应命令，exit 的 code 原样透传', async () => {
    clearMocks();
    const { calls, transport } = makeTransport();
    setTransportOverride(transport);

    await exit();
    await exit(0);
    await exit(2);
    await show();
    await hide();

    assert.deepEqual(calls.map((c) => c[0]), [
        'app.exit', 'app.exit', 'app.exit', 'app.show', 'app.hide',
    ]);

    // 不传 code 时**不带这个字段**，而不是传 `{ code: undefined }`：
    // JSON 里 undefined 会消失，但对象形状仍然会到宿主侧，读起来容易误解成"给了 null"。
    assert.deepEqual(calls[0][1], {});
    // 0 是合法退出码，不能被"falsy 就丢掉"的写法吞掉——这是最容易被写错的一处。
    assert.deepEqual(calls[1][1], { code: 0 });
    assert.deepEqual(calls[2][1], { code: 2 });

    clearMocks();
});

test('exit / show / hide 未授权时错误码透传（它们不在 always-allowed 名单里）', async () => {
    clearMocks();

    for (const [fn, command] of [[exit, 'app.exit'], [show, 'app.show'], [hide, 'app.hide']]) {
        const error = new Error(`命令 '${command}' 不在能力名单里。`);
        error.code = 'not_allowed';
        setTransportOverride({ async invoke() { throw error; } });

        await assert.rejects(
            () => fn(),
            (caught) => {
                assert.equal(caught.code, 'not_allowed', `${command} 被拒时应当带 not_allowed`);
                return true;
            },
        );
    }

    clearMocks();
});

test('失败时错误码透传到调用方', async () => {
    clearMocks();

    const error = new Error('命令 \'app.getVersion\' 需要一个窗口，但这次调用没有来源窗口。');
    error.code = 'no_source_window';
    setTransportOverride({ async invoke() { throw error; } });

    await assert.rejects(
        () => getVersion(),
        (caught) => {
            assert.equal(caught.code, 'no_source_window');
            return true;
        },
    );

    clearMocks();
});

test('导入本模块不会自动发任何命令', async () => {
    clearMocks();
    const { calls, transport } = makeTransport();
    setTransportOverride(transport);

    // 模块顶层的 import 不该产生副作用——那会让"只想用 getName"的页面
    // 在加载时多打几次 IPC，而失败时还会在控制台留下未捕获的 rejection。
    await new Promise((resolve) => setTimeout(resolve, 0));
    assert.deepEqual(calls, []);

    clearMocks();
});
