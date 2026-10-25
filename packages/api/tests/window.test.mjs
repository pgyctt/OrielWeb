// @orielweb/api/window 的单元测试。
//
// 覆盖三类容易悄悄错的地方：
//   1. **命令名与参数形状**：Tauri 的方法名 ≠ 我们的命令名（setAlwaysOnTop → window.setOnTop、
//      unmaximize → window.restore），映射写错的表现是"调了没反应"或"报未知命令"；
//   2. **PhysicalSize 的换算**：要除以缩放比。不换算的话在 Retina/150% 屏上窗口会大 2 倍；
//   3. **onCloseRequested 的语义**：与 Tauri 一致——默认关闭，`preventDefault()` 才留下。
//      所以订阅时要自动打开关闭接管、退订时要关掉，handler 跑完后要补上 destroy。
//
// 运行：node --test packages/api/tests/window.test.mjs

import { test } from 'node:test';
import assert from 'node:assert/strict';

import {
    Window,
    getCurrentWindow,
    getAllWindows,
    getByLabel,
    getFocusedWindow,
    currentMonitor,
    primaryMonitor,
    availableMonitors,
    monitorFromPoint,
    cursorPosition,
} from '../src/window.js';
import { LogicalSize, PhysicalPosition, PhysicalSize } from '../src/dpi.js';
import { clearMocks } from '../src/mocks.js';
import { setTransportOverride } from '../src/internal.js';
import { TauriEvent, toHostEventName } from '../src/event.js';

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

// ---- setTheme（B15）----

test('setTheme 发 window.setTheme，value 是 light/dark/null 三选一', async () => {
    clearMocks();
    const { calls, transport } = makeTransport('main');
    setTransportOverride(transport);

    await new Window('main').setTheme('dark');
    await new Window('main').setTheme('light');
    await new Window('main').setTheme(null);
    await new Window('main').setTheme();   // 省略 = null = 跟随系统

    assert.deepEqual(calls.map((c) => c[0]), [
        'window.setTheme', 'window.setTheme', 'window.setTheme', 'window.setTheme',
    ]);

    // 按 label 寻址（与 theme() 一致），null 与省略都归一成 null
    assert.deepEqual(calls[0][1], { label: 'main', value: 'dark' });
    assert.deepEqual(calls[1][1], { label: 'main', value: 'light' });
    assert.deepEqual(calls[2][1], { label: 'main', value: null });
    assert.deepEqual(calls[3][1], { label: 'main', value: null });

    clearMocks();
});

test('setTheme 收到非法值时抛 TypeError（不发命令）', async () => {
    clearMocks();
    const { calls, transport } = makeTransport('main');
    setTransportOverride(transport);

    for (const bad of ['blue', 'DARK', 0, {}]) {
        assert.throws(
            () => new Window('main').setTheme(/** @type {any} */ (bad)),
            TypeError,
            `应当拒绝 ${JSON.stringify(bad)}`);
    }

    assert.deepEqual(calls, []);

    clearMocks();
});

test('theme() 发 window.theme（带 label），返回值原样透传', async () => {
    clearMocks();
    const { calls, transport } = makeTransport('main', { 'window.theme': 'dark' });
    setTransportOverride(transport);

    assert.equal(await new Window('main').theme(), 'dark');
    assert.deepEqual(calls[0], ['window.theme', { label: 'main' }]);

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

test('getFocusedWindow：宿主给 label 时返回 Window，没有焦点窗口时返回 null', async () => {
    clearMocks();

    // 有焦点窗口：宿主给 label，页面据此构句柄
    {
        const { calls, transport } = makeTransport('main', { 'window.getFocused': 'settings' });
        setTransportOverride(transport);

        const focused = await getFocusedWindow();
        assert.ok(focused instanceof Window, '应当返回 Window 句柄而不是裸 label');
        assert.equal(focused.label, 'settings');
        assert.deepEqual(calls[0], ['window.getFocused', undefined]);
    }

    // 没有焦点窗口（用户切到别的程序）：宿主给 null
    {
        const { transport } = makeTransport('main', { 'window.getFocused': null });
        setTransportOverride(transport);

        assert.equal(await getFocusedWindow(), null);
    }

    // 宿主给了空串或非字符串：同样按"没有"处理，而不是构出一个 label 为空的句柄
    {
        const { transport } = makeTransport('main', { 'window.getFocused': '' });
        setTransportOverride(transport);

        assert.equal(await getFocusedWindow(), null);
    }

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

/** 等一轮宏任务：async 监听器的续体排在微任务里，setTimeout 能确保它跑完。 */
const tick = () => new Promise((resolve) => setTimeout(resolve, 0));

test('onCloseRequested：多个订阅者时，只退订一个不关接管', async () => {
    clearMocks();
    const { calls, transport } = makeTransport('main');
    setTransportOverride(transport);

    const win = new Window('main');
    const first = await win.onCloseRequested(() => {});
    const second = await win.onCloseRequested(() => {});

    calls.length = 0;
    await first();

    // 先卸载的那个组件若把接管关掉，另一个的 preventDefault 会静默失效
    assert.deepEqual(calls, []);

    await second();
    assert.deepEqual(calls, [['window.setCloseIntercept', { label: 'main', enabled: false }]]);

    clearMocks();
});

test('onCloseRequested：退订幂等，重复调用不会重复关接管', async () => {
    clearMocks();
    const { calls, transport } = makeTransport('main');
    setTransportOverride(transport);

    const unlisten = await new Window('main').onCloseRequested(() => {});

    calls.length = 0;
    await unlisten();
    await unlisten();

    assert.equal(calls.length, 1, '第二次退订应当是空操作');

    clearMocks();
});

test('onCloseRequested：handler 没调 preventDefault 时默认关闭（与 Tauri 一致）', async () => {
    clearMocks();

    /** @type {Array<(payload: unknown) => void>} */
    const rawListeners = [];
    /** @type {Array<[string, any]>} */
    const calls = [];
    setTransportOverride({
        label: 'main',
        async invoke(name, args) {
            calls.push([name, args]);
            return null;
        },
        on(name, listener) {
            if (name === 'close-requested') {
                rawListeners.push(listener);
            }

            return () => {};
        },
    });

    let called = 0;
    await new Window('main').onCloseRequested(() => { called++; });

    assert.equal(rawListeners.length, 1, '应当订阅宿主侧的 close-requested');

    rawListeners[0](null);
    await tick();

    assert.equal(called, 1);
    // Tauri 的包装就是 `if (!evt.isPreventDefault()) await this.destroy()`
    assert.deepEqual(calls.at(-1), ['window.destroy', { label: 'main' }]);

    clearMocks();
});

test('onCloseRequested：preventDefault 真的阻止关闭', async () => {
    clearMocks();

    /** @type {Array<(payload: unknown) => void>} */
    const rawListeners = [];
    /** @type {Array<[string, any]>} */
    const calls = [];
    setTransportOverride({
        label: 'main',
        async invoke(name, args) {
            calls.push([name, args]);
            return null;
        },
        on(name, listener) {
            if (name === 'close-requested') {
                rawListeners.push(listener);
            }

            return () => {};
        },
    });

    await new Window('main').onCloseRequested((event) => {
        assert.equal(typeof event.preventDefault, 'function');
        event.preventDefault();
    });

    rawListeners[0](null);
    await tick();

    assert.equal(
        calls.some(([name]) => name === 'window.destroy'),
        false,
        'preventDefault 之后不该再发 destroy');

    clearMocks();
});

test('onCloseRequested：异步 handler 里的 preventDefault 也算数', async () => {
    clearMocks();

    // 这条钉的是一个具体的回归：判定若发生在 handler 返回之前，
    // `await 确认框(); event.preventDefault()` 就变成"拦了没用、窗口照样关"。
    /** @type {Array<(payload: unknown) => void>} */
    const rawListeners = [];
    /** @type {Array<[string, any]>} */
    const calls = [];
    setTransportOverride({
        label: 'main',
        async invoke(name, args) {
            calls.push([name, args]);
            return null;
        },
        on(name, listener) {
            if (name === 'close-requested') {
                rawListeners.push(listener);
            }

            return () => {};
        },
    });

    await new Window('main').onCloseRequested(async (event) => {
        await tick();
        event.preventDefault();
    });

    rawListeners[0](null);
    await tick();
    await tick();

    assert.equal(
        calls.some(([name]) => name === 'window.destroy'),
        false,
        'await 之后的 preventDefault 必须仍然有效');

    clearMocks();
});

test('onCloseRequested：handler 抛错时窗口保持打开，且不静默', async () => {
    clearMocks();

    const originalError = console.error;
    /** @type {string[]} */
    const logged = [];
    console.error = (/** @type {any} */ ...args) => { logged.push(args.join(' ')); };

    try {
        /** @type {Array<(payload: unknown) => void>} */
        const rawListeners = [];
        /** @type {Array<[string, any]>} */
        const calls = [];
        setTransportOverride({
            label: 'main',
            async invoke(name, args) {
                calls.push([name, args]);
                return null;
            },
            on(name, listener) {
                if (name === 'close-requested') {
                    rawListeners.push(listener);
                }

                return () => {};
            },
        });

        await new Window('main').onCloseRequested(() => {
            throw new Error('确认框炸了');
        });

        rawListeners[0](null);
        await tick();

        // 与 Tauri 一致：异常中断后续的 destroy。差别只在于不静默。
        assert.equal(calls.some(([name]) => name === 'window.destroy'), false);
        assert.equal(logged.length, 1);
        assert.match(logged[0], /handler 抛错/);
        assert.match(logged[0], /确认框炸了/);
    } finally {
        console.error = originalError;
        clearMocks();
    }
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

// ---- 文件拖放（B8 → B10 补齐四个阶段）----
//
// 这一组的重点是**两层名字不要混**：宿主侧叫 `drag.enter/over/drop/leave`（本库命名法），
// 页面侧看到的仍是 Tauri 的 `tauri://drag-*`。混了的症状是"订阅了但永远收不到"，
// 而它不会报错——正是 B7 里那类"编译器看不见"的问题。

test('onDragDropEvent：订阅四条宿主事件，payload 归一成 Tauri 形状', async () => {
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

    /** @type {Array<{ event: string, payload: any }>} */
    const seen = [];
    const off = await new Window('main').onDragDropEvent((e) => seen.push(e));

    for (const hostName of ['drag.enter', 'drag.over', 'drag.drop', 'drag.leave']) {
        assert.ok(raw.has(hostName), `应当订阅宿主侧的 ${hostName}`);
    }
    assert.ok(!raw.has(TauriEvent.DRAG_DROP), '不该拿 Tauri 名去订阅宿主（宿主不认这个名字）');

    raw.get('drag.enter')?.({ type: 'enter', paths: ['/a'], position: { x: 1, y: 2 } });
    raw.get('drag.over')?.({ type: 'over', position: { x: 3, y: 4 } });
    raw.get('drag.drop')?.({ type: 'drop', paths: ['/a', '/b'], position: { x: 5, y: 6 } });
    raw.get('drag.leave')?.({ type: 'leave' });

    assert.equal(seen.length, 4);

    assert.equal(seen[0].event, TauriEvent.DRAG_ENTER, '页面读 e.event 应当拿到 Tauri 名');
    assert.equal(seen[0].payload.type, 'enter');
    assert.deepEqual(seen[0].payload.paths, ['/a']);
    assert.ok(seen[0].payload.position instanceof PhysicalPosition, 'position 应当是 PhysicalPosition');
    assert.equal(seen[0].payload.position.x, 1);
    assert.equal(seen[0].payload.position.y, 2);

    assert.equal(seen[1].payload.type, 'over');
    assert.equal(seen[1].payload.position.y, 4);
    assert.equal(seen[1].payload.paths, undefined, 'over 不该带 paths（Tauri 也不带）');

    assert.equal(seen[2].payload.type, 'drop');
    assert.deepEqual(seen[2].payload.paths, ['/a', '/b']);
    assert.equal(seen[2].payload.position.x, 5);

    assert.deepEqual(seen[3].payload, { type: 'leave' }, 'leave 只有 type：坐标是占位，不发');

    off();
    assert.equal(raw.size, 0, '退订之后不该留下四条监听（否则每次调用漏四条）');

    clearMocks();
});

test('四个 DRAG_* 都有宿主对应物', () => {
    // 映射表漏一条 = 页面订阅了一个宿主永远不推的名字。这条把"漏了"变成红的。
    assert.equal(toHostEventName(TauriEvent.DRAG_ENTER), 'drag.enter');
    assert.equal(toHostEventName(TauriEvent.DRAG_OVER), 'drag.over');
    assert.equal(toHostEventName(TauriEvent.DRAG_DROP), 'drag.drop');
    assert.equal(toHostEventName(TauriEvent.DRAG_LEAVE), 'drag.leave');
});

// ---- 显示器 ----
//
// 这一组的重点是**形状与单位**，不是"命令名传对了"：
//   * `position` / `size` 必须是 dpi 模块的类实例（Tauri 如此），不是裸对象——
//     裸对象会让 `monitor.size.toLogical(scale)` 在迁移后抛 TypeError；
//   * 键集必须**恰好**是 name / position / size / scaleFactor（Tauri 的 Monitor 就这四个），
//     多一个 `isPrimary` 会让 deepEqual 形状断言无缘无故变红；
//   * 单位是物理像素：150% 屏上 `size.width` 应当是 2880 而不是 1920。

/** 一台 1920×1080 @1x 的主屏 + 一块 2560×1440 @2x 的副屏（副屏在主屏右侧）。 */
const TWO_MONITORS = [
    {
        name: '\\\\.\\DISPLAY1',
        position: { x: 0, y: 0 },
        size: { width: 1920, height: 1080 },
        scaleFactor: 1,
    },
    {
        name: null,
        position: { x: 1920, y: 0 },
        size: { width: 2560, height: 1440 },
        scaleFactor: 2,
    },
];

test('availableMonitors：返回 dpi 类实例，键集与 Tauri 的 Monitor 一致', async () => {
    clearMocks();
    const { calls, transport } = makeTransport('main', {
        'window.availableMonitors': TWO_MONITORS,
    });
    setTransportOverride(transport);

    const monitors = await availableMonitors();

    assert.equal(calls.length, 1);
    assert.equal(calls[0][0], 'window.availableMonitors');

    assert.equal(monitors.length, 2);

    // 键集恰好四个，顺序也按 Tauri 的 Monitor 声明（顺序不影响正确性，但钉住能防止
    // 有人顺手加字段而不改测试）
    assert.deepEqual(Object.keys(monitors[0]), ['name', 'position', 'size', 'scaleFactor']);

    assert.ok(monitors[0].position instanceof PhysicalPosition);
    assert.ok(monitors[0].size instanceof PhysicalSize);
    assert.equal(monitors[0].position.type, 'Physical');
    assert.equal(monitors[0].size.type, 'Physical');

    assert.equal(monitors[0].name, '\\\\.\\DISPLAY1');
    assert.equal(monitors[0].size.width, 1920);
    assert.equal(monitors[0].size.height, 1080);
    assert.equal(monitors[0].scaleFactor, 1);

    // 第二块屏：name 为 null（平台给不出），2x 缩放
    assert.equal(monitors[1].name, null);
    assert.equal(monitors[1].position.x, 1920);
    assert.equal(monitors[1].scaleFactor, 2);

    clearMocks();
});

test('availableMonitors：size 是物理像素，toLogical 按 scaleFactor 换算', async () => {
    clearMocks();
    setTransportOverride(makeTransport('main', { 'window.availableMonitors': TWO_MONITORS }).transport);

    const [, retina] = await availableMonitors();

    // 2560 物理 / 2.0 = 1280 逻辑。这里钉的是"页面按 Tauri 的写法算出来对"：
    // const logical = monitor.size.toLogical(monitor.scaleFactor);
    const logical = retina.size.toLogical(retina.scaleFactor);
    assert.ok(logical instanceof LogicalSize);
    assert.equal(logical.width, 1280);
    assert.equal(logical.height, 720);

    clearMocks();
});

test('availableMonitors：宿主返回非数组时退化为空数组（不抛）', async () => {
    clearMocks();
    setTransportOverride(makeTransport('main', { 'window.availableMonitors': null }).transport);

    assert.deepEqual(await availableMonitors(), []);

    clearMocks();
});

test('currentMonitor：null 原样返回（不造一个假显示器）', async () => {
    clearMocks();
    const { calls, transport } = makeTransport('main', { 'window.currentMonitor': null });
    setTransportOverride(transport);

    assert.equal(await currentMonitor(), null);
    assert.deepEqual(calls, [['window.currentMonitor', undefined]]);

    clearMocks();
});

test('currentMonitor：有值时与 availableMonitors 同形', async () => {
    clearMocks();
    setTransportOverride(
        makeTransport('main', { 'window.currentMonitor': TWO_MONITORS[1] }).transport);

    const monitor = await currentMonitor();
    assert.ok(monitor.size instanceof PhysicalSize);
    assert.equal(monitor.scaleFactor, 2);
    assert.equal(monitor.position.x, 1920);

    clearMocks();
});

test('primaryMonitor：走独立命令（主屏不一定是列表第一项）', async () => {
    clearMocks();
    const { calls, transport } = makeTransport('main', { 'window.primaryMonitor': TWO_MONITORS[0] });
    setTransportOverride(transport);

    const monitor = await primaryMonitor();
    assert.equal(calls[0][0], 'window.primaryMonitor');
    assert.equal(monitor.position.x, 0);
    assert.equal(monitor.position.y, 0);

    clearMocks();
});

test('monitorFromPoint：把 x/y 作为参数发出去，返回值同样包成类实例', async () => {
    clearMocks();
    const { calls, transport } = makeTransport('main', {
        'window.monitorFromPoint': TWO_MONITORS[1],
    });
    setTransportOverride(transport);

    const monitor = await monitorFromPoint(2000.5, 300);
    assert.deepEqual(calls, [['window.monitorFromPoint', { x: 2000.5, y: 300 }]]);
    assert.ok(monitor.position instanceof PhysicalPosition);

    clearMocks();
});

test('monitorFromPoint：点在任何屏幕之外时是 null', async () => {
    clearMocks();
    setTransportOverride(makeTransport('main', { 'window.monitorFromPoint': null }).transport);

    assert.equal(await monitorFromPoint(-9999, -9999), null);

    clearMocks();
});

test('cursorPosition：返回 PhysicalPosition，拿不到时退到 (0,0)', async () => {
    clearMocks();
    const { calls, transport } = makeTransport('main', {
        'window.cursorPosition': { type: 'Physical', x: 640, y: 480 },
    });
    setTransportOverride(transport);

    const position = await cursorPosition();
    assert.equal(calls[0][0], 'window.cursorPosition');
    assert.ok(position instanceof PhysicalPosition);
    assert.equal(position.type, 'Physical');
    assert.equal(position.x, 640);
    assert.equal(position.y, 480);

    clearMocks();

    // 宿主返回 null（命令不可用/平台给不出）时不能抛：光标查询是尽力而为的诊断
    setTransportOverride(makeTransport('main', { 'window.cursorPosition': null }).transport);
    const fallback = await cursorPosition();
    assert.equal(fallback.x, 0);
    assert.equal(fallback.y, 0);

    clearMocks();
});

// ---- B21：光标 ----

test('setCursorGrab / setCursorVisible / setIgnoreCursorEvents 发对应的命令', async () => {
    clearMocks();
    const { calls, transport } = makeTransport('main');
    setTransportOverride(transport);

    const win = new Window('main');
    await win.setCursorGrab(true);
    await win.setCursorVisible(false);
    await win.setIgnoreCursorEvents(true);

    assert.deepEqual(calls, [
        ['window.setCursorGrab', { label: 'main', grab: true }],
        ['window.setCursorVisible', { label: 'main', visible: false }],
        ['window.setIgnoreCursorEvents', { label: 'main', ignore: true }],
    ]);

    clearMocks();
});

test('三个布尔方法的参数必须是真布尔（"false" 字符串要当场拒）', async () => {
    clearMocks();
    const { calls, transport } = makeTransport('main');
    setTransportOverride(transport);

    const win = new Window('main');

    // "false" 是 truthy：静默接受会把"隐藏光标"变成"显示光标"
    await assert.rejects(() => win.setCursorGrab(/** @type {any} */ ('true')), TypeError);
    await assert.rejects(() => win.setCursorVisible(/** @type {any} */ ('false')), TypeError);
    await assert.rejects(() => win.setIgnoreCursorEvents(/** @type {any} */ (1)), TypeError);

    assert.deepEqual(calls, []);
    clearMocks();
});

test('setCursorIcon 发 { icon }，未知形状当场拒且不发 IPC', async () => {
    clearMocks();
    const { calls, transport } = makeTransport('main');
    setTransportOverride(transport);

    const win = new Window('main');
    await win.setCursorIcon('ewResize');

    assert.deepEqual(calls, [['window.setCursorIcon', { label: 'main', icon: 'ewResize' }]]);

    // CSS 叫 pointer，Tauri 叫 hand；这里只认后者（与 Tauri 的联合类型一致）
    await assert.rejects(() => win.setCursorIcon(/** @type {any} */ ('pointer')), TypeError);
    await assert.rejects(() => win.setCursorIcon(/** @type {any} */ (null)), TypeError);

    assert.equal(calls.length, 1, '被拒的调用不该发出命令');

    clearMocks();
});

test('setCursorPosition：逻辑坐标原样发，物理坐标保留给宿主换算', async () => {
    clearMocks();
    const { calls, transport } = makeTransport('main');
    setTransportOverride(transport);

    const win = new Window('main');

    await win.setCursorPosition({ x: 12, y: 34 });
    assert.deepEqual(calls.at(-1), [
        'window.setCursorPosition',
        { label: 'main', position: { type: 'Logical', x: 12, y: 34 } },
    ]);

    // Physical 不在这里换算：宿主知道缩放比，页面侧再除一次会得到两个略有差异的舍入
    await win.setCursorPosition(new PhysicalPosition(120, 340));
    assert.deepEqual(calls.at(-1)[1].position, { type: 'Physical', x: 120, y: 340 });

    // 数组形式（本库既有约定）
    await win.setCursorPosition([7, 8]);
    assert.deepEqual(calls.at(-1)[1].position, { type: 'Logical', x: 7, y: 8 });

    // 不发 scaleFactor 查询：换算由宿主做
    assert.equal(calls.some(([name]) => name === 'window.scaleFactor'), false);

    clearMocks();
});

test('setCursorPosition 对非有限坐标当场拒', async () => {
    clearMocks();
    const { calls, transport } = makeTransport('main');
    setTransportOverride(transport);

    const win = new Window('main');
    await assert.rejects(() => win.setCursorPosition({ x: NaN, y: 0 }), /有限/);
    await assert.rejects(() => win.setCursorPosition({ x: 0 }), /有限|y/);

    assert.deepEqual(calls, []);
    clearMocks();
});

test('startResizeDragging 收八个方向，其余当场拒', async () => {
    clearMocks();
    const { calls, transport } = makeTransport('main');
    setTransportOverride(transport);

    const win = new Window('main');

    for (const direction of [
        'East', 'North', 'NorthEast', 'NorthWest',
        'South', 'SouthEast', 'SouthWest', 'West',
    ]) {
        await win.startResizeDragging(direction);
    }

    assert.equal(calls.length, 8);
    assert.deepEqual(calls[0], ['window.startResizeDragging', { label: 'main', direction: 'East' }]);

    await assert.rejects(() => win.startResizeDragging(/** @type {any} */ ('east')), TypeError);
    await assert.rejects(() => win.startResizeDragging(/** @type {any} */ ('left')), TypeError);

    assert.equal(calls.length, 8, '被拒的调用不该发出命令');
    clearMocks();
});

test('光标命令作用于实例自己的 label', async () => {
    clearMocks();
    const { calls, transport } = makeTransport('main');
    setTransportOverride(transport);

    await new Window('settings').setCursorIcon('wait');

    assert.equal(calls[0][1].label, 'settings');

    clearMocks();
});

// ---- 平台开关 / 特效 / 任务栏（B22）----
//
// 十条平台开关的形状完全一致（`{ label, value }`），逐条断言命令名就够了：
// **映射写错的表现**是"调了没反应"或"报未知命令"，而那在真机上一时看不出来。

test('十条平台开关各自发对命令，且都是 { label, value } 形状', async () => {
    clearMocks();
    const { calls, transport } = makeTransport('main');
    setTransportOverride(transport);

    const win = new Window('main');

    /** @type {Array<[string, (w: Window) => Promise<void>]>} */
    const cases = [
        ['window.setSkipTaskbar', (w) => w.setSkipTaskbar(true)],
        ['window.setVisibleOnAllWorkspaces', (w) => w.setVisibleOnAllWorkspaces(true)],
        ['window.setContentProtected', (w) => w.setContentProtected(true)],
        ['window.setSimpleFullscreen', (w) => w.setSimpleFullscreen(true)],
        ['window.setTitleBarStyle', (w) => w.setTitleBarStyle('overlay')],
        ['window.setShadow', (w) => w.setShadow(false)],
        ['window.setAlwaysOnBottom', (w) => w.setAlwaysOnBottom(true)],
        ['window.setFocusable', (w) => w.setFocusable(false)],
        ['window.setDecorations', (w) => w.setDecorations(false)],
    ];

    for (const [expected, run] of cases) {
        await run(win);
        assert.equal(calls.at(-1)[0], expected);
        assert.equal(calls.at(-1)[1].label, 'main');
        assert.ok('value' in calls.at(-1)[1], `${expected} 应当把参数放在 value 里`);
    }

    // setFullscreenOnMonitor 的参数是坐标（物理像素），形状不同，单独断言
    await win.setFullscreenOnMonitor(new PhysicalPosition(1920, 0));
    assert.equal(calls.at(-1)[0], 'window.setFullscreenOnMonitor');
    assert.equal(calls.at(-1)[1].label, 'main');
    // 逐个字段断言而不是 deepEqual：PhysicalPosition 是类实例，与字面量对象的原型不同，
    // strict deepEqual 会因为原型而失败（那种失败看起来像"值错了"，其实是测试写法的问题）
    // 只发 x/y：这个参数**永远是屏幕物理坐标**，带不带 type 都一样
    // （主机按物理像素找显示器，不做逻辑坐标换算）。
    assert.deepEqual(calls.at(-1)[1].value, { x: 1920, y: 0 });

    await win.setFullscreenOnMonitor([100, 200]);
    assert.deepEqual(calls.at(-1)[1].value, { x: 100, y: 200 });

    clearMocks();
});

test('平台开关的参数类型不对时当场拒，且不发 IPC', async () => {
    clearMocks();
    const { calls, transport } = makeTransport('main');
    setTransportOverride(transport);

    const win = new Window('main');

    assert.throws(() => win.setSkipTaskbar(/** @type {any} */ ('true')), TypeError);
    assert.throws(() => win.setShadow(/** @type {any} */ (1)), TypeError);
    assert.throws(() => win.setDecorations(/** @type {any} */ (null)), TypeError);
    assert.throws(() => win.setTitleBarStyle(/** @type {any} */ ('hidden')), TypeError);

    assert.deepEqual(calls, []);
    clearMocks();
});

test('setEffects 把 { effects, state, radius, color, interactive } 原样交给宿主', async () => {
    clearMocks();
    const { calls, transport } = makeTransport('main');
    setTransportOverride(transport);

    const win = new Window('main');

    await win.setEffects({
        effects: ['mica', 'sidebar'],
        state: 'active',
        radius: 8,
        color: [255, 0, 0, 128],
        interactive: false,
    });

    assert.deepEqual(calls.at(-1), ['window.setEffects', {
        label: 'main',
        value: {
            effects: ['mica', 'sidebar'],
            state: 'active',
            radius: 8,
            color: [255, 0, 0, 128],
            interactive: false,
        },
    }]);

    // 只给 effects 时不该凭空补几个字段（宿主会把"没给"与"给了默认值"当成两件事）
    await win.setEffects({ effects: ['acrylic'] });
    assert.deepEqual(calls.at(-1)[1].value, { effects: ['acrylic'] });

    clearMocks();
});

test('setEffects 的非法输入当场拒：空列表、未知材质、未知 state、负 radius', async () => {
    clearMocks();
    const { calls, transport } = makeTransport('main');
    setTransportOverride(transport);

    const win = new Window('main');

    assert.throws(() => win.setEffects(/** @type {any} */ ({})), /非空数组/);
    assert.throws(() => win.setEffects({ effects: [] }), /非空数组/);
    assert.throws(() => win.setEffects({ effects: ['blurry'] }), /未知的材质/);
    assert.throws(
        () => win.setEffects(/** @type {any} */ ({ effects: ['mica'], state: 'on' })), /未知的 state/);
    assert.throws(() => win.setEffects({ effects: ['mica'], radius: -1 }), /radius/);

    assert.deepEqual(calls, [], '被拒的调用不该发出命令');
    clearMocks();
});

test('clearEffects 不带参数（清一个不存在的状态不该失败）', async () => {
    clearMocks();
    const { calls, transport } = makeTransport('main');
    setTransportOverride(transport);

    await new Window('main').clearEffects();

    assert.deepEqual(calls, [['window.clearEffects', { label: 'main' }]]);
    clearMocks();
});

test('setProgressBar 收对象与状态字符串两种形状', async () => {
    clearMocks();
    const { calls, transport } = makeTransport('main');
    setTransportOverride(transport);

    const win = new Window('main');

    await win.setProgressBar({ status: 'normal', progress: 50 });
    assert.deepEqual(calls.at(-1)[1].value, { status: 'normal', progress: 50 });

    await win.setProgressBar('indeterminate');
    assert.deepEqual(calls.at(-1)[1].value, { status: 'indeterminate' });

    assert.throws(() => win.setProgressBar(/** @type {any} */ ('done')), /未知的 status/);
    assert.throws(
        () => win.setProgressBar({ status: 'normal', progress: /** @type {any} */ ('50') }), /progress/);

    clearMocks();
});

test('setOverlayIcon：不传是清除，路径与字节分别走 path / bytes', async () => {
    clearMocks();
    const { calls, transport } = makeTransport('main');
    setTransportOverride(transport);

    const win = new Window('main');

    await win.setOverlayIcon();
    assert.deepEqual(calls.at(-1), ['window.setOverlayIcon', { label: 'main' }]);

    await win.setOverlayIcon('/tmp/badge.png');
    assert.deepEqual(calls.at(-1)[1].value, { path: '/tmp/badge.png' });

    // 一个最小的 PNG 头就够 assertImageFormat 通过
    const png = new Uint8Array([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
    await win.setOverlayIcon(png);
    assert.equal(typeof calls.at(-1)[1].value.bytes, 'string');

    assert.throws(() => win.setOverlayIcon(/** @type {any} */ (42)), TypeError);
    clearMocks();
});

test('setBadgeCount / setBadgeLabel：不传是清除，数字四舍五入', async () => {
    clearMocks();
    const { calls, transport } = makeTransport('main');
    setTransportOverride(transport);

    const win = new Window('main');

    await win.setBadgeCount();
    assert.deepEqual(calls.at(-1), ['window.setBadgeCount', { label: 'main' }]);

    await win.setBadgeCount(3.6);
    assert.equal(calls.at(-1)[1].value, 4);

    await win.setBadgeLabel('新');
    assert.equal(calls.at(-1)[1].value, '新');

    await win.setBadgeLabel();
    assert.deepEqual(calls.at(-1), ['window.setBadgeLabel', { label: 'main' }]);

    assert.throws(() => win.setBadgeCount(/** @type {any} */ ('5')), TypeError);
    assert.throws(() => win.setBadgeLabel(/** @type {any} */ (5)), TypeError);

    clearMocks();
});

test('B22 的命令都作用于实例自己的 label', async () => {
    clearMocks();
    const { calls, transport } = makeTransport('main');
    setTransportOverride(transport);

    await new Window('settings').setSkipTaskbar(true);

    assert.equal(calls[0][1].label, 'settings');
    clearMocks();
});
