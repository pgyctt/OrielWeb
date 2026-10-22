# API 差异逐项决策与新批次计划（2026-10-20）

> 这份文档记录 2026-10-20 逐项决策的**结果**与据此重排的批次计划（B12 起）。
> 差异清单见 [TAURI2-GAP-2026-10-20.md](TAURI2-GAP-2026-10-20.md)；此前的批次 B0–B11 见
> [执行计划](TAURI2-ALIGNMENT-EXECUTION-PLAN-2026-10-18.md)（B0–B11 全部完成，0.4.0 已发版）。

## 0. 决策基调（一句话）

**全量对齐**：能与 Tauri 同名同形的都做；平台不支持的抛 `not_supported` 而不是不提供；
连结构性分歧也补壳、线格式也改成 Tauri 2 形状。**只有两组不做**：core 的插件运行时 7 个、
notification 的移动端 8 个。

这意味着对齐账本的 GAPS 在 B12–B23 结束后应当**接近清零**（只剩这两组，理由保留原登记）。

## 1. 决策总表（逐项）

图例：✅ 做 · ⚙️ 做（补壳/兼容） · ❌ 不做（保留 GAPS 理由） · ⏸ 暂缓

### 1.1 模块级

| 项 | 决策 | 备注 |
|---|---|---|
| `@orielweb/api/tray`（TrayIcon 类） | ✅ 完整类 | new/getById/removeById 静态 + 8 个实例方法，形状与 Tauri 一致 |
| `@orielweb/api/webview` | ⏸ 暂缓 | 等真实「窗口内多 webview」需求；但 webviewWindow 的 webview 级方法补壳（见 1.6） |

### 1.2 core（7 个，唯一整组不做）

| 项 | 决策 | 理由（保留） |
|---|---|---|
| Resource / PluginListener / addPluginListener / transformCallback / SERIALIZE_TO_IPC_FN | ❌ | 插件运行时与资源句柄表的配套 API，本库无此机制；造出来是假对象 |
| checkPermissions / requestPermissions | ❌ | 本库能力是构建期 fail-closed，无运行时权限查询；被拒走错误码 |

### 1.3 类型与形状补齐

| 项 | 决策 | 备注 |
|---|---|---|
| `dpi.Size` / `dpi.Position` 类型别名 | ✅ | 纯 d.ts |
| `path`/`fs` 的 `BaseDirectory` | ✅ | 枚举对象 + 与 fs 的 `baseDir` 选项串接（B1 已有 baseDir 静默失败修复，两者要对齐语义） |
| `window.CloseRequestedEvent` | ✅ | 纯类型 |

### 1.4 window：特效 / 任务栏 / 光标 / 平台开关（全做）

| 组 | 项 | 决策 | 平台落点 |
|---|---|---|---|
| 特效 | Effect、EffectState、setEffects、clearEffects | ✅ 照 Tauri 签名 | Win Mica/Acrylic、macOS vibrancy；Linux `not_supported`；不支持的枚举值**报错不静默** |
| 任务栏 | ProgressBarStatus、setProgressBar、setOverlayIcon、setBadgeCount、setBadgeLabel | ✅ 全做 | Win ITaskbarList3、macOS NSDockTile；Linux `not_supported` |
| 光标 | setCursorIcon、setCursorVisible、setCursorGrab、setCursorPosition、setIgnoreCursorEvents、startResizeDragging | ✅ 六项全做 | setIgnoreCursorEvents 在 Linux 走 X11 shape / Wayland input region |
| 平台开关 | setSkipTaskbar、setVisibleOnAllWorkspaces、setContentProtected、setSimpleFullscreen、setFullscreenOnMonitor、setTitleBarStyle、setShadow、setAlwaysOnBottom、setFocusable、setDecorations | ✅ 全部逐个做 | 不支持的平台抛 `not_supported`（setShadow 在 Linux 无对应概念） |
| 主题 | window.setTheme、app.setTheme | ✅ 两级都做 | Win DWM + ImmersiveColorSet、macOS NSAppearance、Linux GtkSettings |
| B 组 | setZoom、clearAllBrowsingData | ✅ | 三平台语义一致，落在 WebviewWindow |

### 1.5 fs / clipboard

| 项 | 决策 | 备注 |
|---|---|---|
| open / create / FileHandle / SeekMode / readTextFileLines | ✅ 全套句柄式 | 新建宿主侧句柄表（与 Channel 同一套 id 分发） |
| watch / watchImmediate | ✅ | Win ReadDirectoryChangesW / Linux inotify / macOS FSEvents |
| startAccessingSecurityScopedResource / stopAccessing… | ✅（macOS 沙箱生效） | 其他平台 `not_supported`；仅沙箱应用有意义，JSDoc 写明 |
| readImage / writeImage / writeHtml / writeSecret | ✅ 四个全做 | writeHtml 的 CF_HTML 偏移头必须带验证；writeSecret 非 macOS `not_supported` |

### 1.6 notification / app / 结构性分歧

| 组 | 项 | 决策 | 备注 |
|---|---|---|---|
| 通知句柄 | active、pending、cancel、cancelAll、removeActive、removeAllActive | ✅ 六个全做 | 宿主侧通知句柄表（Win toast id / macOS 通知中心 / libnotify id） |
| 通知回传 | onAction、onNotificationReceived | ✅ 两个都做 | 重做点击回传要先定「拉起时应用不在前台」的场景 |
| 定时 | Schedule、ScheduleEvery | ✅ | **Linux 应用退出后不响**（无系统级调度），文档明示 |
| 移动端 | Importance、Visibility、channels×5、registerActionTypes | ❌ 8 项不做 | Android/iOS 通知模型，桌面无对应概念 |
| app 桌面 | setDockVisibility、defaultWindowIcon | ✅ | defaultWindowIcon 的来源定为「程序集旁 app.png → OrielImage」 |
| app 杂项 | BundleType、getBundleType、fetchDataStoreIdentifiers、removeDataStore、onBackButtonPress | ✅ 五个全做 | BundleType 从 Velopack 产物推断；数据存储接 WKWebsiteDataStore（macOS）；onBackButtonPress 永不触发 |
| 结构性补壳 | Image.close、Menu.rid、Channel.cleanupCallback、webviewWindow 的 position/size/setAutoResize/reparent | ⚙️ 补壳 | 见 §3 护栏：no-op 必须在 debug 模式打警告日志 |
| 线格式 | `__TAURI_TO_IPC_KEY__` / `toJSON` / dpi 类序列化 | ✅ 改成 Tauri 2 形状 `{ Logical: { x, y } }` | **破坏性变更**，见 §4 |

## 2. 新批次计划（B12–B24）

排序原则：先打地基（类型、线格式、补壳），再按「迁移撞击频率 × 三平台成本」铺能力。

| 批次 | 内容 | 改动面 | 依赖 | 难度 |
|---|---|---|---|---|
| **B12** | `@orielweb/api/tray`：TrayIcon 类（静态 3 + 实例 8）+ d.ts + 单测 | 新 `tray.js`/`tray.d.ts`，复用 `oriel.tray` 命令 | 无 | 低 |
| **B13** | 类型与形状：Size/Position、BaseDirectory（+fs `baseDir` 串接）、CloseRequestedEvent、Effect/EffectState、ProgressBarStatus | `dpi.d.ts`/`path.d.ts`/`window.d.ts`/`fs.js` | 无 | 低 |
| **B14** | **线格式改造**：dpi 类改 Tauri 2 外部标记形状 + 宿主侧双格式兼容读取 + 结构性补壳（Image.close/Menu.rid/Channel.cleanupCallback/webviewWindow 四方法）+ 账本 METHOD_DIVERGENCES 清理 | `dpi.js`、C# 参数绑定、三平台后端、`image.js`/`menu.js`/`core.js`/`webviewWindow.js` | B13 | **高** |
| **B15** | 主题：window.setTheme + app.setTheme（三平台） | 三平台窗口宿主 + `window.js`/`app.js` | 无 | 中 |
| **B16** | webview 级：setZoom、clearAllBrowsingData | 三平台 webview 后端 + `webviewWindow.js` | 无 | 中 |
| **B17** | fs 句柄式：句柄表 + open/create/FileHandle/SeekMode/readTextFileLines | C# 句柄表 + `fs.js` + 生成器 | 无 | 中高 |
| **B18** | notification：句柄 6 项 + 回传 2 项 + 定时 2 项 | 三平台通知后端 + `notification.js` | 无 | 中高 |
| **B19** | clipboard：readImage/writeImage/writeHtml/writeSecret | 三平台剪贴板 + `clipboard.js` + OrielImage 通路 | 无 | 中 |
| **B20** | fs watch/watchImmediate + security-scoped | 三平台监视器（inotify/ReadDirectoryChangesW/FSEvents） | B17 | 高 |
| **B21** | window 光标 6 项 | 三平台光标后端 | 无 | 中高 |
| **B22** | window 平台开关 10 项 + 特效 4 项 + 任务栏 5 项（**已完成**，见 §6） | 三平台窗口宿主（最重的一批） | B13 | **高** |
| **B23** | app 剩余：setDockVisibility、defaultWindowIcon、BundleType、getBundleType、数据存储 2 项、onBackButtonPress（**已完成**，见 §6） | `OrielApp` + macOS Dock + WKWebsiteDataStore | B13 | 中 |
| **B24** | 账本收口：GAPS 清零核对、PLANNED_MODULES 更新、batch 新鲜度断言、迁移演练复跑 | `tauri-alignment.test.mjs`、`tools/migration-drill/` | B12–B23 | 低 |

**规模合计**（按改动点）：新增文件约 6、修改文件约 45、新增测试约 150 条。
B14 与 B22 是两块硬骨头；B12/B13/B15/B16/B23 是「一个下午一批」的规模。

### 2.1 发版节奏

- B12/B13/B15/B16/B19/B23 可各自独立发版（纯新增，不破坏）。
- **B14 单独发 0.5.0**（线格式破坏性变更，见 §4）。
  **2026-10-20 更正**：B14 实际随 **0.4.1** 一并发布（与 B12/B13/B15 同版），没有单独发 0.5.0；
  读兼容策略不变（0.6.0 删旧形状读取）。
- B24 结束后账本 GAPS 应只剩两组（core 7 + notification 移动端 8），发 0.6.0 对齐里程碑。

## 3. 三条实现护栏（与「全量对齐」同时成立）

1. **补壳必须可观测**。`Image.close()`/`setAutoResize()`/`reparent()` 这类无对应物的补壳，
   零删改优先，但 **debug 模式（`UseDebug()`）下必须打一条警告日志**（含调用名），
   否则「调用不报错但什么都没发生」会成为最难查的一类问题。这不破坏「零删改」承诺。
2. **平台不支持 ≠ 静默忽略**。所有 `not_supported` 都走统一错误形状（文案 + 可选错误码），
   尤其是 `setEffects` 的不支持枚举值——用户选的是「照签名全实现」，但实现层必须让
   「效果没生效」有信号，而不是吞掉。
3. **单平台能力写明生效平台**。`writeSecret`/`setDockVisibility`/`security-scoped`/`setSkipTaskbar`
   等在 JSDoc 与 API.md 标注生效平台与其他平台的行为（抛 `not_supported`），不写「支持」二字了事。

## 4. 线格式改造的专门说明（B14）

现状与目标：

| | 形状 | 示例 |
|---|---|---|
| 现状（Tauri 1 形状） | 内联 tag | `{ type: "Logical", x: 1, y: 2 }` |
| 目标（Tauri 2 形状） | 外部标记 | `{ Logical: { x: 1, y: 2 } }` |

- **读兼容、写新**：宿主侧参数绑定同时接受两种形状（旧形状保留一个版本），
  页面侧 `dpi` 类的 `toJSON()`/`__TAURI_TO_IPC_KEY__` 输出新形状。
- 影响面：`packages/api/src/dpi.js`、C# 侧的 Size/Position 反序列化、
  三平台几何命令的参数绑定、全部相关单测、`METHOD_DIVERGENCES` 里那两条登记（改完即删除）。
- **版本策略**：0.5.0 标注 BREAKING（旧形状仍可读），0.6.0 删除旧形状读取。
  CHANGELOG 必须给出「迁移只需要升级包、不需要改页面代码」的说明——因为页面代码看到的是
  类实例，形状只在线格式上。

## 5. 与「不假装支持」原则的冲突记录

本次决策推翻了两条此前的方针级判断，如实记录：

1. **A 组「跨平台不一致的不做」被推翻**——改为「做，不支持的平台抛 `not_supported`」。
   代价是三套平台实现 + 统一错误面；收益是迁移代码零删改。账本 `METHOD_GAP_GROUPS` 的
   【A·跨平台不一致】分类保留，但含义从「不做」改为「做但平台受限」。
2. **「无资源可释放就不给 close」被推翻**——改为补壳 + debug 警告。
   代价是「关闭即释放」的心智模型对本库不成立；收益是迁移零删改。B6a 时期
   「宁可当场炸，也不要错误的心智模型」的判断随决策更新，账本 `METHOD_DIVERGENCES.closeImage`
   改写为补壳登记。

两条都写进账本注释，避免下一个人按旧方针把补壳又删掉。

## 6. 执行记录

### B15（2026-10-20，已完成）：主题覆写 window.setTheme + app.setTheme

**三级模型**（写进 API 与账本）：窗口覆写 > 应用覆写（`app.setTheme`） > 系统主题。
`window.theme()` 按"这个窗口显示成什么"回答（不再是裸的系统值）。

**平台落点（如实记录，不是实现偷懒）**：
- **Windows**：`DwmSetWindowAttribute(DWMWA_USE_IMMERSIVE_DARK_MODE)` + UxTheme
  非客户区重绘。**只改标题栏 / 边框那圈**——客户区（网页）颜色由 CSS 决定，宿主不替你改。
  两个 DWM 属性名都试（20 与 19），旧系统不认新名时跳过。
- **macOS**：`NSWindow.appearance`（按窗口），`null` = 还原成系统/应用默认。
  作用粒度最细，"这个窗口"与"整个应用"是两层。
- **Linux**：**GTK 没有"按窗口选主题"的入口**——能改的只有应用级的
  `gtk-application-prefer-dark-theme`。所以 `window.setTheme` 与 `app.setTheme`
  落到同一个地方、按调用次序后者赢。**这是 GTK 的形状，不是本库的缺口**；
  与 `OrielApp.SetTheme` 的分工是"那边记哪些窗口有自己覆写，这边写进 GTK"。

**门禁姿态**：两条命令都不在 always-allowed 名单里（改外观，不是读元信息），
要显式 `Allow("window.setTheme")` / `Allow("app.setTheme")`。这条由
`AppInfoTests.StatefulAppCommandsAreNotAlwaysAllowed` 与
`BuiltInWindowApiCommandTests.OtherWindowCommands_AreNotAlwaysAllowed` 盯着。

**账本更新**：`app.setTheme` 从 GAPS 移除、`window.Window#setTheme` 从
`METHOD_GAP_GROUPS` 移除——两条都已落地。

### B22（2026-10-21，已完成）：平台开关 10 + 特效 4 + 任务栏 5

这一批把 `window.Window` 的最后 19 项补齐，`METHOD_GAP_GROUPS` 因此**清零**。
决策层面它兑现的是 §5 第 1 条：A 组从"不做"改成"做，不支持的平台抛 `not_supported`"。

**三平台各自的"最重的地方"**：
- **Windows 补齐了三套新设施**：手写的 `ITaskbarList3` 调用（`CoCreateInstance` + vtable
  槽位，本库第一个"只调用、不实现"的 COM 接口）、DWM 的系统背板 + 非客户区渲染策略、
  以及未文档化的 `SetWindowCompositionAttribute`（Win10 上的 blur/acrylic 唯一入口，
  与 Tauri/tao 同一条路）。
- **macOS 补的是"运行时装配"**：`NSVisualEffectView` 要插到 webview **之下**、
  窗口与 webview 的背景要放开（`drawsBackground` 走 KVC，且**调之前必须用
  `class_getInstanceMethod` / `class_getInstanceVariable` 确认那个键存在**——
  KVC 的 undefined key 抛的是 ObjC 异常，托管侧接不住，一次 setEffects 就能带走进程）。
- **Linux 补的是"诚实"**：十条开关里六条是 WM 提示（`skip_taskbar` / `stick` /
  `keep_below` / `accept_focus` 各一层），特效与任务栏徽标整组不存在——一律
  `not_supported`，理由写在方法上（GTK 无阴影 API / 需 libunity / 无桌面无关材质）。

**结构性改动**：`setDecorations` 要能运行期切，所以 Windows 宿主的
`WM_NCCALCSIZE` / `WM_NCHITTEST` 从"判 `_options.Frameless`"改成"判 `_decorations`"；
`IsDecorated` 三平台都改成反映运行期状态（之前恒为创建选项，文档同步更新）。

**"不假装支持"的三处具体体现**：
1. `liquidGlassRegular/Clear` 要 macOS 26 的 `NSGlassEffectView` → 抛错，不降级成别的材质。
2. `EffectState` 在 Windows 上没有落点 → 接受但**记诊断**（不抛错：Tauri 的迁移代码常写
   `state: Active` 却跑在 Windows 上，抛错会让"本该能跑"的应用挂掉）。
3. Linux 的 `setProgressBar` / `setBadgeCount` 需要 `libunity` → 抛 `not_supported`，
   不为一个只在部分桌面环境有效的能力引运行时依赖。

**取证**：演示自检第 12 段按平台分两路（能做的验成功、不支持的验 `not_supported`），
改动全部还原；C# 单测补 64 条（注册/门禁/参数校验/名字表），JS 单测补 9 条
（命令名与形状、参数校验）。

### B23（2026-10-22，已完成）：app 剩余五项

`@orielweb/api/app` 的导出面与 Tauri 2 **一条不缺**，对齐账本 `GAPS.app` 清零。
这一批的难点不在代码量，而在**每一格都要说清"凭什么知道"**：

1. **`getBundleType()` 的答案是推断出来的，不是读出来的。** Tauri 在编译期把
   `TAURI_BUNDLE_TYPE` 塞进二进制；本库没有打包阶段，所以只能观察运行期事实
   （`.app` 里的路径、Velopack 的 `Update.exe`、`APPIMAGE` 环境变量）。
   看不出来就返回 `null`——**不按平台猜**。`msi` / `deb` / `rpm` 在运行期无法区分，
   要精确回答得用程序集元数据 `OrielBundleType` 显式声明。这是"不编值"原则
   在这一批里最直接的一次应用。
2. **数据存储标识符没有走 WebKit 的全局枚举。** `+[WKWebsiteDataStore
   fetchAllDataStoreIdentifiers:]` 是异步回调接口（要传 ObjC block），而本库的 IPC 是
   同步请求-回执——在 UI 线程上等主队列的回调就是死锁。于是改成宿主侧登记表
   （`AppData/data-stores.json`，跨重启可见），并把"按 UUID 隔离"真正接到
   `OrielWindowOptions.DataStoreIdentifier` 上（`+dataStoreForIdentifier:`，macOS 12+，
   调之前用 `class_getClassMethod` 探测）。
3. **`onBackButtonPress` 是"真订阅 + 永不触发"**，不是空函数：桌面端本来就没有返回键，
   Tauri 在桌面端也只是 `listen('back-button', …)`。返回句柄做成 `{ unregister }`，
   结构上兼容它的 `PluginListener`。

**门禁姿态**：只读的三条（`getBundleType` / `defaultWindowIcon` /
`fetchDataStoreIdentifiers`）进 always-allowed；改应用呈现的 `setDockVisibility` 与删数据的
`removeDataStore` 要显式 `Allow`。这条线是 B23 里唯一容易在后续重构中被"合并简化"的东西，
所以 C# 侧单独钉了测试。

**取证**：演示自检第 13 段按平台分两路（macOS 验成功路径、Windows/Linux 验 `not_supported`）；
C# 单测补 47 条（推断规则、魔数判断、登记表落盘与损坏恢复、门禁与参数校验），
JS 单测补 7 条（命令名与形状、16 位元组、退订接口）。
