# OrielWeb API

按能力分章的完整说明。每章末尾的 **为什么** 是取舍记录——它解释的是"为什么不能换成另一种看起来更自然的写法"，
因为这些地方一旦被"优化"回去就会静默出错。

- [1. 应用与窗口](#1-应用与窗口)
- [2. 无边框窗口](#2-无边框窗口)
- [3. 导航与页面通信](#3-导航与页面通信)
- [4. 安全与能力模型](#4-安全与能力模型)
- [5. 拖动与双击的实现边界](#5-拖动与双击的实现边界)
- [6. 剪贴板、系统主题、单实例](#6-剪贴板系统主题单实例)
- [7. 平台集成](#7-平台集成)
- [8. 对话框](#8-对话框)
- [9. 文件拖放](#9-文件拖放)
- [10. 内建右键菜单](#10-内建右键菜单)
- [11. 页面侧 API](#11-页面侧-api)
- [12. 命令行工具 oriel](#12-命令行工具-oriel)
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

| 方法 | 说明 |
|---|---|
| `UseEmbeddedAssets(host, resourcePrefix)` | 启用内嵌前端资源：程序集内嵌资源经 `https://<host>/` 提供（默认 host `app.oriel`，默认前缀 `程序集名.wwwroot.`） |
| `UseJsonContext(ctx)` | 注册 STJ 源生成上下文：DTO 命令参数/返回值的 AOT 安全序列化入口。**按应用实例持有**，不写全局静态状态 |
| `AddCommands<T>()` / `AddCommands<T>(factory)` | 注册含 `[OrielCommand]` 的类型（惰性单例）。命令实例**共享**，所有 invoke 作用在同一实例上 → **命令方法必须线程安全** |
| `AddWindow(configure, onCreated)` | 加窗口；`onCreated` 在 `Run()` 之前回调，用于订阅 `Loaded`/`Closing` |
| `UseDebug(bool = true)` | 打开 DevTools（三平台的实现见下） |
| `SingleInstance(id, onActivate)` | 单实例：第二个实例通知首实例后**立即以 0 退出**（不建窗） |
| `UseCapabilities(configure)` | 声明页面可调用的命令（见 §4） |
| `AddTray(configure)` | 启用系统托盘（应用级，最多一个） |
| `UseShell(configure)` | Shell 集成的 scheme 白名单（默认只放行 `http`/`https`/`mailto`） |
| `UseAutoStartId(id)` | 覆盖开机自启的标识（默认取可执行文件名） |
| `UseNotificationAppId(id)` | 覆盖系统通知的应用标识（Windows AUMID / Linux `--app-name`；默认取入口程序集名） |
| `UseUserDataFolder(path)` | 覆盖 WebView2 用户数据目录（默认 `%LOCALAPPDATA%\OrielWeb\WebView2`） |
| `OnWebView2RuntimeMissing(handler)` | 自定义"运行时缺失"的提示与引导（不注册时库弹默认错误框） |

### 窗口选项（`OrielWindowOptions`，`AddWindow` 的 configure 里链式调用）

`WithTitle(string)`、`WithSize(int, int)`、`WithMinSize(int, int)`、`WithFrameless()`、`WithHidden()`、
`WithUrl(string)`、`WithIcon(string path)`、`WithConsoleForwarding()`、`Centered()`、`Debug` 属性。

`OrielApp`：`Theme` / `ThemeChanged`、`Windows`、`CreateWindow(configure, onCreated)`、`Tray`、
`NotificationsSupported`、`IsAutoStartEnabled` / `SetAutoStart(bool)`、`OpenExternal(url)`、
`RevealInFileManager(path)`、`PostToMainThread(Action)`。

### 多窗口

```csharp
// 运行时新建窗口。必须在 UI 线程调用——命令处理、窗口事件、托盘回调里都在 UI 线程上。
WebviewWindow second = app.CreateWindow(
    w => w.WithTitle("第二个窗口").WithSize(800, 600),
    win => win.Loaded += () => Console.WriteLine("第二个窗口就绪"));
```

| 语义 | 说明 |
|---|---|
| 一个应用、多个窗口 | 内嵌资源目录、可信来源、IPC 令牌与能力配置都继承**应用级**的那些；页面各有一份，命令与门禁共用 |
| `win.*` 只作用到发起者 | 内建窗口命令按"发起调用的那个窗口"路由，多窗口下不会打错窗口 |
| 关掉一个不退出 | 所有窗口都关掉之后消息循环才结束（Windows 由存活窗口计数归零触发 `WM_QUIT`，macOS 由 `applicationShouldTerminateAfterLastWindowClosed:` 回答） |
| `Windows` 会自动摘除已关闭的窗口 | 摘除发生在公共 `Closed` 事件**之前**：处理器里读到的列表不含刚关掉的那个 |
| 必须在 UI 线程调用 | 刻意不做自动 marshal：返回值是同步的窗口对象，"marshal 过去再建"会让调用方拿到一个尚未建好的窗口 |

> **Windows 上全进程共享一个 WebView2 环境**：user data folder 是全进程一个目录，而 WebView2 不允许
> 同一目录上并存两个环境——所以环境由平台后端持有并复用（一份环境 + 每个窗口一个控制器）。
> `UseUserDataFolder(path)` 改的就是这个目录。

### `WebviewWindow`

| 分组 | 成员 |
|---|---|
| 显隐与状态 | `Show()` `Hide()` `Close()` `Focus()` `Maximize()` `Minimize()` `Restore()` `SetFullscreen(bool)` `SetOnTop(bool)` `IsMaximized` `ToggleMaximize()` `ToggleFullscreen()` `ToggleOnTop()` |
| 几何与外观 | `SetTitle` `SetResizable` `SetMinSize` `MoveTo` `Resize` `Center` |
| 导航 | `GoBack` `GoForward` `Reload` `CanGoBack` `CanGoForward` |
| 拖动 | `BeginDrag()` `BeginDragStreaming(px,py,wx,wy,ww,wh,sh)` `DragTo(dx,dy)` `EndDrag()` |
| 剪贴板 | `ClipboardText` `SetClipboardText` `ClipboardHtml` `SetClipboardHtml(html, plainTextFallback)` |
| IPC | `EmitEvent(name, json)` `EmitEvent<T>(name, payload, JsonTypeInfo<T>)` `EvaluateJs(script)` `PostToUiThread(Action)` |
| 对话框 | `ShowMessage` `ShowOpenFileDialog` `ShowSaveFileDialog` `ShowFolderDialog` |
| 菜单 | `ShowContextMenu(items)` `ContextMenuPolicy` |
| 事件 | `Loaded` `Closing` `Closed` `TitleChanged` `MaximizedChanged` `NavigationStarting` `NavigationCompleted` `ConsoleMessage` `MessageReceived` `ContextMenuItemClicked` `FileDropped` |

事件参数类型：`OrielNavigationCompletedEventArgs`（`Success`/`Url`/`Error`）、`OrielConsoleMessageEventArgs`（`Level`/`Text`）、
`OrielMessageReceivedEventArgs`（`Name`/`Json`）、`OrielFileDropEventArgs`、`OrielCloseRequestEventArgs`（`Cancel`）。

> **命令线程模型**：命令执行在**后台线程**（不阻塞 UI 消息循环），回执由分发器切回 UI 线程投递。
> 命令内要碰窗口时经 `OrielApp.PostToMainThread(...)`；应用自己的异步流程在 `await` 之后要用
> `WebviewWindow.PostToUiThread(...)`——**GTK 与 AppKit 只允许在各自的主线程调用窗口 API，从线程池调用会直接崩**。

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

**这些命令由库内建**：应用一行不注册就能用（页面里 `oriel.invoke('win.minimize')` 直接工作）。
它们实现里最终调用的 `WebviewWindow.BeginDrag` / `BeginDragStreaming` / `DragTo` / `EndDrag`
仍是公开 API，应用要自己编排窗口动作时照样可以用。

> **`win.` 是保留前缀，而且现在真的被占用了**：命中内建表的名字**不会**再走应用注册的命令，
> 应用自定义的 `win.*` 会被**静默遮蔽**（注册表只查"路由之间是否重名"，它不知道内建的存在，
> 所以重名不会在启动时报错）。应用自己的命令请换前缀——demo 操作台里那份覆盖各形态的丰富菜单
> 就叫 `manual.contextMenu`。
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

窗口图标：`WithIcon(path)` 三平台各自落到真正有图标槽的位置——Windows `WM_SETICON`、Linux 窗口图标、
macOS Dock 图标。**Windows 会主动从 exe 取图标**（`<ApplicationIcon>`），另外两个平台**没有等价来源**
（ELF/Mach-O 不带图标），所以必须显式给文件；GNOME 还不读 `_NET_WM_ICON`，它按 `WM_CLASS` 匹配 `.desktop` 的 `Icon=`。
`WithIcon` 会校验文件存在——路径写错时立刻报错，而不是安静地显示通用图标。

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
> **失败导航必须报出原因**：三个引擎"接受的失败目标"各不相同（同源缺失页 / 环回端口 / 保留 TLD 各有差异），
> 且都会静默改写不接受的形态，所以自检里逐个试候选目标，任一真的上报失败即算通过。

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
| `win.` 前缀始终放行 | 那是无边框窗口的标题栏按钮与拖动，只操作自己那个窗口，不是"能力"；它们由库内建，自定义同名命令会被遮蔽。**应用自己的命令请避开这个前缀** |
| 只管 `invoke` | 单向的 `postMessage` 没有命令名，由来源与令牌两层覆盖 |

| 层 | 挡什么 |
|---|---|
| 来源 | 只有内嵌资源（与 `AllowOrigin` 放行的来源）算可信。**其它来源里桥接脚本根本不安装**——远程页面里连 `window.oriel` 都不存在，而不是"装上了再拦" |
| 令牌 | 每进程一个 128 位随机串，随脚本注入、每条入站消息回带。挡的是"不是本应用注入的脚本也往通道里塞消息"，**不是**防网络攻击的凭据（消息不经过网络） |
| 命令授权 | 按 allow/deny 名单判定命令名 |

被拒绝时页面侧那个 Promise 会 **reject 并带上原因**（"落进了 Deny 名单"还是"不在 Allow 名单里"），
而不是让它等到超时——"点了没反应"和"这个命令没被授权"必须能分开。

> **为什么 `win.*` 要开一个后门**：Release 下未配置即拒绝这条规则若把它一起拒掉，
> 无边框窗口会**直接变成关不掉的窗口**（自绘标题栏，没有系统标题栏可替代，用户只能去任务管理器）。
>
> **"Debug 还是 Release"取的是消费方**：本库以 Release 发布，它自己的 `#if DEBUG` 永远是 false。
> 先看包内 targets 注入的 `AssemblyMetadata("OrielBuildConfiguration")`，取不到回退到入口程序集的
> `DebuggableAttribute`，两条都没有时按 Release 算（与 fail-closed 一致）。
>
> **为什么来源校验放在注入期**：这样"远程页面不接 IPC"是**结构性成立**的，
> 不必依赖宿主侧每次再判一次，也不存在"远程页面拿到了令牌"这回事。
> 注意 Linux/macOS 上内嵌资源的 `https://` 虚拟主机注册不了、导航前会被改写成 `file://`，
> 所以解压目录也是可信前缀——少了这一条，页面会被**自己的**门禁拒掉，表现为"什么命令都没反应"。

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

## 6. 剪贴板、系统主题、单实例

```csharp
string? text = window.ClipboardText;
window.SetClipboardText("你好");
window.SetClipboardHtml("<b>你好</b>", "你好");   // 第二个参数是纯文本回退

OrielTheme theme = app.Theme;                    // Light / Dark
app.ThemeChanged += t => { };
```

```js
oriel.on('theme.changed', (t) => document.documentElement.dataset.theme = t);
```

三平台写 HTML 时都**同时写一份纯文本回退**（macOS 用 `NSPasteboard`、Windows 用 CF_HTML、
Linux 走自定义 target），只认文本的应用也能粘贴。

```csharp
builder.SingleInstance("com.example.myapp", win => { /* 首实例被唤醒 */ });
```

> **单实例的判定不能用"命名管道能否创建成功"**：管道名冲突与"已经有实例在跑"是两件事，
> 用一个**独占文件锁**判定、再用命名管道通知，才是可靠的形态。
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
| Shell | `app.OpenExternal(url)`、`app.RevealInFileManager(path)` | **默认拒绝式的 scheme 白名单**（`http`/`https`/`mailto`），可 `UseShell` 扩展 |
| 开机自启 | `app.SetAutoStart(true)` / `IsAutoStartEnabled` | Windows 写 HKCU 的 Run 键、macOS 写 LaunchAgent plist（不调 `launchctl load`，避免立刻又拉起一个）、Linux 写 autostart `.desktop` |

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

## 10. 内建右键菜单

```csharp
window.ContextMenuPolicy = OrielContextMenuPolicy.Editing;  // Editing（默认）/ Native / Disabled
```

| 值 | 行为 |
|---|---|
| `Editing` | 只留剪切/复制/粘贴。**Windows 是过滤式**（订阅 `ContextMenuRequested` 按名字保留三项）；**macOS/Linux 是接管式**：自己弹一个只含三项的菜单，编辑命令走引擎的原生通道 |
| `Native` | 平台原样 |
| `Disabled` | 完全不弹 |

> **为什么 macOS/Linux 是接管而不是"就地增删引擎菜单"**：后者在 WebKitGTK 4.1 上会破坏内存
> （连点右键几次就崩），所以改成自己弹菜单、编辑命令经 `webkit_web_view_execute_editing_command` 走引擎。

## 11. 页面侧 API

桥接脚本在文档创建时注入，只安装到**可信来源**（见 §4）。

| 成员 | 说明 |
|---|---|
| `oriel.ready` | Promise，脚本就绪（推荐用它而不是 `orielready` 事件） |
| `oriel.platform` | `'windows'` / `'macos'` / `'linux'` |
| `oriel.version` | 库版本（`Major.Minor.Build`，由宿主注入，不是写死的） |
| `oriel.timeout` | 命令回执超时（毫秒，默认 30000；页面可改，`<=0` 不启用） |
| `oriel.dragRegion(target?)` | 登记拖动区域（见 §2）：不传参数 = 重新扫描整个文档；返回登记的元素数 |
| `oriel.invoke(name, args)` | → `Promise`；失败时 reject 带原因 |
| `oriel.postMessage(name, payload)` | 单向通知 |
| `oriel.on(name, handler)` | 订阅宿主事件，返回退订函数 |

`orielready` 事件也保留（兼容用），但它在 `DOMContentLoaded` 才派发——`DOMContentLoaded` 之后才注册的监听器收不到，
所以文档主推 `await oriel.ready`。

## 12. 打包与分发（Velopack）

安装包与自动更新交给 [Velopack](https://velopack.io)（`vpk`）。**本库不自带打包器**：打包不是 webview
能力，产物形态（Setup.exe / nupkg / RELEASES / 可选 .msi）由 Velopack 决定，我们只把参数喂进去。

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
| 单测（`tests/OrielWeb.Tests`） | 分发器与回执协议、能力模型、内建窗口命令表、资源 URL 解析、文件/Shell/对话框的纯函数、快照回退与数值格式化 |
| 桥接测试（`tests/bridge/bridge.test.mjs`） | 三平台共用脚本的行为一致性：就绪、事件、往返、回执、超时、不可信来源不安装、每条出站消息带令牌 |
| 无人自检（`OrielDemo --selftest <名字>`） | `nav` `ipc` `clipboard` `theme` `single-instance` `shell` `capability` `multiwindow`——自己驱动页面、打印结论行、以退出码表达成败 |
| 手动验证操作台（直接运行 demo） | 托盘、通知、对话框、拖放、右键菜单、图标等**只能人眼**判定项，每项都写了预期 |
| Velopack 打包（CI 每 push 跑） | `vpk pack` 成功、产物齐备（含 `--msi`）、更新清单能被解析且版本正确 |

`--selftest ipc` 还会断言注入脚本里的**拖动接管就位**（`oriel.dragRegion` 是函数、标题栏被登记成
拖动区域）与**内建窗口命令往返**（页面调两次 `win.toggleOnTop`，而 demo 没有注册任何 `win.*`）
——前者覆盖"脚本模板能跑通 + DOM 里标的属性被扫到"，后者覆盖"消息 → 内建命令 → 真实窗口"
整条链路；拖动本身的**手感**只能人眼验证（见 [ROADMAP.md](ROADMAP.md)）。
内建命令的表与参数解析由 `tests/OrielWeb.Tests/BuiltInWindowCommandTests.cs` 用假窗口逐条断言
（含"缺参数时报错而不是当 0"「没有来源窗口时报错」与"内建压过应用同名命令"）。

三平台的具体取证脚本在 `tools/`（`verify-linux.sh`、`verify-linux-shell.sh`、`verify-macos.sh`、`verify-pack.ps1`），
由 CI 调用；哪些项**已经过真机**、哪些还只是编译验证，见 [ROADMAP.md](ROADMAP.md)。
