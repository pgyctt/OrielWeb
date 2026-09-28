# OrielWeb

类 Tauri 的 C# 跨平台系统 webview 核心库。无 C++ 中间层、无 GUI 框架依赖、Native AOT 友好、零反射 IPC。

## 特性

- **纯 C# 互操作**：macOS（WKWebView + ObjC runtime）与 Linux（GTK3 + WebKitGTK）为手写 P/Invoke；Windows 的 WebView2 COM 走 `WebView2Aot` 的 `[GeneratedComInterface]`/`[GeneratedComClass]` **源生成绑定**（无手写 vtable/IID/RefCount）。三平台均不需要 C++ 中间层
- **Composition 宿主**（Windows）：WebView2 作为 DirectComposition 的一份视觉合成进窗口，而非子窗口——因此无边框窗口的边缘 resize 能走系统原生路径，且窗口内容可与其它视觉自由合成
- **零反射 IPC**：`[OrielCommand]` + Roslyn 源生成器在编译期生成分发代码，`[ModuleInitializer]` 自动注册，运行期零反射
- **Native AOT**：全局 `IsAotCompatible`/`IsTrimmable`，发布为原生单文件可执行文件（WebView2 的运行时加载器已内嵌，无需旁文件）
- **无边框窗口**：自绘标题栏 + 流式/原生拖动 + 最大化/全屏/置顶切换
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

## 构建

```bash
# Windows
dotnet publish samples/OrielDemo -c Release -r win-x64

# macOS（需要 Mac）
dotnet publish samples/OrielDemo -c Release -r osx-arm64

# Linux（需要 libwebkit2gtk-4.1）
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
| Linux x64/arm64 | WebKitGTK 4.1 | ⚠️ 编译通过；CI 在 xvfb 下冒烟（进程存活）；尚未在真机完整验证 |
| macOS x64/arm64 | WKWebView | ⚠️ 仅编译通过；尚未在真机运行过 |

> 上表只写有证据的结论：Windows 侧有真机运行记录，Linux/macOS 目前只有编译与冒烟级别的验证。

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
- 反例：`samples/OrielDemo` 的 `TodoCommands` 直接读写 `List<T>` 与 `_nextId++`，并发下并不安全；
  示例为保持简洁如此编写，实际项目请自行加锁或改用线程安全结构。

## 许可

[MIT](LICENSE) © OrielWeb Contributors。

本库在编译期内嵌了微软分发的 `WebView2Loader.dll`（取自 `Microsoft.Web.WebView2` 包），
其版权与许可声明见 [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt)。
