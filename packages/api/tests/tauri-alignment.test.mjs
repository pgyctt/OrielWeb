// **一致性测试**：把 `@tauri-apps/api` 与官方插件当成**参照实现**，机械比对导出面。
//
// 这份测试存在的理由：「模块名与 @tauri-apps/api 一一对应，迁移只需替换 import 前缀」
// 是一句**可证伪**的话。写进文档只是声明；拿真正的 Tauri 包跑一遍比对才是取证。
// 而且它会随 Tauri 升级而失效——那正是它该做的事：Tauri 加了新 API 时，
// 我们要么实现、要么把它记进下面的缺口清单，而不是让差异静默存在。
//
// 规则（两条方向都要管）：
//
//   1. **参照有、我们没有** → 必须在 GAPS 里列出来（带原因）。否则测试失败。
//   2. **我们有、参照没有** → 必须在 EXTRAS 里列出来（带原因）。否则测试失败。
//
// 第二条同样重要：它是"我们偷偷发明了一套自己的 API"的唯一屏障。
//
// 运行：node --test packages/api/tests/tauri-alignment.test.mjs
// 需要参照实现已安装（devDependencies）。**没装时整份跳过**并在输出里说明——
// 离线环境下不该让测试变成红的，但 CI 装了，所以 CI 会强制检查。

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

/** 加载参照实现；没装时返回 null（调用方跳过）。 */
async function tryImport(specifier) {
    try {
        return await import(specifier);
    } catch {
        return null;
    }
}

/**
 * 我们实现的模块 → 对应的参照模块。
 *
 * @type {Array<{ ours: string, theirs: string, label: string }>}
 */
const MODULES = [
    { ours: '../src/core.js', theirs: '@tauri-apps/api/core', label: 'core' },
    { ours: '../src/event.js', theirs: '@tauri-apps/api/event', label: 'event' },
    { ours: '../src/dpi.js', theirs: '@tauri-apps/api/dpi', label: 'dpi' },
    { ours: '../src/window.js', theirs: '@tauri-apps/api/window', label: 'window' },
    // 页面建窗。它是 `Window` 的**子类**——方法比对要能沿原型链走（见下方 collectMethods）。
    { ours: '../src/webviewWindow.js', theirs: '@tauri-apps/api/webviewWindow', label: 'webviewWindow' },
    // 菜单（B6a 弹出 + B6b 菜单栏）。两半都做了：popup / setAsAppMenu / setAsWindowMenu。
    // 结构性差异不是缺口：菜单树是页面侧纯数据（没有 rid），"返回上一份菜单"由页面记账，
    // setAsWindowMenu 在 macOS 上抛 not_supported（菜单在那边是应用级的，Tauri 同）。
    { ours: '../src/menu.js', theirs: '@tauri-apps/api/menu', label: 'menu' },
    { ours: '../src/path.js', theirs: '@tauri-apps/api/path', label: 'path' },
    { ours: '../src/app.js', theirs: '@tauri-apps/api/app', label: 'app' },
    { ours: '../src/mocks.js', theirs: '@tauri-apps/api/mocks', label: 'mocks' },
    // image 模块此前**根本没登记**（清单缺口，2026-10-18 补）：
    // 于是"本库缺 `@tauri-apps/api/image`"这件事没有任何测试盯着。
    { ours: '../src/image.js', theirs: '@tauri-apps/api/image', label: 'image' },
    { ours: '../src/fs.js', theirs: '@tauri-apps/plugin-fs', label: 'fs' },
    { ours: '../src/dialog.js', theirs: '@tauri-apps/plugin-dialog', label: 'dialog' },
    { ours: '../src/clipboard.js', theirs: '@tauri-apps/plugin-clipboard-manager', label: 'clipboard' },
    { ours: '../src/opener.js', theirs: '@tauri-apps/plugin-opener', label: 'opener' },
    { ours: '../src/notification.js', theirs: '@tauri-apps/plugin-notification', label: 'notification' },
    { ours: '../src/autostart.js', theirs: '@tauri-apps/plugin-autostart', label: 'autostart' },
    // 托盘（B12）：TrayIcon 类，形状与 Tauri 一致；rid 是补壳（返回 id，本库没有资源表），
    // setTempDirPath 是补壳（本库不落盘图标），setMenuOnLeftClick 仅 Windows——都登记在下方两张表里。
    { ours: '../src/tray.js', theirs: '@tauri-apps/api/tray', label: 'tray' },
];

/**
 * **参照有、我们整个模块都没有**的那些。
 *
 * 为什么不直接塞进 `GAPS`：`GAPS` 是按模块记"少了哪个导出"，前提是**模块已登记**。
 * 这四个模块此前**连登记都没有**——于是"本库没有 tray/menu/webview/webviewWindow"
 * 这件事没有任何测试盯着，只在文档里写着。名字缺失比导出缺失更彻底：
 * 迁移代码的 `import { Tray } from '@tauri-apps/api/tray'` 会直接解析失败。
 *
 * 每条必须带 `batch`（对应 docs/TAURI2-ALIGNMENT-EXECUTION-PLAN 的批次），
 * 否则这份清单会退化成"以后再说"。
 *
 * @type {Array<{ label: string, theirs: string, ours: string, batch: string, note: string }>}
 */
const PLANNED_MODULES = [
    {
        label: 'webview',
        theirs: '@tauri-apps/api/webview',
        ours: '../src/webview.js',
        // 批次写 B24（收口批）：这条不是"还没排到"，而是"复核后仍然不做"。
        // 写 B24 是为了让"批次新鲜度"那条断言能验证它——见文件末尾那条测试。
        batch: 'B24',
        note: 'webview 的独立概念。本库**窗口即 webview**，没有"一个窗口挂多个 webview"；' +
            'WebviewWindow 已经作为 Window 的子类提供，但独立的 Webview 类与 getAllWebviews 不造。' +
            '**B24 复核结论：继续不做。** 理由是它缺的不是方法而是模型（多 webview 需要宿主侧重做窗口/坐标/事件路由），' +
            '而 webview 级的方法本身在 B16（setZoom / clearAllBrowsingData）与 B22（其余窗口开关）' +
            '已经挂到 WebviewWindow 上——真要用 `getCurrentWebview()` 的地方换成 `getCurrentWindow()` 即可。' +
            '迁移演练里 pier-dock 唯一的模块级缺口就是它，而它那处 `getCurrentWebview().listen(...)` ' +
            '改成 `getCurrentWindow().listen(...)` 后类型检查就过了（见 docs/MIGRATION-FINDINGS.md §5）。',
    },
];

/**
 * 常量对象的逐键比对。
 *
 * 导出面测试只比 `Object.keys(module)`——`TauriEvent` 少三个键、`UserAttentionType`
 * 少一个反向映射，**在那一层完全看不出来**（对象本身是一个导出名）。
 * 2026-10-18 审计发现的 `TauriEvent` 缺 `WINDOW_SUSPENDED`/`WINDOW_RESUMED`/`WEBVIEW_CREATED`
 * 就是这么漏掉的：Tauri 2.x 新增常量，本库静默落后。
 *
 * @type {Array<{ module: string, theirs: string, name: string }>}
 */
const CONST_OBJECTS = [
    { module: '../src/event.js', theirs: '@tauri-apps/api/event', name: 'TauriEvent' },
    { module: '../src/window.js', theirs: '@tauri-apps/api/window', name: 'UserAttentionType' },
    // B13 补的三个常量（值与 Tauri 逐键一致，方法在 B22 落地）：
    { module: '../src/window.js', theirs: '@tauri-apps/api/window', name: 'ProgressBarStatus' },
    { module: '../src/window.js', theirs: '@tauri-apps/api/window', name: 'Effect' },
    { module: '../src/window.js', theirs: '@tauri-apps/api/window', name: 'EffectState' },
    // BaseDirectory 的键值同样是迁移代码会直接引用的（BaseDirectory.AppData = 14）
    { module: '../src/path.js', theirs: '@tauri-apps/api/path', name: 'BaseDirectory' },
    // B17：SeekMode 是句柄族的位置枚举，迁移代码会直接写 SeekMode.End
    { module: '../src/fs.js', theirs: '@tauri-apps/plugin-fs', name: 'SeekMode' },
    // B18：ScheduleEvery 是字符串枚举（Year = 'year' …），迁移代码会直接写 ScheduleEvery.Hour
    { module: '../src/notification.js', theirs: '@tauri-apps/plugin-notification', name: 'ScheduleEvery' },
    // B23：BundleType 是产物类型的枚举（Nsis = 'nsis' …），迁移代码会直接拿它比对
    { module: '../src/app.js', theirs: '@tauri-apps/api/app', name: 'BundleType' },
];

/**
 * **参照有、我们没有**的导出——每一条都要有原因。
 *
 * 这些是"迁移时会撞上的缺口"。写成清单而不是散在文档里：文档会过期，而这里是测试。
 *
 * @type {Record<string, Record<string, string>>}
 */
const GAPS = {
    core: {
        PluginListener: '插件监听器的句柄类；本库没有插件体系',
        Resource: '资源句柄表；本库没有句柄模型（fs 那边同一条理由）',
        SERIALIZE_TO_IPC_FN: 'Tauri 内部序列化协议的钩子，本库没有对应物',
        addPluginListener: '插件的监听注册协议，同上',
        checkPermissions: '权限查询；本库的能力是构建期配置（UseCapabilities），没有运行时查询',
        requestPermissions: '同上',
        transformCallback: '回调注册表（配合 Channel），本库没有',
    },
    event: {},
    dpi: {
        // Size/Position 是纯类型别名，B13 已补（dpi.d.ts）——运行期 Object.keys 看不到类型，
        // 所以它们从不构成"导出缺口"，这里也不再列。
    },
    window: {
        // B13 已补：CloseRequestedEvent（类，preventDefault 记录意图）、Effect/EffectState/
        // ProgressBarStatus（常量对象，与 Tauri 逐键同值；方法在 B22 落地）。
    },
    path: {
        // BaseDirectory 已补（B13）：枚举对象，值与 Tauri 逐键同值，fs 的 baseDir 选项由此串接。
    },
    app: {
        // B23 之后**没有缺口**：
        //   * BundleType / getBundleType —— 产物类型按运行期事实推断（.app / Velopack /
        //     AppImage），也可以用程序集元数据显式声明；观察不出来时返回 null。
        //   * defaultWindowIcon —— 来源是可执行文件旁的 app.png / app.ico。
        //   * setDockVisibility —— macOS 的 NSApplication.setActivationPolicy:。
        //   * fetchDataStoreIdentifiers / removeDataStore —— macOS 12+ 的 WKWebsiteDataStore，
        //     标识符登记在宿主侧（WebKit 的全局枚举是异步回调，与同步 IPC 接不上）。
        //   * onBackButtonPress —— 订阅是真的（listen('back-button')），桌面端永不触发。
    },
    mocks: {},
    fs: {
        // BaseDirectory 已补（B13，从 path re-export）。
        // FileHandle / SeekMode / create / open / readTextFileLines 已补（B17 句柄族）。
        // watch / watchImmediate 已补（B20，宿主侧 FileSystemWatcher 登记表 +
        // oriel://fs-watch/<id> 事件；文件 URL 与 baseDir 都走同一套解析）。
        startAccessingSecurityScopedResource: 'macOS 沙箱的 security-scoped bookmark；本库还没有',
        stopAccessingSecurityScopedResource: '同上',
    },
    dialog: {},
    clipboard: {
        // B19 之后**没有缺口**：readImage / writeImage / writeHtml / writeSecret 四个都做了。
        // 图片走 PNG：宿主读到的剪贴板位图统一编码成 PNG（Windows 那侧用本库的
        // OrielPngEncoder），页面侧 Image 持有的正是编码字节。
    },
    opener: {},
    notification: {
        // B18 之后只剩**移动端专有的 8 个**（决策见 TAURI2-ALIGNMENT-DECISIONS §1.6）。
        // 桌面端没有通知渠道 / 重要级 / 可见性 / 动作类型这些概念：Android 的 channel 模型与
        // iOS 的 action type 在桌面上没有对应物，造出来只能是空壳。
        //
        // 已实现（不再列在这里）：active / pending / cancel / cancelAll / removeActive /
        // removeAllActive（宿主侧登记表）、onNotificationReceived（真事件）、
        // onAction（订阅时抛 not_supported，理由见 notification.js）、
        // Schedule / ScheduleEvery（宿主进程内的调度器）。
        Importance: 'Android 的通知重要级（桌面无对应概念）',
        Visibility: 'Android 的通知可见性（同上）',
        channels: 'Android 的通知渠道（同上）',
        createChannel: '同上',
        deleteChannel: '同上',
        listChannels: '同上',
        removeChannel: '同上',
        registerActionTypes: 'iOS / Android 的通知动作类型（同上）',
    },
    autostart: {},
    tray: {
        // Tauri 的 TrayIcon extends Resource，构造是 rid + id 双字段；本库的构造只有 id
        // （没有资源表）。导出面一致，差异在类的方法层（METHOD_DIVERGENCES）。
    },
};

/**
 * **B24 收口时的剩余缺口基线**——一份"锁定清单"。
 *
 * 为什么要有它：`GAPS` 本身只保证"缺了的都写了原因"，**不保证缺的只有这些**。
 * 一条新缺口只要"配一句原因"就能加进去，于是"缺口总数"这个真正该被盯的数字
 * 反而没人看。这里把它钉成常量：任何增减都必须改这份基线，而改它的人一定会
 * 读到下面这段"为什么只剩这些"。
 *
 * B24 的实地核对结果（与当时的计划说法**不一致**，以这里为准）：
 *
 *   * `core` 7 条——**没有插件体系**这一件事的全部投影（Resource / PluginListener /
 *     addPluginListener / transformCallback / checkPermissions / requestPermissions /
 *     SERIALIZE_TO_IPC_FN）。它们不是七个独立能力，是一个模型缺失的七个面。
 *   * `fs` 2 条——macOS 沙箱的 security-scoped bookmark。需要 open 面板签发的 bookmark
 *     与沙箱 entitlement，本库不做应用打包，给不出那个上下文。
 *   * `notification` 8 条——移动端专有（Android 的 channel / 重要级 / 可见性，iOS 的动作类型）。
 *     **其中 `Importance` / `Visibility` 是纯类型别名**，Tauri 自己也不把它们挂在运行期导出面上，
 *     所以 `Object.keys` 只看得到 6 条——导出面比对报的"缺口 6"与这里的 8 都对，只是口径不同。
 *
 * 计划文档原先写的是"只剩 core 7 + notification 8"，**漏了 fs 的 2 条**。
 * 这不是笔误而是当时没核对：fs 那两条从 B17 起就挂在清单里。收口的意义正在于此——
 * 把"以为的"换成"数出来的"。
 *
 * @type {Record<string, string[]>}
 */
const EXPECTED_GAPS = {
    core: [
        'PluginListener',
        'Resource',
        'SERIALIZE_TO_IPC_FN',
        'addPluginListener',
        'checkPermissions',
        'requestPermissions',
        'transformCallback',
    ],
    fs: [
        'startAccessingSecurityScopedResource',
        'stopAccessingSecurityScopedResource',
    ],
    notification: [
        'Importance',
        'Visibility',
        'channels',
        'createChannel',
        'deleteChannel',
        'listChannels',
        'removeChannel',
        'registerActionTypes',
    ],
};

/**
 * **我们有、参照没有**的导出——同样每条要有原因。
 *
 * 这一列不是"禁止清单"而是"有意的扩展清单"：加一个 Tauri 没有的名字**可以**，
 * 但必须在这里说明原因，否则它就是一处"偷偷发明了自己的 API"。
 *
 * 判据是两条：
 *   * **名字与 Tauri 同名时行为必须与它一致**（那是"一一对应"的全部含义）；
 *   * 新增的名字必须是**纯粹的加法**——不与 Tauri 的名字竞争同一个概念，
 *     且能说清"为什么调用方需要它"。
 */
const EXTRAS = {
    event: {
        __clearEventSubscriptions: '测试钩子：清掉内部订阅表，让 `clearMocks()` 之后不残留监听。双下划线前缀表明它不是公开 API',
        toHostEventName: 'Tauri 事件名 → 宿主事件名的映射函数。导出它是为了让调用方也能翻译（如自己发事件时）',
    },
    dpi: {
        logicalToPhysical: '单位换算。Tauri 只在 window.setSize/setPosition 内部做这件事，没有暴露出来',
        physicalToLogical: '同上',
        normalizeSize: '把"类实例或裸对象"归一成类实例。Tauri 的类型是 TS 联合类型（`Size`），运行期不归一',
        normalizePosition: '同上',
    },
    window: {
        getByLabel: '按 label 取窗口。Tauri 要 `getAllWindows().find(...)`；本库的 label 是寻址单位，所以给了一个直接入口',
        getFocusedWindow: '模块级的便利函数。Tauri 只提供静态方法 `Window.getFocusedWindow()`（本库也提供，两者等价）；' +
            '包内给一个模块级导出是为了让 `import { getFocusedWindow }` 这种写法也能用——' +
            '多一个入口不会与 Tauri 冲突（同名同义），而少一个会让一部分迁移代码在 import 处就报错',
    },
    mocks: {
        getMockWindowLabels: '读 mock 出来的窗口 label 列表，便于断言',
        isMocked: '当前是否装了 mock。前端单测靠它判断"我是不是在测试里"',
        mockEmit: '模拟宿主推事件。Tauri 的 mocks 里没有，而写前端测试时它是必需的（否则 listen 的回调永远不触发）',
    },
    tray: {
        __setDebugWarnings: '测试钩子：开关补壳警告（rid / setTempDirPath 的 debug 提示）。' +
            '双下划线前缀表明它不是公开 API。',
    },
};

test('对照表里的模块都能加载（本地包恒可用）', async () => {
    for (const module of MODULES) {
        const ours = await import(module.ours);
        assert.ok(ours, `${module.label}：本地模块加载失败`);
    }
});

test('与 @tauri-apps/api 的导出面一致（差异都在清单里）', async (t) => {
    const reference = await tryImport('@tauri-apps/api/core');

    if (reference === null) {
        t.skip('参照实现未安装（devDependencies）——CI 会装并强制检查');
        return;
    }

    const report = [];
    /** @type {string[]} */
    const problems = [];

    for (const module of MODULES) {
        const ours = await import(module.ours);
        const theirs = await tryImport(module.theirs);

        if (theirs === null) {
            problems.push(`${module.label}: 参照模块未安装（${module.theirs}）`);
            continue;
        }

        const ourNames = new Set(Object.keys(ours));
        const theirNames = new Set(Object.keys(theirs));

        const gaps = [...theirNames].filter((name) => !ourNames.has(name)).sort();
        const extras = [...ourNames].filter((name) => !theirNames.has(name)).sort();

        const documentedGaps = GAPS[module.label] ?? {};
        const documentedExtras = EXTRAS[module.label] ?? {};

        // 方向一：参照有、我们没有 → 必须在 GAPS 里
        for (const name of gaps.filter((n) => !(n in documentedGaps))) {
            problems.push(
                `${module.label}.${name}: Tauri 有、我们没有，且没记进 GAPS。` +
                '要么实现它，要么记进 GAPS 并写清原因——不要让它静默缺失。');
        }

        // 方向二：我们有、参照没有 → 必须在 EXTRAS 里
        for (const name of extras.filter((n) => !(n in documentedExtras))) {
            problems.push(
                `${module.label}.${name}: 我们独有，但没记进 EXTRAS。` +
                '包应当只提供 Tauri 有的名字；自己的扩展走桥接层。');
        }

        // GAPS 里也不该有已经实现的（说明清单过期了）
        for (const name of Object.keys(documentedGaps).filter((n) => ourNames.has(n))) {
            problems.push(`${module.label}.${name}: GAPS 里列着，但其实已经实现了（清单过期）。`);
        }

        report.push(`${module.label}: 参照 ${theirNames.size}，我有 ${ourNames.size}，缺口 ${gaps.length}`);
    }

    // 不变量：模块数与我们声明的一致
    assert.equal(report.length, MODULES.length);

    // dpi 的四个类 Tauri 从 window 模块也转出一份，本库照做（EXTRAS 里不再列它们）
    // 打印一行摘要，便于在 CI 日志里看到规模
    console.log(`[alignment] ${report.join(' | ')}`);

    // **一次报出全部问题**而不是第一条就停：一条条改要跑十几轮，
    // 而这份清单的价值就在于"一眼看全还差什么"。
    assert.deepEqual(problems, [], `导出面对照发现 ${problems.length} 处未记录差异：\n  - ${problems.join('\n  - ')}`);
});

test('未实现模块有登记，且登记与代码状态一致', async (t) => {
    // **这份断言的三条方向都要管**，缺一条账本就会腐化：
    //   1. 登记了的模块不得同时出现在 MODULES 里（否则"缺口"和"已实现"并存）；
    //   2. 每条都要有批次（否则是"以后再说"）；
    //   3. 源文件一旦出现，就必须从 PLANNED 移进 MODULES —— 实现了却留在
    //      "未实现"清单里，比没登记更糟：下一个人会以为它没做。
    const problems = [];

    const registered = new Set(MODULES.map((m) => m.label));

    for (const planned of PLANNED_MODULES) {
        if (registered.has(planned.label)) {
            problems.push(
                `${planned.label}: 同时出现在 PLANNED_MODULES 与 MODULES 里——` +
                '实现了就从 PLANNED 移走并补进 MODULES，别两边都留。');
        }

        if (!/^B\d/.test(planned.batch ?? '')) {
            problems.push(`${planned.label}: 没有计划批次（batch 要以 B<数字> 开头）。`);
        }

        const sourcePath = fileURLToPath(new URL(planned.ours, import.meta.url));
        if (existsSync(sourcePath)) {
            problems.push(
                `${planned.label}: 登记为"未实现"，但 ${planned.ours.replace('../', '')} 已经存在。` +
                '实现完请移进 MODULES（否则对齐账本会把已实现的东西算成缺口）。');
        }
    }

    // 模块数不变量：只剩 webview 一个仍在计划里（tray 已在 B12 实现并移进 MODULES；
    // 少一个说明有人删了条目而不是做了实现）
    assert.equal(PLANNED_MODULES.length, 1,
        'PLANNED_MODULES 应当登记 1 个未实现模块（webview）');

    assert.deepEqual(problems, [], `未实现模块登记有问题：\n  - ${problems.join('\n  - ')}`);

    // 参照模块名写错的话，清单会指向一个不存在的包——安装时顺便验一下。
    if ((await tryImport('@tauri-apps/api/core')) === null) {
        t.skip('参照实现未安装——CI 会装并强制检查模块名');
        return;
    }

    for (const planned of PLANNED_MODULES) {
        assert.notEqual(await tryImport(planned.theirs), null,
            `${planned.label}: 参照模块 ${planned.theirs} 加载不了，模块名可能写错了`);
    }
});

test('GAPS 只剩基线里那三组——"清零"要能数出来，不是说出来', () => {
    // 这一条是 B24 收口的产物。它盯的**不是"缺的有没有写原因"**（上面那条管那个），
    // 而是"缺的是不是只有这些"——`GAPS` 允许加条目，所以总数需要一个独立的事实来源，
    // 否则账本会一年后变成"一长串都写了原因的缺口"，而没人记得它当初应该是 17 条还是 70 条。
    const problems = [];

    // 方向一：基线里的每一组都要在 GAPS 里，且键集完全一致（多了少了都算变动）
    for (const [module, expected] of Object.entries(EXPECTED_GAPS)) {
        const actual = Object.keys(GAPS[module] ?? {}).sort();
        const wanted = [...expected].sort();

        for (const name of wanted.filter((n) => !actual.includes(n))) {
            problems.push(`${module}.${name}: 基线里有、GAPS 里没有（谁把原因删了？）`);
        }

        for (const name of actual.filter((n) => !wanted.includes(n))) {
            problems.push(
                `${module}.${name}: GAPS 里多出一条基线外的缺口——` +
                '要么它其实是能做的（那就做），要么把它加进 EXPECTED_GAPS 并写清为什么不做。');
        }
    }

    // 方向二：基线之外的模块必须一条不剩
    for (const [module, gaps] of Object.entries(GAPS)) {
        if (module in EXPECTED_GAPS) {
            continue;
        }

        for (const name of Object.keys(gaps)) {
            problems.push(
                `${module}.${name}: 这个模块本该**零缺口**（B24 已收口），` +
                '却又出现了未记录的差异——要么实现它，要么更新 EXPECTED_GAPS 的基线并说明。');
        }
    }

    const total = Object.values(EXPECTED_GAPS).reduce((sum, list) => sum + list.length, 0);

    assert.equal(total, 17, '基线条数变了：改这个数字时请同步改上面那段说明与 docs 的支持矩阵');
    assert.deepEqual(
        problems, [],
        `GAPS 与基线（core 7 + fs 2 + notification 8）不一致：\n  - ${problems.join('\n  - ')}`);
});

test('账本引用的批次都真的落地过（新鲜度）', () => {
    // 一条"批次号"只有在它对应的东西真发出去之后才有意义。往账本里写 B24 而
    // CHANGELOG 里没有 B24 那条，说明这批还停在本地——那样的账本读起来像完成了，
    // 实际上没有。这条测试把"批次号"与"发版记录"绑在一起。
    const changelog = readFileSync(new URL('../../../CHANGELOG.md', import.meta.url), 'utf8');

    // CHANGELOG 里的批次标记是中文全角括号（`（B22）`），也允许半角（手写时容易混）
    const shipped = new Set(
        [...changelog.matchAll(/[（(]B(\d+)[）)]/g)].map((match) => `B${match[1]}`));

    /** @type {Array<{ where: string, batch: string }>} */
    const references = [
        ...PLANNED_MODULES.map((planned) => ({
            where: `PLANNED_MODULES.${planned.label}`,
            batch: planned.batch,
        })),
        ...METHOD_GAP_GROUPS.flatMap((group) => (group.reason.match(/\bB\d+/g) ?? [])
            .map((batch) => ({ where: `METHOD_GAP_GROUPS（${group.methods[0] ?? '?'}…）`, batch }))),
    ];

    const stale = references.filter(({ batch }) => !shipped.has(batch));

    assert.deepEqual(
        stale.map(({ where, batch }) => `${where}: ${batch}`), [],
        '这些批次号在 CHANGELOG 里找不到落地记录（写的是"做完了"，但没有发版条目）：');
});

test('常量对象的键与值都与参照实现一致', async (t) => {
    // 导出面那一层只比"有没有这个导出名"，比不到对象内部。
    // 这里逐个对象比键集 + 值——Tauri 加常量时我们要么跟上，要么改这条断言并说明。
    if ((await tryImport('@tauri-apps/api/core')) === null) {
        t.skip('参照实现未安装——CI 会装并强制检查');
        return;
    }

    const problems = [];

    for (const entry of CONST_OBJECTS) {
        const ours = (await import(entry.module))[entry.name];
        const theirs = (await tryImport(entry.theirs))?.[entry.name];

        if (ours === undefined || theirs === undefined) {
            problems.push(`${entry.name}: 参照或本库缺少这个常量对象，比对无法进行。`);
            continue;
        }

        const ourKeys = new Set(Object.keys(ours));
        const theirKeys = new Set(Object.keys(theirs));

        for (const key of [...theirKeys].filter((k) => !ourKeys.has(k)).sort()) {
            problems.push(`${entry.name}.${key}: Tauri 有、我们没有。`);
        }

        for (const key of [...ourKeys].filter((k) => !theirKeys.has(k)).sort()) {
            problems.push(`${entry.name}.${key}: 我们独有，Tauri 没有——不要自造常量名。`);
        }

        for (const key of [...ourKeys].filter((k) => theirKeys.has(k))) {
            if (ours[key] !== theirs[key]) {
                problems.push(`${entry.name}.${key}: 值不一致（我们 ${JSON.stringify(ours[key])}，Tauri ${JSON.stringify(theirs[key])}）。`);
            }
        }
    }

    console.log(`[alignment] 逐键比对了 ${CONST_OBJECTS.length} 个常量对象`);
    assert.deepEqual(problems, [], `常量对象比对发现 ${problems.length} 处差异：\n  - ${problems.join('\n  - ')}`);
});

/**
 * 有意不实现的方法——每条要有原因。
 *
 * @type {Record<string, string>}
 */
const METHOD_DIVERGENCES = {
    close:
        '**同名不同义（menu）**：Tauri 的 `Menu.close()` 来自 `Resource`，释放的是 Rust 侧资源表里' +
        '那条记录（`rid`）。本库没有资源表，所以**不存在**要释放的宿主对象。' +
        '但本库仍然给了一个 `close()`，它做的是另一件事：**关掉当前正在显示的菜单**' +
        '（等价于用户按 Esc）。' +
        '为什么必须给：Windows 的 `popup()` 会阻塞到用户选择，UI 线程被占着时页面上的按钮点不到——' +
        '没有这条命令，页面就完全无法用代码关掉自己弹的菜单。' +
        '**这是一处有意的语义分歧，迁移代码里的 `await menu.close()` 不会抛错，但也不会释放任何资源**' +
        '（本来就没有资源可释放）。',

    // closeImage（B6a 的"Image.close 有意不做"）已随 B14 决策删除：现在 Image.close
    // 是补壳（存在、无副作用），方法比对两侧都有这个名字，不再是分歧。
    cleanupCallback:
        '**等价物的名字不同**：Tauri 的 `Channel.cleanupCallback()` 注销 `transformCallback` 注册的回调；' +
        '本库没有全局回调表，对应能力是公开的 `Channel.close()`（退订两个事件监听、幂等）。' +
        '因此这里**不做**同名方法——给一个 `cleanupCallback()` 别名只会让"关通道"这件事有两个名字。',
    __TAURI_TO_IPC_KEY__:
        'Tauri 2 的序列化钩子（Symbol）。**B14 起本库的线格式与 Tauri 2 一致**' +
        '（外部标记 `{ Logical: { x, y } }`），但钩子本身仍不提供：本库的序列化走 `toJSON`，' +
        '没有"给 IPC 层单独序列化"这条路。迁移代码若显式访问这个 Symbol，会拿到 undefined——' +
        '正常用法（把实例当 invoke 参数）不受影响。',

    toJSON:
        '（B14 后已不再是分歧——保留登记是因为它是上一条的另一面：）' +
        '本库的 `toJSON` 输出与 Tauri 2 相同的外部标记形状。',

    _handleTauriEvent: 'Tauri 内部的 Window 事件分发入口（下划线前缀），不是公开 API。',

    activityName:
        'Android 专有（当前 Activity 的名字）。本库是桌面库，没有对应物——' +
        '而**永远返回一个空串或抛错都不对**，所以干脆不提供这个名字。',

    sceneIdentifier:
        'iOS 专有（当前 UIScene 的标识符）。理由同 `activityName`。',

    // ---- WebviewWindow 独有（Tauri 的 WebviewWindow = Webview + Window，本库窗口即 webview）----

    position:
        '**结构性差异**：Tauri 的 `WebviewWindow` 继承 `Webview`，"位置"指的是 webview 在**窗口内**的偏移。' +
        '本库**窗口即 webview**（一个窗口一个 webview，铺满客户区），没有"窗口内偏移"这个概念。' +
        '窗口本身的位置是 `outerPosition` / `setPosition`，那两个都有。',

    size:
        '同上：`WebviewWindow.size` 是 webview 在窗口内的尺寸，本库没有这一层；' +
        '窗口尺寸用 `innerSize` / `outerSize` / `setSize`。',

    setAutoResize:
        '同上：控制"窗口尺寸变化时 webview 是否跟着变"。本库的 webview 是窗口的一部分，' +
        '永远跟着变——没有可关的开关，给一个空方法只会让人以为关掉能省性能。',

    reparent:
        '同上：把 webview 移到另一个窗口。本库没有"webview 可从窗口脱离"的模型。',

    // ---- menu（B6a）----

    rid:
        '**结构性差异**：Tauri 的菜单类是 `Resource` 的子类，`rid` 是 Rust 侧资源表里的句柄。' +
        '本库没有资源表（IPC 是一次性请求-回执），菜单树是**页面侧的纯数据**，' +
        '`popup()` 时整棵发给宿主。没有 rid 可给。',
};

/**
 * **还没有实现**的方法，按类别分组。
 *
 * 这份清单就是 H（迁移对照）该产出的东西：它把"迁移会撞到什么"变成一份可维护的名单。
 * 分组而不是逐条写原因：这六十来条的**原因高度重复**，逐条写会淹没真正不同的那几个。
 *
 * **两组标记**（方针定的，见 docs/TAURI2-PLAN.md 的 M5-6）：
 * <list type="bullet">
 *   <item><c>【A·跨平台不一致】</c>——三平台上不是同一件事（有的平台根本没有这个概念，
 *     或落点性质不同）。**方针是不做**：与其在三平台上给出三个语义不同的同名方法，
 *     不如让迁移者一眼看到"这个能力本库不提供"，自己决定怎么兜。</item>
 *   <item><c>【B·跨平台一致·待做】</c>——三平台语义相同，只是还没排到。</item>
 * </list>
 *
 * @type {Array<{ reason: string, methods: string[] }>}
 */
const METHOD_GAP_GROUPS = [
    // 【A·跨平台不一致】与【B·】两组在 B12–B22 结束**全部清空**：
    //
    //   * B16：setZoom / clearAllBrowsingData
    //   * B21：六个光标控制（setCursorGrab / setCursorVisible / setCursorIcon /
    //     setCursorPosition / setIgnoreCursorEvents / startResizeDragging）
    //   * B22：十条平台开关 + setEffects / clearEffects + 四条任务栏
    //     （setBadgeCount / setBadgeLabel / setProgressBar / setOverlayIcon）
    //
    // B22 的做法推翻了两条旧的方针判断（当时的决策记录 §5）：
    // "跨平台不一致的不做"改成"做，不支持的平台抛 not_supported"。
    // 于是【A·】分类**不再表示"不做"**，只表示"做但平台受限"——具体矩阵在
    // docs/API.md 的 B22 支持表里，逐平台一行。
    //
    // 清空之后这条清单仍然有意义：它是"零缺口"的状态快照。要往里面加项，
    // 必须写明是【A·】还是【B·】以及批次号（下面两条测试盯着）。
];

/** 展开成"方法 → 原因"的扁平表，供断言用。 */
const METHOD_GAPS = Object.fromEntries(
    METHOD_GAP_GROUPS.flatMap((group) => group.methods.map((method) => [method, group.reason])),
);

/**
 * **我们有、参照实现的类没有**的方法——方法级的 `EXTRAS`。
 *
 * 键是 `<模块>.<类>#<方法>`，值是有意扩展的理由。
 *
 * 为什么需要这一层：`EXTRAS` 只覆盖**导出面**（模块导出了什么名字），而
 * "某个类多了一个方法"在那一层完全看不出来。2026-10-19 就踩过一次：
 * 给 `Menu` / `Submenu` 加了 `close()`（关掉在屏菜单），而 Tauri 的同名方法来自
 * `Resource`、语义是"释放 rid"——账本当时只查"参照有、我们没有"，于是这处
 * **同名不同义**的分歧没有任何断言盯着。
 *
 * 判据与导出面的 `EXTRAS` 一致：加一个 Tauri 没有的方法**可以**，但必须在这里
 * 说明原因，否则它就是一处"偷偷发明了自己的 API"。
 *
 * @type {Record<string, string>}
 */
const METHOD_EXTRAS = {
    'core.Channel#close':
        '**本库扩展**：显式关闭通道（退订两条监听、幂等）。Tauri 的等价物是内部的 ' +
        '`cleanupCallback()`，由 Rust 侧在通道 drop 时触发，页面代码调不到。' +
        '本库没有 Rust 侧的 drop 时机可用（命令返回后宿主会发结束事件，页面收到就自动关），' +
        '但**手动关**仍有意义：提前不要了、命令还要继续跑的场景。',
    'core.Channel#closed':
        '**本库扩展**：只读状态。Tauri 那边"关没关"是私有的（回调从表里删掉就完了），' +
        '页面没有可查的值。给一个只读标志是纯加法，让"我是不是已经关了"不必靠猜。',

    'menu.Menu#close':
        '**本库扩展**：关掉当前正在显示的菜单（等价于用户按 Esc）。' +
        'Tauri 的 `Menu.close()` 来自 `Resource`，释放的是 Rust 侧资源表那条记录——' +
        '本库没有资源表，但**必须有这条命令**：Windows 的 `popup()` 会阻塞到用户选择，' +
        'UI 线程被占着时页面上的按钮点不到，没有它页面就完全无法用代码关掉自己弹的菜单。',
    'menu.Submenu#close':
        '同上（`Submenu` 也能 `popup()`，因此也需要能关掉自己弹出的那个）。',

    // ---- 既有模块（2026-10-19 加方法级反向检查时一次查出来的 8 处）----
    //
    // 这 8 个方法此前**完全没有约束**：账本只查"参照有、我们没有"。它们不是新加的，
    // 而是一直就在那里——这说明导出面的 EXTRAS 对"类多了一个方法"是瞎的。

    'window.Window#toggleFullscreen':
        '**便利方法**：`setFullscreen(!(await isFullscreen()))` 的合成。' +
        'Tauri 只给 setter + 查询，调用方每次都要自己写这三步，而"取反"是压倒性的常见用法。' +
        '名字与 `toggleMaximize` 同族（那个 Tauri 有，本库照做），所以不会误导。',
    'window.Window#toggleAlwaysOnTop':
        '同上：`setAlwaysOnTop(!(await isAlwaysOnTop()))` 的合成，与 `toggleMaximize` 同族。',
    'webviewWindow.WebviewWindow#toggleFullscreen':
        '与 `window.Window#toggleFullscreen` 同一个方法（`WebviewWindow extends Window`，' +
        '不重抄，只是原型链上能被看到）。理由同上。',
    'webviewWindow.WebviewWindow#toggleAlwaysOnTop':
        '与 `window.Window#toggleAlwaysOnTop` 同一个方法（继承而来）。理由同上。',

    'image.Image#width':
        '**本地值读取**：从已解析的尺寸里直接读宽度，不请宿主。' +
        'Tauri 的 `Image` 有 `size()` 但没有 `width()` / `height()`，调用方要写 ' +
        '`(await image.size()).width`——多一次 await、多一层解构。' +
        '本库的尺寸在页面侧就已解析好（PNG 的 IHDR / ICO 的目录项），给两个直接读取器是纯加法。',
    'image.Image#height': '同上。',
    'image.Image#rgbaBytes':
        '**原始像素的直读入口**：`rgba()` 返回 `Uint8Array` 的副本（要跨 IPC 复制），' +
        '而 `rgbaBytes()` 返回**不复制**的视图，供"只想读几个像素"或"自己喂给 canvas"的场景。' +
        'Tauri 没有对应物（那边像素在 Rust 侧）。两个名字都留，是因为它们的拷贝语义不同，' +
        '合并成一个会让"我改这个数组会不会影响 Image"变成要靠文档回答的问题。',
    'image.Image#hasEncodedBytes':
        '**本库特有状态查询**：`Image` 可能是"只有 RGBA 没有编码字节"的（`fromBytes` 传原始像素），' +
        '这时 `toBytes()` 拿不到 PNG。Tauri 的 Image 总是持编码字节，所以没有这个问题、' +
        '也就没有这个方法。给出来是为了让调用方在调 `toBytes()` 前能判断。',

    // ---- tray（B12）----
    'tray.TrayIcon#onEvent':
        '**本库扩展**：句柄上的事件订阅方法。Tauri 把回调挂在 `TrayIconOptions.action`' +
        '（构造时）或 Rust 侧资源上；本库页面需要"创建后随时订阅"的入口，且事件是' +
        '按 id 广播的（托盘是应用级资源），订阅时按 `this.id` 过滤。' +
        '纯加法：迁移代码用 `TrayIcon.new({ action })` 零改动。',
};

test('待做清单的每一项都标了 A/B 归属（方针：跨平台不一致的不做）', () => {
    // 这条挡的是"清单慢慢退化成没人敢信的历史文档"：
    // 往 METHOD_GAP_GROUPS 里加一组却不说清它是"方针下能做"还是"方针下不做"，
    // 下一个人就得重新判一遍跨平台一致性。
    const offenders = METHOD_GAP_GROUPS
        .filter((group) => !/^【[AB]·/.test(group.reason))
        .map((group) => group.methods[0]);

    assert.deepEqual(
        offenders, [],
        `这些组没标 A/B 归属（reason 要以【A· 或【B· 开头）：\n  - ${offenders.join('\n  - ')}`);
});

test('B 组（方针下可做）必须写明批次——不许无声地留在"以后再说"', () => {
    // 这条原先是"B 组必须为空"（0.3.6 时可做的都做完了）。B4 起会合法地往 B 组加项，
    // 所以判据改成"每条都要有批次"：**往 B 组加东西是允许的，但不写清什么时候做不允许**——
    // 否则这份清单会慢慢变回"没人敢信的历史文档"。
    const groups = METHOD_GAP_GROUPS.filter((group) => group.reason.startsWith('【B·'));

    for (const group of groups) {
        if (group.methods.length === 0) {
            continue;
        }

        assert.match(
            group.reason,
            /\bB\d/,
            `B 组「${group.methods[0]}…」没有写批次（reason 里要出现 B0–B9 之类的批次号）`);
    }

    // 反面：清单为空是**合法状态**（B22 收口后就是空的），但一旦非空，
    // 每条都必须带 A/B 归属——这条与上面那条测试一起，防止清单退化成
    // "没人敢信的历史文档"，也防止有人把"其实已经实现了"的东西留在里面。
    const excluded = METHOD_GAP_GROUPS
        .filter((group) => group.reason.startsWith('【A·'))
        .flatMap((group) => group.methods);
    assert.ok(Array.isArray(excluded), 'A 组（跨平台受限）应当能展开成条目列表');
});

test('同名类的方法也对得上（我们缺的都在 METHOD_DIVERGENCES 里）', async (t) => {
    if ((await tryImport('@tauri-apps/api/core')) === null) {
        t.skip('参照实现未安装——CI 会装并强制检查');
        return;
    }

    /**
     * 收集一个类**含原型链**的方法名。
     *
     * @param {any} cls
     * @returns {Set<string>}
     * @remarks 必须走原型链：`WebviewWindow extends Window` 不重抄父类方法，
     * 只看 own property 会把八十多个继承来的方法全报成"缺失"。
     */
    const collectMethods = (cls) => {
        const methods = new Set();
        let proto = cls.prototype;
        while (proto && proto !== Object.prototype) {
            for (const name of Object.getOwnPropertyNames(proto)) {
                if (name !== 'constructor') {
                    methods.add(name);
                }
            }
            proto = Object.getPrototypeOf(proto);
        }
        return methods;
    };

    /** @type {string[]} */
    const problems = [];
    let compared = 0;

    for (const module of MODULES) {
        const ours = await import(module.ours);
        const theirs = await tryImport(module.theirs);

        if (theirs === null) {
            continue;
        }

        for (const name of Object.keys(ours)) {
            const ourClass = ours[name];
            const theirClass = theirs[name];

            // 只比对"双方都有、且都是类"的那些。
            // 我们没有的类已经在 GAPS 里记过了，这里不重复报。
            if (typeof ourClass !== 'function' || typeof theirClass !== 'function') {
                continue;
            }

            if (ourClass.prototype === undefined || theirClass.prototype === undefined) {
                continue;
            }

            compared++;

            const ourMethods = collectMethods(ourClass);

            for (const method of Object.getOwnPropertyNames(theirClass.prototype)) {
                if (method === 'constructor' || ourMethods.has(method)) {
                    continue;
                }

                // 有意的分歧，或"还没实现但已记录"
                if (method in METHOD_DIVERGENCES) {
                    continue;
                }

                if (`${module.label}.${name}#${method}` in METHOD_GAPS) {
                    continue;
                }

                // 子类继承自 Window：同一批方法在 `window.Window` 下已经分类过，
                // 不必在子类名下再抄一份。
                if (name === 'WebviewWindow' && `window.Window#${method}` in METHOD_GAPS) {
                    continue;
                }

                problems.push(
                    `${module.label}.${name}#${method}: Tauri 的类有这个实例方法、我们的没有，` +
                    '且没记进 METHOD_DIVERGENCES（有意的分歧）或 METHOD_GAP_GROUPS（还没实现）。');
            }

            // **反向检查：清单过期**。
            //
            // 与导出面那条同样的道理——实现了某项却忘了从清单里划掉，会让工作清单慢慢变成
            // 一份没人敢信的历史文档。这里查"清单里列着、但我们已经有了"。
            for (const method of ourMethods) {
                const key = `${module.label}.${name}#${method}`;

                if (key in METHOD_GAPS && !(method in METHOD_DIVERGENCES)) {
                    problems.push(
                        `${key}: 清单里列着"还没实现"，但其实已经实现了（清单过期）。`);
                }
            }

            // **反向检查二：我们独有的方法**。
            //
            // 导出面的 EXTRAS 只管"模块导出了什么名字"，管不到"某个类多了一个方法"。
            // 少了这一条，给类加方法就是**完全无约束**的——而那正是"偷偷发明自己的 API"
            // 最隐蔽的形态（它不影响 import，只在调用时才暴露）。
            const theirMethods = collectMethods(theirClass);

            for (const method of ourMethods) {
                if (theirMethods.has(method)) {
                    continue;
                }

                // 下划线前缀是内部实现（`_wire` / `_wireCore` 等），不是公开 API 面。
                if (method.startsWith('_')) {
                    continue;
                }

                const key = `${module.label}.${name}#${method}`;
                if (key in METHOD_EXTRAS) {
                    continue;
                }

                problems.push(
                    `${key}: 我们有、Tauri 的类没有，且没记进 METHOD_EXTRAS。` +
                    '包应当只提供 Tauri 有的方法；自己的扩展要写清理由。');
            }
        }
    }

    console.log(`[alignment] 比对了 ${compared} 个同名类的方法`);
    assert.deepEqual(problems, [], `方法对照发现 ${problems.length} 处未记录差异：\n  - ${problems.join('\n  - ')}`);
});

test('`close` 的语义按类分开记：menu 是关菜单、Image 是补壳', () => {
    // `METHOD_DIVERGENCES` 是**按方法名**索引的扁平表，一个名字只能有一条理由。
    // `close` 恰好落在两种完全不同的情形上：
    //   * `menu.*` 的 `close()` 做的是"关掉在屏菜单"（本库扩展，Tauri 那边是释放 rid）；
    //   * `Image.close()` 是 **B14 补壳**（没有资源可释放，但迁移代码零删改）。
    //
    // B14 前 Image.close 不存在，用一条反向断言钉着"别为了对齐加空方法"；
    // 决策改为补壳后，这里改为钉住**两者语义仍然不同**，防止哪天有人把
    // Image.close 也做成"关掉什么"。
    return import('../src/image.js').then(async ({ Image }) => {
        assert.equal(
            typeof Image.prototype.close,
            'function',
            'Image.close 应当存在（B14 补壳）');

        // 补壳必须无副作用：调用后不发 IPC、不改变对象
        const image = await Image.new(new Uint8Array(4), 1, 1);
        await image.close();
        assert.equal(image.width, 1, 'close 之后 Image 照常可用（补壳没有资源可释放）');
    });
});
