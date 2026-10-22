#!/usr/bin/env node
/**
 * B7 迁移演练工装。
 *
 * 做三件事，全部机械、无判断：
 *   1. 按 `apps.json` 里固定的 commit SHA 取真实 Tauri 2 应用源码（缓存到 .drill/，不入库）；
 *   2. 把 `@tauri-apps/*` 的 import 说明符按映射表改写成 `@orielweb/api/*`，写到 .drill/migrated/；
 *   3. 用**本库自己的类型声明**检查「改完之后每个被导入的符号是否真的存在」，
 *      并对可解析的调用跑一次 tsc（只保留与本库相关的诊断，外部依赖缺失的噪音丢掉）。
 *
 * 为什么先做符号检查再做 tsc：真实应用普遍是 React/Vue + 一大堆依赖，没有 node_modules
 * 时 tsc 会先报几千条「找不到 react」。符号检查不依赖任何外部包，能在 1 秒内回答
 * 那个真正要紧的问题——**这些真实代码要的东西，我们到底有没有**。
 *
 * 用法：
 *   node tools/migration-drill/drill.mjs                 # 跑全部，打印摘要 + 写 .drill/report.json
 *   node tools/migration-drill/drill.mjs --no-typecheck  # 只做符号检查（快）
 *   node tools/migration-drill/drill.mjs --app lumos     # 只跑一个
 */

import fs from 'node:fs';
import path from 'node:path';
import { execFileSync } from 'node:child_process';
import { createRequire } from 'node:module';
import { fileURLToPath, pathToFileURL } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.resolve(HERE, '..', '..');
const DRILL = path.join(ROOT, '.drill');
const APPS_DIR = path.join(DRILL, 'apps');
const MIGRATED_DIR = path.join(DRILL, 'migrated');
const TYPES_DIR = path.join(ROOT, 'packages', 'api', 'types');

const argv = process.argv.slice(2);
const noTypecheck = argv.includes('--no-typecheck');
const onlyApp = (() => {
    const i = argv.indexOf('--app');
    return i >= 0 ? argv[i + 1] : null;
})();

/**
 * Tauri 模块 ↔ 本库模块。
 *
 * `null` 的含义是「本库没有这个模块」——不是「还没映射」，所以它必须显式写出来：
 * 漏写会表现成「映射表里没有 → 看起来像笔误」，而这里每一条 null 都是**已确认的缺口**。
 */
const MODULE_MAP = {
    '@tauri-apps/api': { oriel: 'core', note: 'v1 风格的根导入；v2 已无此写法' },
    '@tauri-apps/api/core': { oriel: 'core' },
    '@tauri-apps/api/event': { oriel: 'event' },
    '@tauri-apps/api/dpi': { oriel: 'dpi' },
    '@tauri-apps/api/path': { oriel: 'path' },
    '@tauri-apps/api/app': { oriel: 'app' },
    '@tauri-apps/api/window': { oriel: 'window' },
    '@tauri-apps/api/webviewWindow': { oriel: 'webviewWindow' },
    '@tauri-apps/api/menu': { oriel: 'menu' },
    '@tauri-apps/api/mocks': { oriel: 'mocks' },
    '@tauri-apps/api/image': { oriel: 'image' },
    '@tauri-apps/api/webview': { oriel: null, gap: '本库没有 webview 层：窗口即 webview。'
        + 'webview 级的方法已经在 B16（setZoom / clearAllBrowsingData）与 B22 补到 WebviewWindow 上，'
        + 'onDragDropEvent 由 B8 补到 Window 上——所以迁移时把 `getCurrentWebview()` 换成 '
        + '`getCurrentWindow()` 就够用，缺的只是"一个窗口挂多个 webview"那个概念本身' },
    '@tauri-apps/api/tray': { oriel: 'tray' },
    '@tauri-apps/plugin-dialog': { oriel: 'dialog' },
    '@tauri-apps/plugin-fs': { oriel: 'fs' },
    '@tauri-apps/plugin-opener': { oriel: 'opener' },
    '@tauri-apps/plugin-clipboard-manager': { oriel: 'clipboard' },
    '@tauri-apps/plugin-notification': { oriel: 'notification' },
    '@tauri-apps/plugin-autostart': { oriel: 'autostart' },
    '@tauri-apps/plugin-process': { oriel: null, gap: '只有 app.exit；没有 relaunch（重启自己）——需要宿主支持进程重启' },
    '@tauri-apps/plugin-updater': { oriel: null, gap: '没有自动更新：签名校验、差分包、下载器都是整块能力' },
    '@tauri-apps/plugin-log': { oriel: null, gap: '没有日志插件（宿主侧有 stderr 日志，页面侧没有对应 API）' },
    '@tauri-apps/plugin-store': { oriel: null, gap: '没有键值存储持久化（需要宿主侧落地实现）' },
    '@tauri-apps/plugin-window-state': { oriel: null, gap: '没有窗口状态记忆插件' },
    '@tauri-apps/plugin-global-shortcut': { oriel: null, gap: '没有全局快捷键' },
    '@tauri-apps/plugin-http': { oriel: null, gap: '没有带能力的 HTTP 客户端（页面可直接用 fetch）' },
    '@tauri-apps/plugin-shell': { oriel: null, gap: '没有命令执行插件（与本库的进程模型不符）' },
    '@tauri-apps/plugin-os': { oriel: null, gap: '没有 OS 信息模块' },
    '@tauri-apps/plugin-deep-link': { oriel: null, gap: '没有 deep link 支持' },
};

const SOURCE_EXT = new Set(['.ts', '.tsx', '.js', '.jsx', '.mjs', '.cjs', '.vue', '.svelte']);
const SKIP_DIR = new Set(['node_modules', 'target', 'dist', 'build', '.git', '.svelte-kit', 'gen', 'src-tauri']);

// ---------------------------------------------------------------- 类型符号表

const requireFromApi = createRequire(pathToFileURL(path.join(ROOT, 'packages', 'api', 'package.json')));
const ts = requireFromApi('typescript');

/** 解析 `<module>.d.ts`，返回它导出的顶层名字集合。 */
function readExports(moduleName) {
    const file = path.join(TYPES_DIR, `${moduleName}.d.ts`);
    if (!fs.existsSync(file)) {
        return null;
    }

    const text = fs.readFileSync(file, 'utf8');
    const sf = ts.createSourceFile(file, text, ts.ScriptTarget.ESNext, true);
    const names = new Set();

    const hasExport = (node) => (ts.getModifiers(node) ?? []).some((m) => m.kind === ts.SyntaxKind.ExportKeyword);

    for (const st of sf.statements) {
        if (ts.isExportDeclaration(st)) {
            const clause = st.exportClause;
            if (clause && ts.isNamedExports(clause)) {
                for (const el of clause.elements) {
                    names.add((el.name ?? el.propertyName).text);
                }
            }
            continue;
        }
        if (!hasExport(st)) {
            continue;
        }
        if (st.name && (ts.isIdentifier(st.name))) {
            names.add(st.name.text);
        }
    }

    return names;
}

const EXPORTS = new Map();
for (const entry of Object.keys(MODULE_MAP)) {
    const oriel = MODULE_MAP[entry].oriel;
    if (oriel && !EXPORTS.has(oriel)) {
        EXPORTS.set(oriel, readExports(oriel));
    }
}

// ---------------------------------------------------------------- 取源码

function ensureSource(app) {
    const dir = path.join(APPS_DIR, app.slug);
    if (fs.existsSync(dir)) {
        return dir;
    }

    fs.mkdirSync(APPS_DIR, { recursive: true });
    const tgz = path.join(APPS_DIR, `${app.slug}.tgz`);
    const url = `https://codeload.github.com/${app.repo}/tar.gz/${app.sha}`;

    console.log(`  下载 ${app.repo}@${app.sha.slice(0, 8)} …`);
    execFileSync('curl', ['-sL', '--max-time', '180', url, '-o', tgz], { stdio: 'inherit' });

    // 从 stdin 解压，并用 cwd 指目标目录——**不要把任何 Windows 路径交给 tar**。
    // 两个坑叠在一起：`tar xzf E:\a.tgz` 会把 `E:` 当成远程主机（rsh 语法）；
    // 而 `-C E:\dir` 在 MSYS 里会被反转义成 `E\:\\dir`。只用 stdin + cwd 才两边都躲开。
    execFileSync('tar', ['xzf', '-'], { cwd: APPS_DIR, input: fs.readFileSync(tgz) });

    // tarball 顶层目录是 `<repo>-<sha>`。
    const extracted = fs.readdirSync(APPS_DIR).filter((n) => n.startsWith(`${app.repo.split('/')[1]}-`));
    if (extracted.length === 0) {
        throw new Error(`解压后找不到 ${app.repo} 的目录`);
    }
    fs.renameSync(path.join(APPS_DIR, extracted[0]), dir);
    return dir;
}

function collectSources(dir) {
    const out = [];
    const walk = (d) => {
        for (const ent of fs.readdirSync(d, { withFileTypes: true })) {
            if (ent.isDirectory()) {
                if (!SKIP_DIR.has(ent.name)) {
                    walk(path.join(d, ent.name));
                }
                continue;
            }
            if (SOURCE_EXT.has(path.extname(ent.name))) {
                out.push(path.join(d, ent.name));
            }
        }
    };
    walk(dir);
    return out;
}

// ---------------------------------------------------------------- 解析

const RE_IMPORT = /import\s+([\s\S]*?)\s+from\s+['"]([^'"]+)['"]/g;
const RE_DYNAMIC = /\bimport\s*\(\s*['"]([^'"]+)['"]\s*\)/g;
const RE_INVOKE = /\binvoke\s*(?:<[^>]*>)?\s*\(\s*['"]([^'"]+)['"]/g;
const RE_LISTEN = /\blisten\s*(?:<[^>]*>)?\s*\(\s*['"]([^'"]+)['"]/g;
const RE_PLUGIN_LITERAL = /['"]plugin:[a-z0-9-]+\|[a-z0-9_]+['"]/g;
const RE_GLOBAL_API = /__TAURI_INTERNALS__|__TAURI__/g;

function parseNamed(clause) {
    // 只关心具名导入：默认导入与命名空间导入在符号检查里没有意义。
    const m = clause.match(/\{([\s\S]*?)\}/);
    if (!m) {
        return [];
    }
    return m[1]
        .split(',')
        .map((s) => s.trim().replace(/^type\s+/, ''))
        .filter(Boolean)
        .map((s) => {
            const as = s.split(/\s+as\s+/);
            return { imported: as[0].trim(), local: (as[1] ?? as[0]).trim() };
        });
}

// ---------------------------------------------------------------- 单个应用

function analyze(app) {
    const dir = ensureSource(app);
    const files = collectSources(dir);

    const specifiers = new Map(); // tauri 说明符 → { count, symbols: Map }
    const natives = new Map();    // 非 tauri 说明符（用于确认「真应用」的依赖面）
    const commands = new Map();
    const events = new Map();

    // 只能运行期暴露的两类：环境探测用的全局标记、绕过 JS 包装层的 `plugin:` 直调。
    // 它们不会让 tsc 报错（字面量字符串谁都能写），但会让迁移后的应用**行为变了而不报错**。
    const globalProbes = [];
    const pluginLiterals = [];

    const bump = (map, key) => map.set(key, (map.get(key) ?? 0) + 1);

    for (const file of files) {
        const text = fs.readFileSync(file, 'utf8');

        for (const m of text.matchAll(RE_IMPORT)) {
            const spec = m[2];
            if (!spec.startsWith('@tauri-apps/')) {
                bump(natives, spec);
                continue;
            }
            if (!specifiers.has(spec)) {
                specifiers.set(spec, { count: 0, symbols: new Map() });
            }
            const slot = specifiers.get(spec);
            slot.count += 1;
            for (const { imported } of parseNamed(m[1])) {
                bump(slot.symbols, imported);
            }
        }

        for (const m of text.matchAll(RE_DYNAMIC)) {
            if (m[1].startsWith('@tauri-apps/')) {
                if (!specifiers.has(m[1])) {
                    specifiers.set(m[1], { count: 0, symbols: new Map() });
                }
                specifiers.get(m[1]).count += 1;
            }
        }

        for (const m of text.matchAll(RE_INVOKE)) {
            bump(commands, m[1]);
        }
        for (const m of text.matchAll(RE_LISTEN)) {
            bump(events, m[1]);
        }
        for (const m of text.matchAll(RE_PLUGIN_LITERAL)) {
            pluginLiterals.push({ file: path.relative(dir, file).split(path.sep).join('/'), literal: m[0].slice(1, -1) });
        }
        for (const m of text.matchAll(RE_GLOBAL_API)) {
            globalProbes.push({ file: path.relative(dir, file).split(path.sep).join('/'), token: m[0] });
        }
    }

    // 符号检查：模块能映射到有类型的本库模块时，逐个名字对账。
    const imports = [];
    for (const [spec, slot] of [...specifiers].sort((a, b) => b[1].count - a[1].count)) {
        const mapped = MODULE_MAP[spec];
        const oriel = mapped?.oriel ?? null;
        const exports = oriel ? EXPORTS.get(oriel) : null;

        const symbols = [...slot.symbols]
            .sort((a, b) => b[1] - a[1])
            .map(([name, count]) => ({
                name,
                count,
                ok: exports ? exports.has(name) : null,
            }));

        imports.push({
            tauri: spec,
            oriel,
            gap: mapped ? (mapped.gap ?? null) : '未登记的模块（映射表里没有）',
            known: Boolean(mapped),
            files: slot.count,
            symbols,
        });
    }

    return {
        slug: app.slug,
        repo: app.repo,
        sha: app.sha,
        covers: app.covers,
        files: files.length,
        imports,
        commands: [...commands].sort((a, b) => b[1] - a[1]),
        events: [...events].sort((a, b) => b[1] - a[1]),
        globalProbes,
        pluginLiterals,
        topDeps: [...natives].sort((a, b) => b[1] - a[1]).slice(0, 8),
    };
}

// ---------------------------------------------------------------- 改写与类型检查

function rewrite(app, result) {
    const src = path.join(APPS_DIR, app.slug);
    const dst = path.join(MIGRATED_DIR, app.slug);

    // **不做递归删除**。两个理由：
    //   1. 删 200 多个文件在带安全删除垫片的环境里会被直接拒掉（B7 第二次跑就撞上了），
    //      工具因此变成"只能跑一次"；
    //   2. 覆盖本身已经足够——`cpSync(force)` 会把改过的文件盖回去。
    // 残留的风险是"改了 SHA 但旧文件还在"，那会让结论不可信；所以用标记文件显式拦住，
    // 而不是靠删除。宁可报错让人手动清，也不要静默给出混合了两版源码的结果。
    const marker = path.join(dst, '.drill-sha');
    const rel = path.relative(ROOT, dst).split(path.sep).join('/');
    if (fs.existsSync(marker)) {
        const recorded = fs.readFileSync(marker, 'utf8').trim();
        if (recorded !== app.sha) {
            throw new Error(
                `${rel} 是 ${recorded.slice(0, 8)} 的旧产物，与清单里的 ${app.sha.slice(0, 8)} 不一致。` +
                `请先删除该目录再重跑（残留文件会让结论混合两版源码）。`);
        }
    }

    fs.mkdirSync(dst, { recursive: true });
    fs.cpSync(src, dst, { recursive: true, force: true });
    fs.writeFileSync(marker, app.sha);

    const replaceable = new Map();
    for (const item of result.imports) {
        if (item.oriel) {
            replaceable.set(item.tauri, `@orielweb/api/${item.oriel}`);
        }
    }

    let changed = 0;
    for (const file of collectSources(dst)) {
        let text = fs.readFileSync(file, 'utf8');
        let next = text;
        for (const [from, to] of replaceable) {
            next = next.split(`'${from}'`).join(`'${to}'`).split(`"${from}"`).join(`"${to}"`);
        }
        if (next !== text) {
            fs.writeFileSync(file, next);
            changed += 1;
        }
    }

    return { dir: dst, changed };
}

/**
 * 只保留「与本库有关」的诊断。
 *
 * 真实应用没有 node_modules 时，tsc 会先吐几千条找不到 react/vue 的错误。把它们滤掉
 * 不是美化输出——**留下它们会让真正的卡点看不见**，而这正是 B7 要避免的事。
 */
function typecheck(app, migrated) {
    const tsconfig = path.join(migrated, 'tsconfig.drill.json');
    const rel = path.relative(migrated, TYPES_DIR).split(path.sep).join('/');

    fs.writeFileSync(tsconfig, JSON.stringify({
        compilerOptions: {
            noEmit: true,
            skipLibCheck: true,
            strict: false,
            allowJs: true,
            checkJs: false,
            target: 'ES2022',
            module: 'ESNext',
            moduleResolution: 'Bundler',
            jsx: 'preserve',
            resolveJsonModule: true,
            baseUrl: '.',
            paths: { '@orielweb/api/*': [`${rel}/*`] },
        },
        include: ['src/**/*', 'app/**/*', '*.ts', '*.tsx'],
        exclude: ['node_modules', 'src-tauri', 'target', 'dist'],
    }, null, 2));

    const configFile = ts.readConfigFile(tsconfig, ts.sys.readFile);
    const parsed = ts.parseJsonConfigFileContent(configFile.config, ts.sys, migrated);
    const program = ts.createProgram(parsed.fileNames, parsed.options);
    const checker = program.getTypeChecker();
    const diags = ts.getPreEmitDiagnostics(program);

    const typesAbs = path.resolve(TYPES_DIR);
    const isOurFile = (file) => {
        const abs = path.resolve(file);
        return abs.startsWith(typesAbs);
    };

    /**
     * 这条诊断是不是真的和本库有关。
     *
     * 判据是**符号解析出来的声明在不在本库的 d.ts 里**，而不是「这行文字里出现了某个导入名」。
     * 后者在真实应用上完全不可用：`useState<Foo>(...)` 那一行只要碰巧含了 `open`，
     * 就会被当成与本库有关——实测把 4 条真信号埋进了 50 多条 React 噪音里。
     */
    const touchesOriel = (d) => {
        if (!d.file || d.start === undefined) {
            return false;
        }

        let node = ts.findPrecedingToken(d.start, d.file) ?? ts.findNextToken(d.start, d.file);
        for (let depth = 0; node && depth < 6; depth += 1, node = node.parent) {
            if (!ts.isIdentifier(node) && !ts.isPropertyAccessExpression(node)) {
                continue;
            }
            let symbol = checker.getSymbolAtLocation(node);
            if (!symbol) {
                continue;
            }
            if (symbol.flags & ts.SymbolFlags.Alias) {
                symbol = checker.getAliasedSymbol(symbol);
            }
            if (symbol.declarations?.some((dec) => isOurFile(dec.getSourceFile().fileName))) {
                return true;
            }
        }
        return false;
    };

    const kept = [];
    for (const d of diags) {
        const msg = ts.flattenDiagnosticMessageText(d.messageText, ' ');
        const file = d.file?.fileName ?? '';

        // ① 模块级：`@orielweb/api/*` 解析不到、或缺某个导出；
        //    以及**没被改写的** `@tauri-apps/*`（映射表里没有 = 本库没有这个模块）。
        //    这两类是「迁移后编译不过」的主因，必须留。
        const moduleLevel = msg.includes('@orielweb') || msg.includes('@tauri-apps');
        // ② 声明级：诊断落在本库的 d.ts 里（我们的类型自己有矛盾）。
        const inOurTypes = file !== '' && isOurFile(file);
        // ③ 调用级：报错位置的符号能解析到本库声明（参数类型/返回值用错）。
        const callLevel = d.file && d.start !== undefined && touchesOriel(d);

        if (moduleLevel || inOurTypes || callLevel) {
            kept.push({
                code: d.code,
                file: file === '' ? '' : path.relative(migrated, file).split(path.sep).join('/'),
                message: msg.slice(0, 220),
            });
        }
    }

    return kept;
}

// ---------------------------------------------------------------- 主流程

fs.mkdirSync(DRILL, { recursive: true });
const manifest = JSON.parse(fs.readFileSync(path.join(HERE, 'apps.json'), 'utf8'));
const apps = manifest.apps.filter((a) => !onlyApp || a.slug === onlyApp);

const results = [];
for (const app of apps) {
    console.log(`\n=== ${app.slug} (${app.repo}@${app.sha.slice(0, 8)})`);
    const result = analyze(app);
    console.log(`  源文件 ${result.files} 个，Tauri 模块 ${result.imports.length} 个，自定义 invoke ${result.commands.length} 个`);

    const { dir, changed } = rewrite(app, result);
    result.migratedFiles = changed;
    result.migratedDir = path.relative(ROOT, dir);

    if (!noTypecheck) {
        result.diagnostics = typecheck(result, dir);
        console.log(`  改写 ${changed} 个文件；与本库相关的诊断 ${result.diagnostics.length} 条`);
    }

    results.push(result);
}

// ---- 汇总：按「模块」和「符号」两个维度聚合，后者才是频率排行的依据。

const byModule = new Map();
const bySymbol = new Map();

for (const r of results) {
    for (const item of r.imports) {
        const key = item.tauri;
        if (!byModule.has(key)) {
            byModule.set(key, { tauri: key, oriel: item.oriel, gap: item.gap, files: 0, apps: new Set() });
        }
        const m = byModule.get(key);
        m.files += item.files;
        m.apps.add(r.slug);

        for (const s of item.symbols) {
            const sk = `${item.tauri}#${s.name}`;
            if (!bySymbol.has(sk)) {
                bySymbol.set(sk, { tauri: item.tauri, oriel: item.oriel, symbol: s.name, ok: s.ok, count: 0, apps: new Set() });
            }
            const y = bySymbol.get(sk);
            y.count += s.count;
            y.apps.add(r.slug);
        }
    }
}

const report = {
    generatedAt: new Date().toISOString(),
    apps: results.map((r) => ({ ...r, imports: r.imports.map((i) => ({ ...i })) })),
    summary: {
        modules: [...byModule.values()]
            .map((m) => ({ ...m, apps: [...m.apps] }))
            .sort((a, b) => b.files - a.files),
        symbols: [...bySymbol.values()]
            .map((s) => ({ ...s, apps: [...s.apps] }))
            .sort((a, b) => b.count - a.count),
    },
};

fs.mkdirSync(DRILL, { recursive: true });
fs.writeFileSync(path.join(DRILL, 'report.json'), JSON.stringify(report, null, 2));

// ---- 控制台摘要（人读的部分）

console.log('\n\n########## 模块面（按使用文件数） ##########\n');
console.log('Tauri 模块                          → 本库            文件数  涉及应用');
for (const m of report.summary.modules) {
    const target = m.oriel ? `@orielweb/api/${m.oriel}` : '（缺）';
    console.log(`${m.tauri.padEnd(36)}→ ${target.padEnd(22)}${String(m.files).padStart(4)}    ${m.apps.join(', ')}`);
}

console.log('\n\n########## 缺失符号（真实调用点要、我们没有） ##########\n');
const missing = report.summary.symbols.filter((s) => s.ok === false);
if (missing.length === 0) {
    console.log('（无）');
} else {
    for (const s of missing) {
        console.log(`${(s.oriel ?? '?').padEnd(14)} ${s.symbol.padEnd(22)} ${String(s.count).padStart(3)} 次  ${s.apps.join(', ')}`);
    }
}

console.log('\n\n########## 自定义 invoke 命令名（要靠 C# 侧登记） ##########\n');
for (const r of results) {
    console.log(`--- ${r.slug}`);
    for (const [name, count] of r.commands.slice(0, 12)) {
        console.log(`  ${name.padEnd(32)} ${count}`);
    }
}

console.log('\n\n########## 只能运行期暴露的用法 ##########\n');
for (const r of results) {
    const globals = new Map();
    for (const g of r.globalProbes) {
        globals.set(g.token, [...(globals.get(g.token) ?? []), g.file]);
    }
    console.log(`--- ${r.slug}`);
    if (globals.size === 0) {
        console.log('  环境探测：无');
    }
    for (const [token, files] of globals) {
        console.log(`  环境探测 ${token}  ${files.length} 处：${[...new Set(files)].slice(0, 3).join(', ')}`);
    }
    if (r.pluginLiterals.length > 0) {
        console.log(`  plugin 直调 共 ${r.pluginLiterals.length} 处（按文件的统计见下一节）`);
    }
}

console.log('\n\n########## 测试改动点（按文件，迁移后必须跟着改） ##########\n');
// 只看 plugin: 字面量还不够——真正决定"要改多少测试"的是它们**分布在哪些文件**。
// 测试文件里的那一批是迁移后必红的（应用代码换了命令名，桩还停在旧名字），
// 源码里的是运行期行为差异。两种都要改，但改法不同，所以分开计数。
for (const r of results) {
    const isTestFile = (f) =>
        /(^|\/)(__tests__|tests?)\//.test(f) || /\.(test|spec)\.[cm]?[jt]sx?$/.test(f);

    const byFile = new Map();
    for (const p of r.pluginLiterals) {
        byFile.set(p.file, (byFile.get(p.file) ?? 0) + 1);
    }

    console.log(`--- ${r.slug}`);
    if (byFile.size === 0) {
        console.log('  无（没有直接桩 Tauri 线格式的代码）');
        continue;
    }

    const entries = [...byFile].sort((a, b) => b[1] - a[1]);
    const testEntries = entries.filter(([f]) => isTestFile(f));
    const total = entries.reduce((sum, [, n]) => sum + n, 0);
    const testTotal = testEntries.reduce((sum, [, n]) => sum + n, 0);

    console.log(
        `  ${total} 处 / ${byFile.size} 个文件；其中测试文件 ${testEntries.length} 个、${testTotal} 处`,
    );
    for (const [f, n] of entries.slice(0, 6)) {
        console.log(`  ${isTestFile(f) ? '[测试]' : '[源码]'} ${f.padEnd(52)} ${n} 处`);
    }
}

if (!noTypecheck) {
    console.log('\n\n########## 与本库相关的 tsc 诊断 ##########\n');
    for (const r of results) {
        console.log(`--- ${r.slug}（${r.diagnostics.length} 条）`);
        for (const d of r.diagnostics.slice(0, 25)) {
            console.log(`  TS${d.code} ${d.file}: ${d.message}`);
        }
    }
}

console.log(`\n报告：${path.relative(ROOT, path.join(DRILL, 'report.json'))}`);
