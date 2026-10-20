import assert from 'node:assert/strict';
import test from 'node:test';

import { invoke } from '../../../packages/api/src/core.js';
import { clearMocks, mockIPC } from '../../../packages/api/src/mocks.js';

// ============================================================================
// 迁移配方：把"按 Tauri 线格式桩命令名"的测试改成 mockIPC。
//
// 为什么需要这个夹具：B7 演练里 random-frame 有 32 处测试直接桩 `plugin:fs|write_file`
// 这类**线格式命令名**。迁移后应用代码改成本库命令名（`fs.writeFile`），测试却还桩着旧名字，
// 于是编译能过、运行必红——而红的位置在测试里、不在产品代码里，容易被当成"库有问题"。
//
// 这里的两条用例把两种写法的**失败形态**钉下来，让后来的人不必再踩一次。
// ============================================================================

test('迁移后：mockIPC 按本库命令名分支，与生产走同一套名字', async () => {
    /** @type {Array<[string, unknown]>} */
    const calls = [];

    mockIPC((cmd, args) => {
        calls.push([cmd, args]);

        // 迁移后的应用调的是本库命令名。所有分支都写在这里，测试与生产用同一个字符串——
        // 这正是它比"桩线格式"可靠的地方：名字拼错会当场失败，而不是等到真机。
        switch (cmd) {
            case 'fs.writeFile':
                return null;
            case 'fs.readTextFile':
                return 'hi';
            default:
                throw new Error(`宿主没有命令「${cmd}」`);
        }
    });

    await invoke('fs.writeFile', { path: '/tmp/a.txt', contents: 'hi' });
    assert.equal(await invoke('fs.readTextFile', { path: '/tmp/a.txt' }), 'hi');

    assert.deepEqual(calls.map(([cmd]) => cmd), ['fs.writeFile', 'fs.readTextFile']);

    clearMocks();
});

test('反例：仍按 Tauri 线格式调命令，会以「命令不存在」失败', async () => {
    // 这条**故意**调用旧名字，用来固定失败形态：迁移后如果测试还桩着 `plugin:fs|write_file`，
    // 症状就是这一句——"宿主没有命令"。它不是库的 bug，是命令名的映射没跟着改。
    mockIPC((cmd) => {
        throw new Error(`宿主没有命令「${cmd}」`);
    });

    await assert.rejects(
        () => invoke('plugin:fs|write_file'),
        /宿主没有命令「plugin:fs\|write_file」/,
        '线格式命令名在本库上不存在——这正是迁移后测试必红的机制',
    );

    clearMocks();
});

test('迁移后的代码不该出现 plugin: 字面量（自查用）', async () => {
    // 工装（tools/migration-drill）会在演练报告里按文件统计这类字面量。
    // 这里只钉住"本库命令名的形状"，避免从演练结论里读出一个错的替换规则：
    // 命令名是「模块.方法」（fs.writeFile），不是「plugin:x|y」。
    const { readFile } = await import('node:fs/promises');
    const source = await readFile(new URL('./mock-ipc.test.mjs', import.meta.url), 'utf8');

    const literals = [...source.matchAll(/['"]plugin:[a-z0-9-]+\|[a-z0-9_]+['"]/g)].map((m) => m[0]);
    assert.equal(literals.length, 1, '本文件只应在反例里保留一处 plugin: 字面量');
    assert.match(literals[0], /plugin:fs\|write_file/);
});
