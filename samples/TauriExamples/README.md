# Tauri 官方示例移植

`tauri/examples` 里五个示例的逐项移植。它们的作用不是"再演示一遍 API"，而是回答一个具体问题：

> 一个真实的 Tauri 应用搬过来，**页面代码到底要改几行**？

结论写在每个目录的 README 里，代码本身保持最小：五个工程互不引用，每个都能单独跑。

## 对照表

| 官方示例 | 本目录 | 覆盖的能力 | 页面侧改动 |
|---|---|---|---|
| [`examples/commands`](https://github.com/tauri-apps/tauri/tree/dev/examples/commands) | [`Commands/`](Commands/README.md) | 命令注册、异步、错误、DTO 参数、rename | 改 import；DTO 参数多包一层 |
| [`examples/state`](https://github.com/tauri-apps/tauri/tree/dev/examples/state) | [`State/`](State/README.md) | `manage` / `State<T>` → 共享实例 | **零改动** |
| [`examples/multiwindow`](https://github.com/tauri-apps/tauri/tree/dev/examples/multiwindow) | [`MultiWindow/`](MultiWindow/README.md) | 建窗、`emitTo`、`listen` | 改 import；建窗结果改走 `created` |
| [`examples/splashscreen`](https://github.com/tauri-apps/tauri/tree/dev/examples/splashscreen) | [`Splashscreen/`](Splashscreen/README.md) | 双窗口、隐藏主窗、按 label 切换 | 改 import；`invoke` 名字不变 |
| [`examples/drag`](https://github.com/tauri-apps/tauri/tree/dev/examples/drag) | [`Drag/`](Drag/README.md) | 无边框拖动区域 | 属性改名；**拖动代码整段删除** |

原文版权：Apache-2.0 / MIT（Tauri Programme within The Commons Conservancy）。移植只保留验证迁移结论所需的部分，不逐行照抄。

## 四件在所有示例里都一样的事

1. **package 一一对应**：`@tauri-apps/api/core` → `@orielweb/api/core`，`window.__TAURI__.core.invoke`
   → `window.oriel.invoke`。示例没有打包器，所以用相对路径直接指向内嵌的包源码。
2. **`tauri.conf.json` 变成 `Program.cs` 的 builder 链**：窗口、标题、尺寸、可见性都在
   `AddWindow` 里；没有配置文件，也就没有"配置与代码谁优先"。
3. **`#[tauri::command]` 变成 `[OrielCommand("name")]`**：命令名写在特性里，
   与 Rust 侧 `rename` 后的名字逐字一致。
4. **能力要显式声明**：Release 构建下未 `Allow` 的命令一律拒绝（fail-closed）。
   这五个示例都写全了，所以 Debug / Release 行为一致。

## 跑起来

```bash
dotnet run --project samples/TauriExamples/Commands/TauriCommands.csproj
dotnet run --project samples/TauriExamples/State/TauriState.csproj
dotnet run --project samples/TauriExamples/MultiWindow/TauriMultiWindow.csproj
dotnet run --project samples/TauriExamples/Splashscreen/TauriSplashscreen.csproj
dotnet run --project samples/TauriExamples/Drag/TauriDrag.csproj
```

五个工程共享 [`Sample.props`](Sample.props)：项目引用、`wwwroot` 内嵌、前端包源码内嵌都在那里写一次。
每个 `.csproj` 里只剩自己的命名空间与一句"这是哪个示例的移植"。

## 为什么是这五个

它们正好覆盖"单窗口工具类应用"最常用的五条路径，而且每一条都能在本库找到**同名同义**的对应物。
其余官方示例不搬，原因分三类：**机制不存在**、**职责不在运行时**、**结论已是默认形态**。

| 官方示例 | 它实际演示什么 | 不搬的原因 |
|---|---|---|
| `api` | Tauri 自己的"主验证应用"：把 `@tauri-apps/api` 的每项能力（窗口、菜单、托盘、对话框、通知、剪贴板、fs、事件、建 webview……）各做一遍，官方拿它当 testbed。 | 它按 **Tauri 的 API 名录**组织，不按迁移路径组织。1:1 搬过来会有一半卡片落在本库没有的插件上（18 个桌面插件缺失），示例变成"功能缺失清单"；而同一批**已有**能力，`samples/OrielDemo` 已经按本库自己的方式覆盖，还带手动验证台。查单个 API 看 `docs/API.md` 更直接。 |
| `helloworld` | 最小的"一个窗口 + 一条 greet 命令"。 | 与 [`samples/OrielMinimal`](../OrielMinimal) 重复——那个更小（整个 csproj 没有一行 `EmbeddedResource`），且验证的是**从 NuGet 包消费**这条真实路径。 |
| `isolation` | Rust 侧只有一条 `ping` 命令。全部内容在配置与另一个前端包：`app.security.pattern = { use: "isolation" }` 把页面 IPC 先送进沙箱 iframe，由隔离脚本校验后再转发。 | **机制不存在**：本库没有隔离模式，也没有 ACL / CSP 配置层；安全模型是"来源 + 令牌 + 命令 Allow/Deny"（[API.md](../../docs/API.md) §4）。没有那个机制，搬过来只剩一条 `ping` 命令的普通示例，展示不了任何 isolation 语义——这比不搬更误导。 |
| `multiwebview` | 一个 OS 窗口里塞 4 个子 webview，按逻辑坐标摆成四宫格，各自加载不同 URL（含外部 https），`auto_resize` 跟随窗口。 | **模型不存在**，不是缺函数：本库"窗口即 webview"，一个窗口只有一个 webview 且铺满客户区；没有 `add_child`、没有父子布局、没有 `auto_resize`。`webview` 模块已记为"继续不做"（[TAURI2-GAP](../../docs/TAURI2-GAP-2026-10-25.md) §2.4）。要并排显示多个页面：开多个窗口（见 `MultiWindow/`），或在页面里用 iframe（跨域页面拿不到桥接）。 |
| `resources` | `bundle.resources` 把 `assets/index.js` 打进安装包的**资源目录**，运行时用 `app.path().resolve(…, BaseDirectory::Resource)` 拿到磁盘路径再读。 | **语义不存在**：本库没有 bundle 配置、没有 `BaseDirectory::Resource`。前端资源是编译进程序集的（`wwwroot` → 内嵌资源，不落盘）；其余文件由应用自己放进 app data 目录再用 `fs` 读。硬搬只能把 `BaseDirectory::Resource` 换成一个随便的路径，示例会"看起来演示了 bundling"，实际什么都没验证。 |
| `file-associations` | 两半：① 打包侧用 `bundle.fileAssociations` 在注册表 / Info.plist 登记扩展名与 `exportedType`；② 运行侧从 `std::env::args()` 或 macOS 的 `RunEvent::Opened` 拿路径，开窗并用 **initialization script** 注入 `window.openedFiles`。 | **职责不在运行时**：① 是打包器的事，本库把打包交给 Velopack，自己没有 bundle 配置；② 本库有零件（`args`、`SingleInstance(onActivate)` 能拿第二实例参数），但没有 initialization script，页面得靠命令/事件去取。两半手工搭起来，得到的不是"移植示例"，而是"用 Velopack + 自写注册表做文件关联"的另一篇教程。 |
| `run-return` | `Builder::build()` → `run_return(...)` → 事件循环结束后继续执行 `println!("I run after exit")` → `process::exit(code)`。 | **结论已是本库的默认形态**：`Build()` 建应用、`Run()` 阻塞到消息循环结束、之后的代码照常执行，`Quit()` 还带看门狗保证进程退得掉。搬过来只会得到一个空示例："`Run()` 之后还能跑代码"。唯一对不上的细节是 `run_return` 返回退出码、本库 `Run()` 是 `void`（退出码走 `Environment.ExitCode`）——这属于 API 文档，不值得一个工程。 |
| `streaming` | 注册异步自定义 URI scheme `stream`，对 mp4 实现 HTTP Range（206 / `Content-Range` / multipart byteranges），页面用 `convertFileSrc(path, 'stream')` 把 `<video>` 指过去。 | **能力边界**：本库没有注册自定义 scheme 的 API，且内嵌资源明确"每请求整份驻留内存、不支持 Range"（见根 README「内嵌页面资源」的已知边界）。`<video>` 一定要 Range/seek，两条缺任一条都播不动。要播大媒体：自己起本地 HTTP server 并放行来源（根 README「默认首页与开发期」的写法），不要走内嵌资源。 |

> `examples/streaming` 这个名字容易让人以为是 `Channel` 的示例——那是 Tauri 文档里的另一节。
> 本库的 `Channel` 用法见 [docs/API.md](../../docs/API.md) §11.3。

## 移植原则

- **命令名、事件名不改**：页面代码才有资格说"只改了 import"。
- **不引入构建工具**：页面保持"一个 HTML + 一个模块脚本"的形态，避免把打包器配置混进结论。
- **差异写在代码旁**：每处形状差异都在调用点有一行注释，并汇总在各自的 README 里；
  不往"已知问题"里一塞了事——静默差异正是迁移中最贵的那类问题
  （见 [docs/MIGRATION-FINDINGS.md](../../docs/MIGRATION-FINDINGS.md) §3 的 #1 / #2）。
