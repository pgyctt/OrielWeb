// check-tauri-leaks 的自测。
//
// 这个工具的价值全在"查得准不准"：漏报会让迁移后在运行期静默降级，
// 错报会让人不再看它的输出。两种都要钉住。
//
// 运行：node --test tools/check-tauri-leaks.test.mjs

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, mkdir, writeFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { scanText, scanDirectory, formatReport } from './check-tauri-leaks.mjs';

test('scanText：抓到 __TAURI_INTERNALS__ 并给出行列', () => {
    const text = [
        'const isTauri = "__TAURI_INTERNALS__" in window;',
        'if (window.__TAURI_INTERNALS__) { start(); }',
    ].join('\n');

    const hits = scanText(text).filter((hit) => hit.rule === 'internals');

    assert.equal(hits.length, 2);
    assert.equal(hits[0].line, 1);
    assert.equal(hits[1].line, 2);
    assert.match(hits[0].fix, /isTauri\(\)/);
});

test('scanText：@tauri-apps 的三种前缀都映射到本库模块名', () => {
    const text = [
        "import { invoke } from '@tauri-apps/api/core';",
        "import { getCurrentWindow } from '@tauri-apps/api/window';",
        "import { readTextFile } from '@tauri-apps/plugin-fs';",
        "import { readText } from '@tauri-apps/plugin-clipboard-manager';",
    ].join('\n');

    const fixes = scanText(text)
        .filter((hit) => hit.rule === 'tauri-import')
        .map((hit) => hit.fix);

    assert.deepEqual(fixes, [
        "改成 '@orielweb/api/core'",
        "改成 '@orielweb/api/window'",
        "改成 '@orielweb/api/fs'",
        "改成 '@orielweb/api/clipboard'",
    ]);
});

test('scanText：已知命令名给确定对应，未知的指路到映射表', () => {
    const known = scanText("await invoke('plugin:fs|read_text_file', { path });");
    assert.equal(known.length, 1);
    assert.match(known[0].fix, /fs\.readTextFile/);

    const unknown = scanText("await invoke('plugin:updater|check');");
    assert.equal(unknown.length, 1);
    assert.match(unknown[0].fix, /MIGRATION-FINDINGS/);
});

test('scanText：tauri:// 事件名被单独标出', () => {
    const hits = scanText("await listen('tauri://close-requested', fn);");

    assert.equal(hits.length, 1);
    assert.equal(hits[0].rule, 'tauri-event');
    assert.match(hits[0].fix, /TauriEvent/);
});

test('scanText：干净代码零命中（不制造噪音）', () => {
    const text = [
        "import { isTauri } from '@orielweb/api/core';",
        "import { getCurrentWindow } from '@orielweb/api/window';",
        "await window.oriel.invoke('todo.add', { text: 'x' });",
    ].join('\n');

    assert.deepEqual(scanText(text), []);
});

test('scanText：同一段文本里的多处命中按行列排序', () => {
    const hits = scanText("a __TAURI_INTERNALS__\nb 'tauri://x'\nc __TAURI_INTERNALS__");

    assert.deepEqual(hits.map((hit) => hit.line), [1, 2, 3]);
});

test('scanDirectory：跳过 node_modules，但**不**跳过 tests', async () => {
    const root = await mkdtemp(join(tmpdir(), 'oriel-leaks-'));

    try {
        await writeFile(join(root, 'app.mjs'), 'const x = "__TAURI_INTERNALS__" in window;');
        await mkdir(join(root, 'tests'));
        await writeFile(join(root, 'tests', 'stub.mjs'), "invoke('plugin:fs|read_file');");
        await mkdir(join(root, 'node_modules', 'x'), { recursive: true });
        await writeFile(join(root, 'node_modules', 'x', 'index.js'), '"__TAURI_INTERNALS__" in window');

        const result = await scanDirectory(root);

        // 测试桩是泄漏的常见藏身处（B7 演练里 34 处），跳过它等于漏掉一半
        assert.deepEqual(result.hits.map((hit) => hit.file).sort(), ['app.mjs', 'tests/stub.mjs']);
    } finally {
        await rm(root, { recursive: true, force: true });
    }
});

test('scanDirectory：--ignore 的前缀同时匹配文件与目录', async () => {
    const root = await mkdtemp(join(tmpdir(), 'oriel-leaks-'));

    try {
        await writeFile(join(root, 'app.mjs'), 'const x = "__TAURI_INTERNALS__" in window;');
        await mkdir(join(root, 'src'), { recursive: true });
        await writeFile(join(root, 'src', 'shim.mjs'), 'window.__TAURI_INTERNALS__ = {};');
        await writeFile(join(root, 'src', 'other.mjs'), "invoke('plugin:fs|read_file');");

        const result = await scanDirectory(root, { ignore: ['src/shim.mjs'] });

        assert.deepEqual(result.hits.map((hit) => hit.file).sort(), ['app.mjs', 'src/other.mjs']);
    } finally {
        await rm(root, { recursive: true, force: true });
    }
});

test('formatReport：没有命中时给出确定结论，不是空白', () => {
    assert.match(formatReport({ files: 12, hits: [] }), /没有问题|没有发现/);
});

test('formatReport：有命中时按文件分组并带改法', () => {
    const report = formatReport({
        files: 2,
        hits: [{
            file: 'src/app.js',
            line: 7,
            column: 5,
            rule: 'internals',
            text: '__TAURI_INTERNALS__',
            message: '探测恒为 false',
            fix: '改成 isTauri()',
        }],
    });

    assert.match(report, /src\/app\.js/);
    assert.match(report, /7:5/);
    assert.match(report, /改成 isTauri\(\)/);
});
