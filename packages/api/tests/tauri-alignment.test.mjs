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
    { ours: '../src/path.js', theirs: '@tauri-apps/api/path', label: 'path' },
    { ours: '../src/app.js', theirs: '@tauri-apps/api/app', label: 'app' },
    { ours: '../src/mocks.js', theirs: '@tauri-apps/api/mocks', label: 'mocks' },
    { ours: '../src/fs.js', theirs: '@tauri-apps/plugin-fs', label: 'fs' },
    { ours: '../src/dialog.js', theirs: '@tauri-apps/plugin-dialog', label: 'dialog' },
    { ours: '../src/clipboard.js', theirs: '@tauri-apps/plugin-clipboard-manager', label: 'clipboard' },
    { ours: '../src/opener.js', theirs: '@tauri-apps/plugin-opener', label: 'opener' },
    { ours: '../src/notification.js', theirs: '@tauri-apps/plugin-notification', label: 'notification' },
    { ours: '../src/autostart.js', theirs: '@tauri-apps/plugin-autostart', label: 'autostart' },
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
        Channel: '流式回传通道，需要句柄与消息泵；本库的 IPC 是一次性请求-回执',
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
        Position: '尺寸/位置类的联合类型别名（Tauri 用它给 setPosition 收两种单位）；本库用 normalizePosition 做同一件事',
        Size: '同上（本库用 normalizeSize）',
    },
    window: {
        CloseRequestedEvent: 'close-requested 事件的类型；本库在 event 模块用 onCloseRequested 表达',
        availableMonitors: '多显示器枚举；本库还没有显示器模型',
        cursorPosition: '全局光标位置；本库还没有',
        currentMonitor: '同上',
        monitorFromPoint: '同上',
        primaryMonitor: '同上',
        Effect: '窗口特效（macOS 的 vibrancy 等）；本库还没有',
        EffectState: '同上',
        ProgressBarStatus: '任务栏进度条状态（Windows）；本库还没有',
    },
    path: {
        BaseDirectory: 'Tauri 用它给 resolve_directory 传枚举；本库的目录各有独立命令，不需要这个枚举',
    },
    app: {
        BundleType: 'Tauri 打包器的产物类型；本库是库不是打包器，给不出（见 API.md）',
        defaultWindowIcon: '要图标资源表，本库没有',
        exit: '会终止应用；是能力而不是元信息，本批没有放行它',
        fetchDataStoreIdentifiers: '数据存储标识符枚举；本库没有那个存储层',
        getBundleType: '同 BundleType',
        hide: '应用级隐藏；本库目前只有窗口级',
        onBackButtonPress: '移动端返回键；桌面端没有',
        removeDataStore: '同 fetchDataStoreIdentifiers',
        setDockVisibility: 'macOS 的 Dock 显隐；本库还没有',
        setTheme: '应用级主题覆写；本库的 Theme 目前是只读的',
        show: '同 hide',
    },
    mocks: {},
    fs: {
        BaseDirectory: '与 path 的同名导出一样，是"给目录解析传枚举"用的；本库不需要',
        FileHandle: '文件句柄类；本库没有句柄表',
        SeekMode: '配合 FileHandle',
        create: '句柄式创建；本库只做整文件读写',
        open: '句柄式打开；同上',
        readTextFileLines: '句柄式逐行读；同上',
        startAccessingSecurityScopedResource: 'macOS 沙箱的 security-scoped bookmark；本库还没有',
        stopAccessingSecurityScopedResource: '同上',
        watch: '文件系统监视；要常驻 watcher',
        watchImmediate: '同上',
    },
    dialog: {},
    clipboard: {
        readImage: '剪贴板图片；要图像句柄模型',
        writeImage: '同上',
        writeHtml: 'HTML 剪贴板在 Windows 上是 CF_HTML，偏移头写错会静默产生乱码（见 API.md）',
        writeSecret: 'macOS 专有的"不记入剪贴板历史"标记',
    },
    opener: {},
    notification: {
        Importance: 'Android 的通知重要级',
        Schedule: '定时通知；要常驻定时器',
        ScheduleEvery: '同上',
        Visibility: 'Android 的通知可见性',
        active: '活动通知列表；要通知句柄表',
        cancel: '取消通知；同上',
        cancelAll: '同上',
        channels: 'Android 通知渠道',
        createChannel: '同上',
        deleteChannel: '同上',
        listChannels: '同上',
        onAction: '通知点击回传；要把动作送回页面',
        onNotificationReceived: '同上',
        pending: '待发通知列表；要句柄表',
        registerActionTypes: '通知动作类型（移动端为主）',
        removeActive: '同上',
        removeAllActive: '同上',
        removeChannel: 'Android 通知渠道',
    },
    autostart: {},
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
        getFocusedWindow: '取当前聚焦窗口。**恒返回 null**（三平台都还没有"哪个窗口有焦点"的查询能力）——签名先就位是为了让迁移代码能编译',
    },
    mocks: {
        getMockWindowLabels: '读 mock 出来的窗口 label 列表，便于断言',
        isMocked: '当前是否装了 mock。前端单测靠它判断"我是不是在测试里"',
        mockEmit: '模拟宿主推事件。Tauri 的 mocks 里没有，而写前端测试时它是必需的（否则 listen 的回调永远不触发）',
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

/**
 * 有意不实现的方法——每条要有原因。
 *
 * @type {Record<string, string>}
 */
const METHOD_DIVERGENCES = {
    onDragDropEvent:
        '**有意不做**：文件拖放走宿主 `WebviewWindow.FileDropped` 事件，不在页面事件通道上。' +
        '这不是省事——浏览器安全模型决定了**页面拿不到文件路径**（Tauri 能给出路径，' +
        '靠的是它自己那套拖放通道 + IPC 白名单）。本库给路径的地方只有宿主侧 ' +
        '`FileDropped` 回调，页面要拿路径得由应用显式转发。' +
        '既然页面通道上根本不可能有路径数据，' +
        '造一个"形状像 Tauri 但没有 paths"的事件只会误导迁移代码。',
    __TAURI_TO_IPC_KEY__:
        'Tauri 2 的序列化钩子：类把值编成 `{ Logical: { x, y } }`（外部标记枚举）。' +
        '本库的线格式是 `{ type: "Logical", x, y }`（Tauri 1 的形状），宿主只认后者。' +
        '改过去要**同时改宿主协议**，而页面看不到差别（它只是把对象原样传回 setSize）。' +
        '这是一处**有意的分歧**，不是漏实现。',

    toJSON:
        '与 `__TAURI_TO_IPC_KEY__` 是同一件事的两面（Tauri 的类靠它触发那套序列化）。' +
        '本库的类是普通可枚举对象，宿主直接读 `type` / `x` / `y`，不需要 toJSON。',

    _handleTauriEvent: 'Tauri 内部的 Window 事件分发入口（下划线前缀），不是公开 API。',

    activityName:
        'Android 专有（当前 Activity 的名字）。本库是桌面库，没有对应物——' +
        '而**永远返回一个空串或抛错都不对**，所以干脆不提供这个名字。',

    sceneIdentifier:
        'iOS 专有（当前 UIScene 的标识符）。理由同 `activityName`。',
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
    {
        reason: '【A·跨平台不一致】**各自缺一半**：`setShadow` 在 Linux 上没有对应概念（GTK 无阴影 API）；' +
            '`setAlwaysOnBottom` 同样只有 Windows/macOS 有；`setFocusable` 在 macOS 上没有公开入口' +
            '（只有私有的 `_setPreventsActivation`，用它等于把自己绑到未公开 API 上）；' +
            '`setBackgroundColor` **已经做了**（运行期），它是三平台一致的：'
            + '都落到 webview 自己的底色（Windows `DefaultBackgroundColor`、'
            + 'macOS `underPageBackgroundColor`、Linux `webkit_web_view_set_background_color`）。' +
            '`setDecorations`：Windows 上 `ComputeStyle` 是**创建期的静态决定**，' +
            '运行期切换要改整套样式并让 `WM_NCCALCSIZE` 的分支跟着变，而那条路我无法目视验证。',
        methods: [
            'window.Window#setDecorations', 'window.Window#setShadow',
            'window.Window#setFocusable',
            'window.Window#setAlwaysOnBottom',
        ],
    },
    {
        reason: '【A·跨平台不一致】每个平台各有一套、甚至只有一个平台有的概念（任务栏隐藏 / 全工作区可见 / ' +
            '内容防截屏 / 简化全屏 / 指定显示器全屏 / 标题栏样式）。' +
            '这组是**方针的直接目标**：在另两个平台上它要么没有意义（给不出来），' +
            '要么得自造一套语义（那是设计而不是对齐）。',
        methods: [
            'window.Window#setSkipTaskbar', 'window.Window#setVisibleOnAllWorkspaces',
            'window.Window#setContentProtected', 'window.Window#setSimpleFullscreen',
            'window.Window#setFullscreenOnMonitor', 'window.Window#setTitleBarStyle',
        ],
    },
    {
        reason: '【A·跨平台不一致】窗口特效（macOS 的 vibrancy / Windows 的 Mica）。' +
            '三平台机制差异很大，"特效"不是一个跨平台概念，要做就得先自造一套抽象模型。',
        methods: ['window.Window#setEffects', 'window.Window#clearEffects'],
    },
    {
        reason: '【A·跨平台不一致】光标控制。三平台基础能力（形状/可见性/位置）相近，但 ' +
            '`setIgnoreCursorEvents` 在 Linux 上要走 X11 shape 扩展（Wayland 下又不同）、' +
            '`startResizeDragging` 的拖拽边界语义各平台不同；而且这组需要一套**光标资源**' +
            '（Tauri 那边光标图标走的是另一套协议）。整体不是"几个独立开关"。',
        methods: [
            'window.Window#setCursorGrab', 'window.Window#setCursorIcon',
            'window.Window#setCursorPosition', 'window.Window#setCursorVisible',
            'window.Window#setIgnoreCursorEvents', 'window.Window#startResizeDragging',
        ],
    },
    {
        reason: '【A·跨平台不一致】任务栏 / Dock 的徽标与进度。Windows 要 `ITaskbarList3`（COM）、' +
            'macOS 要 `NSDockTile`，而 **Linux 上没有对应概念**（不同桌面环境各有自己的扩展协议）。',
        methods: [
            'window.Window#setBadgeCount', 'window.Window#setBadgeLabel',
            'window.Window#setProgressBar', 'window.Window#setOverlayIcon',
        ],
    },
    {
        reason: '【A·跨平台不一致】应用级主题覆写。本库现在只有"跟随系统"（`OrielApp.Theme` 是只读的），' +
            '要做就得先定"覆写的作用域是什么"（整个应用？单个窗口？）——那是设计决策。',
        methods: ['window.Window#setTheme'],
    },
    {
        reason: '【B·跨平台一致·待做】`onScaleChanged` 已经做完了：三平台各自检测并把'
            + '`scale.changed` 推给页面（Windows `WM_DPICHANGED`、Linux GDK 的 `notify::scale-factor`、'
            + 'macOS 顺带在 `windowDidResize:`/`windowDidMove:` 里查——后者**有一个已知漏报**：'
            + '只改系统缩放而窗口不动时两条回调都不发，详见 MacOSWindowHost.ReportScaleFactorIfChanged 的说明）。'
            + '本组现在是空的：方针下可做的项已经全部实现，剩下的都在 A 组（方针排除）。',
        methods: [],
    },
];

/** 展开成"方法 → 原因"的扁平表，供断言用。 */
const METHOD_GAPS = Object.fromEntries(
    METHOD_GAP_GROUPS.flatMap((group) => group.methods.map((method) => [method, group.reason])),
);

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

test('方针下可做的组（B 组）现在是空的——剩下的都在 A 组（方针排除）', () => {
    const doable = METHOD_GAP_GROUPS
        .filter((group) => group.reason.startsWith('【B·'))
        .flatMap((group) => group.methods);

    // 钉住当前结论：方针下可做的项**已经全部实现**。
    // 哪天要重新在 A 组里开工，必须先改这条断言并说明方针变了——而不是悄悄往里加实现。
    assert.deepEqual(doable, [],
        'B 组（跨平台一致、方针下可做）应当为空：可做的都实现了');

    // 反面：A 组必须非空且明确标为"方针不做"，否则这份清单就退化成"什么都没做"
    const excluded = METHOD_GAP_GROUPS
        .filter((group) => group.reason.startsWith('【A·'))
        .flatMap((group) => group.methods);
    assert.ok(excluded.length > 0, 'A 组（方针排除）应当列出具体项');
});

test('同名类的方法也对得上（我们缺的都在 METHOD_DIVERGENCES 里）', async (t) => {
    if ((await tryImport('@tauri-apps/api/core')) === null) {
        t.skip('参照实现未安装——CI 会装并强制检查');
        return;
    }

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

            const ourMethods = new Set(
                Object.getOwnPropertyNames(ourClass.prototype).filter((m) => m !== 'constructor'));

            for (const method of Object.getOwnPropertyNames(theirClass.prototype)) {
                if (method === 'constructor' || ourMethods.has(method)) {
                    continue;
                }

                // 有意的分歧，或"还没实现但已记录"
                if (method in METHOD_DIVERGENCES || `${module.label}.${name}#${method}` in METHOD_GAPS) {
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
        }
    }

    console.log(`[alignment] 比对了 ${compared} 个同名类的方法`);
    assert.deepEqual(problems, [], `方法对照发现 ${problems.length} 处未记录差异：\n  - ${problems.join('\n  - ')}`);
});
