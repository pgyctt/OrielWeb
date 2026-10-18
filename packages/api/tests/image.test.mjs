// @orielweb/api/image 的单元测试。
//
// 这个模块与 Tauri 的 image 有一处**本质区别**，测试就是围着它写的：
// Tauri 的 `Image` 是资源表 handle（`rid` + `close()`），本库没有资源表，
// 它是**页面侧的字节容器**（见 src/image.js 开头）。所以这里能覆盖的也更多——
// 尺寸解析、格式嗅探、越界字节这些全都在页面侧完成，不需要宿主。
//
// 运行：node --test packages/api/tests/image.test.mjs

import { test } from 'node:test';
import assert from 'node:assert/strict';

import { Image, transformImage } from '../src/image.js';
import { mockIPC, clearMocks } from '../src/mocks.js';
import { setTransportOverride } from '../src/internal.js';

/** 造一个最小的合法 PNG 头（8 字节签名 + IHDR 的宽高），只够读尺寸。 */
function pngHeader(width, height) {
    const bytes = new Uint8Array(24);
    bytes.set([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], 0);
    // 长度(4) + "IHDR"(4) 之后才是宽高；这里把宽高写在 16..23
    bytes.set([0x49, 0x48, 0x44, 0x52], 12);
    writeUint32(bytes, 16, width);
    writeUint32(bytes, 20, height);
    return bytes;
}

/** 造一个 ICO 头（ICONDIR + 一个 ICONDIRENTRY）。 */
function icoHeader(width, height) {
    const bytes = new Uint8Array(22);
    bytes[2] = 1; // 类型：1 = icon
    bytes[4] = 1; // 条目数
    bytes[6] = width === 256 ? 0 : width;   // 0 表示 256
    bytes[7] = height === 256 ? 0 : height;
    return bytes;
}

function writeUint32(bytes, offset, value) {
    bytes[offset] = (value >>> 24) & 0xFF;
    bytes[offset + 1] = (value >>> 16) & 0xFF;
    bytes[offset + 2] = (value >>> 8) & 0xFF;
    bytes[offset + 3] = value & 0xFF;
}

/** 记录命令并给返回值的传输层。 */
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

test('Image.fromBytes：从 PNG 头部读出尺寸（不需要宿主）', async () => {
    const image = await Image.fromBytes(pngHeader(320, 240));

    assert.deepEqual(await image.size(), { width: 320, height: 240 });
    assert.ok(image.bytes instanceof Uint8Array);
    assert.equal(image.isOrielImage, true);
});

test('Image.fromBytes：ICO 的宽高 0 表示 256（格式历史坑）', async () => {
    const image = await Image.fromBytes(icoHeader(256, 256));
    assert.deepEqual(await image.size(), { width: 256, height: 256 });

    // 非 0 值原样读
    const small = await Image.fromBytes(icoHeader(32, 32));
    assert.deepEqual(await small.size(), { width: 32, height: 32 });
});

test('Image：不是 PNG/ICO 就当场抛，并给出开头字节便于排查', async () => {
    await assert.rejects(
        () => Image.fromBytes(new Uint8Array([1, 2, 3, 4, 5, 6, 7, 8])),
        (error) => error instanceof TypeError && /PNG 与 ICO/.test(error.message));

    // 空数据是另一条分支，措辞不同（"是空的"比"认不出格式"准确）
    await assert.rejects(
        () => Image.fromBytes(new Uint8Array(0)),
        (error) => error instanceof TypeError && /空的/.test(error.message));

    // 头部被截断：能认出是 PNG，但读不出尺寸
    await assert.rejects(
        () => Image.fromBytes(pngHeader(100, 100).subarray(0, 16)),
        (error) => error instanceof TypeError && /头部不完整/.test(error.message));
});

test('Image.new：按 width*height*4 校验缓冲区长度', async () => {
    const image = await Image.new(new Uint8Array([255, 0, 0, 255]), 1, 1);
    assert.deepEqual(await image.size(), { width: 1, height: 1 });
    // 长度不对必须拒——给一段短数据会读到越界（拿到的像素是 undefined）
    await assert.rejects(
        () => Image.new(new Uint8Array(3), 1, 1),
        (error) => error instanceof TypeError && /4 字节/.test(error.message));
    await assert.rejects(() => Image.new(new Uint8Array(4), 0, 1), TypeError);
});

test('Image.new：rgba() 直接给本地像素，不打扰宿主', async () => {
    clearMocks();
    const { calls, transport } = makeTransport('main');
    setTransportOverride(transport);

    const pixels = new Uint8Array([1, 2, 3, 4, 5, 6, 7, 8]);
    const image = await Image.new(pixels, 2, 1);

    assert.deepEqual(Array.from(await image.rgba()), Array.from(pixels));
    assert.equal(calls.length, 0, 'Image.new 建的图不该发任何 IPC');

    clearMocks();
});

test('Image.rgba：编码字节的图请宿主解码一次，之后走缓存', async () => {
    clearMocks();
    // 宿主返回 base64 的 "RGBA"
    const decoded = Buffer.from('RGBA').toString('base64');
    const { calls, transport } = makeTransport('main', { 'window.decodeImage': decoded });
    setTransportOverride(transport);

    const image = await Image.fromBytes(pngHeader(1, 1));

    const first = await image.rgba();
    assert.ok(first instanceof Uint8Array);
    assert.equal(Buffer.from(first).toString(), 'RGBA');

    // 第二次不该再发 IPC
    await image.rgba();
    assert.equal(calls.filter((call) => call[0] === 'window.decodeImage').length, 1,
        '解码结果应当被缓存');

    clearMocks();
});

test('Image.rgba：宿主解码长度与尺寸不符时报错（不把错位像素交出去）', async () => {
    clearMocks();
    // 声明 1x1（应为 4 字节），宿主却给回 8 字节
    const wrong = Buffer.from('RGBARGBA').toString('base64');
    const { transport } = makeTransport('main', { 'window.decodeImage': wrong });
    setTransportOverride(transport);

    const image = await Image.fromBytes(pngHeader(1, 1));

    await assert.rejects(
        () => image.rgba(),
        (error) => /相符|不符/.test(error.message));

    clearMocks();
});

test('Image.fromPath：把路径交给宿主读成字节', async () => {
    clearMocks();
    const base64 = Buffer.from(pngHeader(64, 48)).toString('base64');
    const { calls, transport } = makeTransport('main', { 'window.readImageFile': base64 });
    setTransportOverride(transport);

    const image = await Image.fromPath('/icons/app.png');

    assert.deepEqual(calls[0], ['window.readImageFile', { path: '/icons/app.png' }]);
    assert.deepEqual(await image.size(), { width: 64, height: 48 });

    // 空路径当场拒（不发 IPC）
    await assert.rejects(() => Image.fromPath(''), TypeError);
    assert.equal(calls.length, 1);

    clearMocks();
});

test('Image 没有 close()：本库没有资源表可释放', async () => {
    const image = await Image.fromBytes(pngHeader(16, 16));

    // Tauri 的 Image 继承 Resource，有 close()。本库**刻意不提供**：
    // 给一个空方法会让调用方误以为"不关就泄漏"。这条测试就是钉住这个决定——
    // 哪天有人"为了对齐 Tauri"加了个空 close()，这里会红。
    assert.equal(image.close, undefined);
});

test('Image.new 的产物能直接设成图标（走 rgba 而不是编码字节）', async () => {
    clearMocks();
    const { Window } = await import('../src/window.js');
    const { calls, transport } = makeTransport('main', { 'window.setIcon': null });
    setTransportOverride(transport);

    // 1x1 的不透明红
    const rgba = new Uint8Array([255, 0, 0, 255]);
    const image = await Image.new(rgba, 1, 1);

    // 关键：`hasEncodedBytes` 为 false（没有 PNG/ICO 字节），但**照样能设**
    assert.equal(image.hasEncodedBytes, false);
    assert.equal(image.width, 1);
    assert.equal(image.height, 1);

    await new Window('main').setIcon(image);

    // 送的是 rgba + 尺寸，**不是** bytes —— 宿主侧的图标工厂本来就吃 RGBA，
    // 所以不需要在页面侧把 RGBA 编码成 PNG（本库也没有 PNG 编码器）。
    const args = calls[0][1];
    assert.equal(args.bytes, undefined, '不该走编码字节那条路');
    assert.equal(args.rgba, Buffer.from(rgba).toString('base64'));
    assert.equal(args.width, 1);
    assert.equal(args.height, 1);

    clearMocks();
});

test('Image.fromBytes 的产物走 bytes（有编码字节）', async () => {
    clearMocks();
    const { Window } = await import('../src/window.js');
    const { calls, transport } = makeTransport('main', { 'window.setIcon': null });
    setTransportOverride(transport);

    const png = pngHeader(2, 2);
    const image = await Image.fromBytes(png);

    assert.equal(image.hasEncodedBytes, true);
    await new Window('main').setIcon(image);

    const args = calls[0][1];
    assert.equal(args.bytes, Buffer.from(png).toString('base64'));
    assert.equal(args.rgba, undefined);

    clearMocks();
});

test('rgbaBytes：没解码过的编码图会明确抛（而不是给空数组）', async () => {
    const image = await Image.fromBytes(pngHeader(4, 4));

    // 同步访问器不做解码，所以这里必然抛；给空数组会让 setIcon 传一段错位的像素
    assert.throws(() => image.rgbaBytes, (error) => error instanceof TypeError && /await rgba/.test(error.message));

    // Image.new 建的图有本地像素，直接可读
    const raw = await Image.new(new Uint8Array([1, 2, 3, 4]), 1, 1);
    assert.deepEqual(Array.from(raw.rgbaBytes), [1, 2, 3, 4]);
});

test('transformImage：四种形式都能转，null 原样返回', async () => {
    const png = pngHeader(1, 1);

    assert.equal(transformImage(null), null);
    assert.equal(transformImage('/a.png'), '/a.png');
    assert.equal(transformImage(png), Buffer.from(png).toString('base64'));
    assert.equal(transformImage(png.buffer), Buffer.from(png).toString('base64'));
    assert.equal(transformImage(Array.from(png)), Buffer.from(png).toString('base64'));

    const image = await Image.fromBytes(png);
    assert.equal(transformImage(image), Buffer.from(png).toString('base64'));

    assert.throws(() => transformImage({ nope: true }), TypeError);
});
