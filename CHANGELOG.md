# 更新日志

本文件记录 OrielWeb 各版本的用户可感知变化。格式参照 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)。

**发布纪律**（2026-10-05 评审 P2-18 后成文）：

- 版本号唯一来源是根 `Directory.Build.props` 的 `<Version>`；tag 形如 `vX.Y.Z` 与之一一对应，`release.yml` 会校验一致性。
- **改版本的提交与打 tag 是同一次提交**（0.1.8 之后已是这个形态）。历史上 0.1.3–0.1.7 有发布/排障提交但没有对应 tag（0.1.5 被整体跳过），这些版本在仓库里已不可回溯—— NuGet 上仍可查，但源码状态只能靠提交区间近似还原。下表 0.1.3–0.1.7 的条目即按提交区间还原的近似记录。

## 未发布

Tauri 2 API 对齐的第一批（`docs/TAURI2-PLAN.md` 的批次 A + B + E1 测试先行）。
详细差异分析与分阶段计划见该文件。

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
  `getAllWindows` / `getByLabel` / `getFocusedWindow`（**恒 `null`**，见"已知限制"）+ `Window` 类。
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
- **`OrielApp.GetFocusedWindow()` / JS `getFocusedWindow()`**：签名已就位，但**目前恒返回 `null`**
  ——三平台都还没有"哪个窗口有焦点"的**查询**能力（`FocusChanged` 只上报变化、不记录状态）。
  保留它是为了让迁移代码能原样编译，而不是假装"没有窗口有焦点"。
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

### 新增（路径运算，F1）

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

### 修复

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

### 已知限制（未验证，不是未实现）

- **Windows 高 DPI 下的换算未实测**：本机只有一台 100% 缩放显示器。
  `AdjustWindowRectExForDpi` 按**窗口所在显示器**的 DPI 取值，逻辑上正确，
  但 125%/150%/200% 下的逐像素结果没有取证。需要时在有缩放显示器的机器上跑 `--selftest geometry`。
- **`--selftest geometry` / `windowevents` / `event` / `close` / `window` 尚未在 Linux / macOS 上跑过**：
  代码已通过这两个目标的交叉编译（`linux-x64` / `osx-arm64` / `osx-x64` 均 0 警告 0 错误），
  但运行期行为需要 CI 取证。尤其 Linux 的 `configure-event`、macOS 的 `windowDidMove:`
  与三平台的关闭接管路径都是本次新接的——"编译通过"不等于"事件真的到了"。
- **`getFocusedWindow()` 恒返回 `null`**：三平台都还没有"哪个窗口有焦点"的**查询**能力
  （宿主的 `FocusChanged` 只上报变化、不记录状态）。签名先就位是为了让迁移代码能原样编译。
- **`tauri://destroyed` 永远收不到**：窗口销毁时页面正在被拆掉，事件送不出去。
  需要"即将关闭"的通知请用 `tauri://close-requested`（其前提是宿主开了关闭接管）。
- **`tauri://scale-change` / `tauri://theme-changed` / `tauri://window-created` 未实现**。
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
