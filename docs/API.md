# OrielWeb API

按能力分章的完整说明。每章末尾的 **为什么** 是取舍记录——它解释的是"为什么不能换成另一种看起来更自然的写法"，
因为这些地方一旦被"优化"回去就会静默出错。

> **关于 IDE 里的 XML 文档**：生成的 XML 文档**包含 `internal` 成员**（例如
> `OrielCapabilityOptions.NormalizeOrigin`），照着它调用会得到 CS0117——那是库内部的实现说明，
> 不是公开 API。公开面以本文件与 IDE 补全里的 `public` 成员为准（下游反馈 2026-10-11 的 P2-4）。

- [1. 应用与窗口](#1-应用与窗口)
- [2. 无边框窗口](#2-无边框窗口)
- [3. 导航与页面通信](#3-导航与页面通信)
- [4. 安全与能力模型](#4-安全与能力模型)
- [5. 拖动与双击的实现边界](#5-拖动与双击的实现边界)
- [6. 系统主题与单实例](#6-系统主题与单实例)
- [7. 平台集成](#7-平台集成)
- [8. 对话框](#8-对话框)
- [9. 文件拖放](#9-文件拖放)
- [10. 内建右键菜单](#10-内建右键菜单)
  - [10.1 页面侧菜单](#101-页面侧菜单orielwebapimenu)
  - [10.2 菜单栏](#102-菜单栏setasappmenu--setaswindowmenu)
- [11. 页面侧 API](#11-页面侧-api)
- [12. 打包与分发（Velopack）](#12-打包与分发velopack)
- [13. 运行要求](#13-运行要求)
- [14. 验证与自检](#14-验证与自检)

---

## 1. 应用与窗口

### 入口

```csharp
Oriel.CreateBuilder(args)   // → OrielAppBuilder
    .UseEmbeddedAssets(host: "app.oriel", resourcePrefix: null)
    .UseJsonContext(AppJsonContext.Default)
    .AddCommands<TodoCommands>()
    .AddCommands<CounterCommands>(() => new CounterCommands(seed))
    .AddWindow(w => w.WithTitle("Demo").WithSize(1024, 720), win => win.Loaded += OnLoaded)
    .Run();                 // 或 .Build() 拿到 OrielApp 自己控制生命周期
```

所有方法都返回 builder 自身（可链式），失败方式一律是**抛异常**（参数错就立刻抛，不静默降级）。

| 方法（真实签名） | 说明 |
|---|---|
| `Oriel.CreateBuilder(string[]? args = null)` | 入口。`args` 只是透传给应用自己（库不解析命令行）；`Run()` 之外还有 `Build()` 拿 `OrielApp` |
| `UseEmbeddedAssets(string host = "app.oriel", string? resourcePrefix = null)` | 启用内嵌前端资源：程序集内嵌资源经自定义 scheme `oriel://<host>/` 提供（三平台一致，**不写盘**；`https://<host>/…` 作为兼容别名也被接受）。默认前缀是 `程序集名.wwwroot/`（显式 `/` 分隔符）。自己声明资源时必须用同一种形式：不带 `LogicalName` 的旧写法已删除，构建期报 `ORIELWEB001`（见 README 的"内嵌页面资源"）。`host` 只接受字母、数字、`-`、`_`、`.`（会被拼进页面来源并逐字节参与可信前缀判定，其他字符在构建期直接抛）。**已知边界**：引用类库的 wwwroot 会与入口合并提供（同路径入口优先）；每请求整份驻留内存且不支持 Range——wwwroot 只放小静态资源，媒体类不可用；**不设任何缓存头**（没有 `Cache-Control`/`ETag`/`Last-Modified`，"缓存还是不缓存"完全由引擎的默认策略决定，别把它当契约）；资源不存在时 Windows 回真实 HTTP 404（导航成功），Linux/macOS 按导航失败上报（引擎能力差异）；**读失败**（资源在表里但读不出来）三平台都按失败上报，且与"不存在"分开归类（Windows 500 / Linux 399 / macOS `NSURLErrorCannotOpenFile`），排障时不会被引向"资源没打包"；路径只做**一次** URL 解码，字面含 `%` 的文件名（URL 里得写成 `%2520` 之类）实际按解码后的名字查找（`%2520 ≡ %20`）；Linux 的 `oriel://` 处理器是**进程级单例**（scheme 每进程只能注册一次），同进程并发两个不同 host 的应用会互踩——按单进程单应用使用 |
| `UseJsonContext(JsonSerializerContext context)` | 注册 STJ 源生成上下文：DTO 命令参数/返回值的 AOT 安全序列化入口。**按应用实例持有**，不写全局静态状态。**边界**：只有 POCO 参数/返回值真正需要它——命令用 `JsonElement` 收/发时生成器直接透传整个 payload（见 §4 末"逃生口"），不必把 DTO 搬进上下文。需要 `JsonSerializerOptions` 的非 AOT 用法**没有重载**（库面向 AOT，不做反射序列化） |
| `AddCommands<T>() where T : new()`<br>`AddCommands<T>(Func<T> factory)` | 注册含 `[OrielCommand]` 的类型（惰性单例；`factory` 用于需要构造参数的命令类）。命令实例**共享**，所有 invoke 作用在同一实例上 → **命令方法必须线程安全**；同名命令在启动时报冲突 |
| `AddWindow(Action<OrielWindowOptions>? configure = null)`<br>`AddWindow(Action<OrielWindowOptions>? configure, Action<WebviewWindow> onCreated)` | 加窗口。`onCreated` 是**配置期**回调（`Run()` 之前、建窗之前调用），用途只有一个：挂事件处理器（`Loaded`/`Closing` 这类必须早于建窗订阅的）。此时窗口后端还没建，访问 `window.App` / `PostToUiThread` / `Show` / `Hide` / `EmitEvent` 一律**抛 `InvalidOperationException`**（"窗口后端尚未初始化"）——**不是返回 null**；要窗口能力请等 `Loaded` / `NavigationCompleted` |
| `UseDebug(bool debug = true)`；`bool Debug { get; }` | 打开 DevTools（三平台的实现与前置条件见 §1 末与 §13） |
| `UseUserDataFolder(string path)` | 覆盖 WebView2 用户数据目录（默认 `%LOCALAPPDATA%\OrielWeb\WebView2`）。**同一进程的所有窗口共享一个 WebView2 环境**——否则第二个窗口起不来 |
| `SingleInstance(string id, Action<WebviewWindow?, IReadOnlyList<string>>? onActivate = null)` | 单实例（见 §6，含三态判定与运行期目录）。**判定发生在 `Build()`**：`app.IsSecondaryInstance` 在 `Run()` 之前就能问，第二实例因此可以在打开数据库/连设备**之前**分流，再 `Run()` 让库负责"通知首实例并静默退出"。`onActivate` 的首实例回调会拿到**第二实例的启动参数**（`Oriel.CreateBuilder(args)` 的那份），用于"用命令行让已运行的程序打开某个页面" |
| `UseCapabilities(Action<OrielCapabilityOptions>? configure = null)` | 声明页面可调用的命令与可信来源（见 §4） |
| `UseShell(Action<OrielShellOptions>? configure = null)` | Shell 集成的 scheme 白名单（见 §7） |
| `AddTray(Action<OrielTrayOptions>? configure = null)` | 配置系统托盘（**可重复调用**以创建多个；见 §7） |
| `UseAutoStartId(string id)` | 覆盖开机自启的标识（默认取可执行文件名；同一标识在多份构建之间会串，必须显式分开） |
| `UseNotificationAppId(string appId)` | 覆盖系统通知的应用标识（Windows AUMID / Linux `--app-name`；默认取入口程序集名） |
| `OnWebView2RuntimeMissing(Action<OrielWebView2RuntimeMissingEventArgs> handler)` | 自定义"运行时缺失"的提示与引导（不注册时库弹默认错误框；事件参数里有下载地址） |
| `Build()` → `OrielApp`；`Run()` | `Run()` = `Build().Run()`；`Build()` 让你自己控制生命周期（配合 `OrielApp.Quit()`） |

### 窗口选项（`OrielWindowOptions`）

`AddWindow` 的 `configure` 里链式调用；也可以直接设属性。**左边是默认值**：

| 属性 | 默认 | 链式写法 |
|---|---|---|
| `bool InterceptClose` | `false` | `WithInterceptClose(bool = true)`。**让页面接管关闭**：用户点关闭时推一个 `close-requested` 事件给页面，窗口**先不销毁**；页面同意就调 `WebviewWindow.Destroy()`，不同意就什么都不做。对标 Tauri 2 的 `onCloseRequested` + `preventDefault`。**宿主的 `Closing` 优先**（置了 `Cancel` 就不再问页面）；**页面加载完成前不生效**（事件送不到未加载的页面，挂起只会让窗口变成点 X 没反应） |
| `OrielWindowEventKind EventForwarding` | `None` | 哪几类宿主事件**转发给页面**（`Resize` / `Move` / `Focus`）。**默认全关**：尺寸与位置事件在拖动时每个像素来一次（已按 0.5 逻辑像素去重，但一次跨屏拖动仍有上千条），而推给页面在 macOS/Linux 上是一次 JS 求值。JS 包在页面 `listen` 到对应事件名时会自动打开它（见 §11 与 `window.setEventForwarding`） |
| `string Label` | `"main"` | `WithLabel(string)`。**窗口的稳定标识**（对标 Tauri 2 的 `Window.label`），也是寻址单位：`OrielApp.GetWindow(label)` 按它取窗口。字符集限字母/数字/`-`/`.`/`_`，**自动归一小写**（三平台的大小写规则不同，不归一会出现"只在某个平台错配"）。**同一应用内必须唯一**，重复会在建窗时抛 `InvalidOperationException`——不会自动改名（自动改名会让 `emitTo('main', …)` 发到错误窗口且毫无症状）。页面侧读 `window.oriel.label` |
| `string Title` | `"Oriel"` | `WithTitle(string)` |
| `int Width` / `int Height` | `1000` / `700` | `WithSize(int, int)`。**逻辑像素的客户区**（三平台一致）：Windows 在 Per-Monitor-V2 下按窗口 DPI 折算，并把"客户区 → 整窗"换算掉（`AdjustWindowRectExForDpi`）；Linux/macOS 的原生 API 本就是客户区语义。**frameless 窗口不做这层换算**——它的客户区已铺满整窗 |
| `int? MinWidth` / `int? MinHeight` | `null`（不限制） | `WithMinSize(int, int)`（同上：逻辑像素的**客户区**下限；Windows 会换算成整窗的跟踪尺寸） |
| `int? X` / `int? Y` | `null` | `At(int, int)`（顺带把 `Center` 置 false）。窗口**左上角**落在屏幕逻辑坐标 (x,y)——三平台一致（Linux 在 map 前发出 move 请求，最终落点由窗口管理器确认） |
| `bool Center` | `true` | `Centered(bool = true)` |
| `bool Resizable` | `true` | `WithResizable(bool = true)` |
| `bool Fullscreen` / `bool OnTop` / `bool Maximized` | 均 `false` | `WithFullscreen()` / `WithOnTop()` / `WithMaximized()`（各带 `bool = true`） |
| `bool Frameless` | `false` | `WithFrameless(bool = true)`（无边框，见 §2） |
| `bool Hidden` | `false` | `WithHidden(bool = true)`（窗口不上屏，页面照常加载、IPC 照常往返） |
| `string? Url` | `null` | `WithUrl(string)`。`null` = 内嵌资源首页（`oriel://<host>/index.html`）；指向内嵌资源时写 `oriel://<host>/…`（`https://<host>/…` 是兼容别名，导航前会归一）。**已知边界**：别名只在**窗口初始 URL** 上归一；页面运行期自己跳到 `https://<host>/…` 不会被改写——那是真网络请求，且来源不可信（桥接不会安装） |
| `string? Icon` | `null` | `WithIcon(string path)`。`null` 时：Windows 从 exe 取图标，Linux/macOS 回落到随产物分发的 `app.png`。**相对路径按可执行文件所在目录解析**（不是 cwd）；**文件不存在时降级为平台默认图标 + 报一条诊断，不抛异常**（见下方"图标与路径"） |
| `bool RequireIcon` | `false` | `WithRequireIcon(bool = true)`。打开后图标文件缺失会在配置期抛 `FileNotFoundException`（而不是降级）——用于"打包漏文件必须立刻发现"的场景 |
| `bool Debug` | `false` | `WithDebug(bool = true)` |
| `bool ConsoleForwarding` | `false` | `WithConsoleForwarding(bool = true)`（页面 console → `ConsoleMessage` 事件） |
| `uint? BackgroundColor` | `null` | `WithBackgroundColor(uint argb)` / `WithBackgroundColor(byte r, byte g, byte b)`。页面渲染出来之前那一帧的**底色**（防白黑闪，工业界面尤其需要）。三平台各落到引擎的等价开关：Windows `DefaultBackgroundColor`、macOS 窗口底色 + `underPageBackgroundColor`（12+）、Linux `webkit_web_view_set_background_color`（已在 WSL 确认符号存在；该 API 自 2.40 起 deprecated 但未移除，行为待真机确认）。只管底色，页面 CSS 一生效就由页面说了算 |
| `bool Zoom` | `true` | `WithZoom(bool = true)`。关掉**用户手势缩放**（Ctrl/Cmd + 滚轮、Ctrl/Cmd + 加减号/0、触摸板捏合）。固定布局的工业界面通常要关：整屏被误缩放会布局错乱且没有复位入口。实现分两层——宿主侧设引擎专有开关（Windows `IsZoomControlEnabled`+`IsPinchZoomEnabled`、macOS `allowsMagnification`），文档层再由桥接脚本做捕获期拦截；**Linux 的 WebKitGTK 没有等价单开关，完全靠文档层那道拦截** |
| `OrielContextMenuPolicy ContextMenuPolicy` | `Editing` | `WithContextMenuPolicy(OrielContextMenuPolicy)`（见 §10） |
| `string? DragRegionSelector` | `null` | `WithDragRegion(string selector)`（宿主指定拖动区域，见 §2；空/空白会抛） |

### `OrielApp`

| 成员（真实签名） | 说明 |
|---|---|
| `IReadOnlyList<WebviewWindow> Windows` | 当前存活窗口；关闭的窗口会自动摘除 |
| `WebviewWindow? GetWindow(string label)` | **按 label 取窗口**（对标 Tauri 2 的 `Window.getByLabel`）。不存在或已关闭时返回 `null`（不抛）。已关闭的窗口不会被返回——返回一个死窗口会让调用方拿到一个所有方法都抛异常的对象 |
| `WebviewWindow? GetFocusedWindow()` | 有焦点的窗口（对标 Tauri 2 的 `Window.getFocusedWindow`）。**遍历存活窗口、取第一个 `IsFocused` 为真的**——三平台都能回答"这个窗口有焦点吗"，而"哪个窗口有焦点"不需要单独的平台能力（Tauri 的 JS 侧也是这么做的）。返回 `null` 的两种情形：① 应用没有窗口；② 有窗口但都不在前台（用户切到了别的程序）。最小化的窗口不会被判为有焦点 |
| `WebviewWindow CreateWindow(Action<OrielWindowOptions>? configure = null, Action<WebviewWindow>? onCreated = null)` | 运行时新建窗口（见 §1 的多窗口说明）。**只能在 `Run()` 之后调**，否则抛 `InvalidOperationException` |
| `OrielTheme Theme` | `Light` / `Dark`；后端未就绪时返回 `Light` |
| `event Action<OrielTheme>? ThemeChanged` | 系统主题变化（见 §6） |
| `event Action<OrielIpcRejectedEventArgs>? IpcRejected` | 入站 IPC 被门禁拒绝（见 §4）。Release 下拒绝原本只写调试输出，宿主无从观测 |
| `event Action<OrielCommandInvokedEventArgs>? CommandInvoked` | 应用命令执行完成（成功与失败都有）：命令名、耗时、结果、来源文档。内建 `win.*` 不计入（拖动期间高频，会淹没"哪条命令慢"） |
| `event Action<OrielCommandFailedEventArgs>? CommandFailed` | 应用命令抛异常：异常**原样**带出（只给宿主，页面拿到的仍是净化文案）。与 `CommandInvoked` 不重复——那个给耗时/成功率，这个给异常对象 |
| `bool IsSecondaryInstance` | 本实例是不是"第二个实例"（需先 `SingleInstance(id)`）。**`Build()` 之后即可问**，用于在装配数据库/设备连接之前分流；激活请求此时已发给首实例 |
| `void Run()` | 建窗并进入消息循环；`IsSecondaryInstance` 为真时只打印契约标记后返回（退出码 0） |
| `bool IsOnUiThread { get; }` | 当前线程是不是 UI 线程（应用未运行时为 `false`）。宿主自己的后台线程据此判断"要不要编组"，不必再自己维护主线程 ID |
| `void PostToMainThread(Action action)` | 把动作切回 UI 线程（应用自己的后台线程要用它；IPC 命令里 `await` 之后已在 UI 线程，无需再切，见下方线程模型）。**投递失败抛 `InvalidOperationException`** |
| `bool TryPostToMainThread(Action action)` | 同上但不抛：投递失败返回 `false` 并报一条诊断（退出途中"尽力投递"用它） |
| `void InvokeOnUiThread(Action action)` | 在 UI 线程上**同步**执行：已在 UI 线程则直接执行，否则投递并阻塞等待完成。动作抛的异常原样带回调用方。**退出已开始时立刻抛**（消息循环停了，投递的动作不会被处理，干等即永久阻塞） |
| `bool IsExiting { get; }` | 退出流程是否已开始（`Quit` 已调用，或消息循环已结束）。退出一旦开始，UI 线程队列就不再被排空——**别再做"投过去等它执行"的操作** |
| `void Quit()` | 请求退出：结束消息循环、让 `Run()` 返回，并**保证进程最终结束**（见下方"退出语义"）。**只能在 UI 线程调用** |
| `event Action<OrielExitEventArgs>? Exiting` | 退出流程已开始（消息循环即将/已经结束） |
| `event Action<OrielExitEventArgs>? ExitCompleted` | 库侧退出清理已走完、`Run()` 即将返回。**不**代表进程已结束 |
| `event Action<OrielExitTimeoutEventArgs>? ExitTimeout` | 清理超过 `ExitTimeoutDuration`、进程即将被看门狗强制结束。回调在**看门狗线程**上，必须尽快返回 |
| `TimeSpan ExitTimeoutDuration` / `int ForceExitCode` | 退出清理超时（默认 10 秒）/ 被强杀时的退出码（默认 `2`，与正常退出的 `0` 区分） |
| `OrielTray? Tray` / `IReadOnlyCollection<OrielTray> Trays` | 主托盘（第一个）/ 全部托盘 |
| `OrielTray? GetTray(string id)` / `OrielTray CreateTray(Action<OrielTrayOptions>?)` | 按 id 取回 / 运行期新建（见 §7 的托盘注册表） |
| `bool RemoveTray(string id)` / `bool RemoveTray()` / `OrielTray? RestoreTray()` | 按 id 移除 / 移除全部 / 重建构建期托盘 |
| `bool NotificationsSupported` | 这个环境能不能发系统通知 |
| `bool ShowNotification(OrielNotificationOptions)`<br>`bool ShowNotification(string title, string? body = null)` | 发通知；返回系统是否接受 |
| `bool IsAutoStartEnabled` / `bool EnableAutoStart(IReadOnlyList<string>? arguments = null)` / `bool DisableAutoStart()` | 开机自启的查询与开关（`arguments` 是随自启一起传的参数） |
| `bool OpenExternal(string url)` | 用系统默认程序打开（受 §7 的 scheme 白名单约束；被拒或不支持返回 `false`） |
| `bool RevealInFileManager(string path)` | 在文件管理器里定位文件 |
| `bool OpenWithDefaultApp(string path)` | 用默认程序打开本地文件 |
| `void Run()` / `void Dispose()` | 进入消息循环 / 释放（幂等；托盘是系统级资源，退出前必须释放） |

### 多窗口

```csharp
// 运行时新建窗口。必须在 UI 线程调用——命令处理、窗口事件、托盘回调里都在 UI 线程上。
WebviewWindow second = app.CreateWindow(
    w => w.WithLabel("settings")        // 必须给：默认 "main" 已被第一个窗口占用
          .WithTitle("第二个窗口").WithSize(800, 600),
    win => win.Loaded += () => Console.WriteLine("第二个窗口就绪"));

WebviewWindow? again = app.GetWindow("settings");   // 按 label 取回（不存在时 null）
```

| 语义 | 说明 |
|---|---|
| 一个应用、多个窗口 | 内嵌资源表（不落盘）、可信来源、IPC 令牌与能力配置都继承**应用级**的那些；页面各有一份，命令与门禁共用 |
| **每个窗口要有不同的 `Label`** | 默认 `"main"`，多窗口时必须显式给其余窗口设 label；重复会在建窗时抛 `InvalidOperationException`（**不会自动改名**——自动改名会让 `emitTo('main', …)` 发到错误窗口且毫无症状）。页面侧读 `window.oriel.label` 回答"我是谁" |
| `win.*` 只作用到发起者 | 内建窗口命令按"发起调用的那个窗口"路由，多窗口下不会打错窗口 |
| 关掉一个不退出 | 所有窗口都关掉之后消息循环才结束（Windows 由存活窗口计数归零触发 `WM_QUIT`，macOS 由 `applicationShouldTerminateAfterLastWindowClosed:` 回答） |
| `Windows` 会自动摘除已关闭的窗口 | 摘除发生在公共 `Closed` 事件**之前**：处理器里读到的列表不含刚关掉的那个。`GetWindow` 同样不会返回已关闭的窗口 |
| 必须在 UI 线程调用 | 刻意不做自动 marshal：返回值是同步的窗口对象，"marshal 过去再建"会让调用方拿到一个尚未建好的窗口 |

> **Windows 上全进程共享一个 WebView2 环境**：user data folder 是全进程一个目录，而 WebView2 不允许
> 同一目录上并存两个环境——所以环境由平台后端持有并复用（一份环境 + 每个窗口一个控制器）。
> `UseUserDataFolder(path)` 改的就是这个目录。

### 退出语义

`Quit()` 是"最终退出"，不是"建议退出"。它做三件事：

1. 结束消息循环（Windows `PostQuitMessage`、Linux `GtkMainQuit`、macOS `NSApplication.stop:`），让 `Run()` 返回；
2. `Run()` 返回前执行库侧清理（撤销托盘图标、释放后端与共享 WebView2 环境）——**清理照常执行，不是 `Environment.Exit` 一走了之**；
3. 同时起一个看门狗：从"请求退出"算起超过 `ExitTimeoutDuration`（默认 10 秒）进程仍在，就强制结束它，退出码为 `ForceExitCode`（默认 `2`）。

> **为什么必须有第 3 条**：`PostQuitMessage` 只是往消息队列投一条 `WM_QUIT`，它不保证进程结束。
> 消息循环停下之后，宿主在 `Run()` 返回后跑的清理全部同步落在 UI 线程上，任何一步卡住
> （等一个失去响应窗口的同步调用、一个不返回的 join）都会让进程驻留——驻留期间可执行文件映像
> 仍被映射，现场表现是"程序删不掉/装不上"，Windows 还会判成 AppHangB1。
> 兜底做进库里，宿主就不必各写一套看门狗（写错了正好复现这个故障）。

看门狗**覆盖整段退出，包括 `Run()` 返回之后宿主的收尾**——那正是最容易卡住的一段。
进程正常结束时看门狗线程随进程消失，不需要显式收尾。

| 配置（`OrielAppBuilder`） | 说明 |
|---|---|
| `UseExitTimeout(TimeSpan)` | 覆盖超时（默认 10 秒；必须是正值） |
| `UseForceExitCode(int)` | 覆盖被强杀时的退出码（默认 `2`） |
| `UseExitWatchdog(bool = true)` | 关闭看门狗（默认启用）。只有宿主要自己接管退出、且确信清理不会卡时才关 |

退出途中可观测：`Exiting`（消息循环刚停）→ `ExitCompleted`（库侧清理走完、`Run()` 即将返回）
→ 若超时则 `ExitTimeout`（看门狗线程上触发，随后强杀）。**`ExitCompleted` 不代表进程已结束**——
宿主在 `Run()` 返回后的收尾仍受看门狗保护。

> `Quit()` **只能在 UI 线程调用**：`PostQuitMessage` / `GtkMainQuit` / `NSApplication.stop:`
> 都是线程相关的，从线程池调用会投给一个没有消息循环的线程，主循环永远收不到（表现为"调了
> `Quit` 却退不掉"）。从其它线程退出请用 `InvokeOnUiThread` 编组过来。库检测到不在 UI 线程会
> 立刻抛 `InvalidOperationException` 而不是静默投递。

### `WebviewWindow`

窗口门面。所有方法**必须在 UI 线程调用**（`await` 之后要碰窗口就经 `PostToUiThread`）；每个窗口一套独立的页面会话。

| 成员（真实签名） | 返回 / 说明 |
|---|---|
| `OrielApp App { get; }` | 所属应用 |
| **显隐与状态** | |
| `Show()` `Hide()` `Close()` `Focus()` | `Close()` 会先触发 `Closing`（可取消） |
| `Destroy()` | **强制销毁**：绕过 `Closing` 与页面接管。页面实现"确认后关闭"用的就是它——若它也能被拦下，那条路会被宿主否决逻辑无声地变成"关不掉"，而页面以为用户已经确认过了 |
| `bool CloseInterceptEnabled { get; set; }` | 是否让页面接管关闭；初值来自 `OrielWindowOptions.InterceptClose`，运行期可改（页面常只在"有未保存内容"时接管） |
| `OrielWindowEventKind EventForwarding { get; set; }` | 哪几类宿主事件转发给页面；默认 `None`（见 `OrielWindowOptions.EventForwarding`） |
| `double ScaleFactor { get; }` | 窗口所在显示器的缩放比（1.0 = 100%）。页面用它把 `PhysicalSize` 换算成逻辑尺寸。**取窗口所在显示器**而不是系统值——窗口拖到另一块不同缩放的显示器后它会变 |
| `string Title { get; }` | 当前窗口标题（对标 Tauri 2 的 `Window.title()`）。与 `TitleChanged` **不是**同一件事：那个只在页面文档标题变化时触发，程序调 `SetTitle` 不会触发它 |
| `Maximize()` `Minimize()` `Restore()` | — |
| `SetFullscreen(bool enabled)` `SetOnTop(bool enabled)` | — |
| `bool IsMaximized { get; }` / `bool ToggleMaximize()` | 切换后是否最大化（页面据此换图标） |
| `bool IsVisible { get; }` | 窗口当前是否"显示中"（`Hide()` 后为 `false`；**最小化仍算显示中**）。用来确认"真被藏住了"——Windows 组合宿主下渲染窗口曾是独立的顶层窗口，只靠窗口句柄判断不出残余（见 §9 的 `win.isVisible`） |
| `bool ToggleFullscreen()` / `bool ToggleOnTop()` | 切换后的状态 |
| **只读状态查询**（对标 Tauri 2 的 `isMinimized()` / `isFocused()` / `isFullscreen()` / `isAlwaysOnTop()` / `isDecorated()` / `isResizable()` / `isMaximizable()` / `isMinimizable()` / `isClosable()` / `isEnabled()` / `theme()`） | |
| `bool IsMinimized { get; }` | **与 `IsVisible` 是两件事**：最小化的窗口仍然"可见"，它只是被收进了任务栏。混为一谈的后果是"最小化之后页面以为窗口被藏了" |
| `bool IsFocused { get; }` | 按**窗口**判而不是按应用判（多窗口应用里只有一个是 key window）。三平台各有一个"看起来对但语义错误"的近邻，实现刻意避开了：Windows 用 `GetForegroundWindow` 而非 `GetFocus`（后者只对调用线程的窗口队列有意义）、Linux 用 `gtk_window_is_active` 而非 `has_toplevel_focus`、macOS 用 `isKeyWindow` 而非 `isMainWindow` |
| `bool IsFullscreen { get; }` / `bool IsAlwaysOnTop { get; }` | 取**库记住的状态**，不去比对窗口与显示器矩形——后者在用户手动把窗口拖成刚好铺满屏幕时会给出与库不一致的答案 |
| `bool IsDecorated { get; }` | 取创建时的 `Frameless` 选项（窗口生命期内不变）。Windows 上"无边框"是 `WM_NCCALCSIZE` 的处理，读样式位读不出那个意图 |
| `bool IsResizable { get; }` / `bool IsMaximizable { get; }` / `bool IsMinimizable { get; }` / `bool IsClosable { get; }` | **读原生**（改的就是原生样式，读回来才反映"系统实际接受的是什么"，所以写进去读得回来）。三处跨平台语义差异如实登记：`IsMaximizable` / `IsMinimizable` 在 **Linux 恒等于 `IsResizable`**、macOS 上 `IsMaximizable` 也是 resizable 位；`IsClosable` 在 **Linux 恒为 `true`**（GTK 没有这个概念，窗口总能收到 `delete-event`）。给恒真/回落值比给一个猜测值诚实 |
| `bool IsEnabled { get; }` | 窗口是否接收输入。**macOS 上只挡鼠标、不挡键盘**（Cocoa 没有"禁用窗口"这个概念，用 `ignoresMouseEvents` 近似） |

> **页面侧还有 `theme()`**（`getCurrentWindow().theme()` → `'light'` / `'dark'`），它读的是
> `OrielApp.Theme`，**没有**对应的 `WebviewWindow` 属性：本库只有"跟随系统"这一种来源，
> 所以永远给得出值（Tauri 的 `theme()` 可能返回 `null` 表示"无法确定"）。
| **外观与能力开关**（写操作，**均不在标题栏白名单里**，需显式 `Allow`） | |
| `SetEnabled(bool enabled)` | 启用/禁用窗口输入。禁用窗口不是标题栏要做的事，而它让页面能把窗口变成"点不动"的状态——那是能力而非外观 |
| `SetMaximizable(bool enabled)` / `SetMinimizable(bool enabled)` | **Linux 上为空操作**（GTK 无对应入口），且**刻意不记状态**——记了 `IsMaximizable` 就会给出"用户看到的按钮其实还在"的假值。macOS 上 `SetMaximizable` **与 `SetResizable` 是同一个操作**（AppKit 没有独立的最大化位），副作用是会连带影响拖拽缩放，两者互相覆盖时后调的赢 |
| `SetClosable(bool enabled)` | **Linux 上为空操作**（要"关不掉"得拦 `close-requested`，那是应用层策略）。Windows 的落点是**灰化系统菜单里的 `SC_CLOSE`**，不是清 `WS_SYSMENU`——后者会把三个按钮和整个系统菜单一起干掉，那是"删按钮"而不是"按钮不可用"。macOS 切 `NSWindowStyleMaskClosable` |
| `SetMaxSize(int width, int height)` | 最大尺寸（逻辑像素的**客户区**，传 0 表示不限制）。Windows 走 `WM_GETMINMAXINFO` 的 `ptMaxTrackSize`（framed 窗口做客户区→整窗换算）、macOS 用 `setContentMaxSize:`（0 映射成 `double.MaxValue`，因为 Cocoa 的"无上限"不是 0）、**Linux 只声明 `GDK_HINT_MAX_SIZE`，不替换既有的 `set_size_request` min 机制**——两条路各管一端 |
| `RequestUserAttention(OrielUserAttention? type)` | 请求用户注意（任务栏闪烁 / Dock 弹跳）。`Critical` / `Informational` / `null`（取消）。**macOS 上两档区别最明显**（Critical 会一直弹跳到用户来看）；**Linux 上两档没有区别**（GTK 只有 `gtk_window_set_urgency_hint` 这个开/关，没有分级） |
| `SetIcon(string path)`<br>`SetIconFromBytes(byte[] bytes)`<br>`SetIconFromRgba(byte[] rgba, int width, int height)` | 运行期换窗口图标。三平台复用**创建期图标**的代码路径（Windows `WM_SETICON`、Linux `gtk_window_set_icon`、macOS `setApplicationIconImage:`）。**Windows 上大/小两个槽位都会设**（`ICON_BIG` 给任务栏与 Alt-Tab 大视图，`ICON_SMALL` 给标题栏与 Alt-Tab 小视图——只设大图标的表现是"图标换了但标题栏那个还是旧的"）。**macOS 上没有窗口级图标**：这里设的是**应用（Dock）图标**，会影响整个应用；Linux 的 Wayland 下窗口图标由合成器决定，通常忽略本调用。解码失败**静默保持原图标**并记一条诊断（与 `LoadImageW` 返回 0 同语义），非法 base64 / RGBA 长度不符则**当场报错**（那是调用方能自己修的问题） |
| `SetBackgroundColor(uint argb)` | 运行期窗口底色（`0xAARRGGBB`）。这是"页面透明、或内核还没加载完时露出来"的颜色，三平台都落到 **webview 自己的底色**（Windows `ICoreWebView2Controller2.DefaultBackgroundColor`、macOS `NSWindow.backgroundColor` + `WKWebView.underPageBackgroundColor`、Linux `webkit_web_view_set_background_color`）。运行期值优先，没设过时回落创建期的 `OrielWindowOptions.BackgroundColor` |
| **几何与外观** | |
| `SetTitle(string title)` / `SetResizable(bool enabled)` | — |
| `SetMinSize(int width, int height)` | 客户区最小尺寸（逻辑像素，传 0 表示不限制） |
| `MoveTo(int x, int y)` / `Resize(int width, int height)` / `Center()` | 坐标是**屏幕**坐标、**左上角**原点（**逻辑像素**：HiDPI 下由库按窗口 DPI 折算，调用方不要预折算，见 §2）——`MoveTo` 的原点语义三平台一致。`Resize` 的宽高是**客户区**（对标 Tauri 的 `setSize`）：Windows 会把客户区换算成整窗，frameless 窗口不换算（客户区已等于整窗） |
| `OrielSize InnerSize { get; }` / `OrielSize OuterSize { get; }` | **客户区** / **整窗**尺寸，都是逻辑像素。`Resize(InnerSize)` 是不动点（同一单位）。**与 Tauri 的差别**：Tauri 的 `innerSize()` 返回 `PhysicalSize`，本库返回逻辑像素——库对页面只暴露逻辑像素，混入物理会让"这个值是哪种单位"变成每个调用点都要想一遍的问题。**Linux/Wayland 下 `OuterSize` 可能与 `InnerSize` 相等**（合成器不给装饰尺寸） |
| `OrielPosition InnerPosition { get; }` / `OrielPosition OuterPosition { get; }` | **客户区** / **整窗**左上角的屏幕逻辑坐标。**Linux 上 `InnerPosition` 恒等于 `OuterPosition`**：GTK 不暴露"客户区相对窗口原点的偏移"（装饰由 WM 画，客户端看不到尺寸）。比编一个偏移值诚实 |
| **拖动** | |
| `BeginDrag()` | Windows：进入原生模态拖动（**阻塞到松开鼠标**） |
| `BeginDragStreaming(double px, double py, double wx, double wy, double ww, double wh, double sh)` | macOS/Linux 的流式拖动起点；参数含义见 §2。**库自己接管标题栏拖动时走的就是它** |
| `DragTo(double dx, double dy)` / `EndDrag()` | 增量（CSS 点，y 向下为正）/ 收尾 |
| **导航** | |
| `bool CanGoBack { get; }` / `bool CanGoForward { get; }` | — |
| `GoBack()` `GoForward()` `Reload()` | 无历史时为空操作 |
| **IPC** | |
| `void EmitEvent(string name, string jsonPayload)`<br>`void EmitEvent<T>(string name, T payload, JsonTypeInfo<T> typeInfo)` | 宿主 → 页面（页面侧 `oriel.on(name, handler)`） |
| `Task<string> EvaluateJs(string script)` | 在页面当前文档上下文执行 JS，回 **JSON 编码**的结果串（WebView2 风格）。**边界**：macOS/Linux 这条路经页面回环完成，桥接没装上时它会等到超时或失败（不可信来源就是这样，见 §4）；页面导航离开原文档或窗口销毁时以异常立即失败，不会永久挂起 |
| `bool PostToUiThread(Action action)` | 把动作切回 UI 线程（已在 UI 线程则直接执行）。返回 `false` = 调度通道已失效、回调不会执行，同时有一条诊断 |
| `bool IsOnUiThread { get; }` | 当前线程是不是 UI 线程 |
| `void InvokeOnUiThread(Action action)` | 同步编组：投递并阻塞等待完成；动作抛的异常原样带回调用方 |
| **窗口命令与菜单** | |
| `ShowContextMenu(IReadOnlyList<OrielMenuItem> items)` | 弹应用自己的上下文菜单（见 §10） |
| `ShowContextMenu(items, OrielPosition? position)` | 同上，但指定位置（逻辑像素，相对客户区左上角；`null` = 跟随鼠标） |
| `CloseContextMenu()` | 关掉正在显示的菜单（Windows 上能提前结束那个阻塞的弹出） |
| `SetWindowMenuBar(items)` | 设这个窗口的菜单栏（空集合 = 移除）。**占客户区高度**；macOS 抛 `NotSupportedException`（菜单在那边是应用级的） |
| `SetAppMenuBar(items)` | 让这个窗口反映应用级菜单栏。已显式设过菜单栏的窗口**不被覆盖** |
| `OrielContextMenuPolicy ContextMenuPolicy { get; set; }` | 运行期改，**下次右键即生效**（见 §10） |
| **对话框** | |
| `ShowMessage(string text, string? title = null, OrielMessageBoxIcon icon = OrielMessageBoxIcon.Info)` | 模态，阻塞到用户关闭 |
| `string[] ShowOpenFileDialog(OrielOpenFileDialogOptions options)` | 多选；取消返回空数组 |
| `string? ShowOpenFileDialog(string? title = null, string? filter = null, string? initialDirectory = null)` | 单选；取消返回 `null` |
| `string? ShowSaveFileDialog(OrielSaveFileDialogOptions options)`<br>`string? ShowSaveFileDialog(string? title = null, string? filter = null, string? defaultExtension = null)` | 取消返回 `null` |
| `string? ShowFolderDialog(string? title = null, string? initialDirectory = null)` | 取消返回 `null`。Windows 的文件夹选择器**不支持初始目录**（已知取舍） |

事件（`+=` 订阅；第二列就是回调参数）：

| 事件 | 参数 | 触发时机 |
|---|---|---|
| `event Action? Loaded` | — | 首次加载完成（每窗口一次） |
| `event Action<OrielCloseRequestEventArgs>? Closing` | `bool Cancel { get; set; }` | 关闭前；置 `Cancel = true` 阻止关闭。**优先于页面接管**——应用说"不许关"就立刻生效，不再问页面 |
| `event Action? Closed` | — | 窗口已关闭 |
| `event Action<string>? TitleChanged` | 新标题 | 标题变化（含页面 `<title>` 与 `SetTitle`） |
| `event Action<bool>? MaximizedChanged` | 是否最大化 | 最大化状态变化 |
| `event Action<OrielSizeChangedEventArgs>? Resized` | `double Width` / `double Height`（逻辑像素的**客户区**） | 客户区尺寸变化：用户拖边框、最大化/还原、程序调 `Resize`。对标 Tauri 2 的 `onResized` |
| `event Action<OrielPositionChangedEventArgs>? Moved` | `double X` / `double Y`（窗口**左上角**的屏幕逻辑坐标，可为负） | 窗口位置变化：用户拖动、最大化/还原、程序调 `MoveTo`。对标 Tauri 2 的 `onMoved` |
| `event Action<bool>? FocusChanged` | 是否获得焦点 | 窗口获得/失去焦点。对标 Tauri 2 的 `onFocusChanged`。**只上报，不动焦点归属** |
| `event Action<string>? NavigationStarting` | 目标 URL | 每次导航开始 |
| `event Action<OrielNavigationCompletedEventArgs>? NavigationCompleted` | `bool Success` / `string Url` / `string? Error` | 每次导航结束；失败看 `Error` |
| `event Action<OrielConsoleMessageEventArgs>? ConsoleMessage` | `string Level` / `string Text` | 页面 console（需 `ConsoleForwarding`） |
| `event Action<OrielMessageReceivedEventArgs>? MessageReceived` | `string Name` / `string Json` | 页面 `postMessage` 上来 |
| `event Action<string>? ContextMenuItemClicked` | 被点菜单项的 `Id` | 应用自己的上下文菜单（见 §10） |
| `event Action<OrielFileDropEventArgs>? FileDropped` | `IReadOnlyList<string> Paths` | 拖入文件/文件夹（见 §9） |

> **`Resized` / `Moved` / `FocusChanged` 都会去重**：原生侧在拖动时每个像素来一次回调，
> 三平台都按"与上次上报值差 < 0.5 逻辑像素则跳过"处理（焦点是二值，值相同即跳过；
> 首个事件一定会报）。没有去重的话，一次拖动会变成几百次托管事件 + 页面 IPC。
>
> **它们不是页面 `window.resize` 的替代品**，而是补充：页面事件只在客户区变化时触发，
> 且拿不到窗口位置（`window.screenX` 在多屏与 HiDPI 下三平台行为不一致）。

`OrielMessageBoxIcon`：`Info`（默认）/ `Warning` / `Error` / `Question`。

### 关闭管线：宿主否决 → 页面接管 → 放行

用户点关闭、或程序调 `Close()` 时，判定固定按这个顺序走一遍（`OrielClosePipeline`，三平台同一份逻辑）：

| 顺序 | 判据 | 结果 |
|---|---|---|
| 1 | 宿主的 `Closing` 里置了 `Cancel` | **否决**：窗口不关，**且不再问页面**（应用说"不许关"就该立刻生效，不该先问页面再被拒绝——那会让页面收到一个它无权决定的通知，还会把宿主的否决误当成"用户在确认框点了取消"） |
| 2 | `CloseInterceptEnabled` 且页面已加载完成 | **挂起**：推 `close-requested` 给页面，窗口**不销毁**。页面同意就调 `Destroy()`，不同意就什么都不做 |
| 3 | 以上都不成立 | **放行**：真正销毁窗口 |

> **为什么必须"挂起"而不是"等待"**：Windows 的 `WM_CLOSE` 是**同步消息**，页面回执却是异步 IPC。
> 在消息处理里等回执等于把 UI 线程锁死（而回执的处理本身也要 UI 线程）——必然死锁。
> 所以只能"当场不销毁 + 推事件"，由页面稍后主动调 `Destroy()` 补上那一步。这也是 Tauri 的做法。
>
> **页面没加载完不挂起**：那条 `close-requested` 送不到未加载的页面，挂起只会让窗口变成点 X 没反应
> ——用户唯一的出路是任务管理器。这与 `win.*` 后门要防的是同一类事故。
>
> **`Destroy()` 不受任何拦截**：它是页面"确认后关闭"的出口。若它也能被 `Closing` 拦下，
> 那条路会被宿主否决逻辑无声地变成"关不掉"，而调用方以为用户已经确认过了。

> **命令线程模型**：命令从 UI 线程进入；`await` 之后的续体**回到 UI 线程**（三平台一致——
> 三平台都安装了基于主线程队列的 `SynchronizationContext`）。因此命令里 `await` 之后可以直接
> 碰窗口与平台对象。两个代价写明：① `await` 之间的同步段与 `await` 之后的 CPU 密集工作会
> **占用 UI 线程**——重活请自行 `Task.Run` 后把结果切回来；② 并发调用仍然可能交叠（第一个
> 命令 `await` 时 UI 线程就能处理下一个 invoke），**共享命令实例的方法仍须线程安全**。
> 应用自己的后台线程碰窗口仍要走 `OrielApp.PostToMainThread(...)` /
> `WebviewWindow.PostToUiThread(...)`——**GTK 与 AppKit 只允许在各自的主线程调用窗口 API**。
> 另：三平台的 `SynchronizationContext.Send` 都实现为 Post（异步），**不可依赖同步完成**。
>
> **线程归属的可查询与编组**（下游反馈 2026-10-14 的 P2-6）：
>
> | 需要 | 用什么 |
> |---|---|
> | 判断"我现在在不在 UI 线程" | `OrielApp.IsOnUiThread` / `WebviewWindow.IsOnUiThread`（应用未运行时为 `false`） |
> | 异步投递、不等待 | `PostToMainThread` / `PostToUiThread`（返回 `bool`：`false` = 调度通道已失效、回调不会执行，同时有一条诊断。**不再静默丢弃**） |
> | 投递但失败也不抛 | `OrielApp.TryPostToMainThread`（退出途中的"尽力投递"用它） |
> | 同步编组、等它做完 | `InvokeOnUiThread`（已在 UI 线程则直接执行；动作抛的异常原样带回调用方。**退出已开始时会立刻抛**——消息循环停了，投递的动作不会被处理） |
>
> 调用线程相关的库 API（`CreateWindow`、`Quit`）不在 UI 线程时会立刻抛
> `InvalidOperationException` 并说明原因，而不是静默投递到一个没有消息循环的线程。
> 宿主因此**不必再自己维护一份主线程 ID**。
>
> **线程事实**（各平台一致）：Win32 用 `GetCurrentThreadId == _uiThreadId`、Linux/macOS 用
> `Environment.CurrentManagedThreadId == _mainManagedThreadId` 判定。命令处理、窗口事件、
> 托盘菜单回调都在 UI 线程上（Windows 的托盘回调经专属消息窗口送达，与 WebView2 共用同一个
> dispatcher/消息循环线程）。

## 2. 无边框窗口

```csharp
.AddWindow(w => w.WithFrameless().WithTitle("Demo"))
```

标题栏由页面自绘。**页面只需把自己的标题栏元素标成拖动区域，拖动与双击由库接管**：

```html
<div class="titlebar" data-oriel-drag-region>…</div>
```

```csharp
.AddWindow(w => w.WithFrameless()
                 .WithDragRegion("#titlebar"))   // 也可以由宿主指定选择器（页面不必知道有拖动这回事）
```

| 手势 | 结果 |
|---|---|
| 区域上按下并移动超过 3 逻辑像素 | 移动窗口 |
| 区域上双击 | 最大化 / 还原 |
| 区域内的 `button` / `input` / `select` / `textarea` / `a` / `[contenteditable]` / `[data-oriel-no-drag]` 上的按下 | **不接管**（标题栏上的按钮要能正常点击，否则会"点不动"） |
| 右键、中键 | 不接管 |

页面侧还可以动态登记（SPA 换掉标题栏之后）：`oriel.dragRegion(element)`；不传参数 = 重新扫描整个文档，
返回这次登记的元素数。重复登记是幂等的。

底层的窗口命令（`win.*`，库保留前缀，见 §4）由库内建——拖动实现就是它们的调用方：

| 命令 | 用途 |
|---|---|
| `win.minimize` / `win.toggleMaximize` / `win.close` | 标题栏按钮（`toggleMaximize` 返回切换后的状态，页面据此换图标） |
| `win.drag` | Windows：进入原生模态拖动（程序发起的 `WM_NCLBUTTONDOWN`+`HTCAPTION`） |
| `win.dragStart` / `win.dragTo` / `win.dragEnd` | macOS/Linux：按页面给的坐标流式移动 |
| `win.toggleFullscreen` / `win.toggleOnTop` / `win.pickFile` / `win.contextMenu` | 其余窗口操作 |
| `win.isVisible` | 只读查询：窗口当前是否显示中（内建里唯一的查询类命令；最小化仍算显示中） |

**这些命令由库内建**：应用一行不注册就能用（页面里 `oriel.invoke('win.minimize')` 直接工作）。
它们实现里最终调用的 `WebviewWindow.BeginDrag` / `BeginDragStreaming` / `DragTo` / `EndDrag`
仍是公开 API，应用要自己编排窗口动作时照样可以用。

> **`win.` 是保留前缀，而且现在真的被占用了**：命中内建表的名字**不会**再走应用注册的命令。
> 应用自定义的 `win.*` **会在构建期报错**（`ORIELWEB106`，Error 级）——注册表只查"路由之间是否重名"，
> 它不知道内建的存在，所以运行期不会报错；正是因为这个盲区，诊断必须在编译期拦下来。
> 应用自己的命令请换前缀——demo 操作台里那份覆盖各形态的丰富菜单就叫 `manual.contextMenu`。
>
> 内建的两个有默认内容：`win.pickFile` 用库的默认标题与过滤器（可用 `title` / `filter` 参数覆盖）；
> `win.contextMenu` 弹的是**标准编辑菜单**（剪切/复制/粘贴 + 全选 + 关闭），应用自己的菜单项
> 仍应由应用调用 `WebviewWindow.ShowContextMenu` 弹出（见 §9）。

> **为什么拖动逻辑在库里**：三平台的形态差别很大，而每一处都曾在真机上以"双击没反应""窗口不跟手"
> 的形式暴露过。让每个应用各写一遍，等于让每个应用各踩一遍。
> - **Windows**：原生拖动会进入模态循环并**吞掉后续点击**，所以不能在 `mousedown` 里立刻发起——
>   等指针移动超过阈值再 `win.drag`，"原地双击"因此不会被拖动吃掉。
> - **macOS**：宿主不阻塞消息循环，按下后即可 `win.dragStart`，之后按增量 `dragTo`。
> - **Linux**：分两条 GDK 路径。**Wayland** 下窗口移动必须交给合成器（`xdg_toplevel.move`），
>   而一旦交出去指针就被 grab、页面再也收不到事件——所以同样等增量超过阈值才真交出去；
>   **X11** 下客户端自己摆窗口。
> - **双击判定必须在 `mousedown` 内同步完成**（拖动一旦开始，第二次点击就到不了页面），
>   而系统双击间隔只有宿主知道——它随脚本注入，且**只在库内部使用**：页面 API 上不暴露这类值，
>   应用也就不可能依赖它。
>
> **HiDPI 下不要折算**：GDK 的 API 坐标已经是应用像素（GDK 内部乘过 scale 再交给 X），
> 与页面 CSS 像素是同一套坐标系。实测过（WSLg/X11/`GDK_SCALE=2`：逻辑 200x100 ↔ 设备 400x200、
> 位移比值 ≈ 2）。再乘一次 scale 就会在 2x 屏上偏移一倍。

> **边缘调整大小（Windows 已内建，目前没有可调项）**：无边框窗口的四边热区由库接管，
> 厚度取系统的 `SM_CXSIZEFRAME + SM_CXPADDEDBORDER`（按窗口 DPI 折算）——**就是系统默认厚度，
> 没有 API 可调**；只在 `Frameless && Resizable` 时生效（framed 窗口由系统原生处理，见 §2 开头），
> 全屏与最大化时不生效。页面侧因此不需要（也做不到）自己实现边缘热区：既没有 `win.resize` 命令、
> 也没有 HWND 可用（下游反馈 2026-10-11 的 P1-2：他们原来"前端热区 + 2px 原生兜底"两套机制，
> 迁移后完全依赖这一层）。
>
> **已知边界（Windows 窗口移动）**：窗口移动时**不**向 WebView2 组合宿主转发
> `NotifyParentWindowPositionChanged`（实测在最大化等场景会触发访问违例，疑似本机运行时/互操作问题），
> 所以页面内的弹层（下拉、日期选择器）在窗口移动后可能停在旧位置，直到下一次布局；
> 尺寸变化走 `WM_SIZE` 的 bounds 更新，不受影响。

窗口图标：`WithIcon(path)` 三平台各自落到真正有图标槽的位置——Windows `WM_SETICON`、Linux 窗口图标、
macOS Dock 图标。**Windows 会主动从 exe 取图标**（`<ApplicationIcon>`），另外两个平台**没有等价来源**
（ELF/Mach-O 不带图标），所以必须显式给文件；GNOME 还不读 `_NET_WM_ICON`，它按 `WM_CLASS` 匹配 `.desktop` 的 `Icon=`。

#### 图标与路径（相对路径的解析基准）

两条规则适用于**所有**接受文件路径的选项（窗口图标 `OrielWindowOptions.Icon`、托盘图标
`OrielTrayOptions.IconPath` / `OrielTray.SetIcon`、通知图标 `OrielNotificationOptions.IconPath`）：

| 规则 | 说明 |
|---|---|
| **相对路径按可执行文件所在目录解析** | 即 `AppContext.BaseDirectory`，**不是**当前工作目录。工控机现场常从快捷方式、计划任务、开机自启启动，cwd 往往不是程序目录；macOS 从 `.app` 启动时 cwd 是 `/`。要按 cwd 解析请自行传 `Path.GetFullPath(...)` 的结果（绝对路径原样使用） |
| **文件不存在时降级，不抛异常** | 图标是纯观感：缺失回落到平台默认图标（Windows 的 exe 图标、Linux/macOS 的 `app.png`），并经 `OrielDiagnostics.Reported` 报一条警告。**空白路径**仍立刻抛 `ArgumentException`（那是调用方写错了，不是"文件缺失"） |

> 缺失为什么不该抛：它此前在 `AddWindow` 的配置回调里抛 `FileNotFoundException`，此时还没有窗口、
> 没有 UI，用户看到的就是"双击没反应"——一个纯观感问题被升级成"程序打不开"。
> 需要"路径写错就启动失败"的严格行为，用 `WithRequireIcon()` / `RequireIcon = true`。

`Icon` 读回的是**生效值**（缺失且未开严格模式时为 `null`）；原始请求路径见 `RequestedIcon`（排障用）。

#### `OrielImage`：托管图像对象（对标 Tauri 2 的 `tauri::image::Image`）

托盘与窗口图标都可以传**已解码的图像对象**而不是路径——这是对标 Tauri 2 的关键一步：
"图标"从"一个字符串"变成"一张 RGBA 位图"，于是**格式支持不再取决于某个平台的加载器**。

| 成员 | 说明 |
|---|---|
| `new OrielImage(ReadOnlyMemory<byte> rgba, int width, int height)` | 由 RGBA 数据构造（行优先、每像素 4 字节；长度必须等于 `width*height*4`） |
| `Rgba` / `Width` / `Height` | 只读视图与尺寸（**不要改写** `Rgba`：同一实例可能被多处共享） |
| `static FromBytes(ReadOnlySpan<byte>)` | 由 PNG / ICO **字节**解码（按文件头自动识别） |
| `static FromFile(string path)` | 由文件解码（相对路径按可执行文件目录解析）。失败抛 `FileNotFoundException` / `OrielImageFormatException` |
| `static TryFromFile(string, out OrielImage?, out string?)` | 不抛异常的版本：失败返回 `false` 并给出原因 |

解码器是**纯 BCL 实现**（`OrielImageCodec`，用 `ZLibStream` 解 PNG 的 zlib 流），
**不引入任何第三方图像库**——Tauri 那边靠 Rust `image` crate + 两个 Cargo feature，
本库的定位是零额外依赖，所以自己实现。

支持的子集（**刻意不做**的也写明，遇到时明确报错而不是解出花屏）：

| 支持 | 不支持（明确报错） |
|---|---|
| PNG 8 位：真彩 / 真彩+alpha / 灰度 / 灰度+alpha / 调色板（含 `tRNS`） | 隔行扫描（Adam7）、16 位每通道、iCCP/gAMA 等色彩管理块、动画 APNG |
| 五种行过滤器（None / Sub / Up / Average / Paeth） | — |
| ICO：内嵌 PNG、32 位 BMP（含 AND 掩码补 alpha）、24 位、1/4/8 位索引色 | 压缩的 ICO 位图 |

**格式支持三平台一致**：有了它，**Windows 也能吃 PNG 图标**（此前 `LoadImageW` 只认 ICO/BMP，
PNG 会静默失败），Linux 走 `gtk_status_icon_set_from_pixbuf`（不必落盘），macOS 走
`CGImageCreate` + `NSImage`（自动做预乘 alpha）。

## 3. 导航与页面通信

```csharp
window.GoBack();
window.NavigationStarting += url => { };
window.NavigationCompleted += e => { if (!e.Success) Console.WriteLine($"{e.Url} → {e.Error}"); };
```

```js
oriel.on('navigation.starting', (e) => {});
oriel.on('navigation.completed', (e) => { if (!e.success) showError(e.error); });
```

| 方向 | API | 语义 |
|---|---|---|
| 页面 → 宿主（要结果） | `oriel.invoke(name, args)` / `[OrielCommand]` | 零反射路由，有回执与超时（默认 30s，页面可改 `oriel.timeout`） |
| 页面 → 宿主（不要结果） | `oriel.postMessage(name, payload)` / `MessageReceived` | 单向通知，不等回执 |
| 宿主 → 页面 | `EmitEvent(name, payload)` / `oriel.on(name, handler)` | 自定义事件 |
| 页面 console → 宿主 | `WithConsoleForwarding()` / `ConsoleMessage` | **默认关闭**（见下） |

```csharp
window.EmitEvent("todo.changed", """{"count":3}""" );            // payload 已是 JSON 文本
window.EmitEvent("sys.info", info, AppJsonContext.Default.SysInfo); // 或传对象（源生成上下文）
window.MessageReceived += e => Console.WriteLine($"{e.Name}: {e.Json}");
```

**console 转发默认关闭**：注入的 hook 会包装页面的 console 方法（改变其可观测行为，例如
`console.log.toString()`），且高频输出会变成持续的 IPC 流量。开发时打开它就能在宿主侧直接看到页面日志。

> **为什么不用反射分发**：`[OrielCommand]` 的路由由 Roslyn 源生成器在编译期生成 switch，
> 运行期零反射——Native AOT 下反射需要保留大量元数据，而这条路径一次都不会用到。
>
> **`win.` 前缀是保留的**：内建窗口命令先于应用路由匹配，应用自定义的同名命令**在构建期报错**
> （`ORIELWEB106`，Error 级）——注册表只查"路由之间是否重名"、不知道内建的存在，
> 所以运行期既不报错也不会被调用，只能在编译期拦。见 §2 与 §4。

命令侧会用到的公开类型：

| 类型 | 用途 |
|---|---|
| `[OrielCommand(string name)]` | 标在命令方法上，`Name` 就是页面 `oriel.invoke` 用的名字；静态方法与实例方法都支持（实例模式见 `AddCommands<T>`）。**命令方法必须线程安全**——实例是共享的 |
| `OrielJson`（静态） | 命令里读 `JsonElement args` 的助手：`RequireArgElement` / `TryGetArgElement` / `RequireArgKind` / `GetRequiredArg<T>(args, name, jsonContext)` / `GetOptionalArg<T>(…)` / `WriteResult(writer, value, jsonContext)`。缺参数时报的是**带参数名与期望类型**的消息，而不是把 `null` 当默认值用 |
| `OrielIpcException` | 命令里 `throw` 它，页面侧那个 Promise reject 收到的就是它的 `Message`（抛别的异常则给通用失败文本） |
| `OrielCommandRegistry.AddRouter(IOrielCommandRouter)` | 手写路由的注册点。`IOrielCommandRouter`（`CommandNames` / `TargetType` / `RequiresTarget` / `Route(name)` / `InvokeAsync(...)`）由 `OrielWeb.Generators` 为每个 `[OrielCommand]` 类型生成——不用源生成器时才需要自己实现 |
>
> **失败导航必须报出原因**：三个引擎"接受的失败目标"各不相同（同源缺失页 / 环回端口 / 保留 TLD 各有差异），
> 且都会静默改写不接受的形态，所以自检里逐个试候选目标，任一真的上报失败即算通过。

> **首帧闸门（推荐用法）**：`Loaded` 的语义是"首次导航完成"，**早于前端框架的首屏渲染**——
> 直接靠它显示窗口会退回"白屏 / 半成品一闪"的观感。库不替应用包一层，因为"什么时候算渲染好了"
> 只有应用自己知道。推荐做法（demo 也是这么做的）：`WithHidden()` 建窗 → 页面首屏就绪后
> `postMessage` 一个约定名（如 `ui.ready`）→ 宿主在 `MessageReceived` 里 `Show()`，
> 并**加一个超时兜底**（如 8 秒），免得页面报错时窗口永不出现（下游反馈 2026-10-11 的 P2-5）。

## 4. 安全与能力模型

页面里的 `oriel.invoke` **谁能调、能调什么**由三层门禁决定：来源、令牌、命令授权。

**默认姿态**：不调用 `UseCapabilities` 时，**Debug 全放行、Release 全部拒绝**（fail-closed）。
一旦调用就以写的内容为准（Debug/Release 一致）。

```csharp
.UseCapabilities(c => c
    .Allow("todo.*", "sys.info")   // 精确名或前缀通配；"*" 表示全部
    .Deny("todo.remove")           // deny 优先于 allow
    .AllowOrigin("http://localhost:5173/"))  // 额外放行的来源（开发期连 dev server）
```

| 规则 | 说明 |
|---|---|
| 模式 | 精确名 `todo.add`、前缀通配 `todo.*`（末尾的 `.` 是模式的一部分，所以**不**匹配 `todo`）、`*`。**刻意不支持正则**——配错的代价是静默放行 |
| `deny` 优先 | 两条都命中按拒绝算。"先宽泛 allow、再 deny 挖例外"是最常见的写法，若 allow 优先那条 deny 等于没写 |
| 默认拒绝 | 两条都不命中即拒绝 |
| `win.` 前缀始终放行 | 那是无边框窗口的标题栏按钮与拖动，只操作自己那个窗口，不是"能力"；它们由库内建，自定义同名命令会被遮蔽。**应用自己的命令请避开这个前缀**。`tray.` 前缀同理 |
| **`window.` 族只放行一组** | 它比 `win.*` 大得多（有 `window.setIcon`、`window.setSkipTaskbar` 这类真正的能力），所以**不整族放行**；只有标题栏必需的那组（`minimize` / `toggleMaximize` / `toggleFullscreen` / `toggleOnTop` / `close` / `destroy` / `drag*` / `pickFile` / `contextMenu`）与一组只读状态查询（`isVisible` / `isMaximized` / `isMinimized` / `isFocused` / `isFullscreen` / `isAlwaysOnTop` / `isDecorated` / `isResizable` / `isMaximizable` / `isMinimizable` / `isClosable` / `theme` / `title`）例外。逐条理由见 `OrielCapabilityRules.AlwaysAllowedWindowCommands` |
| **`event.emit` 不放行** | 库内建但**不**始终放行的命令之一。它让一个窗口的页面能驱动**另一个窗口**的行为（跨窗口指令通道），而自绘标题栏完全不依赖它——没有开后门的理由。要用必须显式 `Allow("event.emit")` 或 `Allow("event.*")`。注意它只保留了这一个**名字**，`event.` 前缀本身不保留 |
| **`app.exit` / `app.show` / `app.hide` 不放行** | 同族里的只读元信息（`app.getName` 等）整组放行，但这三条会改变应用状态：退出、隐藏全部窗口。它们不是"页面本该有的信息"，而是**应用得点头的能力**——页面能退出应用这件事，不该因为名字在 `app.` 族里就默认成立。要用必须显式 `Allow("app.exit")` 等（Debug 构建不受影响，全放行） |
| 只管 `invoke` | 单向的 `postMessage` 没有命令名，由来源与令牌两层覆盖 |
| `win.` 只保留**命令名**段 | 事件名不受限：`EmitEvent("win.state.changed", …)` 不会被拦（库不校验事件名，事件命名空间是页面自己的）。但为了与"保留前缀"的口径一致，建议避开这个段（下游反馈 2026-10-11 的 P2-3） |

| 层 | 挡什么 |
|---|---|
| 来源 | 只有内嵌资源（与 `AllowOrigin` 放行的来源）算可信。**其它来源里桥接脚本根本不安装**——远程页面里连 `window.oriel` 都不存在，而不是"装上了再拦"。宿主侧逐消息校验时优先用**逐消息来源**（Windows `WebMessageReceived.Source`、macOS `frameInfo.securityOrigin`），顶级导航 URL 快照只作兜底；Linux 的经典 script-message 信号不带 frame 信息，用快照判定（第一层注入期自检与第三层令牌不受影响） |
| 令牌 | 每个应用实例一个 128 位随机串（同进程创建多个 `OrielApp` 时各一份），随脚本注入、每条入站消息回带。挡的是"不是本应用注入的脚本也往通道里塞消息"，**不是**防网络攻击的凭据（消息不经过网络） |
| 命令授权 | 按 allow/deny 名单判定命令名 |

被拒绝时页面侧那个 Promise 会 **reject 并带上原因**（"落进了 Deny 名单"还是"不在 Allow 名单里"），
而不是让它等到超时——"点了没反应"和"这个命令没被授权"必须能分开。

### 错误形状：文案 + 可选错误码

reject 出来的是一个 `Error`：

| 字段 | 说明 |
|---|---|
| `message` | 宿主给的文案。库自己抛的那些只写"是什么错、怎么改" |
| `code` | 机器可判别的错误码；**只在宿主给了的时候才存在** |
| `data` | 附加数据（任意 JSON）；同样只在有时才存在 |

写 `if (e.code === 'not_allowed')` 就够了——没给码时那两个属性**不存在**（不是 `undefined`），
所以 `'code' in e` 与 `e.code === undefined` 会给出同样的答案。

库自己的码（C# 侧是 `OrielIpcErrorCodes` 的常量）：

| 码 | 含义 |
|---|---|
| `not_allowed` | 命令名不在能力名单里（或来源/令牌不通过）。**名单外的名字一律报这个**——哪怕那个命令根本不存在，所以它不泄露"有哪些命令" |
| `unknown_command` | 名字在能力范围内但没注册（拼错、或所在程序集没被引用） |
| `missing_argument` | 缺少必需参数，或传了 null 给非可空参数 |
| `invalid_argument` | 参数类型或取值不合法 |
| `invalid_message` | 消息本身不合法（缺 `name` 等） |
| `target_not_found` | 按 label 找不到目标窗口 |
| `no_source_window` | 这次调用没有来源窗口，而该命令必须有窗口才能执行 |
| `internal` | 库内部的意外失败（**不是**应用命令抛的异常）——意味着该报一个库的缺陷 |

**应用命令抛的异常不带码**：那是应用的错误分类，库不该替它编。要分类就自己写：

```csharp
throw new OrielIpcException("不存在 id=42 的待办事项。")
{
    Code = "todo_not_found",
    Details = JsonDocument.Parse("""{ "id": 42 }""").RootElement.Clone(),
};
```

两个字段会原样到页面（`error.code` / `error.data`）。

### 超时：全局 + 逐次

| 方式 | 写法 |
|---|---|
| 全局 | `window.oriel.timeout = 300000`（默认 30000，`<= 0` 不启用） |
| 逐次 | `invoke(cmd, args, { timeout: 300000 })`（`<= 0` 表示**这次**不超时） |

逐次优先于全局。宿主不支持逐次超时时会**明确拒绝**、而不是静默按全局值走——
静默的话"我设了 5 分钟"会变成"30 秒后失败"，而排查的人会去查命令实现，
不会想到是超时没设上。

宿主侧有三个观测点，都只在宿主进程里可见、都对应用可用：

| 观测点 | 看什么 |
|---|---|
| `OrielApp.IpcRejected` | 被门禁**拒绝或丢弃**的消息：层级（`Source` / `Token` / `Command` / `Size`）、原因、文档 URL、命令名。非 `invoke` 的消息被拒时页面侧毫无反馈，只有这里能看到；`Size` 层（入站消息超过 1 MiB）连 `id` 都没解析出来，页面侧同样收不到回执 |
| `OrielApp.CommandInvoked` / `CommandFailed` | 应用命令的**执行**：命令名、耗时、成功与否；失败时给异常对象（原样，含内部细节）。几十条命令跑在节拍上的应用靠它回答"哪条慢""哪条在失败"。内建 `win.*` 不计入 |
| `OrielDiagnostics.Reported` | 进程级诊断通道：IPC 被拒、shell 路径被拒、单实例锁不可用、托盘/通知失败、原生 trampoline 的兜底异常……文本带 `[OrielWeb]` 前缀。它取代了 `Debug.WriteLine`——后者带 `[Conditional("DEBUG")]`，在 Release 包里被**整个编译掉**（实测发布程序集里一个 `[OrielWeb]` 都没有），等于生产环境零日志 |

三者都在收到消息的线程上**同步**触发（Windows 是 UI 线程，Linux/macOS 是各自主线程），回调里别做重活；
`CommandInvoked` 的耗时只覆盖命令本身，不含门禁与回执投递。`OrielDiagnostics` 是静态事件，订阅者请自行退订。

> **为什么 `win.*` 要开一个后门**：Release 下未配置即拒绝这条规则若把它一起拒掉，
> 无边框窗口会**直接变成关不掉的窗口**（自绘标题栏，没有系统标题栏可替代，用户只能去任务管理器）。
>
> **"Debug 还是 Release"取的是消费方**：本库以 Release 发布，它自己的 `#if DEBUG` 永远是 false。
> 先看包内 targets 注入的 `AssemblyMetadata("OrielBuildConfiguration")`，取不到回退到入口程序集的
> `DebuggableAttribute`，两条都没有时按 Release 算（与 fail-closed 一致）。
>
> **为什么来源校验放在注入期**：这样"远程页面不接 IPC"是**结构性成立**的，
> 不必依赖宿主侧每次再判一次，也不存在"远程页面拿到了令牌"这回事。
> 内嵌资源的来源（`oriel://<host>/`）自动可信——少了这一条，页面会被**自己的**门禁拒掉，
> 表现为"什么命令都没反应"。前缀由 `AssetUrl.TrustedPrefix` 生成，与导航用的 URL 同源，
> 免得手拼出"空格 vs `%20`"这类逐字节差异（2026-10-02 的 macOS 冒烟就是这么红的）。

**威胁模型：不把"可信页面"当敌人，但也不让它把宿主拖垮。** 门禁挡的是**不可信来源**
（远程页面里连桥接都装不上）与**不是本应用注入的脚本**；页面自己发什么，库按"可信"处理。

| 边界 | 现状 |
|---|---|
| 入站消息规模 | 单条上限 **1 MiB**（字符数）。超限的入站消息**不解析即丢弃**，经 `IpcRejected` 的 `Size` 层上报——页面侧收不到回执（`id` 就在那份被丢弃的 JSON 里）。出站方向不设闸：回执 JSON 由宿主自己构造，规模由宿主负责 |
| 取消 | **没有取消令牌**：`invoke` / `EvaluateJs` 发出去就只能等它回来（页面侧 30 秒超时是唯一出路）。命令函数也拿不到取消信号（生成器不注入 `CancellationToken`）——需要可中断的长活要么自己维护取消标志，要么拆成多次调用 |
| 可信页面 DoS | **不在防线内**：可信页面可以把主线程堵住（自己写死循环、或让命令返回一个巨大的对象）。这不是门禁能解决的问题，只能靠页面自身与命令实现的纪律 |

**逃生口：`JsonElement` 参数 / 返回值。** 命令方法直接收 `JsonElement` 时，生成器把页面 payload
**原样**透传（不做逐参数解析）；返回值里的 `JsonElement` 也原样写进回执。两个好处：① 不必把 DTO
搬进 `UseJsonContext` 的源生成上下文（见 §1）；② 响应体与"不用库的框架"逐字节一致，适合从自研
postMessage 桥迁移过来的应用（下游反馈 2026-10-11 的 P1-6）。代价是参数校验得自己写。

## 5. 拖动与双击的实现边界

拖动与双击由库实现（用法见 §2）。页面 API 上**不暴露**双击间隔之类的宿主值——它们只在注入脚本
内部使用，应用也就无从依赖。这一节记的是"库里怎么取值"与"哪些边界还没覆盖"，给维护者看。

| 平台 | 双击间隔 |
|---|---|
| Windows | `GetDoubleClickTime()`（毫秒） |
| macOS | `+[NSEvent doubleClickInterval]`（**秒**，注入前乘 1000） |
| Linux | `GtkSettings` 的 `gtk-double-click-time`（毫秒） |

取值时机是**建窗时一次**，同一文档内不再变（双击间隔是系统设置，拖动阈值是常量）。
三处都可能"取不到"（句柄无效、没有显示器、没有 GtkSettings），回退**只在 `OrielSystemSnapshot.Normalize` 一处**
——各自写一份默认值正是三个平台慢慢长歪的方式。

> **已知边界一（未做）**：macOS 允许用户在 Dock 偏好里把"双击标题栏"改成**最小化**。
> 库目前一律做成最大化/还原，没有读 `AppleActionOnDoubleClick`。
>
> **已知边界二（实测）**：`GDK_SCALE` 这个"伪造缩放"的环境变量**只在 X11 后端生效**
> （Wayland 下 GDK 用合成器给的缩放）。当前拖动折算不使用缩放（见 §2 的 HiDPI 说明），
> 但将来若要按缩放调阈值，CI 里造 2x 必须同时设 `GDK_BACKEND=x11`。
>
> **为什么不做同步 RPC**：页面侧那点同步需求（在 `mousedown` 里判双击）已经由库自己实现掉了，
> 于是不必为它引入三平台各一套自研的原生机制（WebView2 host objects /
> WebKitGTK `script-message-with-reply` / 拦 `window.prompt`）——那还会打破"一份桥接脚本三平台共用"。

## 6. 系统主题与单实例

```csharp
OrielTheme theme = app.Theme;                    // Light / Dark
app.ThemeChanged += t => { };
```

```js
oriel.on('theme.changed', (t) => document.documentElement.dataset.theme = t);
```

`OrielTheme` 只有 `Light` / `Dark` 两个值：它表达**当前实际是哪个**，不是"跟随系统 / 手工指定"的偏好——
偏好由系统设置决定，库只负责读取并上报变更。后端未就绪时 `app.Theme` 返回 `Light`。

### 单实例

```csharp
builder.SingleInstance("com.example.myapp", win => { /* 首实例被唤醒 */ });
```

| 方面 | 行为 |
|---|---|
| 默认 | **完全可选**：不调用 `SingleInstance` 时，`Run()` 里那段整块跳过——不建锁文件、不建管道、不监听，就是普通的多实例应用 |
| 判定时机 | **建窗之前**（否则第二个实例会先闪一个窗口再退出） |
| 第二个实例 | 已把激活请求发给首实例（探活那条连接本身就是判定手段）→ 打印 `SINGLE-INSTANCE-SECONDARY: 已有实例，已通知并退出`（脚本 grep 的契约标记）→ 直接返回，**不进消息循环、退出码 0** |
| 首实例 | 收到通知后在 UI 线程把窗口 `Show()` + `Focus()`；再调用你的 `onActivate`（不传就只做默认激活，参数是首窗口，没有则为 `null`） |
| 判定结果 | 是**三态**而不是 bool：`Acquired` / `AlreadyRunning` / `Unavailable`。`Unavailable`（锁文件建不出来：只读目录、磁盘满、沙箱拦截…）**倾向于照常启动**——静默不启动比多开一个窗口难排查得多 |
| 身份 | `id` 由你给（任意字符串，内部哈希成管道名，并与用户名一起哈希）。不同 id 彼此不影响；**想运行期放行第二个实例，只有换 id 这一条路**（没有"关掉同一个 id"的 API） |
| 通知载荷 | 库自己**不发载荷**（`TryNotifyPrimary` 与 `ListenAsync` 都留着 payload 的位置，但 `OrielApp` 传空串、监听侧也忽略）→ "第二次启动带了文件路径要在已有窗口打开"这类需求得应用自己另开通道 |
| 锁与运行期目录 | 锁是**独占文件句柄**，进程退出（哪怕崩溃）由操作系统释放，不留要手工清理的陈旧状态。目录优先 `XDG_RUNTIME_DIR`，否则 `<临时目录>/orielweb-<用户名>`，Unix 上权限收到 `0700`（防同机其他用户抢占可预测的锁名） |
| 顺序 | 必须配在 `Build()` / `Run()` 之前 |

> **单实例的判定不能用"命名管道能否创建成功"**：管道名冲突与"已经有实例在跑"是两件事——
> Unix 上 .NET 会先删掉旧 socket 再绑定，于是两个进程都会以为自己才是首实例（实测就是这样：
> 两边都在等对方）。用一个**独占文件锁**判定、再用命名管道通知，才是可靠的形态。
>
> **主题检测每个平台都要看两处**：Windows 注册表 + `WM_SETTINGCHANGE`（`ImmersiveColorSet`）；
> Linux `GtkSettings` + `notify::`；macOS `NSUserDefaults` + 系统通知。只查一处会在"启动时已是深色"或
> "切换后事件没到"这两类情况上漏判。

## 7. 平台集成

| 能力 | 用法 | 说明 |
|---|---|---|
| 托盘 | `builder.AddTray(o => { o.Tooltip = "…"; o.MenuOnClick = true; })` → `app.Tray` | 菜单项复用同一套 `OrielMenuItem`；`MenuOnClick` 让左键也弹菜单（手动验证时有用） |
| 通知 | `app.ShowNotification(title, body)` | 返回 `bool`：**提交失败**才是实现问题，"已提交但没显示"是系统设置问题。点击回调**不提供**（三平台都拿不到） |
| 窗口上下文菜单 | `window.ShowContextMenu(items)` | 三平台都支持（Windows 阻塞、另两个异步）；菜单构建按平台抽成共享类 |
| Shell | `app.OpenExternal(url)`、`app.RevealInFileManager(path)`、`app.OpenWithDefaultApp(path)` | **默认拒绝式的 scheme 白名单**（`http`/`https`/`mailto`），可 `UseShell` 扩展；被拒或不支持时返回 `false` |
| 开机自启 | `app.EnableAutoStart(arguments = null)` / `app.DisableAutoStart()` / `app.IsAutoStartEnabled` | Windows 写 HKCU 的 Run 键、macOS 写 LaunchAgent plist（不调 `launchctl load`，避免立刻又拉起一个）、Linux 写 autostart `.desktop`。标识默认取可执行文件名，`UseAutoStartId` 覆盖（多份构建共用一个标识会互相串） |

托盘（`OrielTrayOptions` / `OrielTray`）——**对标 Tauri 2 的托盘 API**，前端与后端同名同义：

| 成员 | 说明 |
|---|---|
| `OrielTrayOptions.Id` | 托盘标识（唯一）。多托盘必须各自不同；不显式给时自动分配（构建期第一个固定为 `"main"`） |
| `OrielTrayOptions.Icon` | **已解码的图标对象**（`OrielImage`）——对标 Tauri 2 的 `icon: Image`。失败策略由调用方掌握 |
| `OrielTrayOptions.IconPath` | 图标文件路径（PNG / ICO）。**赋值不读盘**，解码推迟到建托盘时（配置期抛异常会重演"图标缺失让程序打不开"） |
| `OrielTrayOptions.Tooltip` / `Title` | 悬停文本（默认 `"OrielWeb"`）/ 图标旁文字（**Linux 与 macOS 显示、Windows 不支持**） |
| `OrielTrayOptions.IconAsTemplate` | 是否当模板图（**仅 macOS**，默认 `true`）。**彩色品牌图必须置 false**，否则会被系统反色成单色剪影 |
| `OrielTrayOptions.MenuOnClick` | 左键是否也弹菜单（默认 false）。**macOS/Linux 上此项无效**（设了菜单就是左键弹） |
| `OrielTray.Id` | 本托盘的标识 |
| `OrielTray.Tooltip` / `Title` / `IconAsTemplate` / `IsVisible` | 运行期读写 |
| `OrielTray.SetIcon(string? iconPath)` | 换图标（文件路径）。**解码失败抛异常**；要"失败就保留原图标"用下面那个重载 |
| `OrielTray.SetIcon(OrielImage? icon)` | 换图标（已解码对象；对标 Tauri 2 的 `setIcon(Image)`）。传 `null` 回到平台默认 |
| `OrielTray.SetMenu(IReadOnlyList<OrielMenuItem>)` / `ClearMenu()` | 整份替换菜单 / 清空 |
| `OrielTray.Show()` / `Hide()` / `Dispose()` | 显隐 / **永久移除**（对标 Tauri 2 的 `close()`） |
| `event Action? Clicked` | 图标被点（历史 API，无参数） |
| `event Action<OrielTrayEvent>? TrayEvent` | **富事件**（对标 Tauri 2 的 `TrayIconEvent`）：类型 / 按键 / 状态 / 坐标 |
| `event Action<string>? MenuItemClicked` | 菜单项被点，参数是该项的 `Id` |
| `event Action<string>? RawEvent` | 平台原样事件（排查用，各平台载荷不同） |

应用级托盘注册表（多托盘，对标 Tauri 2 的 `new` / `getById` / `removeById`）：

| 成员 | 说明 |
|---|---|
| `app.CreateTray(Action<OrielTrayOptions>?)` | 运行期新建托盘（对标 `TrayIcon.new`），返回句柄。**必须在 UI 线程调用**；重复 id 抛异常 |
| `app.GetTray(string id)` | 按 id 取回（对标 `getById`）；不存在返回 `null` |
| `app.Trays` | 全部托盘的只读快照 |
| `app.RemoveTray(string id)` / `app.RemoveTray()` | 按 id 移除一个（对标 `removeById`）/ 移除全部 |
| `app.Tray` | 主托盘（第一个）。**多托盘应用请用 `GetTray` / `Trays`** |
| `builder.AddTray(...)` | 构建期配置托盘，**可重复调用**以创建多个 |

#### 托盘事件（`OrielTrayEvent`，对标 Tauri 2 的 `TrayIconEvent`）

| 成员 | 说明 |
|---|---|
| `TrayId` | 触发事件的托盘 id |
| `Type` | `Click` / `DoubleClick` / `Enter` / `Move` / `Leave` |
| `Button` | `Left` / `Right` / `Middle`；平台未提供时 `null` |
| `ButtonState` | `Down` / `Up`；平台未提供时 `null` |
| `Position` / `Rect` | 屏幕坐标（物理像素）/ 图标矩形；平台未提供时 `null` |

**各平台能给的粒度不同，未提供的字段一律是 `null`**（让页面能区分"平台不支持"与"值是 0"）：

| 平台 | Click | DoubleClick | Enter/Move/Leave | 按键 / 坐标 |
|---|---|---|---|---|
| **Windows** | ✅ | ✅ | ✅（V4 的 `NIN_POPUPOPEN`/`NIN_POPUPCLOSE`） | ✅ 按键、状态、坐标（V4 的 `GET_X/Y_LPARAM`） |
| **macOS** | ✅ | ✗ | ✅（`NSTrackingArea` 的 `mouseEntered:`/`mouseExited:`/`mouseMoved:`） | 按键 ✗（`NSStatusItem` action 不带）；坐标 ✗ |
| **Linux** | ✅ | ✅（`GDK_2BUTTON_PRESS`） | **✗**（`GtkStatusIcon` 没有悬停信号——平台事实） | ✅ 按键、状态、坐标（`button-press/release-event`） |

> `Rect` 目前三平台都未提供（Windows 需额外查 `Shell_NotifyIconGetRect`，macOS 的菜单栏坐标系转换成本高）。
> **Linux 的 Enter/Move/Leave 是平台事实而非漏实现**：`GtkStatusIcon` 的权威信号表只有
> `activate` / `button-press-event` / `button-release-event` / `popup-menu` / `query-tooltip` /
> `scroll-event` / `size-changed`。Tauri 2 的文档同样标注 "Linux: Unsupported"。

#### 前端 API（`oriel.tray`，与 Tauri 2 的 `@tauri-apps/api/tray` 一一对应）

| Tauri 2 | OrielWeb（页面侧） |
|---|---|
| `TrayIcon.new(options)` | `await oriel.tray.new({ id, iconPath, tooltip, title, iconAsTemplate, menuOnClick })` → 句柄 |
| `TrayIcon.getById(id)` | `await oriel.tray.getById(id)` → 句柄或 `null` |
| `TrayIcon.removeById(id)` | `await oriel.tray.removeById(id)` → 是否移除 |
| — | `await oriel.tray.getAll()` → 句柄数组 |
| `tray.setIcon(icon)` | `await tray.setIcon(iconPath)` |
| `tray.setMenu(menu)` | `await tray.setMenu(items)`（`{ id, label, type:'separator'\|'role', role, accelerator, enabled, checked, items }`） |
| `tray.setTooltip(s)` / `setTitle(s)` | `await tray.setTooltip(s)` / `await tray.setTitle(s)` |
| `tray.setVisible(b)` | `await tray.setVisible(b)` |
| `tray.setIconAsTemplate(b)` | `await tray.setIconAsTemplate(b)` |
| `tray.close()` | `await tray.close()` |
| `options.action` | `oriel.tray.onEvent(handler)`（返回退订函数） |

**差别（如实标注，不假装一致）**：图标在页面侧给**路径**而不是 `JsImage` 的多态形态（解码在宿主侧的
`OrielImageCodec` 完成，页面不必传字节）；没有 `setTempDirPath`（那是 Tauri 的 AppIndicator 落盘路径
带来的约束，本库走 `GtkStatusIcon` 直吃 pixbuf，不需要它）。

> **`tray.` 是库保留前缀**，与 `win.` 一样始终放行、不受能力配置约束（它只操作本应用自己的托盘，
> 不是"能力"）。应用自己的命令请避开这两个前缀。

> **已知取舍：托盘气泡（`NIF_INFO` / `OrielTray.ShowBalloon`）不做。** interop 字段都在，但库只用 toast：
> 早先走过托盘气球，实测**根本没显示出来**（能回传点击，但看不见的通知谈不上"能用"，
> 见 `Win32ToastNotification` 的说明）。后果要知情：toast 依赖 AUMID 注册、且会被现场的
> "专注助手 / 通知权限"关掉，而托盘气泡不依赖这些——所以"关闭 = 隐藏入托"这类语义下，
> 操作员可能什么提示都看不到，却以为程序已经关了。要这种确定性，请让托盘菜单/页面给出可见状态
> （下游反馈 2026-10-11 的 P1-4）。

通知（`OrielNotificationOptions`）：

| 成员 | 说明 |
|---|---|
| `Title` / `Body` / `IconPath` | 标题（多平台加粗显示，不可为空）/ 正文 / 附加图标（部分平台忽略） |
| `Id` | 通知身份（Windows 上是 toast 的 `Tag`）。**不用于点击回传**——点击上报功能已整体移除 |

shell（`OrielShellOptions`）：`AllowedSchemes`（默认 `http` / `https` / `mailto`）、静态 `DefaultSchemes`、
链式 `WithScheme(string)`。

菜单项与 role：

```csharp
OrielMenuItem.Item("id", "标签");
OrielMenuItem.Separator();
new OrielMenuItem { Id = "pin", Label = "置顶", Checked = true, Enabled = false };
new OrielMenuItem { Label = "子菜单", Items = [OrielMenuItem.Item("sub", "子项")] };
OrielMenuItem.RoleItem(OrielMenuRole.Copy);   // role 由 OrielMenuRoles 统一解释
```

> **Shell 为什么不给 `shell.execute`/PTY**：那属于能力沙箱范畴，与"跨平台 webview 核心库"的定位无关；
> 而"打开外链"必须默认拒绝，否则 `file:`、`javascript:` 之类的目标会直接交给系统默认处理器。
>
> **通知为什么走子进程**：Windows 经一个短命的 **Windows PowerShell 5.1**（Native AOT 下没有 WinRT 投影，
> 且它会去解析 `powershell` 这个名字——必须指定 5.1 与编码）；macOS 用 `osascript`；Linux 用 `notify-send`。

## 8. 对话框

```csharp
string? file  = window.ShowOpenFileDialog("选择文件", "图片|*.png;*.jpg", initialDirectory: null);
string[] many = window.ShowOpenFileDialog(new OrielOpenFileDialogOptions
{
    Title = "多选", AllowMultiple = true, Filters = [new OrielFileFilter("文本", ["txt", "md"])],
});
string? save   = window.ShowSaveFileDialog("另存为", defaultExtension: "txt");
string? folder = window.ShowFolderDialog("选择目录");
```

- 过滤器在三种平台形状间的转换、Windows 多选缓冲区的解析都在**纯函数**里（`OrielFileFilter` /
  `OrielFileDialogSupport`），因此可以离线断言。
- Windows 走 `GetOpenFileNameW` / `SHBrowseForFolderW`（**老 API**：`IFileOpenDialog` 是 COM 接口，
  为一个文件夹对话框引入 COM 互操作不划算；代价是**文件夹选择不支持初始目录**）。
- 保存时若输入没有扩展名，库按 `DefaultExtension` 补上（三平台不会替我们做同一件事）。
- 第二个参数那条 `"图片|*.png;*.jpg"` 是 Win32 风格的过滤器串，内部由 `OrielFileFilter.Parse(string?)`
  转成列表；要精确控制就传选项对象。取消的返回约定见 §1（`string?` 为 `null`，多选为**空数组**）。

| 选项（真实成员） | 适用 |
|---|---|
| `OrielOpenFileDialogOptions`：`Title` / `Filters` / `InitialDirectory` / `AllowMultiple` | 打开 |
| `OrielSaveFileDialogOptions`：`Title` / `Filters` / `InitialDirectory` / `DefaultExtension` / `DefaultFileName` | 保存 |
| `OrielFileFilter`：`Name` / `Patterns`；静态 `Of(string name, params string[] patterns)`、`AllFiles`、`Parse(string?)` | 两者共用 |

## 9. 文件拖放

```csharp
window.FileDropped += e => Console.WriteLine(string.Join(", ", e.Paths));
```

三平台的落点接入方式不同，但对外是同一个事件：

- **Windows**：窗口加 `WS_EX_ACCEPTFILES` → `WM_DROPFILES` → `DragQueryFileW`；
  本库是 Composition 宿主，WebView2 **不是子窗口**，拖放会落到本窗口——不必手写 OLE `IDropTarget`，
  但必须关掉 WebView2 的 `AllowExternalDrop` 以免它截走。
- **macOS**：自定义 `NSView` 子类承载 `NSDraggingDestination`，webview 作为其子视图（不碰 `WKWebView` 的方法表）。
- **Linux**：`gtk_drag_dest_set(webview, "text/uri-list")` + `drag-data-received`。

URI → 本地路径的解析（百分号编码含非 ASCII、`+` 不等于空格、Windows 盘符、`localhost` 与远程主机、批量保序）
有 23 个单测；**"真的把文件拖进去"只能在真机上人眼验证**（无头环境造不出 XDND/OLE 会话）。

### 9.1 页面侧：`onDragDropEvent`

```js
import { getCurrentWindow } from '@orielweb/api/window';

const un = await getCurrentWindow().onDragDropEvent(({ payload }) => {
    if (payload.type !== 'drop') return;   // 本库只有这一种；这么写与 Tauri 的迁移代码一致
    console.log(payload.paths);            // 本地路径（与 C# 的 FileDropped 是同一份数据）
});
```

宿主在 OS 投放完成时推一次 `drag.drop`；页面看到的仍是 Tauri 的名字 `tauri://drag-drop`（`TauriEvent.DRAG_DROP`）。

**与 Tauri 的两处差异，且都会静默生效**：

| 项 | Tauri | 本库 |
|---|---|---|
| 事件类型 | `enter` / `over` / `drop` / `leave` | **只有 `drop`**。三平台的原生投放都只在松手那一刻回调一次；靠 `over` 做的"拖到此处高亮"在本库上**不会亮** |
| payload | 含 `position`（物理像素） | **没有 `position`**。读它得到 `undefined`——有意为之：给一个 0 会被当成"拖到了左上角" |

方法位置：Tauri 的 `Window` 与 `Webview` 上都有（`Window` 那份是转发）；本库没有 webview 层，
只在 `Window` 上。迁移时 `getCurrentWebview().onDragDropEvent(...)` 改成
`getCurrentWindow().onDragDropEvent(...)` 即可。

覆盖率说明：C# 侧钉住 payload 形状与 JSON 转义（6 条单测），JS 侧钉住"Tauri 名 → 宿主名"的映射与回调
（2 条）。**原生投放那一段与上面的 `FileDropped` 是同一条路径**，仍然只能真机验证。

## 10. 内建右键菜单

```csharp
window.ContextMenuPolicy = OrielContextMenuPolicy.Editing;  // Editing（默认）/ Native / Disabled
```

| 值 | 行为 |
|---|---|
| `Editing`（默认，值 0） | 只留剪切/复制/粘贴。**Windows 是过滤式**（订阅 `ContextMenuRequested` 按名字保留三项）；**macOS/Linux 是接管式**：自己弹一个只含三项的菜单，编辑命令走引擎的原生通道 |
| `Native`（1） | 平台原样 |
| `Disabled`（2） | 完全不弹 |

应用自己的菜单用 `window.ShowContextMenu(items)` 弹，点击从 `window.ContextMenuItemClicked`（参数是项的 `Id`）回来。

`OrielMenuItem`（成员与三个工厂）：

| 成员 | 说明 |
|---|---|
| `Id` / `Label` | 标识（点击事件带的就是它）/ 显示文本 |
| `Role` | 平台角色（用 `OrielMenuRole.*` 填），由库翻译成各平台的原生行为 |
| `Accelerator` | 加速键文本，如 `"CmdOrCtrl+Shift+A"`；由 `OrielAccelerator.TryParse` 解析。各平台能表达的子集不同（Linux 托盘菜单只有一层等） |
| `IsSeparator` / `Enabled`（默认 true）/ `Checked` | 分隔线 / 是否可点 / 勾选态 |
| `Items` | 子菜单（一份菜单能嵌几层由平台决定，见 `OrielMenuItem` 的文档注释） |
| `static Separator()` / `static Item(string id, string label)` / `static RoleItem(string role)` | 三个工厂 |

`OrielMenuRole` 的全部取值（17 个常量）：`About` `Quit` `Hide` `HideOthers` `ShowAll`（应用级）；
`Undo` `Redo` `Cut` `Copy` `Paste` `SelectAll` `Delete`（编辑级）；`Minimize` `Zoom` `Close` `Front`
`ToggleFullScreen`（窗口级）。

> **为什么 macOS/Linux 是接管而不是"就地增删引擎菜单"**：后者在 WebKitGTK 4.1 上会破坏内存
> （连点右键几次就崩），所以改成自己弹菜单、编辑命令经 `webkit_web_view_execute_editing_command` 走引擎。

### 10.1 页面侧菜单（`@orielweb/api/menu`）

宿主侧的 `window.ShowContextMenu(items)` 与页面侧的 `Menu.popup()` 是**同一条通道**：
页面把整棵菜单树发过来，宿主按它构建原生菜单并弹出。

```js
import { Menu } from '@orielweb/api/menu';

const menu = await Menu.new({
  items: [
    { id: 'copy', text: 'Copy', accelerator: 'CmdOrCtrl+C', action: (id) => console.log(id) },
    { item: 'Separator' },
    { text: 'More', items: [{ id: 'deep', text: 'Deep' }] },
  ],
});

document.addEventListener('contextmenu', (e) => {
  e.preventDefault();
  void menu.popup({ x: e.clientX, y: e.clientY });
});
```

与 Tauri 的四处差异，都是**结构性**的（本库没有资源表），不是没做完：

| 项 | Tauri 2 | 本库 | 影响 |
|---|---|---|---|
| 菜单对象 | Rust 侧资源（持 `rid`） | 页面侧纯数据 | `new Menu()` / `append` / `items()` **不发 IPC**，因此不可能失败；`popup()` / `setAs*()` 之后改菜单不影响已装上的那份（快照） |
| `action` 回传 | 回调挂在资源上 | `menu.itemclicked` 事件 + 本次的 id 前缀 | 只有**有 `id` 的项**能挂 `action`；**弹出**一次只有一个在屏上，**菜单栏**多条并存 |
| `popup()` 完成时机 | 文档说"菜单一显示就 resolve" | **Windows 与 macOS 阻塞到用户选择**；只有 Linux 立即 resolve | **别在 `await menu.popup()` 之后假设用户已经选过了**，可靠信号是 `action` |
| `close()` | 来自 `Resource`，释放 `rid` | 本库扩展：**关掉在屏菜单**（等价 Esc） | Windows 上 `popup` 阻塞时，这是页面唯一能用代码关掉菜单的路 |
| `setIcon` | 设置图标 | **抛错**（没有图像资源表） | `IconMenuItem` 能构造、能加入菜单，但画成普通文本项 |
| `setAsAppMenu` / `setAsWindowMenu` 的**返回值** | 还给你 Rust 侧那份菜单资源 | 还给你**页面侧记的**上一份 `Menu` | 多页面各看各的；单窗口应用完全一致。`close()` 对它没有意义 |
| `setAsWindowMenu` | 三平台都支持 | **macOS 抛 `not_supported`** | 菜单在 macOS 是应用级的（Tauri 也是 Unsupported）。macOS 上只能用 `setAsAppMenu` |

`NativeIcon` 是 56 个 macOS `NSImage` 名称的常量表，键集与 Tauri **逐键一致**（对齐账本盯着它）。

### 10.2 菜单栏（`setAsAppMenu` / `setAsWindowMenu`）

把一棵菜单树装成**菜单栏**（不是弹出）。装完之后再改这个 `Menu` **不影响**已装上的那份——
本库没有资源表，每次都是整棵发过去重建原生菜单，要改得再设一次。

| 命令 | 落点 | 说明 |
|---|---|---|
| `menu.setAsWindowMenu` | Windows `SetMenu(hwnd)` / GTK `GtkMenuBar` | 只影响**发起调用的那个窗口**。空 `items` = 移除菜单栏（合法输入，不是错误） |
| `menu.setAsAppMenu` | macOS `[NSApp setMainMenu:]`；Windows/Linux 装到所有没显式设过菜单栏的窗口 | 跨窗口：含**此后新建**的窗口。因此门禁更严（见下） |

**门禁姿态不同，这是有意的**：

- `menu.setAsWindowMenu` 与 `menu.popup` 同档，**无条件放行**。两者装的是同一份东西
  （同一个解析器、同样的 role），页面给弹出菜单塞一个 `Quit` 与给菜单栏塞一个是等价的——
  持久与否不构成新能力。
- `menu.setAsAppMenu` **要应用显式放行**（`Allow("menu.setAsAppMenu")`）。它是 menu 族里
  唯一越过调用方窗口的命令：一个不受信任的页面装上应用菜单，等于在别的窗口顶部放了一条
  看起来像宿主自己的菜单栏。与 `app.exit` / `app.show` 同一类。Debug 构建全放行。

**菜单栏占客户区高度**（macOS 除外——菜单在屏幕顶部，不进窗口）：

- Windows 装饰窗口由 `DefWindowProc` 自动让出；**无边框窗口**由宿主在 `WM_NCCALCSIZE` 里让。
- Linux 的布局是 `GtkBox{menubar, webview}`；`InnerSize` 读的是 **webview 的分配尺寸**，
  与 Windows 的 `GetClientRect` 同一口径，`Resize(w, h)` 会补回菜单栏高度。

**已知差异**：应用菜单的自定义项点击回到**装了这份菜单的那个窗口**的页面。
Tauri 那边回到创建 `Menu` 的那个 JS 上下文——单窗口应用两者一致；
多窗口 + 应用菜单 + 自定义 `action` 三者叠加时行为不同，迁移代码若依赖这一点要自己用
`emitTo` 协调。

## 11. 页面侧 API

桥接脚本在文档创建时注入，只安装到**可信来源**（见 §4）。

### 11.1 `window.oriel`（底层传输层）

它等价于 Tauri 的 `__TAURI_INTERNALS__`：能用，但**不是公开 API**。新代码请用 `@orielweb/api/*`。

> **名字不同，这一点会静默咬人**：`window.oriel` 不是 `__TAURI_INTERNALS__` 的别名。
> 从 Tauri 迁过来的代码里那种 `"__TAURI_INTERNALS__" in window` 环境探测，迁移后**恒为
> `false`**——不报错，只是让应用走进浏览器降级分支（假数据、被禁用的按钮、没注册的监听）。
> 请一律换成 `isTauri()`（`@orielweb/api/core`）。B7 演练的 3 个真实应用全都在做这种探测，
> 见 [MIGRATION-FINDINGS](./MIGRATION-FINDINGS.md)。

### 11.2 `@orielweb/api`（推荐）

模块名与 `@tauri-apps/api` 一一对应，迁移 = 换 import 前缀。当前已提供 `core` / `event` / `window` / `dpi` / `mocks` / `path` / `app` / `fs` / `opener` / `notification` / `autostart` / `clipboard` / `dialog` / `image` / `menu` / `webviewWindow`（16 个）。

> 真正要换的不只是 import：**`invoke('plugin:fs|write_file')` 这类命令名、测试夹具里的线格式、
> `__TAURI_INTERNALS__` 探测**都要跟着换。对照表与真实卡点清单见
> [MIGRATION-FINDINGS](./MIGRATION-FINDINGS.md)（由 3 个真实 Tauri 2 应用的迁移演练产出）。

```js
import { getCurrentWindow } from '@orielweb/api/window';
import { listen, TauriEvent } from '@orielweb/api/event';

const win = getCurrentWindow();
console.log(win.label);                       // 我是哪个窗口（宿主注入）

// 关闭接管：**本库是"页面同意才关"**，与 Tauri 的 preventDefault 相反
const un = await win.onCloseRequested(async () => {
  if (await confirmDiscard()) await win.destroy();   // 同意关闭
  // 不同意：什么都不做，窗口留在打开状态
});

// 内置事件用 Tauri 名；包内会翻译成宿主事件名，并按需打开转发
await listen(TauriEvent.WINDOW_RESIZED, (e) => console.log(e.payload.width, e.payload.height));
```

| 模块 | 内容 |
|---|---|
| `core` | `invoke` / `isTauri` / `convertFileSrc`（同步；宿主侧查 `asset.read` 的 scope）/ `Channel`（流式回传，见 §11.3） |
| `event` | `listen` / `once` / `emit` / `emitTo` / `TauriEvent`（`listen` 返回**函数**，同时是 thenable：`const un = listen(...)`、`await listen(...)`、`listen(...).then(fn => ...)` 三种写法都成立；差别只剩 `instanceof Promise === false`） |
| `window` | `getCurrentWindow` / `getAllWindows` / `getByLabel` / `getFocusedWindow` + `Window` 类（含静态方法 `Window.getFocusedWindow()`；M5 的状态查询与外观开关：`isMinimized` / `isFocused` / `isFullscreen` / `isAlwaysOnTop` / `isDecorated` / `isResizable` / `isMaximizable` / `isMinimizable` / `isClosable` / `isEnabled` / `theme`；`setEnabled` / `setMaximizable` / `setMinimizable` / `setClosable` / `setMaxSize` / `setSizeConstraints` / `requestUserAttention` / `setIcon` / `setBackgroundColor`；`onThemeChanged` / `onScaleChanged` / `onDragDropEvent`；显示器 `availableMonitors` / `primaryMonitor` / `currentMonitor` / `monitorFromPoint` / `cursorPosition`） |
| `webviewWindow` | `WebviewWindow`（`extends Window`）/ `getCurrentWebviewWindow` / `getAllWebviewWindows`。**页面建窗**：`new WebviewWindow(label, options)` 后 `await w.created`，或 `await WebviewWindow.create(...)`。与 Tauri 的差别是"创建结果走 `created` 而不是 `tauri://created` 事件"（见该模块一节） |
| `path` | `sep` / `delimiter`（**同步**）+ `join` / `normalize` / `dirname` / `extname` / `basename` / `isAbsolute` / `resolve` |
| `app` | `getName` / `getVersion` / `getIdentifier` / `getTauriVersion` / `supportsMultipleWindows`（只读元信息，**无需能力声明**）；`exit` / `show` / `hide`（**需要显式 `Allow`**，见 §4 的能力表） |
| `fs` | `readTextFile` / `writeTextFile` / `readFile` / `writeFile` / `exists` / `size` / `readDir` / `stat` / `lstat` / `mkdir` / `remove` / `rename` / `copyFile` / `truncate`（**两道关都必过**，见下） |
| `opener` | `openUrl` / `openPath` / `revealItemInDir` |
| `notification` | `isPermissionGranted` / `requestPermission` / `sendNotification`（桌面子集） |
| `autostart` | `isEnabled` / `enable` / `disable` |
| `clipboard` | `readText` / `writeText` / `clear`（纯文本） |
| `dialog` | `open` / `save` / `message` / `ask` / `confirm` |
| `dpi` | `LogicalSize` / `PhysicalSize` / `LogicalPosition` / `PhysicalPosition` + 归一与换算（`toPhysical` / `toLogical`） |
| `image` | `Image`（`fromPath` / `fromBytes` / `new`）+ `transformImage`。**与 Tauri 的本质分歧**：Tauri 的 `Image` 是资源表 handle（持 `rid`、有 `close()`），本库没有资源表——它是**页面侧字节容器**，因此**没有 `close()`**（迁移代码里的 `await image.close()` 会抛 `TypeError`，宁可当场炸也不要带着错误的心智模型继续写）。`rgba()` / `size()` 是本地值，但签名仍是 `Promise`（`await` 照样能跑）。尺寸解析（PNG 的 IHDR、ICO 的 `ICONDIRENTRY`，其中宽高字段的 **0 表示 256**）在页面侧完成；解码**像素**才请宿主（`window.decodeImage`），结果缓存 |
| `menu` | `Menu` / `Submenu` / `MenuItem` / `CheckMenuItem` / `IconMenuItem` / `PredefinedMenuItem` / `NativeIcon` / `itemFromKind`。**弹出与菜单栏都做了**：`Menu.popup(at?, window?)`、`setAsAppMenu()`、`setAsWindowMenu(window?)`（见 §10.2）。菜单树是**页面侧的纯数据**（没有 `rid`），每次整棵发给宿主；`action` 经 `menu.itemclicked` 事件回到页面。另加一条本库扩展 `close()`（关掉在屏菜单，见 §10） |
| `mocks` | `mockIPC` / `mockWindows` / `mockEmit` / `mockConvertFileSrc` / `clearMocks` |

### `path` 模块

**整族无需能力声明**（`path.*` 始终放行）：本族每个命令都只对页面自己传进来的字符串做运算，
不做 I/O、不读宿主状态、不涉及别的窗口。Tauri 的路径命令同样默认开启（`core:path:default`）。

```js
import { join, extname, basename, sep } from '@orielweb/api/path';

await join('C:', 'logs', 'app.log');   // 按平台规则拼接（由宿主算）
await extname('a.tar.gz');             // 'gz'   —— 不含点，与 Node 的 path.extname 不同
await basename('d/app.conf', '.conf'); // 'app'
sep();                                 // 同步：'\\'（Windows）或 '/'（POSIX）
```

与 Tauri 的三处口径（前两处是**对接** Rust，第三处是有意分歧）：

| 项 | 本库/Tauri（Rust） | Node / .NET |
|---|---|---|
| `extname('file.html')` | `'html'`（不含点） | Node 给 `'.html'` |
| `extname('.gitignore')` | `''`（整名是文件名） | **.NET 给 `'gitignore'`** |
| `normalize(p)` 对不存在的路径 | **正常工作**（纯词法） | Tauri 用 `canonicalize`，**会报错** |

第三处值得展开：Tauri 的 `normalize` 用 `std::fs::canonicalize`，它**会解析符号链接**，
而且**路径不存在时直接报错**——"把用户输入的路径先归一一下"在路径还不存在时就会失败，
是很容易踩的坑。本库用纯词法归一（`Path.GetFullPath`），不做任何文件系统访问。
需要"真实路径"（解析链接）应当是 `fs` 模块的一个显式操作。

> **为什么这些"纯字符串运算"也走宿主**：Windows 的盘符、UNC、`\\?\` 前缀、大小写不敏感，
> POSIX 的重复/尾随斜杠……在 JS 里手写一份就是**又造一份平台语义**，两份实现必然在某个边角上
> 不一致，症状是"只在某个平台错"。宿主有 .NET 的 `Path`，那是这些规则在 .NET 里的权威实现——
> 加上上面那两处 Rust 口径的显式调整。

### `path` 的目录解析（24 个）

```js
import { appDataDir, homeDir, tempDir, resolveResource } from '@orielweb/api/path';

await appDataDir();                    // %APPDATA%\com.example.app（Windows）
await homeDir();                       // C:\Users\<你>
await tempDir();
await resolveResource('assets/a.svg'); // 资源目录下拼，带越界校验
```

规则**逐格对齐 Tauri**（`dirs` crate：Windows `SHGetKnownFolderPath` / Linux XDG / macOS `~/Library/*`）：

| 分组 | 函数 | Linux | macOS | Windows |
|---|---|---|---|---|
| 应用 | `appConfigDir` `appDataDir` `appLocalDataDir` `appCacheDir` `appLogDir` | `${基目录}/${标识符}`（`appLogDir` 再加 `/logs`） | 同左（`appLogDir` 是 `~/Library/Logs/${标识符}`） | 同左 |
| 用户 | `homeDir` `desktopDir` `documentDir` `downloadDir` `pictureDir` `videoDir` `audioDir` `publicDir` `templateDir` | XDG（桌面/文档/下载等按 `user-dirs.dirs`，**用户可配置**） | `~/Desktop` 等固定名 | `{FOLDERID_*}`（可被重定向到别的盘） |
| 系统 | `configDir` `dataDir` `localDataDir` `cacheDir` `tempDir` | `$XDG_*` 或 `~/.config` `~/.local/share` `~/.cache` | `~/Library/Application Support` / `~/Library/Caches` | `{FOLDERID_RoamingAppData}` / `{FOLDERID_LocalAppData}` |
| 资源 | `resourceDir` `resolveResource` | `$APPDIR/usr/lib/{exe}` 或 `/usr/lib/{exe}`；开发期 `${exe_dir}/../lib/{exe}` | `${exe_dir}/../Resources` | 主可执行文件所在目录 |
| **平台独有** | `executableDir` `runtimeDir` | ✅ | ⛔ 不支持 | ⛔ 不支持 |
| | `fontDir` | ✅ | ✅ | ⛔ 不支持 |
| | `templateDir` | ✅ | ⛔ 不支持 | ✅ |

⚠️ 标记"不支持"的那些调用会以 **`not_supported`** 被拒（与 Tauri 一致）。
**照 Tauri 拒绝而不是"顺手多给一个值"是有意的**：多给会让"能跑"与"能迁"分叉，
而分叉在迁移完成之后才发现是最糟的时机。

> **Windows 上为什么不用 .NET 的 `SpecialFolder`**：实测有三格对不上——
> `downloadDir` 没有对应成员；`publicDir` 的 `CommonDocuments` 给 `...\Public\Documents`
> 而 Tauri 给 `C:\Users\Public`（**差一层**）；`fontDir` 等三格 Tauri 明确不支持。
> 所以直接用 `SHGetKnownFolderPath`。

> **`resolveResource` 有越界校验**：绝对路径被拒（那等于读文件系统任意位置，
> 而函数名是 resolve**Resource**），拼完跑出资源目录的也拒。
> 判据是"拼完在不在根之内"而不是"字符串里有没有 `..`"——`assets/../logo.svg` 合法且常见。

> **`runtimeDir` 在 `XDG_RUNTIME_DIR` 缺失时明确失败**，不退到 `/tmp`：
> `/tmp` 是全局共享的，不是"本用户的运行时目录"那个语义，给个错值比明确失败糟得多。

### 对话框：`dialog`

```js
import { open, save, message, ask, confirm } from '@orielweb/api/dialog';

const file = await open({ filters: [{ name: '图片', extensions: ['png', 'jpg'] }] });
const many = await open({ multiple: true });            // → string[]
const dir  = await open({ directory: true });           // → string | null（只支持单选）
const out  = await save({ defaultPath: '/tmp/报告.txt' });

await message('已完成', { kind: 'info' });
if (await ask('继续吗？')) { /* ... */ }
if (await confirm('确定删除？', { okLabel: '删除' })) { /* ... */ }
```

需要能力声明（`Allow("dialog.open")` 等）——它们会弹模态对话框，
而"能弹对话框"在无人值守流程里是有意义的。

| 要点 | 说明 |
|---|---|
| **`open` 的返回值随 options 变形** | 单选 → `string`、多选 → `string[]`、取消 → `null`（与 Tauri 一致） |
| **`directory` 只支持单选** | `directory + multiple` 会报错而不是静默只取第一个（与 Tauri 一致） |
| **`message` 返回被点按钮的文案** | 所以 `ask`/`confirm` 能拿它与期望标签比对；被外部关掉时是 `null` |
| **命令层返回语义、页面层映射文案** | Windows 的 `MessageBoxW` 只能显示系统文案，见下 |
| **自定义按钮文案** | Linux/macOS 生效；**Windows 上不生效**（显示系统文案），已记入"已知限制" |

**为什么 `message` 那套要分两层**：Tauri 的 `message` 返回"实际显示的文案"。
但 Windows 的 `MessageBoxW` 无法自定义按钮文字（要 `TaskDialogIndirect`，需要 comctl32 v6
激活清单），所以若照搬"返回文案"，页面拿它与期望标签比对时会**静默得到错误答案**——
用户点了"是"、显示的是"确定"，`ask` 就返回了 `false`。
本库的命令层返回**语义**（`ok`/`cancel`/`yes`/`no`），文案映射在 `dialog.js` 里做。

**与 `Window.pickFile()` 的关系**：那条是既有的公开契约（只收标题与过滤器、返回单个路径），
**冻结不动**；本模块是新入口。两者共用底下的原生对话框，所以行为不会分叉。

**未实现**（会明确报错，不静默忽略）：`canCreateDirectories`、`recursive`。

### 系统剪贴板：`clipboard`

```js
import { readText, writeText, clear } from '@orielweb/api/clipboard';

await writeText('内容');        // → boolean（写成功与否）
const text = await readText();  // → string | null（null = 剪贴板里没有文本）
await clear();                  // → boolean
```

需要能力声明（读写都是能力：写能覆盖用户已复制的内容，读可能读到密码管理器刚放进去的东西）：

```csharp
c.Allow("clipboard.*");   // 或逐条 Allow("clipboard.readText") 等
```

| 要点 | 说明 |
|---|---|
| **`null` ≠ `''`** | `null` 是"剪贴板里没有文本"（可能是图片、文件列表），`''` 是"文本是空的"。两者都要能表达 |
| **写失败返回 `false`** | 剪贴板是**全局独占**资源，别的进程正开着时写不进去是正常状态。Windows 侧会重试 5 次 × 10ms |
| **`label` 明确拒绝** | Tauri 用它选 X11 的哪个剪贴板（`CLIPBOARD`/`PRIMARY`/`SECONDARY`）；本库只操作默认那个 |
| **Linux 上读可能阻塞** | `gtk_clipboard_wait_for_text` 会跑内嵌 main loop 等所有者应答。GTK 没有"非阻塞取一次"的入口 |
| **macOS 的 `clearContents` 不能省** | 它给剪贴板内容打变更计数；不调用则别的应用收不到"变了"的通知（粘贴菜单还是灰的） |

平台实现：Windows 走 Win32 剪贴板 API、Linux 走 GTK `GtkClipboard`、macOS 走 `NSPasteboard`。
**Linux 与 macOS 的实现未在真机验证**（已记入 CHANGELOG 的"已知限制"）。

**刻意不做**：`writeHtml`（Windows 上是 CF_HTML，带字节偏移头的私有格式，
**偏移写错会让粘贴目标是乱码或截断**，且在"看起来成功了"的情况下静默发生）、
`writeImage` / `readImage`（要图像句柄模型）、`writeSecret`（macOS 专有标记）。

> ⚠️ **`--selftest api` 会真写一次系统剪贴板**（写进去、读回来、再清空）——
> 因为它改不了系统状态、也不弹窗口，而"写进去再读回来"是唯一能证明三个平台的原生实现
> 真的接对了的方式。代价是**会覆盖你当前的剪贴板内容**，跑之前请先存好心内容。

### 平台集成：`opener` / `notification` / `autostart`

```js
import { openUrl, openPath, revealItemInDir } from '@orielweb/api/opener';
import { sendNotification, requestPermission } from '@orielweb/api/notification';
import { isEnabled, enable } from '@orielweb/api/autostart';
```

**三族都不整族放行**，要用必须声明：

| 命令 | 为什么要声明 |
|---|---|
| `opener.openUrl` | 能拉起任意协议处理器（不过宿主还会按 `OrielShellOptions.AllowedSchemes` 过滤一次，默认 `http`/`https`/`mailto`） |
| `opener.openPath` | 等于"用默认程序打开任意文件"——对一个 `.exe` 来说就是执行它 |
| `opener.revealItemInDir` | 会打开文件管理器 |
| `notification.*` | 能弹系统通知（可被用来伪装成别的应用） |
| `autostart.*` | 改的是系统状态（往注册表/LaunchAgent/XDG autostart 里写东西） |

与 Tauri 的差别：

| 项 | 本库 | Tauri |
|---|---|---|
| `opener.openWith`（用指定程序打开） | ⛔ **明确拒绝**（`not_supported`）——那是任意程序执行，需要"允许哪些程序"的授权模型 | ✅ |
| `opener.revealItemInDir` 返回值 | `boolean`（**全部**成功才为 true） | `void` |
| `sendNotification` | **异步**（走宿主命令，三平台都是原生通知） | 同步（桌面端用 `window.Notification`） |
| `notification` 的通知管理 | ⛔ 不提供（要通知句柄表：`active`/`pending`/`cancel`/`cancelAll`/`removeActive`） | ✅ |
| `notification` 的渠道与调度 | ⛔ 不提供（`channels`/`Importance`/`Visibility` 是 Android 概念；`Schedule` 要常驻定时器） | ✅ |
| `notification.onAction` / `onNotificationReceived` | ⛔ 不提供（要把通知点击回传到页面） | ✅ |

> **`opener.openWith` 为什么明确拒绝而不是静默忽略**：静默忽略会让调用方以为
> "我指定了用某个程序打开"，实际按系统默认打开——一次静默的行为降级，而调用方不会发现。

> **`autostart.enable()` 装上的是当前这个可执行文件**。开发期跑的是 `dotnet run` 或
> bin 目录里的临时产物，那条自启项在换构建之后会指向一个不存在的路径。
> "打开自启 → 重启 → 没反应"这类现象先要排除这一条。

### `convertFileSrc`：把文件显示出来

```js
import { convertFileSrc } from '@orielweb/api/core';

const url = convertFileSrc('C:\\data\\photo.png');   // 同步，不查权限
img.src = url;                                       // 页面请求时宿主才查 scope
```

| 要点 | 说明 |
|---|---|
| **同步** | 与 Tauri 一致：只拼字符串、不发 IPC |
| **强制点在服务端** | 页面请求那个 URL 时，宿主查 **`asset.read`** 的 scope；不在内就 403（空响应体） |
| **独立 scope** | 不共用 `fs.*`——"把图片显示出来"与"以程序的方式读写文件"是两种意图 |
| **与页面不同源** | host 是 `file.oriel`，所以磁盘上的 HTML 被导航到也**拿不到桥接** |

```csharp
// 声明它才能真的取到内容
c.Allow(OrielAssetFileServer.ScopeCommand, s => s.AllowPath("$APPDATA/images/**"));
```

**不在 `convertFileSrc` 里查权限**（Tauri 亦然）：页面自己也能拼出同样的 URL，
在那一层拦没有意义。真正的判定在服务端：先归一、再查 scope、最后才碰磁盘；
不在 scope 内、文件不存在、或者是目录，**一律 403 且响应体为空**——
理由只进日志，因为"文件不存在"与"路径不在 scope 内"可区分就等于一个路径探测器。

⚠️ **平台覆盖**：目前只有 **Windows** 真的提供内容。Linux 与 macOS 会**一律 403**（fail-closed）——
`convertFileSrc` 能拼出 URL 但取不到内容。宁可"用不了"，也不要"某平台漏了 scope 检查"。

### `fs` 模块：两道关都必过

这是全库**唯一**一处"命令授权 + 路径 scope 都必须声明"的命令族。理由：

```csharp
// 只说"能调读文件这个动作"
c.Allow("fs.readTextFile");

// 还要说"能读哪些文件"——少了这一句，一条 XSS 就能把 ~/.ssh/id_rsa 读走
c.Allow("fs.readTextFile", s => s.AllowPath("$APPDATA/**"));

// 读得宽、写得窄：Tauri 的权限条目各自带 scope，所以这是能表达的
c.Allow("fs.writeTextFile", s => s.AllowPath("$APPDATA/reports/**").DenyPath("$APPDATA/reports/tmp/**"));
```

| 情形 | 错误码 |
|---|---|
| 命令没声明 | `not_allowed` |
| 声明了命令，但**路径不在任何 scope 模式内** | `path_not_allowed` |
| 声明了命令但**完全没给 scope** | `path_not_allowed`（空 scope 表示不允许任何路径，**不是不限制**） |
| 路径命中了某条 deny | `path_not_allowed` |

两条错误码分开是刻意的：混成一个的话，"我明明声明了 `fs.readTextFile`"会把人带向错的方向。

**scope 的规则**（与 Tauri 的 fs scope 一致）：

- 模式是 glob，变量前缀与目录一一对应：`$APPDATA` `$APPCONFIG` `$APPLOCALDATA`
  `$APPCACHE` `$APPLOG` `$AUDIO` `$CACHE` `$CONFIG` `$DATA` `$LOCALDATA` `$DESKTOP`
  `$DOCUMENT` `$DOWNLOAD` `$EXE` `$FONT` `$HOME` `$PICTURE` `$PUBLIC` `$RESOURCE`
  `$RUNTIME` `$TEMP` `$TEMPLATE` `$VIDEO`
- `*` 匹配一层内的任意字符（**不跨分隔符**），`**` 匹配零到多层，
  `?` 一个字符，`[abc]` / `[a-z]` / `[!abc]` 字符集，`{a,b}` 择一
- **deny 优先，且跨条目生效**：写了两条 scope、只在其中一条加 deny 时，另一条不会绕过它
- **路径先归一（解析掉 `..`）再判 scope**：反过来的话 `$APPDATA/../../etc/passwd`
  会被判成"在 $APPDATA 之内"（字面量确实在内），然后归一后指到别处
- 页面给的路径可以是相对的，按**进程当前目录**解析

**与 Tauri 的差别**：

| 项 | 本库 | Tauri |
|---|---|---|
| 句柄式 API（`open`/`FileHandle`/`seek`/`watch`…） | ⛔ 不提供（没有 rid 句柄表） | ✅ |
| 二进制通道 | JSON 数字数组（几十 MB 的文件不适用） | `invoke` 的二进制 body |
| `stat` 的时间 | 毫秒时间戳（数字） | `Date` |
| `requireLiteralLeadingDot` | ⛔ 未实现（默认为 false，与我们的行为一致） | ✅ |
| 变量名大小写 | **不敏感**（`$appdata` 也能用） | 敏感 |
| 大小写敏感性 | 跟随文件系统（Linux 敏感） | 固定 |

**`writeTextFile` 不写 BOM**（Tauri 亦然）：用 `Encoding.UTF8` 写文件会带上
`EF BB BF`，后果是"写 5 个汉字得到 18 字节"，且下游的 JSON/CSV/shell 解析器
会把首字节当内容。读这边会自己探测并去掉 BOM，所以别人写的带 BOM 文件照样能读。

### `app` 模块

```js
import { getName, getVersion, getIdentifier, getTauriVersion } from '@orielweb/api/app';

await getName();           // '示例应用'
await getVersion();        // '1.2.3'    来自程序集，或 WithAppInfo 显式给
await getIdentifier();     // 'com.example.app'
await getTauriVersion();   // '0.4.0'    ← **库**版本，不是 Tauri 框架版本
```

**只读元信息整组无需能力声明**：这几条读的都是宿主本来就会告诉页面的东西（页面已知自己的来源、
窗口 label、平台标识），没有一条是"应用不该知道"的信息。

```js
import { exit, show, hide } from '@orielweb/api/app';

await show();        // 显示全部窗口
await hide();        // 隐藏全部窗口
await exit(0);       // 退出应用，进程退出码 0（省略 code 则保持原退出码）
```

**这三条需要显式 `Allow`**（`app.exit` / `app.show` / `app.hide`）：它们改变应用状态，
不是元信息。页面能退出应用这件事应当由应用点头，而不是因为名字在 `app.` 族里就默认成立。
Debug 构建不受影响（全放行）。

> `exit()` 返回的 Promise 通常**不会 resolve**：应用在回执写回之前就退出了。
> 别 `await` 它再做事，也别放进"失败就降级"的分支。它会走正常退出流程
> （`Exiting` / `ExitCompleted` 照常触发）；若被看门狗强制结束，退出码用
> `UseForceExitCode`（默认 2），**不是**这里传的值。
>
> `show()` / `hide()` 的语义是"显示/隐藏所有窗口"，**不是** macOS 的"取消应用隐藏"
> （`NSApp.unhide:`）——三平台一致，不按平台分叉。

值从哪来（对标 Tauri 的 `tauri.conf.json`）：

| 项 | 默认来源 | 显式配置 |
|---|---|---|
| `name` | 入口程序集的 `AssemblyProduct` → `AssemblyTitle` → 程序集名 | `WithAppInfo(name: …)` |
| `version` | `AssemblyInformationalVersion`（去掉 `+` 后的构建元数据）→ 程序集版本 | `WithAppInfo(version: …)` |
| `identifier` | 程序集名小写化 | `WithAppInfo(identifier: …)` |

三项**独立**退化：只给其中一项时其余仍自动推断——所以"只想改个显示名"不必把版本号也抄一遍
（抄一遍的后果是它从此不再跟随程序集，静默过期）。

标识符的字符集在 `Build()` 时校验（只允许字母、数字、`-`、`_`、`.`），因为它是**应用数据目录的
组成部分**；`.` / `..` 这类"能过字符集但作为目录名指向别处"的输入单独挡住。

> **`getTauriVersion()` 的语义提醒**：Tauri 的这个函数返回**框架**版本（`2.x.y`），
> 本库返回**库**版本（`0.4.0` 这一类）。数字放在一起比较没有意义——迁移时若代码里有
> `getTauriVersion()` 的版本号判断，那一段必须重写，不能照搬。
> 它与 `window.oriel.version` **同源**（都来自程序集版本），所以两处做能力探测不会矛盾。

**本模块仍然不含**：`setTheme` / `setDockVisibility`（应用级主题覆写、macOS Dock 显隐，本库还没有）、
`getBundleType` / `BundleType`（返回的是 Tauri 打包器的产物类型，本库是库不是打包器，
**给不出这个值**——编一个比不提供更糟，迁移代码会基于错信息分支）、`defaultWindowIcon` /
`fetchDataStoreIdentifiers` / `removeDataStore` / `onBackButtonPress`（依赖图标资源表、
数据存储、移动端返回键，暂无对应物）。

**`window` 模块的方法是否需要能力声明**：

| 组 | 命令 | 是否需要 `Allow` |
|---|---|---|
| 标题栏必需 | `minimize` `toggleMaximize` `toggleFullscreen` `toggleOnTop` `close` `destroy` `drag*` `pickFile` `contextMenu` | **否**（无条件放行，理由见 §4） |
| 只读状态查询与环境（标题栏靠它渲染按钮状态与激活配色；显示器靠它按屏换配色） | `isVisible` `isMaximized` `isMinimized` `isFocused` `isFullscreen` `isAlwaysOnTop` `isDecorated` `isResizable` `isMaximizable` `isMinimizable` `isClosable` `isEnabled` `theme` `title` `availableMonitors` `primaryMonitor` `currentMonitor` `monitorFromPoint` `cursorPosition` | **否** |
| 其余 | `show` `hide` `focus` `maximize` `restore` `setTitle` `setResizable` `setFullscreen` `setOnTop` `setMinSize` `moveTo` `resize` `center` `scaleFactor` `innerSize` `outerSize` `innerPosition` `outerPosition` `getAll` `setCloseIntercept` `setEventForwarding` | **是**：`Allow("window.*")` 或逐条声明 |

> **`window.*` 与 `win.*` 的分工**：`win.*` 是早期的库内建命令（**只能作用于发起者**、整族始终放行）；
> `window.*` 是为对齐 Tauri 2 建的族（**可按 `label` 寻址**、只有上面那两组无条件放行）。
> 两族并存、旧的冻结，是不破坏既有应用的唯一路径。

**页面建窗（`window.new`，需要 `Allow("window.new")`）**：

```js
import { WebviewWindow } from '@orielweb/api/webviewWindow';

// 推荐：一步完成"建窗 + 等结果"
const win = await WebviewWindow.create('settings', {
    title: '设置', width: 640, height: 480, url: 'https://app.oriel/settings.html',
});

// 或与 Tauri 同形的写法：构造即发起创建，结果在 created 上
const w2 = new WebviewWindow('about', { url: 'https://app.oriel/about.html' });
await w2.created;

await win.setTitle('改了标题');   // 之后就是普通 Window
```

支持的选项只有 `label` / `title` / `url` / `width` / `height` / `resizable` / `frameless`——
其余（特效、图标、父子窗口…）依赖本库还没有的能力，**传了也不会生效**，所以干脆不认。

> **与 Tauri 唯一一处调用形状差异**：Tauri 的 `new WebviewWindow(...)` 之后用
> `once('tauri://created')` / `once('tauri://error')` 等结果；本库没有这两个页面事件
> （`WEBVIEW_CREATED` 在桌面端不会触发），改成 `await w.created`。
> 迁移时这一处**必须改**——写在这里，而不是让它静默不触发。
>
> `window.new` 不在标题栏白名单里：新窗口跑的是应用自己的页面、共享令牌与能力配置，
> 建窗是能力而不是元信息。

**显示器（`availableMonitors` / `primaryMonitor` / `currentMonitor` / `monitorFromPoint` / `cursorPosition`，无需 `Allow`）**：

```js
import {
    availableMonitors, primaryMonitor, currentMonitor, monitorFromPoint, cursorPosition,
} from '@orielweb/api/window';

const screens = await availableMonitors();   // [{ name, position, size, scaleFactor }, …]
const primary = await primaryMonitor();      // 主屏（坐标原点）
const here = await currentMonitor();         // 发起调用的那个窗口所在的屏
const at = await monitorFromPoint(1200, 300); // 包含该点的屏，不在任何屏上时 null
const cursor = await cursorPosition();       // PhysicalPosition
```

**单位是物理像素**（`position` / `size` / `cursorPosition` 都是）。这与本模块的窗口几何
（`innerSize` / `outerSize` 等是**逻辑像素**）不同，是照 Tauri 的口径：
显示器要跨不同缩放的屏幕比较，只有物理像素是统一刻度；窗口要按 CSS 尺寸布局，
只有逻辑像素是统一刻度。两者之间用 `scaleFactor` 换算：

```js
// 副屏：2560 物理 / 2.0 = 1280 逻辑
const logical = screens[1].size.toLogical(screens[1].scaleFactor);
```

`position` / `size` 是 `dpi` 模块的 `PhysicalPosition` / `PhysicalSize` **实例**（与 Tauri 一致），
所以 `.toLogical()` / `.toPhysical()` 可用。`Monitor` 的键集恰好是
`name` / `position` / `size` / `scaleFactor` 四个——没有 `isPrimary`（Tauri 也没有；主屏用
`primaryMonitor()` 取）。

**平台事实（如实标注，不假装一致）**：

| 平台 | 事实 |
|---|---|
| Windows | `EnumDisplayMonitors` + `GetMonitorInfo` + `GetDpiForMonitor`。进程声明了 per-monitor v2，所以数值是真实像素 |
| macOS | 主屏取 `[NSScreen mainScreen]`（与 Tauri 相同），它同时是坐标原点。名称来自 `localizedName`（10.15+，旧系统上 `name` 为 `null`） |
| Linux | GDK 报的是**应用像素**，这里乘 `gdk_monitor_get_scale_factor` 换成物理像素。**Wayland 没有全局像素坐标系**：分数缩放下 `scaleFactor` 只能是整数（物理尺寸有取整误差），跨屏相对排列可能与 X11 不同，`monitorFromPoint` 只对落在某块屏内的点有效 |

> **窗口在跨屏时的判定**：`currentMonitor()` 按**相交面积最大**算（Windows 的
> `MonitorFromWindow`、macOS 的 `-[NSWindow screen]`、Linux 的
> `gdk_display_get_monitor_at_window`），不是"窗口中心点落在哪块屏"。两者在窗口正好跨在
> 两块屏中间时给出的答案不同，而用户与系统都按前者理解。

> **`scaleFactor` 的用途别搞反**：它是"物理 / 逻辑"，所以逻辑 = 物理 ÷ `scaleFactor`
> （`PhysicalSize.toLogical` 就是这个方向）。把 150% 屏上的 `innerSize()`（逻辑）当成物理去乘，
> 窗口会大 1.5 倍——这是迁移时最容易犯的错。

### 11.3 流式回传：`Channel`

普通命令的回执只有一条，流式场景（模型逐字输出、批量导入进度、日志尾部）需要**在命令返回之前**
多次推送。`Channel` 就是这条路：

```js
import { Channel, invoke } from '@orielweb/api/core';

const progress = new Channel((p) => console.log(p));   // 也可以事后 progress.onmessage = ...
const total = await invoke('import.start', { paths, progress });
// 命令返回后通道自动关闭——宿主发结束事件，页面收到就退订，不必手动关。
```

```csharp
[OrielCommand("import.start")]
public async Task<int> Start(string[] paths, OrielChannel progress)
{
    for (int i = 0; i < paths.Length; i++)
    {
        await Import(paths[i]);
        progress.Send(new { index = i, total = paths.Length });   // 任意可序列化的值
    }

    return paths.Length;
}
```

| 项 | Tauri | 本库 |
|---|---|---|
| 线格式 | `toJSON()` → `__CHANNEL__:<id>` 字符串 | `toJSON()` → `__ORIEL_CHANNEL__:<id>` 字符串（同一个思路：**通道在宿主眼里只是一个字符串参数**） |
| 回调登记 | `__TAURI_INTERNALS__.callbacks` 全局表 | 两条事件名：`oriel://channel/<id>` 与 `oriel://channel-end/<id>` |
| 关闭时机 | Rust 侧通道 drop 时发 `end`，页面 `cleanupCallback()` | 命令结束时**生成器在 `finally` 里**调 `Close()`（正常返回/抛异常/后续参数绑定失败都算）；页面收到结束事件自动退订 |
| 显式关闭 | 无公开 API | `channel.close()`，幂等 |
| 顺序保证 | 消息带 index + 终点标记，页面侧按序重排 | **没有重排**：到达顺序以宿主投递为准（同一线程内有序） |
| `Send` 的序列化 | Rust 侧 `Serialize` | 与**命令返回值完全同一套**（`OrielJson.WriteResult`）：基元/字符串序列/`byte[]` 直接写，DTO 需要应用 `UseJsonContext` 里声明 |

为什么把线格式做成字符串：桥接脚本对 `invoke` 的参数做 `JSON.stringify`，`toJSON()` 让通道
自然变成字符串——**桥接层与三个平台后端一行都不用改**，也就不存在"某个平台没实现通道"。
代价是宿主侧要自己解析前缀，但那只在 `OrielChannel.Bind` 一处。

为什么不做序号重排：Tauri 需要它是因为 Rust 侧可能多线程并发 `send`，而页面按 index 重排能
给出确定顺序。本库的推送最终都落到 UI 线程的 `PostWebMessage` / `EvaluateScript`，同一窗口内
本来就是有序的；加一层重排只会把"跨线程并发推"的顺序问题藏起来，而那种情况本来就该由应用
自己决定顺序。

> **两个容易踩的点**：① `Channel` 要在 `invoke` **之前**构造（构造函数里就订阅了，命令执行中
> 随时可能推）；② 命令返回之后再 `Send` 会被丢弃并在 `OrielDiagnostics` 留一条记录——
> 后台任务与命令返回天然会赛跑，把它变成异常会让 fire-and-forget 的收尾随机炸掉。

### 11.4 `window.oriel` 的原始成员

| 成员 | 说明 |
|---|---|
| `oriel.ready` | Promise，脚本就绪（推荐用它而不是 `orielready` 事件） |
| `oriel.platform` | `'windows'` / `'macos'` / `'linux'` |
| `oriel.label` | 本窗口的 label（对标 Tauri 2 的 `Window.label`）。多窗口下同一份页面会被加载多份，URL 与 title 都可能相同——**只有它是稳定标识**。取值来自 `OrielWindowOptions.Label`，不是写死的 |
| `oriel.version` | 库版本（`Major.Minor.Build`，由宿主注入，不是写死的） |
| `oriel.timeout` | 命令回执超时（毫秒，默认 30000；页面可改，`<=0` 不启用） |
| `oriel.dragRegion(target?)` | 登记拖动区域（见 §2）：不传参数 = 重新扫描整个文档；返回登记的元素数 |
| `oriel.invoke(name, args)` | → `Promise`；失败时 reject 带原因 |
| `oriel.postMessage(name, payload)` | 单向通知 |
| `oriel.on(name, handler)` | 订阅宿主事件，返回退订函数。**参数是裸 payload**（`maximized` 给 `bool`、`theme.changed` 给字符串）——新代码建议用 `@orielweb/api/event` 的 `listen`，它包成 `{ event, id, payload }` |
| `oriel.tray` | **托盘 API**（对标 Tauri 2 的 `@tauri-apps/api/tray`）：`new` / `getById` / `getAll` / `removeById` / `onEvent`，句柄上 `setIcon` / `setMenu` / `setTooltip` / `setTitle` / `setVisible` / `setIconAsTemplate` / `close`——完整对照表见 §7 |

托盘事件的 payload（推给 `oriel.tray.onEvent` 的处理器）：

```js
// { id, type, button, buttonState, position, rect }
// type: 'Click' | 'DoubleClick' | 'Enter' | 'Move' | 'Leave'
// 平台未提供的字段为 null（例如 Linux 的 button、macOS 的 position）
const off = oriel.tray.onEvent((e) => {
  if (e.type === 'Click' && e.button === 'Right') openMyMenu();
});
```

> 托盘事件**不会**走通用的 `oriel.on('tray.event')`——两者是分开的订阅集合，
> 只关心托盘的页面不会被窗口状态事件（最大化、导航等）打扰。

`orielready` 事件也保留（兼容用），但它在 `DOMContentLoaded` 才派发——`DOMContentLoaded` 之后才注册的监听器收不到，
所以文档主推 `await oriel.ready`。

## 12. 打包与分发（Velopack）

安装包与自动更新交给 [Velopack](https://velopack.io)（`vpk`）。**本库不自带打包器**：打包不是 webview
能力，产物形态（Windows 的 Setup.exe / 可选 .msi、Linux 的 AppImage、macOS 的 .pkg）由 Velopack 决定，
我们只把参数喂进去——它顺带产出的更新包与更新清单由我们删掉（理由见下）。

```bash
dotnet tool install --global vpk
pwsh tools/publish.ps1 -Bundle        # 发布 + 打包（版本号取自根 Directory.Build.props 的 <Version>）
```

`-Bundle` 做的事：`dotnet publish` → `vpk pack --packId OrielDemo --packVersion <版本>
--packDir <发布目录> --mainExe OrielDemo.exe --icon … [--msi]`，产物落在 `dist/<rid>-releases/`。

| 产物（Windows 上的实测清单） | 用途 |
|---|---|
| `OrielDemo-win-Setup.exe` | 安装程序（Velopack 在 Windows 上的主形态） |
| `OrielDemo-win.msi` | machine-wide 引导包（`vpk --msi`）——Setup.exe 的外壳，与"per-user 安装"无关 |
| `OrielDemo-win-Portable.zip` | 免安装的便携版 |

Linux 走 `pwsh tools/wsl_publish.ps1 -Bundle`（打包必须在 WSL 里跑），产物落在
`dist/<rid>-releases/`：`OrielDemo.AppImage`（**自更新的 AppImage**——Linux 上没有独立安装器，
这就是分发形态）。不需要外部 `appimagetool`（`vpk` 自带 appimagekit runtime）。

macOS 走 release 工作流（与 AOT 同理，打包只能在 macOS 上跑）：`tools/make-macos-app.sh` 把裸可执行
文件组装成 `dist/OrielDemo.app`，`vpk pack --packDir dist/OrielDemo.app` 再消费它——**不需要
`--mainExe`**（入口点取自那个 `.app` 的 `Info.plist`），也**不要传 `--icon`**（macOS 上它认 `.icns`，
传 `.png` 会失败；`--icon` 本就可选，`--help` 里只有 `--packDir` 标了 REQ）。产出
`OrielDemo-osx-Setup.pkg` 与 `OrielDemo-osx-Portable.zip`。组装脚本还会建好 `Contents/Resources/`：
`vpk` 要往里写 `sq.version`，且假定目录已存在（缺它时抛 `DirectoryNotFoundException`）。

- **只留"能装的东西"**：更新包 `*-full.nupkg`（Velopack 的"release"）与更新清单
  （`releases.*.json` / `assets.*.json` / `RELEASES*`）打包后即被删除——本仓库不发更新源，
  前者与我们的 NuGet 包同名同扩展、后者会指向一个已被删掉的包。`vpk` 没有"不产这些"的开关
  （`--noInst` / `--noPortable` 只管安装器与便携版），所以在打包之后删（脚本与 CI 都这么做）。
- **不需要外部工具**：`vpk` 自带（`--msi` 那一步它内部就用 WiX 模板编译，机器上不必装 WiX）。
  因此原先"把 WiX 锁在 v5 以免碰上 OSMF 付费条款"那条约束不复存在。
- **版本号只接受三段 semver2**：四段（`1.2.3.4`）会被拒；`-Bundle` 会自动截断并说明。
- **每个平台各跑一次**：与 Native AOT 同理，`vpk` 只能产出所在平台的产物。macOS 上它消费的是
  `tools/make-macos-app.sh` 组装好的 `.app`（Velopack 的入口点取自那个 `.app` 的 `Info.plist`）。
- **签名**：本地打包不需要；分发给用户前应当签名（`--signParams` / `--signTemplate`），macOS 还要公证——
  没有它们的包在别人机器上会被 SmartScreen / Gatekeeper 拦下。
- **自动更新（尚未接入）**：应用侧要在 `Main` 的第一行调用 `VelopackApp.Build().Run()`，更新源可以是
  任何静态托管。本仓库目前只做打包，所以 `vpk` 会警告"入口点没有 `VelopackApp.Run()`"——那是预期的。

## 13. 运行要求

### Windows

- WebView2 运行时（Win10+ 常已预装）。**库不自动安装**——联网安装属应用策略，
  未注册 `OnWebView2RuntimeMissing` 时库只弹一个含下载地址的说明框。
- 开发/发布需要 .NET 10 SDK 与 MSVC 工具链（Native AOT）。
- WebView2 运行时过旧时会走到"环境能创建但某个接口调用失败"的路径，错误提示会是通用的那条。

### Linux

```bash
sudo apt-get install -y libwebkit2gtk-4.1-dev libgtk-3-dev zlib1g-dev clang
sudo apt-get install -y fonts-noto-cjk   # 中文界面必需，否则渲染成方框
```

- 需要 X11 或 Wayland 会话（无头环境用 `xvfb-run` 包裹；WSL 里确认 WSLg 可用：`ls /mnt/wslg`）。
- **GNOME 上任务栏图标**需要一份 `.desktop`（按 `WM_CLASS` 匹配，`StartupWMClass` 要与 `xprop WM_CLASS` 的 res_class 逐字符相同）。
- 托盘图标在 GNOME 上需要 AppIndicator 扩展，Wayland 会话下也可能不显示——这是平台事实，不是缺陷。

### macOS

- 系统 ≥ 11；产物**必须打成 `.app`**：WKWebView 是多进程架构，宿主进程要凭 main bundle 的
  `CFBundleIdentifier` 才能与 WebContent/Networking 这些 XPC 服务通信；裸可执行文件会走到 WebKit
  的内部断言（`SIGTRAP`，退出码 133），而且不产生崩溃报告。`tools/make-macos-app.sh` 干的就是这件事，
  `vpk pack` 再消费它（见 §12）。
- Apple Silicon 要求可执行代码有签名（**ad-hoc 即可**）。没有 Developer ID 与公证的包从网上下载后会被
  Gatekeeper 拦下，用户需要去掉 quarantine 属性。
- DevTools：`WKWebView.isInspectable` 是 13.3+ 的公开 API（更低版本上库会跳过设置，DevTools 依赖 Safari 的默认行为）。

## 14. 验证与自检

| 手段 | 覆盖 |
|---|---|
| 单测（`tests/OrielWeb.Tests`） | 分发器与回执协议、能力模型、内建窗口命令表、资源 URL 解析与请求路径归一（含穿越拒绝）、资源名约定、文件/Shell/对话框的纯函数、快照回退与数值格式化 |
| 桥接测试（`tests/bridge/bridge.test.mjs`） | 三平台共用脚本的行为一致性：就绪、事件、往返、回执、超时、不可信来源不安装、每条出站消息带令牌、`oriel.label` 注入 |
| 前端包测试（`packages/api/tests/*.test.mjs`） | `@orielweb/api` 的 `core` / `dpi` / `event` / `mocks`：mock 往返、参数原样透传、per-call 超时未支持时明确拒绝、DPI 换算与宿主侧舍入一致（含 .NET 的 ToEven）、事件包装与 once 语义、`emit`/`emitTo` 的参数形状 |
| 事件投递取证（`--selftest event`） | `event.emit` 的**跨窗口**投递：广播（发起窗口自己也收到）、定向（只有目标收到）、定向到不存在的窗口返回 0 且不抛。断言靠探针页面回显 label——证明"页面 → 宿主 → 页面"整条通路是通的 |
| 关闭接管取证（`--selftest close`） | 四条路径逐条取证：**页面阻止**（什么都不做 → 窗口必须留开）、**页面放行**（调 `win.destroy` → 必须真的关掉）、**宿主否决优先**（`Closing` 里 Cancel → 页面**不该**收到事件）、**未接管**（直接关掉且页面不该收到事件）。另有"`Destroy()` 能强制关掉被接管的窗口"这条逃生口断言 |
| 窗口 API 取证（`--selftest window`） | **按 label 寻址**：主窗口发命令、作用到另一个窗口（`hide`/`show` 用宿主侧 `IsVisible` 取证；`setTitle` 用 `window.title` 读回**双向**确认——改到了目标、没改到发起者）；**能力门禁两侧**：白名单命令（`window.isVisible`）放行、未声明的（`window.setResizable`）被拒且理由是权限；`window.destroy` 只关目标不关发起者 |
| 前端包取证（`--selftest api`） | **包在真实页面里可用**：18 项断言覆盖五个模块——ESM 动态 import 能加载、`core.invoke` 能往返、`dpi` 舍入复刻 .NET 的 ToEven、`TauriEvent` 与 Tauri 同名同值、`getCurrentWindow().label` 是宿主注入值、`getAllWindows` 含本窗口、`mocks.isMocked()` 在真宿主里为 `false`。这是唯一一处"包真被页面 import 并调用"的取证——包的单测跑在 Node 里，那一侧没有浏览器环境 |
| 无人自检（`OrielDemo --selftest <名字>`） | `nav` `ipc` `theme` `single-instance` `shell` `capability` `multiwindow` `scheme` `exit` `exit-timeout` `threading` `tray` `tray-js` `geometry` `geometry-frameless` `windowevents` `event` `close` `window` `api`——自己驱动页面、打印结论行、以退出码表达成败 |
| 几何/焦点事件取证（`--selftest windowevents`） | `Resized`（程序调 `Resize` 后参数应为逻辑像素的客户区）、`Moved`（`MoveTo` 后应为屏幕逻辑坐标）、`FocusChanged`（**再建一个窗口抢焦点 → 收到 false；关掉它 → 收到 true**）、以及去重（重复设同一尺寸不再触发）。三平台的原生来源完全不同，只能逐平台真机取证——"我接上了信号"与"事件真的到了"是两件事 |
| 尺寸语义取证（`--selftest geometry` / `geometry-frameless`） | **三平台尺寸语义一致性**。前者用**装饰窗口**取证：`WithSize(800,600)` 的客户区必须是 `800x600`（修复前 Windows 上是 `784x561`，原因见下面 §1 的 `WithSize` 说明），并再验一次运行期 `Resize(640,480)`。后者用**无边框窗口**取证反面：客户区不该被多做一次"客户区 → 整窗"换算 |
| 托盘后端取证（`--selftest tray`） | 26 项：`app.png`/`app.ico` **双格式解码**、多托盘（建两个 + 按 id 取回 + 互不影响）、图标对象与**模板标记**如实回读、运行期换图（对象与路径两条入口）、菜单、显隐、标题、按 id 移除、重复 id 抛异常 |
| 托盘前端取证（`--selftest tray-js`） | 页面真实调用 `oriel.tray`：`new` → `getById` → `getAll` → 全部 `set*` → `close` → `onEvent`，回显逐条结论。证明**前后端 API 同名同义且命令往返真的通** |
| 退出语义取证（`--selftest exit` / `exit-timeout`） | 正常退出：`Exiting` 与 `ExitCompleted` 都触发、退出码 `0`。超时强杀：在 `Run()` 返回后堵死 UI 线程（复现现场"清理卡住"），断言看门狗在 `ExitTimeoutDuration` 后强制结束进程、退出码为 `ForceExitCode`（`2`），且 `ExitTimeout` 事件先于强杀触发。**必须子进程取证**：`Environment.Exit` 会把测试运行器一起带走 |
| 线程归属取证（`--selftest threading`） | 在真实消息循环里断言 `IsOnUiThread` 的真值（UI 线程上 `true`、后台线程上 `false`）、`InvokeOnUiThread` 的动作确实执行在 UI 线程上、`PostToUiThread` 正常路径返回 `true` 且回调落在 UI 线程 |
| 事实探针（`--selftest scheme`） | 打印页面来源与"来源决定的能力"（`isSecureContext`、`crypto.subtle`、`localStorage`、相对路径 `fetch`）并写成 GitHub 注解。只断言两条设计承诺（来源是 `oriel://<host>`、相对 `fetch` 取得到资源），其余**如实报告**：三平台的自定义 scheme 能力并不对称，那些值是被量出来的而不是推断的。2026-10-03 三平台基线一致：`origin=oriel://app.oriel secure=true subtle=true storage=ok fetch=ok:200:949` |
| 手动验证操作台（直接运行 demo） | 托盘、通知、对话框、拖放、右键菜单、图标等**只能人眼**判定项，每项都写了预期 |
| Velopack 打包 | Windows **每 push** 跑（`vpk pack` 成功、安装产物齐备、更新包与清单按约定已删）；Linux/macOS 只在 release 跑（三平台各自的产物形态见 §12） |

**CI 覆盖（`.github/workflows/ci.yml`）**——上表里"机器可判定"的项都进了 CI，三平台各自跑：

| Job | 覆盖 |
|---|---|
| `test` | 单测（含图像解码器、托盘事件与选项、内建 `tray.*` 命令）+ 桥接 JS 测试 + `dotnet format` 门禁 + 包内 targets 的消费链路（`tools/verify-package.ps1`） |
| `smoke-linux` | `tools/verify-linux.sh` 一次跑完三阶段：`selftest` + `shell`（托盘/通知/Shell/自启）+ `smoke`（运行取证与截图） |
| `smoke-windows` | `tools/verify-windows.ps1` 一次跑完三阶段：`smoke` + `pack`（Velopack）+ `selftest` |
| `smoke-macos` | `tools/verify-macos.sh`（自检 + 运行取证 + 截图）+ 非本机架构的 AOT 发布 |

**三平台的验证脚本命名与职责一一对应**（`verify-linux.sh` / `verify-windows.ps1` / `verify-macos.sh`），
每个平台一个脚本、内部按阶段分节，CI 里各自只调用一行；都支持只跑某一阶段
（`--stages` / `-Stages`）与"是否先发布"（`--no-publish` / `-Publish`）。

自检阶段覆盖的模式（三平台一致）：`nav` `ipc` `capability` `multiwindow` `scheme` `threading`
`tray` `tray-js` `exit` `exit-timeout` `single-instance`；Linux 与 macOS 另跑 `theme` 的**深浅双路径**
（Windows 没有像 `GTK_THEME` 那样可靠的"造值"入口，只跑一次读得到即可）。

> **workflow 里只留"调用一行脚本"**：验证逻辑全部落在 `tools/` 下的独立脚本里。
> 内联在 `ci.yml` 里的大段 shell/PowerShell **没法在本地跑、没法单独 review**，
> 而 diff 时也看不出改的是哪一段逻辑——这正是 `tools/verify-macos.sh` 一直在做的事，
> 现在三个平台对齐。唯一的例外是那条"变量后紧跟非 ASCII"的跨平台 lint（13 行），
> 它是通用检查、与平台验证无关，留在 workflow 里反而更直观。

> **退出码断言的分平台差异**（有据可依，不是偷懒）：`exit-timeout` 的退出码在三平台都判
> （它由 `Environment.Exit(ForceExitCode)` **直接终止进程**，不经过平台 UI 层，因此可靠）；
> 而 `exit` 的"必须为 0"**只在 Linux/Windows 上判**——macOS 的正常退出走 AppKit 的 `terminate`，
> 实测会吃掉 `Main` 里设的 `Environment.ExitCode`（`verify-macos.sh` 的注释里记录过
> "nav 自检打印 FAIL 却返回 0"）。在 macOS 上断言它会引入一个永远红的假失败，而那不是真实缺陷。

`--selftest ipc` 还会断言注入脚本里的**拖动接管就位**（`oriel.dragRegion` 是函数、标题栏被登记成
拖动区域）与**内建窗口命令往返**（页面调两次 `win.toggleOnTop`，而 demo 没有注册任何 `win.*`）
——前者覆盖"脚本模板能跑通 + DOM 里标的属性被扫到"，后者覆盖"消息 → 内建命令 → 真实窗口"
整条链路；拖动本身的**手感**只能人眼验证（见 [ROADMAP.md](ROADMAP.md)）。
内建命令的表与参数解析由 `tests/OrielWeb.Tests/BuiltInWindowCommandTests.cs` 用假窗口逐条断言
（含"缺参数时报错而不是当 0"「没有来源窗口时报错」与"内建压过应用同名命令"）。

三平台的验证脚本在 `tools/`（`verify-linux.sh`、`verify-windows.ps1`、`verify-macos.sh`），
由 CI 调用；哪些项**已经过真机**、哪些还只是编译验证，见 [ROADMAP.md](ROADMAP.md)。

失败时，CI 里的冒烟步骤与打包步骤都会把原因写成 **GitHub 注解**（各自检模式的结论行、`vpk` 自己的
报错），而不是只留一个"exit code 1"：注解用 GitHub API 就能读到，作业日志需要 token——这个差别
决定了排查能不能在不登录的情况下做完（2026-10-02 的几次事故都是靠它定位的）。

注解不只用于失败：`--selftest scheme` 每轮都会把三平台的"来源与来源决定的能力"作为 notice 写进去，
于是那些**没人能靠推理确定**的平台事实（比如 macOS 的自定义 scheme 算不算安全上下文）在 CI 上是
可读的、可对比的，而不是等用户报告"我的页面用不了 SubtleCrypto"。
