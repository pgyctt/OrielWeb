# OrielWeb

类 Tauri 的 C# 跨平台系统 webview 核心库。无 C++ 中间层、无 GUI 框架依赖、Native AOT 友好、零反射 IPC。

## 特性

- **纯 C# 互操作**：macOS（WKWebView + ObjC runtime）与 Linux（GTK3 + WebKitGTK）为手写 P/Invoke；Windows 的 WebView2 COM 走 `WebView2Aot` 的 `[GeneratedComInterface]`/`[GeneratedComClass]` **源生成绑定**（无手写 vtable/IID/RefCount）。三平台均不需要 C++ 中间层
- **Composition 宿主**（Windows）：WebView2 作为 DirectComposition 的一份视觉合成进窗口，而非子窗口——因此无边框窗口的边缘 resize 能走系统原生路径，且窗口内容可与其它视觉自由合成
- **零反射 IPC**：`[OrielCommand]` + Roslyn 源生成器在编译期生成分发代码，`[ModuleInitializer]` 自动注册，运行期零反射
- **Native AOT**：全局 `IsAotCompatible`/`IsTrimmable`，发布为原生单文件可执行文件（WebView2 的运行时加载器已内嵌，无需旁文件）
- **无边框窗口**：自绘标题栏 + 流式/原生拖动 + 最大化/全屏/置顶切换
- **导航与双向通信**：前进/后退/刷新 + 导航事件（开始/完成/失败，带错误信息）、页面 console 转发、
  `EmitEvent(name, payload)` 推事件、`oriel.postMessage` 收消息——三条通道都能在无头环境里机器断言
  （见 `--nav-selftest` / `--ipc-selftest`）
- **平台集成**：剪贴板（文本 + HTML）、系统主题（深/浅 + 变更事件）、单实例、系统托盘、系统通知、
  **菜单**（应用菜单 + 窗口上下文菜单，含平台 role 与加速键）——都有无头自检
  （见 `--clipboard-selftest` / `--theme-selftest` / `--shell-selftest` / 单实例的双进程断言）
- **系统 webview**：Windows 用 WebView2、macOS 用 WKWebView、Linux 用 WebKitGTK——不捆绑浏览器内核

## 快速开始

要求 **.NET 10 SDK**（本库只提供 `net10.0` 目标）。

```bash
dotnet add package OrielWeb
```

IPC 路由由随包分发的 Roslyn 源生成器在编译期生成——生成器 DLL 打在包的 `analyzers/dotnet/cs`，
NuGet 会自动运行它，因此不需要额外的包。

```xml
<!-- 应用项目：其余用 dotnet new 的默认值即可 -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>             <!-- 不显示控制台窗口 -->
    <TargetFramework>net10.0</TargetFramework>
    <PublishAot>true</PublishAot>               <!-- 原生单文件发布 -->
    <ApplicationIcon>app.ico</ApplicationIcon>  <!-- 任务栏图标，见「应用图标」 -->
  </PropertyGroup>
  <ItemGroup>
    <EmbeddedResource Include="wwwroot\**\*" /> <!-- 前端资源内嵌 -->
  </ItemGroup>
</Project>
```

在仓库内开发时（而不是引用 NuGet 包），把包引用换成项目引用。注意分析器不会随
`ProjectReference` 传递，生成器需要像下面这样显式引用：

```xml
<ProjectReference Include="..\..\src\OrielWeb\OrielWeb.csproj" />
<ProjectReference Include="..\..\src\OrielWeb.Generators\OrielWeb.Generators.csproj"
                  OutputItemType="Analyzer" ReferenceOutputAssembly="false" PrivateAssets="all" />
```

```csharp
using System.Text.Json.Serialization;
using OrielWeb;
using OrielWeb.Ipc;

internal static class Program
{
    [STAThread]                                     // Windows 上要求 STA
    private static void Main(string[] args)
    {
        Oriel.CreateBuilder(args)
            .UseEmbeddedAssets()                    // https://app.oriel/ ← wwwroot/**
            .UseJsonContext(AppJsonContext.Default) // STJ 源生成上下文（DTO）
            .AddCommands<TodoCommands>()            // [OrielCommand] 命令类
            .UseDebug()                             // 打开 DevTools
            .AddWindow(w => w.WithTitle("Demo")
                             .WithSize(1024, 720)
                             .WithFrameless()       // 无边框
                             .Centered())
            .Run();
    }
}

public sealed record TodoItem(int Id, string Text, bool Done);

// IPC 命令：页面 JS 调 window.oriel.invoke('todo.add', { text: '买牛奶' })
public sealed partial class TodoCommands
{
    private readonly List<TodoItem> _items = [];
    private int _nextId;

    [OrielCommand("todo.add")]
    public TodoItem Add(string text)
    {
        var item = new TodoItem(_nextId++, text, Done: false);
        _items.Add(item);
        return item;
    }
}

// STJ 源生成上下文：DTO 序列化的 AOT 安全入口（命令参数与返回值都经它）
[JsonSerializable(typeof(TodoItem))]
internal partial class AppJsonContext : JsonSerializerContext;
```

```html
<!-- wwwroot/app.js（页面侧） -->
<script>
    await window.oriel.ready;
    const item = await window.oriel.invoke('todo.add', { text: '买牛奶' });
    // → IPC 往返：JSON 参数 → 编译期路由 → C# 方法 → JSON 回执 → Promise resolve
</script>
```

## 无边框窗口

```html
<!-- 标题栏由页面自绘。图标用 10x10 的内联 SVG 而不是字体符号（─ □ ×）——字体符号的笔画粗细与
     对齐会随字体度量漂移，和系统标题栏差别明显。线宽用 stroke-width: calc(1px / var(--dpr))
     折算成 1 物理像素。 -->
<div class="titlebar">
    <div class="drag-region">标题</div>
    <button onclick="oriel.invoke('win.minimize')" aria-label="最小化">
        <svg viewBox="0 0 10 10" width="10" height="10"><path d="M0 5.5 H10" /></svg>
    </button>
    <button onclick="oriel.invoke('win.toggleMaximize')" aria-label="最大化">
        <svg viewBox="0 0 10 10" width="10" height="10"><rect x="0.5" y="0.5" width="9" height="9" /></svg>
    </button>
    <button onclick="oriel.invoke('win.close')" aria-label="关闭">
        <svg viewBox="0 0 10 10" width="10" height="10"><path d="M0.5 0.5 L9.5 9.5 M9.5 0.5 L0.5 9.5" /></svg>
    </button>
</div>

<!-- 最大化后的"还原"字形是两个方块：完整方块在左下，背面方块错位到右上、只画露出的上边与右边，
     以及右下角到前方块右缘的那一小段：
     <path d="M0.5 2.5 H7.5 V9.5 H0.5 Z" />  +  <path d="M2.5 0.5 H9.5 V7.5 H7.5" />
     与最大化字形一起放进按钮，用 CSS 类切换显示（字形几何照系统字体的 E923 量得）。 -->
```

```js
// 拖动：Windows 上要等指针移动超过阈值再发起——立即发起会进入原生模态循环并吞掉第二次点击，
// 双击序列就凑不满、dblclick 不触发。macOS/Linux 用流式拖动（dragStart/dragTo/dragEnd），可立即开始。
dragRegion.addEventListener('mousedown', (e) => { /* 记录起点 */ });

// 双击标题栏：最大化 / 还原
dragRegion.addEventListener('dblclick', () => oriel.invoke('win.toggleMaximize'));

// 同步最大化/还原图标（用户在原生路径下最大化时也会推送）
oriel.on('maximized', (maximized) => { /* 切换图标 */ });
```

窗口**完全无边框**，边缘拖动调整大小由系统原生处理，无需在页面里实现任何热区。
Windows 上这一点由 **Composition 宿主**保证：窗口以 `WS_EX_NOREDIRECTIONBITMAP` 创建，WebView2 通过
`ICoreWebView2CompositionController` 作为 DirectComposition 的一份视觉接入，**不再是子窗口**，
因此窗口能收到 `WM_NCHITTEST` 并显式给出边缘命中值（子窗口会以 `HTCLIENT` 阻断该消息向上的传递）。
代价是组合托管的 WebView 收不到系统输入，鼠标消息由宿主转发（键盘不需要）。
详见 `docs/DECISIONS.md` 中的设计记录。

## 导航与页面通信

**导航**：`GoBack()` / `GoForward()` / `Reload()` 与 `CanGoBack` / `CanGoForward`，语义与各平台原生
webview 一致（无历史时调用是空操作）。

```csharp
window.GoBack();
window.Reload();
bool canGoBack = window.CanGoBack;

window.NavigationStarting += url => { /* 新文档开始加载 */ };
window.NavigationCompleted += e =>         // 成功与失败都触发
{
    if (!e.Success) { Console.WriteLine($"加载失败：{e.Url} → {e.Error}"); }
};
```

页面侧对应两个事件（页内跳转与刷新同样触发）：

```js
oriel.on('navigation.starting', (e) => console.log('开始加载', e.url));
oriel.on('navigation.completed', (e) => { if (!e.success) showError(e.error); });
```

**三条 IPC 通道**：

| 方向 | API | 语义 |
|---|---|---|
| 页面 → 宿主（要结果） | `oriel.invoke(name, args)` / `[OrielCommand]` | 零反射命令路由，有回执与超时 |
| 页面 → 宿主（不要结果） | `oriel.postMessage(name, payload)` / `MessageReceived` | 单向通知，不等回执 |
| 宿主 → 页面 | `EmitEvent(name, payload)` / `oriel.on(name, handler)` | 自定义事件 |
| 页面 console → 宿主 | `WithConsoleForwarding()` / `ConsoleMessage` | 默认关闭，见下 |

```csharp
// 宿主 → 页面：payload 已是 JSON 文本（库只投递，不解析也不改写）
window.EmitEvent("todo.changed", """{"count":3}""");

// 或者直接传对象，用 STJ 类型信息序列化（源生成上下文导出，Native AOT 安全）
window.EmitEvent("sys.info", info, AppJsonContext.Default.SysInfo);

// 页面 → 宿主的单向消息
window.MessageReceived += e => Console.WriteLine($"{e.Name}: {e.Json}");
```

**console 转发默认关闭**（`WithConsoleForwarding()` 打开）：注入的 hook 会包装页面的 console 方法
（改变其可观测行为，例如 `console.log.toString()`），且高频输出会变成持续的 IPC 流量。开发时打开它，
就能在宿主侧直接看到页面日志。

**跨 `await` 之后要回 UI 线程**：`await` 的续体不在 UI 线程上，而 GTK（Linux）与 AppKit（macOS）只允许在
各自的主线程调用窗口 API——从线程池调用会直接崩。用 `PostToUiThread` 回到 UI 线程再碰窗口：

```csharp
await window.EvaluateJs("document.body.dataset.ready = '1'");
window.PostToUiThread(() => window.SetTitle("页面已就绪"));   // 必须回 UI 线程
```

### 这两块能力怎么验证

`samples/OrielDemo` 带两个自检开关，**不需要人眼**——它们自己驱动页面、打印结论，并以进程退出码表达成败
（CI 的三平台冒烟都会跑）：

```bash
OrielDemo --nav-selftest    # 跳转 → 后退 → 前进 → 刷新 → 加载失败（含错误信息）
OrielDemo --ipc-selftest    # console 转发、postMessage、EmitEvent 闭环（推送 → 页面回显 → 收回）
```

## 剪贴板、系统主题与单实例

**剪贴板**（文本与 HTML；写 HTML 时**同时**写一份纯文本回退，只认文本的应用也能粘贴）：

```csharp
string? text = window.ClipboardText;
window.SetClipboardText("你好");

string? html = window.ClipboardHtml;
window.SetClipboardHtml("<b>你好</b>", "你好");   // 第二个参数是纯文本回退
```

平台差异：Windows 用 `CF_UNICODETEXT` + `HTML Format` 自定义格式（CF_HTML，头里是按**字节**计的偏移）；
macOS 用 `NSPasteboard`（`public.utf8-plain-text` / `public.html`）；Linux 用 `gtk_clipboard_*`
（HTML 走 `text/html` 自定义 target，文本走 `UTF8_STRING`）。

**系统主题**：`OrielApp.Theme` + `ThemeChanged`；同一变化也会以 `theme.changed` 推给每个页面
（payload 为 `"light"` / `"dark"`），并且**每次导航成功后补推一次**——新文档不必等用户切换就知道当前主题。

```csharp
OrielTheme theme = app.Theme;
app.ThemeChanged += t => Console.WriteLine($"主题切换为 {t}");

// 页面侧
oriel.on('theme.changed', (theme) => document.documentElement.dataset.theme = theme);
```

检测方式：Windows 读 `HKCU\…\Themes\Personalize\AppsUseLightTheme` 并监听 `WM_SETTINGCHANGE`；
Linux 看 `gtk-application-prefer-dark-theme`、主题名与 `GTK_THEME`，并连 `notify::` 信号；
macOS 看 `NSUserDefaults` 的 `AppleInterfaceStyle` 并订阅系统通知。

**单实例**：第二个实例会通知首实例，随后**立即以退出码 0 退出**（不建窗、不进消息循环）；
首实例默认把窗口前置并激活，也可用回调做别的事（例如把第二实例的命令行参数用起来）。

```csharp
Oriel.CreateBuilder(args)
    .AddWindow(/* … */)
    .SingleInstance("com.example.myapp", window => window?.Focus())
    .Run();
```

判定用**独占文件锁**，通知用命名管道（Unix 上即 Unix domain socket）。
⚠️ 不要用"同名命名管道能否创建成功"来判定——Unix 上 .NET 会先删掉已存在的 socket 再绑定，
第二个实例也会"成功"，于是两个进程都以为自己是首实例（实测踩到过）。

### 这三项的验证账

| 能力 | 机器断言（三平台 CI 都跑） | 尚未验证 |
|---|---|---|
| 剪贴板 | `--clipboard-selftest`：文本与 HTML 各自写→读回、两种类型互不干扰 | **跨进程互操作**（与其它应用互相粘贴）——只验证了同进程写读 |
| 主题 | `--theme-selftest`：宿主读到的值与页面回显一致；Linux 上跑两次（用 `GTK_THEME` 造值）并断言深浅结论**不同** | **切换的实时性**（在系统设置里切换后事件是否立刻到达）需要真实桌面 |
| 单实例 | 双进程：第二个立即成功退出并通知首实例、第一个收到激活请求 | — |

> 与路线图的约定一致：这里只写有证据的结论；未验证项同样列在 `docs/ROADMAP.md` 的待真机清单里。

## 托盘、通知与菜单

托盘与通知都是**应用级**能力（不属于任何窗口）。托盘经 `AddTray` 配置、运行期从 `app.Tray` 取：

```csharp
Oriel.CreateBuilder(args)
    .AddTray(o => { o.IconPath = "assets/tray.png"; o.Tooltip = "Todo"; })
    .AddWindow(w => w.WithTitle("Todo"))
    .Run();
```

```csharp
var tray = app.Tray!;
tray.Clicked += () => window.Show();
tray.MenuItemClicked += id => { if (id == "quit") app.Quit(); };
tray.SetMenu(
[
    OrielMenuItem.Item("show", "显示窗口"),
    OrielMenuItem.Separator(),
    new OrielMenuItem { Id = "mute", Label = "静音", Checked = true },
    OrielMenuItem.RoleItem(OrielMenuRole.Quit),   // 平台标准项（行为由平台给，如退出应用）
]);
```

通知是应用级入口，与是否启用托盘无关（Windows 上它的载体恰好是托盘气球，但那是实现细节）：

```csharp
app.ShowNotification("下载完成", "文件已保存到「下载」");   // 便捷重载
app.ShowNotification(new OrielNotificationOptions
{
    Title = "构建失败", Body = "见控制台", IconPath = "assets/error.png", Id = "build-failed",
});
app.NotificationClicked += id => { /* 点了哪条通知（平台差异见下表） */ };
```

菜单项统一用 `OrielMenuItem`（分隔线、禁用、勾选、子菜单、平台 role 都在其中），
加速键用 `OrielAccelerator` 语法（如 `"CmdOrCtrl+Shift+A"`——macOS 上是 Command、其它平台是 Ctrl）。

### 托盘与通知的验证账

| 能力 | 机器断言 | 尚未验证（需人眼或真机） |
|---|---|---|
| 托盘 | `--shell-selftest`：创建托盘 + 设进一份含分隔线/勾选/禁用/子菜单/role 的菜单，进程不崩；`tools/verify-linux-shell.sh` 采集证据 | **图标是否真的出现在托盘区**、菜单外观、点击行为。Linux 另有平台限制：GNOME Shell 需 AppIndicator 扩展、Wayland 会话多数不显示 |
| 通知投递 | `tools/verify-linux-shell.sh`：真 `notify-send` → 会话总线 → 假通知服务，断言**标题与正文逐字符正确** | macOS 的通知横幅外观与点击上报；Windows 气球的实际展示 |
| 通知点击上报 | Windows：气球点击回传 `Id` | Linux（`notify-send` 拿不到点击，要改 libnotify 的 action 回调）、macOS（`osascript` 无回调）——两处都在代码里**显式空实现**，而不是"忘了触发" |
| 菜单 | `--shell-selftest` 设置一份含子菜单/自定义项/role 的应用菜单并断言不崩（Linux 上是空操作）；加速键解析有 41 个单测 | 菜单的外观、上下文菜单的弹出位置与交互、macOS 上加速键是否真的生效——需人眼 |

> 三平台实现：Windows `Shell_NotifyIconW` + 弹出菜单（`TrackPopupMenuEx`）、macOS `NSStatusBar`/`NSMenu`、
> Linux GTK3 `GtkStatusIcon`/`GtkMenu`。通知：Windows 用独立的隐藏托盘项发气球（因此不启用托盘也能发）、
> macOS 走 `osascript`、Linux 走 `notify-send`。

### 菜单

菜单项结构与加速键语法在**三处共用**（托盘菜单、应用菜单、窗口上下文菜单），role 由同一套解释器落到行为上：

```csharp
window.ShowContextMenu(
[
    OrielMenuItem.Item("copy-path", "复制路径"),
    OrielMenuItem.Separator(),
    new OrielMenuItem { Id = "pin", Label = "置顶", Checked = true },
    OrielMenuItem.RoleItem(OrielMenuRole.Copy),      // 平台标准项
]);
window.ContextMenuItemClicked += id => { /* 自定义项的 Id */ };

app.SetAppMenu(
[
    OrielMenuItem.Item("about", "关于"),
    OrielMenuItem.Separator(),
    OrielMenuItem.RoleItem(OrielMenuRole.Quit),
]);
app.AppMenuItemClicked += id => { /* 自定义项的 Id */ };
```

平台差异集中列在这里，免得逐处猜：

| 项 | macOS | Windows | Linux |
|---|---|---|---|
| 应用菜单 | ✅ 顶部主菜单栏（`setMainMenu:`） | ✅ 每个窗口的菜单栏；**无边框窗口会跳过**（客户区铺满窗口，系统菜单栏会被盖住，与其"设了看不见"不如明确跳过） | ❌ 不支持：现代 GTK 应用用 header bar，且硬塞菜单栏会与 webview 的布局层级打架。`SetAppMenu` 是空操作，**需要菜单就把入口画在页面里** |
| 上下文菜单 | ✅ 鼠标位置弹出（异步） | ✅ 同样在鼠标位置，但**调用会阻塞**到用户选择（原生弹出菜单自带模态消息循环） | ✅ 指针位置弹出（异步） |
| 菜单加速键 | **真快捷键**（系统拦下按键） | 只作显示（按键仍送到页面） | 只作显示（跟在标签后面） |
| 子菜单 / 勾选 / 禁用 / 分隔线 | ✅ | ✅ | ✅ |
| role 项 | 全部（`close`/`minimize`/`zoom`/编辑类等） | 窗口类与编辑类；托盘菜单里只有应用级（`quit`）——托盘没有"当前窗口" | 同 Windows（但无应用菜单） |

> role 的语义由 `OrielMenuRoles` 统一解释（`quit` 退出应用、`copy` 交给页面 `document.execCommand` 等），
> 三平台后端只负责"把菜单画出来"和"把选择报回来"。编辑类 role 是**尽力而为**：
> `copy`/`selectAll`/`undo`/`redo` 通常可用，`cut`/`paste` 在多数 webview 里会被安全策略拦下。

## 构建

```bash
# Windows
dotnet publish samples/OrielDemo -c Release -r win-x64

# macOS（需要 Mac；产物必须打包成 .app 才能运行 WKWebView，见「macOS 运行要求」）
dotnet publish samples/OrielDemo -c Release -r osx-arm64

# Linux（需要 libwebkit2gtk-4.1；中文界面另需 CJK 字体，见「Linux 环境依赖与已知限制」）
dotnet publish samples/OrielDemo -c Release -r linux-x64
```

### Windows 发布产物

WebView2 的运行需要微软的 `WebView2Loader.dll`。官方只有两条路：随 exe 放一份 loader，或把它内嵌为
程序集资源。OrielWeb 取后者——三个架构（x64/arm64/x86）的 loader 已在编译期内嵌进 `OrielWeb.dll`，
首次运行时按进程架构解压到 `%TEMP%` 再加载。因此**引用本库的项目不需要任何发布配置**，
`dotnet publish -c Release -r win-x64` 直接得到单文件：

| 命令 | 产物 |
|---|---|
| `dotnet publish -c Release -r win-x64` | `OrielDemo.exe` 4.81 MB + `OrielDemo.pdb` 21 MB + `OrielWeb.pdb` + `OrielWeb.xml` |
| 再加 `-p:DebugType=none` | `OrielDemo.exe` + `OrielWeb.xml` |
| 再加 `-p:AllowedReferenceRelatedFileExtensions=.pdb\;.pri` | 仅 `OrielDemo.exe`（4.81 MB） |

- 那 21 MB 的 PDB 由原生链接步骤产出，与托管的 `DebugType` 无关；`-p:DebugType=none` **只在全新构建**
  下去掉它（增量发布会复用旧的链接结果）。
- 内嵌 loader 的代价：单架构产物会一并带上另外两个架构的 loader（约 273 KB）。
- 另有一条可选路线：用 Native AOT 的 `DirectPInvoke` 把 `WebView2LoaderStatic.lib`（包内自带）静态链入，
  可免去 `%TEMP%` 解压。实测可链接成功，但属**应用级**配置（库无法替消费方设置），且需跳过
  `WebView2Utilities.Initialize`，故本库未采用。

#### 应用图标

任务栏与 Alt-Tab 的按钮图标取自**窗口图标**：窗口完全没有图标时，Windows 退回的是**通用应用图标**，
而不是 exe 自带的那个（实测确认）。因此本库在建窗口类时会主动把 exe 的图标取来设到窗口上
（`ExtractIconEx`，走 shell 自身的解析，不依赖图标资源 ID）。

应用侧只需在 csproj 里声明图标即可：

```xml
<ApplicationIcon>app.ico</ApplicationIcon>
```

不声明则窗口不带图标，任务栏显示系统通用图标。

## 平台支持

| 平台 | Webview | 状态 |
|------|---------|------|
| Windows x64/arm64 | WebView2 (Evergreen) | ✅ 已运行验证（demo IPC 往返、单测、AOT 发布）；仅一套实现，无遗留开关 |
| Linux x64 | WebKitGTK 4.1 | ✅ 已运行验证（WSL2 + WSLg 真机：窗口创建、页面渲染、IPC 往返均已确认；X11 与 Wayland 双后端各跑通一次） |
| Linux arm64 | WebKitGTK 4.1 | ⚠️ 编译通过；CI 在 xvfb 下冒烟（进程存活）；未在真机运行 |
| macOS arm64 | WKWebView | ✅ 已运行验证（GitHub 托管 macOS runner / macOS 26：窗口创建、页面渲染、中文、IPC 往返均已确认） |
| macOS x64 | WKWebView | ⚠️ 编译通过（CI `compile-macos`）；未在真机运行 |

> 上表只写有证据的结论：Windows / Linux x64 / macOS arm64 均有实际运行验证记录——Windows 与 Linux 为本地
> 真机，macOS 为 GitHub 托管的 macOS runner；三者的 IPC 往返都由页面徽章人眼确认。其余平台目前只有编译与
> 冒烟级别的验证。
>
> Linux x64 与 macOS 的验证过程见 `docs/DECISIONS.md`——其中记录了真机上依次暴露的后端缺陷及修法，
> 这些缺陷在只有编译验证时完全看不出来。

### Linux 环境依赖与已知限制

- **中文字体必须另行安装**：WebKitGTK 经 fontconfig 取字体，发行版未预装 CJK 字体时中文会渲染成方框。
  先装 `fonts-noto-cjk`（Debian/Ubuntu）再运行；页面侧的 `font-family` 也应带上 `"Noto Sans CJK SC"`
  这类跨平台族，而不是只写 `"Segoe UI"` / `"Microsoft YaHei"` 这类 Windows 专有字体名。
  库本身不碰字体（字体选择属应用与系统职责）。
- **Wayland 下无边框窗口不可拖动**：无边框拖动依赖 `gtk_window_move`，而 Wayland 协议不允许客户端自行
  移动窗口，该调用在 Wayland 下是空操作；强制 X11（`GDK_BACKEND=x11`）时拖动正常。改用
  `gtk_window_begin_move_drag` 交合成器接管的修法**尚未实施**。

### macOS 运行要求

- **必须打包成 `.app` bundle 才能运行 WKWebView**：`WKWebView` 在 macOS 上是多进程架构，宿主进程需要
  有效的 bundle 身份（`Info.plist` 的 `CFBundleIdentifier`）才能与 `WebContent` / `Networking` 这些 XPC
  服务通信；裸可执行文件会走到 WebKit 的内部断言（`SIGTRAP`，退出码 133）。`tools/verify-macos.sh`
  会在产物旁构造一个最小 `OrielDemo.app` 并从中启动——这是把 demo 跑起来所需的**打包步骤**，不是库的配置项。
- **后端初始化时会 `dlopen` Foundation/AppKit/WebKit**：本库在 macOS 上是纯 P/Invoke、只链接 `libobjc`，
  不链接任何框架。缺这一步时 `objc_getClass("NSWindow")` 之类返回 nil，而 ObjC 向 nil 发消息是静默
  no-op——表现为"进程不崩、不报错、直接退出，但从来没有窗口"。库已内置（`ObjCRuntime.LoadFrameworks`），
  消费方无需处理。
- **CI 上如何验证**：`smoke-macos` job 运行 `tools/verify-macos.sh`，断言进程存活、出现 `WebContent`
  子进程、以及 `CGWindowList` 能枚举到标题含 `Oriel Demo` 的窗口，并把截图作为 artifact 上传
  （页面渲染、中文与 IPC 徽章只能人眼判定）。托管 runner 具备图形登录会话这一点在 2026-09-29
  实测确认过（会话类型、屏幕数、截图、WKWebView 探针的原始数据见 `docs/DECISIONS.md`）——它属于
  **GitHub 侧的前提假设**，不是本库的保证。

### WebView2 运行时与缺失引导

- 使用**内嵌资源**（`UseEmbeddedAssets`）需要 `ICoreWebView2_3`，即 WebView2 Runtime **≥ 1.0.864.35**。
- 环境变量 `ORIEL_WEBVIEW2_FOLDER` 可指定固定版本运行时目录（调试与离线镜像场景）。
- **运行时不存在**时会先向 loader 询问可用版本，取不到即进入引导流程，不会静默白屏。用
  `OnWebView2RuntimeMissing` 接管提示（回调在窗口已可用之后触发，可用窗口门面 API）：

  ```csharp
  using System.Diagnostics;   // Process.Start

  Oriel.CreateBuilder(args)
      .OnWebView2RuntimeMissing(e =>
      {
          e.Window.ShowMessage(
              $"缺少 Microsoft Edge WebView2 运行时，界面无法显示。安装地址：\n{e.DownloadUrl}",
              "缺少 WebView2 运行时", OrielMessageBoxIcon.Warning);
          Process.Start(new ProcessStartInfo
          {
              FileName = OrielWebView2RuntimeMissingEventArgs.DownloadUrl,
              UseShellExecute = true,
          });
      })
      .AddWindow(...)
      .Run();
  ```

  - 不注册时，库弹一个说明「缺什么、去哪装」的错误框，并区分两种情形：系统未装 Evergreen 运行时、
    或 `ORIEL_WEBVIEW2_FOLDER` 指向的固定版本目录无效。
  - 回调返回后库销毁窗口——没有 WebView2 的窗口没有内容可按，销毁即让应用退出，与"装完再启动"一致；
    要自行保留窗口则置 `e.KeepWindowOpen = true`。
  - **库不自动安装**：提示方式、是否静默安装都属应用策略（企业环境常禁止联网安装）。对比 Tauri 的默认
    行为 `webviewInstallMode: downloadBootstrapper`——它在**安装器**层下载并运行微软 bootstrapper；
    本项目没有安装器（只有 NuGet 包 + 单文件 exe），因此只能在应用内引导。
- **运行时过旧**（低于上表下限）走的是另一条路径：环境能创建，但缺少所需接口的调用会失败并给出通用错误提示。

## 命令线程模型

- **命令实例是共享的**：`AddCommands<T>()` 注册的类型只创建一次（惰性单例），所有 invoke 都作用于同一实例。
  因此**命令方法必须线程安全**——并发 invoke 可能同时进入同一方法。
- 命令执行发生在**后台线程**（不阻塞 UI 消息循环）；回执由分发器切回 UI 线程后投递。
- 命令内需要操作 UI 时，请经 `OrielApp.PostToMainThread(...)` 切回主线程。
- 应用自己的异步流程同理：`await` 之后不在 UI 线程，碰窗口前要经 `WebviewWindow.PostToUiThread(...)`
  （原因见「导航与页面通信」）。
- 反例：`samples/OrielDemo` 的 `TodoCommands` 直接读写 `List<T>` 与 `_nextId++`，并发下并不安全；
  示例为保持简洁如此编写，实际项目请自行加锁或改用线程安全结构。

## 路线图

后续要补的能力（**三平台一致性缺口 → 内容/IPC 深度 → 平台集成外壳**）、每一项的验证方式，
以及无头环境验证不了的那部分"待真机验证清单"，见 [docs/ROADMAP.md](docs/ROADMAP.md)。
已经落地的取舍与实测结论见 [docs/DECISIONS.md](docs/DECISIONS.md)。

## 许可

[MIT](LICENSE) © OrielWeb Contributors。

本库在编译期内嵌了微软分发的 `WebView2Loader.dll`（取自 `Microsoft.Web.WebView2` 包），
其版权与许可声明见 [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt)。
