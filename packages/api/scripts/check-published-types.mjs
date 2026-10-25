// 发布前的类型闸门：检查**真正会被装进 tarball 的那批 .d.ts**。
//
// 为什么需要它：包内 `npm run typecheck` 用的是 `skipLibCheck: true`（对源码是对的，
// 否则每次改 JSDoc 都会被 lib.dom 的变动打断），但 `skipLibCheck` 会把 `types/*.d.ts`
// 整个跳过——声明文件自己引用了个不存在的名字，本地一路绿，消费者那边一装就红。
// 0.4.14 发布前就是这么撞上的：`types/window.d.ts` 用了 `LogicalSize` 却没 import
// （模块末尾有 `export { LogicalSize } from './dpi.js'`，声明发射器认定它"已经在了"，
// 不再补 import），以及 `types/internal.d.ts` 引用 `./types.js` 而副本没被打包。
// 两者在 `skipLibCheck: false` 下分别是 TS2304 / TS2307。
//
// 这个脚本做两件事：
//   1. 按 `exports` 逐条核对 `types` / `default` 指向的文件真实存在——`exports` 写错
//      一个字符，消费方只会看到"找不到模块"，而 npm 上已经发出去的版本撤不回。
//   2. 用 `skipLibCheck: false` 把全部声明文件过一遍 tsc，把错误按文件打出来。
//
// 退出码非 0 即失败，由 `prepublishOnly` 拦住 `npm publish`。

import { readFileSync, existsSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import ts from 'typescript';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const pkg = JSON.parse(readFileSync(join(root, 'package.json'), 'utf8'));

const problems = [];

// ---- 1. exports 自洽 ----
const typeFiles = [];
const entrypoints = Object.entries(pkg.exports ?? {});
if (entrypoints.length === 0) problems.push('package.json 没有 exports');

for (const [subpath, target] of entrypoints) {
  // `./package.json` 是给打包器/工具读的包自身元数据（npm 官方惯例），
  // 它没有也不需要 types 声明——豁免，其余条目一律要求 types + default。
  if (subpath === './package.json') continue;

  for (const field of ['types', 'default']) {
    const rel = target?.[field];
    if (typeof rel !== 'string') {
      problems.push(`exports["${subpath}"] 缺 ${field}`);
      continue;
    }
    const abs = join(root, rel);
    if (!existsSync(abs)) {
      problems.push(`exports["${subpath}"].${field} → ${rel} 不存在`);
      continue;
    }
    if (field === 'types') typeFiles.push(abs);
  }
}

// ---- 2. 声明文件自洽 ----
if (typeFiles.length > 0) {
  const program = ts.createProgram(typeFiles, {
    target: ts.ScriptTarget.ES2022,
    module: ts.ModuleKind.ESNext,
    moduleResolution: ts.ModuleResolutionKind.Bundler,
    lib: ['lib.es2022.d.ts', 'lib.dom.d.ts'],
    strict: true,
    noEmit: true,
    // 关键：不跳过 .d.ts，否则这个脚本和 `npm run typecheck` 没有区别
    skipLibCheck: false,
    types: [],
  });

  const diagnostics = ts.getPreEmitDiagnostics(program);
  for (const d of diagnostics) {
    const message = ts.flattenDiagnosticMessageText(d.messageText, '\n');
    if (d.file && d.start !== undefined) {
      const { line, character } = d.file.getLineAndCharacterOfPosition(d.start);
      const file = d.file.fileName.replace(root + '/', '').replace(root + '\\', '');
      problems.push(`${file}:${line + 1}:${character + 1} TS${d.code} ${message}`);
    } else {
      problems.push(`TS${d.code} ${message}`);
    }
  }
}

if (problems.length > 0) {
  console.error(`✗ 声明文件检查失败（${problems.length} 项）：`);
  for (const p of problems) console.error('  - ' + p);
  process.exit(1);
}

console.log(`✓ ${entrypoints.length} 个入口、${typeFiles.length} 份声明文件自洽`);
