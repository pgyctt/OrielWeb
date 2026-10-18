# 更新日志

本文件记录 OrielWeb 各版本的用户可感知变化。格式参照 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)。

**发布纪律**（2026-10-05 评审 P2-18 后成文）：

- 版本号唯一来源是根 `Directory.Build.props` 的 `<Version>`；tag 形如 `vX.Y.Z` 与之一一对应，`release.yml` 会校验一致性。
- **改版本的提交与打 tag 是同一次提交**（0.1.8 之后已是这个形态）。历史上 0.1.3–0.1.7 有发布/排障提交但没有对应 tag（0.1.5 被整体跳过），这些版本在仓库里已不可回溯—— NuGet 上仍可查，但源码状态只能靠提交区间近似还原。下表 0.1.3–0.1.7 的条目即按提交区间还原的近似记录。

## 0.3.7 - 2026-10-18

修 0.3.6 的两个 macOS 缺陷，其中 `requestUserAttention` 是**一调用就崩**的回归，
所以 0.3.6 发布后数小时内即出补丁版。

### 修复

- **`requestUserAttention` 在 macOS 上崩溃（SIGABRT）**：`requestUserAttention:` 与
  `cancelUserAttentionRequest:` 都是 `NSApplication` 的**实例方法**，0.3.6 把前者发给了
  `NSWindow`、后者发给了 `NSApplication` 的**类对象**——两条路径都会因 unrecognized selector
  抛 `NSException`，而未捕获的 ObjC 异常会直接终止进程（退出码 134）。
  统一改用 `sharedApplication` 实例。
  **影响面**：macOS 上任何调用 `window.requestUserAttention` / `requestUserAttention()` 的应用，
  调用即崩；不调则无影响。
  **为什么没在发布前拦住**：`--selftest window` 正好走到这一步时崩，而 0.3.6 的 CI 在发布**之前**
  就已红（`smoke-macos`）——当时同批还有 Linux 的 `nav` 超时失败，发布闸只校验版本号、
  不校验 CI 结论，于是带病发布。这是流程问题，不只是代码问题（见下）。
- **api 自检的目录断言写错（不是实现缺陷）**：macOS 的 `configDir` 与 `dataDir` 同为
  `~/Library/Application Support`（与 Windows 的 Roaming 同构）。0.3.6 的断言把 macOS 与 Linux
  归成一类，导致 `--selftest api` 在 macOS 上必红。

### 已知限制

- `--selftest tray-js` 在 macOS 上仍是 SIGSEGV（**0.3.6 之前就存在**，不是本版引入）：
  C# 侧的 `tray` 自检全绿，只有"页面调 `tray.new`"这条路径崩，日志里没有栈。
  排查需要 macOS 真机 lldb，或在 `tray.new` 各步骤补 stderr 后跑一轮 CI。单独跟踪。

## 0.3.6 - 2026-10-18

Tauri 2 API 对齐的完整一轮（`docs/TAURI2-PLAN.md` 的批次 A → H 与 M5）。前端包 `@orielweb/api`
从 3 个模块扩到 14 个，宿主侧新增 `path.*` / `app.*` / `fs.*` / `dialog.*` / `clipboard.*` /
`opener` / `notification` / `autostart` / `window.*` 九个命令族，`window` 模块补齐到 Tauri 2 的
可对齐范围。详细差异分析与分阶段计划见 `docs/TAURI2-PLAN.md`；对齐审计与后续执行计划见
`docs/TAURI2-ALIGNMENT-AUDIT-2026-10-18.md` 与 `docs/TAURI2-ALIGNMENT-EXECUTION-PLAN-2026-10-18.md`。

**方针**（2026-10-18 定）：**跨平台不一致的不做**。据此把 `window.Window` 的剩余差异分成
A（跨平台不一致，25 项，不做）与 B（跨平台一致，0 项），并用测试钉住——哪天要在 A 组开工，
必须先改断言并说明方针变了。

### 新增

- **`@orielweb/api` 前端包**（`packages/api`）：模块名与 `@tauri-apps/api` 一一对应，
  迁移时只需替换 import 前缀。首批提供 `core`（`invoke` / `isTauri`）、`event`
  （`listen` / `once` / `emit` / `emitTo` / `TauriEvent`）、`dpi`
  （`LogicalSize` / `PhysicalSize` / `LogicalPosition` / `PhysicalPosition` + 归一与换算）、
  `mocks`（`mockIPC` / `mockWindows` / `mockEmit` / `mockConvertFileSrc` / `clearMocks`）。
  **JS + JSDoc，无打包器**：`exports` 直接指向 `src/*.js`，`tsc` 只做类型门禁（`--noEmit`）；
  `.d.ts` 由 `tsc -p tsconfig.types.json` 生成并**提交进仓库**，CI 校验无漂移。
- **`event` 模块的事件对象包装**：`listen` / `once` 的回调收到 `{ event, id, payload }`
  （Tauri 形状），而底层 `window.oriel.on` **仍是裸 payload**——包装在包内做，
  既有页面零影响。两套并存是刻意的：迁移不应该是"要么全改、要么不改"。
- **内建命令 `event.emit`**：页面把事件广播给所有窗口或定向到某个 label
  （对标 Tauri 的 `emit` / `emitTo`）。目标窗口不存在时**静默丢弃**（返回 0，不抛）——
  多窗口应用里窗口随时可能被关掉，"发的时候还在、到的时候没了"是正常时序。
  **它是唯一一处"库内建但不始终放行"的命令**：广播是真正的跨窗口能力，必须显式
  `Allow("event.emit")`；而自绘标题栏不依赖它，没有开后门的理由。
- **生成器诊断 `ORIELWEB107`**：应用定义 `event.emit` 与库内建重名时给出 Warning
  （注册成功但永不可达）。只按**精确名**判——`event.` 前缀不保留，`event.onUserAction` 之类完全合法。
- **`--selftest event`**：跨窗口事件投递取证。广播（发起窗口自己也收到）、定向（只有目标收到）、
  定向到不存在的窗口返回 0。
- **关闭接管（可阻止关闭）**：`OrielWindowOptions.InterceptClose` / `WithInterceptClose()`、
  `WebviewWindow.CloseInterceptEnabled`（运行期可改）、`WebviewWindow.Destroy()`，
  以及内建命令 `win.destroy` / `win.setCloseIntercept`。
  用户点关闭时推 `close-requested` 给页面，窗口**先不销毁**；页面同意就调 `Destroy()`，
  不同意就什么都不做。对标 Tauri 2 的 `onCloseRequested` + `preventDefault`。
  判定固定按**宿主否决 → 页面接管 → 放行**三层走（`OrielClosePipeline`，三平台同一份逻辑）：
  * **宿主的 `Closing` 优先**：置了 `Cancel` 就立刻生效、不再问页面。顺序颠倒的话，
    页面会收到一个它无权决定的通知，还会把宿主的否决误当成"用户在确认框点了取消"。
  * **页面加载完成前不挂起**：那条事件送不到未加载的页面，挂起只会让窗口变成点 X 没反应
    （用户唯一的出路是任务管理器）——与 `win.*` 后门要防的是同一类事故。
  * **`Destroy()` 不受任何拦截**：它是页面"确认后关闭"的出口。若它也能被拦下，那条路会被
    宿主否决逻辑无声地变成"关不掉"，而调用方以为用户已经确认过了。
  * 三平台的原生入口语义相反（GTK `delete-event` 返回 true = 阻止；Cocoa `windowShouldClose:`
    返回 true = 允许），翻译只发生在各自的入口处，判定逻辑共用一份。
- **`--selftest close`**：四条路径逐条真机取证（页面阻止 / 页面放行 / 宿主否决优先 / 未接管），
  外加"`Destroy()` 能强制关掉被接管的窗口"这条逃生口断言。
- **`window.*` 命令族**（对标 Tauri 2 的 `@tauri-apps/api/window`）：**可按 `label` 寻址**——
  页面能操作**别的窗口**，而 `win.*` 只能作用于发起者。含 `show` / `hide` / `close` / `destroy` /
  `focus` / `minimize` / `maximize` / `restore` / `toggleMaximize` / `toggleFullscreen` /
  `toggleOnTop` / `setTitle` / `setResizable` / `setFullscreen` / `setOnTop` / `setMinSize` /
  `moveTo` / `resize` / `center` / `drag*` / `pickFile` / `contextMenu` / `setCloseIntercept` /
  `setEventForwarding`，以及只读查询 `label` / `title` / `isVisible` / `isMaximized` /
  `scaleFactor` / `getAll`。
  **门禁姿态与 `win.*` 刻意不同**：`win.*` 整族始终放行，而 `window.*` 只为**标题栏必需的一组**
  （`minimize` / `toggleMaximize` / `toggleFullscreen` / `toggleOnTop` / `close` / `destroy` /
  `drag*` / `pickFile` / `contextMenu`）与三条只读查询（`isVisible` / `isMaximized` / `title`）例外，
  其余走 deny-by-default——这一族里有 `setIcon`、`setSkipTaskbar` 这类真正的能力，
  整族放行等于给一个比 `win.*` 大得多的面开后门。逐条理由见
  `OrielCapabilityRules.AlwaysAllowedWindowCommands`。
- **`@orielweb/api/window`**（对标 `@tauri-apps/api/window`）：`getCurrentWindow` /
  `getAllWindows` / `getByLabel` / `getFocusedWindow` + `Window` 类（含静态方法
  `Window.getFocusedWindow()`，Tauri 只提供后者）。
  方法名与 Tauri 一一对应，命令名映射在包内完成（`setAlwaysOnTop` → `window.setOnTop`、
  `unmaximize` → `window.restore`、`setFocus` → `window.focus`）。`setSize` / `setPosition` /
  `setMinSize` 接受 `LogicalSize` 与 `PhysicalSize`（后者按窗口所在显示器的缩放比换算）。
- **`Window.onCloseRequested`**：**与 Tauri 语义相反**——Tauri 在回调里 `preventDefault()` 阻止关闭；
  本库是"宿主挂起、页面**同意**才 `destroy()`"。订阅时自动打开关闭接管、退订时关掉。
  传进回调的 `event.preventDefault` 是**空操作**（保留它只是为了让迁移代码不抛 `TypeError`），
  文档与代码注释里都写明了。
- **事件转发开关**（`window.setEventForwarding` + `OrielWindowOptions.EventForwarding`）：
  尺寸 / 位置 / 焦点事件**默认不推给页面**——它们在拖动时每个像素来一次
  （已按 0.5 逻辑像素去重，但一次跨屏拖动仍有上千条），而推给页面在 macOS/Linux 上是一次 JS 求值。
  JS 包在页面 `listen` 到 `TauriEvent.WINDOW_RESIZED` 这类名字时**自动**按需打开；
  打不开（命令被门禁拒掉）会 `console.error` 说明原因，而不是静默地"订阅了收不到"。
- **`TauriEvent` 名字映射**：`tauri://resize` → 宿主 `resize` 等，映射在包内完成，
  页面看到的 `event.event` 仍是它订阅时用的 Tauri 名（`e.event === TauriEvent.WINDOW_RESIZED` 成立）。
  `tauri://destroyed` 标注为**永远收不到**：窗口销毁时页面正在被拆掉，事件送不出去
  （Tauri 的 JS 侧同样如此，那个事件是给 Rust 用的）。
- **`OrielWindowEventKind`**：`None` / `Resize` / `Move` / `Focus` 的标志位枚举。
- **`WebviewWindow.ScaleFactor` / `WebviewWindow.Title`**：前者取**窗口所在显示器**的缩放比
  （Windows `GetDpiForWindow/96`、Linux `gtk_widget_get_scale_factor`、macOS `backingScaleFactor`），
  后者是"标题现在是什么"的查询（与 `TitleChanged` **不同**：那个只在页面文档标题变化时触发）。
- **`--selftest window`**：按 label 寻址 + 能力门禁两侧的逐条真机取证。
- **demo 迁到前端包**（批次 G）：`samples/OrielDemo/wwwroot/app.js` 与 `manual-check.js`
  改用 `@orielweb/api`（`invoke` / `listen` / `isTauri`），页面脚本改为 `type="module"`。
  包源码**直接内嵌**（csproj 里 `packages/api/src/*.js` → `wwwroot/vendor/orielweb-api/`），
  **不复制、不提交副本**——从根上不存在"副本过期"这种漂移。
  demo 用**相对路径 import** 是因为它没有打包器；真实项目的两条路（打包器用裸标识符 /
  没有就配 import map）写在 `packages/api/README.md` 里。
  两处迁移要点：① 事件回调要取 `e.payload`（包把宿主事件包成 `{event,id,payload}`）；
  ② `await window.oriel.ready` → `isTauri()`（包与 Tauri 一样没有 `ready`，
  桥接脚本在 document 创建时就同步装好了）。
- **`--selftest api`**：18 项断言，逐模块验证包在**真实页面**里可用——ESM 动态 import、
  `core.invoke` 往返、`dpi` 舍入复刻 .NET 的 ToEven、`TauriEvent` 同名同值、
  `getCurrentWindow().label`、`getAllWindows` 含本窗口、`mocks.isMocked() === false` 等。
  这是**唯一一处"包在真页面里被 import 并调用"的取证**：包的单测全跑在 Node 里，
  而那一侧没有浏览器环境，ESM 加载、相对 import 解析、宿主真有那几条命令……
  一件都没验过。
- **窗口 `label`**（对标 Tauri 2 的 `Window.label`）：`OrielWindowOptions.Label`（默认 `"main"`，
  自动归一小写，字符集限字母/数字/`-`/`.`/`_`）、`OrielApp.GetWindow(label)`、
  `WebviewWindow.Label`，以及页面侧的 `window.oriel.label`。
  **同一应用内必须唯一**，重复时建窗抛 `InvalidOperationException`——不会自动改名，
  因为自动改名会让 `emitTo('main', …)` 发到错误窗口且毫无症状。
- **窗口几何与焦点事件**（对标 Tauri 2 的 `onResized` / `onMoved` / `onFocusChanged`）：
  `WebviewWindow.Resized`（`OrielSizeChangedEventArgs`，逻辑像素的客户区）、
  `WebviewWindow.Moved`（`OrielPositionChangedEventArgs`，左上角原点的屏幕逻辑坐标）、
  `WebviewWindow.FocusChanged`（`bool`）。
  三平台原生来源完全不同——Windows `WM_SIZE`/`WM_MOVE`/`WM_SETFOCUS`+`WM_KILLFOCUS`、
  Linux `configure-event`/`focus-in-event`+`focus-out-event`、
  macOS `windowDidResize:`/`windowDidMove:`/`windowDidBecomeKey:`+`windowDidResignKey:`。
  **三者都按"与上次上报值差 < 0.5 逻辑像素则跳过"去重**：原生侧在拖动时每个像素来一次回调，
  不去重会把一次拖动变成几百次托管事件 + 页面 IPC。
  注意 `WM_KILLFOCUS` 此前刻意不处理（把焦点塞回组合宿主会让页面 caret/IME 错乱）——
  现在只**上报**失焦，**不动焦点归属**，两者互不干涉。
- **`OrielApp.GetFocusedWindow()` / JS `getFocusedWindow()`**（对标 Tauri 2 的
  `Window.getFocusedWindow()`）：**已实现**——遍历存活窗口、取第一个 `IsFocused` 为真的。
  三平台都能回答"这个窗口有焦点吗"（Windows 比 `GetForegroundWindow`、Linux
  `gtk_window_is_active`、macOS `isKeyWindow`），而"哪个窗口有焦点"不需要单独的平台能力；
  Tauri 的 JS 侧也正是这么做的（`for (const w of await getAllWindows()) if (await w.isFocused())`）。
  新增宿主命令 `window.getFocused`（返回焦点窗口的 label 或 `null`，**不进标题栏白名单**——
  它泄露的是跨窗口信息）。JS 侧同时提供模块级 `getFocusedWindow()` 与静态方法
  `Window.getFocusedWindow()`（Tauri 只提供后者，前者是本库的便利入口）。
  返回 `null` 的两种情形：应用没有窗口；有窗口但都不在前台（用户切到了别的程序）。
  最小化的窗口不会被判为有焦点。
- **结构化错误**（F7）：失败回执带上可选的 `code` 与 `data`，页面 reject 出来的 `Error` 上多了
  这两个字段，于是能按错误类型分支（提示"没有权限"而不是"出错了"）——
  分不清"命令没授权"与"命令自己炸了"是排障时最费时间的一件事。
  两个字段**只在有值时才存在**（不写 `"code":null`），所以 `if (e.code)` 与 `'code' in e`
  给出同样的答案。C# 侧的码是 `OrielIpcErrorCodes`：`not_allowed` / `unknown_command` /
  `missing_argument` / `invalid_argument` / `invalid_message` / `target_not_found` /
  `no_source_window` / `internal`。
  **应用命令抛的异常不带码**——那是应用的错误分类，库不该替它编；
  要分类就自己 `new OrielIpcException("…") { Code = …, Details = … }`。
- **`OrielIpcException.Code` / `.Details`**：可选的错误码与附加数据（`init` 属性，
  不影响既有构造器）。`Details` 的类型是 `JsonElement` 而不是 `object`——
  回执要按 AOT 安全的方式写出，而 `JsonElement` 正是那个不需要应用注册 STJ 上下文的逃生口。
- **逐次调用超时**（`invoke(cmd, args, { timeout })`）：优先于全局 `oriel.timeout`，
  `<= 0` 表示这次不超时。宿主**显式声明**支持（传输层上的 `supportsPerCallTimeout: true`），
  不声明时明确拒绝而不是静默按全局值走——静默的后果是"我设了 5 分钟"变成"30 秒后失败"，
  而排查的人会去查命令实现。
- **`--selftest api` 扩到 28 项**：新增真页面里的结构化错误取证（被拒的命令带 `not_allowed`、
  名单外的名字也报 `not_allowed`、能力范围内的未知名字报 `unknown_command`）
  与 per-call timeout 在真宿主上可用。

> 一条值得记下的口径：**名单外的名字一律报 `not_allowed`，哪怕那个命令根本不存在**——
> 因为授权发生在路由查找之前。这顺带成了一个好性质：错误码不泄露"这个应用有哪些命令"。
> 自检里那条断言最初就是写错的（拿 `no.such.command` 去期望 `unknown_command`）。

- **几何查询**（E2/E3，对标 Tauri 的 `innerSize` / `outerSize` / `innerPosition` / `outerPosition`）：
  `WebviewWindow.InnerSize` / `OuterSize` / `InnerPosition` / `OuterPosition`，
  内建命令 `window.innerSize` 等四条，JS 侧 `Window.innerSize()` 等四个方法
  （返回 `LogicalSize` / `LogicalPosition` 实例）。
  **这四条刻意不在标题栏白名单里**：自绘标题栏不需要它们，而它们能按 label 读到**别的**窗口
  的几何，所以要用必须显式声明。
  **与 Tauri 的差别**：Tauri 返回 `PhysicalSize` / `PhysicalPosition`，本库返回逻辑像素
  （库对页面只暴露逻辑像素）；字段名一致、返回对象又能直接喂回 `setSize`/`setPosition`，
  所以迁移代码通常感觉不到。
  **平台事实**（已写进文档，不假装支持）：Linux/Wayland 下 `OuterSize` 可能与 `InnerSize` 相等
  （合成器不给装饰尺寸）、`InnerPosition` 恒等于 `OuterPosition`（GTK 不暴露装饰偏移）、
  `OuterPosition` 在 Wayland 下恒为 `(0,0)`（合成器不告诉客户端窗口在哪）。
- **`--selftest geometry` / `geometry-frameless` / `windowevents` / `event` / `close` / `window` / `api`**：
  本批新增的七项无人自检（尺寸语义、几何/焦点事件、事件投递、关闭接管、窗口 API、前端包）。
  其中 `geometry` 在本批扩展了"查询"那半：`InnerSize` 必须等于刚 `Resize` 进去的值、
  `Resize(InnerSize)` 是不动点、frameless 下内外尺寸相等、framed 下外框严格更大（Linux 除外，
  因为 Wayland 是平台事实）、`OuterPosition` 接近刚 `MoveTo` 的值、`InnerPosition` 在整窗之内。
  实测数值与 `WithSize` 那处修复**交叉吻合**：framed 下 `Outer - Inner = 16x39`、
  `InnerPos - OuterPos = 8,31`。

#### 路径运算（F1）

- **`@orielweb/api/path`**（对标 `@tauri-apps/api/path` 的路径运算部分）：`sep` / `delimiter`
  （**同步**，读宿主注入的常量）+ `join` / `normalize` / `dirname` / `extname` / `basename` /
  `isAbsolute` / `resolve`（异步，走宿主）。宿主侧是内建命令族 `path.*`。
- **为什么"纯字符串运算"也放宿主**：这是本批唯一反直觉的决定。Tauri 2 自己就是全部走 IPC 的
  （读官方源码才确认——我原以为可以在 JS 里做），而真正的理由不是照抄：Windows 的盘符、UNC、
  `\\?\` 前缀、大小写不敏感，POSIX 的重复/尾随斜杠…… 在 JS 里手写一份就是**又造一份平台语义**，
  两份实现必然在某个边角上不一致，症状是"只在某个平台错"。宿主有 .NET 的 `Path`。
- **`path.*` 整族始终放行**（见 `OrielCapabilityRules.AlwaysAllowedPathCommands`）：
  本族每个命令都只对**页面自己传进来的字符串**做运算，不做 I/O、不读宿主状态、不涉及别的窗口，
  所以放行它不会让页面多做到任何一件它本来做不到的事。Tauri 的路径命令也属
  `core:path:default`（默认开启）。若这里要求逐条声明，应用的实际反应会是 `Allow("path.*")`——
  和整族放行没区别，只多一层容易写错的配置。
  **这条放行的前提是不做 I/O**：需要 I/O 的路径能力（`fs` 读写、`convertFileSrc`）不在本族，
  照旧走 scope + 能力声明。
- **`normalize` 是词法的，不做文件系统访问**（与 Tauri 的**有意分歧**）：
  Tauri 用 `std::fs::canonicalize`，它**会解析符号链接**，而且**路径不存在时直接报错**——
  后者是很容易踩的坑："把用户输入的路径先归一一下"在路径还不存在时就会失败。
  本库用 `Path.GetFullPath`，纯词法，对不存在的路径也能正确工作。
  需要"真实路径"应当是 `fs` 模块的一个显式操作，而不是 `normalize` 偷偷做的事。

#### 应用元信息（F4a）

- **`OrielAppInfo`**（`OrielApp.Info`）+ `OrielAppBuilder.WithAppInfo(name, version, identifier)`：
  应用自身的名称 / 版本 / 标识符，对标 Tauri 的 `tauri.conf.json` 里那三个字段。
  三项**独立**退化（只给其中一项时其余仍从入口程序集推断）——"只想改个显示名"
  不必把版本号也抄一遍，而抄一遍的后果是它从此不再跟随程序集、静默过期。
  默认来源：名称取 `AssemblyProduct` → `AssemblyTitle` → 程序集名；版本取
  `AssemblyInformationalVersion`（**去掉 `+` 之后的构建元数据**，那是 git 提交号，
  参与版本比较会让同一个发布版本看起来"不相等"）→ 程序集版本；标识符取程序集名小写化。
- **标识符在 `Build()` 时校验**：只允许字母、数字、`-`、`_`、`.`，因为它是**应用数据目录的
  组成部分**。路径分隔符被明确拒绝（`/` 或 `\` 会让数据目录跳到别的地方去，
  而那是从配置里读出来的值）。`"."` / `".."` 这类"能过字符集但作为目录名指向别处"的输入
  单独挡住。校验放在启动而不是"第一次写配置"：后者故障点离原因很远。
- **`OrielLibrary.Version`**：库版本的**唯一来源**（程序集版本前三段）。
  桥接模板注入的 `oriel.version` 与 `app.getTauriVersion()` 共用它——
  两个来源会让"我该信哪个"变成一个没有答案的问题。
- **内建命令族 `app.*`**（5 条只读元信息）+ **`@orielweb/api/app`**：
  `getName` / `getVersion` / `getIdentifier` / `getTauriVersion` / `supportsMultipleWindows`。
  **整组始终放行**（见 `OrielCapabilityRules.AlwaysAllowedAppCommands`）：读的都是宿主
  本来就会告诉页面的东西。放行的是**列举出来的那几条**、不是 `app.*` 前缀——
  将来加 `exit` / `show` / `hide` 时它们不在名单里，会照旧走能力校验（有测试钉住这条）。
  **刻意不含**：`exit` / `show` / `hide` / `setTheme` / `setDockVisibility`（是能力不是元信息）、
  `getBundleType` / `BundleType`（返回 Tauri 打包器的产物类型，本库是库不是打包器，
  **给不出这个值**——编一个比不提供更糟，迁移代码会基于错信息分支）。

#### 目录解析（F4b-1）

- **`path.*Dir` 一族（24 条命令）+ JS 侧 24 个函数**（`appDataDir` / `homeDir` / `tempDir` / …），
  与 Tauri 的 `@tauri-apps/api/path` 函数名一一对应。**规则逐格对齐 Tauri 的
  `tauri::path::PathResolver`**（底层是 Rust 的 `dirs` crate：Windows 走
  `SHGetKnownFolderPath`、Linux 走 XDG、macOS 走 `~/Library/*`）。
- **为什么不用 .NET 的 `Environment.SpecialFolder`"差不多就行"**：实测**有三格不一样**。
  `downloadDir` 在 .NET 里没有可靠的成员；`publicDir` 的 `CommonDocuments` 给
  `C:\Users\Public\Documents`，而 Tauri 的 `FOLDERID_Public` 是 `C:\Users\Public`——
  **差一层目录**；`fontDir` / `runtimeDir` / `executableDir` 在 Windows 上 Tauri 明确不支持
  （调用报错），而 .NET 能给出 `fontDir`。所以 Windows 侧直接用 `SHGetKnownFolderPath`
  （`Downloads` / `Public` 两格必须如此），而**不支持的那三格照 Tauri 拒绝**
  （`not_supported`）：多给一个值会让"能跑"与"能迁"分叉，
  而分叉在迁移完成之后才发现是最糟的时机。
- **`not_supported` 错误码**（新增）：用于"这个平台确实没有这个概念"，
  与 `invalid_argument`（调用方拼错了）分开——前者是调用方该按平台分支的正常情况。
- **`resolveResource(path)`**：资源目录下拼相对路径，**带越界校验**。绝对路径被拒
  （那等于"读文件系统任意位置"，而函数名是 resolve**Resource**）；拼完跑出资源目录的也拒。
  判据是"拼完在不在根之内"而不是"字符串里有没有 `..`"——`assets/../logo.svg` 是合法且常见的。
- **`path.*` 放行名单改为由目录清单派生**，不再手抄第二份：手抄的失败模式是
  "加了目录忘了加放行"，症状为某个 `path.xxxDir` 在 Release 下被拒，而那看起来像
  "这个目录不存在"。有一条测试逐个断言"每个目录名都有可注册、可放行的命令"。
- **Linux 的 XDG 用户目录按用户配置读**：`~/.config/user-dirs.dirs` 里的
  `XDG_DESKTOP_DIR="$HOME/桌面"` 这类值（中文系统上很常见），含 `$HOME` 要展开。
  **不能假定 `~/Desktop`**。文件读不了或格式不认识时退回默认名、不抛异常
  （那是用户可编辑的文件，一份坏配置不该让"取桌面目录"整个失败）。
- **`runtimeDir` 在 `XDG_RUNTIME_DIR` 缺失时明确失败**，**不**退到 `/tmp`：
  `/tmp` 是全局共享的，不是"本用户的运行时目录"那个语义，给个错值比明确失败糟得多
  （调用方会把 socket 放进 `/tmp`）。

#### 文件访问（F4b-2）

- **内建命令族 `fs.*`（14 条）+ `@orielweb/api/fs`**：`readTextFile` / `writeTextFile` /
  `readFile` / `writeFile` / `exists` / `size` / `readDir` / `stat` / `lstat` /
  `mkdir` / `remove` / `rename` / `copyFile` / `truncate`。
- **本族是唯一"两道关都必过"的命令族**：命令授权（`Allow("fs.readTextFile")`）
  **加** 路径 scope（`Allow("fs.readTextFile", s => s.AllowPath("$APPDATA/**"))`）。
  命令授权解决"能不能做这件事"，scope 解决"能做在什么上"——少了后者，
  一条 XSS 就能把 `~/.ssh/id_rsa` 读走。所以 `fs.*` **不整族放行**，
  与"只做运算所以整族放行"的 `path.*` 形成对照。
- **两种失败分开报**：命令没授权 → `not_allowed`；授权了但路径不在 scope 内 →
  **`path_not_allowed`**（新增错误码）。混成一个码会让"我明明声明了 fs.readTextFile"
  把人带向完全错的方向。
- **`rename` / `copyFile` 的两个路径都要过 scope**：只查源不查目标的话，
  页面能把允许目录里的文件"搬"到 scope 之外（那是一次写）。
- **归一在 scope 判定之前**（实现里写明了顺序）：反过来的话
  `$APPDATA/../../etc/passwd` 会先被判成"在 $APPDATA 之内"（字面量确实在内），
  然后归一后指到别处——这就是经典的 scope 绕过。
  12 条带路径的命令各有一条专门的逃逸测试。
- **`writeTextFile` 不写 BOM**：见下面的缺陷条目。
- **`readDir` / `stat` 的字段名是 camelCase**（`isDirectory` 而不是 `IsDirectory`）：
  内建命令的返回值由库手写 JSON 保证，不依赖应用注册 STJ 上下文
  （应用凭什么知道库内部有个 `DirEntry` 类型？）。
- **不含句柄式 API**（`open` / `create` / `FileHandle` / `seek` / `read` / `write` /
  `readTextFileLines` / `watch`）：那些靠一张 `rid` 句柄表，而本库没有句柄表——
  加它是一整套新机制，不该混在"文件操作"里做。整文件读写覆盖了 webview 界面里绝大多数场景。
- **二进制走 JSON 数字数组**（`readFile` 给 `Uint8Array`、`writeFile` 收它）：
  本库的 IPC 是纯 JSON，没有 Tauri 的二进制 body 通道。一个字节要变成两三个字符，
  所以**几十 MB 级别的文件不适用**——已在文档写明。

#### 文件内容显示（F2：`convertFileSrc`）

- **`convertFileSrc(path)` 可用**（对标 Tauri 的 `convertFileSrc`）：同步返回
  `oriel://file.oriel/<编码后的路径>`，可以直接放进 `<img src>` / `<video src>` / `fetch()`。
  之前它是显式抛错的（那时还没有 asset 协议）。
- **文件内容与页面**不同源**：用的是专用 host `file.oriel` 而不是应用的 host。
  桥接只注入到可信来源，所以磁盘上任何一个 HTML 文件被导航到都**拿不到 IPC 能力**。
  少了这一步，"下载一个 .html 到应用数据目录、再让应用打开它"就是一条提权路径。
  （换 host 之所以真能隔开，是因为登记 scheme 时开了 `HasAuthorityComponent`——
  host 参与来源计算。）
- **它有独立的 scope（`asset.read`），不共用 `fs.*` 的**（照 Tauri 的 `assetProtocol.scope`）：
  "把图片显示出来"与"以程序的方式读写文件"是两种意图，共用一个 scope 会让
  只想显示一张图的应用顺带获得读任意文本的能力。
  ```csharp
  c.Allow(OrielAssetFileServer.ScopeCommand, s => s.AllowPath("$APPDATA/images/**"));
  ```
- **强制点在服务端，不在 `convertFileSrc`**（Tauri 亦然）：那个函数只拼字符串，
  页面自己也能拼出同样的 URL，所以拦它没有意义。真正的判定在 webview 去请求时
  （`OrielAssetFileServer.Serve`）：先归一、再查 scope、最后才碰磁盘；
  不在 scope 内、文件不存在、或者是目录，**一律回 403 且响应体为空**。
  理由只进日志、不进响应——进响应会变成一个探测器，页面能用它区分
  "文件不存在"与"路径不在 scope 内"，进而枚举出这台机器上有哪些路径。
- **URL 把整条路径编成**一个**路径段**（分隔符也编进去）。这一点是踩过才定的：
  URI 的 path 总以 `/` 开头，逐段编码会让"绝对路径的前导斜杠"与"拼 URL 加的那个"在解析时
  无法区分——`/tmp/a.png` 会解成 `tmp/a.png`，在 POSIX 上就从绝对路径变成相对路径。
  代价是 URL 不可读（`oriel://file.oriel/%2Ftmp%2Fa.png`），可以接受。
- **`protocol` 参数**：只接受缺省（Tauri 的 `'asset'`）；传别的值**明确报错**而不是静默忽略。
  本库只登记一个自定义 scheme，静默忽略的后果是"我在配置里指定了协议名，但页面加载不出来"，
  而那个现象与协议名毫无关系。

> **平台覆盖**：目前只有 **Windows** 真的提供文件内容（`Win32AssetScheme` 里接了授权器）。
> Linux 与 macOS 的请求会**一律 403**（fail-closed）——`convertFileSrc` 能拼出 URL、
> 但取不到内容。这是刻意的：宁可"用不了"，也不要"某平台漏了 scope 检查"。
> 已在 ROADMAP 里记为待办。

#### 平台集成（F5：opener / notification / autostart）

- **三个新模块 + 内建命令族**，对标 Tauri 的 `plugin-opener` / `plugin-notification` /
  `plugin-autostart`。**没有新平台代码**——宿主侧的能力早就在 `OrielApp` 上
  （`OpenExternal` / `OpenWithDefaultApp` / `RevealInFileManager` / `ShowNotification` /
  `IsAutoStartEnabled` / `EnableAutoStart` / `DisableAutoStart`），这批做的是给它们配
  Tauri 形状的命令名与参数。
- **三族都不整族放行**，要用必须声明。理由各不相同但都成立：
  `opener.openPath` 等于"用默认程序打开任意文件"（双击一个 `.exe` 就是执行它）、
  `opener.openUrl` 能拉起协议处理器、`autostart.*` 改的是系统状态。
  它们都不是"只操作自己那个窗口"那一类。
- **`opener.openWith` 明确拒绝而不是静默忽略**：那个参数是"用指定程序打开"，
  等于**任意程序执行**，需要一套"允许哪些程序"的授权模型（本库没有）。
  静默忽略会让调用方以为"我指定了用某个程序打开"，实际按系统默认打开——
  一次静默的行为降级，而调用方不会发现。
- **`opener.openUrl` 的协议白名单**：宿主侧的 `OrielShellPolicy` 已经限制了 scheme
  （默认 `http`/`https`/`mailto`），`file:` / `javascript:` / 裸路径会被拒并返回 `false`。
  要放行自定义协议请在**宿主侧**配置白名单，而不是在页面侧拼字符串绕过。
  （写文档时我一度写成"不限制协议"，那是错的——实现比文档更严。）
- **`sendNotification` 在本库是异步的**（与 Tauri 的差别）：Tauri 桌面端直接
  `new window.Notification(...)`（走浏览器的通知 API，所以同步），而 WebView 的浏览器通知
  API 三平台并不可靠（WKWebView 上基本没有），所以本库走宿主命令，三平台都是原生的。
  忽略返回值的调用方不受影响，但不能依赖"调用完通知就已经发出去了"。
- **`notification` 只提供桌面子集**（`isPermissionGranted` / `requestPermission` /
  `sendNotification`）：Tauri 那边还有一整套 `active` / `pending` / `cancel` /
  `removeActive` / `channels` / `registerActionTypes` / `onAction` / `Schedule` /
  `Importance` / `Visibility`，其中大部分要一张通知句柄表（本库没有）或是 Android 概念、
  常驻定时器、通知点击回传。桌面端最常见的用法就是"发一条通知"，那一个函数覆盖了绝大多数场景。
- **`revealItemInDir` 返回布尔**（Tauri 是 void）：一个失败时调用方会以为"都定位到了"。
  全部成功才算成功。
- **`--selftest api` 扩到 93 项**：新增平台集成的 7 项。自检**只验只读那几条与参数校验**
  ——真去 `openUrl` 会弹浏览器、真去 `autostart.enable` 会往系统里写一个自启项，
  自检不该改机器的状态。

#### 系统剪贴板（F6）

- **`@orielweb/api/clipboard`**（`readText` / `writeText` / `clear`）+ 内建命令族
  `clipboard.*`，对标 Tauri 的 `plugin-clipboard-manager` 的**纯文本**部分。
  **这一批有新的平台代码**（此前只有原生编辑命令，没有剪贴板 API）：
  Windows 走 Win32 剪贴板 API、Linux 走 GTK `GtkClipboard`、macOS 走 `NSPasteboard`。
- **`readText` 返回 `null` 而不是空串**：剪贴板里可能是图片、文件列表，或者根本是空的，
  而空串是**一个合法的文本内容**。调用方要能区分"没有文本"与"文本是空的"。
- **写失败返回 `false` 不抛错**：剪贴板是**全局独占**资源
  （Windows 上一次只有一个进程能打开它），别的进程正开着时写不进去是**正常状态**。
  Windows 侧内部会重试 5 次 × 10ms 再放弃——那个状态通常在毫秒级结束，
  而不重试的表现是"偶发写不进剪贴板"，那种偶发最难查。
- **`writeText` 的 `label` 明确拒绝**（Tauri 用它选 X11 的哪个剪贴板：
  `CLIPBOARD` / `PRIMARY` / `SECONDARY`）：本库只操作默认那个，而 Windows/macOS 上
  也没有第二个。静默忽略会让调用方以为"我写进的是 PRIMARY"（X11 上中键粘贴那个），
  实际写进了 CLIPBOARD——一次静默的目标错位。
- **刻意不做**：`writeHtml`（HTML 剪贴板在 Windows 上是 CF_HTML，一个带字节偏移头的
  私有格式，**偏移写错会让粘贴目标是乱码或截断**，而它是在"看起来成功了"的情况下静默发生；
  收益远小于风险）、`writeImage` / `readImage`（要图像句柄模型，本库没有）、
  `writeSecret`（macOS 专有的"不记入剪贴板历史"标记）。
- **Windows 上 `SetClipboardData` 成功之后那块 `GMEM_MOVEABLE` 内存归系统所有**，
  不能再 `GlobalFree`——释放了会让系统在粘贴时读到已释放的内存。
  所以只有失败路径才释放（实现里有注释标出那两处）。
- **Linux 上读剪贴板可能阻塞**：`gtk_clipboard_wait_for_text` 会跑一个内嵌的 main loop
  等所有者应答，对方不响应时会一直等（表现为命令等到前端超时）。
  这是 GTK 剪贴板 API 的形状——它没有"非阻塞取一次"的入口。已在文档写明。
- **macOS 的 `clearContents` 不能省**：它告诉系统"这块内容归我了"，同时给内容打一个
  变更计数（changeCount）。不调用的话别的应用收不到"剪贴板变了"的通知，
  表现为"复制了但粘贴菜单还是灰的"。

> **一件事要先说**：`--selftest api` 会**真写一次系统剪贴板**（写进去、读回来、再清空），
> 因为它改不了系统状态、也不弹窗口，而"写进去再读回来"是唯一能证明三个平台的原生实现
> 真的接对了的方式（单测里没有后端，只测得到"没后端时不抛"）。
> 代价是**会把用户当前的剪贴板内容覆盖掉**——跑这个自检之前请先存好心内容。

#### 对话框（F3：open / save / message）

- **`@orielweb/api/dialog`**（`open` / `save` / `message` / `ask` / `confirm`）+ 内建命令族
  `dialog.*`，对标 Tauri 的 `plugin-dialog`。
- **与既有 `win.pickFile` / `window.pickFile` 的关系**（一个刻意的决定）：那两条是既有的
  公开契约，**冻结不动**（只收 `title` / `filter`、返回单个路径）。本族是**新入口**，
  收 Tauri 形状的 options（多选 / 过滤器 / 初始目录）。
  两者**共用底下的原生对话框**（`IPlatformBackend.ShowOpenFileDialog` 本来就收 options
  对象）——把原生实现抄第二份才是真的错。所以"独立"指的是**命令契约独立**
  （互不影响、可各自演进），不是复制原生代码。有一条测试专门钉住
  "`win.pickFile` 走的仍是宽松签名那条路"。
- **`message` 系列：命令层返回语义，页面层映射成文案**。这是本批最重要的一个设计决定。
  Tauri 的 `message` 返回**被点按钮的文案**，而 Windows 的 `MessageBoxW` **只能显示系统文案**
  （自定义要上 `TaskDialogIndirect`，那需要 comctl32 v6 激活清单）。
  若让命令返回"实际显示的文案"，页面拿它与期望标签比对时会**静默得到错误答案**——
  用户点了"是"、显示的却是"确定"，比较不成立，`ask` 就返回了 `false`。
  返回语义之后，文案只是页面层的事，判断不再受影响。
- **`dialog.open` 在命令层返回数组**，由页面层按"是不是多选"摊平：命令层的返回值形状稳定，
  而页面拿到的形状与 Tauri 一致（单选 `string` / 多选 `string[]` / 取消 `null`）。
- **三处"明确拒绝"**：`canCreateDirectories` / `recursive`（没有对应能力）、
  `directory + multiple`（目录选择器只支持单选，与 Tauri 一致）。
  静默忽略一个布尔选项最危险的地方是**调用方以为它生效了**——
  比如 `canCreateDirectories: false` 被忽略后用户仍能新建目录，而调用方已不再校验那条路径。
- **`dialog.message` 需要新的原生代码**：Windows `MessageBoxW`、Linux `GtkMessageDialog`、
  macOS `NSAlert`。Linux 与 macOS **支持自定义按钮文案**，Windows 不支持（平台限制，
  已记入"已知限制"）。

#### 迁移对照（H：把"一一对应"变成可证伪的）

- **`packages/api/tests/tauri-alignment.test.mjs`**：把 `@tauri-apps/api` 与官方插件当
  **参照实现**，机械比对导出面与方法。
  「模块名与 `@tauri-apps/api` 一一对应，迁移只需替换 import 前缀」是一句**可证伪的话**——
  写进文档只是声明，拿真正的 Tauri 包跑一遍比对才是取证。三个方向都管：

  | 方向 | 要求 |
  |---|---|
  | 参照有、我们没有 | 必须在 `GAPS` 里逐条给出原因（这就是**迁移会撞上的缺口清单**） |
  | 我们有、参照没有 | 必须在 `EXTRAS` 里给出原因（防止"偷偷发明一套自己的 API"） |
  | 清单里列着但其实已实现 | 报错（清单过期了） |

  另有一条**方法级**比对：导出名对了不代表能用——`LogicalSize` 两边都有，但 Tauri 的那个有
  `toPhysical(scaleFactor)`，我们的一开始没有。只比导出名看不出来，而调用方一用就炸。
- **参照实现是 devDependency**：CI 改用 `npm ci`（并加了 `package-lock.json`）。
  没装时那份测试会**跳过**并在输出里说明——离线环境不该让它变红，但 CI 装了、所以 CI 强制检查。
  "跳过的门禁等于没有门禁"，所以 CI 那条是必须的。
- **比对的结果（当前规模）**：

  | 模块 | 参照导出 | 我们有 | 缺口 |
  |---|---|---|---|
  | `dialog` / `opener` / `autostart` | 5 / 3 / 3 | 同 | **0** |
  | `event` / `mocks` | 5 / 4 | 7 / 7 | **0** |
  | `path` | 34 | 33 | 1 |
  | `dpi` | 6 | 8 | 2 |
  | `clipboard` | 7 | 3 | 4 |
  | `core` | 11 | 3 | 8 |
  | `window` / `fs` | 17 / 24 | 9 / 14 | 10 / 10 |
  | `app` | 16 | 5 | 11 |
  | `notification` | 19 | 3 | 16 |

  方法级：比对了 19 个同名类，`window.Window` 上有 62 处未实现的方法，
  已按**类别**（事件入口 / 只读查询 / 状态设置 / 特效 / 光标 / 任务栏 / 移动端专有）
  记进 `METHOD_GAP_GROUPS`——**那就是 M5 的工作清单**。
- **比对抓到的两个真实缺陷（本批已修）**：
  1. **`window` 模块没有转出 dpi 的四个类**。Tauri 的 `@tauri-apps/api/window` 会 re-export
     它们，所以 `import { LogicalSize } from '@tauri-apps/api/window'` 在 Tauri 下能跑；
     我们没转出，那种写法迁移后会直接报"没有这个导出"——看起来像"包不完整"，实际只差一次转出。
  2. **dpi 的类缺 `toPhysical` / `toLogical`**（Tauri 有）。纯数学，已补。
     补的时候发现一处**舍入不对称**并写进了测试：`logicalToPhysical` 要舍入
     （设备像素是整数），`physicalToLogical` **不**舍入（逻辑像素可以是小数，
     4/1.5 = 2.667，舍成 3 就多算了 12%）。
- **一处有意的分歧**（记在 `METHOD_DIVERGENCES`）：**不实现 Tauri 2 的 `toJSON` /
  `__TAURI_TO_IPC_KEY__`**。Tauri 2 把 dpi 值编成 `{ Logical: { x, y } }`（外部标记枚举），
  本库的线格式是 `{ type: "Logical", x, y }`（Tauri 1 的形状），宿主只认后者。
  改过去要**同时改宿主协议**，而页面看不到差别（它只是把对象原样传回 `setSize`）。

#### 窗口只读状态查询（M5-1）

- **`isMinimized` / `isFocused` / `isFullscreen` / `isAlwaysOnTop`**（对标 Tauri 的同名方法）：
  `IWindowBackend` 四个属性 + `WebviewWindow` 四个属性 + `window.*` 四条命令 + JS 四个方法。
- **四条都进了标题栏白名单**（与 `isVisible` / `isMaximized` / `title` 同一理由）：
  自绘标题栏要用它们渲染按钮的当前状态与"窗口是否激活"的配色，
  而它们只报告自己窗口的状态、不泄露跨窗口信息。
- **`isMinimized` 与 `isVisible` 是两件事**：最小化的窗口仍然"可见"，
  它只是被收进了任务栏。混为一谈的后果是"最小化之后页面以为窗口被藏了"。
  三平台实现：Windows `IsIconic`、Linux `gdk_window_get_state & GDK_WINDOW_STATE_ICONIFIED`、
  macOS `isMiniaturized`。
- **`isFocused` 按窗口判、不按应用判**：多窗口应用里只有一个是 key window。
  三平台：Windows 比对 `GetForegroundWindow()`（**不用 `GetFocus`**——它只对调用线程的
  窗口队列有意义）、Linux `gtk_window_is_active`（**不用 `has_toplevel_focus`**——
  同应用内切换窗口时它可能仍为真）、macOS `isKeyWindow`（不是 `isMainWindow`）。
- **`isFullscreen` / `isAlwaysOnTop` 取库记住的状态**，不是去问原生窗口：
  三平台判断"是不是全屏"都要比对窗口矩形与显示器矩形，而那在用户手动把窗口拖成
  刚好铺满屏幕时会给出与"库认为的状态"不一致的答案。记住的状态与 `ToggleFullscreen()`
  的返回值自洽。

#### 外观与能力的只读查询（M5-2）

- **`isDecorated` / `isResizable` / `isMaximizable` / `isMinimizable` / `isClosable` / `theme`**
  （对标 Tauri 的同名方法）：与 M5-1 同构（后端属性 + `WebviewWindow` + `window.*` + JS）。
  **`window.Window` 的只读查询至此全部做完**，工作清单从 58 项降到 **52 项**。
- **"读原生"还是"读记住的状态"，按哪个反映事实来分**：
  `isResizable` 等四条**从原生动读**（Windows 读 `WS_THICKFRAME` / `WS_MAXIMIZEBOX` /
  `WS_MINIMIZEBOX` / `WS_SYSMENU`、GTK `gtk_window_get_resizable`、Cocoa `styleMask`）——
  `SetResizable` 本来就是改原生样式，读回来才反映"系统实际接受的是什么"；
  而 `isDecorated` 取**创建时的选项**（窗口生命期内不变，且 Windows 上它是我们的
  `WM_NCCALCSIZE` 处理，读样式位读不出那个意图）。
- **三处跨平台语义差异，如实写进接口文档**：

  | 查询 | Linux | macOS | Windows |
  |---|---|---|---|
  | `isMaximizable` | = `isResizable` | = `isResizable` | 独立样式位 |
  | `isMinimizable` | = `isResizable` | `Miniaturizable` 位 | 独立样式位 |
  | `isClosable` | **恒 true** | `Closable` 位 | `WS_SYSMENU` |

  前两行是因为 GTK 与 AppKit 都没有"可最大化"这个**独立概念**（最大化跟着 resizable 走）；
  最后一行是因为 GTK 没有"可关闭"概念——窗口总能收到 delete-event，
  而本库的关闭接管是在那之上做的**拦截**。给恒真/回落值比给一个猜测值诚实。
- **`theme` 总给得出值**（`'light'` / `'dark'`）：Tauri 可能返回 `null`（"无法确定"），
  而本库目前只有"跟随系统"这一种来源，三平台都能读到系统主题，
  也没有"按窗口覆写主题"的能力。
- 六条都进了**标题栏白名单**：标题栏靠 `isDecorated` 决定"要不要渲染自绘标题栏"、
  靠 `isMaximizable`/`isMinimizable`/`isClosable` 决定"哪个按钮该出现"、靠 `theme` 决定配色。

#### Window 上的事件转发 + `isEnabled`/`setEnabled`（M5-3 / M5-4）

- **`Window.listen` / `once` / `emit` / `emitTo`**（对标 Tauri 同名方法）：**纯委托**到
  `event` 模块——那边已经做了 Tauri 事件名 → 宿主事件名的映射、"订阅高频事件时自动打开
  宿主转发"，在这一层重新实现一份没有意义。
  Tauri 的 `Window.listen` 对内置事件走一条本地快路径、其余走全局事件系统；
  本库**统一走全局那条**——库里的内置事件本来就是宿主推给页面的同一条通道，
  分成两条只会让"为什么这个事件收得到那个收不到"变得难查。
  一处如实标注：本库的 `event.listen` **同步**返回退订函数（Tauri 返回 `Promise`）；
  `await` 一个非 Promise 值同样可用，所以迁移代码不用改。
- **`unminimize`** 映射到宿主的 `window.restore`：三平台的"还原"本来就是**同一个原生动作**
  （Windows `SW_RESTORE`、GTK `deiconify` + `unmaximize`、Cocoa `deminiaturize:`）——
  Tauri 把 `restore` 与 `unminimize` 分成两条命令，而 Win32 的 `SW_RESTORE` 一个调用
  就同时解除最小化与最大化。如实映射，而不是假装它们是两件事。
- **`isEnabled` / `setEnabled`**：三平台实现——Windows `IsWindowEnabled` / `EnableWindow`、
  GTK `gtk_widget_get_sensitive` / `set_sensitive`、Cocoa `ignoresMouseEvents`。
  **macOS 上"禁用"只挡鼠标、不挡键盘**（Cocoa 没有这个概念），如实写进接口文档。
  `isEnabled` 进标题栏白名单（只读自己窗口的状态），**`setEnabled` 不进**——
  禁用窗口不是标题栏要做的事，而它让页面能把窗口变成"点不动"的状态，那是能力而非外观。

#### `window.Window` 的差异清单全部归类完毕

H 产出的那份清单现在**每一项都有归属**，测试机械保证不会漏：

| 归类 | 含义 |
|---|---|
| 已实现 | 只读查询 13 + 外观查询 6 + 事件入口 4 + `unminimize` + `isEnabled`/`setEnabled` |
| 有意不做 | `activityName` / `sceneIdentifier`（Android/iOS 专有，桌面端没有对应物） |
| 待做（已分类，带原因） | 36 项，见下 |

待做的 36 项按**批次优先级**分组，每组写明"为什么还没做"：

| 组 | 数量 | 为什么还没做 |
|---|---|---|
| 窗口尺寸约束 | 2 | Linux 上 GTK 没有 max size 的简单入口，要改用 `gtk_window_set_geometry_hints`（会**替换**现有 min 机制），属于"没在真机验证过就不动已验证代码" |
| 窗口外观开关 | 9 | 多数是"切一个样式位"，适合一批做；`setDecorations` 单独麻烦（Windows 的 `ComputeStyle` 是**创建期静态决定**，运行期切换要改整套样式与 `WM_NCCALCSIZE` 分支） |
| 平台专有概念 | 6 | 每个平台各有一套甚至只有一个平台有——先得决定"在另两个平台上意味着什么" |
| 窗口特效 | 2 | 三平台机制差异大，要先有一套特效抽象 |
| 光标控制 | 6 | 要光标模型**加光标资源**，不是几个独立开关 |
| 任务栏徽标/进度 | 4 | Windows 要 `ITaskbarList3`（COM）、macOS 要 `NSDockTile` |
| 应用级主题覆写 | 1 | 要先定"覆写的作用域"（整个应用？单窗口？） |
| 宿主事件 | 3 | `onScaleChanged` / `onThemeChanged` / `onDragDropEvent` 需要新的宿主事件 |
| 请求注意 | 1 | **不难**——三平台都有干净入口，只是本批预算用在了别处 |

#### M5-6：方针下可做的项全部实现（10 个 API）

用户 2026-10-18 定下方针：**跨平台不一致的不做**。据此把剩余 36 项分成 A（不做，25 项）与
B（可做，1 项 = `onScaleChanged`），并把 B 组做完。清单现在是
**A·25 项 / B·0 项**，`tauri-alignment.test.mjs` 有测试钉住这两条（每组必须标 A/B 归属、
B 组必须为空）——哪天要在 A 组开工，必须先改断言并说明方针变了，而不是悄悄往里加实现。

- **`requestUserAttention(type?)` + `UserAttentionType`**（任务栏闪烁 / Dock 弹跳）：
  三平台各用原生入口——Windows `FlashWindowEx`（`Critical` 用 `FLASHW_TIMERNOFG` 一直闪到被激活、
  `Informational` 只闪三下、取消走 `FLASHW_STOP`）、Linux `gtk_window_set_urgency_hint`、
  macOS `requestUserAttention:`（返回请求 id 用于取消）。
  **取值 1/2 与 Tauri 一致**，且带反向映射（`UserAttentionType[1] === 'Critical'`）并冻结。
  不传或传 `null` = 取消。**未知值在 JS 层当场抛**——静默当成 `null` 会让"我请求了注意"
  变成"我取消了注意"，两个相反的意图而现场什么都不发生。
  **Linux 上两档没有区别**（GTK 只有开/关，没有分级），如实写进接口文档。
- **`setMaxSize(size?)`**：最大尺寸（逻辑像素的客户区，0 = 不限制）。
  Windows 走 `WM_GETMINMAXINFO` 的 `ptMaxTrackSize`（framed 窗口做客户区→整窗换算，
  与 min 同一处处理）；macOS 用 `setContentMaxSize:`（0 映射成 `double.MaxValue`，
  因为 Cocoa 的"无上限"不是 0）；**Linux 只声明 `GDK_HINT_MAX_SIZE`，不替换既有的
  `set_size_request` min 机制**——两条路各管一端、互不覆盖。取消上限时掩码里不带那个 hint，
  而不是"声明一个 0 的上限"（后者会把窗口卡成 0 宽）。
- **`setSizeConstraints({minWidth?, minHeight?, maxWidth?, maxHeight?} | null)`**：
  页面侧分派到上面两条命令（宿主表面不增加"组合命令"，组合语义只在页面上存在）。
  **语义是整体替换**：省略或给 `null` 的项表示**取消那一项的约束**，传 `null` 取消全部。
  这与 Tauri 一致，也是唯一能在"`Window` 是轻量句柄、不缓存状态"的前提下自洽的做法——
  "省略即保持不动"要求知道当前约束，而本库既不缓存状态、Tauri 也没有"读取约束"的 API
  （加一个会被对齐测试的 `EXTRAS` 检查拦住）。**要只改一侧，就把另一侧的现值一起传进来。**
- **`setMaximizable(bool)` / `setMinimizable(bool)`**：选这一组是因为三平台都有对应的
  只读查询，改完能读回来（自检里有真断言）。
  Windows 改 `WS_MAXIMIZEBOX` / `WS_MINIMIZEBOX`——**只写 `GWL_STYLE` 不够**，
  必须再发一次 `SWP_FRAMECHANGED`，否则系统仍按旧样式画按钮（表现为"属性改了但按钮还在"）；
  只清这两个位是安全的，Win32 会据此自动灰掉系统菜单里的对应项。
  macOS 改 styleMask，**`setMaximizable` 在这里与 `setResizable` 是同一个操作**
  （AppKit 没有独立的最大化位，缩放按钮可用与否完全由 resizable 位决定），副作用已写进文档。
  **Linux 上为空操作**（GTK 无对应入口，tao 同样如此），且**刻意不记状态**——
  一旦记了，`IsMaximizable` 就会给出"用户看到的按钮其实还在"的假值；它继续回落 `isResizable`。
- **`setClosable(bool)`**：三平台落点**不是同一件事**。Windows **灰化系统菜单里的"关闭"项**
  （`EnableMenuItem(SC_CLOSE, MF_GRAYED)` + `DrawMenuBar`）——关闭按钮在 Win32 里属于系统菜单，
  没有独立样式位，用清 `WS_SYSMENU` 代替会把三个按钮和整个系统菜单一起干掉，那是"删按钮"
  而不是"按钮不可用"；macOS 切 `NSWindowStyleMaskClosable`；**Linux 空操作**（GTK 没有这一属性，
  窗口总能收到 `delete-event`，要"关不掉"得拦 `close-requested`，那是应用层策略）。
  **顺带修正 `IsClosable` 的读取语义**：原先只读 `WS_SYSMENU`，那答的是"有没有系统菜单"，
  与 setter 的落点对不上，会"写进去读不回来"。现改为**"存在且未灰化"**
  （`WS_SYSMENU` 且 `GetMenuState(SC_CLOSE)` 不含 `MF_GRAYED`/`MF_DISABLED`），
  不变量成立且旧行为向后兼容（从未灰化过的窗口读取结果不变）。
  实测事实：**无边框窗口也有系统菜单**（frameless 靠 `WM_NCCALCSIZE` 隐藏边框，
  没摘 `WS_SYSMENU`），所以它的 `IsClosable` 是 `true`。
- **`onThemeChanged`**：宿主侧三平台**早就实现好了**（Windows 读注册表 + `WM_SETTINGCHANGE`、
  macOS 听 `AppleInterfaceThemeChangedNotification`、Linux 听 GSettings），
  `OrielApp.PushTheme` 也已经推给每个窗口，缺的只是页面侧的 Tauri 风格包装。
  `HOST_EVENT_MAP` 加 `tauri://theme-changed → theme.changed` + `Window#onThemeChanged`。
  与 Tauri 一致：**只推变化，不推当前值**——初始值要用 `theme()` 读，否则"主题一直没变过"的
  页面永远收不到第一个值。
- **`setIcon`（路径 / 字节 / 裸 RGBA 三种形式）**：覆盖 Tauri `JsImage` 的全部五种写法。
  三平台复用**创建期图标**的代码路径，不写第二份：
  Windows `WM_SETICON`、Linux `gtk_window_set_icon_from_file` / `gtk_window_set_icon`、
  macOS `setApplicationIconImage:`。**Windows 上大/小两个槽位都会设**——
  `ICON_BIG` 给任务栏与 Alt-Tab 大视图、`ICON_SMALL` 给标题栏与 Alt-Tab 小视图；
  只设大图标的表现是"图标换了但标题栏那个还是旧的"。
  顺带填掉一个久已记录的缺口：`LoadImageW` 只认 ICO/BMP，喂 PNG 会返回 0 而且不报错
  （"设了图标但没生效"这种最难查的失败），现在 PNG 先经 `OrielImage` 解码，
  再交给托盘用的那套 `Win32IconFactory.CreateFromRgba` 造 HICON。
  字节走 **base64** 过 IPC（数字数组要四倍体积，而 IPC 是 JSON 文本）；
  `number[]` 的每一项都校验 0..255（`new Uint8Array([999])` 会**静默截断**成 231）。
  解码失败**静默保持原图标并记一条诊断**（与 `LoadImageW` 返回 0 同语义），
  但非法 base64 / RGBA 长度不符**当场抛**——两者的修法完全不同。
  **macOS 上没有窗口级图标**：设的是应用（Dock）图标，会影响整个应用（AppKit 的模型，照实实现）。
- **`setBackgroundColor(color)`（运行期）**：三平台都落到 **webview 自己的底色**，语义一致——
  Windows `ICoreWebView2Controller2.DefaultBackgroundColor`、macOS `NSWindow.backgroundColor` +
  `WKWebView.underPageBackgroundColor`（12+，用 `respondsToSelector:` 探测）、
  Linux `webkit_web_view_set_background_color`。复用各平台已有的 `ApplyBackgroundColor`，
  只加一条"运行期值优先、否则回落创建期选项"的分支。页面侧把 Tauri 的四种 `Color` 形状
  （hex / RGB / RGBA 数组 / 对象）归一成 `0xAARRGGBB` 再发——归一放在 JS 层是因为它能被单测穷举，
  坏输入一律当场抛并说清是哪一路（"红/绿/蓝/alpha" + 越界值）。
- **`onScaleChanged`**：**按窗口**的缩放变化（不是应用级广播：同一时刻两个窗口可以待在不同缩放的
  显示器上，全局事件会让副屏的窗口收到不属于它的值）。Windows 触发点是 `WM_DPICHANGED`
  （**只上报、不接管**消息，窗口尺寸该不该按 lParam 的建议矩形调整留给 `DefWindowProc`）、
  Linux 是 GDK 的 `notify::scale-factor`、macOS 用三个 `NSWindowDelegate` 回调
  （`windowDidResize:` / `windowDidMove:` 覆盖跨屏拖动，`windowDidChangeBackingProperties:`
  覆盖"只改系统缩放而窗口不动"）。payload 与 Tauri 的 `ScaleFactorChanged` 同形
  `{ scaleFactor, size }`，其中 `size` 是**物理像素**（迁移代码会照搬 `payload.size.width`，
  给逻辑尺寸等于悄悄换了单位）。沿用 `OrielWindowEventKind` 的"订阅了才推"机制（新增 `Scale = 8`）。

#### `@orielweb/api/image` 新模块

导出 `Image` 与 `transformImage`（与 Tauri 同名同形状），于是 `setIcon` 支持 `JsImage` 的全部五种形式。

**与 Tauri 的一处本质分歧（有意为之，已记入 `METHOD_DIVERGENCES`）**：Tauri 的 `Image` 是
**资源表 handle**（继承 `Resource`、持 `rid`、字节留在宿主、有 `close()`）。本库 IPC 是一次性
请求-回执，**没有资源表**——引入它等于新造一套句柄生命周期（创建/释放/失效检测），那是另一个
量级的改动，而 `Image` 的实际用途（图标）用字节容器完全够。所以本库的 `Image` 是
**页面侧字节容器**，三个可观察后果：**没有 `close()`**（迁移代码里的 `await image.close()`
会抛 `TypeError`——宁可当场炸，不要带着错误的心智模型继续写）、`rgba()` / `size()` 是本地值
但签名仍是 `Promise`（`await` 照样能跑）、跨 IPC 传的是完整字节。

**尺寸解析在页面侧完成**：PNG 的 IHDR、ICO 的 `ICONDIRENTRY` 都是固定偏移，直接读那几个字节——
这是"纯 JS 容器"能成立的关键（`size()` 不需要宿主）。ICO 的宽高字段只有 1 字节、
**0 表示 256**，这个历史坑单独有测试。解码**像素**才请宿主（`window.decodeImage`），结果缓存；
宿主解出的长度与页面解析的尺寸对不上时报错，不把错位像素交出去。

新增两个宿主命令（都在 demo 白名单里）：`window.readImageFile`（读文件，`Image.fromPath` 用）、
`window.decodeImage`（PNG/ICO → 裸 RGBA，`Image.rgba()` 用）。另新增包内私有模块
`image-bytes.js`（不进 exports）：`setIcon` 与 `image.js` 共用"字节归一 / base64 编解 / 格式嗅探"。

> **清单本身漏项**：`image` 模块此前根本没进对齐测试的 `MODULES` 列表，所以"本库缺
> `@tauri-apps/api/image`"没有任何测试盯着。已登记，对照结果现在是「参照 2，我有 2，缺口 0」。

### 修复

- **CI 的 smoke-linux / smoke-macos 自 0.3.6 起一直红**（`test` 与 `compile-*` 全绿，只有两个平台的自检作业失败）。
  根因有两类，都不是"库在 Linux/macOS 上坏了"：

  1. **自检里写死了 Windows 的平台事实**——`path.sep()` 期望 `\`、`path.delimiter()` 期望 `;`、
     `isAbsolute` 用 `C:`、`configDir == dataDir`、`localDataDir == cacheDir`、
     "Roaming 与 Local 不同"、`fontDir` 必然 `not_supported`、fs 逃逸路径用 `\` 拼。
     这些在另两个平台**必然**不成立（POSIX 的分隔符是 `/`、目录布局完全不同、`fontDir` 有值），
     于是自检一跑就是 8 项红。现在统一按宿主平台取期望值（`window.oriel.platform`），
     并把 `path` 的导入提前到脚本开头——它原来在 fs 段之后声明，fs 段用它会踩 TDZ。
  2. **`<script type="module">` 在自定义 scheme 下不执行**：Windows(WebView2) 能加载，
     macOS(WKWebView) / Linux(WebKitGTK) 上同一个页面**脚本静默不执行**。
     症状极具误导性——徽章不更新、主题不应用、探针不回报，而宿主侧只看到"超时"。
     修法：页面容器改成**经典脚本里的动态 `import()`**（这条路三平台都能用，
     `--selftest api` 里那些 `await import('./vendor/…')` 一直是这么做的），
     `app.js` 本身仍是 ES module。顺带给 Linux 的 `oriel://` 补上
     `webkit_security_manager_register_uri_scheme_as_cors_enabled` 注册
     （module 抓取走 CORS 检查，未登记的 scheme 会被拒；它与 `..._as_secure` 是两件事）。

- **主题在页面侧依赖"事件早于监听"的时序**：宿主只在**导航完成**时补推一次 `theme.changed`，
  而页面脚本的注册时机取决于模块怎么加载（静态 module 是 defer、动态 import 是异步），
  两者没有可依赖的先后。现在 `app.js` 启动时主动 `getCurrentWindow().theme()` 查一次
  （`window.theme` 在标题栏白名单里，无需额外授权），事件只负责后续变化。
  **这是把跨平台 CI 里"主题永不生效"与本地"改加载方式后主题失效"两个现象一次性修掉的点**——
  它俩是同一个竞态的两面。

- **`--selftest theme` 的失败信息说不出根因**：只有"20 秒内页面没有把 data-theme 设为 X"，
  而这一句话对应三种完全不同的原因（桥接没注入 / 页面脚本没执行 / 监听注册晚于事件）。
  现在失败时会额外打印页面状态（`typeof window.oriel`、`_onEvent`、`readyState`、
  当前 `data-theme`、徽章文案）——本次就是靠它一眼看出"脚本跑了、桥接在、只是监听晚了"。

- **Linux 的剪贴板读回是乱码**：`ReadClipboardText` 用了 `Marshal.PtrToStringUni`（UTF-16），
  而 `gtk_clipboard_wait_for_text` 返回的是 **UTF-8** 的 `char*`。后果不是"看起来乱码"那么明显——
  ASCII 会被两两配对成汉字（`"oriel-selftest"` 读成 `"牯敩⵳晬整瑳…"`），
  而那看起来像"剪贴板里本来就是乱码"，想不到编码。改用 `PtrToStringUTF8`。

- **`getFocusedWindow()` 在 `--selftest window` 之外没有真断言**：`app.js` 与窗口自检都补了
  "页面查询与宿主侧一致"的断言；`--selftest api` 里那条"恒为 null"改成
  "要么 null、要么已知 label，且与 `isFocused()` 自洽"。

- **依赖窗口管理器 / 平台能力的自检项在无 WM 的 CI 环境里假红**：最小化（`iconify`）、
  "关掉一个窗口后焦点回到另一个"、关闭接管的四条路径，都需要**窗口管理器**才成立；
  而 xvfb 里没有 WM。现在 `verify-linux.sh` 探测 `_NET_SUPPORTING_WM_CHECK` 并传
  `ORIEL_HAS_WM`，自检据此把这些项打印成 `SKIP`（可见、但不计入失败）。
  同理，Linux 上 `IsClosable`（恒 true）与 `setMaximizable` / `setMinimizable`（空操作）
  是**已登记的平台事实**，对应断言也改为 `SKIP` 而不是 FAIL。
  跳过不等于通过——输出里写明原因，不会让"这条没验过"消失。

- **自检的收尾依赖窗口关闭流程，导致 Linux 上"结论已 PASS 却退出码 124"**：
  `theme` / `nav` / `ipc` / `capability` / `scheme` / `multiwindow` 都以 `window.Close()` 收尾，
  而 GTK 的 `gtk_window_close` 要走 delete-event——没有窗口管理器的环境里那条链不成立，
  进程于是挂到脚本的 `timeout` 才被杀。后果不只是慢：`smoke-linux` 每项白等 150 秒，
  18 项就吃掉二十多分钟，而 UI 上只看到"超时"。
  现统一改成 `App.Quit()`（直接停消息循环，三平台都可靠）。
  **WSL + xvfb 实测**：改前 `nav`/`theme` 都是 124，改后全部 0。

- **Linux 的 `load-failed` 把"被新导航取消的旧加载"当成真失败**：
  WebKitGTK 在 `go_back` / `go_forward` / `reload` 或页面自行发起新导航时，会给上一次未完成的
  加载补一条 `load-failed`，错误码 `WEBKIT_NETWORK_ERROR_CANCELLED(302)`、文案
  "Load request cancelled"。原来的实现无条件上报失败，于是**后退被记成失败**，
  而实际后退是成功的。现按错误码 / 文案识别 cancelled 并跳过（浏览器语义里它也不属于错误）。
  这是 `--selftest nav` 在 Linux 上唯一红的那条。

- **Linux 的剪贴板"清空"没真的清空**：写的时候会 `gtk_clipboard_store` 把内容交给
  剪贴板管理器持久化（否则应用一退内容就没了），而 `gtk_clipboard_clear` 只放弃本进程的
  所有权——管理器手里那份还在，于是"清空之后再读"会拿回**旧内容**。
  现在 clear 之后补一次 `set_text("")` + `store`，让管理器也变成空。

- **`openUrl` 在 POSIX 上会接受裸绝对路径**：`Uri.TryCreate("/tmp/a.txt", UriKind.Absolute)`
  在 Unix 上**成功**（被当成隐式的 `file:` URI），于是同一条校验在 Windows 上拒绝、
  在 Linux 上放行——放行之后又被 `OpenExternal` 的白名单挡掉，页面拿到的是 `false`
  而不是 `invalid_argument`。判据补了半句"原文必须以 `<scheme>:` 开头"。
  **这条是 Linux 上跑单测才暴露的**（Windows 上 1056 项全绿，Linux 上红 1 项）。

- **`--selftest geometry` / `multiwindow` 在无窗口管理器的环境里假红**：
  `MoveTo` 依赖 WM（`gtk_window_move` 在 xvfb 下是空操作，位置停在 26,23 或 0,0），
  所以 geometry 的"OuterPosition 应接近 MoveTo 值"与 windowevents 的同类断言在无 WM 时 SKIP；
  multiwindow 的"关窗"改为在该环境用 `win.destroy`（同样只作用于发起者，但不经 delete-event），
  并把"关窗后第一个窗口还能回显"降级为 SKIP——**关窗没把应用带走**这个实质改由
  `Windows.Count == 1` 直接断言，不再依赖跨窗口求值的时序。

- **`verify-linux.sh` 的窗口管理器探测**：新增 `_NET_SUPPORTING_WM_CHECK` 检测并导出
  `ORIEL_HAS_WM`，让上述 SKIP 有客观依据（而不是"Linux 就跳过"）。

- **Windows 的 `setIcon` 只设了大图标，漏了小图标**（`ICON_SMALL`）：PNG / 字节 / 裸 RGBA
  三条路径都只发 `WM_SETICON ICON_BIG`，而 ICO/BMP 路径（`LoadImageW` 那条）一直是两个都设。
  后果是"图标换了，但标题栏那个还是旧的"——**恰好是这批要消灭的那类"设了但部分没生效"**，
  而且按输入格式分叉。现在四条路径统一走 `SetIconHandles`，同一个 HICON 同时设到两个槽位
  （`WM_SETICON` 不接管所有权，同一个句柄设两次是合法的）。
  另：`ApplyPngIcon` 解码失败时**补上诊断**——字节那条路本来就会记一笔，两条路都是
  "静默保持原图标"，不该只有一条留下痕迹。
- **`setSizeConstraints` 的文档承诺与实现矛盾**：文档写"省略或给 `null` 的项保持不动"，
  而实现把缺省项 `?? 0` 直接下发——`{ minHeight: 150 }` 会**把已设的 minWidth 清成 0**。
  两版语义只能留一个，而"保持不动"要求知道当前约束（本库不缓存状态、Tauri 也没有读取 API），
  所以统一为**整体替换**（省略 = 取消那一项），与 Tauri 一致，并把理由写进 JSDoc 与类型声明。
  测试也补上了"只给一侧中的一个分量"这一档——旧测试只覆盖了"只给上限"与"两侧都给"，
  正好漏掉了出问题的形状。
- **`requestUserAttention` 对未知值静默当成取消**：`requestUserAttention(3)` 会被翻成 `null`
  （= 取消已发出的请求），调用方以为"我请求了注意"、实际执行了相反的操作，而现场什么都不发生。
  现在 JS 层对既不是两个常量也不是 `null`/`undefined` 的值**当场抛** `TypeError`，
  并且**不发命令**（发了就是把错误意图执行了一遍）。
- **`IsClosable` / `GetSystemMenu` 的注释举例与实测相反**：两处写着"无系统菜单的窗口（无边框）"、
  "未带 `WS_SYSMENU`，如无边框窗口"，而实测是**无边框窗口也有系统菜单**
  （frameless 靠 `WM_NCCALCSIZE` 隐藏边框，`ComputeStyle` 里照样带 `WS_SYSMENU`），
  所以它的 `IsClosable` 是 `true`。实现按位判断不受影响，但注释会把人引向"无边框窗口
  不可关闭"的错误结论，已按实测更正。
- **事件转发只开不关**：`listen` 时按需打开 `window.setEventForwarding`，但**退订时不关**——
  一次 `listen` 会让宿主在整个窗口生命期里持续推送该事件（拖动时每像素一条），
  而页面已经没人听了。这也与 `onCloseRequested` 的"退订时关掉接管"不一致。
  现改为**引用计数**（同一个 kind 可能被多个订阅共享：`tauri://focus` 与 `tauri://blur`
  都映射到 `focus`），最后一个订阅者走了才关；退订是幂等的，重复调用不会把计数多减一次
  （否则会把另一个订阅者的份额吃掉，让它静默收不到事件）。关不掉**不报错**——
  那只会多推几条事件，不影响正确性，与"打不开必须报"是两回事。
  此前这个开关**完全没有测试**，现补 4 条（开与关对称、共享 kind 的计数、幂等、不需要转发的事件不碰命令）。
- **`setEventForwarding` 的返回值走匿名类型，必然失败**：给它加第 4 个字段时自检当场报
  "命令返回类型 `<>f__AnonymousType0\`4[...]` 需要 JSON 上下文"。这**不是**新引入的：
  之前返回的是三字段匿名类型，而 demo 的 `JsonSerializerContext` 里没有任何匿名类型注册，
  所以同样会失败——只是此前从没有自检真正调用过这个命令。后果是转发功能本身是好的
  （副作用先发生），但每次 `listen` 打开转发都会在页面 `console.error` 里留下一条**假的失败**，
  排查真实问题时会被误导。改用手拼 JSON 返回 `JsonElement`（与 `SizeOf` / `PositionOf` 同一套做法）。

- **白名单改动因 PowerShell 解析失败而静默丢失**：`window.isEnabled` 的白名单条目
  在一次批量编辑里没写进去——那条命令在**后面**的 JS 编辑处遇到中文解析错误而整体失败，
  前面的 C# 编辑虽然执行了，但这一条没有。症状是自检报"`window.isEnabled` 不在 Allow 名单里"，
  而我以为它已在白名单里。
  教训：**批量命令里的中文一律用 `edit` 工具**，不要塞进 PowerShell 字符串——
  这个会话里它已经反复把中文弄乱；判断"编辑是否生效"不能靠记忆，要 grep 回来。

- **自检里"拿主窗口当装饰对照"的错误假设**：`isDecorated` 的第一版断言是
  "探针窗口 false、主窗口 true"，结果主窗口那条**失败**——demo 的主窗口**也是无边框的**
  （它用自绘标题栏）。于是"两个都 false"根本证明不了查询不是写死的。
  改成**另建一个带装饰的探针窗口**，在同一时刻让两个窗口给出不同答案——
  那才说明查询真的读了每个窗口自己的状态。
  这类"我以为是 A、实际是 B"的假设，只有把断言写成可观察的对照才会暴露。

- **`dialog.js` 里形参遮蔽了同名导出函数**：`ask(message, options)` 与
  `confirm(message, options)` 里的形参 `message` 遮蔽了导出的 `message` 函数，
  于是内部写 `message(...)` 会得到 **"message is not a function"**。
  TypeScript 在 `checkJs` 下抓到了它，但那是"碰巧被抓到"——形参改名就不会了。
  所以结构上避开：抽一个内部 `sendMessage(text, options)`，三个导出都调它。
  （Tauri 那边把它叫 `messageCommand` 正是躲这个。）

- **`--selftest windowevents` 在当前桌面会话下失败（环境依赖，经回滚验证非本批引入）**：
  "另一个窗口关闭后，本窗口应收到 `FocusChanged(true)`" 这一条不成立。
  **验证方式**：把工作区退到上一批（F6）重新构建，同一个二进制**同样失败**，
  所以不是本批的回归。现场特征：残留进程为 0，而 `GetForegroundWindow()` 取不到
  前台窗口标题——当前会话没有把前台交给这个进程，于是关闭第二个窗口时
  `WM_SETFOCUS` 不会到来。这一条本来就依赖真实的 OS 焦点交接。

- **`--selftest api` 的超时消息说不出根因**：探针脚本解析失败时（例如一个重复的
  `const`）**一条输出都没有**，表现是"60 秒超时"——而超时这个现象与"有个重复的变量名"
  看不出任何关系。现在超时消息会指明"若此前一条 OK 都没有，多半是解析失败"，
  并给出定位手段（`ORIEL_PROBE_DUMP=1` + `node --check`）。
  **这个坑在本轮踩了两次**（`escapeCode`、`readBack` 各一次）——根因是探针是一个
  **扁平作用域**，所有 `const` 都在同一层。诊断开关因此是值得留的。

- **未 `Run()` 时内建命令抛的是未分类异常**：`ShowNotification` / `IsAutoStartEnabled`
  等在 `_backend is null` 时抛 `InvalidOperationException("应用尚未运行")`，
  而 `OpenExternal` 返回 `false`——同一个"宿主没起来"的状态有两套行为。
  到了页面就是一句"命令执行失败"（`internal` 码），调用方看不出原因。
  新增 `OrielApp.IsRunning`，平台集成命令在未就绪时统一返回 `false`。
  实践中页面只在 `Run()` 之后才存在，所以这条分支正常走不到——
  而"正常走不到"的分支正是最需要给出可读错误的那些。

- **`fs.writeTextFile` 会写 UTF-8 BOM**：用了 `File.WriteAllText(..., Encoding.UTF8)`，
  而那个静态属性写文件时**会写 BOM**（`EF BB BF`）。后果很具体：写 5 个汉字，
  `fs.size` 给的是 18 而不是 15——而调用方算的是字符数乘 3。
  更麻烦的是下游：BOM 会让 JSON/CSV/shell 解析器把首字节当内容的一部分。
  Tauri 的 `writeTextFile` 走 `String::into_bytes()`、**不写 BOM**，现已对齐
  （读这边不必改：`File.ReadAllText` 会自己探测并去掉 BOM，所以别人写的带 BOM 文件照样能读）。
  **这是真页面自检抓到的**——单测里我用的 `File.WriteAllText(file, "12345")`
  两参重载默认不写 BOM，所以那条路一直是绿的。
- **`byte[]` 返回值需要 STJ 上下文**：`fs.readFile` 返回 `byte[]`，
  而 `OrielJson.WriteResult` 只认基元、`JsonElement` 与字符串序列——
  `byte[]` 是纯 BCL 类型，要求应用为它加 `[JsonSerializable]` 并不合理，
  漏加的症状是"这个命令一调就报错"。现手写循环输出数字数组（AOT 安全、无反射）。
- **`fs` 的 `AuthorizePath` 里补了命令授权检查**：分发器已经查过，所以这不影响正常运行；
  但它让"没声明这个命令"与"声明了命令但路径不对"在**任何入口**下都能分开报。
  少了这一遍的话，直接调处理函数会把"命令没授权"报成 `path_not_allowed`——
  那个错因描述会把人引向错的方向。
- **`--selftest api` 不幂等**：它在临时目录里真建文件，而第二次跑时
  `fs.mkdir` 会因为"目录已存在"（非递归会报错）而失败。
  这种"第一次跑过、第二次不过"的失败最难查——它看起来像随机失败。现在开头先清理。
- **一条"探针脚本解析失败"的诊断手段**：手写的探针脚本里出现重复的 `let` 声明时，
  整个脚本**解析失败、一条输出都没有**，表现是"自检超时"——而超时这个现象完全指不到
  "有个重复的 const"这种根因。现在支持 `ORIEL_PROBE_DUMP=1` 把生成的脚本落盘，
  一条 `node --check` 就能定位。（这个开关不是假设出来的：调试这一批时就撞上了。）
- **`Video` 目录那格把 `macName` 与 `windowsFolder` 写反了**
  （`UserDir("Videos", …, "Movies")`，正确是 `UserDir("Movies", …, KnownFolder.Videos)`）。
  运行到那一格才炸，而炸出来的是 `ArgumentException: Requested value 'Movies' was not found`
  ——与"目录解析"看不出关系。**修复方式是消除这类笔误的可能性**：第三个参数从
  "枚举名字符串 + `Enum.Parse`"改成直接传枚举，同样的笔误从此是编译错误。
  这个缺陷是测试抓到的，而测试之所以能抓到，是因为它**用 `SHGetKnownFolderPath`
  独立算一遍期望值**，而不是复用实现里的枚举——否则"实现和测试抄同一份错的映射"
  会双双通过。
- **`oriel.version` 被双重编码**：`__ORIEL_VERSION__` 在模板里原本写着 `'__ORIEL_VERSION__'`
  （自带引号），而本轮把宿主的注入值改成了 JSON 字符串字面量，于是生成的脚本成了
  `version: '"0.3.5"',`——页面拿到的是**连引号一起**的 `"0.3.5"`，所有基于
  `oriel.version` 的版本比较会静默给出错答案，而打印出来几乎看不出来（两边都有引号）。
  模板改成不带引号的写法（与 `label` 同一模式：token 展开成一段 **JS 表达式**）。
  **这是真页面自检抓到的**——Node 侧的桥接测试当时也一起红了（夹具注入裸值导致语法错），
  但双重编码那个更阴的变体只有把值打出来看才认得出。
  现在夹具统一用 `JSON.stringify` 注入，并加了一条断言：`oriel.version` 是 string 且
  **不含引号**。token 的契约（"展开成表达式"）也写进了夹具注释。
- **`extname` 对隐藏文件的口径**：.NET 的 `Path.GetExtension(".gitignore")` 给 `gitignore`，
  而 Rust 的 `Path::extension()`（Tauri 的 `extname`）与 Node 的 `path.extname` 都给**空串**
  ——整个名字是文件名，前导点不算扩展名分隔符。从 Tauri 迁来的代码走的是 Rust 口径，
  所以这里显式实现那条规则（文件名里最后一个点之后的部分，且点不能在第 0 位或末尾）。
- **`basename` / `dirname` 忽略尾随分隔符**：`Path.GetFileName("/a/b/")` 给空串、
  `Path.GetDirectoryName("dir/")` 给 `dir`，而 Rust 的 `file_name()` / `parent()`
  忽略尾随分隔符（Tauri 文档里就写着 "trailing directory separators are ignored"，
  其例子 `dirname('/path/to/somedir/')` 给 `/path/to`）。现在两者都先去尾再算。
  去尾时**保留根**：把 `"C:\"` 剪成 `"C:"` 会让 `GetFileName` 给出一个看起来像文件名、
  实际什么都不是的东西。
- **`WithSize` / `Resize` / `WithMinSize` 在 Windows 上按整窗尺寸设置（客户区比另两个平台小一圈）**。
  实测 `WithSize(800, 600)` 在 Windows 装饰窗口上得到客户区 `784x561`——宽度少 16（左右边框各 8）、
  高度少 39（标题栏 31 + 下边框 8），而 Linux（`gtk_window_resize`）与 macOS（`setContentSize:`）
  得到的就是 800x600。三个平台里只有 Windows 吃的是**整窗**尺寸，这一直是缺陷而不是语义选择。
  现在 Windows 侧在 `Create` / `Resize` / `WM_GETMINMAXINFO` 三条路径上都做
  "逻辑像素 → 物理像素 → 客户区转整窗（`AdjustWindowRectExForDpi`）"的换算，
  **无边框窗口明确不做这一步**（它的 `WM_NCCALCSIZE` 让客户区铺满整窗，再换算会大一圈）。
  取证：`--selftest geometry` 修复前 `784x561`（FAIL），修复后 `800x600`（PASS）；
  运行期 `Resize(640, 480)` 同样精确；`--selftest geometry-frameless` 确认无边框形态未被误伤。
  **影响面**：装饰窗口（`Frameless` 默认 false，即平台原生形态）的内容区会**变大**到与另两个平台一致；
  无边框窗口行为不变（demo 与多数工业界面用的就是它）。
- **命令返回值支持字符串序列**（`string[]` / `List<string>` / 任何 `IEnumerable<string>`）：
  此前它们需要应用往 `JsonSerializerContext` 里加 `[JsonSerializable]`，而对一个纯 BCL 类型
  来说那是很不合理的负担，漏加的症状是"这个命令一调就报错、其它命令都好"。
  现在手写数组输出（AOT 安全、无反射）。`window.getAll` 返回 label 列表正依赖它。
- **几何查询的返回值最初用匿名对象，会要求应用注册库内部的类型**：它们走
  `OrielJson.WriteResult`，而那条路只认基元与 `JsonElement`——匿名对象是普通类型、
  需要 STJ 类型信息，于是运行期报错说"请为 `<>f__AnonymousType0\`3` 添加 [JsonSerializable]"
  （应用凭什么知道库内部有个匿名类型？）。现改为手拼 JSON 再解析成 `JsonElement`
  （文档里那个逃生口），不需要应用配合。
- **主题自检不再依赖页面的测试钩子**：原先 `app.js` 会 `postMessage('theme-echo')` 回显给宿主，
  宿主据此判定。迁移时去掉了那个回显，改为宿主直接轮询页面上的 `<html data-theme>`。
  两处好处：示例页不再夹带测试钩子；而且验的是"页面真的应用了主题"这个**可见效果**，
  比验一条旁路回显更接近用户看到的东西。
- **`API.md` 里 macOS 退出的说法与实现相反**：两处仍写着 `NSApplication.terminate:`，
  而 0.3.5 已改成 `stop:`（`terminate:` 不让 `Run()` 返回，退出清理被整个跳过）。
  文档已更正。
- **demo 的结论行在 Windows 上按 GBK 写出**：控制台默认代码页是 GBK，重定向到文件后
  CI 用 UTF-8 的 grep 匹配不到中文结论——症状是"自检明明跑了，脚本却说没有结论行"。
  现在 `Main` 首行显式置 `Console.OutputEncoding = UTF8`。
- **`.gitignore` 的 `**/packages/*` 会把 `packages/api` 整个忽略**：`git status` 里看不到，
  CI checkout 后找不到文件。本仓库全部使用 `PackageReference`（还原到全局缓存），
  从不需要旧式 `packages/` 目录，该规则已删除。

### 文档

- **`API.md` 补上 M5 新增的窗口 API**：`WebviewWindow` 表此前只有 M5-3/4/5 之前的成员，
  M5-6 的 10 个 API（`SetEnabled` / `SetMaximizable` / `SetMinimizable` / `SetClosable` /
  `SetMaxSize` / `RequestUserAttention` / `SetIcon` 三种形式 / `SetBackgroundColor`）与
  M5-1/2 的只读查询（`IsMinimized` / `IsFocused` / `IsFullscreen` / `IsAlwaysOnTop` /
  `IsDecorated` / `IsResizable` / `IsMaximizable` / `IsMinimizable` / `IsClosable` / `IsEnabled`）
  全部补齐，并逐条写明三平台落点差异。
- **`API.md` 的前端包模块表补上 `image`**，并把 `window` 模块的 M5 方法与事件列出来；
  模块总数从"5 个"更正为 14 个。
- **`packages/api/README.md` 的模块状态表整体重写**：此前停在"A/B/E1 刚落地"的时点——
  `convertFileSrc` 标着"显式未实现"（早已实现）、`app` / `dialog` / `fs` / `image` 标着"尚未实现"
  （都已实现）、`getFocusedWindow` 标着"恒 `null`"（已实现）。另外"与 Tauri 的语义差异"那段
  还写着"per-call `timeout` 尚未实现"与"失败值是字符串、结构化 `code`/`data` 尚未实现"，
  两条都已过时。顺带修掉一处制表符导致的乱码（`ests/tauri-alignment.test.mjs`）。
- **`REVIEW-2026-10-18.md` 里 `setMaxSize` 的 Linux 说明是错的**（写"整体替换 min 机制，
  故 min/max 合并成一次调用"）：实现只声明 `GDK_HINT_MAX_SIZE`，min 仍走 `set_size_request`，
  两条路各管一端、互不覆盖。已按代码更正。
- **版本号统一到 `0.3.6`**：根 `Directory.Build.props` 与 `packages/api/package.json` /
  `package-lock.json` 此前不一致（0.3.5 / 0.4.0），而 `release.yml` 只认根 `<Version>`。
  包内的 `0.3.6+` 能力门槛提示（per-call timeout、`getCurrentWindow` 的 label 注入）
  随之更正——它们此前写的是 `0.4.0+`，但那些能力是本批新增的，不是 0.4.0 的。

### 已知限制（未验证，不是未实现）

- **Windows 高 DPI 下的换算未实测**：本机只有一台 100% 缩放显示器。
  `AdjustWindowRectExForDpi` 按**窗口所在显示器**的 DPI 取值，逻辑上正确，
  但 125%/150%/200% 下的逐像素结果没有取证。需要时在有缩放显示器的机器上跑 `--selftest geometry`。
- **`--selftest geometry` / `windowevents` / `event` / `close` / `window` 尚未在 Linux / macOS 上跑过**：
  代码已通过这两个目标的交叉编译（`linux-x64` / `osx-arm64` / `osx-x64` 均 0 警告 0 错误），
  但运行期行为需要 CI 取证。尤其 Linux 的 `configure-event`、macOS 的 `windowDidMove:`
  与三平台的关闭接管路径都是本次新接的——"编译通过"不等于"事件真的到了"。
- **`getFocusedWindow()` 已在 0.3.6 实现**（遍历存活窗口取第一个 `IsFocused` 为真的），
  但**焦点状态本身没有在 Linux / macOS 上真机取证**：Windows 侧
  （`GetForegroundWindow` 比对）已在 `--selftest window` 里验过，另两个平台的
  `gtk_window_is_active` / `isKeyWindow` 只有交叉编译。若某个平台上它恒返回 `null`，
  先确认 `isFocused` 那一条自检——两者的判定依据是同一个。
- **`tauri://destroyed` 永远收不到**：窗口销毁时页面正在被拆掉，事件送不出去。
  需要"即将关闭"的通知请用 `tauri://close-requested`（其前提是宿主开了关闭接管）。
- **`tauri://window-created` 未实现**；`tauri://scale-change`（`onScaleChanged`）与
  `tauri://theme-changed`（`onThemeChanged`）已在 M5-6 实现。
- **macOS 缩放变化的两条路径未在真机验证**：`windowDidResize:` / `windowDidMove:` 覆盖跨屏拖动，
  `windowDidChangeBackingProperties:` 覆盖"只改系统缩放而窗口不动"。改法本身正确且更简单
  （那个方法本来就是 `NSWindowDelegate` 的正式成员，不需要新观察者），但**选择器名写错时
  ObjC 会静默不调用**——这类错误只有真机能发现。已记入 ROADMAP 真机清单。
- **图标与底色的最终显示只能目视**：命令层与解码有自动化覆盖（`--selftest window`），
  但宿主没有"读回当前图标/底色"的接口（加一个就是偷偷发明 API，会被 `EXTRAS` 检查拦住）。
  两处容易误解：macOS 的 `setIcon` 影响的是**整个应用**的 Dock 图标；
  Linux 的 Wayland 下窗口图标由合成器决定，通常忽略该调用。
- **`innerSize` 等几何查询的 Linux 平台限制**（是 Wayland/X11 的平台事实，不是实现取巧）：
  `OuterSize` 可能与 `InnerSize` 相等（合成器不给装饰尺寸）、`InnerPosition` 恒等于
  `OuterPosition`（GTK 不暴露装饰偏移）、`OuterPosition` 在 Wayland 下恒为 `(0,0)`
  （合成器出于隐私不告诉客户端窗口在哪）。三平台里只有 Windows 与 macOS 能给出完整的内外几何。

## 0.3.5 - 2026-10-14

下游反馈（一个真实工业上位机的迁移复盘）里"值得在库上做"的六条，按价值排序全部落地。
其中前三条是**缺陷**（会直接导致现场故障），后三条是**缺口与文档**（让宿主不必各写一套兜底）。

### 修复

- **macOS：`Quit()` 走 `terminate:` 导致 `Run()` 永不返回，退出清理被整个跳过**。
  **这条是本批接进 CI 之后才暴露的**——`terminate:` 走的是 AppKit 的"应用终止"流程
  （询问文档控制器、delegate 等），它**不会让 `NSApplication.run` 返回**；
  而 `run` 在 AppKit 里是 [point of no return](https://lapcatsoftware.com/blog/2007/03/10/everything-you-always-wanted-to-know-about-nsapplication/)。
  后果是 `Run()` 之后的清理（撤销托盘、释放后端与共享环境）**全部被跳过**，
  `ExitCompleted` 永不触发，退出码也不是应用设的那个——这与库对使用者的承诺
  （"`Quit` 只请求退出，`Run()` 返回后该做的清理仍会执行"）直接冲突。
  CI 上的症状是 exit 自检只打印 `Exiting`、没有结论行、退出码 0。
  现在改用 `stop:`（Apple 文档："Stops the main event loop... sets a flag that the app checks
  only after it finishes dispatching an event"——循环会退出、`run` 会返回，与 Windows 的
  `PostQuitMessage`、Linux 的 `gtk_main_quit` 同义），并补投一个 `CGEventCreate(NULL)` 空事件
  唤醒 run loop（`stop:` 只保证"处理完当前事件后"退出，队列空时它会一直等下一个事件，
  表现为"调了 Quit 却退不掉"）。
  连带修正 `applicationShouldTerminateAfterLastWindowClosed:` 的答复：由 **1 改为 0**——
  答 1 会让 AppKit 抢先走 `terminate:`，于是"所有窗口关闭"这条路径同样跳过清理。
  改答 0 后它汇到窗口委托的 `windowWillClose:` → `OnWindowDestroyed` → `Quit()`（`stop:`），
  与 Windows（存活窗口归零 → `PostQuitMessage`）、Linux（`gtk_main_quit`）语义一致。
- **`Quit()` 语义分裂：只请求退出、不保证退出**。`PostQuitMessage` 只是往消息队列投一条 `WM_QUIT`，
  而消息循环停下之后，宿主在 `Run()` 返回后跑的清理全部同步落在 UI 线程上，任何一步卡住
   （等一个失去响应窗口的同步调用、一个不返回的 join）都会让进程驻留——驻留期间可执行文件映像
  仍被映射，现场表现是**"程序删不掉/装不上"**，Windows 还会判成 AppHangB1（宿主实测最坏 ≈ 7s：
  `Join(2s) + Wait(2s) + Logger.Dispose(3s)`，而消息循环此时已停，任何需要 UI 线程的清理
  都不再可能完成）。现在 `Quit()` 在投递退出消息的同时起一个看门狗线程：从"请求退出"算起超过
  `ExitTimeoutDuration`（默认 10 秒）进程仍在，就强制结束它并以 `ForceExitCode`（默认 `2`）退出。
  **看门狗覆盖整段退出，包括 `Run()` 返回之后宿主的收尾**——那正是最容易卡住的一段。
  可用 `UseExitTimeout` / `UseForceExitCode` / `UseExitWatchdog(false)` 调整。
  自然退出路径（所有窗口关闭、平台落出循环）同样受保护。
- **`Quit()` 的线程相关性此前是隐式的**。`PostQuitMessage` / `GtkMainQuit` / `NSApplication.terminate:`
  都是线程相关的：从线程池调用会投给一个**没有消息循环的线程**，主循环永远收不到，表现为
  "调了 `Quit` 却退不掉"。现在不在 UI 线程调用会立刻抛 `InvalidOperationException` 并说明原因，
  而不是静默投递（从其它线程退出请用 `InvokeOnUiThread` 编组）。
- **`WithIcon` 文件缺失直接抛异常，把观感问题升级成启动失败**。图标是纯观感，缺失只该回落到平台
  默认图标；此前它在 `AddWindow` 的**配置回调**里抛 `FileNotFoundException`——此时还没有窗口、
  没有 UI，用户看到的就是**"双击没反应"**（现场留下 `startup-error.log`：
  `FileNotFoundException: 图标文件不存在：icon.ico` at `set_Icon` at `AppHost.ConfigureWindow`）。
  现在缺失即降级为平台默认图标 + 经 `OrielDiagnostics.Reported` 报一条警告；需要"路径写错就启动
  失败"的严格行为用 `WithRequireIcon()` / `RequireIcon = true`。空白路径仍立刻抛 `ArgumentException`
  （那是调用方写错了，不是"文件缺失"）。`Icon` 读回的是**生效值**，原始请求路径见 `RequestedIcon`。
- **相对路径按当前工作目录解析**（工控机高发）。现场从快捷方式、计划任务、开机自启启动时 cwd
  常常不是程序目录，macOS 从 `.app` 启动时 cwd 更是 `/`——"开发机上好好的、一到现场就失效"。
  现在**所有接受文件路径的选项**（窗口图标、`OrielTrayOptions.IconPath` / `OrielTray.SetIcon`、
  `OrielNotificationOptions.IconPath`）都统一按 `AppContext.BaseDirectory`（可执行文件所在目录）解析；
  绝对路径原样使用，要按 cwd 解析请自行传 `Path.GetFullPath(...)` 的结果。
- **`PostToMainThread` 失败时静默丢弃**。调度通道失效（应用正在退出、消息窗口已销毁）时回调
  不会执行、也没有任何痕迹，自动化验证里表现为"设置了但没生效"的**假通过**。现在
  `IPlatformBackend.PostToMainThread` / `IWindowBackend.PostToUiThread` 返回 `bool`，失败时返回
  `false` 并报一条诊断；公开 API 侧 `WebviewWindow.PostToUiThread` 随之返回 `bool`，并新增
  `OrielApp.TryPostToMainThread`（不抛异常的"尽力投递"，退出途中用）。

### 新增

- **退出生命周期事件**：`OrielApp.Exiting`（消息循环即将/已经结束）、`ExitCompleted`（库侧清理
  走完、`Run()` 即将返回）、`ExitTimeout`（清理超时、进程即将被强杀）。此前宿主**无从观测**
  退出走到了哪一步——进程到底卡在哪、清理有没有走完、是不是被兜底强杀，只能靠自建看门狗 +
  同步日志才看得清。`ExitTimeout` 尤其关键：它让宿主知道"这次是被强杀的"，而不是静默变成
  AppHangB1（回调在**看门狗线程**上，只该做与 UI 无关的事并尽快返回）。
- **`OrielApp.IsOnUiThread` / `WebviewWindow.IsOnUiThread`**：公开"当前线程是不是 UI 线程"。
  此前只有 internal 的 `IPlatformBackend.IsOnUiThread`，宿主想判断"要不要编组"就得自己维护一份
  主线程 ID。应用未运行时返回 `false`。
- **`OrielApp.InvokeOnUiThread` / `WebviewWindow.InvokeOnUiThread`**：**同步**编组——已在 UI 线程
  则直接执行，否则投递并阻塞等待完成，动作抛的异常原样带回调用方。与 `PostToMainThread`
  （异步、不等待）配对。文档写明"不要在 UI 线程上等一个正在等 UI 线程的东西"。
- **`OrielApp.ExitTimeoutDuration` / `ForceExitCode`**，以及构建器上的
  `UseExitTimeout(TimeSpan)` / `UseForceExitCode(int)` / `UseExitWatchdog(bool)`。
- **`OrielApp.IsExiting`**：退出流程是否已开始。**这是本次实现过程中自己踩出来的一条**——
  `InvokeOnUiThread` 在退出途中会永久阻塞（消息循环已停，投递的动作永不被处理，而它在
  `Wait()` 上无超时），现在退出已开始就立刻抛 `InvalidOperationException` 而不是挂住调用方。

### 变更（破坏性 / 行为可见）

- **`IPlatformBackend.PostToMainThread` 与 `IWindowBackend.PostToUiThread` 的返回类型
  `void` → `bool`**（internal 接口，不影响宿主）。`WebviewWindow.PostToUiThread` 的签名随之
  由 `void` 变为 `bool`——**纯返回值增强**，`await`-之后-`Post` 那种"当作语句用"的调用方源码
  无需改动，但把方法组当 `Action<Action>` 用的反射/委托场景会受影响。
- **`OrielWindowOptions.Icon` 不再因文件缺失抛异常**（改降级 + 诊断）。依赖"缺失即抛"来发现
  打包漏文件的宿主，请改用 `RequireIcon`。
- **`Quit()` 在非 UI 线程调用由"静默无效"改为抛 `InvalidOperationException`**。
- **`OrielTrayOptions.IconPath` / `OrielNotificationOptions.IconPath` 由自动属性改为按
  `AppContext.BaseDirectory` 解析的受控 setter**——写入相对路径时读回的是解析后的绝对路径。

### 文档

- API.md §1 新增**退出语义**专节（`Quit()` 的三件事、看门狗为什么必须有、三个退出事件、
  只能从 UI 线程调用）；`OrielApp` / `WebviewWindow` 成员表补齐 `IsOnUiThread` /
  `TryPostToMainThread` / `InvokeOnUiThread` / 退出事件与配置项。
- API.md §2 新增**图标与路径**小节：相对路径的解析基准（`AppContext.BaseDirectory`）与
  "缺失即降级"两条规则，适用全部路径类选项。
- API.md 线程模型一节补**线程归属的可查询与编组**对照表，并把线程事实写实（Win32 用
  `GetCurrentThreadId == _uiThreadId`、Linux/macOS 用 `Environment.CurrentManagedThreadId`；
  命令处理、窗口事件、托盘菜单回调都在 UI 线程上，Windows 托盘回调经专属消息窗口与 WebView2
  共用同一个 dispatcher/消息循环线程）。
- API.md §14 补两条取证项：`--selftest exit` / `exit-timeout`（退出语义，必须子进程取证——
  `Environment.Exit` 会把测试运行器一起带走）与 `--selftest threading`（线程归属与编组）。
- ROADMAP 增两条待真机项（Linux/macOS 的退出看门狗与线程归属；Windows 已取证），
  窗口图标条目补上"缺失降级 + 相对路径解析基准"的 Windows 真机取证记录。

### 验证

- 单测 471 → **495**（新增 `IconOptionsTests`、`ExitSemanticsTests`）。
- demo 新增两个自检模式：`--selftest exit` / `exit-timeout` / `threading`，
  并在 Windows 真机取证通过——
  `EXIT-SELFTEST: PASS`（两个事件都到、退出码 0）；`exit-timeout` 在 `Run()` 返回后堵死 UI 线程
  ⇒ 看门狗 3 秒后强杀、**退出码 2**、`ExitTimeout` 先于强杀触发并指出卡住的阶段；
  `THREADING-SELFTEST: PASS`（8 项）。
- 相对路径解析基准的决定性取证：exe 目录放 `probe-icon.ico`、cwd 设为 `E:\`、
  开 `--require-icon` 严格模式 ⇒ 程序仍正常启动（退出码 0）。旧实现必然在此抛
  `FileNotFoundException` 而"双击没反应"；同文件缺失 + 严格模式则如期抛异常。

### 托盘 API 对标 Tauri 2（同批第二段）

上游明确要求"对标 Tauri 2 实现，后端与前端的 API 尽量保持一致"，因此托盘从"单托盘 + 路径字符串 +
无参点击事件"整体升级到 Tauri 2 的能力面。**这是一批破坏性变更**，详见下方"变更"。

#### 新增

- **多托盘，按 id 管理**（对标 Tauri 2 的 `TrayIcon.new` / `getById` / `removeById`）。
  `OrielApp` 持有托盘注册表：`CreateTray(Action<OrielTrayOptions>?)`（运行期新建）、
  `GetTray(id)`、`Trays`、`RemoveTray(id)` / `RemoveTray()`；构建期的 `AddTray` **可重复调用**。
  三平台后端从"单例"改为按 id 登记（Windows 每个实例占一个进程内唯一的 `NOTIFYICONDATA.uID`、
  Linux 按 `GtkStatusIcon` 指针、macOS 各持一个 `NSStatusItem`）。重复 id 抛异常——
  否则 `GetTray` 的结果取决于创建顺序，那是静默的意外。
- **`OrielImage`：托管解码的图像对象**（对标 Tauri 2 的 `tauri::image::Image`）。
  `FromBytes` / `FromFile` / `TryFromFile` + `Rgba` / `Width` / `Height`。
  解码器 `OrielImageCodec` 是**纯 BCL 实现**（PNG 走 `ZLibStream`），**不引入任何第三方图像库**——
  Tauri 靠 Rust `image` crate + `image-png`/`image-ico` 两个 Cargo feature，本库的定位是零额外依赖。
  支持 PNG 8 位的五种颜色类型与**全部五种行过滤器**、ICO 的内嵌 PNG/32 位 BMP/24 位/索引色；
  隔行扫描、16 位每通道、APNG、压缩 ICO **明确报错**而不是解出花屏。
- **图标从"路径"升级为"RGBA 对象"**：`OrielTrayOptions.Icon` / `OrielTray.SetIcon(OrielImage?)`。
  三平台各自落到原生入口——Windows `CreateDIBSection` + `CreateIconIndirect`、
  macOS `CGImageCreate` + `NSImage`（自动做预乘 alpha）、Linux `gdk_pixbuf_new_from_data` +
  `gtk_status_icon_set_from_pixbuf`。**都不引入新的原生依赖**（GDI/CoreGraphics/gdk-pixbuf 都是系统自带）。
  副作用是**格式支持三平台一致**：Windows 此前走 `LoadImageW`（只认 ICO/BMP），**PNG 会静默失败**，
  现在也能吃 PNG。
- **富事件模型 `OrielTrayEvent`**（对标 Tauri 2 的 `TrayIconEvent`）：`TrayId` / `Type`
  （`Click` / `DoubleClick` / `Enter` / `Move` / `Leave`）/ `Button` / `ButtonState` / `Position` / `Rect`。
  **平台未提供的字段一律是 `null`**，让页面能区分"平台不支持"与"值是 0"。新增
  `OrielTray.TrayEvent` 事件（旧的 `Clicked` 保留，两者同一次交互都触发）。
- **`OrielTrayOptions.Title` / `IconAsTemplate`**（对标 Tauri 2 的 `title` / `iconAsTemplate`）。
  `IconAsTemplate` **默认 `true`**（单色剪影跟随菜单栏反色），但**彩色品牌图可以显式置 false**——
  这是修掉的一个真实缺陷：模板标记此前被**无条件**设为 true，调用方无从表达"我这个是彩色图"，
  结果是颜色全丢、只剩一个按 alpha 抠出来的剪影。
- **前端 `oriel.tray` 命名空间**（对标 Tauri 2 的 `@tauri-apps/api/tray`）：
  `new` / `getById` / `getAll` / `removeById` / `onEvent`，句柄上 `setIcon` / `setMenu` /
  `setTooltip` / `setTitle` / `setVisible` / `setIconAsTemplate` / `close`。方法名与语义**一一对齐**。
  底层经内建 `tray.*` 命令（与 `win.*` 同属库保留前缀、**始终放行**，应用不必注册任何命令）。
  托盘事件经出站事件 `tray.event` 推给页面，**与通用 `oriel.on` 分开**（只关心托盘的页面不被
  窗口状态事件打扰）。

#### 变更（破坏性）

- **`ITrayBackend` 契约重写**：新增 `Id`、`EventReceived`、`SetTitle`、`SetIconAsTemplate`；
  `SetIcon` 由 `string?` 改为 `OrielImage?`；`CreateTray` 多一个 `id` 参数。
  （internal 接口，不影响宿主。）
- **`OrielTrayOptions.IconPath` 语义变更**：赋值**不再读盘**（解码推迟到建托盘时）。
  理由与 0.3.5 前面那批同源——`AddTray` 是**配置期**回调，在这里抛异常会重演
  "图标缺失让程序打不开"。解码失败降级为平台默认图标 + 报诊断；要"路径写错就立刻失败"
  请直接用 `Icon = OrielImage.FromFile(path)`（显式调用，抛异常是预期行为）。
- **`OrielApp.Tray` / `RemoveTray()` 的语义**：`Tray` 现在是"主托盘"（第一个），
  多托盘应用请用 `GetTray` / `Trays`；`RemoveTray()` 从"移除那一个"变成"移除**全部**"，
  按 id 移除用 `RemoveTray(id)`。
- **内建命令的异常文案改为原样透传**。此前 `win.*` 的失败被净化成"命令 'x' 执行失败。"，
  排查时看不到真因（例如"应用尚未运行"）。内建命令全部由库自己实现、异常里不会有应用/页面
  塞进去的内容，因此原样透传；**应用命令仍是"只透传 `OrielIpcException`"**（那条安全口径不变）。
- **`tray.` 成为库保留前缀**：应用自己的命令不要以它开头，否则会绕过能力配置（与 `win.` 同理）。

#### Windows：托盘回调切换到 V4

`NIM_SETVERSION(NOTIFYICON_VERSION_4)`（MSDN：必须在**每次 `NIM_ADD` 之后**调用）。
这是拿到 **Enter/Leave** 的唯一途径（`NIN_POPUPOPEN` / `NIN_POPUPCLOSE`），并带来事件坐标
（`GET_X_LPARAM`/`GET_Y_LPARAM`）；图标 ID 移到 `HIWORD(lParam)`，多托盘照样分得清。

早年"刻意不切 V4"的三条理由现在都不成立，逐条核实过：
① 旧式"简单可靠"不再优先——事件面完整性更重要；
② `uTimeoutOrVersion` 联合体的坑——本库的通知**早已不走托盘气球**（改走 WinRT toast，
见 `Win32ToastNotification`），没有任何调用点会写 `uTimeout`，该字段语义恒定是"版本号"；
③ 多托盘靠 `HIWORD` 的图标 ID 分辨，V4 同样支持（MSDN 明确图标 ID 限制 16 位）。

切换失败时**不致命**：回调退回旧式形态（点击与菜单照常，只是没有 Enter/Leave 与坐标），并报一条诊断。

#### macOS：悬停事件走 `NSTrackingArea`

状态项按钮上装跟踪区域（`NSTrackingMouseEnteredAndExited | NSTrackingMouseMoved |
NSTrackingActiveAlways | NSTrackingInVisibleRect`），AppKit 才会派发 `mouseEntered:` /
`mouseExited:` / `mouseMoved:`。**owner 必须是本托盘自己的 handler**——多托盘下共用一个会让
事件串到别的托盘上。按键与坐标**如实不给**（`NSStatusItem` 的 action 不带按键；菜单栏窗口坐标系
的转换成本高于收益），而不是编造一个值。

#### Linux：Click 补上按键与坐标

`GtkStatusIcon` **没有** Enter/Leave/Move 信号（权威信号表只有 `activate` / `button-press-event` /
`button-release-event` / `popup-menu` / `query-tooltip` / `scroll-event` / `size-changed`）——
这是平台事实，Tauri 2 的文档同样标注 "Linux: Unsupported"。
但 `button-press/release-event` 能给出**按键与坐标**（`GdkEventButton` + `gdk_event_get_root_coords`），
因此 Click/DoubleClick 的 `button` / `buttonState` / `position` 与另外两平台对等。
`SetTitle` 落到 `gtk_status_icon_set_title`，但**语义与 Tauri 的 Linux title 不同**（GTK 文档：
它是无障碍标签，不会画在托盘上）——如实标注这条差异。

#### 文档

- API.md §7 托盘章节整体重写：后端成员表、托盘注册表（多托盘）、`OrielTrayEvent` 字段表、
  **各平台事件粒度对照表**、**前端 `oriel.tray` 与 Tauri 2 的完整对照表**，
  以及"差别如实标注"（页面侧给路径而非 `JsImage` 多态、没有 `setTempDirPath`）。
- API.md §2 新增 **`OrielImage` 小节**：构造入口、支持与不支持的图像子集、三平台的原生落点。
- API.md §11 页面侧 API 补 `oriel.tray` 与托盘事件 payload 形状。
- API.md §14 补两条取证项（`--selftest tray` / `tray-js`）。
- ROADMAP 增三条待真机项：Linux/macOS 的事件送达、Windows 的 PNG 图标显示
  （Windows 侧已取证，其余**未在真机跑过**）。

#### 验证

- 单测 495 → **548**（新增 `ImageCodecTests`（含五种行过滤器的逐字节往返、真实 PNG/ICO 解码）、
  `TrayEventTests`、`TrayOptionsTests`、`BuiltInTrayCommandTests`）。
- 桥接测试 81 → **120**（新增 39 项托盘前端用例：`new` 默认值、句柄方法逐条命令、
  `close` 等价 `removeById`、`getById` 返回 null、事件隔离与处理器异常隔离）。
- demo 新增两个自检模式并在 **Windows 真机取证通过**：
  `TRAY-SELFTEST: PASS`（26 项，含 `app.png` 与 `app.ico` **双格式解码**、多托盘互不影响、
  模板标记如实回读、按 id 移除、重复 id 抛异常）；`TRAY-JS-SELFTEST: PASS`
  （页面经 `oriel.tray` 走完 `new` → `getById` → `getAll` → 全部 `set*` → `close` → `onEvent` 的完整往返）。
- 同批的退出/线程自检复跑无回归（6 项真机自检全 PASS）。
- `app.ico` 此前**没被复制到产物**（csproj 只配了 `app.png`）——自检把它暴露出来后已修，
  现在 PNG 与 ICO 都随产物分发（托盘/窗口图标需要可读的文件路径）。

#### CI：新增自检全部接入（并因此暴露一个缺陷）

上表新增的 `threading` / `tray` / `tray-js` / `exit` / `exit-timeout` 五项此前**一个都没进 CI**
（`smoke-linux` 与 `smoke-windows` 的模式列表停在六个旧模式，`verify-macos.sh` 同理）。
现在三个平台各自接入，并按平台差异给出**有依据的**断言口径：

- **`exit-timeout` 必须同时断言结论行与退出码**。只看结论行会漏掉"看门狗没真的强杀"
  （进程正常退出了）；只看退出码会漏掉"事件没触发"。
- **`exit` 的"退出码必须为 0"只在 Linux/Windows 上判**。macOS 的正常退出走 AppKit 的
  `terminate`，实测会吃掉 `Main` 里设的 `Environment.ExitCode`（`verify-macos.sh` 自己的注释里
  记录过"nav 自检打印 FAIL 却返回 0"）——在那里断言它会引入一个永远红的假失败，而那不是真实缺陷。
  `exit-timeout` 的退出码在三平台都判：它由 `Environment.Exit` 直接终止进程，不经过平台 UI 层。

**接进 CI 的过程中暴露了一个真实缺陷**：`--selftest exit-timeout` **从不打印结论行**——
超时模式的进程紧接着就被 `Environment.Exit(2)` 强杀，而打印结论的代码写在 `Run()` 返回之后，
**永远执行不到**。早先在 Windows 上只验证了退出码（2），所以没暴露；但 CI 按结论行判定就会永远红。
现在结论行在 `ExitTimeout` 回调里打印——那是强杀前**最后的执行机会**，也正是这个事件存在的意义
（让宿主知道"这次是被强杀的"，而不是静默消失）。

复刻验证：在 Linux（WSL，AOT 产物）与 Windows（AOT 产物）上分别按 `ci.yml` 的断言逻辑逐项复跑
五项新自检，**全部 PASS**（含退出码 2 的强杀断言）。这同时是 `tray` / `tray-js` **首次在 Linux 上跑通**
（此前只有 Windows 有实证）。

#### CI：大段内联脚本抽成独立文件，且三平台命名与职责对齐

`ci.yml` 里 Linux 的验证（约 190 行 bash，含自检 + 托盘通知 + 运行取证）与 Windows 的
（约 180 行 PowerShell，含冒烟 + 打包 + 自检）原本**内联在 workflow 里**。这种形态有三个实际问题：
**没法在本地跑**（只能靠 push 到 CI 试）、**没法单独 review**、diff 时也看不出改的是哪一段逻辑。
macOS 那条一直是独立脚本（`tools/verify-macos.sh`），现在三平台对齐：

| 脚本 | 语言 | 阶段 |
|---|---|---|
| `tools/verify-linux.sh` | bash | `selftest` + `shell` + `smoke` |
| `tools/verify-windows.ps1` | PowerShell | `smoke` + `pack` + `selftest` |
| `tools/verify-macos.sh` | bash | 自检 + 运行取证 + 截图（含签名与 `.app` 组装） |

**命名与职责一一对应**：每个平台一个 `verify-<平台>.<扩展名>`，内部按阶段分节，
CI 里各自只调用一行。三者都支持只跑某一阶段（`--stages` / `-Stages`）与"是否先发布"
（`--no-publish` / `-Publish`），失败时逐阶段给结论 + 写一条 GitHub 注解。
workflow 里现在只剩"调用一行脚本"（外加一条 13 行的跨平台 lint，那是通用检查、
与平台验证无关，留在原处更直观）。

> 中途一度按"每平台多个脚本"拆过（`verify-linux-selftest.sh` / `-shell.sh` /
> `verify-windows-selftest.ps1` / `-pack.ps1`），随后按"命名与职责要对齐"的要求合并回
> 每平台一个——拆开的名字与 macOS 那条对不上，而"哪个平台该跑什么"要跨文件去拼。

另有一个**与平台无关**的脚本改名以免歧义：`verify-pack.ps1` → **`verify-package.ps1`**
（它验证的是 NuGet 包内 build logic 的消费链路，在 `test` job 跑，不属于 `verify-<平台>` 这一类；
原名里的 "pack" 容易被读成"打包"，与 Windows 的 Velopack 打包阶段混淆）。引用点已同步
（`ci.yml` / `README.md` / `API.md` / 脚本自身的 usage）。

几个刻意的设计点：

- **Linux 脚本自己处理无头环境**：没有 `DISPLAY`/`WAYLAND_DISPLAY` 且装了 `xvfb-run` 时
  **重新执行自己**（带重入哨兵防止递归套娃），这样本地与 CI 的调用方式一致。
- **`shell` 阶段自己起 `dbus-run-session`**：通知服务与 demo 必须在**同一个会话总线**里，
  否则 demo 的 `notify-send` 找不到那个假服务。这是合并三个脚本时最容易出错的一处。
- **打包与断言分开**：没有复用 `tools/publish.ps1 -Bundle`——那个面向人工发布
  （含 `-Zip` / `-NoClean` / 交互式 `Pause`），塞进 CI 特有的断言会让它同时承担两个职责。
- **三个脚本都经实测**：Windows 的三个阶段、Linux 的三个阶段都在本地实跑通过（退出码 0）；
  另做**反向验证**确认断言不是假绿灯（未知 `--stages` → 退出码 2、喂坏产物 → 退出码 2、
  喂多余文件 → 打包断言报错）。

过程中踩到并修掉的两个真实问题（都不是语法层面，只有实跑才暴露）：

1. **PowerShell 函数返回值被污染**：`Invoke-SelftestMode` 里既有 `Write-Output`（打印日志）
   又有 `return [pscustomobject]`——PowerShell 会把**所有未捕获的输出并入返回值**，
   于是调用方拿到数组而非对象，症状是"在此对象上找不到属性 'Verdict'"。
   改用 `Write-Host`（走信息流、不进管道）。
2. **用"起点关键字 + 终点关键字"做区间替换时终点越界**，一次把整个 `smoke-windows` job 删掉了
   （241 行）。已从 git 恢复，并改为**精确行号区间 + 逐条锚点校验**的替换脚本
   （任一锚点不符就中止，宁可报错也不静默删掉一段）。

## 0.3.4 - 2026-10-12

Windows 组合宿主下"隐藏窗口仍吃点击"的修复批次。**宿主侧针对 0.3.3 的临时兜底（按类名 `Chrome_WidgetWin_1` 隐藏残留顶层窗口、并在 `Show()` 时对称还原）可以整段删除**——本版起 `Hide()` / `Show()` 已覆盖全部承担呈现与命中测试的窗口。

### 修复

- **Windows：`Hide()` 之后 WebView2 渲染窗口仍浮在桌面上吃点击**。本窗口走 `CoreWebView2CompositionController`，WebView2 是 DirectComposition 合成树里的一个视觉，其浏览器 HWND（类名 `Chrome_WidgetWin_1`，属主 `msedgewebview2.exe`）是一个**顶层窗口**、**不是**宿主 HWND 的子窗口——对宿主发 `SW_HIDE` 带不走它，它会继续参与合成与命中测试，把整块区域的鼠标点击全部吃掉。现场表现为"点桌面没反应、点图标无反应"，极易被误判成桌面/系统故障（实测：宿主 `Process.MainWindowHandle = 0`、`explorer.exe` 正常、桌面图标未隐藏，`WindowFromPoint` 四个探测点全部命中那个残余窗口）。现在 `Hide()` / `Show()` 一并驱动 `ICoreWebView2Controller.IsVisible`（隐藏时先置 `false` 再 `SW_HIDE` 宿主；显示时先 `SW_SHOW` 宿主再置 `true`），顺带停掉 Chromium 合成、省一份全屏 GPU 开销；**不引入任何新的原生依赖**，`WithFrameless` / `WithHidden` / `WithMaximized` 调用方无需改动。
- **Windows：`WithHidden()` 首帧闸门期间同样在吃点击**。隐藏启动时窗口在 `PostCreate` 就 `SW_HIDE` 了，但 WebView2 装配完成后原本**无条件**把控制器置为可见——于是闸门期间（等前端 `ui.ready`，可达 8 秒）那个顶层渲染窗口就已经浮在桌面上吃点击，"程序还没显示出来就已经在吃点击"。现在装配按**可见性意图**恢复，闸门由宿主 `Show()` 打开。

### 新增

- **`WebviewWindow.IsVisible { get; }`**（以及内建 `win.isVisible` 命令）。此前宿主没有任何手段确认窗口"真被藏住了"——上面的缺陷只能靠外部 `WindowFromPoint` 探测才看得见。三平台语义一致：`Hide()` 之后为 `false`，**最小化仍算显示中**（不清可见位）。Windows 用 `IsWindowVisible`、Linux 用 `gtk_widget_get_visible`、macOS 用 `NSWindow.isVisible`。纯新增，向后兼容。

### 文档

- API.md §3 窗口成员表补 `IsVisible`；§9 内建命令表补 `win.isVisible`（内建里唯一的查询类命令）。
- 代码里写明一条**跨平台不变量**：`Show()` / `Hide()` 必须覆盖**全部**承担呈现与命中测试的表面，藏好后不留任何仍在参与合成与命中测试的残余窗口（见 `Win32WindowHost.Hide` 的 XML 文档），避免以后再退化。

## 0.3.3 - 2026-10-11

第三轮评审 P3 遗留里"值得做"的四条（2026-10-11 对着代码复核后判定：同批的另三条——ModuleInitializer 时序、托盘解析散落、`EnsureClassRegistered` 无锁——前提不成立，未动代码）。

### 修复

- **Windows：指针移出窗口后页面的悬停态滞留**。组合托管下 WebView 收不到任何系统输入，"指针离开"同样要宿主转发——此前 `WM_MOUSELEAVE` 只复位了内部跟踪标志、**没有**把 `COREWEBVIEW2_MOUSE_EVENT_KIND_LEAVE` 送进 WebView，于是 `:hover` / tooltip 会停在被移出前的位置上，直到指针再次进入并移动。现在离开时按窗口内最后一个有效落点补发 LEAVE；失焦（`WM_KILLFOCUS`）**刻意不补**——失焦不等于指针离开，补了会凭空清掉页面状态。**待真机确认**（见 ROADMAP）。
- **macOS：关窗后仍在途的 `oriel://` 请求永不回执**。`AssetSchemeStart` 在"状态表里没有这个 handler"（窗口销毁时整表移除）与"请求取不出 URL"这两条路径上原本直接返回，引擎就在那条 scheme task 上一直等——eval 回环早有 `FailPendingEvals` 兜底，scheme 侧漏了同一手。现在两条路径都给出终结（`NSURLErrorCancelled` / `NSURLErrorBadURL`）；同时记住被 `stopURLSchemeTask:` 停过的 task，**对已停的 task 不再回执**（Apple 的契约，撞上会触发 WebKit 断言）。
- **Linux/macOS：资源"读失败"被报成"不存在"**。`oriel://` 读到表里存在、但流读不出来的资源时，Linux 归 303（`FILE_DOES_NOT_EXIST`）、macOS 归 -1100，排障会被告知"资源没打包"，真因只在诊断文本里。现在分开归类：读失败 → Linux 399（`FAILED`）、macOS `NSURLErrorCannotOpenFile`（-3001）；Windows 本来就是 500 / 404 分开的。
- **入站消息规模闸（1 MiB）**。三平台的入站路径都是「整串读进托管内存 → `JsonDocument.Parse`」，此前没有任何上限。现在超过 1 MiB 的入站消息**不解析即丢弃**，并经 `IpcRejected` 的新层级 `OrielIpcRejectionLayer.Size` 上报（页面侧收不到回执——`id` 就在那份被丢弃的 JSON 里）。出站方向不设闸：回执 JSON 由宿主自己构造。

### 文档

- API.md §4 增补**威胁模型**一段，把三条口径写实：入站 1 MiB 上限、**没有取消令牌**（`invoke` / `EvaluateJs` 发出去只能等，页面侧 30 秒超时是唯一出路；命令函数也拿不到取消信号）、"可信页面 DoS 不在防线内"。
- API.md 的资源行写明：**不设任何缓存头**（没有 `Cache-Control`/`ETag`/`Last-Modified`，缓存行为由引擎默认策略决定，不是契约）；以及"读失败"与"不存在"的分平台归类。
- ROADMAP 增两条已知缺口（取消令牌、显式缓存头——Linux 的 `webkit_uri_scheme_request_finish` 没有响应头参数，加不了）与一条待真机项（Windows hover 复位）。

## 0.3.2 - 2026-10-11

下游反馈（一个真实工业上位机的迁移复盘）后的修复批次。

### 新增

- **`OrielWindowOptions.BackgroundColor` / `WithBackgroundColor(uint argb)`（以及 `(byte r, byte g, byte b)` 重载）**：页面渲染出来之前那一帧的**底色**，用来消掉所有 webview 应用都有的启动闪色（工业界面尤其刺眼）。三平台各落到引擎的等价开关——Windows `DefaultBackgroundColor`、macOS 窗口底色 + `WKWebView.underPageBackgroundColor`（12+）、Linux `webkit_web_view_set_background_color`。Windows 路径已真机验证（探针窗口实测渲染成设定的深色底）。
- **`OrielDiagnostics.Reported`（库内诊断通道）**：进程级事件，库自己的告警与失败留痕都走它（IPC 被拒、shell 路径被拒、单实例锁不可用、托盘/通知失败、原生 trampoline 兜底异常……）。它取代了 `Debug.WriteLine`——后者带 `[Conditional("DEBUG")]`、条件在**调用方程序集的编译期**求值，于是发布包里这些调用**根本不存在**（实测 0.3.1 的 Release 程序集里 `[OrielWeb]` 命中 0）。无订阅者时不构造参数、不分配；有订阅者时逐个调用并各自兜异常。**全库 74 处调用点已迁移。**
- **`OrielApp.CommandInvoked` / `CommandFailed`**：应用命令的执行观测——命令名、耗时、成功与否（前者），失败时异常**原样**带出（后者，只给宿主，页面拿到的仍是净化文案）。内建 `win.*` 不计入（拖动期间高频，会淹没"哪条命令慢"）。
- **`OrielWindowOptions.Zoom` / `WithZoom(bool = true)`**：关掉用户手势缩放（Ctrl/Cmd + 滚轮、Ctrl/Cmd + 加减号/0、触摸板捏合）。宿主侧设引擎专有开关（Windows `IsZoomControlEnabled` + `IsPinchZoomEnabled`、macOS `allowsMagnification`），文档层再由桥接脚本做捕获期拦截——**Linux 的 WebKitGTK 没有等价单开关，靠的正是那道拦截**。
- **`OrielApp.IsSecondaryInstance`**：`Build()` 之后即可查询"本实例是不是第二个实例"。

### 变更（破坏性 / 行为可见）

- **单实例判定从 `Run()` 提前到 `Build()`**：宿主可以在装配配置/数据库/设备连接**之前**分流第二实例（此前第二实例会先把这些副作用跑一遍）。代价是 `Build()` 不再纯构造（会抢一次进程级锁）；接收"激活请求"的管道仍在 `Run()` 建立，因此 `Build()` 与 `Run()` 之间到达的第二实例会被正确判定并退出，但它的激活请求送不进来。
- **`SingleInstance` 的 `onActivate` 签名**：`Action<WebviewWindow?>` → `Action<WebviewWindow?, IReadOnlyList<string>>`，第二实例的启动参数（`Oriel.CreateBuilder(args)` 的那份）随激活请求带回，用于"用命令行让已运行的程序打开某个页面"。

### 修复

- **[反馈 P1-6] `UseJsonContext` 的边界写清**：只有 POCO 参数/返回值真正需要它；命令用 `JsonElement` 收/发时生成器原样透传 payload，不必把 DTO 搬进上下文。
- **[反馈 P1-2 / P1-4] 两处已知取舍/边界写进 API.md**：Windows 边缘调整大小的厚度与生效条件（系统默认值、`Frameless && Resizable`、全屏/最大化不生效）；托盘气泡（`NIF_INFO`）**不做**的理由与后果（toast 可被"专注助手/权限"关掉，而"关闭即入托"的场景需要可见反馈）。
- **[反馈 P2-1] 内嵌资源名不再混用分隔符**：包内 `targets` 把 `%(RecursiveDir)` 归一成 `/`（Windows 上它带反斜杠，于是子目录资源名长成 `MyApp.wwwroot/assets\img\logo.svg` 这种混合形态）——资源名从此与 README 承诺的形状逐字符一致，`samples/OrielDemo` 的自声明写法同步，`tools/verify-pack.ps1` 新增两条**硬断言**（原始资源名不含反斜杠、自声明与零配置的原始形态逐字符一致）。此前那条比较会先把名字归一，等于放过了这个问题。
- **[反馈 P2-2 / P2-3 / P2-4 / P2-5] 文档补全**：`onCreated` 是配置期回调（访问 `App`/`Show` 会抛 `InvalidOperationException`，不是返回 null）；`win.` 只保留**命令名**段（事件名不受限）；13 个 `With*` 方法补 XML 文档，并说明 XML 文档包含 `internal` 成员；首帧闸门的推荐做法（`WithHidden` + 页面就绪消息 + 超时兜底）。

## 0.3.1 - 2026-10-08

第三轮全面评审后的修复批次。

### 新增

- **`OrielApp.IpcRejected` 事件**（`OrielIpcRejectedEventArgs`）：入站 IPC 被门禁拒绝时触发，带层级（`Source` / `Token` / `Command`）、原因、文档 URL 与命令名。Release 下拒绝原本只写 `Debug.WriteLine`，宿主无从观测；非 `invoke` 的消息被拒时页面也没有任何反馈，只有这个事件能看到。
- **源生成器编译期诊断 ORIELWEB101–106**：空白/含控制字符命令名、同程序集内同名命令、不可访问的命令方法、泛型宿主类、`ref`/`out` 参数此前要么被静默吞掉、要么变成生成文件里定位不了的 CS 错误，现在全部在编译期指回用户方法；`win.` 保留前缀给出警告（应用同名命令会被内建静默遮蔽）。
- **逐消息来源纵深加固**（IPC 安全第二层）：Windows 用 `WebMessageReceived.Source`、macOS 用 `frameInfo.securityOrigin`（10.15+，缺失时优雅回退）判定发消息文档的真实来源，顶级导航 URL 快照降级为兜底。Linux 的经典 script-message 信号不带 frame 信息，保持快照判定（已知差距，注入期自检与令牌两层不受影响）。
- Linux/macOS 的 async IPC 命令 `await` 之后**回 UI 线程**：与 Windows 语义一致，命令里 `await` 后可直接操作窗口与平台对象。

### 变更（破坏性 / 行为可见）

- **窗口尺寸统一为逻辑像素**：Windows 此前把 `WithSize`/`WithMinSize` 当物理像素用，高分屏（150%/200%）上窗口比 Linux/macOS 小一半/三分之一；现在按窗口 DPI 折算，三平台语义一致（高分屏用户会看到窗口变大）。
- **macOS `MoveTo` 改为左上角原点**：此前用的是 cocoa 左下角坐标，与 API.md 的屏幕坐标契约（和另外两个平台）相反；`At(x, y)` 同时补齐 Linux/macOS 支持（此前仅 Windows）。
- `publish.ps1 -Bundle` 明确为 **Windows 打包脚本**：非 `win-*` RID 直接报错并指路（macOS 走 `release.yml` / `tools/make-macos-app.sh`，Linux 走 `tools/wsl_publish.ps1`）。
- `AllowOrigin` 归一化：自动补尾斜杠；裸 host（漏 scheme）、带 query/fragment 的前缀直接报错——缺尾斜杠时 `http://localhost:5173` 会逐字节命中 `http://localhost:5173.evil.com/`，形似域名即可获得完整桥接与令牌。

### 修复

- **[审核发现] Windows 窗口位置语义**：`At`/`MoveTo` 与尺寸统一按窗口 DPI 折算（与 `Resize`、另两个平台一致）——此前只有尺寸折算，150%/200% 屏上位置会偏。
- **[审核发现] Windows 创建期失败的收尾**：`Create` 中段抛异常时不再二次释放根引用（此前会把真正的失败原因顶成 `InvalidOperationException`），也不再对"应用从没见过的窗口"触发 `Closed` 与窗口计数（此前计数会被打成负数并误发退出消息）。
- **[P3 批次] 生命周期收尾与一致性**：Linux `ExecuteScriptAsync` 的投递闭包补 `_webview == 0` 判断；Linux/macOS 的窗口计数改在建窗**成功之后**自增（此前 `Create` 抛异常会让计数虚高、把"全关退出"的时点推早）；Win32 销毁时清零 `GWLP_USERDATA`（不再靠 WndProc 的兜底 catch 过日子）；`WindowsPlatformBackend.Dispose` 清空静态 `s_current`；`GetMessageW` 失败时不再静默退出消息循环；MIME 表补 `.m4a/.m4v/.aac/.flac/.heic/.avifs`。
- **[P3 批次] 参数与协议容错**：`UseEmbeddedAssets` 的 host 做字符校验（此前含空格/斜杠时症状是白屏且宿主无异常）；`gtk_window_get_position` 的签名改回 GTK3 的 `void`；回执层对非整数 `id` 不再让异常逃逸（此前那条 eval 会干等到 30 秒超时）；包内 targets 的 wwwroot 判定改为大小写不敏感（目录写成 `WWWRoot` 时不再漏报）。
- **[P1] IPC 异常净化**：只有 `OrielIpcException` 的 `Message` 原样回传页面，其余异常回通用文案（此前与 API.md 承诺不符，非受控异常的内部路径可被页面探测）。
- **[P1] Windows 默认（framed）窗口**：`WM_NCCALCSIZE` 不再压掉非客户区——系统标题栏/边框与原生边缘 resize 恢复（此前默认路径窗口无标题栏且无法拖边调整大小）。
- **[P1] Linux/macOS `EvaluateJs` 挂死**：窗口销毁或页面导航离开原文档时，挂起的 ExecuteScript 立即以明确异常失败，不再永久挂起。
- **[P1] Linux oriel:// 回调**：trampoline 全身异常保护——托管异常逃逸 GLib 即进程 fail-fast，现在兜底为该次资源请求失败。
- **[P1] 类库 wwwroot**：引用类库的 wwwroot 现在与入口程序集合并提供（同一相对路径入口优先）；此前运行期只扫入口程序集，类库资源静默 404。
- **[P2]** macOS 资源应答不再每请求泄漏一个 `NSHTTPURLResponse`；Win32 `Create` 中段失败回收 HWND/GCHandle；两平台窗口销毁时释放保留的上下文菜单；Linux 销毁后 `_gtkWindow` 清零并全入口守卫（迟到调用 no-op 而非悬空指针）；macOS/Linux 全屏状态的原生路径回灌（绿钮/ESC/WM 快捷键进出全屏后 `ToggleFullscreen` 行为不再颠倒）；`WM_KILLFOCUS` 不再反向塞焦点（IME/caret 错乱风险）。
- **[P2]** `release.yml`：手动触发旁路版本闸的洞已封（publish job 仅对 tag ref 执行），加 `concurrency: release`；CI 补 linux-arm64 交叉编译。

## 0.3.0 - 2026-10-03

### 破坏性

- **`oriel://` 自定义 scheme 取代文件系统方案**：内嵌资源不再解压落盘，三平台统一经 `oriel://<host>/` 提供（只读介质/沙箱可用、无陈旧目录残留）。`https://<host>/…` 作为兼容别名仅影响入参解析。
- **删除不带 `LogicalName` 的 `EmbeddedResource` 旧写法**：那种资源名无法区分"目录"与"含点文件名"（`app.min.js` 被推成 `app/min.js` → 白屏），构建期报 `ORIELWEB001`；零配置自动内嵌继续可用，自声明必须带显式 `/` 分隔符的 `LogicalName`。

## 0.2.0 - 2026-10-02

### 破坏性

- **移除库内剪贴板 API**（`feat!` 提交 `d000e05`）：页面经 `navigator.clipboard` 自理，库不再包一层。

### 变更

- 文档收敛：README / API.md / ROADMAP 与代码树重新对齐，API.md 各章改为真实签名引用。

## 0.1.8 - 2026-10-02

- Windows 托盘与通知的修复批次（tag 存在的最近一个 0.1.x 稳定点）。

## 0.1.3 - 0.1.7（2026-09-30 → 10-02，无 tag，按提交区间近似）

- NuGet 上有这几个版本，但仓库里没有对应 tag；提交区间内的主题：CLI 撤销（`65f0d80`）、自研打包器替换前的过渡修复、多轮文档拉齐。逐版本差异已不可靠，不再展开——这也是本文件与发布纪律存在的原因。
