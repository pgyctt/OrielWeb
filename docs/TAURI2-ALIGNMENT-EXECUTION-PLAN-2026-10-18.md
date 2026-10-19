# Tauri 2 对齐执行计划（审计后）

> 制定时间：2026-10-18
> 输入：`docs/TAURI2-ALIGNMENT-AUDIT-2026-10-18.md`（对齐审计，基准 Tauri 2.12.4）
> 上一轮：批次 A–H 与 M5 **已完成**（导出面到 67%、方法缺口在方针内归零）
> 本计划只处理**审计后剩下的活**，不重复上一轮

---

## 0. 这份计划的取舍

### 0.1 为什么不是一个"继续补 API"的清单

审计的核心结论是：**名字层的边际收益已经很低**。

- 方法缺口在现行方针（跨平台不一致不做）下已经是 **0**；
- 导出面 67%，剩下 31 个缺口里有 23 个是"方针排除"或"移动端"，真正能补的只有 `app` 状态族、显示器、`webview`/`webviewWindow`、`menu` 四块；
- 与此同时，**已有 67% 里藏着 9 处同名不同义**——这些才是迁移时真正会出错的地方。

所以顺序是：**先让差异可见 → 再让失败可见 → 最后才补能力**。

### 0.2 总原则

| 原则 | 含义 |
|---|---|
| **每批独立交付** | 每批都能单独合入、单独回滚、单独发版；不搞"全做完再验" |
| **只做加法** | 除 B3 的 `listen`（见待决策 D-1）外，所有改动都不破坏既有页面 |
| **测试先行** | 每批先写"会红的断言"，再写实现——否则改完不知道有没有改对 |
| **三平台同改** | 涉及平台的批次，`--selftest` 必须同时进三个验证脚本（验收口径见 §3） |
| **方针不变** | `Window` 的 A 组 23 项继续排除；要动先改断言并说明方针变了 |

### 0.3 明确不在本计划内

| 项 | 原因 | 何时重新考虑 |
|---|---|---|
| Resource / rid 句柄表（S1） | 架构级改动，影响 IPC 协议本身 | 出现"必须流式回传/必须持句柄"的真实需求 |
| 插件体系（S2） | 库定位是核心库，不是平台 | 出现第三方扩展需求 |
| 多 webview（S4） | 三平台窗口宿主都要拆层 | 出现"单窗口多 webview"的真实需求 |
| capability 文件格式对齐（S3） | 形状不同但语义等价，转换器比改造便宜 | 若要做 `tauri.conf.json` 迁移工具，再单独评估 |
| `Window` A 组 23 项 | 跨平台语义不一致 | 方针变更 |
| `shell` / `http` / `sql` / `updater` / `deep-link` | 与定位冲突 | 不复核 |

---

## 1. 批次总表

| 批次 | 目标 | 难度 | 改动面 | 依赖 | 可独立发版 |
|---|---|---|---|---|---|
| **B0** | 对齐账本收口：把"看不见的差异"登记出来 | 低 | 1 个测试文件 + `event.js` 常量 | 无 | ✅ |
| **B1** | 三处静默失败：`baseDir` / 超时 `code` / `ORIELWEB106` | 低 | `fs.js` / `oriel-bridge.js` / 生成器 / 2 个测试 | 无 | ✅ |
| **B2** | `app` 状态族：`exit` / `show` / `hide` | 低 | `OrielBuiltInAppCommands.cs` / `app.js` / 能力表 | B0 | ✅ |
| **B3** | `listen` 形状对齐 + 事件常量补齐 | 中 | `event.js` / `window.js` / 全仓调用点 | B0 | ⚠️ 见 D-1 |
| **B4** | 页面建窗：`window.new` + `webviewWindow` 模块 | 中高 | 新命令族 + 新 JS 模块 + dispatcher 线程处理 | B2 | ✅ |
| **B5** | 显示器模型（5 个导出） | 中 | 三平台后端 + 新命令 + `window.js` | 无 | ✅ |
| **B6a** | 菜单模型 + `Menu.popup`（不做菜单栏） | 中高 | C# 菜单模型 + 前端模块 | 无 | ✅ |
| **B6b** | 应用菜单栏（三平台各自的挂载点） | 高 | 三平台窗口宿主（Linux 要改布局） | B6a | ✅ |
| **B7** | 真实 Tauri 应用迁移演练 | 中 | 无代码改动，产出卡点清单 | B0–B4 | — |

**推荐顺序（单人）**：`B0 → B1 → B2 → B3 → B4 → B5 → B6a → B6b → B7`
**双人并行**：A 线 `B0 → B1 → B3 → B5`；B 线 `B2 → B4`；汇合后 `B6a → B6b`。

### 1.1 规模估算（按改动点，不按天数）

| 批次 | 新增文件 | 修改文件 | 新增/改动测试 | 难度 |
|---|---|---|---|---|
| B0 | 0 | 3 | ~8 条断言 | 低 |
| B1 | 0 | 5 | ~20 条 | 低 |
| B2 | 1 | 4 | ~12 条 | 低 |
| B3 | 0 | 3 | ~6 条（选 B 另加全仓改动） | 中 |
| B4 | 2 | 5 | ~15 条 + 1 个自检 | 中高 |
| B5 | 1 | 5 | ~15 条 + 1 个自检 | 中 |
| B6a | 2 | 4 | ~12 条 + 1 个自检 | 中高 |
| B6b | 0 | 6 | ~9 条 + 3 个自检 | 高 |
| **合计** | **~6** | **~35** | **~100 条** | — |

> 这个量级说明：**B0–B2 是"一个下午能落三批"的规模**，B4/B6b 才是真正吃时间的两块。
> 如果只能做三批，就做 B0 + B1 + B2——它们解决"差异看不见 + 迁移静默出错 + 页面不能退出"，性价比最高。

> B5 与 B6 都改平台层，但**改的是不同文件**（B5 只读枚举，B6 动菜单宿主）——可以并行，但 Linux 的 `GtkMenu` 与 `GtkBox` 布局会碰同一个 `LinuxWindowHost`。单人做时按表中顺序，避免同时持有同一文件。

---

## 2. 批次细节

### B0 · 对齐账本收口

**目标**：让"没登记 = 不检查"这件事**不可能再发生**。

**现状问题**（审计 §6.2）：
- `packages/api/tests/tauri-alignment.test.mjs:36-53` 的 `MODULES` 只登记 14 个模块，`tray`/`menu`/`webview`/`webviewWindow` 未登记；
- 测试只比 `Object.keys()`，常量对象内部键（`TauriEvent` 缺 3 个）比不出来；
- 方法名比对（`METHOD_GAP_GROUPS`）不含签名与返回类型。

**改动清单**

| 文件 | 改动 |
|---|---|
| `packages/api/tests/tauri-alignment.test.mjs` | ① 新增 `PLANNED_MODULES`：4 个未实现模块 + 计划批次 + 缺口说明；② 新增"PLANNED 里的模块不得出现在 MODULES 里"断言；③ **新增反向断言：`PLANNED` 里声明的模块，若 `packages/api/src/<name>.js` 已存在，则测试失败**（实现后忘了移出登记 → 立刻红，防止账本腐化）；④ 新增常量对象键比对（`TauriEvent` / `UserAttentionType`） |
| `packages/api/src/event.js` | `TauriEvent` 补 3 个键：`WINDOW_SUSPENDED` / `WINDOW_RESUMED` / `WEBVIEW_CREATED`，JSDoc 标 `⛔ 桌面端不会触发` |
| `packages/api/tests/event.test.mjs` | 断言 `Object.keys(TauriEvent).length === 16`，且**每个键都能在参照实现的 `TauriEvent` 里找到**（防止自造名字） |
| `packages/api/tests/fs.test.mjs` | 新增"`stat` 的 `mtime` 是 `number` 而不是 `Date`"断言（钉住 B6 那类差异） |

**为什么保留 3 个不可用常量而不是删掉**
"名字在、能力明确标注不可用"与"名字都没有"对迁移者是两种体验：前者在 `import` 处就能跑、在文档里立刻看到 ⛔；后者会在 `TauriEvent.WINDOW_SUSPENDED` 求值处得到 `undefined`，然后在 `listen(undefined)` 时炸在离原因很远的地方。

**DoD**
```bash
node --test packages/api/tests/tauri-alignment.test.mjs
# 输出必须包含：未实现模块 4 个（tray/menu/webview/webviewWindow），且每条有计划批次
node --test packages/api/tests/event.test.mjs
node --test packages/api/tests/fs.test.mjs
```

**回滚**：纯测试与常量，回滚无副作用。

---

### B1 · 三处静默失败

**目标**：把三种"不报错但结果是错的"变成"当场报错"。

#### B1-1 `fs` 的 `baseDir` 被静默忽略

**现状**：`packages/api/src/fs.js` 的 `readTextFile(path)` 只传 `{ path }`，Tauri 的第二个参数 `{ baseDir }` 被 JS 静默丢弃 → 相对路径按**进程 CWD** 解析。

**改动**

| 文件 | 改动 |
|---|---|
| `packages/api/src/fs.js` | 每个函数按 Tauri 的签名位置接收 options，调用统一校验 `assertNoBaseDir(fnName, options)`；非空 options 一律抛错，文案给出替代方案（`await appDataDir()` 拼绝对路径） |
| `packages/api/tests/fs.test.mjs` | 参数化 14 条：传 `{ baseDir: 1 }` 必须 reject，且 `message` 含 `baseDir` |

**签名位置**（实测自 `@tauri-apps/plugin-fs` 2.x 的 `dist-js/index.d.ts`，**每个函数都有 options**）：

| 函数 | options 位置 |
|---|---|
| `readTextFile` / `readFile` / `readDir` / `stat` / `lstat` / `exists` / `size` | 第 2 参 |
| `writeTextFile` / `writeFile` | 第 3 参（第 2 参是 `data`） |
| `rename` / `copyFile` | 第 3 参（前两参是两个路径） |
| `truncate` | 第 3 参（第 2 参是 `len`） |
| `mkdir` / `remove` | 第 2 参（本库**已有** options，只需加 `baseDir` 拒绝） |

> `mkdir({ recursive })` / `remove({ recursive })` 在本库已实现，所以校验只能拒 `baseDir` 这个键，不能整体拒——**逐键判**，不要一刀切。
> `truncate` 的 `len` 是第 2 参，别把它当 options 拒掉；这是最容易写错的一处。

**DoD**：14 条断言全绿；`grep -n "baseDir" packages/api/src/fs.js` 能看出统一的拒绝入口。

#### B1-2 超时没有错误码

**现状**：`src/OrielWeb/Bridge/oriel-bridge.js:157` 只 `reject(new Error(...))`，页面无法机器判别超时。

**改动**

| 文件 | 改动 |
|---|---|
| `src/OrielWeb/Ipc/OrielIpcException.cs` | `OrielIpcErrorCodes` 加 `public const string Timeout = "timeout";` |
| `src/OrielWeb/Bridge/oriel-bridge.js` | 超时 reject 的 Error 挂 `code = 'timeout'`（字符串字面量 + 注释指向 `OrielIpcErrorCodes.Timeout`） |
| `tests/bridge/bridge.test.mjs` | 在 726 行附近的 per-call timeout 用例里断言 `err.code === 'timeout'` |
| `packages/api/src/core.js` | JSDoc 的错误码列表加 `timeout` |
| `packages/api/tests/core.test.mjs` | 断言超时错误带 `code` |

**注意**：桥接脚本是独立注入的 JS，**不能引用 C# 常量**。两边各写一份字面量是必然的，所以测试必须同时钉住两边（桥接测试 + 包测试）——只钉一边，改另一边就会静默漂移。

#### B1-3 `ORIELWEB106` 是 Warning

**现状**：`src/OrielWeb.Generators/OrielCommandGenerator.cs:117-120` 是 `DiagnosticSeverity.Warning`；应用定义 `win.*` 命令会**注册成功但永不可达**。

**改动**

| 文件 | 改动 |
|---|---|
| `src/OrielWeb.Generators/OrielCommandGenerator.cs:117` | `Warning` → `Error`（与同族 101–105 一致） |
| `tests/OrielWeb.Tests/GeneratorDiagnosticsTests.cs:161-175` | 断言从 warning 集合改到 error 集合 |
| `README.md` "两条容易踩的约定" | "会被**静默遮蔽**" → "会在**构建期报错**" |
| `docs/API.md` 保留前缀相关段落 | 同步措辞 |

**前置检查**（先跑，避免改完构建红）：
```bash
grep -rn 'OrielCommand("win\.' --include=*.cs . | grep -v "/bin/" | grep -v "/obj/"
grep -rn 'OrielCommand("tray\.' --include=*.cs . | grep -v "/bin/" | grep -v "/obj/"
```
`samples/` 与 `tests/` 里若有命中，先改名再升级诊断——否则这批会连带修别人的示例。

**DoD**
```bash
dotnet test tests/OrielWeb.Tests -c Release --filter "FullyQualifiedName~GeneratorDiagnostics"
```

**回滚**：B1-3 单独一个提交，回滚只影响诊断严重级。

---

### B2 · `app` 状态族

**目标**：补 `app.exit` / `app.show` / `app.hide`——审计里的 P1-1，也是"页面能退出应用"这个最基础的能力。

**为什么现在好做**：门禁已经预留好了。`OrielIpcGuard.cs:175-182` 只放行 `AlwaysAllowedAppCommands` 里**列举的 5 条**，注释明确写着"将来加 exit/show/hide 时它们不在名单里，会照旧走能力校验"。所以不需要改门禁逻辑。

**改动清单**

| 文件 | 改动 |
|---|---|
| `src/OrielWeb/Ipc/OrielBuiltInAppCommands.cs` | 加 3 个 case：`app.exit` → `window.App.Quit()`；`app.show` / `app.hide` → 遍历 `window.App.Windows` 调 `Show()` / `Hide()` |
| `src/OrielWeb/App/OrielApp.cs` | 可选：加 `ShowAllWindows()` / `HideAllWindows()`（把遍历放进 App，命令层只调一次；也为将来 macOS 的 `NSApp` 语义留口） |
| `src/OrielWeb/Ipc/OrielCapabilityRules.cs` | **不加**进 `AlwaysAllowedAppCommands`——这是刻意的，且要补一条注释说明 |
| `packages/api/src/app.js` | 加 `exit(code?)` / `show()` / `hide()` 三个导出 |
| `packages/api/package.json` | 无需改（`app` 已导出） |
| `packages/api/tests/app.test.mjs` | 3 条：命令名与参数形状；`exit` 传 code 时原样透传；未授权时 reject |
| `tests/OrielWeb.Tests/BuiltInAppStateCommandTests.cs`（新建） | 用 fake backend 断言 `Quit`/`Show`/`Hide` 被调用，**不真退出**；`app.exit` 不在 always-allowed 名单 |
| `docs/API.md` §11.2 与 §4 | 表格补 3 行；能力表说明"这三条必须显式 Allow" |

**待定的语义**（见 §6 D-2）：`app.show()` 是"显示所有窗口"还是"取消 macOS 应用隐藏"。建议**先用"所有窗口"**——三平台语义一致，且不引入平台分支；macOS 的 `NSApp` 语义等真有需求再补。

**DoD**
```bash
dotnet test tests/OrielWeb.Tests -c Release --filter "FullyQualifiedName~AppState"
node --test packages/api/tests/app.test.mjs
```

**风险**：`Quit()` 有退出超时与看门狗（`UseExitTimeout` / `UseExitWatchdog`）。测试里必须用 fake，且断言"没真的调 `Environment.Exit`"。

---

### B3 · `listen` 形状 + 事件常量

**目标**：消掉审计里的 B4（`listen` 返回同步函数）与事件常量缺口。

**这是本计划唯一的破坏性变更候选**，所以先看 D-1。

| 方案 | 做法 | 优点 | 代价 |
|---|---|---|---|
| **A. thenable 函数**（推荐） | 返回的函数对象上挂 `then(resolve)`，`await` 与直接调用都成立 | **零破坏**：既有 `const un = listen(...)` 不用改；`await listen(...)` 也对；`listen(...).then(...)` 也对 | `un instanceof Promise === false`；`Promise.all` 里要包一层 |
| B. 真 Promise | 与 Tauri 完全一致 | 形状 100% 对齐 | 全仓 `const un = listen(...)` 全部要改；示例、文档、测试一起动 |
| C. 不改 | 保持现状 | 无成本 | 审计 B4 一直在 |

**推荐 A 的理由**：唯一的差别是 `instanceof`，而那个判断在实际迁移代码里几乎不存在；换来的是"既有页面一行不改"。B 方案的收益只是形式上的，代价却是全仓改动 + 破坏性发版。

**改动清单（按 A）**

| 文件 | 改动 |
|---|---|
| `packages/api/src/event.js` | `listen` / `once` 返回 thenable 函数；JSDoc 说明这是 thenable 而非 Promise |
| `packages/api/src/window.js` | `Window.listen` / `Window.once` 同步受益（它们转调 event.js） |
| `packages/api/tests/event.test.mjs` | 新增 3 条：`await listen(...)` 得到函数；`listen(...).then(fn => ...)` 成立；`typeof un === 'function'` 仍成立 |
| `docs/API.md` §11.2 `event` 行 | 补一句 thenable 说明 |

**若选 B**：额外需要
```bash
grep -rn "= listen(\|= once(" --include=*.js --include=*.mjs --include=*.html --include=*.md . | grep -v node_modules
```
逐处加 `await`，并在 CHANGELOG 标 **breaking**。

**DoD**：`node --test packages/api/tests/event.test.mjs`；若选 B，还要 `node --test tests/bridge/bridge.test.mjs` 确认桥接层没被带坏。

---

### B4 · 页面建窗

**目标**：补 `@tauri-apps/api/webviewWindow` 的最小可用面——页面能 `new WebviewWindow(label, options)`。

**现状**：只有 C# `app.CreateWindow(...)`，且它**要求 UI 线程 + Run 之后**（`OrielApp.cs:252-265` 有明确异常）。

**改动清单**

| 文件 | 改动 |
|---|---|
| `src/OrielWeb/Ipc/OrielBuiltInWindowApiCommands.cs` | 加 `window.new`：解析 `{ label, title, width, height, url, resizable, frameless }` → `window.App.CreateWindow(...)` → 返回 label |
| 同上 | **线程**：若 `!app.IsOnUiThread`，用 `app.InvokeOnUiThread` 编组后等待结果（`CreateWindow` 的异常要回传给页面） |
| `src/OrielWeb/Ipc/OrielCommandDispatcher.cs` | 无需改（`window.*` 族已挂载），但确认 `window.new` **不在** `AlwaysAllowedWindowCommands` 里 |
| `packages/api/src/webviewWindow.js`（新建） | `class WebviewWindow extends Window`；`new WebviewWindow(label, options)` 先 `invoke('window.new', ...)` 再返回实例；`getCurrentWebviewWindow()` / `getAllWebviewWindows()` |
| `packages/api/package.json` | `exports` 加 `./webviewWindow` |
| `packages/api/tests/webviewWindow.test.mjs`（新建） | 建窗成功/失败、未授权被拒、`getAllWebviewWindows` 映射 |
| `tests/OrielWeb.Tests/BuiltInWindowApiCommandTests.cs` | 加 `window.new`：label 重复报错、未授权被拒、非 UI 线程能编组成功 |
| `tools/verify-windows.ps1` / `verify-linux.sh` / `verify-macos.sh` | 新增 `--selftest windownew`（三处同加）：页面建窗 → 新窗口加载同一页面 → 两窗口互发事件 → 关掉 |

**关键风险**：`CreateWindow` 在 IPC 处理中调用，可能与"新窗口初始化期间又收到 IPC"重入。自检必须真的建一个窗口并等它 `Loaded`，不能只断言命令返回。

**DoD**
```bash
dotnet test tests/OrielWeb.Tests -c Release --filter "FullyQualifiedName~BuiltInWindowApi"
node --test packages/api/tests/webviewWindow.test.mjs
pwsh tools/verify-windows.ps1 -Stages selftest   # 含 windownew
```

---

### B5 · 显示器模型

**目标**：补 `currentMonitor` / `primaryMonitor` / `availableMonitors` / `monitorFromPoint` / `cursorPosition`。

**改动清单**

| 文件 | 改动 |
|---|---|
| `src/OrielWeb/App/OrielMonitor.cs`（新建） | 记录：`Name` / `Position` / `Size`（物理与逻辑两套）/ `ScaleFactor` / `IsPrimary` |
| `src/OrielWeb/Platform/IPlatformBackend.cs` | 加 `IReadOnlyList<OrielMonitor> GetMonitors()` 与 `OrielPosition GetCursorPosition()` |
| `Platform/Windows/WindowsPlatformBackend.cs` | `EnumDisplayMonitors` + `GetMonitorInfo` + `GetCursorPos` |
| `Platform/Linux/LinuxPlatformBackend.cs` | `gdk_display_get_n_monitors` / `gdk_monitor_get_geometry` / `gdk_monitor_get_scale_factor` |
| `Platform/MacOS/MacOSPlatformBackend.cs` | `[NSScreen screens]` / `mainScreen` / `backingScaleFactor` |
| `src/OrielWeb/Ipc/OrielBuiltInWindowApiCommands.cs` | 5 个命令：`window.currentMonitor` / `window.primaryMonitor` / `window.availableMonitors` / `window.monitorFromPoint` / `window.cursorPosition` |
| `packages/api/src/window.js` | 5 个导出 + `Monitor` 类型（纯 JSDoc） |
| `packages/api/tests/window.test.mjs` | 形状断言（字段名与 Tauri 的 `Monitor` 一致：`name` / `position` / `size` / `scaleFactor`） |
| 三平台验证脚本 | `--selftest monitors`：断言至少 1 个显示器、主屏尺寸 > 0、`availableMonitors` 含主屏 |

**已知平台事实**（要在 JSDoc 里如实标注，不要假装一致）：
- Linux Wayland 下 GDK 给的可能是逻辑坐标，X11 下是物理坐标——**如实报告**，不强行换算；
- `monitorFromPoint` 在 Wayland 可能拿不到跨屏坐标。

**DoD**：三平台自检各打印一行（显示器数 / 主屏尺寸 / 缩放），Windows 与 WSL 必须真跑。

---

### B6a · 菜单模型 + `popup`

**目标**：先把"菜单"这个数据模型搬到前端（`Menu` / `Submenu` / `MenuItem` / `PredefinedMenuItem` / `CheckMenuItem` / `IconMenuItem` / `NativeIcon` / `itemFromKind`），并支持 `Menu.popup()`。

**为什么先做这个**：C# 已有 `OrielMenuItem` + 三平台菜单构建（`Win32Menu` / `GtkMenu` / `MacOSMenu`），**但它们只服务于托盘菜单与上下文菜单**。把模型暴露给页面 + 支持 popup，是"复用既有能力"，风险低；菜单栏要动窗口布局，单独一批。

| 文件 | 改动 |
|---|---|
| `src/OrielWeb/Ipc/OrielBuiltInMenuCommands.cs`（新建） | `menu.new` / `menu.append` / `menu.popup` / `menu.setAsWindowMenu`（先只做 popup，`setAsWindowMenu` 留到 B6b） |
| 三平台菜单后端 | 复用现有构建器；`popup` 需要"在指定坐标弹出"（Windows `TrackPopupMenuEx` / GTK `gtk_menu_popup_at_rect` / macOS `popUpMenuPositioningItem`） |
| `packages/api/src/menu.js`（新建） | 8 个导出，类形状对齐 Tauri |
| `packages/api/package.json` | `exports` 加 `./menu` |
| `packages/api/tests/menu.test.mjs`（新建） | 模型序列化形状、popup 命令参数 |
| 三平台验证脚本 | `--selftest menu`：弹一个 3 项菜单并取消（不真点） |

**注意**：Tauri 的 `Menu` 方法有 `append` / `prepend` / `insert` / `remove` / `removeAt` / `get` / `items` / `popup` / `setAsAppMenu` / `setAsWindowMenu`。本批只做 `append` / `popup` / `items`，其余在 B6b 补——**别一次做全**，`insert`/`removeAt` 要维护 index 一致性，容易出错且当前没有调用方。

---

### B6b · 应用菜单栏

**目标**：`Menu.setAsAppMenu()`（macOS 顶部菜单）与 `setAsWindowMenu()`（Windows/Linux 窗口内菜单栏）。

**这是全计划风险最高的一批**，因为 Linux 要改窗口布局：当前 `LinuxWindowHost` 的 `GtkWindow` 直接装 webview，加菜单栏要改成 `GtkBox { menubar, webview }`——**所有尺寸/几何换算都要重新验一遍**（`WindowSizeSemanticsTests` 的客户区契约会被直接影响）。

| 平台 | 落点 | 难点 |
|---|---|---|
| Windows | `SetMenu(hwnd, hMenu)` + `WM_COMMAND` 路由到 `MenuEvent` | `WM_NCCALCSIZE` 与无边框分支要重算客户区 |
| Linux | `GtkMenuBar` + `GtkBox` 重排 | 客户区尺寸语义改变，必须重跑 E-1 那套测试 |
| macOS | `[NSApp setMainMenu:]` | 与"无边框"无关，相对独立 |

**建议**：B6b 拆成三个平台提交（Windows → macOS → Linux），Linux 单独一批并**先跑几何自检**。若时间紧，**只做 macOS + Windows**（Linux 的布局改动性价比最低），在文档里如实标"Linux 无应用菜单栏"。

**DoD**：三平台各一条自检（菜单项可见 + 点击回传事件）；Linux 额外跑 `--selftest geometry` 确认客户区未回归。

---

### B7 · 迁移演练

**目标**：停止按名字对账，用真实卡点决定后续投入。

**做法**
1. 选 2–3 个真实的 Tauri 2 开源小应用（一个单窗口工具、一个多窗口应用、一个用 fs/对话框的）；
2. 只做机械迁移：`@tauri-apps/api` → `@orielweb/api`，`@tauri-apps/plugin-*` → 对应模块；命令名按下表映射；
3. **不改业务逻辑**，让它在编译期/运行期把问题暴露出来；
4. 记录每个卡点：属于哪一类（缺能力 / 语义差异 / 命名 / 权限），修一次的成本。

**命令名映射表**（迁移必备，B7 的副产物，应进文档）

| Tauri | 本库 |
|---|---|
| `plugin:fs\|read_text_file` | `fs.readTextFile` |
| `plugin:dialog\|open` | `dialog.open` |
| `plugin:opener\|open_url` | `opener.openUrl` |
| 自定义 `#[tauri::command] fn add_item` | `[OrielCommand("add.item")] AddItem(...)`（参数名写 camelCase） |

**产出**：`docs/MIGRATION-FINDINGS.md`——按出现频率排序的卡点清单。下一轮计划由它决定，不再由导出面比对决定。

---

## 3. 每批的统一验收

任何批次合入前必须全绿：

| 层次 | 命令 |
|---|---|
| 单测 | `dotnet test tests/OrielWeb.Tests -c Release` |
| 桥接测试 | `node --test tests/bridge/bridge.test.mjs` |
| JS 包测试 | `node --test packages/api/tests/*.test.mjs` |
| 类型 | `npx tsc -p packages/api --noEmit` |
| 格式 | `dotnet format OrielWeb.slnx --verify-no-changes` |
| 自检 | 三平台脚本（涉及平台时**必须三处都加名字**） |

> `--selftest` 的开关名与结论行正则在三个脚本里各有一份（Windows 在 `Test-Stage` 的取值表与结论行匹配处、Linux 在 `stage_enabled` 与结论行正则处、macOS 在 `--selftest` 的模式分支处）。加新名字时**三处同改**，漏一处的结果是"某平台静默不验"——而 CI 仍然是绿的。
>
> 建议顺手加一条 CI 检查：三个脚本里出现的 selftest 名字集合必须相同。这条比"记得改三处"可靠。

---

## 4. 提交与版本节奏

| 批次 | 提交粒度 | 版本 |
|---|---|---|
| B0 | 1 个提交（测试 + 常量） | patch |
| B1 | 3 个提交（B1-1 / B1-2 / B1-3 各自独立） | patch |
| B2 | 1–2 个提交 | minor（新 API） |
| B3 | 1 个提交（选 A 则 patch；选 B 则 **minor + breaking**） | 见 D-1 |
| B4 | 2 个提交（C# 命令族 + JS 模块） | minor |
| B5 | 3 个提交（记录类型 + 三平台后端 + 命令/JS） | minor |
| B6a / B6b | 各 1–2 个提交 | minor |
| B7 | 文档提交 | — |

提交说明遵循 `AGENTS.md`：「类型: 简明描述」，中文。

---

## 5. 风险与对策

| # | 风险 | 影响 | 对策 |
|---|---|---|---|
| R1 | B3 选 B 方案导致全仓破坏 | 高：示例/文档/测试一起改，容易漏 | 默认 A 方案（thenable）；若坚持 B，先跑全仓 grep 统计调用点，再决定 |
| R2 | B4 的 `CreateWindow` 在 IPC 中重入 | 高：可能死锁或建出半个窗口 | 自检必须真建窗并等 `Loaded`；实现里对 `CreateWindow` 的异常做完整回传 |
| R3 | B6b 改 Linux 窗口布局，破坏客户区契约 | **高**：几何全平台回归 | Linux 单独提交；先跑 `--selftest geometry`；B6b 允许只做 Win/macOS |
| R4 | B1-3 把 Warning 升 Error，构建红 | 中：阻塞所有分支 | 先 grep 全仓 `win.` 前缀命令；有命中先改名，再升级诊断 |
| R5 | B1-1 一刀切拒绝 options，误伤 `mkdir({recursive:true})` | 中：功能回归 | 逐键判，只拒 `baseDir`；测试覆盖 `recursive` 仍可用 |
| R6 | 新命令忘了进能力表 → 全平台"命令不存在" | 中 | 每批 DoD 里加一条"Debug 下可调、Release 未授权被拒"的双向断言 |
| R7 | 三平台验证脚本漏加名字 | 低但会静默漏平台 | §3 已列三处行号；CI 里加"三个脚本的 selftest 名单一致"的检查（可选） |

---

## 6. 待决策点（动手前必须定）

| # | 决策 | 选项 | 建议 |
|---|---|---|---|
| **D-1** | `listen` 的对齐方式 | A thenable 函数（零破坏）/ B 真 Promise（完全对齐，破坏性）/ C 不改 | **A**：唯一差别是 `instanceof Promise`，实际迁移代码几乎不判它 |
| **D-2** | `app.show/hide` 的语义 | A 遍历所有窗口 / B 跟 macOS `NSApp` 隐藏走 | **A**：三平台一致，不引入平台分支 |
| **D-3** | `window.new` 的参数集 | A 只收本库支持的（label/title/size/url/resizable/frameless）/ B 照抄 Tauri 的完整 `WindowOptions` | **A**：B 会让"传了没生效"变成新的静默失败 |
| **D-4** | 菜单批次范围 | A 只做 popup / B popup + 菜单栏一起 / C 只做 macOS+Windows 菜单栏 | **A → C**：先 popup（低风险），菜单栏按平台分批，Linux 最后 |
| **D-5** | `baseDir` | A 显式拒绝 / B 实现 `BaseDirectory` 枚举（+ 24 个目录值） | **A**：拒绝是 1 小时的事，实现要补齐枚举 + 映射 + 测试，而收益只在迁移期 |

---

## 7. 进度表

| 批次 | 状态 | 证据 |
|---|---|---|
| B0 | ✅ 完成（2026-10-19） | `d4cb4bd`：PLANNED_MODULES 登记 4→3、常量逐键比对、`TauriEvent` 补齐 16 键、`fs.stat.mtime` 类型断言 |
| B1 | ✅ 完成（2026-10-19） | `675c4c2`（baseDir 14 条）/ `8a110ed`（timeout 码两侧钉住）/ `0e4a92c`（ORIELWEB106 → Error） |
| B2 | ✅ 完成（2026-10-19） | `1e5ab23`：`app.exit/show/hide` + 门禁姿态测试；三条仍不在 always-allowed 名单 |
| B3 | ✅ 完成（2026-10-19，按 D-1 选 A） | `12a046c`：thenable 函数，三种写法并存；`instanceof Promise` 是唯一差别 |
| B4 | ✅ 完成（2026-10-19） | `b993aa1` + `fecd7d6`：`window.new`、`@orielweb/api/webviewWindow`、`--selftest windownew`（三平台 PASS） |
| B5 | ✅ 完成（2026-10-19） | `5c3c17c`：`IMonitorBackend` + 三平台 `*MonitorBackend`、5 个 `window.*` 命令、`--selftest monitors`（Windows 实测 PASS，Linux/macOS 走 CI） |
| B6a | ✅ 完成（2026-10-19） | `@orielweb/api/menu`（8 个导出，`NativeIcon` 56 键逐键对齐）、`menu.popup` / `menu.close`、三平台按坐标弹出、`--selftest menu` |
| B6b | 待开始（可裁剪） | Linux 要做窗口布局改造，建议最后做或不做 |
| B7 | 待开始 | 需要 2–3 个真实 Tauri 应用做迁移演练 |

> **B4 的一处实现细节值得记住**：Tauri 用 `once('tauri://created')` 通知建窗结果，
> 本库没有这条页面事件（`WEBVIEW_CREATED` 在桌面端不会触发），所以改成构造即发起、
> 结果在 `created`（Promise）上。这是本计划范围内唯一一处**必须改调用点**的迁移差异，
> 已写进 `webviewWindow.js` 的 JSDoc 与 `docs/API.md`。
>
> **对齐账本在 B4 暴露过一次真实问题**：方法比对原先只看类的 own property，
> `WebviewWindow extends Window` 不重抄父类方法，于是八十多个继承来的方法会被误报成缺失。
> 已改为沿原型链收集；同时发现 webview 级独有的六个方法——
> `position`/`size`/`setAutoResize`/`reparent` 属于"本库没有 webview 层"的结构性差异（记入
> `METHOD_DIVERGENCES`），`setZoom`/`clearAllBrowsingData` 属于跨平台一致的待做项（记入 B 组并标批次）。
> B 组"必须为空"的断言随之改成"必须写明批次"。——**账本自己也会腐化，这正是它该被测试的原因**。
>
> **B5 改变了后端的分层方式（按"每平台一个子后端"）**：计划里原本是往
> `IPlatformBackend` 加两个成员，实际改成新建 `IMonitorBackend` + 三个
> `*MonitorBackend.cs`。理由是 `IPlatformBackend` 已经 742 行、混了六件事，
> 而显示器是与它们互不相干的一整块平台知识（三套枚举 API、三套坐标系、三套缩放语义）。
> 分开之后每个文件只讲一件事，与 `ITrayBackend` 同一套分层；`OrielApp` 只多一行
> `MonitorBackend = _backend.Monitors`。**后续批次若要再加应用级能力，沿用这个形状**
> ——先问"这是一个新的子后端，还是现有子后端的一个成员"，不要往主接口上继续堆。
>
> **B6a 有三处值得记住的教训**：
>
> 1. **`NativeIcon` 凭印象写就是错的**。第一版多出 `Apple`/`Document`/`Generic*` 等 12 个
>    Tauri 里根本不存在的键，又少了 `Bluetooth`/`Caution`/`TrashFull` 等。这些都不会让任何
>    代码报错——它们只是让"用 `NativeIcon.Apple` 的迁移代码"在运行期拿到 `undefined`。
>    教训是**常量表必须从参照实现枚举出来**，不能手写；现在这张表由对齐账本的常量比对逐键盯着。
>
> 2. **子菜单的 `items` 曾经是空的**。`MenuItemBase._wire()` 调 `_wireCore(id)` 时没把
>    `actions`/`token` 往下传，而 `Submenu._wireCore` 靠这两个参数递归序列化子项。表现是
>    "菜单弹出来了，但子菜单是空的"——单测一跑就红（22 条里 4 条），因为断言直接看线格式。
>    这印证了 B0 那条原则：**先让差异可见**。
>
> 3. **`menu.close` 不是锦上添花，是必需品**。Windows 的 `TrackPopupMenuEx` 自带模态循环，
>    `popup()` 在用户选择前不返回，UI 线程被占着时页面上的按钮点不到。Tauri 那边菜单是 Rust
>    资源、可以在别的线程上结束它；本库没有资源表，只能显式给一条命令。
>    <br>但**自检不能靠它**：`menu.close` 也是一条 IPC 消息，要排进同一套消息泵，而泵此刻正被
>    阻塞的弹出占着——实测这条回路不可靠。自检改用**宿主侧自动关闭**
>    （`ORIELWEB_MENU_AUTODISMISS_MS` → 三平台各自的取消机制），完全不依赖页面。
>    这个分工值得记住：**`menu.close` 给应用用（用户点了页面上的按钮），自动关闭给测试用**。
>
> 4. **账本的 `METHOD_DIVERGENCES` 是按方法名索引的扁平表**，一个名字只能有一条理由。
>    `close` 恰好落在两种不同情形上（menu 有、Image 没有），登记之后 `Image` 缺 `close`
>    这件事就被静默掩盖了。已补一条与扁平表无关的独立断言钉住 `Image`。
>
> 5. **"三平台弹出时机"这件事，文档写的和实际的不一样**。原先按"macOS 异步、Windows 阻塞"
>    写进 API.md，实测 **macOS 也是阻塞的**（`popUpMenuPositioningItem:atLocation:inView:`
>    一直跟踪到用户选择，Apple 文档没写）。现在文档与三处注释统一为
>    **Windows + macOS 阻塞、只有 Linux 异步**。这是迁移者最容易踩的一处语义差异
>    （"`await popup()` 之后用户已经选过了"在三个平台都不成立）。
>
> 6. **"关掉一个阻塞的菜单"在三个平台需要三种完全不同的机制**，而且每一种都踩过坑：
>
>    | 平台 | 手段 | 踩过的坑 |
>    |---|---|---|
>    | Windows | `PostMessage(WM_CANCELMODE)` | `EndMenu` **无效**——它只结束 `StartMenu`/`EndMenu` 那套经典协议，`TrackPopupMenuEx` 用的是自己创建的模态循环 |
>    | macOS | `performSelector:afterDelay:inModes:` + `NSEventTrackingRunLoopMode` | ① 从后台线程调 `cancelTracking` 会崩（AppKit 要求主线程）；② 投回主线程走的是 **default mode**，而跟踪时主线程跑在 **eventTracking mode**，等不到；③ `objc_msgSend` 的槽位漏了 `withObject:` 那个 id，double 落进整数寄存器 |
>    | Linux | `gtk_menu_popdown`（回 GTK 主循环） | 该函数不是线程安全的，必须编组 |
>
>    这条对后续批次有用：**"阻塞式原生 UI + 程序化取消"是三个平台各有一套的东西**，
>    不存在一份能照抄的实现。而且它们的失败形态都很隐蔽（超时、静默不触发），
>    只能靠真机自检取证——这正是 `--selftest menu` 存在的理由。

---

## 8. 一页速览

```
B0 账本收口        ← 先做。让后面所有差异都能被测到
B1 静默失败        ← 三处小改，收益最高（避免迁移时"不报错但是错的"）
B2 app.exit        ← 最小可用能力，门禁已预留
B3 listen 形状      ← 唯一的破坏性候选，D-1 定生死
B4 页面建窗         ← 多窗口应用的门槛
B5 显示器           ← 多屏体验的门槛
B6 菜单             ← 先 popup，菜单栏按平台拆（Linux 最后/可不做）
B7 迁移演练         ← 停止按名字对账，让真实卡点决定下一步
```
