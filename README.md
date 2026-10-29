# OrielWeb

类 Tauri 的 C# 跨平台系统 webview 核心库。纯 C# P/Invoke、无 C++ 中间层、Native AOT 友好、零反射 IPC。

```bash
dotnet add package OrielWeb
```

| 平台 | Webview | 状态 | 仓库内可核对的证据 |
|---|---|---|---|
| Windows x64/arm64 | WebView2 (Evergreen) | ✅ 真机验证（IPC 往返、单测、AOT 发布） | `ci:compile-windows`、`ci:smoke-windows` |
| Linux x64 | WebKitGTK 4.1 | ✅ 真机验证（WSL2 + WSLg；X11 与 Wayland 双后端） | `ci:compile-linux`、`ci:smoke-linux` |
| macOS arm64 | WKWebView | ✅ 真机验证（GitHub 托管 runner） | `ci:smoke-macos` |
| Linux arm64 | WebKitGTK 4.1 | ✅ CI 自检（原生 arm64 runner + Xvfb，2026-10-23 首绿）；未在真机桌面运行 | `ci:compile-linux`、`ci:smoke-linux-arm64` |
| macOS x64 | WKWebView | ✅ 真机验证（`macos-15-intel` 托管 runner，2026-10-23 首绿）；此前只做过 IL 编译 | `ci:compile-macos`、`ci:smoke-macos-x64` |

> **"证据"列不是形容词，是可核对的引用**：`ci:<job>` 指向 `.github/workflows/ci.yml` 里的 job 名，
> 由 `tools/verify-matrix.ps1` 在 `test` job 里校验；引用一个不存在的 job 会让 CI 红。
> 这条规则的由来：2026-10-23 评审发现 arm64 那行写着"xvfb 冒烟"而仓库里只有 IL 交叉编译——
> 一句无法复现的话，比一句难看的实话贵得多。
>
> macOS 的 Intel runner 在 2025-12-04 随 `macos-13` 退役，现用 `macos-15-intel`（可用到 2027 年秋）；
> Linux arm64 用 `ubuntu-24.04-arm`（公开仓库免费，原生 4 vCPU）。

**完整 API 与各能力的取舍记录见 [docs/API.md](docs/API.md)**；尚未完成与待真机验证的部分见 [docs/ROADMAP.md](docs/ROADMAP.md)。

想按 Tauri 的写法对照？[`samples/TauriExamples`](samples/TauriExamples/README.md) 是官方
`tauri/examples` 的五份逐项移植（commands / state / multiwindow / splashscreen / drag），
每个目录都写明了"页面代码到底改了哪几行"——包括那些**没改**的地方。

想逐个 API 点着看？[`samples/ApiDemo`](samples/ApiDemo/README.md) 是已实现 API 的主验证台：
17 个模块、300+ 个可运行动作，一条 API 一行，回执里直接看到成功值、错误码或平台差异。

## 快速开始

要求 **.NET 10 SDK**（本库只提供 `net10.0` 目标）。

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>             <!-- 不显示控制台窗口 -->
    <TargetFramework>net10.0</TargetFramework>
    <PublishAot>true</PublishAot>               <!-- 原生单文件发布 -->
    <ApplicationIcon>app.ico</ApplicationIcon>  <!-- Windows 的任务栏图标 -->
  </PropertyGroup>
  <!-- 就这些。wwwroot 下的前端资源由包内的 buildTransitive/OrielWeb.targets 自动内嵌（零配置）。
       注意这一条只在**从包**消费时生效：build/ 与 buildTransitive/ 不随 ProjectReference 分发，
       那种工程要自己写一行带 LogicalName 的声明——见「内嵌页面资源」一节。 -->
</Project>
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
            .UseEmbeddedAssets()                    // oriel://app.oriel/ ← wwwroot/**
            .UseJsonContext(AppJsonContext.Default) // STJ 源生成上下文（DTO）
            .AddCommands<TodoCommands>()            // [OrielCommand] 命令类
            .UseCapabilities(c => c.Allow("todo.*"))// 页面能调哪些命令（Release 下不写就一律拒绝）
            .UseDebug()                             // 打开 DevTools
            .AddWindow(w => w.WithTitle("Demo")
                             .WithSize(1024, 720)
                             .WithFrameless()       // 无边框
                             .Centered(),
                       win => win.Loaded += () => Console.WriteLine("已就绪"))
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
<!-- wwwroot/index.html -->
<script>
    await window.oriel.ready;
    const item = await window.oriel.invoke('todo.add', { text: '买牛奶' });
    // → JSON 参数 → 编译期路由 → C# 方法 → JSON 回执 → Promise resolve
</script>
```

> 上面的 `window.oriel` 是最小可运行形态，等价于 Tauri 的 `__TAURI_INTERNALS__`——能用，但不是公开 API。
> 应用代码建议用 npm 包 `@orielweb/api`：模块名与 `@tauri-apps/api` 一一对应，迁移只改 import 前缀。
>
> ```sh
> npm install @orielweb/api
> ```

**两条容易踩的约定**：

- 应用自己的命令**不要用 `win.` 或 `tray.` 前缀**——那是库保留的命令前缀，始终放行、不受能力配置约束，
  而且其中的内建命令（窗口：最小化/最大化/关闭/拖动/全屏/置顶/选文件/上下文菜单；托盘：new/setMenu/…）
  真正占用了这些名字：自定义的同名命令**会在构建期报错**（`ORIELWEB106`）。它原先只是警告——而"注册成功但永远
  不会被调用"意味着一个永远不会执行的分支，症状是"我明明写了这个命令、页面说找不到"，
  根因却在另一条命令上。改成构建失败后，代价是改个前缀，收益是这类排障消失。
  `tray.` 是 2026-10-23 补拦的（此前只拦 `win.`）：前缀整族绕过能力配置的洞两个前缀同构。
  另外注意**任何前缀下与内建精确名重合**（如 `window.close`、`fs.readTextFile`）会在
  **启动时**抛 `InvalidOperationException`——内建命令先于应用路由解析，这类重合编译期看不见，
  只能运行期拦（两层分工：编译期管前缀，运行期管精确名）。
- **Release 构建下必须声明能力**，否则 `oriel.invoke` 一律被拒（fail-closed）。Debug 构建则全放行。

## 内嵌页面资源

前端资源**编译进程序集**，运行期由库通过自定义 scheme `oriel://<host>/…` 直接交给引擎加载
（**不写盘**，也不再解压到任何目录）。发布产物里没有 `wwwroot` 文件夹，但页面照常工作；
从 **NuGet 包**消费时默认**零配置**（本仓库内用 `ProjectReference` 的工程要自己声明一行，原因见下）。

### 零配置是怎么来的

包内的 `buildTransitive/OrielWeb.targets`（默认开启）把项目里的 `wwwroot\**\*` 全部内嵌：

```xml
<!-- targets 自动做的事，等价于下面这行；新项目不要手写它 -->
<EmbeddedResource Include="wwwroot\**\*"
                  LogicalName="$(AssemblyName).wwwroot/$([System.String]::Copy('%(RecursiveDir)').Replace('\','/'))%(Filename)%(Extension)" />
```

资源名用**显式 `/` 分隔符**（`MyApp.wwwroot/assets/img/logo.svg`）。这是刻意的：若按 MSBuild 的默认形式反推，
`app.min.js` 会被推成 `app/min.js`——**静默 404、页面白屏**，而资源名里没有任何信息能区分这两种情况。
`%(RecursiveDir)` 在 Windows 上带**反斜杠**，所以上面那行显式把它归一成 `/`：不归一的话子目录资源名会是
`MyApp.wwwroot/assets\img\logo.svg` 这种混合形态——库读取时会归一（`EmbeddedAssetStore` 那道安全网），
但资源名就不长成这里承诺的样子了。自己声明资源时请照抄上面那行的写法（`samples/OrielDemo` 与
`tools/verify-package.ps1` 的场景 B1 都是）。

构建日志会说明它做了什么（在 `-v:n` 及以上可见，默认的 `dotnet build` 看不到）：

```
OrielWeb: 已自动内嵌 12 个 wwwroot 资源（LogicalName 用显式 '/' 分隔符，含点文件名不会再被推错路径）。
```

| 情形 | 行为 |
|---|---|
| 项目里没有 `wwwroot`，或里面没有文件 | 启动时**抛异常**并说明该怎么配（不是静默白屏） |
| 项目自己声明 `wwwroot` 资源（**带** `LogicalName`） | targets 检测到后**跳过**自动内嵌（日志会说明），资源名与自动内嵌逐字符一致，不会重复嵌入 |
| 项目写了 `<EmbeddedResource Include="wwwroot\**\*" />`（**旧写法**，不带 `LogicalName`） | **构建期报错 `ORIELWEB001`**。那种资源名无法区分"目录"与"含点的文件名"，库猜不准——删掉那行即可 |
| 不想要自动内嵌 | `<OrielWebEmbeddedAssets>false</OrielWebEmbeddedAssets>`，然后自己声明资源（必须带 `LogicalName`，并用 `UseEmbeddedAssets` 的第二个参数指定资源前缀） |

> 上面那条"检测到就让位"的逻辑不是多余的：若没有它，同一个文件被两种方式各内嵌一次，页面会同时存在
> 两条指向同一份文件的资源，MSBuild 还可能直接报 `CS1508 已使用资源标识符`。

> **仓库内用 `ProjectReference` 的工程要自己声明**：`build/` 与 `buildTransitive/` 是 NuGet 包的机制，
> 只随包分发——`ProjectReference` 不带它们。所以本仓库的 `samples/OrielDemo` 自己写了带 `LogicalName`
> 的那一行，而零配置只有从**包**消费时才生效（`samples/OrielMinimal` 与 `tools/verify-package.ps1` 走的
> 就是那条路）。踩这个坑的症状是启动即 `未找到前缀为 '…wwwroot/' 的内嵌资源`。
>
> 这类工程既然不导入 targets，构建期那道 `ORIELWEB001` 也拦不到它——**运行期**会以"检测到旧写法内嵌资源"
> 报出来并给出正确写法。那条运行期检查就是为这条路径留的安全网。

> **旧写法为什么被删掉**（2026-10-03）：不带 `LogicalName` 时 MSBuild 会把 `%(RecursiveDir)` 里的分隔符压成 `.`，
> 于是 `app.min.js` 与 `app/min.js` 生成的资源名**完全一样**——库只能靠"最后一个 `.` 是扩展名"去猜，
> 而猜错的表现是页面 404 白屏、宿主侧毫无异常。既然无法正确解释，就不要装作能解释：这种写法现在在**构建期**
> 被 `ORIELWEB001` 拦下，错误信息里直接给出正确写法。回归证据是 `tools/verify-package.ps1` 的场景 B1
> （带 `LogicalName` 的自声明受支持）与 B2（旧写法必须构建失败）。

### C# 侧的开关

```csharp
.UseEmbeddedAssets()                                  // 默认：host 是 app.oriel（即 oriel://app.oriel/），资源前缀按程序集名推断
.UseEmbeddedAssets("myapp.local")                     // 换 host（于是页面在 oriel://myapp.local/ 下）
.UseEmbeddedAssets("app.oriel", "MyApp.wwwroot/")     // 显式指定资源名前缀（自己声明资源时用；必须以 '/' 结尾）
```

### 运行期：资源怎么交给页面

三平台统一走自定义 scheme `oriel://<host>/…`，**资源留在程序集里**，由各平台的 scheme 处理器按需应答：

```
oriel://app.oriel/index.html          （默认 host 是 app.oriel）
```

- **不落盘**：只读介质、容器、受限沙箱里都能跑；也不在用户目录里留一份可被篡改的前端文件。
  由此消失的还有一整套麻烦——"启动时递归清空"、"代码里删了资源运行时还能访问到"、
  "递归删除前必须校验路径否则删错数据"、"第二个实例先清空了前一个的目录"。
- 资源是编译进程序集的，所以**单文件 / Native AOT 发布**后依然取得到。
- 内容类型（MIME）**由库给**：自定义 scheme 没有引擎内置的"按扩展名推断"这一步，所以库里有一张
  扩展名 → Content-Type 的表（见 `src/OrielWeb/Assets/MimeTypes.cs`）。
  认不出来的扩展名一律 `application/octet-stream`，不做猜测。

### 三平台怎么把它交给引擎

| 平台 | 通道 | 页面所在的来源 |
|---|---|---|
| Windows | `WebResourceRequested` 拦截 + `CreateWebResourceResponse`（自定义 scheme 经 `CoreWebView2CustomSchemeRegistration` 登记为 `TreatAsSecure`） | `oriel://<host>/…` |
| Linux | `webkit_web_context_register_uri_scheme` + `WebKitURISchemeRequest`（`register_uri_scheme_as_secure`） | `oriel://<host>/…` |
| macOS | `WKURLSchemeHandler`（挂在 `WKWebViewConfiguration` 上，建 webview 之前） | `oriel://<host>/…` |

> **为什么不用 `file://`**：`file://` 是不透明来源，`fetch` 相对路径、`localStorage`、安全上下文这些全都要看
> 引擎脸色，而且三平台并不一致；更麻烦的是它要求资源**必须真的在磁盘上**（macOS 还要用
> `allowingReadAccessToURL:` 单独把读权限授予那个目录，少了它同目录的 `styles.css`/`app.js` 会被拦下，
> 页面照样空白）。换成有 authority 的自定义 scheme 之后，URL、来源、安全上下文语义三平台一致。
>
> **`https://<host>/…` 仍然接受**，作为兼容别名映射到同一份资源（0.2.0 及以前的文档与示例写的都是这个形态，
> 直接不认会让已有项目白屏）。别名只影响"入参怎么解析"：页面真正的来源始终是 `oriel://`，所以可信前缀也只有
> 这一条。**由此带来的一条实用推论**：页面 origin 现在是 `oriel://<host>`，你的前端代码不要依赖
> `location.origin` 的具体值（开发期连 dev server 时它就是 `http://localhost:…`）。

### URL 怎么解析

`oriel://<host>/…`（以及兼容别名 `https://<host>/…`）由 `AssetUrl` 解析成资源相对路径：

| 输入 | 结果 |
|---|---|
| `oriel://app.oriel/` 或任何以 `/` 结尾的空路径 | `index.html` |
| `…/sub/page.html` | `sub/page.html`（子目录保持层级） |
| `…/manual-check.html?v=2#top` | `manual-check.html`（query 与 fragment 不参与定位） |
| `…/../secret` | 被 URI 解析折叠成 `…/secret`——**这是 URI 规范的行为，不是防线**；真正的边界是资源表查表（查不到就是 404） |
| `…/..%2Fsecret`（编码的斜杠不会被折叠） | **拒绝** |
| `https://app.oriel.example.com/…` | **不命中**（host 精确相等，不做后缀匹配） |

### 默认首页与开发期

窗口的 `Url` 没设时导航到 `oriel://<host>/index.html`。

开发期让页面指向 Vite 之类的 dev server 只需设 `Url`，**但要同时放行那个来源**——否则页面在库眼里是"外来的"，
桥接脚本根本不会安装（见[安全与能力模型](docs/API.md#4-安全与能力模型)）：

```csharp
.AddWindow(w => w.WithUrl("http://localhost:5173/"))
.UseCapabilities(c => c.AllowOrigin("http://localhost:5173/"))
```

> 没有"指向磁盘上任意目录"的一等入口：内嵌资源是一条 scheme 内的资源表，不是"把某个目录挂上去"。
> 要加载磁盘上的文件请自己起一个本地 http server，再按上面的方式放行来源。

### 不在 `wwwroot` 里的文件

没有机制。要么放进 `wwwroot`（会被整体内嵌），要么由应用通过 `WithUrl` 指向外部服务。
MIME 由库按扩展名给出（自定义 scheme 下引擎不再替你推断，表在 `src/OrielWeb/Assets/MimeTypes.cs`）。

### 验证账

- `tools/verify-package.ps1`（CI 的 `test` job 每轮跑）造四种消费形态并断言资源名：
  **A 零配置** → 显式分隔符形式（`MyApp.wwwroot/app.min.js`，不会被拆成 `app/min.js`）；
  **B1 自带 `LogicalName` 的自声明** → 与零配置逐字符一致，且 targets 确实跳过了（没有重复嵌入）；
  **B2 旧写法（不带 `LogicalName`）** → **构建失败**并给出正确写法（错误码 `ORIELWEB001`）；
  **C `-p:OrielWebEmbeddedAssets=false`** → 一条 wwwroot 资源都没有。
- 单测：`AssetUrlTests`（默认首页、query/fragment、子目录、`oriel://` 与兼容别名、相似域名不命中、
  `..` 折叠与编码斜杠被拒、MIME 表）、`EmbeddedAssetTests`（只认显式 `/` 分隔符这一种约定、自定义前缀必须以
  `/` 结尾、旧形式报错并指路、请求路径归一与穿越拒绝）。
- 真机：三平台的自检都从内嵌资源加载页面（`--selftest nav` 覆盖了"导航到不存在的页面应当失败"这类细节，
  自定义 scheme 的 404 必须真的失败而不是返回一页空内容）。
- 事实探针：`--selftest scheme` 打印页面来源与"来源决定的能力"（`isSecureContext`、`crypto.subtle`、
  `localStorage`、相对路径 `fetch`），并写成一条 GitHub 注解（注解读得出来、日志要 token）。
  它断言两条设计承诺——来源必须是 `oriel://<host>`、相对 `fetch` 必须取得到资源——其余**如实报告**：
  三平台的自定义 scheme 能力并不对称（macOS 没有"标记为安全上下文"的公开开关），所以这些事实是
  **量**出来的、不是从上游文档推断的。2026-10-03 的实测基线三平台一致：
  `origin=oriel://app.oriel secure=true subtle=true storage=ok fetch=ok:200:949`。

## 能力一览

| 能力 | 一句话 |
|---|---|
| 内嵌页面资源 | `wwwroot\**\*` 从包消费时零配置自动内嵌；运行期由 `oriel://<host>/…` 直接应答（不落盘）；可用 `UseEmbeddedAssets(host)` 改 host、`OrielWebEmbeddedAssets=false` 关闭 |
| 无边框窗口 | 自绘标题栏（拖动与双击由库接管，页面只标一个属性）；窗口命令 `win.*`（最小化/最大化/关闭/拖动/全屏/置顶/选文件/上下文菜单）由库内建，应用一行不写；窗口图标三平台各自落到真正的图标槽 |
| 导航与 IPC | 前进/后退/刷新、导航事件（含失败原因）、`invoke`（有回执）/`postMessage`（单向）/`EmitEvent`（宿主→页面）/console 转发 |
| 安全与能力模型 | 来源 + 令牌 + 按命令授权三层；不可信来源的页面根本拿不到桥接脚本。**逐消息来源校验的精度三平台不同**（Linux 只能判到顶级文档，见 [API.md §4](docs/API.md)） |
| 主题 / 单实例 | 深浅色检测与变更事件、第二实例唤醒首实例 |
| 平台集成 | 托盘（**对标 Tauri 2**：多托盘按 id 管理、图标是已解码的 RGBA 对象、富事件 Click/DoubleClick/Enter/Move/Leave、前端 `oriel.tray` 与后端同名同义）、系统通知、窗口上下文菜单、Shell（打开外链/定位文件）、开机自启 |
| 对话框 | 打开（可多选）/ 保存 / 选文件夹，支持结构化过滤器与初始目录 |
| 文件拖放 | 拖入文件/文件夹 → 本地路径列表 + 事件 |
| 内建右键菜单 | 默认只留剪切/复制/粘贴，可切平台原样或完全禁用 |
| 多窗口 | `app.CreateWindow(...)` 运行时新建窗口；关掉一个窗口应用不退出，`app.Windows` 自动摘除已关闭的窗口 |
| 打包与更新 | 交给 **Velopack**（`vpk`）：Windows 出 Setup.exe / .msi / Portable.zip，Linux 出 AppImage，macOS 出 Setup.pkg / Portable.zip；Windows 上 `pwsh tools/publish.ps1 -Bundle` 一条命令出全套（macOS 的 .app 组装走 release.yml / `tools/make-macos-app.sh`，Linux 用 `tools/wsl_publish.ps1`）。**应用自更新尚未接入**（见下） |

各项的用法、平台差异与**为什么这么设计**见 [docs/API.md](docs/API.md)。

## 打包与分发（Velopack）

安装包与自动更新交给 [Velopack](https://velopack.io)：本库**不再自带打包器**，只把 `dotnet publish`
的产物交给 `vpk`。发布仍然只由 `dotnet publish` 决定（各项目的 AOT 属性不同，库没有立场替你决定）。

```bash
dotnet tool install --global vpk

# 发布 + 打包一条命令（版本号从根 Directory.Build.props 的 <Version> 读）
pwsh tools/publish.ps1 -Bundle

# 也可以分开：先发布，再让 vpk 消费产物
pwsh tools/publish.ps1
vpk pack --packId OrielDemo --packVersion <版本> --packDir dist/win-x64 \
  --packTitle "Oriel Demo" --mainExe OrielDemo.exe --icon samples/OrielDemo/app.ico --msi
```

产物落在 `dist/<rid>-releases/`。Windows 上的实际清单（Linux 与 macOS 见下）:

| 产物 | 用途 |
|---|---|
| `OrielDemo-win-Setup.exe` | 安装程序（Velopack 在 Windows 上的主形态） |
| `OrielDemo-win.msi` | machine-wide 的**引导包**（`vpk --msi`）——它只是 Setup.exe 的外壳，不是"per-user 安装" |
| `OrielDemo-win-Portable.zip` | 免安装的便携版 |

Linux 上（`pwsh tools/wsl_publish.ps1 -Bundle`，打包在 WSL 里跑）出的是另一套：

| 产物 | 用途 |
|---|---|
| `OrielDemo.AppImage` | **自更新的 AppImage**——Linux 上没有独立的安装器，这就是分发形态 |

AppImage 不需要外部 `appimagetool`（`vpk` 自带 appimagekit runtime），也不需要 FUSE 就能自解包运行。

macOS 上（在 macOS 机器上跑，`vpk` 消费 `tools/make-macos-app.sh` 组装好的 `.app`）出的是：

| 产物 | 用途 |
|---|---|
| `OrielDemo-osx-Setup.pkg` | 安装包（`.pkg`）——macOS 上没有独立的 Setup.exe 这类外壳 |
| `OrielDemo-osx-Portable.zip` | 免安装的便携版（解压出 `.app`） |

macOS 上 `vpk pack` 有两条**额外要求**，都是实测踩出来的：

- **`.app` 里必须有 `Contents/Resources/` 目录**：`vpk` 要往里写 `sq.version`，而且假定目录已存在
  （缺它时报 `DirectoryNotFoundException: … Contents/Resources/sq.version`）。`make-macos-app.sh` 会建好。
- **不要给 `--icon` 传 `.png`**：`vpk` 在 macOS 上认的是 `.icns`（Windows 用 `.ico`、Linux 用 `.png`），
  传错格式会直接失败。`--icon` 本就可选（`--help` 里只有 `--packDir` 标了 REQ），图标该由 `.app` 自己带；
  要给图标就从 `app.png` 生成 `.icns` 放进 bundle。

- **只留"能装的东西"**：更新包 `*-full.nupkg`（Velopack 的"release"）与更新清单
  （`releases.*.json` / `assets.*.json` / `RELEASES*`）在打包后**即被删除**——本仓库不发更新源，
  前者与我们的 NuGet 包同名同扩展、后者会指向一个已被删掉的包，留着都只会让人以为有更新可用。
  `vpk` 没有"不产这些"的开关（`--noInst` / `--noPortable` 只管安装器与便携版），所以这一步在打包之后做。
- **不需要外部打包工具**：`vpk` 自带它们（`--msi` 那一步它内部就用 WiX 模板编译，机器上不必装 WiX）。
- **版本号只接受三段 semver2**：`1.2.3.4` 会被拒；`publish.ps1 -Bundle` 会自动把四段截成三段并说明。
- **签名**：本地打包不需要，但分发给用户前应当签名（`--signParams` / `--signTemplate`），macOS 还要公证；
  没有它们的包在别人机器上会被 SmartScreen / Gatekeeper 拦下。
- **每个平台各跑一次**：`vpk` 在哪个平台上就跑出那个平台的产物（与 AOT 一样不能跨平台）。
- **自动更新是应用侧的事**：需要在 `Main` 的第一行调用 `VelopackApp.Build().Run()`，更新源可以是任何
  静态托管。本仓库目前**只做打包、还没接更新**——没接时 `vpk` 会警告"入口点没有 `VelopackApp.Run()`"，
  那是预期内的（见 Velopack 文档）。

## 构建与发布

```bash
dotnet build OrielWeb.slnx
dotnet test tests/OrielWeb.Tests
node --test tests/bridge/bridge.test.mjs        # 桥接脚本的三平台一致性

pwsh tools/publish.ps1 -Bundle                  # AOT 发布 + Velopack 打包
pwsh tools/wsl_publish.ps1 -Bundle              # 在 WSL 里发 Linux 产物（同上）

dotnet run -c Release --project benchmarks/OrielWeb.Benchmarks -- --filter '*'   # 性能基线（见下）
```

**性能基线**（`benchmarks/`，2026-10-23 实测 / Windows x64 / .NET 10 / `--job short`）：

| 场景 | 耗时 | 每次分配 |
|---|---|---|
| 一次 invoke 往返（1 条路由） | 1.24 μs | 3.8 KB |
| 同上，注册表里有 1000 条命令 | 1.19 μs | 3.81 KB |
| 内嵌资源查找（子路径 / 带 query / 未命中） | 94 / 61 / 59 ns | 192 / 192 / 176 B |
| 能力名单：前缀通配 / 精确 / 命中 deny | 25 / 13 / 1.8 ns | 0 |

**1000 条路由与 1 条的耗时相同**——这是"O(1) 分发"的实测证据，不是从代码推断的。

同一轮基准还抓到一处每条消息都在交的税：前缀通配匹配原先把 `pattern[..^1]` 当 string 切片，
每次分配 32 B；改成 `ReadOnlySpan` 后归零（0.4.7 的「性能」条目）。
3.8 KB/次是回执序列化的代价（`ArrayBufferWriter` + `Utf8JsonWriter` + `string`），属已知优化空间，
不是契约——数字会随实现改进下降。

基准项目**不在** `OrielWeb.slnx` 里：跑一次要几分钟，不该拖慢每次 `dotnet build` 与 `dotnet format`。
CI 用单独 job 跑（只在 `main` 与手动触发，PR 上不跑——共享 runner 的噪声会让"回归 20%"变成狼来了）。

`tools/` 下是验证与取证脚本，由 CI 调用；用法见各脚本头部的注释。

**`--selftest` 的实现住在 `tests/OrielWeb.Conformance`**（0.4.14 起）。它是**类库**，不是独立程序：
CI 的三条 verify 脚本各自 `publish` 一次，再用同一个二进制跑完 selftest / shell / smoke，
macOS 那条还要把它打成 `.app` 并 codesign——做成第二个 exe 会让每个平台多一次 AOT 发布与签名，
而收益只有"能单独启动"。所以 `ConformanceRun` 以库的形式被 demo 的转发入口调用，
示例工程从 9,000+ 行降到 744 行。自检需要的能力名单也随实现一起搬走：那是**测试夹具**，
不是示例应用的能力面，混在一起会让"这个应用需要什么权限"读不出来。

**XML 注释策略**：`src/` 开了 `TreatWarningsAsErrors`，但 `NoWarn` 里保留 `CS1591`——公开成员**允许**没有 `<summary>`。
这不是遗漏：本库的注释标准是"只写不显然的部分"（为什么这么做、边界在哪、踩过什么坑），
而不是把方法名翻译一遍。某个 API 能用做什么，看 [docs/API.md](docs/API.md) 而不是看签名。

**三平台的验证脚本命名与职责一一对应**——每个平台一个脚本、内部按阶段分节，CI 里各自只调用一行：

| 脚本 | 语言 | 阶段 |
|---|---|---|
| `verify-linux.sh` | bash | `selftest`（机器断言）+ `shell`（托盘/通知/Shell/自启）+ `smoke`（运行取证 + 截图） |
| `verify-windows.ps1` | PowerShell | `smoke`（进程存活）+ `pack`（Velopack 打包与产物断言）+ `selftest`（机器断言） |
| `verify-macos.sh` | bash | 自检 + 运行取证 + 截图（含签名与 `.app` 组装这两个 macOS 特有的前置） |

Linux 与 Windows 两条支持用 `--stages` / `-Stages` 只跑某一阶段；三条都支持控制是否先发布
（`--no-publish` / `-Publish`），并在失败时逐阶段给结论 + 写一条 GitHub 注解。
**macOS 那条没有阶段开关**——它是单一流程（签名 → 组装 `.app` → 自检 → 运行取证 → 截图），
拆开跑会缺前置，所以只提供 `--seconds` / `--rid` / `--out`。

其余脚本是**跨平台或专项**的，不带平台名：
`verify-package.ps1`（包内 targets 的消费链路）、`publish.ps1` / `wsl_publish.ps1`（发布与打包）、
`publish-vm-payload.ps1`（VM 部署验证用的自包含非 AOT 载荷）、
`make-macos-app.sh`（组装 macOS `.app`）、`fake-notification-service.py` / `fake-notify-send.py`（通知替身）、
`check-tauri-leaks.mjs`（迁移项目自查：`__TAURI_INTERNALS__` / `plugin:` 命令名 / `tauri://` 事件名，
见 [MIGRATION-FINDINGS](docs/MIGRATION-FINDINGS.md#8-迁移前自查check-tauri-leaks)）、
`verify-api-demo.mjs`（ApiDemo 的无人点击验证：连 WebView2 的 CDP 端口，把面板里的按钮逐个点一遍；
需要 Windows 桌面，用法见 [ApiDemo 的 README](samples/ApiDemo/README.md)）。

**前端包（`packages/api`）的测试与发布**：

```bash
cd packages/api
npm ci && npm test && npm run typecheck   # 单测 + JSDoc 类型
npm run build:types && npm run check:types # 重新生成 types/ 并过发布闸门
```

`check:types` 是发布专用的一关：包内 `typecheck` 用 `skipLibCheck: true`，**看不见
`types/*.d.ts` 自己的错**（声明文件引用了不存在的名字，本地全绿、消费者一装就红）。
它用 `skipLibCheck: false` 把声明重新过一遍 tsc，并按 `exports` 逐条核对文件真实存在；
挂在 `prepublishOnly` 上，任一步红都不会上传。

**npm 发布是手动的一步，不在 CI 里**：npm 要求发布者带 2FA，而 `npm login` 拿到的 token
**不带** bypass 2FA，直接 `npm publish` 必然 403。做法是建一个勾了 bypass 2FA 的
Granular Access Token，写进**临时** `.npmrc`（`//registry.npmjs.org/:_authToken=...`）后
`npm publish --userconfig=<那个文件>`，用完立即删除——不要写进仓库或用户级 `.npmrc`。

一条容易吃亏的规则：**tarball 是快照**。包页上的 README、LICENSE、`description` 都取自
上传那一刻的文件，发出去就改不了，只能发新版本。所以文档定稿要排在 `npm publish` 之前
（0.4.14 → 0.4.15 就是因为先发布、后改 README）。

## 平台运行要求

| 平台 | 要求 |
|---|---|
| Windows | WebView2 运行时（Win10+ 常已预装；未装时库会引导，或由 `OnWebView2RuntimeMissing` 自定义提示）。开发/发布需要 .NET 10 SDK 与 MSVC 工具链（AOT） |
| Linux | `libwebkit2gtk-4.1` **或** `4.0`（Ubuntu 22.04 / Debian 11 只有 4.0——宿主会自动回退，两者是同一套 C API）、`libgtk-3`；中文界面另需 CJK 字体（否则渲染成方框）。AOT 发布需要 `clang` 与 `zlib1g-dev` |
| macOS | 系统 ≥ 11；产物必须打成 `.app` 才能运行（WKWebView 需要 bundle identifier）。`tools/make-macos-app.sh` 会做这件事，`vpk pack` 再消费它 |

三平台的详细依赖、已知限制与排查入口见 [docs/API.md](docs/API.md#13-运行要求)。

## 许可

[MIT](LICENSE) © OrielWeb Contributors。
