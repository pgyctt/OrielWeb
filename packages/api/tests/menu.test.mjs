// @orielweb/api/menu 的单元测试（B6a）。
//
// 这个模块最容易悄悄错的地方有四类，对应下面四组用例：
//
//   1. **线格式**：菜单树要序列化成宿主 `OrielBuiltInMenuCommands.ParseItems` 认识的形状。
//      字段名写错（`label` 而不是 `text`）不会报错——宿主拿到一个空菜单，什么都不显示。
//   2. **action 路由**：Tauri 把 action 挂在 Rust 资源上；本库靠"本次弹出专属 id 前缀 +
//      `menu.itemclicked` 事件"还原。前缀写错或令牌没更新，回调就永远不触发。
//   3. **与 Tauri 的签名一致**：`insert(items, position)` 的顺序、`remove(item)` 收项对象
//      而不是 id、`removeAt` 返回被移除的项。这些错了在 JS 里都不会当场抛。
//   4. **不支持的东西要明说**：`setIcon` / `setAsAppMenu` / `setAsWindowMenu` 必须抛错，
//      不能静默成功——静默成功会让迁移者以为菜单栏已经生效。
//
// 运行：node --test packages/api/tests/menu.test.mjs

import { test, beforeEach } from 'node:test';
import assert from 'node:assert/strict';

import {
    Menu,
    MenuItem,
    Submenu,
    CheckMenuItem,
    IconMenuItem,
    PredefinedMenuItem,
    NativeIcon,
    itemFromKind,
} from '../src/menu.js';
import { clearMocks, mockEmit } from '../src/mocks.js';
import { setTransportOverride } from '../src/internal.js';

/**
 * 造一个记录调用的传输层，并暴露一个"模拟宿主推事件"的入口。
 *
 * @param {Record<string, unknown>} [responses]
 * @returns {{ calls: Array<[string, any]>, emit: (name: string, payload: any) => void }}
 * @remarks
 * `on()` 返回的退订函数是空实现：本模块的监听是**模块级、与页面同寿命**的，
 * 测试不关心退订（`clearMocks()` 会清订阅表）。
 */
function installTransport(responses = {}) {
    /** @type {Array<[string, any]>} */
    const calls = [];
    /** @type {Map<string, Set<(payload: any) => void>>} */
    const listeners = new Map();

    setTransportOverride({
        label: 'main',
        async invoke(name, args) {
            calls.push([name, args]);
            return Object.prototype.hasOwnProperty.call(responses, name) ? responses[name] : null;
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
    };
}

beforeEach(() => {
    clearMocks();
});

// ---------------------------------------------------------------------------
// 1. 线格式
// ---------------------------------------------------------------------------

test('普通菜单项序列化成 { kind, id, text, enabled, accelerator }', async () => {
    const { calls } = installTransport();
    const menu = await Menu.new({
        items: [{ id: 'open', text: 'Open', accelerator: 'CmdOrCtrl+O' }],
    });

    await menu.popup();

    assert.equal(calls.length, 1);
    const [name, args] = calls[0];
    assert.equal(name, 'menu.popup');
    assert.deepEqual(args.items, [
        { kind: 'item', id: 'open', text: 'Open', enabled: true, accelerator: 'CmdOrCtrl+O' },
    ]);
    assert.equal(args.position, null, '没给位置时必须是 null（跟随鼠标），不能是 undefined');
    assert.equal(args.label, null);
});

test('四类菜单项各有自己的 kind，且勾选项带 checked', async () => {
    const { calls } = installTransport();
    const menu = await Menu.new({
        items: [
            { text: 'Edit', items: [
                { item: 'Separator' },
                { id: 'wrap', text: 'Wrap', checked: true },
                { id: 'ico', text: 'Icon', icon: NativeIcon.Folder },
                { id: 'plain', text: 'Plain' },
            ] },
        ],
    });

    await menu.popup();

    const submenu = calls[0][1].items[0];
    assert.equal(submenu.kind, 'submenu');
    assert.deepEqual(submenu.items.map((item) => item.kind),
        ['predefined', 'check', 'icon', 'item']);

    const [sep, check, icon] = submenu.items;
    assert.equal(sep.item, 'Separator');
    assert.equal(sep.text, null, '预定义项没给文本时是 null（让平台填标准文案）');
    assert.equal(check.checked, true);
    assert.equal(icon.text, 'Icon');
});

test('"选项对象"按 item/items/checked 推断类型（与 toItem 的三条规则一致）', async () => {
    // 有 `item` → 预定义；有 `items` → 子菜单；有 `checked` → 勾选项。
    const predefined = await Menu.new({ items: [{ item: 'Quit' }] });
    const { calls } = installTransport();
    await predefined.popup();
    assert.equal(calls[0][1].items[0].kind, 'predefined');
    assert.equal(calls[0][1].items[0].item, 'Quit');
});

test('About 的对象形态被保留（不是被当成 Separator）', async () => {
    const { calls } = installTransport();
    const menu = await Menu.new({
        items: [{ item: { About: { name: 'Demo', version: '1.2.3' } } }],
    });

    await menu.popup();

    const item = calls[0][1].items[0];
    assert.equal(item.kind, 'predefined');
    assert.deepEqual(item.item, { About: { name: 'Demo', version: '1.2.3' } },
        'About 是唯一能带数据的预定义项，序列化时不能被压成字符串');
});

test('传实例与传选项对象等价', async () => {
    const { calls } = installTransport();
    const item = await MenuItem.new({ id: 'a', text: 'A' });
    const menu = await Menu.new({ items: [item, { id: 'b', text: 'B' }] });

    await menu.popup();

    assert.deepEqual(calls[0][1].items.map((i) => i.id), ['a', 'b']);
});

test('把 Menu 当项传入时按子菜单序列化（不丢子项）', async () => {
    // Tauri 的类型签名不允许把 Menu 放进 items，但运行期拦不住。
    // 继承来的默认 `_wireCore` 会把它序列化成 `kind: 'item'` 并**丢掉整棵子项**——
    // 表现是"子菜单弹出来是空的"，而且不报错。宁可按它实际的结构序列化。
    const { calls } = installTransport();
    const inner = await Menu.new({ items: [{ id: 'x', text: 'X' }] });
    const outer = await Menu.new({ items: [inner] });

    await outer.popup();

    const wired = calls[0][1].items[0];
    assert.equal(wired.kind, 'submenu');
    assert.deepEqual(wired.items.map((i) => i.id), ['x']);
});

// ---------------------------------------------------------------------------
// 2. action 路由
// ---------------------------------------------------------------------------

test('popup 后宿主点击 → action 收到原始 id（不是带前缀的那个）', async () => {
    const { calls, emit } = installTransport();
    /** @type {string[]} */
    const clicked = [];

    const menu = await Menu.new({
        items: [{ id: 'copy', text: 'Copy', action: (id) => clicked.push(id) }],
    });

    await menu.popup();

    const wired = calls[0][1].items[0];
    assert.match(wired.id, /^[^:]+:copy$/,
        '带 action 的项要加"本次弹出令牌:"前缀，宿主靠它把点击路由回来');

    // 宿主点击后推事件，payload 里是**加了前缀**的 id
    emit('menu.itemclicked', { id: wired.id });

    assert.deepEqual(clicked, ['copy'], 'action 收到的应当是页面自己写的 id');
});

test('不是本次弹出的 id 不认领（别的窗口/宿主自己的菜单）', async () => {
    const { emit } = installTransport();
    /** @type {string[]} */
    const clicked = [];

    const menu = await Menu.new({
        items: [{ id: 'copy', text: 'Copy', action: (id) => clicked.push(id) }],
    });
    await menu.popup();

    emit('menu.itemclicked', { id: 'some-other-token:copy' });

    assert.deepEqual(clicked, [], '前缀不匹配就说明不是这次弹出的项，不能瞎认领');
});

test('没有 action 的项不改 id（订阅者看到的是应用自己写的名字）', async () => {
    const { calls } = installTransport();
    const menu = await Menu.new({ items: [{ id: 'plain', text: 'Plain' }] });

    await menu.popup();

    assert.equal(calls[0][1].items[0].id, 'plain');
});

test('action 抛错不会炸掉事件分发', async () => {
    const { calls, emit } = installTransport();
    let secondCalled = false;

    const menu = await Menu.new({
        items: [
            { id: 'boom', text: 'Boom', action: () => { throw new Error('故意的'); } },
            { id: 'ok', text: 'Ok', action: () => { secondCalled = true; } },
        ],
    });
    await menu.popup();

    const [boom, ok] = calls[0][1].items;

    const originalError = console.error;
    console.error = () => {};
    try {
        emit('menu.itemclicked', { id: boom.id });
        emit('menu.itemclicked', { id: ok.id });
    } finally {
        console.error = originalError;
    }

    assert.equal(secondCalled, true);
});

// ---------------------------------------------------------------------------
// 3. 与 Tauri 的签名一致
// ---------------------------------------------------------------------------

test('insert 的参数顺序是 (items, position)', async () => {
    const { calls } = installTransport();
    const menu = await Menu.new({ items: [{ id: 'a', text: 'A' }, { id: 'c', text: 'C' }] });

    await menu.insert({ id: 'b', text: 'B' }, 1);
    await menu.popup();

    assert.deepEqual(calls[0][1].items.map((i) => i.id), ['a', 'b', 'c']);
});

test('remove 收项对象（按身份），不是 id', async () => {
    const { calls } = installTransport();
    const keep = await MenuItem.new({ id: 'keep', text: 'Keep' });
    const drop = await MenuItem.new({ id: 'drop', text: 'Drop' });
    const menu = await Menu.new({ items: [keep, drop] });

    await menu.remove(drop);
    await menu.remove('keep'); // 传字符串不该命中——Tauri 也不按 id 删
    await menu.popup();

    assert.deepEqual(calls[0][1].items.map((i) => i.id), ['keep']);
});

test('removeAt 返回被移除的项；越界返回 null', async () => {
    const menu = await Menu.new({ items: [{ id: 'a', text: 'A' }] });

    const removed = await menu.removeAt(0);
    assert.equal(removed.id, 'a');

    assert.equal(await menu.removeAt(0), null, '已经空了，越界应当返回 null 而不是抛');
});

test('get 只找直接子项，不递归', async () => {
    const menu = await Menu.new({
        items: [{ text: 'Sub', items: [{ id: 'inner', text: 'Inner' }] }],
    });

    assert.equal(await menu.get('inner'), null, 'get 不递归进子菜单');
    assert.notEqual(await menu.get(''), null, '子菜单自己没有 id，空串能命中它');
});

test('setAccelerator(null) 清除加速键', async () => {
    const { calls } = installTransport();
    const item = await MenuItem.new({ id: 'a', text: 'A', accelerator: 'Ctrl+A' });
    await item.setAccelerator(null);

    const menu = await Menu.new({ items: [item] });
    await menu.popup();

    assert.equal(calls[0][1].items[0].accelerator, null);
});

test('Menu.default() 造出 Edit / Window / Help 三组', async () => {
    const { calls } = installTransport();
    const menu = await Menu.default();

    await menu.popup();

    const groups = calls[0][1].items;
    assert.deepEqual(groups.map((g) => g.text), ['Edit', 'Window', 'Help']);
    assert.ok(groups.every((g) => g.kind === 'submenu'));
    assert.ok(groups[0].items.some((i) => i.item === 'SelectAll'));
    assert.ok(groups[1].items.some((i) => i.item === 'Minimize'));
});

test('popup 传位置时归一成 { type, x, y }', async () => {
    const { calls } = installTransport();
    const menu = await Menu.new({ items: [{ id: 'a', text: 'A' }] });

    await menu.popup({ type: 'Physical', x: 10, y: 20 });

    assert.deepEqual(calls[0][1].position, { type: 'Physical', x: 10, y: 20 });
});

test('popup 传窗口时把 label 发过去', async () => {
    const { calls } = installTransport();
    const menu = await Menu.new({ items: [{ id: 'a', text: 'A' }] });

    await menu.popup(null, { label: 'settings' });

    assert.equal(calls[0][1].label, 'settings');
});

// ---------------------------------------------------------------------------
// 4. 不支持的东西要明说
// ---------------------------------------------------------------------------

test('setIcon 抛错而不是静默成功', async () => {
    const item = await IconMenuItem.new({ text: 'With icon' });
    await assert.rejects(() => item.setIcon(NativeIcon.Folder), /没有菜单图标的资源通道/);

    const submenu = await Submenu.new({ text: 'Sub' });
    await assert.rejects(() => submenu.setIcon(NativeIcon.Folder), /没有菜单图标的资源通道/);
});

test('菜单栏三件套在 B6b 之前必须抛错', async () => {
    const menu = await Menu.new({ items: [] });

    await assert.rejects(() => menu.setAsAppMenu(), /B6b/);
    await assert.rejects(() => menu.setAsWindowMenu(), /B6b/);

    const submenu = await Submenu.new({ text: 'Sub' });
    await assert.rejects(() => submenu.setAsWindowsMenuForNSApp(), /B6b/);
    await assert.rejects(() => submenu.setAsHelpMenuForNSApp(), /B6b/);
});

test('没有资源表：菜单类不提供 rid', async () => {
    // 这是"结构性缺口"，不是遗漏。Tauri 的 rid 是 Rust 侧资源表的句柄，本库没有那张表。
    const menu = await Menu.new({ items: [] });
    assert.equal(menu.rid, undefined);
});

test('close() 是本库扩展：关掉在屏菜单，不是释放 rid', async () => {
    // **同名不同义，必须钉住**：Tauri 的 `close()` 来自 `Resource`，释放的是 Rust 侧那条
    // 资源记录；本库的 `close()` 关的是**当前正在显示的菜单**（等价于用户按 Esc）。
    //
    // 为什么要给这个名字：Windows 的 popup() 阻塞到用户选择，UI 线程被占着时页面上的
    // 按钮点不到——没有这条命令，页面就没有任何办法用代码关掉自己弹的菜单。
    const { calls } = installTransport();
    const menu = await Menu.new({ items: [{ id: 'a', text: 'A' }] });

    await menu.close();

    assert.deepEqual(calls, [['menu.close', { label: null }]]);
});

test('Submenu.close() 同样发 menu.close', async () => {
    const { calls } = installTransport();
    const submenu = await Submenu.new({ text: 'Sub', items: [{ id: 'a', text: 'A' }] });

    await submenu.close({ label: 'settings' });

    assert.deepEqual(calls, [['menu.close', { label: 'settings' }]]);
});

// ---------------------------------------------------------------------------
// 常量表
// ---------------------------------------------------------------------------

test('NativeIcon 的键与参照实现逐键一致（56 个）', () => {
    // 这张表曾经是凭印象写的：多出 Apple/Document/Generic* 等 12 个不存在的键，
    // 又少了 Bluetooth/Caution/TrashFull 等。对齐账本的常量比对会拦住它，
    // 这里再钉一次数量，让"改了表却忘了跑对齐测试"也能被发现。
    assert.equal(Object.keys(NativeIcon).length, 56);
    assert.equal(NativeIcon.Folder, 'Folder');
    assert.equal(NativeIcon.TrashFull, 'TrashFull');
    assert.equal(NativeIcon.Apple, undefined, 'Apple 不是参照实现里的键');
    assert.ok(Object.isFrozen(NativeIcon));
});

test('itemFromKind 按 kind 还原成对应类', () => {
    assert.ok(itemFromKind([1, 'a', 'Submenu']) instanceof Submenu);
    assert.ok(itemFromKind([1, 'a', 'Check']) instanceof CheckMenuItem);
    assert.ok(itemFromKind([1, 'a', 'Icon']) instanceof IconMenuItem);
    assert.ok(itemFromKind([1, 'a', 'Predefined']) instanceof PredefinedMenuItem);
    assert.ok(itemFromKind([1, 'a', 'MenuItem']) instanceof MenuItem);
    assert.equal(itemFromKind([1, 'a', 'Check']).id, 'a');
});
