# OrielWeb · 架构方案与里程碑计划

## 架构总览

```
┌─────────────────────────────────────────────────────────┐
│ 应用程序（Native AOT 发布）                                │
│  Program.cs  [STAThread] Main                            │
│  [OrielCommand] 命令类  +  JsonSerializerContext (STJ源生成)│
└──────────────┬──────────────────────────────┬────────────┘
               │ 引用                          │ Roslyn 分析器
┌──────────────▼──────────────┐  ┌────────────▼─────────────┐
│ OrielWeb（核心库，单包）       │  │ OrielWeb.Generators      │
│  App:      OrielApp/Builder │◄─┤  编译期生成：              │
│  Window:   WebviewWindow     │  │   ICommandRouter 实现     │
│  Ipc:      Registry/Dispatcher│ │   [ModuleInitializer] 注册│
│  Bridge:   注入 JS           │  │  零反射、O(1) switch 分发  │
│  Assets:   内嵌资源解压       │  └──────────────────────────┘
│  Platform: IPlatformBackend  │
│   ├─ Windows: Win32+WebView2 │  [GeneratedComInterface]
│   ├─ macOS:   WKWebView      │  （后续里程碑）
│   └─ Linux:   GTK+WebKitGTK  │  （后续里程碑）
└─────────────────────────────┘
```

### 分层职责

| 层 | 内容 | 说明 |
|----|------|------|
| **App** | `OrielApp.CreateBuilder(args)` → `UseEmbeddedAssets / UseJsonContext / AddCommands<T> / AddWindow` → `Run()` | 应用生命周期与消息循环入口；多窗口列表 |
| **Window** | `WebviewWindow`（公共门面）+ `OrielWindowOptions` + 事件 | pywebview 基本面：Title/Size/MinSize/Resizable/Fullscreen/OnTop/Frameless/Hidden/Maximized；事件 Loaded/Closing(可取消)/Closed/TitleChanged；`EvaluateJs` |
| **Ipc** | `OrielCommandRegistry` + `OrielCommandDispatcher` | 协议：`{__oriel:"invoke",id,name,args}` → `{__oriel:"result",id,ok,value|error}`；基元类型手写 Utf8Json 读写，DTO 走用户注册的 `JsonSerializerContext` |
| **Bridge** | `document` 创建前注入的 JS | `window.oriel.invoke(name, args)` 返回 Promise；`orielready` 就绪事件；通道 `chrome.webview.postMessage`（WebView2）|
| **Assets** | `EmbeddedAssetExtractor` | csproj 里 `EmbeddedResource Include="wwwroot/**"`；启动解压至 `%LOCALAPPDATA%\OrielWeb\<app>\www`；映射虚拟主机 `https://app.oriel/` |
| **Platform** | `IPlatformBackend` / `IWindowBackend` | 平台差异收敛点；未支持平台抛明确 `PlatformNotSupportedException` |

### Windows 实现要点（M1）

- 窗口：`RegisterClassExW` + `CreateWindowExW` + `WndProc`（静态委托常驻防 GC）；`WM_SIZE`→控制器 Bounds、`WM_CLOSE`→Closing 事件（可取消）、`WM_APP+n`→主线程调度队列；PerMonitorV2 DPI。
- 全屏：样式切换 popup + 显示器矩形；置顶：`SetWindowPos` TOPMOST；无边框：样式去 WS_CAPTION。
- WebView2：`[LibraryImport]` 直调 `WebView2Loader.dll!CreateCoreWebView2EnvironmentWithOptions`（userDataFolder 指向 `%LOCALAPPDATA%\OrielWeb`，options 传 null 用默认）；env→controller 两段异步回调均在 UI 线程。
- COM 面：`[GeneratedComInterface]` 手写最小集：Environment / Environment3 / WebView2 / WebView2_3 / Controller / Settings / WebMessageReceived(Handler+Args) / NavigationCompleted(Handler+Args)。
- 对话框：`MessageBoxW`、`GetOpenFileNameW`/`GetSaveFileNameW`（comdlg32，平面 P/Invoke，AOT 安全；现代 IFileDialog 为后续优化）。
- Loader 获取：引用 `Microsoft.Web.WebView2` 包仅取 `WebView2Loader.dll` 原生位（`ExcludeAssets="compile"`；托管程序集不被引用，AOT 裁剪自动丢弃）。
- 入口强制 `[STAThread]`，`GetApartmentState()` 检测给出可读报错。

### 源生成器（M1）

- 增量生成器（`IIncrementalGenerator`）扫描 `[OrielCommand("name")]` 方法。
- 每个含命令的类生成一个 `ICommandRouter`：`switch` 按命令名 O(1) 分发；参数从 `JsonDocument` 逐字段强类型提取；DTO 参数经 `OrielJson.Resolve(typeof(T))` → 用户上下文 `JsonTypeInfo` 反序列化。
- 程序集生成 `[ModuleInitializer]` 自动注册全部 Router——应用侧零手工登记。
- 静态命令直接调用；实例命令经 `AddCommands<T>` 注册的工厂创建。

## 里程碑

| 里程碑 | 范围 | 验收 |
|--------|------|------|
| **M1 Windows MVP（当前）** | 核心抽象 + Windows 平台 + 源生成器 + demo | demo 在 Windows `PublishAot=true` 单文件发布并正常运行：窗口显示内嵌 UI、JS↔C# 双向 IPC、事件、全屏/置顶/无边框/对话框 |
| M2 macOS | WKWebView + ObjC runtime P/Invoke | 同 M1 验收 |
| M3 Linux | GTK + WebKitGTK P/Invoke | 同 M1 验收 |
| M4 打磨 | 内存直供 scheme、IFileDialog、多窗口全面验证、NuGet 打包与 CI（含 aot.yml）、测试 | NuGet 预览包 |

## 目录结构

```
OrielWeb/
├── OrielWeb.sln
├── Directory.Build.props          # net10.0 / nullable / IsAotCompatible / IsTrimmable
├── docs/                          # 本文档与决策记录
├── src/OrielWeb/                  # 核心库（App/Window/Ipc/Bridge/Assets/Platform）
├── src/OrielWeb.Generators/       # Roslyn 源生成器（netstandard2.0）
└── samples/OrielDemo/             # AOT demo（todo 应用）
```

## 使用形态（目标 API）

```csharp
[STAThread]
static void Main(string[] args) =>
    Oriel.CreateBuilder(args)
        .UseEmbeddedAssets()                        // 虚拟主机 https://app.oriel/ ← wwwroot/**
        .UseJsonContext(AppJsonContext.Default)     // DTO 的 STJ 源生成上下文
        .AddCommands<TodoCommands>()                // [OrielCommand] 命令类
        .UseDebug()                                 // DevTools
        .AddWindow(w => w.WithTitle("Oriel Demo — Todo")
                         .WithSize(1024, 720)
                         .WithMinSize(640, 480)
                         .Centered())
        .Run();   // 需要订阅窗口事件时用重载 AddWindow(cfg, win => win.Loaded += ...)

public partial class TodoCommands
{
    [OrielCommand("todo.list")] public IReadOnlyList<TodoItem> List() => ...;
    [OrielCommand("todo.add")]  public TodoItem Add(string text) => ...;
}
```

```js
await window.oriel.ready;                 // 就绪 Promise（orielready 事件）
const items = await window.oriel.invoke('todo.list');
await window.oriel.invoke('todo.add', { text: '买牛奶' });
```

发布：`dotnet publish samples\OrielDemo -c Release -r win-x64`（csproj 内置 `PublishAot=true`）。

## M1 实现状态（2026-09-27）

- ✅ 全部达成：窗口与 pywebview 基本面方法、手工 CCW 回调、零反射源生成 IPC（[ModuleInitializer] 自动注册）、虚拟主机 + 内嵌资源、Native AOT 单文件发布（demo 约 2MB + WebView2Loader.dll）并稳定运行；resize / 最大化 / 还原 / **IPC 端到端往返（invoke→命令→回执→Promise resolve）**均程序化验证通过。
- ✅ 无边框窗口 demo：自绘标题栏（拖动/双击最大化/最小化/关闭按钮）、`win.*` 控制命令（BeginDrag 用 WM_NCLBUTTONDOWN+HTCAPTION 技巧）、WM_GETMINMAXINFO 将最大化限制到工作区（任务栏不被遮挡，已程序化验证）。
- 环境变量 `ORIEL_WEBVIEW2_FOLDER`：可固定 WebView2 运行时版本（browserExecutableFolder），不设则用系统 Evergreen 最新版。
- 已知限制（M4 处理）：资源名映射按"最后一个 '.' 为扩展名"解析（文件名主干再含 `.` 的资源不支持）；Resize 按窗口外尺寸；文件对话框用 comdlg32（非 IFileDialog）；内存直供 scheme 未实现；WM_MOVE 不调用 NotifyParentWindowPositionChanged（由 WM_SIZE 的 put_Bounds 兜底）；文档标题自动同步为窗口标题。
- 参考实现：[smourier/WebView2Aot](https://github.com/smourier/WebView2Aot)（手工互操作路线验证）、官方 WebView2.h（vtable 槽位权威来源，`.reference/` 内有副本）。
- 互操作教训（详见 DECISIONS.md）：①本机运行时的 ComWrappers CCW/RCW 均不可靠，双向均为手工实现——回调侧手工 vtable + UnmanagedCallersOnly + 手动引用计数，调用侧指针包装结构按槽位直调；②回调参数必须 AddRef 才能存过回调生命周期，否则环境/控制器析构、浏览器进程树关闭，之后任何 COM 调用都会 AV；③所有 UnmanagedCallersOnly thunk 全身 try/catch，异常一律转 HRESULT，禁止逃逸。

## M2 macOS 实现状态（2026-09-27，代码完成、待真机验证）

- ✅ 纯 C# ObjC runtime 互操作（`/usr/lib/libobjc.A.dylib` P/Invoke + objc_msgSend 按签名声明），无 C++ 组件、无 ComWrappers。
- ✅ 手工类对（`objc_allocateClassPair`）：AppDelegate / WindowDelegate / ScriptMessageHandler / NavigationDelegate / 主线程泵，全部 `[UnmanagedCallersOnly]` trampoline（返回 nint）。
- ✅ NSWindow + WKWebView：创建、加载（loadFileURL 内嵌资产 / loadRequest 外部 URL）、IPC（WKScriptMessageHandler 收 + evaluateJavaScript 发，避开 ObjC block）、桥接 JS 注入（WKUserScript）、标题同步、窗口全 API（全屏/置顶/缩放/最小尺寸…）、对话框（NSOpenPanel/SavePanel）。
- ✅ 编译验证：Windows 本机 + `-r osx-arm64` + `-r osx-x64` 均通过（AOT 链接需在 macOS 上执行）。
- ⚠️ 待真机验证清单（Mac 上执行 `dotnet publish samples/OrielDemo -c Release -r osx-arm64`）：窗口创建/显示、页面渲染、IPC 往返、拖动、关闭退出、多窗口。
- 设计要点：①不用 ObjC block（完成回调经页面回环消息，evaluateJavaScript 传 NULL completion）；②不做 struct 返回的 msgSend（窗口原点由 JS 提供 + 宿主跟踪增量）；③NSPoint/NSSize/NSRect 参数以分离 double 声明（x64 SysV 与 arm64 的整数/浮点寄存器分库天然对齐）；④回调参数/委托实例均 objc_retain 长期持有；⑤拖动为流式坐标模型（win.dragStart/dragTo/dragEnd），Windows 原生模态拖动不受影响。

## M3 Linux 实现状态（2026-09-27，代码完成、待 Linux 环境验证）

- ✅ 纯 C# GTK3 + WebKitGTK 4.1 P/Invoke（`libgtk-3.so.0` / `libwebkit2gtk-4.1.so.0`），无 C 组件、无 ComWrappers。
- ✅ LinuxSignalHandlers：GTK 信号 trampoline（delete-event/destroy/load-changed/notify::title/script-message-received）+ 状态注册表 + g_idle_add_full 主线程泵。
- ✅ LinuxWindowHost：GTK 窗口 + WebKitWebView 全套窗口 API + load_uri 内嵌资产/外部 URL + IPC（script-message-received 收 + evaluate_javascript 发）+ 桥接注入（webkit_user_script_new）+ 标题同步 + GtkFileChooserDialog + GtkMessageDialog。
- ✅ 编译验证：Windows 本机 + `-r linux-x64` 均通过；测试 33/33 全绿。
- 依赖：`apt install libwebkit2gtk-4.1-dev`（或对应发行版包）。
- 已知限制：Wayland 下 gtk_window_move 不可靠（拖动可能不生效）；HiDPI 缩放下拖动偏移；文件对话框用 GTK chooser（filter 限 M4）。
- 设计与 macOS 共享全部架构模式（回环消息、状态注册表、[UnmanagedCallersOnly] trampoline、流式拖动）。

## M4 实现状态（2026-09-27）

- ✅ NuGet 打包：`OrielWeb` + `OrielWeb.Generators` 双包元数据（License/Tags/Readme/Symbols），分析器 DLL → `analyzers/dotnet/cs`；本地开发用 ProjectReference 直引。
- ✅ CI（[.github/workflows/ci.yml](.github/workflows/ci.yml)）：Windows 测试 + Windows AOT 发布 + macOS 编译/AOT（arm64+x64）+ Linux 依赖安装/编译/AOT。
- ✅ README.md：快速开始 / 无边框窗口 / IPC 命令定义 / 构建命令 / 平台矩阵。
- ✅ .gitignore。
- ✅ IPC 测试套件 33/33（`tests/OrielWeb.Tests/`）：参数提取全基元类型、DTO 往返、分发器端到端、错误路径、协议形状、并发 50 并发全成功。
- ✅ 端到端验证：窗口加载（title 同步）+ IPC 全链路（invoke→命令→回执→Promise resolve）+ resize/最大化/还原稳定。

## 后续路线图

### 短期（功能补齐）

| 项 | 说明 | 涉及平台 |
|----|------|----------|
| 内存直供 scheme | 替代临时目录解压；Windows 用 WebResourceRequested + IStream，macOS 用 WKURLSchemeHandler，Linux 用 webkit_web_context_register_uri_scheme | 三平台 |
| IFileDialog | 替换 comdlg32（Windows）/ GTK chooser（Linux）；macOS NSOpenPanel 已到位 | Windows/Linux |
| WM_NCHITTEST | 无边框窗口边缘 resize 热区自定义（当前 WS_THICKFRAME 覆盖与内容重叠） | Windows |
| Wayland 拖动 | GTK3 Wayland 下 gtk_window_move 不可靠；需 GTK4 或 layer-shell 协议 | Linux |
| HiDPI 拖动偏移 | GTK3 屏幕坐标为设备像素，JS 为 CSS 点，需乘 scale factor | Linux |
| 多窗口验证 | AddWindow 多次 + 窗口间 IPC + 窗口生命周期独立测试 | 三平台 |
| 窗口图标 | Windows LoadIcon + macOS NSWindow setRepresentedURL/Icon | Windows/macOS |
| 深色模式标题栏 | Windows DwmSetWindowAttribute DWMWA_USE_IMMERSIVE_DARK_MODE | Windows |

### 中期（功能增强）

| 项 | 说明 |
|----|------|
| CLI 工具（类 Tauri CLI） | `oriel new`（脚手架）/ `oriel dev`（dev server + 热重载）/ `oriel build`（打包安装器） |
| 系统托盘 | Windows NotifyIcon / macOS NSStatusItem / Linux AppIndicator |
| 原生菜单 | Windows HMENU / macOS NSMenu / Linux GtkMenuBar |
| 通知 | Windows Toast / macOS NSUserNotification / Linux libnotify |
| 自动更新 | 内嵌更新检查（Tauri updater 模式） |
| 多窗口 API | `app.OpenWindow(name, options)` + 窗口间通信 `window.emit(name, data)` |
| WebView 事件增强 | 页面导航拦截（before-navigate）、下载处理、右键菜单自定义 |
| 安全 | IPC 白名单/能力模型（Ryn 的 ryn.json 能力模型参考） |

### 长期（生态）

| 项 | 说明 |
|----|------|
| NuGet 发布 | 发布 OrielWeb + OrielWeb.Generators 到 nuget.org（CI 自动打包） |
| 模板包 | `dotnet new install OrielWeb.Templates` → `dotnet new orielweb` 脚手架 |
| 移动端 | iOS（WKWebView + ObjC runtime）+ Android（系统 WebView + JNI）——架构预留，优先级最低 |
| 文档站 | API 参考 + 教程 + 示例集 |

### 已知限制（按优先级排序）

| 优先级 | 限制 | 影响 | 解决方案 |
|--------|------|------|----------|
| P1 | 资源文件名含 `.`（如 `app.min.js`）解压路径歧义 | 部分前端构建产物 | 嵌入 zip 或 manifest |
| P1 | macOS/Linux 运行时未经真机验证 | 发布信心 | CI AOT job + Mac/Linux 环境 |
| P2 | Resize 按窗口外尺寸（非客户区） | Windows 体验偏差 | AdjustWindowRectEx 换算 |
| P2 | Linux GTK3 屏幕坐标为设备像素（HiDPI 偏移） | Linux 拖动偏移 | GdkMonitor scale factor |
| P2 | macOS WM_MOVE 不调 NotifyParentWindowPositionChanged | 无边框移动后子视图偏移 | 补调用（需确认安全） |
| P3 | 文件对话框 filter 不生效（Linux/macOS） | 体验 | nsftypes → macOS allowedContentTypes / GTK filter |
| P3 |ExecuteScript 在 macOS/Linux 上返回 "E:…" 前缀的字符串而非 JSON null | 语义不一致 | 统一错误模型 |
