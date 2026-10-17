// @orielweb/api/window 的单元测试。
//
// 覆盖三类容易悄悄错的地方：
//   1. **命令名与参数形状**：Tauri 的方法名 ≠ 我们的命令名（setAlwaysOnTop → window.setOnTop、
//      unmaximize → window.restore），映射写错的表现是"调了没反应"或"报未知命令"；
//   2. **PhysicalSize 的换算**：要除以缩放比。不换算的话在 Retina/150% 屏上窗口会大 2 倍；
//   3. **onCloseRequested 的语义**：本库是"页面同意才 destroy"，与 Tauri 的 preventDefault
//      **相反**，所以订阅时要自动打开关闭接管、退订时要关掉。
//
// 运行：node --test packages/api/tests/window.test.mjs

import { test } from 'node:test';
import assert from 'node:assert/strict';

import { Window, getCurrentWindow, getAllWindows, getByLabel, getFocusedWindow } from '../src/window.js';
import { clearMocks } from '../src/mocks.js';
import { setTransportOverride } from '../src/internal.js';

/**
 * 最小的传输层工厂：记录调用并给返回值。
 *
 * @param {string} label
 * @param {Record<string, unknown>} [responses]
 * @returns {{ calls: Array<[string, any]>, transport: any }}
 * @remarks
 * 返回对象而不是把 transport 挂在数组上：挂属性会让 `assert.deepEqual(calls, [])`
 * 因为那个属性而失败——那种失败看起来像"命令被多发了"，其实只是测试自己的噪音。
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

test('getCurrentWindow：读宿主注入的 label，同步返回', () => {
    clearMocks();
    const { calls, transport } = makeTransport('main');
    setTransportOverride(transport);

    const win = getCurrentWindow();
    assert.ok(win instanceof Window);
    assert.equal(win.label, 'main');

    // 同步：不读宿主，所以不该产生任何 IPC
    assert.deepEqual(calls, []);

    clearMocks();
});

test('getCurrentWindow：传输层没有 label 时给出可排查的报错', () => {
    clearMocks();
    setTransportOverride({ async invoke() { return null; }, on: () => () => {} });

    assert.throws(() => getCurrentWindow(), /没有 label/);

    clearMocks();
});

test('Window：label 必须是非空字符串', () => {
    assert.throws(() => new Window(''), /label 必须是非空字符串/);
    assert.throws(() => new Window(/** @type {any} */ (null)), /label 必须是非空字符串/);
});

// ---- 命令名映射（Tauri 名 → 我们的命令名）----

test('close / destroy 发出对应命令并带 label', async () => {
    clearMocks();
    const { calls, transport } = makeTransport('main');
    setTransportOverride(transport);

    const win = new Window('main');
    await win.close();
    await win.destroy();

    assert.deepEqual(calls.map((c) => c[0]), ['window.close', 'window.destroy']);
    assert.deepEqual(calls[0][1], { label: 'main' });
    assert.deepEqual(calls[1][1], { label: 'main' });

    clearMocks();
});

test('unmaximize 映射到 window.restore（Tauri 名与我们不同）', async () => {
    clearMocks();
    const { calls, transport } = makeTransport('main');
    setTransportOverride(transport);

    await new Window('main').unmaximize();

    assert.deepEqual(calls.map((c) => c[0]), ['window.restore']);

    clearMocks();
});

test('setAlwaysOnTop 映射到 window.setOnTop', async () => {
    clearMocks();
    const { calls, transport } = makeTransport('main');
    setTransportOverride(transport);

    await new Window('main').setAlwaysOnTop(true);

    assert.deepEqual(calls[0][0], 'window.setOnTop');
    assert.equal(calls[0][1].value, true);

    clearMocks();
});

test('setFocus 映射到 window.focus', async () => {
    clearMocks();
    const { calls, transport } = makeTransport('main');
    setTransportOverride(transport);

    await new Window('main').setFocus();

    assert.deepEqual(calls.map((c) => c[0]), ['window.focus']);

    clearMocks();
});

test('startDragging 映射到 window.drag', async () => {
    clearMocks();
    const { calls, transport } = makeTransport('main');
    setTransportOverride(transport);

    await new Window('main').startDragging();

    assert.deepEqual(calls.map((c) => c[0]), ['window.drag']);

    clearMocks();
});

test('toggleMaximize / toggleFullscreen / toggleAlwaysOnTop 返回宿主的值', async () => {
    clearMocks();
    const { calls, transport } = makeTransport('main', {
        'window.toggleMaximize': true,
        'window.toggleFullscreen': false,
        'window.toggleOnTop': true,
    });
    setTransportOverride(transport);

    const win = new Window('main');
    assert.equal(await win.toggleMaximize(), true);
    assert.equal(await win.toggleFullscreen(), false);
    assert.equal(await win.toggleAlwaysOnTop(), true);

    clearMocks();
});

test('只读查询：isMaximized / isVisible / scaleFactor', async () => {
    clearMocks();
    const { calls, transport } = makeTransport('main', {
        'window.isMaximized': true,
        'window.isVisible': false,
        'window.scaleFactor': 1.5,
    });
    setTransportOverride(transport);

    const win = new Window('main');
    assert.equal(await win.isMaximized(), true);
    assert.equal(await win.isVisible(), false);
    assert.equal(await win.scaleFactor(), 1.5);

    clearMocks();
});

test('title：无条件放行的只读查询', async () => {
    clearMocks();
    const { calls, transport } = makeTransport('main', { 'window.title': '我的窗口' });
    setTransportOverride(transport);

    assert.equal(await new Window('main').title(), '我的窗口');
    assert.deepEqual(calls[0], ['window.title', { label: 'main' }]);

    clearMocks();
});

test('setTitle 校验参数类型', async () => {
    clearMocks();
    const { calls, transport } = makeTransport('main');
    setTransportOverride(transport);

    await assert.rejects(
        async () => new Window('main').setTitle(/** @type {any} */ (42)),
        /title 必须是字符串/);

    clearMocks();
});

// ---- 几何：PhysicalSize 要换算 ----

test('setSize(LogicalSize)：原样发出，不做换算', async () => {
    clearMocks();
    const { calls, transport } = makeTransport('main', { 'window.scaleFactor': 2 });
    setTransportOverride(transport);

    await new Window('main').setSize({ type: 'Logical', width: 800, height: 600 });

    assert.deepEqual(calls.map((c) => c[0]), ['window.resize']);
    assert.equal(calls[0][1].width, 800);
    assert.equal(calls[0][1].height, 600);

    clearMocks();
});

test('setSize(PhysicalSize)：除以缩放比换算成逻辑尺寸', async () => {
    clearMocks();
    const { calls, transport } = makeTransport('main', { 'window.scaleFactor': 2 });
    setTransportOverride(transport);

    // Retina 上物理 1600x1200 = 逻辑 800x600。不换算的话窗口会大 2 倍。
    await new Window('main').setSize({ type: 'Physical', width: 1600, height: 1200 });

    assert.deepEqual(calls.map((c) => c[0]), ['window.scaleFactor', 'window.resize']);
    assert.equal(calls[1][1].width, 800);
    assert.equal(calls[1][1].height, 600);

    clearMocks();
});

test('setSize(PhysicalSize) 在 150% 缩放下的换算', async () => {
    clearMocks();
    const { calls, transport } = makeTransport('main', { 'window.scaleFactor': 1.5 });
    setTransportOverride(transport);

    await new Window('main').setSize({ type: 'Physical', width: 1200, height: 900 });

    assert.equal(calls[1][1].width, 800);
    assert.equal(calls[1][1].height, 600);

    clearMocks();
});

test('setSize：缩放比取不到时按 1 算（不做换算），而不是让整条失败', async () => {
    clearMocks();
    const { calls, transport } = makeTransport('main', { 'window.scaleFactor': 0 });
    setTransportOverride(transport);

    await new Window('main').setSize({ type: 'Physical', width: 800, height: 600 });

    assert.equal(calls[1][1].width, 800);

    clearMocks();
});

test('setSize：接受裸对象与数字对（Tauri 的宽松度）', async () => {
    clearMocks();
    const { calls, transport } = makeTransport('main');
    setTransportOverride(transport);

    const win = new Window('main');
    await win.setSize({ width: 800, height: 600 });
    await win.setSize([640, 480]);

    assert.equal(calls[0][1].width, 800);
    assert.equal(calls[1][1].width, 640);

    clearMocks();
});

test('setPosition：Physical 换算并对负坐标保留符号', async () => {
    clearMocks();
    const { calls, transport } = makeTransport('main', { 'window.scaleFactor': 2 });
    setTransportOverride(transport);

    await new Window('main').setPosition({ type: 'Physical', x: -1600, y: 400 });

    assert.deepEqual(calls.map((c) => c[0]), ['window.scaleFactor', 'window.moveTo']);
    assert.equal(calls[1][1].x, -800);
    assert.equal(calls[1][1].y, 200);

    clearMocks();
});

test('setMinSize(null)：发 0x0 表示取消限制', async () => {
    clearMocks();
    const { calls, transport } = makeTransport('main');
    setTransportOverride(transport);

    await new Window('main').setMinSize(null);

    assert.deepEqual(calls[0][0], 'window.setMinSize');
    assert.equal(calls[0][1].width, 0);
    assert.equal(calls[0][1].height, 0);

    clearMocks();
});

// ---- 窗口枚举 ----

test('getAllWindows：把 label 列表映射成 Window 数组', async () => {
    clearMocks();
    const { calls, transport } = makeTransport('main', { 'window.getAll': ['main', 'settings'] });
    setTransportOverride(transport);

    const windows = await getAllWindows();

    assert.equal(windows.length, 2);
    assert.ok(windows[0] instanceof Window);
    assert.deepEqual(windows.map((w) => w.label), ['main', 'settings']);

    clearMocks();
});

test('getAllWindows：宿主给非数组时返回空数组而不是崩', async () => {
    clearMocks();
    const { calls, transport } = makeTransport('main', { 'window.getAll': null });
    setTransportOverride(transport);

    assert.deepEqual(await getAllWindows(), []);

    clearMocks();
});

test('getByLabel：找到返回 Window，找不到返回 null（不抛）', async () => {
    clearMocks();
    const { calls, transport } = makeTransport('main', { 'window.getAll': ['main', 'settings'] });
    setTransportOverride(transport);

    const found = await getByLabel('settings');
    assert.ok(found instanceof Window);
    assert.equal(found.label, 'settings');

    assert.equal(await getByLabel('nope'), null);

    clearMocks();
});

test('getByLabel：label 必须是非空字符串', async () => {
    clearMocks();
    const { calls, transport } = makeTransport('main');
    setTransportOverride(transport);

    await assert.rejects(() => getByLabel(''), /label 必须是非空字符串/);

    clearMocks();
});

test('getFocusedWindow：目前恒返回 null（如实标注，不假装有答案）', async () => {
    clearMocks();
    const { calls, transport } = makeTransport('main', { 'window.getAll': ['main'] });
    setTransportOverride(transport);

    assert.equal(await getFocusedWindow(), null);
    // 它不该先问宿主再返回 null——那会让调用方以为"宿主说没有"
    assert.deepEqual(calls, []);

    clearMocks();
});

// ---- 关闭接管 ----

test('onCloseRequested：订阅时自动打开接管，退订时关掉', async () => {
    clearMocks();
    const { calls, transport } = makeTransport('main');
    setTransportOverride(transport);

    const unlisten = await new Window('main').onCloseRequested(() => {});

    // 订阅必须**先**打开接管，否则点关闭会直接关掉、回调永远不来
    assert.deepEqual(calls[0][0], 'window.setCloseIntercept');
    assert.equal(calls[0][1].enabled, true);

    calls.length = 0;
    await unlisten();

    // 退订最后一个监听者时关掉接管：留着它会让窗口在没人处理时点关闭没反应
    assert.deepEqual(calls[0][0], 'window.setCloseIntercept');
    assert.equal(calls[0][1].enabled, false);

    clearMocks();
});

test('onCloseRequested：回调收到的 event.preventDefault 是空操作（与 Tauri 语义相反）', async () => {
    clearMocks();

    /** @type {Array<(payload: unknown) => void>} */
    const rawListeners = [];
    setTransportOverride({
        label: 'main',
        async invoke() { return null; },
        on(name, listener) {
            if (name === 'close-requested') {
                rawListeners.push(listener);
            }

            return () => {};
        },
    });

    let called = 0;
    await new Window('main').onCloseRequested((event) => {
        called++;
        // 这两条都必须不抛：迁移代码会写 preventDefault()，
        // 而本库的"阻止"是"什么都不做"。
        event.preventDefault();
        assert.equal(typeof event.preventDefault, 'function');
    });

    assert.equal(rawListeners.length, 1, '应当订阅宿主侧的 close-requested');
    rawListeners[0](null);
    assert.equal(called, 1);

    clearMocks();
});

test('onCloseRequested：handler 必须是函数', async () => {
    clearMocks();
    const { calls, transport } = makeTransport('main');
    setTransportOverride(transport);

    await assert.rejects(
        () => new Window('main').onCloseRequested(/** @type {any} */ (null)),
        /需要一个函数/);

    clearMocks();
});

test('onCloseRequested：接管打不开时打到 console.error（不静默）', async () => {
    clearMocks();

    const originalError = console.error;
    /** @type {string[]} */
    const logged = [];
    console.error = (/** @type {any} */ ...args) => { logged.push(args.join(' ')); };

    try {
        setTransportOverride({
            label: 'main',
            async invoke(name) {
                if (name === 'window.setCloseIntercept') {
                    throw new Error("命令 'window.setCloseIntercept' 不在 Allow 名单里");
                }

                return null;
            },
            on() { return () => {}; },
        });

        await new Window('main').onCloseRequested(() => {});

        // 静默的后果是"我明明订阅了却收不到"，那是最难排查的一类问题
        assert.equal(logged.length, 1);
        assert.match(logged[0], /无法打开关闭接管/);
        assert.match(logged[0], /window\.setCloseIntercept/);
    } finally {
        console.error = originalError;
        clearMocks();
    }
});

test('onResized / onMoved：都返回 Promise<退订函数>（Tauri 形状）', async () => {
    clearMocks();
    setTransportOverride({
        label: 'main',
        async invoke() { return null; },
        on() { return () => {}; },
    });

    const win = new Window('main');
    const offResized = await win.onResized(() => {});
    const offMoved = await win.onMoved(() => {});

    assert.equal(typeof offResized, 'function');
    assert.equal(typeof offMoved, 'function');

    offResized();
    offMoved();

    clearMocks();
});

test('onFocusChanged：一个回调覆盖 focus 与 blur 两个宿主事件', async () => {
    clearMocks();

    /** @type {Map<string, (payload: unknown) => void>} */
    const raw = new Map();
    setTransportOverride({
        label: 'main',
        async invoke() { return null; },
        on(name, listener) {
            raw.set(name, listener);
            return () => raw.delete(name);
        },
    });

    let count = 0;
    const off = await new Window('main').onFocusChanged(() => { count++; });

    assert.ok(raw.has('focus'), '应当订阅宿主侧的 focus');
    assert.ok(raw.has('blur'), '应当订阅宿主侧的 blur');

    raw.get('focus')?.(null);
    raw.get('blur')?.(null);
    assert.equal(count, 2);

    off();
    assert.equal(raw.size, 0);

    clearMocks();
});
