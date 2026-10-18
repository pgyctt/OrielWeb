import assert from 'node:assert/strict';
import { test } from 'node:test';

import { UserAttentionType, Window } from '../src/window.js';
import { clearMocks } from '../src/mocks.js';
import { setTransportOverride } from '../src/internal.js';

/**
 * 装一个记录命令并给返回值的传输层。
 *
 * @param {string} label
 * @param {Record<string, any>} [responses]
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

test('UserAttentionType 的取值与反向映射与 Tauri 一致', () => {
    // 取值必须对得上 Tauri（1/2）——迁移代码里可能写成 UserAttentionType.Critical，
    // 也可能写成裸的 1，两种都要能工作
    assert.equal(UserAttentionType.Critical, 1);
    assert.equal(UserAttentionType.Informational, 2);

    // 反向映射也要有：Tauri 的枚举对象两个方向都带
    assert.equal(UserAttentionType[1], 'Critical');
    assert.equal(UserAttentionType[2], 'Informational');

    // 冻结：这是常量表，改它会让上面两条断言在别处失效
    assert.ok(Object.isFrozen(UserAttentionType));
});

test('requestUserAttention 把常量翻成宿主的字符串，不传表示取消', async () => {
    clearMocks();
    const { calls, transport } = makeTransport('main', { 'window.requestUserAttention': null });
    setTransportOverride(transport);

    const win = new Window('main');

    await win.requestUserAttention(UserAttentionType.Critical);
    await win.requestUserAttention(UserAttentionType.Informational);
    await win.requestUserAttention();
    await win.requestUserAttention(null);

    assert.deepEqual(calls[0], [
        'window.requestUserAttention', { label: 'main', type: 'critical' },
    ]);
    assert.deepEqual(calls[1], [
        'window.requestUserAttention', { label: 'main', type: 'informational' },
    ]);

    // 不传 / 传 null → 取消（type 为 null），与 Tauri 一致
    assert.equal(calls[2][1].type, null);
    assert.equal(calls[3][1].type, null);

    clearMocks();
});

test('setMaxSize 收与 setSize 同样的形状，null 表示取消上限', async () => {
    clearMocks();
    const { calls, transport } = makeTransport('main', { 'window.setMaxSize': null });
    setTransportOverride(transport);

    const win = new Window('main');

    await win.setMaxSize({ width: 800, height: 600 });
    assert.deepEqual(calls[0][1], { label: 'main', value: { width: 800, height: 600 } });

    // 元组形状（与 setSize 一致）
    await win.setMaxSize([1024, 768]);
    assert.deepEqual(calls[1][1].value, { width: 1024, height: 768 });

    // null → 0/0（宿主侧 0 表示不限制）
    await win.setMaxSize(null);
    assert.deepEqual(calls[2][1].value, { width: 0, height: 0 });

    clearMocks();
});

test('setMaximizable / setMinimizable 走布尔校验与同一形状', async () => {
    clearMocks();
    const { calls, transport } = makeTransport('main', {
        'window.setMaximizable': null,
        'window.setMinimizable': null,
    });
    setTransportOverride(transport);

    const win = new Window('main');

    await win.setMaximizable(false);
    await win.setMinimizable(true);

    assert.deepEqual(calls[0], ['window.setMaximizable', { label: 'main', value: false }]);
    assert.deepEqual(calls[1], ['window.setMinimizable', { label: 'main', value: true }]);

    // 非布尔必须当场抛，而不是把 truthy 值透传给宿主——
    // 宿主侧 GetRequiredArg<bool> 会拒，但错误信息里就没有"参数名"这个上下文了
    assert.throws(() => win.setMaximizable('false'), TypeError);
    assert.throws(() => win.setMinimizable(0), TypeError);

    // 抛了就不该发命令（上面两次断言之后计数不该变）
    assert.equal(calls.length, 2);

    clearMocks();
});

test('setClosable 同样只收布尔，且与另外两个走同一条路径', async () => {
    clearMocks();
    const { calls, transport } = makeTransport('main', { 'window.setClosable': null });
    setTransportOverride(transport);

    const win = new Window('main');

    await win.setClosable(false);
    assert.deepEqual(calls[0], ['window.setClosable', { label: 'main', value: false }]);

    assert.throws(() => win.setClosable(undefined), TypeError);
    assert.throws(() => win.setClosable(1), TypeError);
    assert.equal(calls.length, 1);

    clearMocks();
});

test('setIcon 只拒 Image 对象，路径与字节都收', async () => {
    clearMocks();
    const { calls, transport } = makeTransport('main', { 'window.setIcon': null });
    setTransportOverride(transport);

    const win = new Window('main');

    await win.setIcon('/icons/app.png');
    assert.deepEqual(calls[0], ['window.setIcon', { label: 'main', path: '/icons/app.png' }]);

    // 早先这条测试断言"字节形式应当被拒"——现在它们**已经支持了**（见下面两个用例），
    // 所以这里只留 Image 对象：它是 JsImage 里唯一还需要 @orielweb/api/image 的形式。
    assert.throws(
        () => win.setIcon({ rid: 1 }),
        (error) => error instanceof TypeError && /Image/.test(error.message));

    assert.throws(() => win.setIcon(null), TypeError);
    assert.throws(() => win.setIcon(''), TypeError);

    // 抛了就不该发命令
    assert.equal(calls.length, 1);

    clearMocks();
});

test('setBackgroundColor：四种 Color 形状都归一成 0xAARRGGBB', async () => {
    clearMocks();
    const { calls, transport } = makeTransport('main', { 'window.setBackgroundColor': null });
    setTransportOverride(transport);

    const win = new Window('main');
    const send = async (color) => {
        await win.setBackgroundColor(color);
        return calls[calls.length - 1][1].argb;
    };

    // hex：长写
    assert.equal(await send('#123456'), 0xFF123456);
    assert.equal(await send('#12345678'), 0x78123456);

    // 不带 # 也认（从 CSS 抄过来的值常常没有 #）
    assert.equal(await send('123456'), 0xFF123456);

    // 短写展开：每位复制一次（#abc → #aabbcc）
    assert.equal(await send('#abc'), 0xFFAABBCC);
    assert.equal(await send('#abcd'), 0xDD_AABBCC);

    // 数组：3 个按全不透明，4 个用给的 alpha
    assert.equal(await send([18, 52, 86]), 0xFF123456);
    assert.equal(await send([18, 52, 86, 120]), 0x78123456);

    // 对象
    assert.equal(await send({ red: 18, green: 52, blue: 86, alpha: 120 }), 0x78123456);
    // alpha 缺省 → 全不透明。写错成 0 会让窗口整个透明，看着像"没生效"
    assert.equal(await send({ red: 18, green: 52, blue: 86 }), 0xFF123456);

    // 0 与 255 是边界，必须原样透传（不能因为 falsy 就当缺省）
    assert.equal(await send([0, 0, 0, 0]), 0x00000000);
    assert.equal(await send([255, 255, 255, 255]), 0xFFFFFFFF);

    // 结果必须是无符号整数：用 << 24 算出负数会让宿主的 uint 解析拒绝
    assert.ok((await send('#ffffff')) > 0, 'ARGB 应当是正数（无符号）');

    clearMocks();
});

test('setBackgroundColor：坏输入当场抛，且说清错在哪', async () => {
    clearMocks();
    const { calls, transport } = makeTransport('main', { 'window.setBackgroundColor': null });
    setTransportOverride(transport);

    const win = new Window('main');

    // 分量越界 / 非整数：报出是哪一路
    assert.throws(() => win.setBackgroundColor([256, 0, 0]), (e) => e instanceof TypeError && /红/.test(e.message));
    assert.throws(() => win.setBackgroundColor([0, -1, 0]), (e) => e instanceof TypeError && /绿/.test(e.message));
    assert.throws(() => win.setBackgroundColor([0, 0, 1.5]), (e) => e instanceof TypeError && /蓝/.test(e.message));
    assert.throws(() => win.setBackgroundColor({ red: 0, green: 0, blue: 0, alpha: 300 }),
        (e) => e instanceof TypeError && /alpha/.test(e.message));

    // 长度不对
    assert.throws(() => win.setBackgroundColor([1, 2]), TypeError);
    assert.throws(() => win.setBackgroundColor([1, 2, 3, 4, 5]), TypeError);

    // 认不出的字符串（含长度不对的 hex）
    assert.throws(() => win.setBackgroundColor('#12345'), TypeError);
    assert.throws(() => win.setBackgroundColor('tomato'), TypeError);
    assert.throws(() => win.setBackgroundColor('#gggggg'), TypeError);

    // 类型就不对
    assert.throws(() => win.setBackgroundColor(null), TypeError);
    assert.throws(() => win.setBackgroundColor(123), TypeError);

    // 一条都不该发出去
    assert.equal(calls.length, 0);

    clearMocks();
});

test('setIcon：字节的三种写法都编码成同一个 base64', async () => {
    clearMocks();
    const { calls, transport } = makeTransport('main', { 'window.setIcon': null });
    setTransportOverride(transport);

    const win = new Window('main');

    // 一个最小的 PNG 头 + 两个任意字节；这里只验**编码**，不验图像能否解码（那是宿主的事）
    const bytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    await win.setIcon(Uint8Array.from(bytes));
    await win.setIcon(Uint8Array.from(bytes).buffer);       // ArrayBuffer
    await win.setIcon(bytes);                                // number[]

    assert.equal(calls.length, 3);

    // 三种写法必须给出**同一个** base64——否则同一个图标会因为写法不同而表现不同
    const encoded = calls.map((call) => call[1].bytes);
    assert.deepEqual(encoded[1], encoded[0], 'ArrayBuffer 与 Uint8Array 应当一致');
    assert.deepEqual(encoded[2], encoded[0], 'number[] 与 Uint8Array 应当一致');

    // 与手工 base64 对账（\x89PNG\r\n\x1a\n）
    assert.equal(encoded[0], btoa('\x89PNG\r\n\x1a\n'));

    // 路径形式仍然走 path，不能与 bytes 混在一起
    await win.setIcon('/icons/app.png');
    assert.equal(calls[3][1].path, '/icons/app.png');
    assert.equal(calls[3][1].bytes, undefined);

    clearMocks();
});

test('setIcon：越界的字节必须当场抛（Uint8Array 会静默截断）', async () => {
    clearMocks();
    const { calls, transport } = makeTransport('main', { 'window.setIcon': null });
    setTransportOverride(transport);

    const win = new Window('main');

    // 999 会被 new Uint8Array 截断成 231——不拦的话页面拿到的是"没报错但图标不对"
    assert.throws(() => win.setIcon([999]), (e) => e instanceof TypeError && /0\.\.255/.test(e.message));
    assert.throws(() => win.setIcon([0, -1]), TypeError);
    assert.throws(() => win.setIcon([0, 1.5]), TypeError);
    assert.throws(() => win.setIcon([0, '2']), TypeError);

    // Image 对象（本库还不支持）也要给出可排查的说明
    assert.throws(() => win.setIcon({ rid: 1 }), (e) => e instanceof TypeError && /Image/.test(e.message));

    assert.equal(calls.length, 0);
    clearMocks();
});

test('setSizeConstraints 只发给了值的那一侧', async () => {
    clearMocks();
    const { calls, transport } = makeTransport('main', {
        'window.setMinSize': null,
        'window.setMaxSize': null,
    });
    setTransportOverride(transport);

    const win = new Window('main');

    // 只给上限：不该顺带把最小尺寸设成 0（那会把窗口卡成 0 宽）
    await win.setSizeConstraints({ maxWidth: 800, maxHeight: 600 });
    assert.equal(calls.length, 1);
    assert.equal(calls[0][0], 'window.setMaxSize');

    // 两侧都给：两条命令，顺序是先 min 后 max
    await win.setSizeConstraints({ minWidth: 200, minHeight: 150, maxWidth: 800, maxHeight: 600 });
    assert.deepEqual(calls.slice(1).map((call) => call[0]), ['window.setMinSize', 'window.setMaxSize']);

    // 什么都不给：一条命令都不发（不是"发一条把两边清零"）
    const before = calls.length;
    await win.setSizeConstraints({});
    assert.equal(calls.length, before);

    clearMocks();
});
