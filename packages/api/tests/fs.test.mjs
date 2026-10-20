// @orielweb/api/fs 的测试。
//
// 这个模块的重点不是"命令名传对了"，而是**二进制在 JSON 通道上的转换**——
// 那是唯一的真实陷阱：JSON 不认 Uint8Array，直接塞进 JSON.stringify 会得到一个
// **对象**（{"0":1,"1":2}）而不是数组，而宿主侧期望数组、会报类型不符。
// 症状是"写二进制文件报参数类型错"，而调用方明明传的是 Uint8Array。
//
// 另外钉住 scope 失败的错误码（path_not_allowed）与"命令没授权"（not_allowed）
// 是分开的——那两个混在一起时排障方向完全不同。
//
// 运行：node --test packages/api/tests/fs.test.mjs

import { test } from 'node:test';
import assert from 'node:assert/strict';

import {
    readTextFile,
    writeTextFile,
    readFile,
    writeFile,
    exists,
    size,
    readDir,
    stat,
    lstat,
    mkdir,
    remove,
    rename,
    copyFile,
    truncate,
    BaseDirectory,
} from '../src/fs.js';
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

// ---- 文本 ----

test('readTextFile / writeTextFile 的参数字段是 path 与 data', async () => {
    clearMocks();
    const { calls, transport } = makeTransport({ 'fs.readTextFile': '内容' });
    setTransportOverride(transport);

    assert.equal(await readTextFile('/a.txt'), '内容');
    await writeTextFile('/a.txt', '内容');

    assert.deepEqual(calls[0], ['fs.readTextFile', { path: '/a.txt' }]);
    assert.deepEqual(calls[1], ['fs.writeTextFile', { path: '/a.txt', data: '内容' }]);

    clearMocks();
});

test('readTextFile 不会把返回值二次处理（原样给字符串）', async () => {
    clearMocks();
    // 空字符串、含换行、含引号——都不该被改动
    const samples = ['', 'a\nb', '"quoted"', '\u0000'];

    for (const sample of samples) {
        clearMocks();
        setTransportOverride({ async invoke() { return sample; } });
        assert.equal(await readTextFile('/x'), sample);
    }

    clearMocks();
});

// ---- 二进制：JSON 通道上最容易出错的地方 ----

test('readFile 把数字数组包成 Uint8Array', async () => {
    clearMocks();
    setTransportOverride({ async invoke() { return [0, 1, 127, 128, 255]; } });

    const bytes = await readFile('/a.bin');

    assert.ok(bytes instanceof Uint8Array, '应当是 Uint8Array');
    assert.deepEqual([...bytes], [0, 1, 127, 128, 255]);

    clearMocks();
});

test('writeFile 把 Uint8Array 转成**普通数组**再发', async () => {
    clearMocks();
    const { calls, transport } = makeTransport();
    setTransportOverride(transport);

    await writeFile('/a.bin', new Uint8Array([1, 2, 3]));

    // **这条是本文件的重点**：JSON 不认 Uint8Array。直接发的话
    // JSON.stringify 会把它变成 {"0":1,"1":2,"2":3}（对象），宿主侧期望数组 → 报类型不符。
    // 所以这里必须 Array.from。
    assert.deepEqual(calls[0], ['fs.writeFile', { path: '/a.bin', data: [1, 2, 3] }]);
    assert.ok(Array.isArray(calls[0][1].data), '发出的 data 必须是数组');
    assert.ok(!(calls[0][1].data instanceof Uint8Array), '不该把 Uint8Array 直接发出去');

    clearMocks();
});

test('writeFile 也接受普通数组', async () => {
    clearMocks();
    const { calls, transport } = makeTransport();
    setTransportOverride(transport);

    await writeFile('/a.bin', [4, 5]);

    assert.deepEqual(calls[0][1].data, [4, 5]);

    clearMocks();
});

test('writeFile 发出的数组能被 JSON 序列化成数组字面量', async () => {
    clearMocks();
    setTransportOverride({ async invoke() { return null; } });

    /** @type {string | undefined} */
    let serialized;

    // 模拟真实的桥接：参数要过 JSON.stringify。
    // Uint8Array 会变成对象——这正是要避免的。
    setTransportOverride({
        async invoke(_name, args) {
            serialized = JSON.stringify(args);
            return null;
        },
    });

    await writeFile('/a.bin', new Uint8Array([7, 8]));

    assert.equal(serialized, '{"path":"/a.bin","data":[7,8]}');

    clearMocks();
});

// ---- 查询 ----

test('exists / size / readDir / stat / lstat 的参数形状', async () => {
    clearMocks();
    const { calls, transport } = makeTransport({
        'fs.exists': true,
        'fs.size': 42,
        'fs.readDir': [],
        'fs.stat': { isFile: true },
        'fs.lstat': { isFile: true },
    });
    setTransportOverride(transport);

    assert.equal(await exists('/a'), true);
    assert.equal(await size('/a'), 42);
    assert.deepEqual(await readDir('/d'), []);
    assert.deepEqual(await stat('/a'), { isFile: true });
    assert.deepEqual(await lstat('/a'), { isFile: true });

    assert.deepEqual(calls.map((c) => c[0]), [
        'fs.exists', 'fs.size', 'fs.readDir', 'fs.stat', 'fs.lstat',
    ]);
    for (const [, args] of calls) {
        assert.deepEqual(Object.keys(args), ['path']);
    }

    clearMocks();
});

test('readDir 的字段名是 camelCase（与 Tauri 一致）', async () => {
    clearMocks();
    // 宿主侧是手写 JSON 保证 camelCase 的（STJ 默认写 PascalCase）。
    // 这里钉住"页面侧读的是 entry.name 而不是 entry.Name"。
    setTransportOverride({
        async invoke() {
            return [{ name: 'a.txt', isFile: true, isDirectory: false, isSymlink: false }];
        },
    });

    const entries = await readDir('/d');

    assert.equal(entries[0].name, 'a.txt');
    assert.equal(entries[0].isFile, true);
    assert.equal(entries[0].isDirectory, false);

    clearMocks();
});

test('stat 的时间是毫秒时间戳（不是 Date）', async () => {
    clearMocks();
    setTransportOverride({
        async invoke() {
            return {
                isFile: true, isDirectory: false, isSymlink: false,
                size: 5, mtime: 1700000000000, atime: 1700000000000, birthtime: 1700000000000,
            };
        },
    });

    const info = await stat('/a');

    // 时间戳而不是 Date：跨 JSON 无损，且少一层意外（Date 要走 new Date(x) 转换）
    assert.equal(typeof info.mtime, 'number');
    assert.equal(info.mtime, 1700000000000);

    clearMocks();
});

// ---- 写操作 ----

test('mkdir / remove 只在 recursive 为 true 时带那个字段', async () => {
    clearMocks();
    const { calls, transport } = makeTransport();
    setTransportOverride(transport);

    await mkdir('/d');
    await mkdir('/d', { recursive: true });
    await remove('/f');
    await remove('/d', { recursive: true });

    // **缺省时不带字段**：带一个 undefined 到了宿主那边会变成 null（JSON 里没有 undefined），
    // 而宿主侧按"字段在不在"判断——两种结果的差别很微妙。
    assert.deepEqual(calls[0], ['fs.mkdir', { path: '/d' }]);
    assert.deepEqual(calls[1], ['fs.mkdir', { path: '/d', recursive: true }]);
    assert.deepEqual(calls[2], ['fs.remove', { path: '/f' }]);
    assert.deepEqual(calls[3], ['fs.remove', { path: '/d', recursive: true }]);

    clearMocks();
});

test('recursive 只认严格的 true', async () => {
    clearMocks();
    const { calls, transport } = makeTransport();
    setTransportOverride(transport);

    await mkdir('/d', { recursive: /** @type {any} */ ('yes') });
    await remove('/d', { recursive: /** @type {any} */ (1) });

    assert.deepEqual(calls[0], ['fs.mkdir', { path: '/d' }]);
    assert.deepEqual(calls[1], ['fs.remove', { path: '/d' }]);

    clearMocks();
});

test('rename 用 oldPath / newPath 两个字段', async () => {
    clearMocks();
    const { calls, transport } = makeTransport();
    setTransportOverride(transport);

    await rename('/a', '/b');

    assert.deepEqual(calls[0], ['fs.rename', { oldPath: '/a', newPath: '/b' }]);

    clearMocks();
});

test('copyFile 的 JS 签名是 (from, to)，命令字段是 oldPath / newPath', async () => {
    clearMocks();
    const { calls, transport } = makeTransport();
    setTransportOverride(transport);

    // Tauri 的 copyFile(fromPath, toPath)，而它的 Rust 命令字段叫 old_path / new_path
    // （与 rename 共用）。这里保持同样的映射，迁移时参数顺序不会错。
    await copyFile('/src', '/dst');

    assert.deepEqual(calls[0], ['fs.copyFile', { oldPath: '/src', newPath: '/dst' }]);

    clearMocks();
});

test('truncate 带 len', async () => {
    clearMocks();
    const { calls, transport } = makeTransport();
    setTransportOverride(transport);

    await truncate('/a', 4);

    assert.deepEqual(calls[0], ['fs.truncate', { path: '/a', len: 4 }]);

    clearMocks();
});

// ---- baseDir：相对路径按 BaseDirectory 解析（B13 起受支持）----

test('14 个函数都接受 baseDir，相对路径按目录解析', async () => {
    // 参数表的**位置**是重点：每个函数的 options 位置不同，照印象写测试会漏掉
    // `truncate` 那种"第 2 参是 len、第 3 参才是 options"的签名。
    const cases = [
        ['readTextFile', (o) => readTextFile('a', o), { path: '/data/a' }],
        ['writeTextFile', (o) => writeTextFile('a', 'x', o), { path: '/data/a', data: 'x' }],
        ['readFile', (o) => readFile('a', o), { path: '/data/a' }],
        ['writeFile', (o) => writeFile('a', [1], o), { path: '/data/a', data: [1] }],
        ['exists', (o) => exists('a', o), { path: '/data/a' }],
        ['size', (o) => size('a', o), { path: '/data/a' }],
        ['readDir', (o) => readDir('a', o), { path: '/data/a' }],
        ['stat', (o) => stat('a', o), { path: '/data/a' }],
        ['lstat', (o) => lstat('a', o), { path: '/data/a' }],
        ['mkdir', (o) => mkdir('a', o), { path: '/data/a' }],
        ['remove', (o) => remove('a', o), { path: '/data/a' }],
        ['rename', (o) => rename('a', 'b', o), { oldPath: '/data/a', newPath: '/data/b' }],
        ['copyFile', (o) => copyFile('a', 'b', o), { oldPath: '/data/a', newPath: '/data/b' }],
        ['truncate', (o) => truncate('a', 4, o), { path: '/data/a', len: 4 }],
    ];

    assert.equal(cases.length, 14, 'Tauri 的 fs 有 14 个带 options 的函数');

    for (const [name, call, expected] of cases) {
        clearMocks();
        const { calls, transport } = makeTransport({ 'path.appDataDir': '/data' });
        setTransportOverride(transport);

        await call({ baseDir: BaseDirectory.AppData });

        // calls 里还会有目录函数自己的 `path.appDataDir` 调用——只看 fs.* 那条
        const fsCalls = calls.filter(([n]) => n.startsWith('fs.'));
        assert.equal(fsCalls.length, 1, `${name}: 应当发出命令（baseDir 不再被拒绝）`);
        assert.deepEqual(fsCalls[0][1], expected, `${name}: 相对路径应按 baseDir 解析`);
    }

    clearMocks();
});

test('绝对路径不参与 baseDir 解析（Tauri 语义）', async () => {
    clearMocks();
    const { calls, transport } = makeTransport({ 'path.appDataDir': '/data' });
    setTransportOverride(transport);

    await readTextFile('/abs/a.txt', { baseDir: BaseDirectory.AppData });

    assert.deepEqual(calls[0][1], { path: '/abs/a.txt' });

    clearMocks();
});

test('未知 baseDir 值报错且不发命令', async () => {
    clearMocks();
    const { calls, transport } = makeTransport({ 'path.appDataDir': '/data' });
    setTransportOverride(transport);

    await assert.rejects(
        () => readTextFile('a', { baseDir: 999 }),
        (err) => {
            assert.match(err.message, /baseDir/);
            return true;
        });
    assert.deepEqual(calls, [], '报错前不该发命令');

    clearMocks();
});

test('baseDir 与 recursive 并存（逐键判，不是一刀切）', async () => {
    clearMocks();
    const { calls, transport } = makeTransport({ 'path.appDataDir': '/data' });
    setTransportOverride(transport);

    await mkdir('a', { recursive: true, baseDir: BaseDirectory.AppData });

    assert.deepEqual(
        calls.filter(([n]) => n.startsWith('fs.')),
        [['fs.mkdir', { path: '/data/a', recursive: true }]]);

    clearMocks();
});

test('未知选项也拒绝（防止把 dir 之类的拼写错误当没写）', async () => {
    clearMocks();
    const { transport } = makeTransport();
    setTransportOverride(transport);

    await assert.rejects(() => readTextFile('/a', { dir: 'x' }), /不支持的选项/);
    await assert.rejects(() => mkdir('/a', { recusive: true }), /不支持的选项/);

    clearMocks();
});

// ---- 返回形状 ----

test('stat / lstat 的 mtime 是 number（毫秒），不是 Date', async () => {
    // 这一条钉的是**一处真实的迁移差异**：Tauri 的 `FileInfo.mtime` 是 `Date`，
    // 本库是毫秒时间戳。差异本身是有意的（JSON 无损），但"有意"不等于"可以静默"——
    // 迁移代码里 `info.mtime.getTime()` 在 number 上会抛 TypeError，
    // 而 `new Date(info.mtime)` 在 Date 上也能跑（拿到 Invalid Date? 不，是原样复制），
    // 所以只有断言类型才能保证这条差异不被实现悄悄改掉。
    clearMocks();
    const stamp = 1_700_000_000_000;
    const { transport } = makeTransport({
        'fs.stat': { isFile: true, mtime: stamp },
        'fs.lstat': { isFile: true, mtime: stamp },
    });
    setTransportOverride(transport);

    const info = await stat('/a');
    assert.equal(typeof info.mtime, 'number', 'mtime 必须是毫秒时间戳（number），不能是 Date');
    assert.equal(info.mtime, stamp, 'mtime 必须原样透传，不做单位换算');

    const linkInfo = await lstat('/a');
    assert.equal(typeof linkInfo.mtime, 'number', 'lstat 与 stat 的时间类型必须一致');

    clearMocks();
});

// ---- 失败分类 ----

test('路径不在 scope 内时错误码是 path_not_allowed', async () => {
    clearMocks();

    const error = new Error('路径不在任何允许的模式内（已声明：$APPDATA/**）。');
    error.code = 'path_not_allowed';
    setTransportOverride({ async invoke() { throw error; } });

    await assert.rejects(
        () => readTextFile('/etc/passwd'),
        (caught) => {
            // 与"命令没授权"（not_allowed）分开：那个是没声明命令，
            // 这个是声明了命令但没声明这个路径。混在一起时排障方向完全不同。
            assert.equal(caught.code, 'path_not_allowed');
            return true;
        },
    );

    clearMocks();
});

test('模块顶层导入不发任何命令', async () => {
    clearMocks();
    const { calls, transport } = makeTransport();
    setTransportOverride(transport);

    await new Promise((resolve) => setTimeout(resolve, 0));

    assert.deepEqual(calls, []);
    clearMocks();
});
