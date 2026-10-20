// @orielweb/api/tray 的单元测试（B12）。
//
// 这个模块最容易悄悄错的地方有五类，对应下面五组用例：
//
//   1. **命令名与参数名**：`tray.new` / `tray.setIcon` …… 写错不会报错——宿主拿到一个
//      未知命令就回"未知命令"，但若参数名写错（`iconPath` vs `path`），宿主按默认值走，
//      表现是"图标没变"。
//   2. **线格式**：托盘菜单必须与 `menu.*` 同一套（`kind`/`text`），不能是旧的
//      `type`/`label`——两套格式并存是静默漂移的温床（B12 把 tray 统一到 ParseItems）。
//   3. **action 路由**：带 action 的菜单项在 setMenu 时加 `tray:<token>:<id>` 前缀，
//      `menu.itemclicked` 回来后按前缀认领。前缀写错，回调永远不触发。
//   4. **补壳必须可观测**：`setTempDirPath` / `rid` 是补壳——debug 关时不发 IPC、不报错
//      （零删改），debug 开时必须有警告。没有警告的补壳等于把"不生效"藏起来。
//   5. **平台差异要报错而不是静默**：`setShowMenuOnLeftClick` 在非 Windows 由宿主抛
//      `not_supported`；本层只负责把调用送到正确命令。
//
// 运行：node --test packages/api/tests/tray.test.mjs

import { test, beforeEach } from 'node:test';
import assert from 'node:assert/strict';

import { TrayIcon, __setDebugWarnings } from '../src/tray.js';
import { clearMocks } from '../src/mocks.js';
import { setTransportOverride } from '../src/internal.js';
import { Menu } from '../src/menu.js';
import { Image } from '../src/image.js';

/**
 * 造一个记录调用的传输层。
 *
 * @param {Record<string, unknown>} [responses] 命令名 → 返回值（默认 null；tray.new 返回 't1'）。
 */
function installTransport(responses = {}) {
    /** @type {Array<[string, any]>} */
    const calls = [];
    /** @type {Map<string, Set<(payload: any) => void>>} */
    const listeners = new Map();
    const defaults = { ...responses };

    setTransportOverride({
        label: 'main',
        async invoke(name, args) {
            calls.push([name, args]);
            // tray.new 回显页面给的 id（宿主语义）；没给时回一个随机占位。
            if (name === 'tray.new' && !Object.prototype.hasOwnProperty.call(defaults, name)) {
                return args && typeof args.id === 'string' && args.id !== '' ? args.id : 't1';
            }
            if (Object.prototype.hasOwnProperty.call(defaults, name)) {
                const value = defaults[name];
                return typeof value === 'function' ? value(args) : value;
            }
            return null;
        },
        on(name, handler) {
            let set = listeners.get(name);
            if (!set) {
                set = new Set();
                listeners.set(name, set);
            }
            set.add(handler);
            return () => set.delete(handler);
        },
    });

    return {
        calls,
        emit(name, payload) {
            for (const handler of Array.from(listeners.get(name) ?? [])) {
                handler(payload);
            }
        },
        /** 取某命令的全部调用参数。 */
        argsOf(command) {
            return calls.filter(([name]) => name === command).map(([, args]) => args);
        },
    };
}

beforeEach(() => {
    clearMocks();
    __setDebugWarnings(false);
});

// ---------------------------------------------------------------------------
// 1. 命令名与参数映射
// ---------------------------------------------------------------------------

test('TrayIcon.new 的参数映射与默认值', async () => {
    const { calls } = installTransport();

    const tray = await TrayIcon.new({
        id: 'main-tray',
        tooltip: 'hello',
        title: '42',
        iconAsTemplate: false,
        showMenuOnLeftClick: true,
    });

    assert.equal(tray.id, 'main-tray');
    assert.equal(calls.length, 1);
    const [name, args] = calls[0];
    assert.equal(name, 'tray.new');
    assert.equal(args.id, 'main-tray');
    assert.equal(args.tooltip, 'hello');
    assert.equal(args.title, '42');
    assert.equal(args.iconAsTemplate, false);
    assert.equal(args.menuOnClick, true, 'showMenuOnLeftClick 映射到宿主的 menuOnClick');
});

test('menuOnLeftClick 是 showMenuOnLeftClick 的旧名，两者都认', async () => {
    const a = installTransport();
    await TrayIcon.new({ menuOnLeftClick: true });
    assert.equal(a.calls[0][1].menuOnClick, true);

    clearMocks();
    const b = installTransport();
    await TrayIcon.new({ showMenuOnLeftClick: true });
    assert.equal(b.calls[0][1].menuOnClick, true);
});

test('id 省略时传 null（宿主生成随机 id）', async () => {
    const { calls } = installTransport();
    await TrayIcon.new();
    assert.equal(calls[0][1].id, null);
});

// ---------------------------------------------------------------------------
// 2. setIcon 的 JsImage 三形态（与 window.setIcon 同一套）
// ---------------------------------------------------------------------------

test('setIcon(路径) 走 iconPath', async () => {
    const { argsOf } = installTransport();
    const tray = await TrayIcon.new({ id: 't' });
    await tray.setIcon('icons/a.png');
    assert.deepEqual(argsOf('tray.setIcon')[0], { id: 't', iconPath: 'icons/a.png' });
});

test('setIcon(字节) 走 bytes + base64', async () => {
    const { argsOf } = installTransport();
    const tray = await TrayIcon.new({ id: 't' });
    // 最小 PNG 头（1x1 的真 PNG 被 sniffImageFormat 认出即可）
    const png = new Uint8Array([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0, 0, 0, 0]);
    await tray.setIcon(png);
    const args = argsOf('tray.setIcon')[0];
    assert.equal(args.id, 't');
    assert.equal(typeof args.bytes, 'string');
    assert.equal(args.iconPath, undefined);
});

test('setIcon(Image 实例)：有编码字节送 bytes，裸 RGBA 送 rgba+尺寸', async () => {
    const { argsOf } = installTransport();
    const tray = await TrayIcon.new({ id: 't' });

    const raw = await Image.new(new Uint8Array(4 * 2 * 2).fill(7), 2, 2);
    await tray.setIcon(raw);
    const rgbaArgs = argsOf('tray.setIcon')[0];
    assert.equal(typeof rgbaArgs.rgba, 'string');
    assert.equal(rgbaArgs.width, 2);
    assert.equal(rgbaArgs.height, 2);
});

test('setIcon(null) 清除图标（iconPath: null）', async () => {
    const { argsOf } = installTransport();
    const tray = await TrayIcon.new({ id: 't' });
    await tray.setIcon(null);
    assert.deepEqual(argsOf('tray.setIcon')[0], { id: 't', iconPath: null });
});

test('setIcon(非法值) 当场抛 TypeError，不发 IPC', async () => {
    const { calls } = installTransport();
    const tray = await TrayIcon.new({ id: 't' });
    assert.throws(() => tray.setIcon(42), TypeError);
    assert.equal(calls.filter(([n]) => n === 'tray.setIcon').length, 0);
});

// ---------------------------------------------------------------------------
// 3. 菜单线格式与 action 路由
// ---------------------------------------------------------------------------

test('setMenu 用 menu.* 同一套线格式（kind/text，不是旧的 type/label）', async () => {
    const { argsOf } = installTransport();
    const tray = await TrayIcon.new({ id: 't' });
    const menu = await Menu.new({ items: [{ id: 'quit', text: 'Quit' }] });

    await tray.setMenu(menu);

    const args = argsOf('tray.setMenu')[0];
    assert.equal(args.id, 't');
    assert.deepEqual(args.items, [
        { kind: 'item', id: 'quit', text: 'Quit', enabled: true, accelerator: null },
    ]);
    // 旧线格式的字段必须不存在——它会让宿主的两套解析器各认各的
    assert.equal(args.items[0].label, undefined);
    assert.equal(args.items[0].type, undefined);
});

test('带 action 的菜单项获得 tray:<token>:<id> 前缀，点击回来后按前缀认领', async () => {
    const transport = installTransport();
    const tray = await TrayIcon.new({ id: 't' });

    /** @type {string[]} */
    const clicked = [];
    const menu = await Menu.new({
        items: [{ id: 'quit', text: 'Quit', action: (id) => clicked.push(id) }],
    });
    await tray.setMenu(menu);

    const wireId = transport.argsOf('tray.setMenu')[0].items[0].id;
    assert.match(wireId, /^tray[a-z0-9]+:quit$/, 'id 必须带本托盘的 token 前缀');

    // 模拟宿主点击回传（原样送回带前缀的 id）
    transport.emit('menu.itemclicked', { id: wireId });
    assert.deepEqual(clicked, ['quit'], 'action 必须收到**原始** id');
});

test('action 路由按 token 隔离：别的托盘的点击不会串', async () => {
    const transport = installTransport();
    const a = await TrayIcon.new({ id: 'a' });
    const b = await TrayIcon.new({ id: 'b' });

    /** @type {string[]} */
    const fromA = [];
    /** @type {string[]} */
    const fromB = [];
    await a.setMenu(await Menu.new({ items: [{ id: 'x', text: 'X', action: (id) => fromA.push(id) }] }));
    await b.setMenu(await Menu.new({ items: [{ id: 'x', text: 'X', action: (id) => fromB.push(id) }] }));

    const [wireA, wireB] = transport.argsOf('tray.setMenu').map((args) => args.items[0].id);
    transport.emit('menu.itemclicked', { id: wireA });
    assert.deepEqual(fromA, ['x']);
    assert.deepEqual(fromB, [], '另一个托盘的 action 不得被触发');
});

test('setMenu(null) 清空菜单项', async () => {
    const { argsOf } = installTransport();
    const tray = await TrayIcon.new({ id: 't' });
    await tray.setMenu(null);
    assert.deepEqual(argsOf('tray.setMenu')[0], { id: 't', items: [] });
});

// ---------------------------------------------------------------------------
// 4. 补壳必须可观测
// ---------------------------------------------------------------------------

test('setTempDirPath 是补壳：不发 IPC；debug 开时有警告', async () => {
    const warnings = [];
    const original = console.warn;
    console.warn = (...args) => warnings.push(args.join(' '));

    try {
        const { calls } = installTransport();
        const tray = await TrayIcon.new({ id: 't' });

        await tray.setTempDirPath('/tmp/tray');
        assert.equal(calls.filter(([n]) => n === 'tray.setTempDirPath').length, 0,
            '补壳不得发 IPC');
        assert.equal(warnings.length, 0, 'debug 关闭时静默（零删改）');

        __setDebugWarnings(true);
        await tray.setTempDirPath('/tmp/tray');
        assert.equal(warnings.length, 1, 'debug 开启时必须有警告——补壳必须可观测');
        assert.match(warnings[0], /setTempDirPath/);
    } finally {
        console.warn = original;
        __setDebugWarnings(false);
    }
});

test('rid 是补壳：返回 id；debug 开时有警告', async () => {
    const warnings = [];
    const original = console.warn;
    console.warn = (...args) => warnings.push(args.join(' '));

    try {
        installTransport();
        const tray = await TrayIcon.new({ id: 't7' });
        assert.equal(tray.rid, 't7');
        assert.equal(warnings.length, 0);

        __setDebugWarnings(true);
        assert.equal(tray.rid, 't7');
        assert.equal(warnings.length, 1);
    } finally {
        console.warn = original;
        __setDebugWarnings(false);
    }
});

// ---------------------------------------------------------------------------
// 5. 其余方法的命令映射
// ---------------------------------------------------------------------------

test('setShowMenuOnLeftClick 走 tray.setMenuOnLeftClick（setMenuOnLeftClick 是旧名）', async () => {
    const { argsOf } = installTransport();
    const tray = await TrayIcon.new({ id: 't' });

    await tray.setShowMenuOnLeftClick(false);
    await tray.setMenuOnLeftClick(true);

    const calls = argsOf('tray.setMenuOnLeftClick');
    assert.equal(calls.length, 2);
    assert.deepEqual(calls[0], { id: 't', onLeft: false });
    assert.deepEqual(calls[1], { id: 't', onLeft: true });
});

test('setIconWithAsTemplate 先换图标再改模板标记', async () => {
    const { calls } = installTransport();
    const tray = await TrayIcon.new({ id: 't' });
    await tray.setIconWithAsTemplate('icons/b.png', true);

    const names = calls.map(([n]) => n).filter((n) => n.startsWith('tray.setIcon'));
    assert.deepEqual(names, ['tray.setIcon', 'tray.setIconAsTemplate']);
});

test('setVisible / setTooltip / setTitle 各自的参数名与默认', async () => {
    const { argsOf } = installTransport();
    const tray = await TrayIcon.new({ id: 't' });

    await tray.setVisible(false);
    await tray.setTooltip('tip');
    await tray.setTitle('tt');

    assert.deepEqual(argsOf('tray.setVisible')[0], { id: 't', visible: false });
    assert.deepEqual(argsOf('tray.setTooltip')[0], { id: 't', tooltip: 'tip' });
    assert.deepEqual(argsOf('tray.setTitle')[0], { id: 't', title: 'tt' });
});

test('close / removeById / getById 的命令映射', async () => {
    const transport = installTransport({
        'tray.getById': (args) => (args.id === 't' ? 't' : null),
    });
    const tray = await TrayIcon.new({ id: 't' });

    const again = await TrayIcon.getById('t');
    assert.equal(again.id, 't');

    await tray.close();
    assert.equal(transport.argsOf('tray.removeById')[0].id, 't');

    const missing = await TrayIcon.getById('nope');
    assert.equal(missing, null);
});

test('onEvent 按 id 过滤：别的托盘的事件不送达', async () => {
    const transport = installTransport();
    const tray = await TrayIcon.new({ id: 't' });

    /** @type {any[]} */
    const got = [];
    tray.onEvent((e) => got.push(e));

    transport.emit('tray.event', { id: 'other', type: 'Click' });
    assert.equal(got.length, 0, '别的 id 的事件不得送达');

    transport.emit('tray.event', { id: 't', type: 'Click' });
    assert.equal(got.length, 1);
    assert.equal(got[0].id, 't');
});
