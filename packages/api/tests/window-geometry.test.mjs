// @orielweb/api/window 的几何查询测试（E2/E3）。
//
// 覆盖三件事：
//   1. **命令名与形状**：宿主给 { type, width, height }，包要包成 LogicalSize 实例
//      （`instanceof` 成立、能原样喂回 setSize）；
//   2. **读出来能写回去**：`setSize(innerSize())` 是恒等操作——这是这四个查询最实际的用途；
//   3. **与 Tauri 的单位差别**：Tauri 给 PhysicalSize，本库给 LogicalSize。
//      差别只在 `.type`，字段名一致，所以迁移代码通常感觉不到——但那一点必须钉住，
//      免得日后有人"顺手"改成物理像素而让所有几何运算在 Retina 上偏一倍。
//
// 运行：node --test packages/api/tests/window-geometry.test.mjs

import { test } from 'node:test';
import assert from 'node:assert/strict';

import { Window } from '../src/window.js';
import { LogicalSize, LogicalPosition } from '../src/dpi.js';
import { clearMocks } from '../src/mocks.js';
import { setTransportOverride } from '../src/internal.js';

/**
 * 装一个记录命令并给返回值的传输层。
 *
 * @param {string} label
 * @param {Record<string, unknown>} [responses]
 * @returns {{ calls: Array<[string, any]>, transport: any }}
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

test('innerSize：包成 LogicalSize 实例（不是裸对象）', async () => {
    clearMocks();
    const { calls, transport } = makeTransport('main', {
        'window.innerSize': { type: 'Logical', width: 800, height: 600 },
    });
    setTransportOverride(transport);

    const size = await new Window('main').innerSize();

    // instanceof 要成立：调用方可能拿它做类型判断，而裸对象过不了那个判断
    assert.ok(size instanceof LogicalSize, '应当是 LogicalSize 实例');
    assert.equal(size.type, 'Logical');
    assert.equal(size.width, 800);
    assert.equal(size.height, 600);
    assert.deepEqual(calls[0], ['window.innerSize', { label: 'main' }]);

    clearMocks();
});

test('outerSize：同样包成 LogicalSize', async () => {
    clearMocks();
    const { calls, transport } = makeTransport('main', {
        'window.outerSize': { type: 'Logical', width: 816, height: 639 },
    });
    setTransportOverride(transport);

    const size = await new Window('main').outerSize();

    assert.ok(size instanceof LogicalSize);
    assert.equal(size.width, 816);
    assert.equal(size.height, 639);

    clearMocks();
});

test('outerPosition / innerPosition：包成 LogicalPosition', async () => {
    clearMocks();
    const { transport } = makeTransport('main', {
        'window.outerPosition': { type: 'Logical', x: 120, y: 90 },
        'window.innerPosition': { type: 'Logical', x: 128, y: 121 },
    });
    setTransportOverride(transport);

    const win = new Window('main');
    const outer = await win.outerPosition();
    const inner = await win.innerPosition();

    assert.ok(outer instanceof LogicalPosition);
    assert.ok(inner instanceof LogicalPosition);
    assert.deepEqual([outer.x, outer.y], [120, 90]);
    assert.deepEqual([inner.x, inner.y], [128, 121]);

    clearMocks();
});

test('位置查询保留负坐标（副屏在主屏左侧/上方）', async () => {
    clearMocks();
    const { transport } = makeTransport('main', {
        'window.outerPosition': { type: 'Logical', x: -1920, y: -200 },
    });
    setTransportOverride(transport);

    const position = await new Window('main').outerPosition();

    assert.equal(position.x, -1920);
    assert.equal(position.y, -200);

    clearMocks();
});

test('读出来能原样写回去：setSize(innerSize()) 不改动尺寸', async () => {
    clearMocks();
    // 这两个模块最实际的用法就是"读出来做算术再写回去"
    const { calls, transport } = makeTransport('main', {
        'window.innerSize': { type: 'Logical', width: 800, height: 600 },
    });
    setTransportOverride(transport);

    const win = new Window('main');
    await win.setSize(await win.innerSize());

    assert.deepEqual(calls.map((c) => c[0]), ['window.innerSize', 'window.resize']);
    // LogicalSize 直通，不做任何换算——若哪天真改成物理像素，这里会算出 1600x1200 而变红
    assert.equal(calls[1][1].width, 800);
    assert.equal(calls[1][1].height, 600);

    clearMocks();
});

test('setPosition(outerPosition()) 也不改动位置', async () => {
    clearMocks();
    const { calls, transport } = makeTransport('main', {
        'window.outerPosition': { type: 'Logical', x: 120, y: 90 },
    });
    setTransportOverride(transport);

    const win = new Window('main');
    await win.setPosition(await win.outerPosition());

    assert.deepEqual(calls.map((c) => c[0]), ['window.outerPosition', 'window.moveTo']);
    assert.equal(calls[1][1].x, 120);
    assert.equal(calls[1][1].y, 90);

    clearMocks();
});

test('与 Tauri 的单位差别：type 必须是 Logical，不是 Physical', async () => {
    clearMocks();
    const { transport } = makeTransport('main', {
        'window.innerSize': { type: 'Logical', width: 800, height: 600 },
    });
    setTransportOverride(transport);

    const size = await new Window('main').innerSize();

    // Tauri 的 innerSize() 给 PhysicalSize；本库给 LogicalSize（库对页面只暴露逻辑像素）。
    // 差别只在 .type，字段名一致——所以迁移代码通常无感，但这一点必须钉住：
    // "顺手改成物理像素"会让所有几何运算在 Retina/150% 屏上偏一倍。
    assert.equal(size.type, 'Logical');
    // 同时确认它**不是** Physical，否则 setSize 会再除一次缩放比
    assert.notEqual(size.type, 'Physical');

    clearMocks();
});

test('宿主返回畸形值时按 0 兜底（不让整条链路崩）', async () => {
    clearMocks();
    const { transport } = makeTransport('main', {
        'window.innerSize': null,
        'window.outerPosition': {},
    });
    setTransportOverride(transport);

    const win = new Window('main');
    const size = await win.innerSize();
    const position = await win.outerPosition();

    // 窗口正在销毁时读几何是正常时序，宿主可能给 null。
    // 兜成 0 让调用方能继续（它自己判断 0 意味着"没有"）。
    assert.ok(size instanceof LogicalSize);
    assert.equal(size.width, 0);
    assert.equal(position.x, 0);

    clearMocks();
});

test('几何查询按 label 寻址（能读别的窗口）', async () => {
    clearMocks();
    const { calls, transport } = makeTransport('main', {
        'window.innerSize': { type: 'Logical', width: 400, height: 300 },
    });
    setTransportOverride(transport);

    const other = new Window('settings');
    await other.innerSize();

    assert.deepEqual(calls[0], ['window.innerSize', { label: 'settings' }]);

    clearMocks();
});

// ---- 只读状态查询（M5-1）----

test('四条状态查询的命令名与 label', async () => {
    clearMocks();
    const { calls, transport } = makeTransport('main', {
        'window.isMinimized': false,
        'window.isFocused': true,
        'window.isFullscreen': false,
        'window.isAlwaysOnTop': true,
    });
    setTransportOverride(transport);

    const win = new Window('main');

    assert.equal(await win.isMinimized(), false);
    assert.equal(await win.isFocused(), true);
    assert.equal(await win.isFullscreen(), false);
    assert.equal(await win.isAlwaysOnTop(), true);

    assert.deepEqual(calls.map((c) => c[0]), [
        'window.isMinimized', 'window.isFocused', 'window.isFullscreen', 'window.isAlwaysOnTop',
    ]);
    for (const [, args] of calls) {
        assert.deepEqual(args, { label: 'main' });
    }

    clearMocks();
});

test('四条状态查询按 label 寻址（能读别的窗口）', async () => {
    clearMocks();
    const { calls, transport } = makeTransport('settings', { 'window.isFocused': false });
    setTransportOverride(transport);

    await new Window('settings').isFocused();

    assert.deepEqual(calls[0], ['window.isFocused', { label: 'settings' }]);

    clearMocks();
});
