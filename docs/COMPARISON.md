# OrielWeb 与三个参考实现的对照

- **日期**：2026-10-01
- **目的**：为 `docs/ROADMAP.md` 选型——**哪些能力值得拿、哪些路径不要走**
- **口径**：只写调研到的事实；某项确实没有就写"未实现"，不臆测。OrielWeb 侧以本仓库代码与文档为准，三个参考实现以各自源码与文档为准。
- **覆盖范围**：`参考项目/` 下的三个——**AOTrino**、**Ryn 0.38.0**、**pywebview 6.2.1**。

> ROADMAP 的「定位与参照」一节还提到 **Tauri 2**，但它不在本仓库，本文不展开，
> 只在相关处引用它已被 ROADMAP 记录的做法（`capabilities/*.json`、插件化）。

---

## 1. 四者一览

| | **OrielWeb** | **AOTrino** | **Ryn 0.38.0** | **pywebview 6.2.1** |
|---|---|---|---|---|
| 语言 / 运行时 | C# / .NET 10 | C# / .NET 10 | C# / .NET 10 | Python ≥ 3.8 |
| 平台 | Win / Linux / macOS | **仅 Windows** | Win / Linux / macOS | Win / Linux / macOS / Android / OpenBSD |
| 渲染引擎 | WebView2 / WebKitGTK 4.1 / WKWebView（每平台一种） | 仅 WebView2 | 同左（经 saucer） | **每平台 2–3 种可选**（Windows: WebView2/MSHTML/CEF；macOS: Cocoa/Qt；Linux: GTK/Qt） |
| C++ 中间层 | **无** | 无 | **有**（saucer，C ABI） | 无 |
| 原生绑定来源 | 自研 P/Invoke + WebView2 源生成绑定 | 第三方 DirectN 系（`DirectNAot` / `WebView2Aot`） | ClangSharp 生成 saucer 绑定 | pythonnet / pyobjc / PyGObject / QtPy |
| 原生发布 | Native AOT 单文件 | Native AOT 单文件（~11 MB，UPX 后 ~4 MB） | Native AOT-first | 不适用（PyInstaller / py2app） |
| **IPC 机制** | Roslyn 源生成 switch 路由，**零反射** | **反射 + COM `IDispatch`** | Roslyn 源生成 switch 路由，**零反射** | 注入 JS + `evaluate_js` |
| **安全模型** | 能力模型已落地（阶段 D，2026-10-01）：deny-by-default + 每启动 token + 来源校验 + 远程页面不装桥接；另有 Shell 的 scheme 白名单 | 近乎没有（一个 `NavigationMode` 开关） | **capabilities deny-by-default，最完整** | 会话 token 防 CSRF |
| CLI / 模板 / 打包 | **`oriel` CLI（`doctor` / `bundle`：.msi / .dmg / .AppImage）+ 包内 MSBuild targets**（阶段 D，2026-10-01）；`dotnet new` 模板与 updater 仍无 | `dotnet new` 模板 + NuGet + 包内 MSBuild targets | **`ryn` CLI（new / dev / build / bundle / doctor）** | PyInstaller / py2app / buildozer hook |
| 自动更新 | 无 | 无 | **有（强制 ECDSA P-256 验签 + 防降级）** | 无 |
| 测试 | 298 单测 + 41 桥接 + 三平台 CI smoke | **零测试**（靠 15 个示例 + 手工 checklist） | ~566 个用例 / 10 个测试项目 + CodeQL + benchmark | 43 个 pytest 文件 |
| 文档形态 | README（就地标验证状态）+ ROADMAP（待真机清单）+ DECISIONS（取舍记录） | 6 篇 + 详尽"陷阱清单" | 16 篇 docs + SECURITY.md + ROADMAP + 迁移说明 | VuePress 10 篇 guide + API 文档 + 61 示例 |

---

## 2. 功能面对照

图例：✅ 有 ｜ ⚠️ 部分 / 有已知限制 ｜ ❌ 未实现 ｜ — 不适用

| 能力 | OrielWeb | AOTrino | Ryn | pywebview |
|---|---|---|---|---|
| 无边框窗口 | ✅ 页面自绘标题栏 | ✅ 页面自绘（透明 composition） | ✅ `TitleBarStyle` | ✅ |
| 边缘 resize | ✅ **系统原生**（Composition 宿主，窗口能收 `WM_NCHITTEST`） | ✅ 同类做法 | ⚠️ 靠 `StartResize` 注入脚本 | ✅ 后端相关 |
| 窗口拖动 | ✅ 三平台两套路径（X11 自摆 / Wayland 交合成器 / macOS 流式） | ✅ 发 `WM_NCLBUTTONDOWN` | ✅ `data-webview-drag` + `StartDrag` | ⚠️ `easy_drag` 仅 edgechromium |
| 最大化 / 全屏 / 置顶 / 居中 | ✅ | ⚠️ 全屏仅属性、置顶仅视觉层 | ✅ 另有 `SetBackdrop`(mica/acrylic) | ✅ |
| 多窗口 | ✅ `AddWindow` 可多次 | ⚠️ 以单窗口为主 | ✅ `IRynWindowManager` | ✅ |
| 导航（前进/后退/刷新 + 可用性） | ✅ 三平台原生历史 | ⚠️ 未提供专用 API | ✅ | ✅ |
| 导航事件（含失败原因） | ✅ 三平台（Linux 新增 `load-failed`） | ⚠️ `page-error` 消息 | ✅ 有源生成的回调 | ⚠️ 部分 |
| 页面 → 宿主：命令（有回执） | ✅ `oriel.invoke` | ✅ host objects（COM） | ✅ `__ryn.invoke` → XHR `ryn://` | ✅ `pywebview.api.*` |
| 页面 → 宿主：单向消息 | ✅ `oriel.postMessage` | ✅ | ✅ | ✅ |
| 宿主 → 页面：事件 | ✅ `EmitEvent` / `oriel.on` | ✅ `ExecuteScript` / `PostWebMessageAsJson` | ✅ `_emit`（base64 eval） | ✅ `evaluate_js` |
| console 转发 | ✅ 默认关，可开 | ❌ | ❌ | ❌ |
| 应用菜单栏 | ❌ **刻意移除** | ❌ | ✅ MenuBar 插件（Linux 不支持） | ✅（GTK 无窗口级） |
| 系统托盘 | ✅ | ❌ | ✅（Linux 用 D-Bus `StatusNotifierItem`） | ❌ |
| 托盘 / 窗口菜单 | ✅ 共用一套构建与 role 解释 | ❌ | ✅ 托盘有；**右键菜单未实现** | ❌ |
| 内建右键菜单策略 | ✅ `Editing`/`Native`/`Disabled`，macOS/Linux 接管式 | ⚠️ 默认禁用，无策略 API | ❌ | ⚠️ 默认禁用，debug 时显示 |
| 系统通知 | ✅ | ❌ | ✅ | ❌ |
| 通知点击上报 | ❌ 三平台都是显式空实现 | ❌ | ⚠️ Linux 未端到端验证 | ❌ |
| 对话框（消息/打开/保存/选目录） | ✅ 含多选、结构化过滤器 | ❌ **框架不提供**，留给应用 | ✅ 另有 `*Secure` + 文件访问授权 | ✅ |
| 文件拖入 | ✅ 真实路径 | ✅ OLE `IDropTarget` | ⚠️ **仅文件名，不给原生路径** | ✅ |
| 文件拖出 | ❌ | ✅（示例） | ❌ | ❌ |
| 剪贴板（文本） | ✅ | ❌（靠页面 `navigator.clipboard`） | ✅ | ❌（GTK 交给页面） |
| 剪贴板（HTML / CF_HTML） | ✅ 含纯文本回退 | ❌ | ⚠️ 有图像，未提 HTML | ❌ |
| 系统主题（含变更事件） | ✅ 三平台检测 + 推给页面 | ⚠️ 靠引擎的 `prefers-color-scheme` | ✅ `SystemThemeDetector` | ⚠️ 仅 Windows 标题栏 / macOS 暗色 |
| 单实例 | ✅ 独占文件锁 + 命名管道 | ❌ | ⚠️ 仅作为深链协调存在 | ❌ |
| 开机自启 | ✅ 三平台 | ❌ | ❌（ROADMAP 标 Planned） | ❌ |
| Shell：打开外链 | ✅ 默认拒绝式白名单 | ✅ `OpenExternal(Uri)` | ✅ `shell.open`（白名单） | ✅ |
| Shell：在文件管理器显示 | ✅ 三平台 | ❌ | ❌（未单独实现） | ❌ |
| 执行命令 / PTY | ❌ **刻意不做** | ❌ | ✅ `shell.execute` + 原生 PTY shim | ❌ |
| 深链 | ❌ **刻意移除** | ❌ | ✅ | ❌ |
| DevTools | ⚠️ 开关已实现、**未真机验证** | ✅ F12 | ✅ | ✅ |
| 内嵌资源虚拟主机 | ✅ 三平台统一语义 | ⚠️ `file://` 或 `VirtualHostName` 二选一 | ✅ 自定义 scheme | ✅ 内置 HTTP 服务器 |
| 能力 / 权限系统 | ❌ **排在 ROADMAP 之后** | ❌ | ✅ **最完整** | ❌ |
| 自动更新 | ❌ | ❌ | ✅ | ❌ |
| CLI / `dotnet new` 模板 | ❌ | ✅ 模板 + 包内 MSBuild targets | ✅ CLI 四件套 | ❌（有打包 hook） |
| 安装器 / 分发包 | ⚠️ 三平台 zip（GitHub Release） | ⚠️ 单 exe + NuGet | ✅ `.app`+dmg / WiX wxs / AppImage | ⚠️ 交给 PyInstaller 等 |

---

## 3. 实现方式的六个关键分岔

这六处是四者真正分开的地方。每处都写清"四种做法"与"各自付了什么代价"。

### 3.1 有没有 C++ 中间层 —— 一致性的代价 vs 控制力的代价

| | 做法 | 代价 |
|---|---|---|
| **OrielWeb** | 无。三平台各写一份手写 P/Invoke（约 1.0 万行后端代码） | 代码量大、三平台要各验一遍；换来的是**完全控制**，不被中间层的能力边界卡住 |
| **AOTrino** | 无。但重度依赖第三方绑定包 `DirectNAot` / `WebView2Aot` | 绑定质量与更新节奏受制于人；换来的是核心库只有 ~4.7k 行 |
| **Ryn** | **有**：saucer（C ABI）+ ClangSharp 生成绑定 | 三平台一致性最好、代码量最小；代价是**被 saucer 的能力边界锁死**——自定义 scheme 响应非零拷贝（要 materialize 到 saucer 的连续 stash）、macOS 多窗口首绘可能只画背景 |
| **pywebview** | 无。靠 pythonnet / pyobjc / PyGObject 等**反射式**绑定 | 上手最快；代价是**每个后端能力不一致**，且差异只能靠运行时日志暴露 |

**对 OrielWeb**：这条路已经选定且是对的（"无 C++ 中间层"是核心卖点）。Ryn 的取舍反过来印证了这一点——它换来的三平台一致性，代价恰好落在"自定义 scheme"这个 OrielWeb 也要面对的面上。

### 3.2 IPC 怎么做到零反射 —— 两条路，OrielWeb 与 Ryn 殊途同归

- **OrielWeb**：`[OrielCommand("name")]` + `IIncrementalGenerator` → 每个命令类生成一个 `IOrielCommandRouter`（`Route(name)` 返回全局索引 + `InvokeAsync` 的 `switch`），`[ModuleInitializer]` 在程序集加载时注册。分发器构造期建「命令名 → (路由, 索引)」索引，热路径 O(1) 无锁无分配。参数提取分两条路：编译期已知类型的**直出**（`element.GetInt32()`），其余走泛型 fallback。
- **Ryn**：同样是 `IIncrementalGenerator` + `ICommandRouter`（`CanRoute` 模式匹配 + `RouteAsync` switch）+ DI 扩展 `AddXxx`。参数用 `JsonDocument.GetProperty` 手工提取，不支持的类型直接报 `RYN001–005` 编译诊断。

> 两者的思路**高度一致**——这本身是个信号：源生成 + switch 是 .NET 上做零反射 IPC 的正解。
> 差异在**传输**：OrielWeb 走引擎自带的消息通道（WebView2 的 `postMessage` / WebKit 的
> `messageHandlers`）；Ryn 走自定义 scheme `ryn://` 的 XHR POST。后者的好处是能复用 HTTP 语义，
> 代价是引入了一次真实的网络栈往返。

- **AOTrino 是反面参照**：它用**反射 + COM `IDispatch`**（`DispatchType` 反射 public 成员、`ConcurrentDictionary` 缓存、成员名映射成 DISPID），靠 `[DynamicallyAccessedMembers]` 保住裁剪。这条路能用，但要在每个 host 类上打裁剪注解，且异步 `Task<T>` 得靠**手工 switch 白名单**解包——为了绕开反射而写的手工代码，正是源生成器要消灭的那类东西。
- **pywebview**：注入 `window.pywebview` + `evaluate_js`（`eval` 包裹、返回 JSON），JS→Python 按平台分派到 5 种不同通道（`window.external.call` / `chrome.webview.postMessage` / `webkit.messageHandlers` / `QWebChannel` / Android）。Python 侧是**反射调用**，且回调在**新线程**执行。

### 3.3 多平台/多后端怎么统一 —— 能力声明 vs 静默降级

这是四者差异最值得抄的一处。

| | 统一机制 | 缺能力时怎么办 |
|---|---|---|
| **OrielWeb** | 接口 + 三平台实现（`IPlatformBackend` / `IWindowBackend`） | **文档里如实标注 + 给排查入口**（README 的「验证账」+ ROADMAP 的「待真机验证清单」） |
| **Ryn** | 接口 + saucer + **capabilities 声明** | 文档列明"Linux 无菜单栏/徽章/全局快捷键"；能力按声明授权，**未声明就是拒绝** |
| **pywebview** | `platforms/*.py` 的**同名函数约定**（无接口、无声明） | `logger.warning` 后**静默返回**（如 Android 不支持文件对话框、GTK 无窗口级菜单） |
| **AOTrino** | 无此问题（单平台） | — |

**对 OrielWeb**：pywebview 的"同名函数约定 + 静默降级"是最该避免的形态——调用方在 macOS 上写好的代码，到 Linux 上只是少了一个 `warning`，行为静默不同。OrielWeb 现在选的"接口 + 文档如实标注"更好，但**还差 Ryn 那一层**：把"这个平台支持什么"变成**机器可读的声明**，而不是只写在 README 表格里。

### 3.4 安全模型做到哪一层 —— 当初 OrielWeb 最大的功能缺口（已补）

- **Ryn（最完整）**：`ryn.json` 的 capabilities **deny-by-default**；配置缺失时 Debug=allow-all、Release=fail-closed；按插件前缀授权、支持 allow/deny；`scope` 是**路径 glob + 符号链接规范化**；`scopedCommands` 用 **argv 模板 + regex**（不是拼字符串）；自定义 scheme 做路径遍历防护；Origin 校验 + **每启动一个 token** + 仅 loopback；**远程页面故意不接 IPC**。
- **pywebview**：只有一个会话级 `token` 防 CSRF，加几个全局开关（`ALLOW_FILE_URLS` / `ALLOW_DOWNLOADS` / `IGNORE_SSL_ERRORS`）。
- **AOTrino**：几乎没有——一个 `NavigationMode` 开关 + 三个重写点；host object **没有 origin 过滤**（作者自己在文档里标为限制：Web 窗口下注册即泄漏）。
- **OrielWeb（调研当时）**：**没有能力系统**。仅 Shell 集成有默认拒绝式的 scheme 白名单（这一处做得比 AOTrino 好），IPC 命令**谁都能调**。
  **（2026-10-01 更新：已落地。）** 阶段 D 的第 2 项按这份对照的结论实现，落脚点写在 `DECISIONS.md` 的
  「能力模型」一节：deny-by-default + allow/deny（deny 优先）+ 每启动 token + 来源校验，
  且来源校验放在**注入期**——不可信来源的文档里根本不安装桥接脚本（比"装上再拦"更结构性）。

**对 OrielWeb（调研当时）**：这是**最该抄的一节**。Ryn 的五个具体做法（deny-by-default、路径 glob 规范化、argv 模板、每启动 token + origin 校验、远程页面不接 IPC）都是可移植的，而且它与 OrielWeb 的技术前提一致（Native AOT、零反射）。
**（2026-10-01 更新）** 落地时只拿了其中三条，另外两条**刻意没拿**：
路径 glob 规范化与 argv 模板的判据是给"文件读写 / 执行命令"这类沙箱能力用的，而本库不提供这类能力面
（`shell.execute` / PTY 明确不在路线图内），能力面就是"应用自己注册的命令名"，标识符匹配足够；
多搬一个 glob 规范化只会多一处能写错的地方。模式因此只有精确名与 `todo.*` 这类前缀通配，**不做正则**。

### 3.5 桌面能力面：框架给还是应用自建

- **AOTrino 走"框架最小化"**：只给窗口、WebView、桥、拖放；对话框、剪贴板、托盘、菜单**全部留给应用自建 host object**。作者的理由是"避免框架膨胀"，代价是每个应用都要重写一遍。
- **OrielWeb 与 Ryn 走"框架给全"**：托盘、通知、菜单、对话框、剪贴板、主题、自启（仅 OrielWeb）、Shell 都在库里。
- **pywebview 居中**：对话框有，托盘/通知/单实例/自启**都没有**（示例里用外部 `pystray` + 多进程自己拼）。

**对 OrielWeb**：选"框架给全"是对的，而且已经兑现了大半。值得注意的是**Ryn 的插件化**（12 个 `Ryn.Plugins.*`，托盘/菜单/updater 都是插件）——它让核心保持小、能力可裁剪。OrielWeb 目前是**单体**：所有能力编进同一个程序集，消费方无法只取托盘不要 updater。这在 AOT 裁剪下不致命（未用到的代码会被裁掉），但**API 面的组织**值得参考。

### 3.6 工具链与分发的边界

| | 做到了哪一步 |
|---|---|
| **Ryn** | 最完整：`ryn new/dev/build/bundle/doctor`；macOS `.app` + 签名 + 公证 + dmg；Windows WiX `.wxs`；Linux AppDir/AppImage；**updater 强制 ECDSA 验签 + 防降级** |
| **AOTrino** | `dotnet new` 三个模板 + NuGet 包 + **包内 MSBuild targets**（自动嵌 `WebRoot\dist`、自动 `npm build`、默认 DPI manifest）+ npm 类型化客户端 |
| **pywebview** | 交给 PyInstaller / py2app / buildozer，提供 hook，不捆绑重型 GUI |
| **OrielWeb** | 只有 `tools/publish.ps1` / `wsl_publish.ps1` + GitHub Release 三平台 zip。**无 CLI、无模板、无安装器、无 updater**（ROADMAP 明确排在 A/B/C 之后） |

**对 OrielWeb**：**AOTrino 的"包内 MSBuild build logic"优先级最高**。原因很具体——OrielWeb 现在要求使用者在 csproj 里手写 `<EmbeddedResource>`，而这一行**有坑**（`LogicalName` 不写、文件名含 `.` 就会静默解压到错误路径，见 `docs/reviews/2026-10-01.md` §4.3）。把这行搬进包内的 targets，坑就不存在了。这一条比 CLI 更便宜、收益更直接。

---

## 4. 各自的取舍与已知限制（引各自文档）

**AOTrino**（README 自述）
- 仅 Windows；嵌套数组传值失效（WebView2 #3183），复杂数据必须传 JSON；`Task<T>` 自定义类型须重写 `GetTaskResult`；
- `file://` 下 ES module 被 CORS 挡，需 `VirtualHostName`；**host object 无 origin 过滤**；
- 无任何测试项目。

**Ryn**（README / ROADMAP / SECURITY 自述）
- alpha 阶段；macOS 多窗口首绘可能只画背景（WebKit/saucer 限制）；
- Linux 无菜单栏/徽章/全局快捷键，Wayland 下快捷键不支持，通知激活未端到端验证；
- Windows 菜单加速键仅显示、必须 `[STAThread]` 同步 `Main` 否则 WebView2 死锁；
- 自定义 scheme 响应**非零拷贝**；`ryn dev` 实为重启进程、**不是热重载**；
- 无 Blazor、无移动端、无 sidecar。

**pywebview**（文档自述）
- GUI 必须主线程（Cocoa 硬要求）；无 C++ 层，依赖反射绑定；
- `mshtml` 已弃用；`clear_cookies` 等在非 chromium 后端未实现；GTK 无窗口级菜单；
- Android 明确不支持文件对话框、多窗口、置顶、改标题等（代码里逐条 warning）；
- `file://` 明确不推荐。

**OrielWeb**（本仓库文档自述）
- 无能力/权限模型；无 CLI/模板/安装器/updater；无执行任意命令（刻意不做）；
- 应用菜单、全局快捷键、深链已刻意移除（提交 `49c8220`）；
- macOS x64 与 Linux arm64 仅有编译/冒烟验证，未真机运行；
- 一批能力（托盘图标可见性、通知展示、真实拖放、对话框外观、剪贴板跨进程、主题切换实时性、DevTools 面板）**整体未验证**，逐项列在 ROADMAP 的「待真机验证清单」。

---

## 5. 对 OrielWeb 的启示

### 5.1 值得拿的（按"性价比"排序）

| # | 拿什么 | 从哪拿 | 为什么 |
|---|---|---|---|
| 1 | **能力/权限模型** | Ryn | 唯一的功能级缺口。Ryn 的五个做法可直接移植，且与 OrielWeb 的技术前提（AOT、零反射）一致 |
| 2 | **包内 MSBuild build logic** | AOTrino | 把 `<EmbeddedResource>` + `LogicalName` 搬进 targets，直接消灭 §4.3 那个静默白屏的坑；比 CLI 便宜得多 |
| 3 | **`doctor` 子命令** | Ryn | OrielWeb 的「待真机验证清单」已经写得很细，把它变成一条可执行的命令，比让人对着文档手工点更靠谱 |
| 4 | **运行时注入的同步 API** | AOTrino | 解决一个真实问题：无边框拖动要在 `mousedown` 内**同步**判断双击，不能 await。OrielWeb 现在靠"移动阈值"绕过 |
| 5 | **打包链 + updater 验签** | Ryn | `.app`/dmg、WiX、AppImage 与"强制 ECDSA 验签 + 防降级"都是成品级做法 |
| 6 | **`dotnet new` 模板** | AOTrino | 上手成本；与 #2 天然配套 |
| 7 | **插件化的 API 组织** | Ryn | 核心小、能力可裁剪；AOT 下不致命但 API 面更清晰 |

### 5.2 明确不拿的（及理由）

| 不拿 | 理由 |
|---|---|
| **C++ 中间层（saucer 路线）** | 与"无 C++ 中间层"的核心卖点直接冲突。Ryn 自己付了代价（自定义 scheme 非零拷贝、多窗口首绘），这些代价 OrielWeb 现在并不需要付 |
| **反射式 IPC（AOTrino 的 `IDispatch`）** | 与"零反射"冲突；而且它为了绕开反射写的手工 `Task<T>` 解包，正是源生成器要消灭的东西 |
| **多渲染后端可选（pywebview 路线）** | 统一代价是"无能力声明、靠 `logger.warning` 静默降级"，与 OrielWeb「只写有证据的结论」的文化冲突 |
| **把桌面能力全推给应用（AOTrino 路线）** | OrielWeb 已选"框架给全"且兑现大半，退回会让每个应用重写一遍 |
| **`shell.execute` / PTY** | ROADMAP 已明确划为"能力沙箱的范畴，与定位无关" |
| **AOTrino 的"零测试"** | 反面参照 |

### 5.3 OrielWeb 已经领先或独有的

1. **无 C++ 中间层 + 自研 P/Invoke 三平台后端**——四者里只有它做到"全自研绑定 + 三平台真机跑通"。AOTrino 单平台且依赖第三方绑定；Ryn 有中间层；pywebview 是反射胶水。
2. **Composition 宿主让无边框窗口的边缘 resize 走系统原生路径**（`WS_EX_NOREDIRECTIONBITMAP` + `ICoreWebView2CompositionController`，窗口因此能收 `WM_NCHITTEST`）。AOTrino 同类，Ryn 靠注入脚本 `StartResize`。
3. **验证状态的如实标注粒度**。四者里只有 OrielWeb 把"哪项有单测、哪项只有编译、哪项必须人眼"逐项写在 README 的就地「验证账」里，并把未验证项汇成 ROADMAP 的待真机清单。Ryn 的 ROADMAP 也很清晰，但粒度没到这一层。
4. **通知点击上报在三个平台上都是显式空实现**——不是"某个平台看起来支持"。这种"让缺失在代码里可见"的做法，四者里独此一家。

### 5.4 两个最大的缺口（结论）

1. **安全模型**：OrielWeb 的 IPC 命令**谁都能调**，没有 origin 校验、没有 token、没有按命令授权。Ryn 的 capabilities 是可以直接照着做的样板。
2. **工具链**：没有 CLI/模板/包内 build logic，使用者要自己写 csproj——而那一行现在**有坑**。

> 与 ROADMAP 现有排序的关系：ROADMAP 把「安全与能力模型」和「工具链与打包」都排在 A/B/C 之后，
> 本对照支持这个顺序；但建议把 **#2（包内 MSBuild build logic）单独提前**——
> 它不是"新能力"，而是"消灭一个现有缺陷"，成本远低于另外几项。

**（2026-10-01 更新：上面的第 1 条与第 2 条的前半已完成。）**
- #1 安全模型 → 阶段 D 第 2 项，已落地并在 Windows / WSL2 + WSLg 真机上跑过端到端（见 `DECISIONS.md`）。
- #2 包内 MSBuild build logic → 阶段 D 第 1 项，已落地（`tools/verify-pack.ps1` 已接进 CI）。
- 仍未做的：**`dotnet new` 模板**与 **updater**（ROADMAP 阶段 D 第 4 项剩下的两块）。
- 阶段 D 的第 3 项（**运行时注入的同步 API**）也已落地——做成了注入快照而不是同步 RPC，
  理由与实测见 `DECISIONS.md`。

---

## 6. 附：调研口径

- 三个参考实现各由一次独立调研覆盖（源码 + README + docs + 目录结构），
  路径分别为 `参考项目/AOTrino/`、`参考项目/Ryn-0.38.0/`、`参考项目/pywebview-6.2.1/`。
- **未做的事**：未编译/运行这三个项目；未逐行审阅它们的实现；它们的代码量、测试数等取自目录统计与各自文档，
  **未做独立复核**（与本文对 OrielWeb 的陈述不同——后者以本仓库实测为准）。
- 表中"未实现"表示在该项目的源码/文档中未找到对应能力，不排除存在于示例或第三方扩展里。
