// @orielweb/api/path 的测试。
//
// 覆盖的重点不是"命令名传对了"（那是次要的），而是三件容易错的事：
//   1. `sep()` / `delimiter()` 是**同步**的（读宿主注入的常量，不 await）；
//   2. `join` / `resolve` 收的是**变长参数**，要打包成 `paths` 数组才发出去；
//   3. `basename` 的 `ext` 缺省时**不带那个字段**（不是带 undefined）——
//      宿主侧按"字段在不在"判断，带一个 undefined 会在 JSON 里变成 null 或消失，
//      两种结果都让服务端的判断变得可疑。
//
// 运行：node --test packages/api/tests/path.test.mjs

import { test } from 'node:test';
import assert from 'node:assert/strict';

import {
    sep, delimiter, join, normalize, dirname, extname, basename, isAbsolute, resolve, resolveResource,
    appDataDir, appLogDir, homeDir, tempDir, fontDir,
} from '../src/path.js';
import { clearMocks } from '../src/mocks.js';
import { setTransportOverride } from '../src/internal.js';

/**
 * 装一个记录命令的传输层。
 *
 * @param {Record<string, unknown>} [responses]
 * @param {{ sep?: string, delimiter?: string }} [pathConstants]
 * @returns {{ calls: Array<[string, any]>, transport: any }}
 */
function makeTransport(responses = {}, pathConstants = {}) {
    /** @type {Array<[string, any]>} */
    const calls = [];

    return {
        calls,
        transport: {
            path: {
                sep: pathConstants.sep ?? '/',
                delimiter: pathConstants.delimiter ?? ':',
            },
            async invoke(name, args) {
                calls.push([name, args]);
                return Object.prototype.hasOwnProperty.call(responses, name) ? responses[name] : null;
            },
        },
    };
}

// ---- 同步常量 ----

test('sep() / delimiter() 是同步的，从宿主注入的常量读', () => {
    clearMocks();
    const { transport } = makeTransport({}, { sep: '/', delimiter: ':' });
    setTransportOverride(transport);

    // 没有 await —— 它们必须同步可读。给这两个常量走一次 IPC 会让
    // `join(a, b)` 这种本该同步可算的东西也必须 await，而 Tauri 那边它们是同步的。
    assert.equal(sep(), '/');
    assert.equal(delimiter(), ':');

    clearMocks();
});

test('sep() 在 Windows 上是反斜杠', () => {
    clearMocks();
    setTransportOverride({ path: { sep: '\\', delimiter: ';' } });

    assert.equal(sep(), '\\');
    assert.equal(delimiter(), ';');

    clearMocks();
});

test('传输层没有 path 常量时给 POSIX 兜底（而不是 undefined）', () => {
    clearMocks();
    // 老宿主（0.4.0 之前）没有这个字段。给兜底值而不是 undefined：
    // `path.join` 里用 undefined 拼字符串会得到 "aundefinedb" 这种东西。
    setTransportOverride({ async invoke() { return null; } });

    assert.equal(sep(), '/');
    assert.equal(delimiter(), ':');

    clearMocks();
});

// ---- 变长参数要打包成数组 ----

test('join() 把变长参数打包成 paths 数组', async () => {
    clearMocks();
    const { calls, transport } = makeTransport({ 'path.join': '/a/b/c' });
    setTransportOverride(transport);

    assert.equal(await join('/a', 'b', 'c'), '/a/b/c');
    assert.deepEqual(calls[0], ['path.join', { paths: ['/a', 'b', 'c'] }]);

    clearMocks();
});

test('join() 无参数时发空数组（不是省略字段）', async () => {
    clearMocks();
    const { calls, transport } = makeTransport({ 'path.join': '' });
    setTransportOverride(transport);

    await join();

    // 省略 paths 字段会让宿主报"缺少必需参数"，而空数组是合法输入（结果是空串）
    assert.deepEqual(calls[0], ['path.join', { paths: [] }]);

    clearMocks();
});

test('resolve() 同样打包成 paths 数组', async () => {
    clearMocks();
    const { calls, transport } = makeTransport({ 'path.resolve': '/work/sub/f.txt' });
    setTransportOverride(transport);

    assert.equal(await resolve('sub', 'f.txt'), '/work/sub/f.txt');
    assert.deepEqual(calls[0], ['path.resolve', { paths: ['sub', 'f.txt'] }]);

    clearMocks();
});

// ---- 单参数命令 ----

test('normalize / dirname / extname / isAbsolute 的参数字段名是 path', async () => {
    clearMocks();
    const { calls, transport } = makeTransport({
        'path.normalize': '/a/b',
        'path.dirname': '/a',
        'path.extname': 'txt',
        'path.isAbsolute': true,
    });
    setTransportOverride(transport);

    assert.equal(await normalize('/a/../a/b'), '/a/b');
    assert.equal(await dirname('/a/b.txt'), '/a');
    assert.equal(await extname('b.txt'), 'txt');
    assert.equal(await isAbsolute('/a'), true);

    assert.deepEqual(calls.map((c) => c[0]), [
        'path.normalize',
        'path.dirname',
        'path.extname',
        'path.isAbsolute',
    ]);
    for (const [, args] of calls) {
        assert.deepEqual(Object.keys(args), ['path']);
    }

    clearMocks();
});

// ---- basename 的可选参数 ----

test('basename() 不给 ext 时**不带那个字段**', async () => {
    clearMocks();
    const { calls, transport } = makeTransport({ 'path.basename': 'app.conf' });
    setTransportOverride(transport);

    assert.equal(await basename('d/app.conf'), 'app.conf');

    // 宿主侧按"字段在不在"判断。带一个 `ext: undefined` 到了宿主那边会变成 null
    // （JSON 里没有 undefined），于是 `ext.ValueKind === String` 为假、但字段存在——
    // 两种结果都让判断变得可疑。所以干脆不发。
    assert.deepEqual(calls[0], ['path.basename', { path: 'd/app.conf' }]);
    assert.equal('ext' in calls[0][1], false);

    clearMocks();
});

test('basename() 给了 ext 时带上它', async () => {
    clearMocks();
    const { calls, transport } = makeTransport({ 'path.basename': 'app' });
    setTransportOverride(transport);

    assert.equal(await basename('d/app.conf', '.conf'), 'app');
    assert.deepEqual(calls[0], ['path.basename', { path: 'd/app.conf', ext: '.conf' }]);

    clearMocks();
});

test('basename() 的 ext 为空串时仍然带上（调用方显式给了）', async () => {
    clearMocks();
    const { calls, transport } = makeTransport({ 'path.basename': 'app.conf' });
    setTransportOverride(transport);

    await basename('app.conf', '');

    // 只有 `undefined` 才算"没给"。空串是显式值，发出去；宿主侧会因 suffix 为空而不做剥离。
    assert.deepEqual(calls[0], ['path.basename', { path: 'app.conf', ext: '' }]);

    clearMocks();
});

// ---- 目录解析（F4b-1）----

test('每个目录函数发对应命令，且**不带参数**', async () => {
    clearMocks();
    const { calls, transport } = makeTransport();
    setTransportOverride(transport);

    // 只挑几条代表性的（24 个逐个跑没必要，而下面那条会覆盖"全部都注册了"）
    await appDataDir();
    await appLogDir();
    await homeDir();
    await tempDir();

    assert.deepEqual(calls.map((c) => c[0]), [
        'path.appDataDir',
        'path.appLogDir',
        'path.homeDir',
        'path.tempDir',
    ]);

    for (const [, args] of calls) {
        assert.equal(args, undefined);
    }

    clearMocks();
});

test('全部 24 个目录函数都存在且发出形状正确的命令名', async () => {
    clearMocks();
    const { calls, transport } = makeTransport();
    setTransportOverride(transport);

    // 目录名 → 命令名的规则是"首字母小写 + Dir 后缀"，宿主侧同一条规则。
    // 这里逐个调一遍，把 24 条命令名全钉住：漏一个的症状是某个
    // `appConfigDir()` 之类的函数发了宿主不认的命令名（报 unknown_command）。
    const directories = [
        'appConfigDir', 'appDataDir', 'appLocalDataDir', 'appCacheDir', 'appLogDir',
        'audioDir', 'cacheDir', 'configDir', 'dataDir', 'localDataDir',
        'desktopDir', 'documentDir', 'downloadDir', 'executableDir', 'fontDir',
        'homeDir', 'pictureDir', 'publicDir', 'resourceDir', 'runtimeDir',
        'tempDir', 'templateDir', 'videoDir',
    ];

    const mod = await import('../src/path.js');
    for (const name of directories) {
        assert.equal(typeof mod[name], 'function', `缺少 ${name}`);
        await mod[name]();
    }

    assert.equal(calls.length, directories.length);
    for (const [command, args] of calls) {
        assert.match(command, /^path\.[a-z][A-Za-z]*Dir$/, `命令名形状不对：${command}`);
        assert.equal(args, undefined);
    }

    clearMocks();
});

test('resolveResource() 把参数放在 path 字段里', async () => {
    clearMocks();
    const { calls, transport } = makeTransport({ 'path.resolveResource': 'C:\\res\\assets\\a.svg' });
    setTransportOverride(transport);

    assert.equal(await resolveResource('assets/a.svg'), 'C:\\res\\assets\\a.svg');
    assert.deepEqual(calls[0], ['path.resolveResource', { path: 'assets/a.svg' }]);

    clearMocks();
});

test('目录解析在平台不支持时以 not_supported 拒绝', async () => {
    clearMocks();

    // Windows 上的 fontDir / runtimeDir / executableDir 与 macOS 上的 templateDir
    // 都是"这个平台没有这个概念"——调用方据此按平台分支，而不是当成 bug。
    const error = new Error('fontDir 在 Windows 上不存在（与 Tauri 一致，请按平台分支处理）。');
    error.code = 'not_supported';
    setTransportOverride({ async invoke() { throw error; } });

    await assert.rejects(
        () => fontDir(),
        (caught) => {
            assert.equal(caught.code, 'not_supported');
            return true;
        },
    );

    clearMocks();
});


test('path 命令的失败带着错误码上来到调用方', async () => {
    clearMocks();

    const error = new Error('命令参数 \'paths\' 的类型不符：期望 Array，实际 String。');
    error.code = 'invalid_argument';

    setTransportOverride({ async invoke() { throw error; } });

    await assert.rejects(
        () => join('a', 'b'),
        (caught) => {
            assert.equal(caught.code, 'invalid_argument');
            return true;
        },
    );

    clearMocks();
});
