// @orielweb/api/dpi 的单元测试。
//
// dpi 是纯计算模块，因此能在任何平台上断言——这正是它该被钉死的原因：
// 换算错了的表现是"某个平台上窗口差一圈"，而那种偏差在开发机上（100% 缩放）
// 完全看不出来。
//
// 运行：node --test packages/api/tests/dpi.test.mjs

import { test } from 'node:test';
import assert from 'node:assert/strict';

import {
    LogicalSize,
    PhysicalSize,
    LogicalPosition,
    PhysicalPosition,
    normalizeSize,
    normalizePosition,
    logicalToPhysical,
    physicalToLogical,
} from '../src/dpi.js';

test('四个类带上正确的 type 字段（宿主据此区分逻辑/物理）', () => {
    assert.equal(new LogicalSize(800, 600).type, 'Logical');
    assert.equal(new PhysicalSize(800, 600).type, 'Physical');
    assert.equal(new LogicalPosition(10, 20).type, 'Logical');
    assert.equal(new PhysicalPosition(10, 20).type, 'Physical');
});

test('类实例字段可读且与构造参数一致', () => {
    const size = new LogicalSize(1024, 768);
    assert.equal(size.width, 1024);
    assert.equal(size.height, 768);

    const position = new PhysicalPosition(-5, 30);
    assert.equal(position.x, -5);
    assert.equal(position.y, 30);
});

test('normalizeSize 接受类实例、裸对象与数组', () => {
    assert.deepEqual(normalizeSize(new LogicalSize(1, 2)), { type: 'Logical', width: 1, height: 2 });
    assert.deepEqual(normalizeSize(new PhysicalSize(3, 4)), { type: 'Physical', width: 3, height: 4 });
    assert.deepEqual(normalizeSize({ width: 5, height: 6 }), { type: 'Logical', width: 5, height: 6 });
    assert.deepEqual(normalizeSize([7, 8]), { type: 'Logical', width: 7, height: 8 });
});

test('normalizeSize 缺 type 时用 fallbackType，但入参自带 type 时以入参为准', () => {
    assert.deepEqual(
        normalizeSize({ width: 1, height: 2 }, 'Physical'),
        { type: 'Physical', width: 1, height: 2 },
    );

    // 显式给了 Physical 就不该被 fallback 覆盖——否则物理像素会被当成逻辑像素，差 scale 倍
    assert.deepEqual(
        normalizeSize({ type: 'Physical', width: 1, height: 2 }, 'Logical'),
        { type: 'Physical', width: 1, height: 2 },
    );
});

test('normalizeSize 拒绝不可解释的入参（不静默当 0）', () => {
    // 静默当 0 的后果是 0x0 的窗口，而调用方只会看到"窗口没了"
    assert.throws(() => normalizeSize(/** @type {any} */ (null)), /需要 \{width, height\}/);
    assert.throws(() => normalizeSize(/** @type {any} */ (42)), /需要 \{width, height\}/);
    assert.throws(() => normalizeSize(/** @type {any} */ ({})), /width/);
});

test('normalizeSize 拒绝 NaN / Infinity / 非数字', () => {
    assert.throws(() => normalizeSize({ width: NaN, height: 1 }), /必须是有限数字/);
    assert.throws(() => normalizeSize({ width: Infinity, height: 1 }), /必须是有限数字/);
    assert.throws(() => normalizeSize({ width: /** @type {any} */ ('800'), height: 1 }), /必须是有限数字/);
});

test('normalizePosition 接受类实例、裸对象与数组', () => {
    assert.deepEqual(normalizePosition(new LogicalPosition(1, 2)), { type: 'Logical', x: 1, y: 2 });
    assert.deepEqual(normalizePosition(new PhysicalPosition(3, 4)), { type: 'Physical', x: 3, y: 4 });
    assert.deepEqual(normalizePosition({ x: 5, y: 6 }), { type: 'Logical', x: 5, y: 6 });
    assert.deepEqual(normalizePosition([7, 8]), { type: 'Logical', x: 7, y: 8 });
});

test('normalizePosition 允许负坐标（副屏在主屏左侧/上方）', () => {
    assert.deepEqual(normalizePosition([-1920, -200]), { type: 'Logical', x: -1920, y: -200 });
});

test('logicalToPhysical 按缩放比折算并四舍五入', () => {
    assert.equal(logicalToPhysical(1000, 1), 1000);
    assert.equal(logicalToPhysical(1000, 1.25), 1250);
    assert.equal(logicalToPhysical(1000, 1.5), 1500);
    assert.equal(logicalToPhysical(1000, 2), 2000);
});

test('logicalToPhysical 的舍入与宿主侧 LogicalPixels.ToPhysical 逐例一致', () => {
    // 宿主侧是 .NET 的 Math.Round(logical * dpi / 96)，默认 MidpointRounding.**ToEven**：
    // Math.Round(4.5) 给 4，而 JS 的 Math.round(4.5) 给 5。两边不一致的后果是
    // "同一份前端代码在 150% 屏上比宿主算的大 1 像素"——只在恰好 .5 的输入上出现，
    // 人眼看不出、随机测试也撞不到。下面每一行都对着 LogicalPixelsTests.cs 的用例。
    assert.equal(logicalToPhysical(1, 1.25), 1);   // 1.25 → 1
    assert.equal(logicalToPhysical(2, 1.5), 3);    // 3.0
    assert.equal(logicalToPhysical(3, 1.5), 4);    // 4.5 → 4（ToEven，**不是** 5）
    assert.equal(logicalToPhysical(5, 1.5), 8);    // 7.5 → 8（ToEven：7 是奇数，进一到 8）
    assert.equal(logicalToPhysical(7, 1.5), 10);   // 10.5 → 10（ToEven：10 是偶数，留 10）
});

test('logicalToPhysical 保留符号与零', () => {
    assert.equal(logicalToPhysical(-1000, 1.5), -1500);
    assert.equal(logicalToPhysical(0, 2), 0);

    // 负数方向的 .5 也要取偶：-4.5 → -4，-5.5 → -6
    assert.equal(logicalToPhysical(-3, 1.5), -4);
    assert.equal(logicalToPhysical(-7, 1.5), -10);
});

test('physicalToLogical 保留小数，不取整', () => {
    // 取整会逐次累积偏差：反复 setSize(getSize()) 之后窗口会越来越小
    assert.equal(physicalToLogical(1250, 1.25), 1000);
    assert.equal(physicalToLogical(1000, 3), 1000 / 3);
});

test('两个换算函数都拒绝非正的 scaleFactor', () => {
    // scaleFactor 为 0 时 physicalToLogical 会得到 Infinity，而那个值发到宿主会变成 null
    assert.throws(() => logicalToPhysical(100, 0), /必须是正数/);
    assert.throws(() => physicalToLogical(100, 0), /必须是正数/);
    assert.throws(() => logicalToPhysical(100, -1), /必须是正数/);
});

test('换算往返在常见缩放比下无损', () => {
    for (const scale of [1, 1.25, 1.5, 2, 3]) {
        const physical = logicalToPhysical(800, scale);
        assert.equal(physicalToLogical(physical, scale), 800, `scale=${scale}`);
    }
});
