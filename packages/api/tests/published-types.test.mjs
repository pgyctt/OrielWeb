// 发布类型面的对齐断言（B13 的同类收尾）。
//
// 为什么需要这份测试：`tauri-alignment.test.mjs` 比对的是**运行期导出**
// （`import * as ours`），而 `Event<T>` / `UnlistenFn` 这类是纯类型——运行期
// 根本看不到，于是它们既不进对齐清单、也不会被任何一条运行期断言挡住。
// 结果是"类型缺了"只在消费者写下 `import type { Event }` 时才暴露，
// 而那时包已经发出去了。
//
// 这里读**真正会被装进 tarball 的 `types/*.d.ts`**（即 `npm run build:types`
// 的产物），断言两件事：
//   1. Tauri 有的那批类型名，我们也有；名字必须逐字符一致（迁移代码不改标注）。
//   2. 我们**没有**把内部实现细节当成公开类型导出去。
//
// **`types/` 缺失时整组跳过（skip）而不是失败**：CI 的顺序是
// `npm test` → `npm run typecheck` → `npm run build:types` 比对，`types/` 的
// 新鲜度由最后那一步（`Frontend API types are up to date`）负责——它本来就会
// 重新生成再 diff，是更合适的守门位置。在这里硬要求 types/ 存在，只会让
// `npm test` 在干净检出上必然红（第一版就是这么把三个平台的 test 全弄挂的）。
//
// 而本地/发布前若已生成，这组就会真跑——`prepublishOnly` 的顺序恰好是
// build:types → check:types，那时 types/ 一定在。

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, existsSync } from 'node:fs';
import { join, dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const typesDir = join(root, 'types');

/** `types/` 未生成时整组跳过（理由见文件头）。 */
const typesReady = existsSync(join(typesDir, 'event.d.ts'));
const skip = typesReady
    ? false
    : 'types/ 未生成：这组断言只在 `npm run build:types` 之后跑（CI 由「types are up to date」那一步守门）';

/**
 * 读一份已发布的声明文件。
 *
 * @param {string} rel 相对 `types/` 的路径。
 * @returns {string}
 */
function readTypes(rel) {
    return readFileSync(join(typesDir, rel), 'utf8');
}

/**
 * 从声明文本里抓出 `export type X` / `export interface X` 的名字。
 *
 * 不用 TypeScript 编译器 API：这里只关心"名字在不在"，正则足够且不引入额外依赖。
 *
 * @param {string} source
 * @returns {Set<string>}
 */
function exportedTypeNames(source) {
    const names = new Set();
    const re = /^export\s+(?:type|interface)\s+([A-Za-z_$][\w$]*)/gm;
    let m;
    while ((m = re.exec(source)) !== null) {
        names.add(m[1]);
    }
    return names;
}

test('event.d.ts 导出 Tauri 的那批类型名', { skip }, () => {
    const names = exportedTypeNames(readTypes('event.d.ts'));

    // 这一组**必须与 `@tauri-apps/api/event` 的导出逐字符一致**：
    // 迁移代码里的 `import type { Event, UnlistenFn } from '@tauri-apps/api/event'`
    // 换包之后只改模块名，其余一个字符都不该动。
    for (const name of ['Event', 'EventTarget', 'EventCallback', 'UnlistenFn', 'EventName', 'Options']) {
        assert.ok(
            names.has(name),
            `event.d.ts 缺少导出类型 ${name}（Tauri 有，迁移代码会直接 import 它）`);
    }
});

test('event.d.ts 不导出内部实现细节', { skip }, () => {
    const names = exportedTypeNames(readTypes('event.d.ts'));

    // Subscription 是包内的订阅记录形状。它此前确实被导出了——因为 TypeScript
    // 对 .js 的声明发射会把模块级每个 @typedef 都变成 export type，
    // `stripInternal` 也挡不住。改法是内联那个形状，而不是加 @internal。
    assert.ok(
        !names.has('Subscription'),
        'event.d.ts 不该导出 Subscription——那是包内实现细节，不是可用 API');
});

test('TauriEvent 在 d.ts 里仍是常量对象而不是 enum', { skip }, () => {
    // 反向确认这条测试本身没有"什么都通过"：Tauri 的 EventTarget 是 **type**，
    // 而我们的 TauriEvent 是 **const 对象**（运行期可枚举）。两者都不该混。
    const source = readTypes('event.d.ts');

    assert.match(source, /export const TauriEvent/, 'TauriEvent 应当是 const 对象（运行期可枚举）');
    assert.doesNotMatch(
        source,
        /export (?:declare )?enum TauriEvent/,
        'TauriEvent 不该是 enum——本库不用 enum，且运行期要能 Object.keys');
});
