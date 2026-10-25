#!/usr/bin/env node
// 迁移泄漏检查：把 Tauri 里"编译器看不见"的那类字符串找出来。
//
// 为什么需要它：`__TAURI_INTERNALS__` 这类探测**不可能**用运行时兼容层解决——
// 本库要让 `"__TAURI_INTERNALS__" in window` 为 false，就不能同时"观测到这次读取"。
// 而漏改的症状不是报错，是应用静默走进浏览器降级分支（假数据、禁用按钮、不注册监听）。
// 这些字符串在源码里是可见的，所以正确的解法是在源码层查。
//
// 用法：
//   node tools/check-tauri-leaks.mjs                 # 扫当前目录
//   node tools/check-tauri-leaks.mjs ../my-app       # 扫指定项目
//   node tools/check-tauri-leaks.mjs --json          # 机器可读
//   node tools/check-tauri-leaks.mjs . --ignore=src/legacy,scripts   # 跳过已知的兼容层
//
// 退出码：0 = 没发现问题；1 = 有命中（可直接当 CI 门禁）。
//
// **别对本库自己跑**：`packages/api/src` 里本来就有 `__TAURI_INTERNALS__` 与
// `tauri://`（那是兼容判断与事件映射表），扫出来全是噪音。
//
// 边界：这是**文本检查**，不是类型检查。它报告"这里有一处需要人看"，不保证
// 自动改对——注释里的 `__TAURI_INTERNALS__` 也会被列出（那是有意的：宁可多报）。

import { readdir, readFile } from 'node:fs/promises';
import { extname, join, relative, resolve, sep } from 'node:path';
import { pathToFileURL } from 'node:url';
import process from 'node:process';

/** 只扫这些扩展名：Tauri 迁移的泄漏点都在前端源码里。 */
const EXTENSIONS = new Set([
    '.js', '.mjs', '.cjs', '.jsx', '.ts', '.tsx', '.mts', '.cts',
    '.vue', '.svelte', '.html', '.htm',
]);

/** 默认跳过的目录。`tests` 不跳——测试桩是泄漏的常见藏身处（B7 演练里 34 处）。 */
const SKIP_DIRS = new Set([
    'node_modules', '.git', 'dist', 'build', 'target', 'out', 'coverage',
    '.next', '.nuxt', '.svelte-kit', '.output', 'bin', 'obj',
]);

/**
 * Tauri 命令名 → 本库命令名。
 *
 * 只收**有确定对应**的那些；不在表里的会报"未知命令名"，指路到
 * `docs/MIGRATION-FINDINGS.md` §4 的完整映射表。不收全的原因：
 * 编不出来就是编，漏报比错报好查。
 */
const COMMAND_MAP = {
    'plugin:fs|read_text_file': 'fs.readTextFile',
    'plugin:fs|write_text_file': 'fs.writeTextFile',
    'plugin:fs|read_file': 'fs.readFile',
    'plugin:fs|write_file': 'fs.writeFile',
    'plugin:fs|exists': 'fs.exists',
    'plugin:fs|size': 'fs.size',
    'plugin:fs|read_dir': 'fs.readDir',
    'plugin:fs|stat': 'fs.stat',
    'plugin:fs|lstat': 'fs.lstat',
    'plugin:fs|mkdir': 'fs.mkdir',
    'plugin:fs|remove': 'fs.remove',
    'plugin:fs|rename': 'fs.rename',
    'plugin:fs|copy_file': 'fs.copyFile',
    'plugin:fs|truncate': 'fs.truncate',
    'plugin:fs|watch': 'fs.watch',
    'plugin:fs|watch_immediate': 'fs.watchImmediate',
    'plugin:dialog|open': 'dialog.open',
    'plugin:dialog|save': 'dialog.save',
    'plugin:dialog|message': 'dialog.message',
    'plugin:dialog|ask': 'dialog.ask',
    'plugin:dialog|confirm': 'dialog.confirm',
    'plugin:clipboard-manager|read_text': 'clipboard.readText',
    'plugin:clipboard-manager|write_text': 'clipboard.writeText',
    'plugin:clipboard-manager|clear': 'clipboard.clear',
    'plugin:clipboard-manager|read_image': 'clipboard.readImage',
    'plugin:clipboard-manager|write_image': 'clipboard.writeImage',
    'plugin:notification|is_permission_granted': 'notification.isPermissionGranted',
    'plugin:notification|request_permission': 'notification.requestPermission',
    'plugin:notification|notify': 'notification.sendNotification',
    'plugin:opener|open_url': 'opener.openUrl',
    'plugin:opener|open_path': 'opener.openPath',
    'plugin:opener|reveal_item_in_dir': 'opener.revealItemInDir',
    'plugin:autostart|is_enabled': 'autostart.isEnabled',
    'plugin:autostart|enable': 'autostart.enable',
    'plugin:autostart|disable': 'autostart.disable',
};

/** `@tauri-apps/xxx` 的 xxx → 本库模块名。带 `api/` 前缀的是核心包。 */
function ourModule(tauriModule) {
    const trimmed = tauriModule.replace(/\/$/, '');
    if (trimmed === 'api') {
        return 'core';
    }

    if (trimmed.startsWith('api/')) {
        return trimmed.slice('api/'.length);
    }

    if (trimmed.startsWith('plugin-')) {
        const name = trimmed.slice('plugin-'.length);
        return name === 'clipboard-manager' ? 'clipboard' : name;
    }

    return trimmed;
}

/**
 * 一条规则：正则 + 说明 + 改法。
 *
 * @typedef {{ id: string, pattern: RegExp, message: string, fix: (match: RegExpExecArray) => string }} Rule
 * @type {Rule[]}
 */
const RULES = [
    {
        id: 'internals',
        // 不区分 `in` 与属性读取：两种都要改，且改法一样。
        pattern: /\b__TAURI_INTERNALS__\b/g,
        message: '本库没有这个对象，探测恒为 false —— 应用会静默走进浏览器降级分支',
        fix: () => "改成 import { isTauri } from '@orielweb/api/core'，用 isTauri() 判断",
    },
    {
        id: 'tauri-import',
        pattern: /['"](@tauri-apps\/[^'"]+)['"]/g,
        message: 'Tauri 包名在迁移后不存在',
        fix: (match) => `改成 '@orielweb/api/${ourModule(match[1].slice('@tauri-apps/'.length))}'`,
    },
    {
        id: 'plugin-command',
        pattern: /['"`](plugin:[a-z-]+\|[a-z_0-9]+)['"`]/g,
        message: 'Tauri 的插件命令名，本库按点分命名',
        fix: (match) => {
            const ours = COMMAND_MAP[match[1]];
            return ours
                ? `改成 '${ours}'`
                : `没有确定的对应命令，查 docs/MIGRATION-FINDINGS.md §4（本库不用 plugin:x|y 形式）`;
        },
    },
    {
        id: 'tauri-event',
        pattern: /['"`](tauri:\/\/[a-z-]+)['"`]/g,
        message: 'Tauri 的事件名与本库宿主事件名不同，直接照搬监听不会触发',
        fix: () => '改用 @orielweb/api/event 的 TauriEvent 常量（包内会自动翻译成宿主事件名）',
    },
];

/**
 * 扫描一段源码文本，返回命中项。
 *
 * @param {string} text
 * @returns {Array<{ rule: string, line: number, column: number, text: string, message: string, fix: string }>}
 */
export function scanText(text) {
    /** @type {ReturnType<typeof scanText>} */
    const hits = [];

    for (const rule of RULES) {
        // 每条规则各自 new 一次正则状态（g 标志带 lastIndex，跨文件复用会漏匹配）
        const pattern = new RegExp(rule.pattern.source, rule.pattern.flags);
        /** @type {RegExpExecArray | null} */
        let match;

        while ((match = pattern.exec(text)) !== null) {
            const before = text.slice(0, match.index);
            const line = before.split('\n').length;
            const column = match.index - before.lastIndexOf('\n');

            hits.push({
                rule: rule.id,
                line,
                column,
                text: match[0],
                message: rule.message,
                fix: rule.fix(match),
            });

            // 零宽匹配兜底：不推进 lastIndex 会死循环
            if (match[0].length === 0) {
                pattern.lastIndex++;
            }
        }
    }

    return hits.sort((a, b) => a.line - b.line || a.column - b.column);
}

/**
 * 递归收集待扫文件。
 *
 * @param {string} dir
 * @param {string} root
 * @returns {Promise<string[]>}
 */
async function collectFiles(dir, root) {
    /** @type {string[]} */
    const files = [];

    let entries;
    try {
        entries = await readdir(dir, { withFileTypes: true });
    } catch {
        return files;
    }

    for (const entry of entries) {
        if (entry.isDirectory()) {
            if (SKIP_DIRS.has(entry.name)) {
                continue;
            }

            files.push(...await collectFiles(join(dir, entry.name), root));
            continue;
        }

        if (entry.isFile() && EXTENSIONS.has(extname(entry.name).toLowerCase())) {
            files.push(join(dir, entry.name));
        }
    }

    return files;
}

/**
 * 扫描一个目录。
 *
 * @param {string} root 目标项目根目录
 * @param {{ ignore?: string[] }} [options]
 *   `ignore` 是**相对 root 的路径前缀**（文件或目录）。给兼容层用：自己写的
 *   `__TAURI_INTERNALS__` 探测垫片是**有意的**，不该被当成漏改。
 * @returns {Promise<{ files: number, hits: Array<{ file: string, line: number, column: number, rule: string, text: string, message: string, fix: string }> }>}
 */
export async function scanDirectory(root, options = {}) {
    const absolute = resolve(root);
    const ignore = (options.ignore ?? [])
        .map((prefix) => prefix.replace(/\\/g, '/').replace(/^\.\//, '').replace(/\/+$/, ''))
        .filter(Boolean);

    const files = await collectFiles(absolute, absolute);
    /** @type {Array<{ file: string, line: number, column: number, rule: string, text: string, message: string, fix: string }>} */
    const hits = [];

    for (const file of files) {
        const relativePath = relative(absolute, file).split(sep).join('/');

        if (ignore.some((prefix) => relativePath === prefix || relativePath.startsWith(`${prefix}/`))) {
            continue;
        }

        let text;
        try {
            text = await readFile(file, 'utf8');
        } catch {
            continue;
        }

        for (const hit of scanText(text)) {
            hits.push({ file: relativePath, ...hit });
        }
    }

    return { files: files.length, hits };
}

/**
 * 把结果排成人读的输出。
 *
 * @param {{ files: number, hits: Array<{ file: string, line: number, column: number, rule: string, text: string, message: string, fix: string }> }} result
 * @returns {string}
 */
export function formatReport(result) {
    if (result.hits.length === 0) {
        return `扫描 ${result.files} 个文件，没有发现 Tauri 侧的字符串泄漏。`;
    }

    /** @type {Map<string, string[]>} */
    const byFile = new Map();

    for (const hit of result.hits) {
        if (!byFile.has(hit.file)) {
            byFile.set(hit.file, []);
        }

        byFile.get(hit.file).push(
            `  ${hit.line}:${hit.column}  [${hit.rule}] ${hit.message}\n` +
            `      ${hit.text}\n` +
            `      改法：${hit.fix}`);
    }

    /** @type {Map<string, number>} */
    const byRule = new Map();
    for (const hit of result.hits) {
        byRule.set(hit.rule, (byRule.get(hit.rule) ?? 0) + 1);
    }

    const sections = [...byFile.entries()]
        .map(([file, lines]) => `\n${file}\n${lines.join('\n')}`)
        .join('\n');

    const summary = [...byRule.entries()]
        .map(([rule, count]) => `${rule} ${count}`)
        .join(' / ');

    return `扫描 ${result.files} 个文件，发现 ${result.hits.length} 处需要改（${summary}）：` +
        `${sections}\n\n` +
        '这些字符串编译器不会报错，漏改的症状是"功能静默消失"。\n' +
        '完整的命令名映射与迁移卡点见 docs/MIGRATION-FINDINGS.md。';
}

/**
 * CLI 入口。
 *
 * @param {string[]} argv 不含 node 与脚本路径
 * @returns {Promise<number>} 退出码
 */
export async function main(argv) {
    const json = argv.includes('--json');
    const target = argv.find((arg) => !arg.startsWith('--')) ?? '.';
    const ignore = argv
        .filter((arg) => arg.startsWith('--ignore='))
        .flatMap((arg) => arg.slice('--ignore='.length).split(','))
        .filter(Boolean);

    const result = await scanDirectory(target, { ignore });

    if (json) {
        process.stdout.write(JSON.stringify(result, null, 2) + '\n');
    } else {
        process.stdout.write(formatReport(result) + '\n');
    }

    return result.hits.length === 0 ? 0 : 1;
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
    process.exitCode = await main(process.argv.slice(2));
}
