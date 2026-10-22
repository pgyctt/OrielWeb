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
    setTheme,
    BundleType,
    getBundleType,
    defaultWindowIcon,
    setDockVisibility,
    fetchDataStoreIdentifiers,
    removeDataStore,
    onBackButtonPress,
} from '../src/app.js';
import { Image } from '../src/image.js';
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
        'app.getTauriVersion': '0.4.1',
        'app.supportsMultipleWindows': true,
    });
    setTransportOverride(transport);

    assert.equal(await getName(), '示例应用');
    assert.equal(await getVersion(), '1.2.3');
    assert.equal(await getIdentifier(), 'com.example.app');
    assert.equal(await getTauriVersion(), '0.4.1');
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
    const { transport } = makeTransport({ 'app.getTauriVersion': '0.4.1' });
    setTransportOverride(transport);

    const reported = await getTauriVersion();

    // 这不是"实现细节"，是迁移正确性的核心：
    // Tauri 的 getTauriVersion() 给 '2.x.y'（框架版本），本库给 '0.4.1'（库版本）。
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

// ---- setTheme（B15）----

test('setTheme 发 app.setTheme，value 是 light/dark/null 三选一', async () => {
    clearMocks();
    const { calls, transport } = makeTransport();
    setTransportOverride(transport);

    await setTheme('dark');
    await setTheme('light');
    await setTheme(null);
    await setTheme();   // 省略 = null = 跟随系统

    assert.deepEqual(calls.map((c) => c[0]), [
        'app.setTheme', 'app.setTheme', 'app.setTheme', 'app.setTheme',
    ]);

    assert.deepEqual(calls[0][1], { value: 'dark' });
    assert.deepEqual(calls[1][1], { value: 'light' });
    // null 与省略都归一成 null（"跟随系统"），而不是发 undefined——JSON 里 undefined 会消失
    assert.deepEqual(calls[2][1], { value: null });
    assert.deepEqual(calls[3][1], { value: null });

    clearMocks();
});

test('setTheme 收到非法值时抛 TypeError（不发命令）', async () => {
    clearMocks();
    const { calls, transport } = makeTransport();
    setTransportOverride(transport);

    for (const bad of ['blue', 'DARK', 0, {}]) {
        assert.throws(() => setTheme(bad), TypeError, `应当拒绝 ${JSON.stringify(bad)}`);
    }

    // 校验在发命令**之前**，所以一条都不该发出去
    assert.deepEqual(calls, []);

    clearMocks();
});

test('setTheme 未授权时错误码透传（它不在 always-allowed 名单里）', async () => {
    clearMocks();

    const error = new Error("命令 'app.setTheme' 不在能力名单里。");
    error.code = 'not_allowed';
    setTransportOverride({ async invoke() { throw error; } });

    await assert.rejects(
        () => setTheme('dark'),
        (caught) => {
            assert.equal(caught.code, 'not_allowed');
            return true;
        },
    );

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

// ---- B23：产物类型 / 默认图标 / Dock / 数据存储 / 返回键 ----

/** 造一个最小 PNG 头（Image.fromBytes 只读 IHDR 里的宽高）。 */
function pngHeader(width, height) {
    const bytes = new Uint8Array(24);
    bytes.set([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], 0);
    bytes.set([0x49, 0x48, 0x44, 0x52], 12);
    bytes[16] = (width >>> 24) & 0xFF;
    bytes[17] = (width >>> 16) & 0xFF;
    bytes[18] = (width >>> 8) & 0xFF;
    bytes[19] = width & 0xFF;
    bytes[20] = (height >>> 24) & 0xFF;
    bytes[21] = (height >>> 16) & 0xFF;
    bytes[22] = (height >>> 8) & 0xFF;
    bytes[23] = height & 0xFF;
    return bytes;
}

test('BundleType 与 Tauri 逐键同值', () => {
    // 迁移代码会直接写 BundleType.Nsis / BundleType.AppImage；错一个字母就是 undefined，
    // 而 undefined 用来分支时永远走 else——静默走错分支。
    assert.deepEqual(BundleType, {
        Nsis: 'nsis',
        Msi: 'msi',
        Deb: 'deb',
        Rpm: 'rpm',
        AppImage: 'appimage',
        App: 'app',
    });
    assert.ok(Object.isFrozen(BundleType), '常量对象应当冻结，避免被调用方改写');
});

test('getBundleType 原样返回宿主的判断（含 null）', async () => {
    clearMocks();
    const { calls, transport } = makeTransport({ 'app.getBundleType': 'nsis' });
    setTransportOverride(transport);

    assert.equal(await getBundleType(), 'nsis');
    assert.deepEqual(calls.at(-1), ['app.getBundleType', undefined]);

    // 开发期/手工解压：宿主返回 null，页面也拿到 null（不编值）
    const { transport: nullTransport } = makeTransport({ 'app.getBundleType': null });
    setTransportOverride(nullTransport);
    assert.equal(await getBundleType(), null);

    clearMocks();
});

test('defaultWindowIcon：宿主给 base64 时包成 Image，不给时返回 null', async () => {
    clearMocks();
    const png = pngHeader(64, 48);
    const { calls, transport } = makeTransport({
        'app.defaultWindowIcon': Buffer.from(png).toString('base64'),
    });
    setTransportOverride(transport);

    const icon = await defaultWindowIcon();
    assert.ok(icon instanceof Image, '应当返回 Image 实例');
    assert.deepEqual(await icon.size(), { width: 64, height: 48 });
    assert.deepEqual(calls.at(-1), ['app.defaultWindowIcon', undefined]);

    const { transport: emptyTransport } = makeTransport({ 'app.defaultWindowIcon': null });
    setTransportOverride(emptyTransport);
    assert.equal(await defaultWindowIcon(), null);

    clearMocks();
});

test('setDockVisibility 发 { value }，非布尔当场拒', async () => {
    clearMocks();
    const { calls, transport } = makeTransport();
    setTransportOverride(transport);

    await setDockVisibility(false);
    assert.deepEqual(calls.at(-1), ['app.setDockVisibility', { value: false }]);

    await setDockVisibility(true);
    assert.deepEqual(calls.at(-1), ['app.setDockVisibility', { value: true }]);

    assert.throws(() => setDockVisibility(/** @type {any} */ ('false')), TypeError);
    assert.equal(calls.length, 2, '被拒的调用不该发出命令');

    clearMocks();
});

test('fetchDataStoreIdentifiers 把 base64 解成 16 个整数的数组', async () => {
    clearMocks();
    const first = Uint8Array.from({ length: 16 }, (_, index) => index);
    const second = Uint8Array.from({ length: 16 }, () => 0xFF);
    const { calls, transport } = makeTransport({
        'app.fetchDataStoreIdentifiers': [
            Buffer.from(first).toString('base64'),
            Buffer.from(second).toString('base64'),
        ],
    });
    setTransportOverride(transport);

    const identifiers = await fetchDataStoreIdentifiers();

    assert.deepEqual(calls.at(-1), ['app.fetchDataStoreIdentifiers', undefined]);
    assert.equal(identifiers.length, 2);

    // Tauri 的形状是"16 个数字的元组"，不是 Uint8Array——`id[0]` 与 JSON 化都要对得上
    assert.ok(Array.isArray(identifiers[0]), '每个标识符应当是数组而不是 Uint8Array');
    assert.equal(identifiers[0].length, 16);
    assert.deepEqual(identifiers[0], [...first]);
    assert.equal(identifiers[1][15], 0xFF);

    clearMocks();
});

test('removeDataStore 收 16 项数组，逐项校验 0–255', async () => {
    clearMocks();
    const { calls, transport } = makeTransport();
    setTransportOverride(transport);

    const identifier = /** @type {any} */ (Array.from({ length: 16 }, (_, index) => index));
    await removeDataStore(identifier);

    assert.deepEqual(calls.at(-1), [
        'app.removeDataStore',
        { value: Buffer.from(identifier).toString('base64') },
    ]);

    // Uint8Array 也接受（同一种东西的另一种常见持有形态）
    await removeDataStore(/** @type {any} */ (Uint8Array.from(identifier)));
    assert.equal(calls.length, 2);

    // 长度与取值范围都要拒：`300` 在 Uint8Array 里会回绕成 44，
    // 那样删掉的是另一个存储——不可逆。
    assert.throws(() => removeDataStore(/** @type {any} */ ([1, 2, 3])), /16 个整数/);
    assert.throws(() => removeDataStore(/** @type {any} */ ('AAAA')), /16 个整数/);
    assert.throws(
        () => removeDataStore(/** @type {any} */ ([...identifier.slice(1), 300])),
        /0–255/);

    assert.equal(calls.length, 2, '被拒的调用不该发出命令');
    clearMocks();
});

test('onBackButtonPress 返回 { unregister }，桌面端回调永不触发', async () => {
    clearMocks();
    const { transport } = makeTransport();
    // listen 需要传输层的 on()（桥接脚本提供）；这里给一个什么都不做的订阅入口。
    setTransportOverride({ ...transport, on: () => () => {} });

    let called = 0;
    const listener = await onBackButtonPress(() => { called += 1; });

    assert.equal(typeof listener.unregister, 'function');
    assert.equal(called, 0);

    // 宿主没有发出 back-button（桌面端本来也没有这个事件），退订也不该抛
    await listener.unregister();

    // async 函数里抛的错是"被拒的 Promise"，不是同步异常
    await assert.rejects(() => onBackButtonPress(/** @type {any} */ (null)), TypeError);

    clearMocks();
});
