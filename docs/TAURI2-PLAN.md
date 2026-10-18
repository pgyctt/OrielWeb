# OrielWeb ↔ Tauri 2 对齐：目标与执行计划

> 输入：[TAURI2-ALIGNMENT.md](TAURI2-ALIGNMENT.md)（差异分析）
> 本文件只回答两件事：**要做到什么算完成**（目标），**按什么顺序做**（计划）。
>
> 分析基准：仓库当前 `main`（0.3.5，`docs/TAURI2-ALIGNMENT.md` 未入库），
> 已逐条对照 `src/OrielWeb/**` 核实，勘误见 §2。

## 1. 一页结论

1. **差异分析的方向成立**：S1–S7 七条结构性差距都真实存在，P0 排序也对——JS 包与 `label` 确实是"做了以后其他项成本减半"的两件事。
2. **但有 7 处需要修正**（§2），其中 **E-1（三平台尺寸语义本就不一致）** 影响最大：它不是"对齐 Tauri 时的语义取舍"，而是**当前库的既有缺陷**——同一份代码在 Windows 上得到的是整窗尺寸，在 Linux/macOS 上得到的是客户区尺寸。frameless 窗口下两者几乎相等，所以至今没在现场暴露；装饰窗口上立刻可见。
3. **因此 P0-5 要拆成两步**：先"三平台内部一致"，再"与 Tauri 一致"。前者是修 bug，后者才是对齐。
4. **总目标不是"实现 Tauri 的 80 个窗口方法"**，而是"迁移只改 import"。据此设 5 个里程碑，每个都有机器可验证的退出条件（§3），任何一项没做到就不算完成。
5. **建议的下一步**：批次 A（`@orielweb/api` 骨架）+ 批次 B（窗口 `label`）并行开工——两者文件不重叠，且是后面所有批次的前置。

## 2. 对差异分析的勘误（核实结论）

以下 7 条按"会不会误导实现"排序：E-1/E-2 会直接导致改错代码，必须改；E-3/E-4 影响实现方式，强烈建议补；E-5/E-6/E-7 是量级与一致性问题。

### E-1（必须改）§1.5 尺寸语义：三平台**本来就不一致**，不是统一的外框语义

原文断言 `Resize` = 整窗外框。实际三平台用的是三种不同语义的 API：

| 平台 | `Resize` 实现 | 语义 | 初始尺寸 API | 语义 |
|---|---|---|---|---|
| Windows | `SetWindowPos(..., w, h, ...)`（`Win32WindowHost.cs:1821`） | **整窗**（含边框标题栏） | `CreateWindowExW(..., w, h, ...)`（`:392`） | **整窗** |
| Linux | `gtk_window_resize`（`LinuxWindowHost.cs:985`） | **客户区**（GTK 拿不到装饰尺寸） | `gtk_window_set_default_size`（`:442`） | **客户区** |
| macOS | `setContentSize:`（`MacOSWindowHost.cs:866`） | **客户区** | `initWithContentRect:`（`:345`） | **客户区** |

**后果**：偏差只出现在 Windows + 装饰窗口。`samples/OrielDemo` 用的是 `WithFrameless()`，`WS_POPUP` 下整窗 ≈ 客户区，所以自检与真机都没暴露。一旦应用用装饰窗口（默认形态！`Frameless` 默认 `false`），Windows 的内容区就比 Linux/macOS 小一圈——**这与是否迁移 Tauri 无关，是现在的行为**。

**对计划的影响**：P0-5 的验收标准必须是"三平台先一致"，而不是"与 Tauri 一致"。Windows 侧要改用 `AdjustWindowRectExForDpi` 把客户区换算成整窗，或在 `Resize` 里区分 inner/outer 两条路径。

> **实现前置**：`Win32Native.cs` 目前**没有** `AdjustWindowRectExForDpi` 绑定（只有 `GetDpiForWindow`，`:611`），要新增。三平台对称的缺口：Linux 侧 `GtkNative.cs` 也**没有** `gtk_widget_get_scale_factor` / `gdk_window_get_scale_factor` 绑定，`ScaleFactor` 需要一并新增。这两条要算进 E1/E2 的量级，别当成"现成值取一下"。

### E-2（必须改）§1.3 事件源：`resize`/`move`/`focus` 并非"三平台各自已有原生事件源"

原文说"三平台后端各自已有原生事件源"。实际缺口：

| 事件 | Windows | Linux | macOS |
|---|---|---|---|
| resize | `WM_SIZE` 已处理（`Win32WindowHost.cs:779`，但只 `UpdateBounds`，不上报） | **无** `configure-event` 连接（`LinuxSignalHandlers.cs:92-120`） | `windowDidResize:` 已有 |
| move | `WM_MOVE` **刻意 `return 0` 且不取位置**（`:783`） | 同上 | **无** `windowDidMove:` |
| focus/blur | `WM_SETFOCUS` 只移交焦点（`:769`）；`WM_KILLFOCUS` **刻意不处理**（`:773` 有明确理由） | **无** `focus-in-event` / `focus-out-event` | **无** `windowDidBecomeKey:` / `windowDidResignKey:` |
| destroyed | `WM_DESTROY` 已有（`:825`） | `destroy` 已有（`:96`） | `windowWillClose:` 已有（`MacOSObjCClasses.cs:123`） |

**后果**：P0-3 不是"接线"，而是"新增信号 + 上报"。尤其 `WM_KILLFOCUS` 那条——注释解释了为什么**不移交焦点**，但**上报 blur 事件**与移交焦点是两件事，可以只上报不动焦点。实现时要区分清楚，否则会踩回已修过的坑（失焦后 caret/IME 错乱）。

### E-3（建议补）§1.3 第 3 条：`close-requested` 的 preventDefault 有一个同步/异步硬冲突

原文说"JS 侧需要一个往返"。实际难点没说透：**Windows 的 `WM_CLOSE` 是同步消息**，`Win32WindowHost.cs:811` 在消息处理中直接决定 `DestroyWindow` 或 `return 0`。而页面回执是异步的（`invoke` 走 IPC）。所以不可能"在 `WM_CLOSE` 里等页面答复"。

**可行的对齐做法**（Tauri 用的也是这个）：拦截 `WM_CLOSE` → 若该窗口已注册"页面接管关闭"，则**挂起关闭**（`return 0`，窗口不销毁）→ 发 `close-requested` 事件 → 页面在回调里决定：不调 `preventDefault` 则 JS 包发 `window.destroy` 真正关闭；调了则什么都不做。

需要一个"挂起中"的状态机，且要处理"页面已挂起但窗口被 `DestroyWindow` 强关"的竞态。这是 P0-3 里最容易被低估的一项，建议单独一批（C5）。

### E-4（建议补）§1.4 / §4.4：拒绝层已有 4 个，不是 3 个

原文说"现有三层：Source/Token/Command，加 `Scope` 第四层"。实际 `OrielIpcRejectionLayer`（`Ipc/OrielIpcRejectedEventArgs.cs`）已有 **Source / Token / Command / Size** 四个值，`Size` 是 1 MiB 入站规模闸。所以 `Scope` 是**第五个**枚举值，且要明确它插在 `Command` 之后（命令授权通过后再判路径 scope）还是之前。建议：**之后**——先判"这个命令能不能调"，再判"这个路径在不在范围内"。

另：`tray.*` 与 `win.*` 一样在 `OrielCapabilityRules.IsBuiltInCommand` 的"始终放行"名单里（`:100-101`）。原文只强调了 `win.*` 的后门，`tray.*` 也是。新增 `tray.setIcon(Image)` 之类会扩大这个后门的实际能力面，需要一并评估。

### E-5（建议补）§2.3 `setSize` 语义相反——只在 Windows 上相反

见 E-1。原文表述"`Resize` 是外框尺寸"会让实现者以为 Linux/macOS 也要改，实际只有 Windows 要动。这一点搞错会导致两个本来正确的平台被改坏。

### E-6（建议补）§2.3 说 `startResizeDragging` 缺失——Windows/Linux 已实现，只缺 macOS 与 JS 入口

原文把 `startResizeDragging(direction)` 判为 ❌，但代码里已有等价能力，只是**没有页面入口**：

| 平台 | 现状 |
|---|---|
| Windows | `HitTestResizeBorder`（`Win32WindowHost.cs:1381`）在 `WM_NCHITTEST` 里返回 `HT*`，系统原生边缘缩放已生效 |
| Linux | `LinuxResizeSupport.ResolveEdge` 判定 + `gtk_window_begin_resize_drag`（`LinuxWindowHost.cs:365`、`GtkNative.cs:225`），frameless 下也生效 |
| macOS | 无（`styleMask` 带 `StyleResizable`，装饰窗口靠系统边框；frameless 下没有热区） |

所以这一项的判定应是 **⚠️ 语义差异（只缺 API）**，而不是 ❌ 缺失。量级从"三平台实现"降到"macOS 补热区 + 加一条 JS 命令"。归到 M5 的 P2-1（窗口修饰）里一起做。

### E-7（顺带发现）`API.md` 里 macOS 退出的说法已过期（两处）

`API.md:145` 与 `API.md:168` 都写着"macOS `NSApplication.terminate:`"：

> 1. 结束消息循环（Windows `PostQuitMessage`、Linux `GtkMainQuit`、**macOS `NSApplication.terminate:`**），让 `Run()` 返回；

而 `CHANGELOG.md:24-29` 明确记录 macOS 已从 `terminate:` **改成 `stop:`**（因为 `terminate:` 不让 `run` 返回，退出清理被整个跳过，`ExitCompleted` 永不触发）。文档没跟上，且这两处正好是"让 `Run()` 返回"的上下文——说法与实际实现相反。这与 Tauri 对齐无关，但属于"同一份文档里两处说法互相矛盾"，做 E 系列时顺手改掉。

### 核实无误的部分（可直接依赖）

| 原文结论 | 核实 |
|---|---|
| 没有 npm 包 / TS 类型 | ✅ 仓库无 `package.json` / `tsconfig.json`，前端只有 `tests/bridge/*.mjs` |
| 窗口没有 `label` | ✅ 全库 `Label` 只出现在菜单项（`OrielMenuItem.Label`）与 macOS 的 `LaunchAgent` |
| 事件是裸 payload + 点分名 | ✅ 页面可见事件恰好 5 个：`navigation.starting` / `navigation.completed` / `theme.changed` / `maximized`（bool）/ `tray.event` |
| 几何只有 int 逻辑像素 | ✅ `Resize(int,int)` / `MoveTo(int,int)` / `SetMinSize(int,int)`，无 `double` 入口 |
| 错误是字符串 | ✅ `OrielIpcException` 只有一个构造器、只有 `Message` |
| 无 Resource / rid | ✅ 无任何句柄表 |
| 无内建命名空间（只有 `win.*` / `tray.*`） | ✅ `OrielBuiltInWindowCommands` 12 条 + `OrielBuiltInTrayCommands` 11 条 |
| 超时是 30s 全局 | ✅ `oriel-bridge.js:92` `timeout: 30000`，`invoke` 读全局值，无 per-call |
| `oriel.version` 是库版本 | ✅ `OrielBridgeTemplate.VersionLiteral` 读程序集版本 |
| 托盘是唯一完整对齐项 | ✅ 11 条命令 + `TrayHandle`，方法名与 Tauri 一一对应 |
| `path` 全缺、`OrielPathResolver` 不对外 | ✅ 37 行、`internal` |
| `mocks` 缺失（JS 包一建就必须补） | ✅ 判断正确 |

## 3. 目标

### 3.1 北极星（唯一终极验收）

> **同一份前端代码，只做两件事就能在 Tauri 2 与 OrielWeb 下都跑：**
> ① `@tauri-apps/api/*` → `@orielweb/api/*` 的 import 替换；
> ② [TAURI2-ALIGNMENT.md](TAURI2-ALIGNMENT.md) §3 那 16 条语义修正（多数由 JS 包吸收，剩下的是"超时要放宽""关窗即退出的行为不同"）。

这个目标**不进近期计划**——它是 M5 之后的终局验收，写在这里是为了给每个里程碑一个统一的"为什么"。

### 3.2 五个里程碑（每个都有机器可验证的退出条件）

#### M1 — 地基可写代码（对应 P0-1 + P0-2）

| 项 | 退出条件 |
|---|---|
| `@orielweb/api` 包存在 | `packages/api/` 有 `package.json`（`type: module`）+ 逐模块 `exports` 映射；`tsc` 产出 `.d.ts` 无报错 |
| `core` / `dpi` / `mocks` 可用 | `node --test packages/api/tests/*.test.mjs` 全绿；`mockIPC` 能在 Node 里驱动 `invoke` |
| 窗口有 `label` | C#：`OrielWindowOptions.Label`（默认 `"main"`，重复抛）、`OrielApp.GetWindow(label)` |
| 页面知道"我是谁" | `await getCurrentWindow().label === 'main'` 在 demo 页成立（三平台） |
| 回归 | 既有 `dotnet test` + `node --test tests/bridge` + 三平台 `--selftest` 全绿（**零破坏**） |

#### M2 — 事件与窗口形状对齐（对应 P0-3 + P0-4）

| 项 | 退出条件 |
|---|---|
| `event` 模块 | `listen` / `once` / `emit` / `emitTo` 四个函数；回调收到 `{event, id, payload}` |
| 内置事件 | `resize` / `move` / `focus` / `blur` / `close-requested` / `destroyed` 六种在**三平台**都能触发 |
| 关闭接管 | 页面 `onCloseRequested(e => e.preventDefault())` 能真正阻止关闭（Windows 含挂起/竞态路径） |
| `window` 模块基础 | `getCurrentWindow` / `getAllWindows` / `getByLabel` / `getFocusedWindow` + 现有 22 项包装 |
| 新命令族过门禁 | `window.*` **不在** allow 名单时被拒（一条回归测试钉死"后门不扩散"） |
| 验收 | `--selftest event`（六种事件逐条断言）+ `--selftest window` |
| demo 迁移 | `samples/OrielDemo/wwwroot` 改吃 `@orielweb/api`（证明包能用，不是只有测试能用） |

#### M3 — 几何正确（对应 P0-5，含 E-1 的修 bug）

| 项 | 退出条件 |
|---|---|
| 三平台先一致 | `Resize(w,h)` 与初始尺寸在**装饰窗口**下三平台客户区逐像素相同（这是修 bug，不是对齐） |
| `scaleFactor` | `WebviewWindow.ScaleFactor` 三平台可用（Windows `GetDpiForWindow/96`、Linux `gtk_widget_get_scale_factor`（需新增绑定）、macOS `NSScreen.backingScaleFactor`） |
| inner/outer 拆分 | `InnerSize` / `OuterSize` / `InnerPosition` / `OuterPosition` 四个查询 |
| JS 多态 | `setSize(new LogicalSize(800,600))` / `new PhysicalSize(...)` 都能用 |
| 验收 | `--selftest geometry`：100% / 150% / 200% 三档下逐像素断言，**同一份用例三平台结果一致** |

#### M4 — 高频能力（对应 P1）

| 项 | 退出条件 |
|---|---|
| `path` | 24 个目录 + 7 个路径操作 + 2 个常量，纯计算无平台后端 |
| `convertFileSrc` + 文件 scheme | 页面能 `<img src>` 加载 scope 内的本地文件；scope 外的路径被拒 |
| `dialog` 全量 | `open` / `save` / `message` / `ask` / `confirm`，过滤器形状对齐 `{name, extensions}` |
| `app` 元信息 | `getName` / `getVersion` / `getIdentifier` / `getBundleType` / `exit` |
| 已有 C# 能力的 JS 入口 | `opener` 3 个 + `notification` + `autostart` 3 个 |
| `clipboard`（text） | 三平台剪贴板读写 |
| 错误模型 | reject 的 `Error` 上带 `code` / `data`；`invoke` 支持 per-call `timeout` |

#### M5 — 进阶（对应 P2）

按 TAURI2-ALIGNMENT.md §4.3 P2-1 … P2-8 原样执行。**M5 不设统一退出条件**，每项独立验收（它们互不依赖，可按需取用）。

### 3.3 明确不做的（与差异分析一致，这里再钉一次）

移动端、插件模型 / `plugin:` 前缀 / 插件 ABI、`tauri.conf.json` 清单、多 webview、`shell` 子进程、`http`/`websocket`/`upload`/`sql`/`stronghold`、`updater`、`deep-link`。

**新增一条**：不引入前端构建链（打包器 / bundler / 框架）。理由见 §6 决策 D-1。

## 4. 计划

### 4.1 批次总表

量级是"单人、熟悉本库"的粗估：S ≤ 1 天，M 2–3 天，L 4–7 天，XL > 1 周。

| 批次 | 内容 | 前置 | 量级 | 主要改动面 |
|---|---|---|---|---|
| **A** | `@orielweb/api` 骨架（core / dpi / mocks） | — | M | 新增 `packages/api/**`、CI 加一步 |
| **B** | 窗口 `label` | — | M | `OrielWindowOptions`、`OrielApp`、`IOrielWindowControl`、注入脚本、能力模型 |
| **C1** | 后端事件接口（`Resized`/`Moved`/`FocusChanged`） | — | S | `IPlatformBackend.cs` |
| **C2** | 三平台事件接线（6 类事件 × 3 平台） | C1 | L | `Win32WindowHost`、`LinuxWindowHost` + `LinuxSignalHandlers` + `GtkNative`、`MacOSWindowHost` + `MacOSObjCClasses` |
| **C3** | `event` 模块 + `{event,id,payload}` 包装 | A、B | M | `packages/api/event.js`、注入脚本 |
| **C4** | `once` / `emit` / `emitTo` | C3 | S | 新内建 `event.emit`、JS 包 |
| **C5** | `close-requested` 挂起/接管状态机 | C2 | M | 三平台 `Closing` 路径 + 新命令 `window.destroy` |
| **D1** | `window.*` 命令族 + 按 label 路由 | B | M | 新 `OrielBuiltInWindowApiCommands`（与 `win.*` 分开） |
| **D2** | `window` 模块 JS 侧 | A、D1 | M | `packages/api/window.js` |
| **E1** | **修三平台尺寸语义不一致**（E-1） | — | M | Windows `Win32WindowHost`（`AdjustWindowRectExForDpi`） |
| **E2** | `ScaleFactor` + inner/outer 查询 | E1 | M | 三平台后端 + `WebviewWindow` + `IOrielWindowControl` |
| **E3** | `dpi` 多态换算 + `setSize`/`setPosition` | A、E2 | S | `packages/api/dpi.js`、`window.js` |
| **F1** | `path` 模块 | A | S | 纯计算，无后端 |
| **F2** | `convertFileSrc` + 文件 scheme + **scope 系统**（含 `Scope` 拒绝层） | F1 | L | 三平台 scheme handler、`OrielCapabilityOptions`、`OrielIpcRejectionLayer` |
| **F3** | `dialog` 全量（含 `ask`/`confirm`） | A | M | 三平台对话框 + JS 包 |
| **F4** | `app` 元信息 + `exit` + `show/hide` | A | S | `OrielApp`、JS 包 |
| **F5** | `opener` / `notification` / `autostart` JS 入口 | A | S | 只加命令包装 |
| **F6** | `clipboard`（text） | A | M | 三平台剪贴板 |
| **F7** | 错误模型（`code`/`data` + per-call timeout） | A | M | `OrielIpcException`、回执协议、注入脚本、JS 包 |
| **G** | demo 迁到 `@orielweb/api` + `--selftest api` | M2 完成 | M | `samples/OrielDemo/wwwroot`、新自检 |
| **H** | 迁移样例 `samples/OrielTauriCompat` | G | S | 同一页面 + 两份 `package.json` 别名 |

**M5（P2）不再拆批**，按差异分析 §4.3 逐项做，每项独立提交。唯一需要修正的是 **P2-1 里的 `startResizeDragging`**：Windows/Linux 已有热区实现（见 §2 E-6），只需补 macOS 与 JS 命令，量级是 S 不是 L。

### 4.2 关键路径与并行

```
A ──┬─→ C3 ─→ C4
    │
B ──┴─→ D1 ─→ D2
      └─→ C3（label 是 emitTo 的前置）

C1 ─→ C2 ─→ C5

E1 ─→ E2 ─→ E3

A ─→ F1 / F3 / F4 / F5 / F7   （互相独立，可任意插入）
F1 ─→ F2（scope 系统是 convertFileSrc 的前置）
```

- **A 与 B 完全并行**（A 只碰 `packages/`，B 只碰 `src/OrielWeb/`）。
- **C 与 D 都依赖 B**（都要 label），但 C 与 D 之间不互相依赖，可并行。
- **E 与 C 争同一批文件**（三平台 WindowHost）→ 单人做则串行，建议 **C 先 E 后**（事件是 P0 刚需，几何偏差在 frameless 下不明显）。
- **F 系列只依赖 A**，是"填缝工作"——任何一个卡住时都能切过去。`F2` 例外：它自带 scope 系统，是最重的一项，不要顺手插进别的批次。
- **C5（关闭接管）单独串行**：它动的是三平台的关闭路径，任何其它批次同时改那条路都会产生难查的竞态。

### 4.3 版本节奏

| 版本 | 内容 | 破坏性 |
|---|---|---|
| 0.4.0 | A + B + C1/C2/C3/C4 | 无（事件包装只在 JS 包内，旧 `oriel.on` 不变） |
| 0.5.0 | C5 + D + G | 无（`window.*` 是新族；`win.*` 保留） |
| 0.6.0 | E（含 E1 的 bug 修复）+ F1/F3/F4/F5 | **E1 是行为变更**：Windows 装饰窗口的内容区会变大（变成与另两平台一致）。CHANGELOG 必须写明"这是修 bug，尺寸可能变化" |
| 0.7.0 | F2/F6/F7 + H | `OrielIpcException` 加 `Code`/`Data` 是纯增量 |
| 0.8.0+ | M5 各项 | 逐项评估 |

**关于"破坏性变更只在首个稳定版前做"**（差异分析 §4.2）：本计划**不做任何破坏性变更**。`Resize` 保留 outer 语义（Windows 侧修成"客户区"是 bug 修复，不是语义变更——因为它要与另两平台一致），inner 走新增的 `SetInnerSize`。这样既有代码不会静默变样。

## 5. 每批次的验收动作（统一口径）

任何批次合入前必须过：

| 层次 | 命令 |
|---|---|
| 单测 | `dotnet test tests/OrielWeb.Tests -c Release` |
| 桥接测试 | `node --test tests/bridge/bridge.test.mjs` |
| JS 包测试 | `node --test packages/api/tests/*.test.mjs` |
| 类型 | `npx tsc -p packages/api --noEmit` |
| 格式 | `dotnet format OrielWeb.slnx --verify-no-changes` |
| 自检（Windows） | `pwsh tools/verify-windows.ps1 -Stages selftest` |
| 自检（Linux） | `bash tools/verify-linux.sh --stages selftest`（CI 里 xvfb） |
| 自检（macOS） | `bash tools/verify-macos.sh` |

**新增自检必须同时进三个脚本**，否则"只在 Windows 验过"会变成默认状态——现有 `--selftest` 的结论行正则分散在 `tools/verify-windows.ps1:289`、`verify-linux.sh:285-288`、`verify-macos.sh:222`，加名字要三处同步。

## 6. 待决策点

这几项必须在动手前定，否则中途返工。

### D-1：TS 源码 vs JS + JSDoc（建议后者）

| 方案 | 优点 | 代价 |
|---|---|---|
| A. TS 源码 + `tsc` 编译到 `dist/` | 类型即源码，最标准 | 引入构建步骤；CI 多一步；与"零依赖"调性冲突；`npm publish` 要带 `dist/` |
| **B. JS + JSDoc → `tsc` 只出 `.d.ts`**（推荐） | **无打包器**，`exports` 直接指向 `src/*.js`；CI 只跑 `tsc --noEmit` 校验类型；与 `tests/bridge` 的 `node --test` 完全同构 | 类型写在注释里，IDE 补全略弱（现代 tsserver 已足够） |

选 B。理由：本库的卖点是"零依赖、AOT、单文件"，前端包也不该引入 bundler。`node --test` 能直接跑 `.js`，CI 改动最小。

### D-2：`Resize` 语义 —— **已定，且结论与原计划相反**

| 方案 | 优点 | 代价 |
|---|---|---|
| A. `Resize` 改成 inner（对齐 Tauri） | JS 层少一层换算 | 原以为会**破坏性** |
| **B. `Resize` 保持 outer，新增 `SetInnerSize`**（原推荐） | 零破坏 | 需要两条代码路径 |
| **C. `Resize` 直接定为 inner（已采纳）** | 与 Tauri 一致；Windows 侧本来就是**错的** | 无 |

**为什么改判**：计划阶段我以为"Windows 设整窗"是一种可保留的语义选择，所以推荐了 B（保留 outer + 新增 `SetInnerSize`）。
实测取证后结论不同——Linux/macOS 的 `Resize` **本来**就是客户区语义，只有 Windows 是整窗。
所以"把 Windows 改成客户区"不是语义变更，而是**修 bug**：修完之后三平台一致，且与 Tauri 的 `setSize` 一致。
不存在"既有代码会变样"的问题，因为既有代码在 Linux/macOS 上一直是客户区语义。

代价是**必须区分 frameless**：无边框窗口的 `WM_NCCALCSIZE` 让客户区铺满整窗，
再换算一次就会大一圈。这一点用 `--selftest geometry-frameless` 单独钉住。

### D-3：`fs` 做不做（差异分析 §2.11 的决策点）

建议**做方案 B 的最小集**（`readTextFile`/`readFile`/`writeTextFile`/`writeFile`/`exists`/`mkdir`/`remove`/`readDir`/`stat` + `BaseDirectory` + allow/deny glob），理由：`convertFileSrc`（F2）反正要引入 scope 系统，fs 复用同一套 scope，边际成本低。**不做** `watch` / `FileHandle` 随机读写 / `copyFile` / `rename`。

这一项**推迟到 F2 之后再定**——如果 F2 的 scope 系统证明好用，fs 就是顺水推舟；如果 scope 做得很别扭，fs 就砍掉。

### D-4：`tray.*` 后门要不要收紧

`tray.*` 现在与 `win.*` 一样"始终放行"（`OrielCapabilityRules.IsBuiltInCommand:100`）。加了 `tray.setIcon(Image)` 之后，页面可以任意改托盘图标——风险低但确实扩大能力面。建议：**保持放行，但在文档里明确列出"后门包含哪些命令"**，而不是笼统说"win.* 与 tray.*"。

## 7. 风险与对策

| # | 风险 | 影响 | 对策 |
|---|---|---|---|
| R-1 | **E-1 的尺寸 bug 被当成"对齐工作"**，导致修完 Windows 又去改本来正确的 Linux/macOS | 高：改坏两个平台 | E1 的验收标准写成"三平台客户区逐像素相同"，且**先写测试再改**（`--selftest geometry` 先行） |
| R-2 | `close-requested` 的挂起状态机与"用户直接 DestroyWindow"竞态 | 高：可能关不掉窗口或崩 | C5 单独立批；Windows 侧先在自检里跑通"挂起→页面放行→销毁"与"挂起→强关"两条路径 |
| R-3 | 6 类事件 × 3 平台 = 18 处改动，散在 3 个 1000+ 行文件里 | 中：漏一个平台就"某平台没有 resize" | C2 的验收按"事件 × 平台"矩阵逐格勾，不用"跑通了"概括 |
| R-4 | `WM_KILLFOCUS` 加上报时误改回"移交焦点" | 中：caret/IME 错乱（已修过的坑） | C2 实现时注释里保留原有理由；只加 `FocusChanged?.Invoke(false)`，不动 `MoveFocus` |
| R-5 | 新 `window.*` 族误用 `win.*` 的后门 | **高：安全** | D1 加一条回归测试：`window.setIcon` 不在 allow 名单时必须被拒 |
| R-6 | 事件从裸 payload 变 `{event,id,payload}` 破坏既有页面 | 中 | 包装**只在 JS 包内**做；`oriel.on` 的旧语义原样保留。两套并存，不迁移也能跑 |
| R-7 | `packages/api` 引入 Node 工具链，CI 变慢 | 低 | D-1 选 B（无打包器）；CI 只加 `tsc --noEmit` + `node --test`，复用现有 Node 22 |
| R-8 | 窗口 `label` 重复、`GetWindow` 返回已销毁窗口 | 中 | B 的单测覆盖：重复 label 抛、关闭后 `GetWindow` 返回 null、`GetAllWindows` 不含已关闭 |
| R-9 | `--selftest` 新名字忘了同步三个验证脚本 | 低但会静默漏平台 | 加名字时三处同改（§5 已列行号） |

## 8. 进度

| 批次 | 状态 | 说明 |
|---|---|---|
| **A** `@orielweb/api` 骨架 | ✅ 完成 | `packages/api`：`core` / `dpi` / `mocks`，27 项测试 + `tsc --noEmit` 门禁，CI 已接入 |
| **B** 窗口 `label` | ✅ 完成 | `OrielWindowOptions.Label` / `OrielApp.GetWindow` / `WebviewWindow.Label` / `oriel.label`；27 项单测 + `--selftest multiwindow` 新增 5 条断言 |
| **E1** 尺寸语义修 bug | ✅ 完成（Windows 已验证） | Windows 侧加 `AdjustWindowRectExForDpi` 换算，`Create` / `Resize` / `WM_GETMINMAXINFO` 三条路径全部改为客户区语义；**frameless 明确不换算**。`--selftest geometry` 由红转绿：`784x561` → `800x600`，运行期 `Resize(640,480)` 也精确。新增 `--selftest geometry-frameless` 钉住反面 |
| **C1** 后端事件接口 | ✅ 完成 | `IWindowBackend` 新增 `Resized` / `Moved` / `FocusChanged`；`OrielSizeChangedEventArgs` / `OrielPositionChangedEventArgs`；`WebviewWindow` 三个公开事件 |
| **C2** 三平台事件接线 | ✅ 完成（Windows 已验证，另两平台待 CI） | Windows `WM_SIZE`+`WM_MOVE`+`WM_SETFOCUS`/`WM_KILLFOCUS`；Linux 新接 `configure-event` + `focus-in/out-event`（含 `GdkEventConfigure` 结构体映射）；macOS 新接 `windowDidMove:` + `windowDidBecomeKey:`/`windowDidResignKey:`。三平台都做 0.5 逻辑像素去重。新增 `--selftest windowevents` 7 项断言 |
| **C3** `event` 模块 | ✅ 完成 | `packages/api/event.js`：`listen` / `once` / `emit` / `emitTo` / `TauriEvent`；事件对象 `{event,id,payload}` **包装在包内**，底层 `oriel.on` 仍裸 payload（既有页面零影响）。`mocks` 新增 `mockEmit` 供前端单测造宿主事件 |
| **C4** `event.emit` 内建命令 | ✅ 完成（Windows 已验证） | 广播 / 按 label 定向；目标不存在静默丢弃并返回 0。**唯一一处"库内建但不始终放行"**：必须显式 `Allow("event.emit")`。新增 `ORIELWEB107` 诊断与 `--selftest event` 7 项断言 |
| **C5** 关闭接管状态机 | ✅ 完成（Windows 已验证） | `OrielClosePipeline`（宿主否决 → 页面接管 → 放行，三平台共用一份判定）；`InterceptClose` / `CloseInterceptEnabled` / `Destroy()`；内建命令 `win.destroy` / `win.setCloseIntercept`。三平台原生入口的**相反返回值语义**只在各自入口处翻译。新增 `--selftest close` 17 项断言（四条路径 + 逃生口） |
| **D1** `window.*` 命令族 | ✅ 完成（Windows 已验证） | 26 条命令，**可按 label 寻址**；门禁只为标题栏必需那组 + 三条只读查询放行（`OrielCapabilityRules.AlwaysAllowedWindowCommands`），其余 deny-by-default。新增 `window.setEventForwarding`（按需转发）、`window.scaleFactor`、`window.title` |
| **D2** `window` 模块 JS 侧 | ✅ 完成 | `getCurrentWindow` / `getAllWindows` / `getByLabel` / `getFocusedWindow` + `Window` 类（方法名与 Tauri 一一对应，命令名映射在包内）；`onCloseRequested` / `onResized` / `onMoved` / `onFocusChanged`；`TauriEvent` 名字映射 + 自动打开转发。新增 30 项前端包测试与 `--selftest window` 12 项断言 |
| **G** demo 迁到前端包 | ✅ 完成（Windows 已验证） | `app.js` / `manual-check.js` 改用 `@orielweb/api`，页面脚本改 `type="module"`；包源码**直接内嵌**（不复制、不提交副本）；新增 `--selftest api` 18 项断言（唯一一处"包在真页面里被 import 并调用"的取证） |
| **E2** inner/outer 查询 | ✅ 完成（Windows 已验证） | `WebviewWindow.InnerSize` / `OuterSize` / `InnerPosition` / `OuterPosition` + `window.*` 四条命令 + JS 四个方法。**刻意不在标题栏白名单里**（能按 label 读别的窗口几何）。`--selftest geometry` 扩展"查询"那半，实测与 E1 的边框开销**交叉吻合**：framed 下 `Outer - Inner = 16x39`、`InnerPos - OuterPos = 8,31` |
| **E3** dpi 多态接进 window 模块 | ✅ 完成 | `setSize` / `setPosition` / `setMinSize` 接受 `LogicalSize` 与 `PhysicalSize`（后者按窗口所在显示器的缩放比换算）；查询返回 `LogicalSize` / `LogicalPosition` 实例，`setSize(innerSize())` 是**不动点**（单测 + 真页面断言都有） |
| **F7** 错误模型 | ✅ 完成（Windows 已验证） | 回执带可选 `code` / `data` → 页面 `Error` 上可读；8 个库级错误码（`OrielIpcErrorCodes`）；应用可自带 `OrielIpcException.Code` / `.Details`；**逐次调用超时**（`invoke(cmd, args, { timeout })`，宿主显式声明 `supportsPerCallTimeout`）；回执改为整对象传递（`_onResult(result)`），消除三平台的抽取漂移。新增 13 项单测 + 桥接 24 项 + 真页面 5 项 |
| **F1** `path` 模块 | ✅ 完成（Windows 已验证） | 内建命令族 `path.*`（7 条）+ `@orielweb/api/path`；`sep` / `delimiter` 由宿主注入（**同步**）；**整族始终放行**（不做 I/O 是那条放行的前提）。两处对接 Rust 口径（`extname` 不带点、隐藏文件无扩展名、`basename`/`dirname` 忽略尾随分隔符）；`normalize` **不做 I/O**（与 Tauri 的 `canonicalize` 有意分歧）。新增 37 项单测 + 前端 11 项 + 真页面 14 项 |
| **F4a** `app` 元信息 | ✅ 完成（Windows 已验证） | `OrielAppInfo` + `WithAppInfo(name, version, identifier)`（三项独立从入口程序集退化）；标识符在 `Build()` 时校验字符集（它是数据目录的组成部分）；`OrielLibrary.Version` 成为库版本唯一来源；内建 `app.*` 五条只读 + `@orielweb/api/app`，**整组始终放行**（放行的是列举的那几条，不是前缀——有测试钉住）。刻意不含 `exit`/`show`/`hide`（是能力）与 `getBundleType`（本库不是打包器，给不出）。新增 47 项单测 + 前端 6 项 + 真页面 6 项 |
| **F4b-1** 目录解析 | ✅ 完成（Windows 已验证） | 24 条 `path.*Dir` 命令 + JS 24 个函数，**逐格对齐 Tauri 的 `PathResolver`**（Windows `SHGetKnownFolderPath` / Linux XDG / macOS `~/Library/*`）。实测 .NET 的 `SpecialFolder` 有三格对不上，故 Windows 侧直接用已知文件夹 API；Tauri 标记"不支持"的三格**照 Tauri 拒绝**（新增 `not_supported` 码）。`resolveResource` 带越界校验。放行名单由目录清单派生（不再手抄）。新增 43 项单测 + 前端 4 项 + 真页面 10 项 |
| **F4b-2a** 路径 scope 引擎 | ✅ 完成（Windows 已验证） | glob（`glob` crate 语义：`**` 吃零段、`*` 不跨分隔符）+ 23 个 `$VAR` + allow/deny。**空 scope 拒绝一切**（不是不限制）、**不认识的变量保守拒绝**、**deny 跨条目优先**。新增 56 项单测 |
| **F4b-2b** `fs.*` 命令 + JS 模块 | ✅ 完成（Windows 已验证） | 14 条命令 + `@orielweb/api/fs`，**两道关都必过**（命令授权 + 路径 scope）。新增 `path_not_allowed` 码；归一在 scope 判定之前；`rename`/`copyFile` 两个路径都查。不含句柄式 API（无 rid 表）。新增 36 项单测 + 前端 16 项 + 真页面 22 项 |
| **F2** `convertFileSrc` + asset 协议 | ✅ 完成（**仅 Windows**；Linux/macOS fail-closed） | 专用 host `file.oriel`（与页面**不同源**，所以磁盘上的 HTML 拿不到桥接）+ 独立 scope `asset.read` + 服务端"先归一后判 scope"。整条路径编成**单段**（URI 的 path 总以 `/` 开头，逐段编码会丢掉绝对路径的前导斜杠）。新增 21 项单测 + 真页面 6 项（含"页面真能加载出来"，验 `%2F` 没被引擎归一） |
| **F5** 已有能力的 JS 入口 | ✅ 完成（Windows 已验证） | `opener`（3）/ `notification`（3，桌面子集）/ `autostart`（3）+ 内建命令族。**没有新平台代码**——宿主能力早就在 `OrielApp` 上。三族都不整族放行；`openWith` **明确拒绝**（任意程序执行，无授权模型）；`sendNotification` **异步**（Tauri 桌面端用 `window.Notification`）。新增 38 项单测 + 前端 15 项 + 真页面 7 项 |
| **F6** `clipboard` | ✅ 完成（Windows 已验证；Linux/macOS 交叉编译通过、运行期未验） | `@orielweb/api/clipboard`（3 条）+ 内建命令族。**本批有新平台代码**：Windows Win32 剪贴板 API / Linux GTK `GtkClipboard` / macOS `NSPasteboard`。`readText` 的 `null` 与 `''` 区分开；写失败返回 false（全局独占资源被占是正常状态，Windows 侧重试 5×10ms）；`label` 明确拒绝；不做 `writeHtml`（CF_HTML 的偏移头写错会静默产生乱码）。新增 13 项单测 + 前端 9 项 + 真页面 5 项（真剪贴板往返） |
| **F3** `dialog` | ✅ 完成（Windows 已验证；Linux/macOS 交叉编译通过、运行期未验） | `@orielweb/api/dialog`（5 条）+ 内建命令族。open/save **复用既有原生层**（`win.pickFile` **冻结不动**，两者共用底层、命令契约独立）；message/ask/confirm 是**新平台代码**（`MessageBoxW` / `GtkMessageDialog` / `NSAlert`）。**命令层返回语义、页面层映射文案**——Windows 无法自定义按钮文字，返回文案会导致静默错答。新增 29 项单测 + 前端 19 项 + 真页面 4 项 |
| **H** 迁移对照 | ✅ 完成 | `tauri-alignment.test.mjs`：把 `@tauri-apps/api` 与官方插件当**参照实现**机械比对导出面与方法，三个方向都管（缺口/扩展/清单过期），每个差异都必须有记录。参照实现是 devDependency，CI 用 `npm ci` 强制检查。**抓到并修了两个真实缺陷**（window 没转出 dpi 四类、dpi 类缺 toPhysical/toLogical）；方法级比对产出 62 项 `window.Window` 缺口，按类别记为 **M5 的工作清单** |
| **M5-1** 窗口只读状态查询 | ✅ 完成（Windows 已验证） | `isMinimized` / `isFocused` / `isFullscreen` / `isAlwaysOnTop`（对标 Tauri 同名方法）。四条都进标题栏白名单。新增 13 项单测 + 前端 2 项 + 真页面 12 项（含"页面查询与宿主侧逐条一致"与状态流转） |
| **M5-2** 外观与能力的只读查询 | ✅ 完成（Windows 已验证） | `isDecorated` / `isResizable` / `isMaximizable` / `isMinimizable` / `isClosable` / `theme`。四条读原生样式位、`isDecorated` 读选项。**三处跨平台语义差异如实记录**（`isMaximizable` 在 Linux/macOS 等于 `isResizable`、`isClosable` 在 Linux 恒真）。**只读查询全部做完**。新增 18 项单测 + 前端 2 项 + 真页面 9 项 |
| **M5-3** Window 事件转发 | ✅ 完成 | `Window.listen` / `once` / `emit` / `emitTo`（纯委托到 `event` 模块）+ `unminimize`（映射到 `window.restore`，三平台同一个原生动作） |
| **M5-4** `isEnabled` / `setEnabled` | ✅ 完成（Windows 已验证） | 三平台：`IsWindowEnabled`/`EnableWindow`、`gtk_widget_get_sensitive`/`set_sensitive`、Cocoa `ignoresMouseEvents`。**macOS 只挡鼠标不挡键盘**，如实记录。`isEnabled` 进白名单、`setEnabled` 不进 |
| **M5-5** 剩余 36 项的**归类** | ✅ 完成（归类，不是实现） | H 的清单现在**每一项都有归属**：已实现 / 有意不做（2 项移动端专有）/ 待做（36 项，按批次优先级分 9 组、每组写明为什么还没做）。见 `packages/api/tests/tauri-alignment.test.mjs` 的 `METHOD_GAP_GROUPS` |
| **M5-6** 待做清单的实现 | 🔄 进行中（9 项已实现；方针下待做 **1** 项） | **已完成**：`requestUserAttention` + `UserAttentionType`；`setMaxSize`；`setSizeConstraints`；`setMaximizable` / `setMinimizable`；`setClosable`（Windows 灰化系统菜单 `SC_CLOSE`，顺带把 `IsClosable` 从"有 `WS_SYSMENU`"修正为"存在且未灰化"）；`onThemeChanged`（宿主三平台早就检测并广播 `theme.changed`，缺的只是页面侧包装）；`setIcon`（**路径形式**；三平台复用创建期图标代码——Windows 顺手填掉了"`LoadImageW` 只认 ICO/BMP，喂 PNG 静默返回 0"这个久已记录的缺口，PNG 现在经 `OrielImage` + 托盘那套 `Win32IconFactory.CreateFromRgba` 造 HICON）；`setBackgroundColor`（三平台一致的 webview 底色；**复用**创建期的 `ApplyBackgroundColor`，各加一条"运行期优先、否则回落创建期选项"的分支，ARGB 拆解集中到 `OrielWindowOptions.UnpackArgb`）。<br>**方针（用户 2026-10-18 定）：跨平台不一致的不做。** 据此把剩余待做项分成 A/B 标记（见 `tauri-alignment.test.mjs` 的 `METHOD_GAP_GROUPS`，有测试钉住）：**A·跨平台不一致 = 不做**（外观开关 4 项、每平台一套 6 项、特效 2 项、光标 6 项、任务栏徽标 4 项、`setTheme` 共 **23** 项）；**B·跨平台一致·待做 = 1 项**（`onScaleChanged`）。另有 `onDragDropEvent` 记入 `METHOD_DIVERGENCES`（**有意不做**）。 |

### 背景色：一次错误结论的更正（2026-10-18）

**更正**：本节原先写着"`WithBackgroundColor` 是一个设置了完全没有效果的公开 API、`TryGetBackgroundArgb` 全仓零调用者"。**这个结论是错的。**

真实情况是：创建期背景色**早就完整实现了**，三平台各有落点：

| 平台 | 实现 | 位置 |
| --- | --- | --- |
| Windows | `WebView2Utilities.GetInterface<ICoreWebView2Controller2>` + `put_DefaultBackgroundColor(...).ThrowOnError()` | `Win32WindowHost.cs` 的 `ApplyBackgroundColor`（由 `InitializeWebView2` 之后调用） |
| macOS | `NSWindow.backgroundColor` + `WKWebView.underPageBackgroundColor`（`respondsToSelector:` 探测 12+） | `MacOSWindowHost.cs` 的创建流程 |
| Linux | `webkit_web_view_set_background_color` | `LinuxWindowHost.cs` 的 `ApplyBackgroundColor` |

**我为什么会判错**：用 `Select-String -Path src\OrielWeb\**\*.cs` 搜索时，PowerShell 的通配符 `**` **不会递归子目录**——它没扫到任何 `Platform\Windows\**`、`Platform\Linux\**`、`Platform\MacOS\**` 下的文件，于是我看到的"零调用者"只是"搜索范围为零"。**同一原因可能还影响过本项目其他结论**，所以：本仓搜代码一律用 `grep`/`rg` 工具，不要用 PowerShell 的 `**` 通配符。

**这一轮的真正增量**是运行期 `setBackgroundColor`：三平台各加一个"运行期值优先、否则回落创建期选项"的分支，**复用**已有的 `ApplyBackgroundColor`，并把 ARGB 拆解集中到 `OrielWindowOptions.UnpackArgb`（拆法仍然只有一处）。原先被误判为"缺失"的创建期功能一行都没改。

它**没有读取路径可闭环验证**（Tauri 也没有 `backgroundColor` 的读 API，加一个就是"偷偷发明 API"，会被 `EXTRAS` 检查拦住），所以自检只能验"命令跑得通 + 颜色解析正确"，**证明不了颜色真的显示出来了**——这一点不变。


### M3 的状态（2026-10-17）

M3 的两项（E2 几何查询、E3 dpi 多态）都已完成，**M3 的退出条件成立**：

| M3 退出条件 | 状态 |
|---|---|
| 三平台先一致（装饰窗口下客户区逐像素相同） | ✅ E1（Windows 实测 `784x561` → `800x600`；另两平台本就是客户区语义） |
| `scaleFactor` 三平台可用 | ✅ D1 顺带完成（Windows `GetDpiForWindow/96`、Linux `gtk_widget_get_scale_factor`、macOS `backingScaleFactor`） |
| inner/outer 拆分 | ✅ E2 |
| JS 多态 | ✅ E3 |
| `--selftest geometry` 三档缩放逐像素 | ⚠️ **100% 缩放已验**；125%/150%/200% 未验（本机只有一台 100% 显示器） |

唯一没打勾的是高 DPI 那档，属**环境限制**而非能力缺口，已记在 CHANGELOG 的"已知限制"与 ROADMAP 的待真机清单里。

### M2 已收口（2026-10-17）

| M2 退出条件 | 状态 |
|---|---|
| `event` 模块四函数 + `{event,id,payload}` | ✅ C3 |
| 内置事件六种在三平台都能触发 | ⚠️ 前五个可用（`resize`/`move`/`focus`/`blur`/`close-requested`，转发按需打开）；**`destroyed` 标注为永远收不到**——窗口销毁时页面正在被拆掉（Tauri 的 JS 侧同样如此） |
| 页面 `onCloseRequested` 能真正阻止关闭 | ✅ D2。语义与 Tauri **相反**：页面同意才 `destroy()` |
| `window` 模块基础 + 现有能力包装 | ✅ D1/D2（26 条命令 + `Window` 类） |
| 新命令族过门禁 | ✅ C4（`event.emit`）+ D1（`window.*` 白名单） |
| `--selftest event` + `--selftest window` | ✅ 7 + 12 项断言，Windows 全 PASS |
| demo 迁移到 `@orielweb/api` | ✅ 批次 G |

唯一没打勾的是**批次 G（demo 迁移）**——那是验收性的一步，不是 M2 的能力缺口。

> 更新（2026-10-17）：批次 G 也已完成，M2 的全部退出条件成立。

### C1/C2 的一处实现教训（写计划时未知）

**GDK 结构体的字段偏移不能手算**。第一版 `OnConfigureEvent` 用 `Marshal.ReadInt32(event, offset)` 手算，
偏移取了 24——而 `GdkEventConfigure` 的 `x` 在 64 位下是 **20**（`type` 4 字节 + 4 padding +
指针 8 字节 + `send_event` 1 字节 + 3 padding）。差 4 字节会让 `x` 读到 `y`、`width` 读到
`height` 之后的数据，而**编译完全通过**。

现在改成 `Marshal.PtrToStructure<GdkEventConfigure>`，并加了 `GdkStructLayoutTests` 钉住
字段顺序、连续性与 `send_event` 必须是 `byte` 而不是 `bool`（.NET 的 `bool` 默认 marshal 成
4 字节 Win32 BOOL，会让后续字段整体偏移 3 字节）。

### C3/C4 的一处实现教训（写计划时未知）

**`EvaluateJs` 不会替调用方 await Promise**。自检里读 `window.oriel.invoke(...)` 的返回值，
`ExecuteScriptAsync` 得到的是 Promise 的序列化形态 `{}`——症状是"命令返回了空对象"，
而看起来像是宿主侧序列化坏了。正确做法是让页面 `.then()` 后经 `postMessage` 回显。
这条与 E1 的"自检必须挂 UI 线程"同属"自检写法的坑"，会反复出现在后续所有基于页面的取证里。

另一条：**探针页面的 ready 必须早于 `Loaded` 订阅**。页面在解析时就发 `postMessage`，
而 `Loaded` 在导航完成后才触发——订阅晚于消息会让 ready 永远收不到。所以监听要挂在
`onCreated`（早于导航），而需要 `App` 的逻辑仍放 `Loaded`。

### E1 的验收状态（如实标注）

| 项 | 状态 |
|---|---|
| Windows framed 建窗尺寸 | ✅ 真机验证：`WithSize(800,600)` → 客户区 `800x600` |
| Windows framed 运行期 `Resize` | ✅ 真机验证：`Resize(640,480)` → 客户区 `640x480` |
| Windows frameless 建窗 + `Resize` | ✅ 真机验证：两种形态都精确 `800x600` / `640x480` |
| **Windows 高 DPI（125%/150%/200%）** | ⚠️ **未验证**——本机只有一台 100% 缩放显示器。`AdjustWindowRectExForDpi` 按窗口 DPI 取值，逻辑上正确，但没有实测取证 |
| **Linux / macOS** | ⚠️ **未改代码**——这两个平台的 `Resize` 本来就是客户区语义，无需改动。但 `--selftest geometry` 在它们上面**尚未跑过**，需 CI 取证 |

> 高 DPI 那条要在有缩放显示器的机器上跑 `--selftest geometry`；若客户区偏离 800x600 超过 ±2，
> 先查 `ClientToWindowSize` 传的 DPI 是不是**窗口所在显示器**的（不是系统 DPI）。

### 已核实的两处新增结论（写计划时未知）

1. **舍入规则会漂移**：宿主侧 `.NET` 的 `Math.Round` 默认 `MidpointRounding.ToEven`
   （`Math.Round(4.5)` = 4），而 JS 的 `Math.round` 是向 +∞（= 5）。E3 的 DPI 换算如果不复刻
   ToEven，同一份代码在 150% 屏上会比宿主算的大 1 像素——只在恰好 `.5` 的输入上出现，
   人眼看不出、随机测试也撞不到。已在 `packages/api/src/dpi.js` 的 `roundToEven` 里复刻并逐例钉住。
2. **`EvaluateJs` 有线程约束**：自检/测试里从线程池调它会得到 `0x802A000C`
   （"只能从创建它的线程调用"）。写 E1 骨架时就踩了一次——它看起来像"几何断言失败"，
   实际是调用方式错了。后续所有基于 `EvaluateJs` 的自检都要挂在 UI 线程上（`Loaded` 里 `await`，
   不要 `Task.Run`）。

### 立刻可做的三件事（原始建议，已全部完成）

1. ~~批次 A：建 `packages/api/`~~ ✅
2. ~~批次 B：`OrielWindowOptions.Label`~~ ✅
3. ~~E1 的测试先行~~ ✅（红灯已亮）

