# OrielWeb

类 Tauri 的 C# 跨平台系统 webview 核心库。无 C++ 中间层、无 GUI 框架依赖、Native AOT 友好、零反射 IPC。

## 目录

- [特性](#特性) · [平台支持](#平台支持) · [快速开始](#快速开始)
- [无边框窗口](#无边框窗口) · [导航与页面通信](#导航与页面通信)
- [剪贴板、系统主题与单实例](#剪贴板系统主题与单实例) · [平台集成](#平台集成)
- [对话框](#对话框) · [文件拖放](#文件拖放) · [内建右键菜单](#内建右键菜单)
- [手动验证与无人自检](#手动验证与无人自检)
- [构建与发布](#构建与发布) · [平台运行要求](#平台运行要求)
- [路线图](#路线图) · [许可](#许可)

> 各节末尾的 **验证账** 是该能力的证据与缺口：哪些有单测、哪些只有编译验证、哪些必须人眼。
> 无头环境验证不了的部分另见 [docs/ROADMAP.md](docs/ROADMAP.md) 的待真机清单。

## 特性

- **系统 webview**：Windows 用 WebView2、macOS 用 WKWebView、Linux 用 WebKitGTK——不捆绑浏览器内核
- **纯 C# 互操作**：macOS（WKWebView + ObjC runtime）与 Linux（GTK3 + WebKitGTK）为手写 P/Invoke；Windows 的 WebView2 COM 走 `WebView2Aot` 的 `[GeneratedComInterface]`/`[GeneratedComClass]` **源生成绑定**（无手写 vtable/IID/RefCount）。三平台都不需要 C++ 中间层
- **Composition 宿主**（Windows）：WebView2 作为 DirectComposition 的一份视觉合成进窗口，而非子窗口——无边框窗口的边缘 resize 因此能走系统原生路径
- **零反射 IPC**：`[OrielCommand]` + Roslyn 源生成器在编译期生成分发代码，运行期零反射
- **Native AOT**：全局 `IsAotCompatible`/`IsTrimmable`，发布为原生单文件（WebView2 的运行时加载器已内嵌，无旁文件）
- **无边框窗口**：自绘标题栏 + 流式/原生拖动 + 最大化/全屏/置顶切换
- **导航与双向通信**：前进/后退/刷新、导航事件（带错误信息）、页面 console 转发、`EmitEvent` 推事件、`postMessage` 收消息
- **文件对话框**：打开（可多选）/ 保存 / 选文件夹，支持结构化过滤器与初始目录
- **文件拖放**：外部文件拖进窗口 → 本地路径列表（`window.FileDropped`）
- **内建右键菜单策略**：默认只留剪切/复制/粘贴，可切平台原样或完全禁用
- **平台集成**：剪贴板（文本 + HTML）、系统主题、单实例、系统托盘、系统通知、菜单（窗口上下文菜单 + 托盘菜单，含平台 role 与加速键）、Shell 集成、开机自启

> 每项能力**验证到什么程度**（哪些有单测、哪些只有编译、哪些必须人眼）在本文件中就地标注，
> 未验证项的清单汇总在 [docs/ROADMAP.md](docs/ROADMAP.md)。demo 带两类验证入口：
> 给人看的操作台（直接运行）与给 CI 的无人自检（`--selftest <名字>`），见[手动验证与无人自检](#手动验证与无人自检)。

## 平台支持

| 平台 | Webview | 状态 |
|---|---|---|
| Windows x64/arm64 | WebView2 (Evergreen) | ✅ **已运行验证**（demo IPC 往返、单测、AOT 发布） |
| Linux x64 | WebKitGTK 4.1 | ✅ **已运行验证**（WSL2 + WSLg 真机：窗口、渲染、IPC 往返；X11 与 Wayland 双后端各跑通一次） |
| macOS arm64 | WKWebView | ✅ **已运行验证**（GitHub 托管 macOS runner：窗口、渲染、中文、IPC 往返） |
| Linux arm64 | WebKitGTK 4.1 | ⚠️ 编译通过；CI 在 xvfb 下冒烟（进程存活）；未在真机运行 |
| macOS x64 | WKWebView | ⚠️ 编译通过（CI `compile-macos`）；未在真机运行 |

> 只写有证据的结论：Windows 与 Linux x64 是本地真机，macOS arm64 是 GitHub 托管的 runner；
> 三者的 IPC 往返都由页面徽章人眼确认。其余平台目前只有编译与冒烟级别的验证。
> Linux x64 与 macOS 的验证过程（真机上依次暴露的后端缺陷及修法）见 `docs/DECISIONS.md`——
> 那些缺陷在只有编译验证时完全看不出来。
>
> **各平台的环境依赖与已知限制**不在这里展开，见后面的[平台运行要求](#平台运行要求)。

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
  <!-- 就这些。wwwroot 下的前端资源由包内的 buildTransitive/OrielWeb.targets 自动内嵌。 -->
</Project>
```

**不需要写 `<EmbeddedResource>`** —— 包里带了一份 MSBuild targets（`buildTransitive/OrielWeb.targets`），
它会把 `wwwroot\**\*` 按带 `/` 分隔符的 `LogicalName` 嵌进去，`buildTransitive/` 意味着**间接
引用者也能吃到**（应用 → 带 `wwwroot` 的类库 → 那个类库的资源同样正确）。

这不是便利性问题，而是消灭一个静默缺陷：手写那一行时若漏了 `LogicalName`，MSBuild 会把
目录分隔符压成 `.`，库只能靠"最后一个 `.` 是扩展名"反推目录——文件名主干或目录名含 `.` 时
**必然推错**：

| 写法 | 资源名 | 解压结果 |
|---|---|---|
| 包内 targets 用的 `LogicalName="…wwwroot/%(RecursiveDir)%(Filename)%(Extension)"` | `App.wwwroot/assets/img/logo.svg`、`App.wwwroot/app.min.js` | 按 `/` 还原目录，`.` 一律是文件名的一部分 ✅ |
| 只写 `Include="wwwroot\**\*"` | `App.wwwroot.assets.img.logo.svg`、`App.wwwroot.app.min.js` | MSBuild 把目录压成了 `.`，库只能反推 —— 含点文件名时**必然推错** ❌ |

推错的形态是**静默的**：解压不报错，但文件落到了 `app/min.js`，页面按原 URL 请求就是 404 白屏。

两条行为约定：

- **自己声明过就跳过**（幂等）：项目里已经有指向 `wwwroot` 的 `<EmbeddedResource>` 时，
  targets 不做任何事，旧写法的行为完全不变（`-v:n` 构建日志里能看到"跳过自动内嵌"）。
- **可整体关掉**：`<OrielWebEmbeddedAssets>false</OrielWebEmbeddedAssets>`。
  关掉之后你要自己写那一行，并且**必须写 `LogicalName`**。

> 映射逻辑（资源名 → 磁盘路径）由 `EmbeddedAssetExtractor` 靠前缀判定（`wwwroot/` 还是 `wwwroot.`），
> 有单测覆盖（`tests/OrielWeb.Tests/EmbeddedAssetTests.cs`）；整条"打包 → 消费 → 断言"链路由
> `tools/verify-pack.ps1` 验证（含 `app.min.js` 这类含点文件名、嵌套目录、旧写法不重复嵌入、
> 显式关掉四个场景），最小消费样例见 `samples/OrielMinimal/`。

在仓库内开发时（而不是引用 NuGet 包），把包引用换成项目引用。注意分析器不会随
`ProjectReference` 传递，生成器需要像下面这样显式引用——而且 `build/`、`buildTransitive/`
这类资产只随**包**分发，`ProjectReference` 拿不到，所以仓库内的项目仍然自己写那一行：

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
// 拖动：**三个平台都不要在 mousedown 里立刻发起原生拖动**——立即发起会吞掉第二次点击，
// 双击序列就凑不满、dblclick 不触发。
//   * Windows：由页面自己等指针移动超过阈值（原生模态循环会吞掉后续点击）；
//   * Linux（Wayland）：库在 dragStart 里**先不交给合成器**，等 dragTo 的增量超过阈值才交
//     （一旦交给合成器，指针就被 grab，页面再也收不到事件）——所以页面照常写即可；
//   * Linux（X11）/ macOS：流式拖动不抢指针，但页面仍应在阈值之后才发 dragStart。
dragRegion.addEventListener('mousedown', (e) => { /* 记录起点 */ });

// 双击标题栏：最大化 / 还原
dragRegion.addEventListener('dblclick', () => oriel.invoke('win.toggleMaximize'));

// 同步最大化/还原图标（用户在原生路径下最大化时也会推送）
oriel.on('maximized', (maximized) => { /* 切换图标 */ });
```

窗口**完全无边框**。边缘拖动调整大小由**库自己接管**，页面里不需要任何热区：

| 平台 | 边缘 resize | 说明 |
|---|---|---|
| Windows | ✅ 已运行验证 | 由 **Composition 宿主**保证：窗口以 `WS_EX_NOREDIRECTIONBITMAP` 创建，WebView2 通过 `ICoreWebView2CompositionController` 作为 DirectComposition 的一份视觉接入，**不再是子窗口**，因此窗口能收到 `WM_NCHITTEST` 并显式给出边缘命中值（子窗口会以 `HTCLIENT` 阻断该消息向上的传递）。代价是组合托管的 WebView 收不到系统输入，鼠标消息由宿主转发（键盘不需要） |
| Linux | ⚠️ **已实现，未真机验证** | 去掉窗口装饰后 WM 不再提供 resize 边框（Ubuntu 22.04 实测确认），所以由库自己在 webview 的 `motion-notify-event` / `button-press-event` 里判**边缘热区**（5 逻辑像素），命中就把 resize 交给 WM/合成器（`gtk_window_begin_resize_drag`：X11 走 WM，Wayland 转成 `xdg_toplevel.resize`）。命中判定是纯函数、有 18 个单测；**GTK 侧尚未在真机验证** |
| macOS | ⚠️ 未实现 | 无边框后同样不再有系统 resize 边框；尚未处理 |

> 热区那几像素里页面收不到 `mousemove`/`click`——这与 Windows 上 `WM_NCHITTEST` 把边缘像素交给 WM
> 是同一件事，不是 Linux 特有的损失。窗口被设为不可调整大小（`WithResizable(false)`）时不接管。
> 需要更宽的抓手或自定义外观时，仍可用 `Resize(w, h)` 由应用自己给出手柄。
> 详见 `docs/DECISIONS.md` 与 ROADMAP 的「已确认的缺陷」。

### 应用图标

**三个平台拿到图标的途径完全不同**——这是最容易被"在 Windows 上试通了"骗过去的一处：

| 平台 | 图标从哪来 | 应用要做什么 |
|---|---|---|
| Windows | 库**主动从 exe 取**（`ExtractIconEx`，走 shell 自身的解析，不依赖图标资源 ID）设到窗口类上。因为窗口完全没有图标时，Windows 退回的是**通用应用图标**而不是 exe 自带的那个（实测确认） | 只在 csproj 声明 `<ApplicationIcon>` |
| Linux | **只能由应用给文件**：`gtk_window_set_icon_from_file` → X11 下写入 `_NET_WM_ICON`。ELF 里没有图标可取。**⚠️ 但 `_NET_WM_ICON` 只管到一部分桌面环境**——GNOME（Ubuntu 默认）不读它，见下 | `WithIcon(绝对路径)` + 随产物分发文件；**GNOME 上还需要一个 `.desktop`** |
| macOS | **只能由应用给文件**：`NSApplication.applicationIconImage`（Dock 图标）。裸可执行文件没有 bundle 身份，也就没有图标 | 同上 |

```xml
<ApplicationIcon>app.ico</ApplicationIcon>                      <!-- Windows：exe 图标 -->
<None Include="app.png" CopyToOutputDirectory="PreserveNewest" /><!-- Linux/macOS：随产物分发的图标文件 -->
```

```csharp
.AddWindow(w => w.WithTitle("我的应用")
                 .WithIcon(Path.Combine(AppContext.BaseDirectory, "app.png")))
```

几条**踩过才知道**的：

- **相对路径会失效**：三个平台都按当前工作目录解析相对路径，而 macOS 从 `.app` 启动时 cwd 是 `/`。
  一律用 `Path.Combine(AppContext.BaseDirectory, …)` 拼绝对路径。
- **格式**：Linux/macOS 走 **PNG 最稳**（GTK 经 gdk-pixbuf、Cocoa 经 `NSImage`）；
  Windows 的 `LoadImageW` 只认 ICO/BMP——而 Windows 侧通常根本不需要设它。
- **赋值时会校验文件存在**（不存在抛 `FileNotFoundException`）。因为三个平台在加载失败时都是**静默**的
  （GTK 丢掉 GError、Cocoa 拿到 nil 就跳过、`LoadImageW` 返回 0），不在这里拦住，
  表现就只剩"图标没生效，且没有任何提示"。
- 不设图标时：Windows 用 exe 图标，Linux/macOS 显示**系统通用图标**（`samples/OrielDemo` 就是按上面这套
  做的：`app.ico` 给 Windows，同源的 `app.png` 给另外两个平台，两者都由 `tools/make-icons.ps1` 生成）。

**⚠️ GNOME 上光有 `WithIcon` 不够（2026-10-01 用户实测）**

`_NET_WM_ICON` 是 X11 的"窗口图标"约定，但 **GNOME Shell 的任务栏/应用切换器并不读它**：GNOME 按窗口的
`WM_CLASS` 去找**匹配的 `.desktop` 文件**、取其中的 `Icon=`；找不到就退回通用图标。
这正好解释了"`xprop` 里 `_NET_WM_ICON` 明明写进去了，任务栏却还是通用图标"——
本仓库早先的 Linux 验证只断言了**那个属性存在**，没有断言任务栏真的显示它。

GNOME 上要让任务栏出图标，得给应用装一份 `.desktop`：

```ini
# ~/.local/share/applications/orieldemo.desktop
[Desktop Entry]
Type=Application
Name=Oriel Demo
Exec=/绝对路径/OrielDemo
Icon=/绝对路径/app.png
StartupWMClass=OrielDemo        # 必须与窗口的 WM_CLASS 的 res_class 对上
Categories=Utility;
```

```bash
update-desktop-database ~/.local/share/applications
```

`StartupWMClass` 是关键——写错就等于没有匹配，图标照旧是通用图标。用 `xprop WM_CLASS` 取窗口实际的值
（本仓库实测是 `("OrielDemo" "OrielDemo")`，取第二个，即 res_class）。

> 这条来自用户在 Ubuntu 22.04 上的实测报告；`.desktop` 这一侧的修法**尚未在本仓库逐项验证**。
> 对要分发的应用来说这份文件应当由安装器写入——那属于 ROADMAP 阶段 D「工具链与打包」。

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

### 命令线程模型

- **命令实例是共享的**：`AddCommands<T>()` 注册的类型只创建一次（惰性单例），所有 invoke 都作用于同一实例。
  因此**命令方法必须线程安全**——并发 invoke 可能同时进入同一方法。
  > 严格说"只创建一次"有一个例外：首次并发 invoke 时，惰性创建走的是
  > `ConcurrentDictionary.GetOrAdd`，竞态下工厂**可能被调用多次**（多次创建、留最后一个）。
  > 所以工厂应当幂等，不要在里面做"只能做一次"的事（占独占资源、启动线程等）。
- 命令执行发生在**后台线程**（不阻塞 UI 消息循环）；回执由分发器切回 UI 线程后投递。
- 命令内需要操作 UI 时，请经 `OrielApp.PostToMainThread(...)` 切回主线程。
- 应用自己的异步流程同理：`await` 之后不在 UI 线程，碰窗口前要经 `WebviewWindow.PostToUiThread(...)`（见上文）。
- 反例：`samples/OrielDemo` 的 `TodoCommands` 直接读写 `List<T>` 与 `_nextId++`，并发下并不安全；
  示例为保持简洁如此编写，实际项目请自行加锁或改用线程安全结构。

### 验证账

`samples/OrielDemo` 带两个自检开关，**不需要人眼**——它们自己驱动页面、打印结论，并以进程退出码表达成败
（CI 的三平台冒烟都会跑）：

```bash
OrielDemo --selftest nav    # 跳转 → 后退 → 前进 → 刷新 → 加载失败（含错误信息）
OrielDemo --selftest ipc    # console 转发、postMessage、EmitEvent 闭环（推送 → 页面回显 → 收回）
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

### 验证账

| 能力 | 机器断言（三平台 CI 都跑） | 尚未验证 |
|---|---|---|
| 剪贴板 | `--selftest clipboard`：文本与 HTML 各自写→读回、两种类型互不干扰 | **跨进程互操作**（与其它应用互相粘贴）——只验证了同进程写读 |
| 主题 | `--selftest theme`：宿主读到的值与页面回显一致；Linux 上跑两次（用 `GTK_THEME` 造值）并断言深浅结论**不同** | **切换的实时性**（在系统设置里切换后事件是否立刻到达）需要真实桌面 |
| 单实例 | 双进程：第二个立即成功退出并通知首实例、第一个收到激活请求 | — |

> 与路线图的约定一致：这里只写有证据的结论；未验证项同样列在 `docs/ROADMAP.md` 的待真机清单里。

## 平台集成

本节是**应用级**能力（不属于某个窗口）：托盘、通知、菜单、Shell 集成、开机自启。
它们各自的证据与缺口统一放在节末的「验证账」里。

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

通知是应用级入口，与是否启用托盘无关（Windows 上走 WinRT toast，**不经托盘**）：

```csharp
app.ShowNotification("下载完成", "文件已保存到「下载」");   // 便捷重载
app.ShowNotification(new OrielNotificationOptions
{
    Title = "构建失败", Body = "见控制台", IconPath = "assets/error.png", Id = "build-failed",
});
```

> **没有"通知被点击"的回调**——三个平台都拿不到（见下面的验证账）。这个能力**不提供**，
> 而不是提供一个永不触发的事件：订阅一个空事件不会编译报错，调用方只会在运行时才发现收不到。

通知的**应用标识**默认取入口程序集名（Windows 上即 AUMID，Linux 上是 `notify-send --app-name`），
因此同机安装的多个基于本库的应用在系统「通知」设置里是分开的、可以逐个静音。需要与打包时注册的
AUMID 对齐时用 `UseNotificationAppId` 覆盖：

```csharp
Oriel.CreateBuilder(args)
    .UseNotificationAppId("com.example.myapp")   // 不设则用程序集名（规范化后：≤128 字符、不含空格）
    .AddWindow(w => w.WithTitle("Todo"))
    .Run();
```

> macOS 没有对应入口：未打包运行时 `osascript` 投递的通知归属于 Script Editor，应用名由系统决定
> （要按应用分组得进 `.app` bundle 后用 `UNUserNotificationCenter`，已记入 ROADMAP）。
```

菜单项统一用 `OrielMenuItem`（分隔线、禁用、勾选、子菜单、平台 role 都在其中），
加速键用 `OrielAccelerator` 语法（如 `"CmdOrCtrl+Shift+A"`——macOS 上是 Command、其它平台是 Ctrl）。

托盘可以临时移除再重建（例如"始终显示托盘图标"这类开关）：

```csharp
app.RemoveTray();                       // 撤销原生图标（Windows 走 NIM_DELETE）
OrielTray? tray = app.RestoreTray();    // 按 AddTray 的配置重建；未配置过则返回 null
tray?.SetMenu(/* … */);                 // 重建出来的是**新对象**：事件与菜单都要重挂
```

> 这一对 API 值在"原生资源真的被撤销"：Windows 上最难查的不是"图标没出现"，
> 而是进程退出后图标还留在通知区（幽灵图标）——那正是没调 `NIM_DELETE` 的症状。

> 三平台实现：Windows `Shell_NotifyIconW` + 弹出菜单（`TrackPopupMenuEx`）、macOS `NSStatusBar`/`NSMenu`、
> Linux GTK3 `GtkStatusIcon`/`GtkMenu`。通知：Windows 经一个短命的 **Windows PowerShell 5.1** 进程调
> WinRT `ToastNotificationManager`（Native AOT 下没有 WinRT 投影；这条路不依赖托盘是否存在）、
> macOS 走 `osascript`、Linux 走 `notify-send`。

### 菜单

菜单项结构与加速键语法在**两处共用**（托盘菜单、窗口上下文菜单），role 由同一套解释器落到行为上：

```csharp
window.ShowContextMenu(
[
    OrielMenuItem.Item("copy-path", "复制路径"),
    OrielMenuItem.Separator(),
    new OrielMenuItem { Id = "pin", Label = "置顶", Checked = true },
    OrielMenuItem.RoleItem(OrielMenuRole.Copy),      // 平台标准项
]);
window.ContextMenuItemClicked += id => { /* 自定义项的 Id */ };
```

平台差异集中列在这里，免得逐处猜：

| 项 | macOS | Windows | Linux |
|---|---|---|---|
| 上下文菜单 | ✅ 鼠标位置弹出（异步） | ✅ 同样在鼠标位置，但**调用会阻塞**到用户选择（原生弹出菜单自带模态消息循环） | ✅ 指针位置弹出（异步） |
| 菜单加速键 | 菜单打开时由 AppKit 匹配（`keyEquivalent`） | 只作显示（按键仍送到页面） | 只作显示（跟在标签后面） |
| 子菜单 / 勾选 / 禁用 / 分隔线 | ✅ | ✅ | ✅ |
| role 项 | 全部（`close`/`minimize`/`zoom`/编辑类等） | 窗口类与编辑类；托盘菜单里只有应用级（`quit`）——托盘没有"当前窗口" | 同 Windows |

> role 的语义由 `OrielMenuRoles` 统一解释（`quit` 退出应用、`copy` 交给页面 `document.execCommand` 等），
> 三平台后端只负责"把菜单画出来"和"把选择报回来"。编辑类 role 是**尽力而为**：
> `copy`/`selectAll`/`undo`/`redo` 通常可用，`cut`/`paste` 在多数 webview 里会被安全策略拦下。

### Shell 集成（打开外链、在文件管理器里显示）

```csharp
app.OpenExternal("https://example.com");        // 白名单内的 scheme 才放行
app.RevealInFileManager("/path/to/file.txt");   // Windows 选中它、macOS 用 Finder 显示、Linux 打开所在目录
app.OpenWithDefaultApp("/path/to/file.pdf");
```

**白名单是默认拒绝式的**：默认只放行 `http`/`https`/`mailto`，`file:`、`javascript:`、裸路径
与含控制字符的目标一律拒绝并返回 `false`（`UseShell` 可追加自定义协议）。
这类 API 的风险不在"自己执行了什么"，而在**"它决定让别的程序去打开什么"**——
未经校验的 `file:` 会被系统默认处理器以它自己的权限打开。

| 动作 | Windows | macOS | Linux |
|---|---|---|---|
| 打开链接 / 文件 | `UseShellExecute`（系统默认处理） | `open <target>` | `xdg-open <target>` |
| 在文件管理器里显示 | `explorer /select,<path>`（逗号后**不能有空格**） | `open -R <path>` | `xdg-open <父目录>`——`xdg-open` 没有"选中"这个入口 |

> 参数一律经 `ArgumentList` 逐项传递、**不经 shell**：目标里的空格、引号、分号都不会变成第二个命令。
> 路径不存在或不是绝对路径时直接返回 `false`，并且不做任何调用。
> 库**不提供"执行任意命令"**（Ryn 的 `shell.execute`/PTY 那一层）：那属于能力沙箱的范畴，
> 与"跨平台 webview 核心库"的定位无关。

### 开机自启

```csharp
app.EnableAutoStart(["--minimized"]);   // 典型用法：开机静默启动到托盘
app.IsAutoStartEnabled;                 // 读平台里的实际配置
app.DisableAutoStart();
```

| 平台 | 写到哪里 |
|---|---|
| Windows | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 下的一个值（HKCU 不需要管理员权限，而且"开机自启"本就是当前用户的偏好） |
| macOS | `~/Library/LaunchAgents/<id>.plist`（**不调用 `launchctl load`**：那会立刻把应用再拉起一遍，可当前进程还在跑；代价是下次登录才生效） |
| Linux | `$XDG_CONFIG_HOME/autostart/<id>.desktop`（freedesktop 的约定，各主流桌面都遵守） |

> 标识默认取可执行文件名，可用 `UseAutoStartId` 覆盖。
> `IsAutoStartEnabled` **读的是平台里实际存在的配置**而不是内存标记——用户可能在"任务管理器 → 启动"
> 或系统设置里关掉它，也可能手工删了文件，那时应当如实返回 `false`。
> 三段配置文本由共用的纯函数生成（`AutoStartContent`），因此格式正确性能被单测覆盖——
> 自启项写错不会当场失败，而是等用户下次开机才发现应用没起来。

### 验证账

本节六项能力的证据与缺口（**放在节末**：其中几项覆盖了本节多个子节，提前出现会让人以为只管前两项）：

| 能力 | 机器断言 | 尚未验证（需人眼或真机） |
|---|---|---|
| 托盘 | `--selftest shell`：创建托盘 + 设进一份含分隔线/勾选/禁用/子菜单/role 的菜单，进程不崩；`tools/verify-linux-shell.sh` 采集证据 | **图标是否真的出现在托盘区**、菜单外观、点击行为。Linux 另有平台限制：GNOME Shell 需 AppIndicator 扩展、Wayland 会话多数不显示 |
| 通知投递 | `tools/verify-linux-shell.sh`：真 `notify-send` → 会话总线 → 假通知服务，断言**标题与正文逐字符正确**；另有 8 个单测覆盖**应用标识的规范化**（128 字符上限、空格与非法字符、非 ASCII 保留、兜底值） | macOS 的通知横幅外观；Windows 上 PowerShell/WinRT toast 的实际展示 |
| 通知点击上报 | **不提供该能力**：三平台都拿不到——Windows 需要打包器注册 COM 激活器，Linux 的 `notify-send` 与 macOS 的 `osascript` 根本没有回调入口。2026-10-01 起从 API 中**整体移除**（此前是三个"显式空实现"，但订阅一个永不触发的事件不会编译报错） | — |
| 菜单构建 | 托盘菜单与窗口上下文菜单共用一套构建与 role 解释；加速键解析有 41 个单测 | 菜单的外观、上下文菜单的弹出位置与交互——需人眼 |
| 开机自启 | `--selftest shell`：**启用 → 查得到 → 禁用 → 查不到**的闭环（三平台都成立）；Linux 上还逐项核对写出的 `.desktop` 内容；三段配置文本另有 22 个单测（含参数引号的边界：结尾反斜杠、内嵌引号、freedesktop 保留字符） | 下次**开机/登录时是否真的自动启动**——需要真机重启 |
| Shell 集成 | 取证脚本用 `xdg-open` 替身断言两件事：URL **真的**交给了系统默认程序，且 `file:`/裸路径/`javascript:` **一次都没调出去**（白名单有效性）；另有 24 个单测覆盖校验与三平台命令翻译 | 真实桌面上弹出的浏览器/文件管理器是否符合预期——需人眼 |

## 对话框

三个入口都挂在窗口上（对话框需要一个宿主窗口）：

```csharp
// 消息框（模态）
window.ShowMessage("保存成功", "提示", OrielMessageBoxIcon.Info);

// 打开文件：可多选；取消返回**空数组**
string[] files = window.ShowOpenFileDialog(new OrielOpenFileDialogOptions
{
    Title = "导入",
    Filters = [OrielFileFilter.Of("文本", "*.txt", "*.md"), OrielFileFilter.AllFiles],
    AllowMultiple = true,
});

// 保存文件；取消返回 null
string? target = window.ShowSaveFileDialog(new OrielSaveFileDialogOptions
{
    Title = "导出",
    Filters = [OrielFileFilter.Of("Markdown", "*.md")],
    DefaultExtension = "md",
    DefaultFileName = "报告",
});

// 选择文件夹；取消返回 null
string? folder = window.ShowFolderDialog("选择输出目录");
```

旧的字符串写法仍然可用（内部经 `OrielFileFilter.Parse` 转成结构化过滤器，行为不变）：

```csharp
string? single = window.ShowOpenFileDialog("打开", "文本文件|*.txt;*.md|所有文件|*.*");
```

### 对话框的三平台差异

| 项 | Windows | macOS | Linux |
|---|---|---|---|
| 多选 | `OFN_ALLOWMULTISELECT` + `OFN_EXPLORER`，返回值是「目录 + 多个文件名」的多段缓冲区，需自己拼回完整路径 | `allowsMultipleSelection`，直接读 `URLs` 数组 | `gtk_file_chooser_set_select_multiple`，读 `GSList` |
| 文件夹选择 | `SHBrowseForFolderW`（**忽略初始目录**：要设初值得挂 `BFFM_INITIALIZED` 回调） | `NSOpenPanel` + `setCanChooseDirectories:` | 同一个 chooser 切到 `SELECT_FOLDER` |
| 过滤器 | `名称\0模式;模式\0…\0`（**双 null 结尾**） | 扩展名数组（`*.tar.gz` → `tar.gz`）；`*.*` 等于「不设」 | 逐条 `gtk_file_filter_add_pattern`；`*.*` 要归一成 `*`（fnmatch 要求文件名含点） |
| 补扩展名 | 原生对话框自己补（`lpstrDefExt`） | 系统行为 | **不补**，由 `OrielFileDialogSupport.EnsureExtension` 补 |

> 过滤器与返回值的转换全是纯函数（`OrielFileFilter` 的三个平台渲染 + `OrielFileDialogSupport` 的
> Win32 多选解析），所以它们有单测、在 Linux 的 CI 上就能跑——**包括为 Windows 写的那两个**。
> 对话框本身弹出后没法在无头环境断言（README 的验证账里如实标着），仍需人眼。

## 文件拖放

```csharp
window.FileDropped += e =>
{
    foreach (string path in e.Paths)
    {
        Console.WriteLine($"拖入：{path}");   // 本地路径，不是 file:// URI
    }
};
```

路径只能由原生侧给：页面自己的 `drop` 事件**拿不到文件路径**（浏览器的安全模型如此）。
需要拖放视觉反馈（高亮、预览）时，页面仍应订阅 `dragover`/`drop` 画效果，真正的路径从这里来。

### 三平台的接入方式

| 平台 | 机制 | 关键点 |
|---|---|---|
| Windows | 窗口加 `WS_EX_ACCEPTFILES` → 收 `WM_DROPFILES` → `DragQueryFileW` 逐项取 | 本库用 **Composition 宿主**（`WS_EX_NOREDIRECTIONBITMAP`），WebView2 **不是子窗口**，所以拖放会落到本窗口——不必手写 OLE `IDropTarget`、也不必做 OLE 初始化。同时把 WebView2 的 `AllowExternalDrop` 关掉，避免它把拖放截走 |
| macOS | 自定义 `NSView` 子类（`OrielDropView`）承载 `NSDraggingDestination`，webview 是它的子视图 | 不给 `WKWebView` 加/替换方法：那会动到 WebKit 自带的拖放实现。AppKit 查找拖放目标时会**沿父视图链向上**，容器因此能收到事件 |
| Linux | `gtk_drag_dest_set(webview, …, "text/uri-list", …)` + `drag-data-received` 信号 | 落点设在 webview 上（它铺满客户区）。载荷经 `gtk_selection_data_get_uris` 取到，再逐项转成本地路径 |

### 验证账

| 项 | 机器断言 | 尚未验证 |
|---|---|---|
| URI → 本地路径 | **23 个用例**：百分号编码（含非 ASCII）、`+` 不被当空格、Windows 盘符、`localhost` 与远程主机、非 file 协议、批量保持顺序 | — |
| 落点注册 | `--selftest shell`：窗口创建会走完各平台的注册路径、事件订阅可用、进程不崩（输出 `FILE-DROP-SUBSCRIBED`） | **真实拖拽**：需要真人从文件管理器拖文件进窗口。无头环境造不出 XDND/OLE 会话，因此这一项**整体不声称已验证** |

> 落点坐标**没有**暴露：三平台的坐标系与 y 轴方向各不相同（Cocoa 原点在左下、GTK 的 y 轴向下、
> Win32 还要算进 DPI 缩放），要一致就得再引入一层换算，而导入文件根本不需要它——
> 需要落点做反馈时用页面自己的 `dragover` 即可。这条记在 `docs/ROADMAP.md`。

## 内建右键菜单

页面里右键弹出的那个菜单由渲染引擎提供，**默认只保留剪切 / 复制 / 粘贴**：

```csharp
// 默认就是 Editing：只留剪切/复制/粘贴
var window = app.CreateWindow(new OrielWindowOptions().WithTitle("我的应用"));

// 需要「检查元素」等原生项时（开发期常用）
window.ContextMenuPolicy = OrielContextMenuPolicy.Native;

// 或者完全不弹
window.ContextMenuPolicy = OrielContextMenuPolicy.Disabled;

// 也可以在建窗时指定
app.CreateWindow(new OrielWindowOptions().WithContextMenuPolicy(OrielContextMenuPolicy.Native));
```

策略**运行时随时可改**，下次右键就生效（不缓存）。

被去掉的项里有几个是**会造成实际损失**的，不只是"没用"：「刷新」在单页应用里等于丢掉整页状态
（用户填了一半的表单），「另存为」存下来的是一份引用了外部脚本/样式的 HTML 外壳，
「后退」会跑出应用自己的路由。

留下的三项则是**只有原生侧能给**的——粘贴要把系统剪贴板内容送进页面编辑区，而页面自己做不到
（`document.execCommand('paste')` 在现代浏览器里被禁用）。

> 这与 `ShowContextMenu` 是**两条独立通道**：那个是宿主自己构造并弹出的菜单（项由
> `OrielMenuItem` 描述、点击回传 `ContextMenuItemClicked`），与渲染引擎的内建菜单互不干涉。

### 实现方式：接管宿主菜单，编辑命令走引擎

`Editing` 策略在 macOS 与 Linux 上是**接管式**——不复用引擎构造好的菜单，而是自己弹一个只含
剪切/复制/粘贴的菜单；三项走引擎的原生编辑命令，所以仍然作用于页面选区与系统剪贴板。

| 平台 | 做法 | 编辑命令 |
|---|---|---|
| Windows | **过滤式**：订阅 `CoreWebView2.ContextMenuRequested`，按 `Name`（未本地化，如 `"copy"`）保留三项、其余删掉（集合按下标删，因此**从后往前**遍历）；菜单仍由 WebView2 弹 | 由 WebView2 自己执行保留下来的项 |
| macOS | **接管式**：`willOpenMenu:` 里 `removeAllItems` 后换上我们的三项 | `NSMenuItem` 的 `cut:` / `copy:` / `paste:`，经 `sendAction:to:from:`（target = nil）走响应链 |
| Linux | **接管式**：`context-menu` 信号返回 `TRUE`（"我自己弹，你别弹"），宿主用 `GtkMenu` 弹三项 | `webkit_web_view_execute_editing_command(webview, "Cut"/"Copy"/"Paste")` |

Windows 为什么保持过滤式：WebView2 没有公开的 cut/copy/paste 编程接口，自建菜单的编辑项只能退回
`document.execCommand`，粘贴会失效；而它保留下来的项由引擎自己执行，这条路本来就通。

macOS/Linux 为什么不能"就地增删引擎的菜单"：那要经 `webkit_context_menu_get_items` 配合
`remove` / `g_list_free` 之类的接口，在 WebKitGTK 4.1 上会破坏引擎内部结构——连点几次右键就崩。
完整症状与二分证据见 `docs/DECISIONS.md`。

`OrielMenuRoles.IsEditingRole` 与 `EditingMenuItems()` 集中定义"哪些 role 算编辑类""接管菜单弹哪三项"；
编辑类 role 优先交给平台的原生通道（`TryActivate` 的 `nativeEditing` 参数），平台不认识才退回 `execCommand`。

### 验证账

| 项 | 机器断言 | 尚未验证 |
|---|---|---|
| 接管菜单的内容与 role 归类 | **37 个用例**：三项的顺序与默认文案、role 项不带 `Id`、`IsEditingRole` 的编辑类/非编辑类边界；以及 Windows 保留名单（含 `copyImage`/`copyLink` 这类"看着像复制但不是"的陷阱、`null`/空串/大小写） | **菜单弹出后的实际外观**、**剪切/复制/粘贴在真机上是否整链路可用**——需人眼，清单见 `docs/ROADMAP.md` |
| Linux 接管链路 | **实测**（WSLg + GTK3）：连点右键 6 次不崩、stderr 无警告、菜单窗口按指针位置弹出、`"Copy"` 命令把页面选中内容送进了系统剪贴板 | macOS 侧全部（本机无 macOS） |

## 手动验证与无人自检

两类验证各有入口：

- **给人看的**：直接运行 demo（**不带参数**）就是手动验证操作台。
- **给 CI 的**：`--selftest <名字>` 跑无人自检，跑完打印结论并以退出码表达成败。可用名字：
  `nav` | `ipc` | `clipboard` | `theme` | `single-instance` | `shell`。
  名字缺失或写错时会列出可用值并以退出码 2 结束——不留"静默跑成别的模式"的空间。

```bash
OrielDemo.exe --selftest shell   # 托盘、通知、菜单、自启、Shell、拖放
OrielDemo.exe --selftest nav     # 跳转 → 后退 → 前进 → 刷新 → 加载失败
OrielDemo.exe                    # 默认：手动验证操作台
OrielDemo.exe --todo             # Todo 示例页（"怎么用本库写应用"的示范）
```

操作台把每项能力做成一个按钮页面，
并把**托管侧的回调**（托盘菜单项、上下文菜单项、拖放路径）回显到页面底部的日志区。
其中「内建右键菜单」那一栏可以直接切策略（`Editing` / `Native` / `Disabled`），切完在页面任意处右键
即可对比——那一栏还带一个输入框，用来验证留下的三项**真的能作用在选中内容上**（粘贴要能把系统剪贴板
内容送进去，这是"过滤没把原生行为弄坏"的关键证据）：

```bash
dotnet run --project samples/OrielDemo -c Release -- --manual-check
```

页面上每项都写了"预期是什么"，其中几处**看起来像 bug 其实不是**：

| 现象 | 为什么是预期 |
|---|---|
| 点通知不会有回调 | **该能力已整体移除**——三平台都拿不到点击（见「验证账」），所以不再提供这个事件，而不是留一个永不触发的空实现 |
| 上下文菜单开着时窗口不响应 | 原生弹出菜单的模态行为，直到你选择或取消 |
| 选文件夹时初始目录无效 | Windows 走老 API（`SHBrowseForFolder`），设初值要挂回调，已记为取舍 |
| 日志写「已提交给系统」但没看到横幅 | 库已把通知交给系统；显不显示由系统的通知设置与专注助手决定（Windows：设置 → 系统 → 通知） |

> `ShowNotification` 返回 `bool` 正是为这条分辨而生：「提交失败」才是实现问题，
> 「已提交但没显示」是系统设置问题。两者混在一起就只能靠猜。

## 构建与发布

仓库里带一个发布脚本，默认把产物放到 `publish/<rid>/`：

```powershell
pwsh tools/publish.ps1                    # 本机平台，产物在 publish/<当前 rid>/
pwsh tools/publish.ps1 -Runtime win-arm64 # 指定 RID（只能是当前操作系统的，见下）
pwsh tools/publish.ps1 -Zip               # 额外打一个 zip 便于分发
pwsh tools/publish.ps1 -NoClean           # 保留上一次的产物

pwsh tools/wsl_publish.ps1                # Windows 上出 Linux 产物：丢进 WSL 里发布
pwsh tools/wsl_publish.ps1 -Zip           # 同上，并打包成 publish/linux-x64.zip
pwsh tools/wsl_publish.ps1 -DryRun        # 只打印将要执行的命令，先确认路径映射

pwsh tools/verify-pack.ps1                # 验证包内 build logic：打包 → 消费 → 断言资源名
```

`tools/verify-pack.ps1` 走的不是"发布应用"，而是**包的消费链路**：先 `dotnet pack` 到本地
`dist/nuget`，再构建 `samples/OrielMinimal/`（一个只写 `<PackageReference>` 的工程），
断言 `wwwroot` 资源被嵌成正确的名字。四个场景：零配置、旧写法不重复嵌入、显式关掉、
以及包里确实带上了那份 targets。改了 `buildTransitive/OrielWeb.targets` 之后跑它。

**按 RID 分子目录不是偏好而是必需**：三个平台/架构的 AOT 产物都是自包含的，混在一个目录里会互相覆盖，
也判断不出"这份是给谁的"。分开之后多次发布互不干扰，清理也只影响对应那一份。

它只是下面这些命令的封装，多做了三步：产物落到固定位置、发布前清掉旧产物（否则分不清这次产出了什么）、
发布后校验主产物确实存在：

```bash
# Windows
dotnet publish samples/OrielDemo -c Release -r win-x64

# macOS（需要 Mac；产物必须打包成 .app 才能运行 WKWebView，见「macOS 运行要求」）
dotnet publish samples/OrielDemo -c Release -r osx-arm64

# Linux（需要 libwebkit2gtk-4.1；中文界面另需 CJK 字体，见「Linux 环境依赖与已知限制」）
dotnet publish samples/OrielDemo -c Release -r linux-x64
```

> 不给 `-Runtime` 时脚本按当前平台与架构推断（`win-x64` / `linux-x64` / `osx-arm64` …），
> 所以在自己机器上发布通常不用带任何参数。
>
> **不能跨操作系统发布**：NativeAOT 不支持（ILCompiler 会以
> `Cross-OS native compilation is not supported` 失败），脚本会在发起编译**之前**挡下并说明原因——
> 这也是 CI 里每个平台各有一个 runner 的缘故。跨**架构**是另一回事（例如在同一台 Linux 上出 arm64），
> 能否成功取决于是否装了目标架构的工具链。另外 macOS 的产物必须打包成 `.app` 才能启动 WKWebView（见下）。
>
> Windows 上要出 Linux 产物不必切机器：**`tools/wsl_publish.ps1` 把发布丢进 WSL**——对 NativeAOT 来说
> WSL 就是"目标平台"，于是 `publish.ps1` 挡下的那个缺口由它补上。产物仍落在 Windows 侧的
> `publish/linux-x64/`，与 `publish.ps1` 的产物按 RID 分目录共存、互不覆盖。它只接受 `linux-*`，
> 其余平台仍归 `publish.ps1`。两个注意点：AOT 在 `/mnt/*`（跨文件系统）上明显更慢，首次可能几分钟；
> 仓库放在 UNC 路径（`\\server\share`）或网络驱动器上时映射不到 `/mnt`，脚本会直接报错——
> 拿不准就先 `-DryRun` 把要执行的命令与全部路径打印出来看。

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

### 三平台分发包（GitHub Release）

推送 `v*` 标签会触发 `.github/workflows/release.yml`：三个平台各自在自己的 runner 上发布 demo
（NativeAOT 不能跨操作系统编译，这正是 CI 里每平台一个 runner 的原因），再汇总到同一个 Release。
资产名按 RID 区分——三份产物都是自包含的，混在一起分不出谁是谁：

| 资产 | 内容 | 运行前提 |
|---|---|---|
| `OrielWeb-demo-win-x64.zip` | `OrielDemo.exe` | WebView2 Runtime（缺失时应用自己会引导安装） |
| `OrielWeb-demo-linux-x64.zip` | `OrielDemo` | `libwebkit2gtk-4.1` + GTK3 运行库；中文字体与 Wayland 的限制见[平台运行要求](#平台运行要求) |
| `OrielWeb-demo-osx-arm64.zip` | `OrielDemo.app`（ad-hoc 签名） | Apple Silicon；下载后要去掉 quarantine 属性才能启动 |

三个包都用 `-p:DebugType=none` 发布，所以不带 pdb/dbg/dSYM。macOS 那份**必须是 `.app` 而不是裸可执行文件**：
WKWebView 是多进程架构，宿主进程要凭 main bundle 身份才能与 WebContent 通信，裸文件会 `SIGTRAP`
（退出码 133）——原因与验证过程见 [macOS 运行要求](#macos-运行要求)。

下载后怎么跑：

```bash
# Linux
unzip OrielWeb-demo-linux-x64.zip && ./OrielDemo

# macOS：先去掉隔离标记，再打开
unzip OrielWeb-demo-osx-arm64.zip
xattr -dr com.apple.quarantine OrielDemo.app
open OrielDemo.app
```

macOS 那份是 **ad-hoc 签名、未做公证**：Gatekeeper 对"从网上下载、又没有 Developer ID 签名"的包
一律拦截，去掉 quarantine 是最省事的路（右键 → 打开 也可以）。

## 平台运行要求

> 各平台的支持状态（哪些已运行验证、哪些只有编译）见前面的[平台支持](#平台支持)一节。
> 这里只讲**跑起来需要什么、有什么已知限制**。

**内嵌资源 URL（`UseEmbeddedAssets` 的虚拟主机）在三平台靠不同机制落地**：`WithUrl($"https://{host}/…")`
在 Windows 上由 WebView2 的虚拟主机映射处理；在 Linux/macOS 上则由库在导航前把它映射成解压目录里的本地文件
（那两个引擎只能注册**自定义** scheme，而 `https` 是保留 scheme）。对调用方是同一个 URL、同一份资源；
host 之外的外部 URL（Vite dev server 等）原样加载。背景与取舍见 `docs/DECISIONS.md`。

### Linux 环境依赖与已知限制

- **WSLg / 无 GPU 环境下的 MESA 警告是噪音，别当线索**：stderr 会出现这样一串——

  ```
  libEGL warning: failed to get driver name for fd -1
  libEGL warning: MESA-LOADER: failed to retrieve device information
  MESA: error: ZINK: failed to choose pdev
  libEGL warning: egl: failed to create dri2 screen
  ```

  WSLg 下没有 `/dev/dri`，Mesa 找不到硬件 GL，回落到软件渲染——**页面照常显示，功能不受影响**
  （只是性能差些）。**尤其不要拿它排查"窗口白屏"**：同环境下未经加速的 GTK+WebKit 程序照样画得好，
  白屏另有原因（见[平台运行要求](#平台运行要求)开头的内嵌资源说明与 `docs/DECISIONS.md`）。
  这条是**被误导过一次之后**才记下来的。
- **中文字体必须另行安装**：WebKitGTK 经 fontconfig 取字体，发行版未预装 CJK 字体时中文会渲染成方框。
  先装 `fonts-noto-cjk`（Debian/Ubuntu）再运行；页面侧的 `font-family` 也应带上 `"Noto Sans CJK SC"`
  这类跨平台族，而不是只写 `"Segoe UI"` / `"Microsoft YaHei"` 这类 Windows 专有字体名。
  库本身不碰字体（字体选择属应用与系统职责）。
- **无边框拖动在两种后端下是两条路**：X11 按增量自己摆（`gtk_window_move`）；Wayland 协议不允许客户端
  移动自己的窗口（连窗口位置都拿不到），改调 `gtk_window_begin_move_drag`，由 GDK 转成
  `xdg_toplevel.move` 交给合成器——跟手、边缘吸附与贴边平铺因此都是原生行为。这条路的时序有要求
  （请求必须在鼠标按住期间发出，理由见 `docs/DECISIONS.md`），**真机上的跟手效果尚未人眼确认**；
  若在你的合成器上拖不动，可用 `GDK_BACKEND=x11` 作对照。

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

## 路线图

后续要补的能力（**三平台一致性缺口 → 内容/IPC 深度 → 平台集成外壳**）、每一项的验证方式，
以及无头环境验证不了的那部分"待真机验证清单"，见 [docs/ROADMAP.md](docs/ROADMAP.md)。
已经落地的取舍与实测结论见 [docs/DECISIONS.md](docs/DECISIONS.md)。

## 许可

[MIT](LICENSE) © OrielWeb Contributors。

本库在编译期内嵌了微软分发的 `WebView2Loader.dll`（取自 `Microsoft.Web.WebView2` 包），
其版权与许可声明见 [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt)。
