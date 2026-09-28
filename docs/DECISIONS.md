# OrielWeb · 决策记录

> 2026-09-27 经四轮决策确认达成共识。状态：已共识。

## 决策清单

| # | 决策点 | 结论 | 备注 |
|---|--------|------|------|
| 1 | 产品定位 | 跨平台 webview **核心库**先行，发布 NuGet；CLI 脚手架/打包器等框架能力后续迭代 | Tauri 亦为"核心库 + 工具链"分离形态 |
| 2 | 目标平台 | 桌面三平台，系统 webview 路线：Windows=WebView2、macOS=WKWebView、Linux=WebKitGTK+GTK | 不捆绑浏览器内核 |
| 3 | 参考策略 | **Ryn**=架构蓝本（分层/源生成 IPC/自定义 scheme/AOT 属性）；**pywebview**=API 基本面与多引擎桥接经验；**IgniteView**=仅借鉴窗口特效、按 RID 加载原生库等细节 | IgniteView 的反射 IPC、本地 HTTP 托管、Linux QtWebEngine 路线**不采用** |
| 4 | 里程碑 | 第一个里程碑 = **Windows Native AOT demo 跑通**（窗口+IPC+内嵌资源+AOT 发布）；macOS/Linux 随后 | |
| 5 | 技术底座 | 仅 **net10.0**；**纯 C# P/Invoke**——无 C++ 组件（不用 saucer）、无 GUI 框架依赖；Windows = Win32 + WebView2 COM 互操作 | 与 Ryn 的 saucer C++ 路线有意区分 |
| 6 | MVP 范围 | 最小核心（窗口、URL/内嵌资源、自定义 scheme/虚拟主机、双向 IPC、事件）+ **对齐 pywebview 基本面**（全屏/置顶/尺寸位置/无边框、loaded/closing 等事件、evaluate_js、文件与消息对话框、debug） | |
| 7 | IPC 模型 | `[OrielCommand]` 特性 + **Roslyn 源生成器**编译期生成分发代码；System.Text.Json 源生成序列化；**运行期零反射** | AOT 硬性要求；Ryn 已验证此路线 |
| 8 | API 形态 | **App 生命周期构建器风格**（`OrielApp.CreateBuilder(...)...Run()`），多窗口一等公民，窗口类内部可独立使用 | 对齐 Ryn / Tauri 体验 |
| 9 | 包结构与许可 | **单包先行**（OrielWeb + OrielWeb.Generators）；**MIT** | 后续视需要拆平台包 |
| 10 | 命名 | **OrielWeb**（Oriel=凸窗，用户定名） | NuGet 0 同名包（已核实）；GitHub 有跨生态同名（低代码构建器等），不冲突 |

## 事实依据（三参考项目分析结论，2026-09-27）

- **Ryn 0.38.0**（MIT，.NET 10）：与本项目定位几乎相同的现有实现——saucer C++ 底座 + ClangSharp 绑定 + 源生成 IPC（`[RynCommand]`，零反射）+ `ryn://` scheme + 能力安全模型，有专门 AOT CI。缺陷：Alpha、单人维护、与 saucer 强耦合、原生二进制需预编译分发。→ 作为架构蓝本，但底座换纯 C#。
- **IgniteView 2.2.9**（MIT，.NET 9）：IPC 为运行时反射 + Newtonsoft，**Native AOT 下不可用**；内容靠本地 HTTP 端口；Linux 实际是 QtWebEngine（捆绑 Chromium）。→ 反例 + 窗口特效参考。
- **pywebview 6.2.1**（BSD-3，Python）：最成熟的系统 webview 轻封装；按引擎适配桥接差异、就绪事件、HTTP 兜底的工程经验。→ API 基本面与桥接经验来源。

## 关键技术取舍（由以上决策派生，属实现细节）

- **COM 回调参数的引用规则（M1 最深的一次坑）**：完成回调/事件回调的参数（如 `createdEnvironment`、`createdController`）只在回调调用期间有效，WebView2 在回调返回后会 Release。若把裸指针存过回调生命周期而不 AddRef，环境/控制器随即析构、浏览器进程树在启动约 1 秒后全部退出，此后任何 COM 调用（如窗口 resize/maximize 触发的 put_Bounds）都会 AV。修复：回调内对需要长期持有的对象调用 `WebView2Native.AddRefComObject`（vtable[1]），不再 Release，随进程生命周期存活。
- **thunk 异常纪律**：所有 `[UnmanagedCallersOnly]` thunk 全身 try/catch，托管异常一律转成 HRESULT 返回，绝不外泄（外泄 = 进程 fail-fast）。曾因状态对象类型不匹配（WebMessage thunk 收到的是 host 而非 handler）在消息到达时 InvalidCastException 直接闪退。
- **桥接 JS 必须监听回执**：`chrome.webview.postMessage` 只发不收会让所有 Promise 永远挂起；桥接初始化时须 `chrome.webview.addEventListener('message', …)` 分发 `{__oriel:'result'}` 回执。
- **macOS 平台层（M2）的四个关键决策**：
  1. 不用 ObjC block：完成回调（evaluateJavaScript 的 completionHandler 等）全部改走"页面回环消息"——宿主 evaluateJavaScript 注入 `_evalScriptDone(id, json)` → 页面 postMessage 回传 → 消息处理器完成 TCS。绕开手工构建 block ABI 的复杂度与风险。
  2. 不做 struct 返回的 msgSend：NSRect/NSPoint 返回值（frame、screen frame）不取——窗口几何由 JS 侧提供（window.screenX 等）+ 宿主跟踪拖动增量，setFrameOrigin 等仅以参数方向发送。
  3. NSPoint/NSSize/NSRect 参数以**分离 double** 声明（如 `SendVoidDouble2(self, sel, x, y)`）：macOS x64 SysV 与 arm64 的整数/浮点参数分属不同寄存器组（rdi/rsi/xmm0… 或 x0/x1/d0…），与 objc_msgSend(self, _cmd, NSPoint) 的槽位天然对齐。
  4. 拖动用**流式坐标模型**（win.dragStart 记录指针起点与窗口 cocoa 原点 → win.dragTo 应用增量），Windows 继续用原生模态拖动（win.drag），JS 侧按 `window.oriel.platform` 分支。
- **内嵌资源解压的名称映射**：EmbeddedResource 资源名中的 '.' 既是目录分隔也是扩展名点。按"最后一个 '.' 为扩展名"解析（目录部分 '.'→分隔符），否则 `app.js` 会被写成目录 `app/js`，虚拟主机 404。
- COM 互操作**双向均手工实现，不使用 ComWrappers**（M1 实现过程中实测本机 .NET 10.0.401 运行时存在三个致命问题，参考 [smourier/WebView2Aot](https://github.com/smourier/WebView2Aot) 的手工路线后彻底绕开）：
  1. CCW 方向：`StrategyBasedComWrappers.GetOrCreateComInterfaceForObject` 首次调用后一律返回空指针；
  2. WebView2 加载器查询 `IReferenceTrackerTarget` 时，`ComWrappers.ManagedObjectWrapper.AsRuntimeDefined` 抛 NRE 崩溃进程；
  3. RCW 方向：`ConvertToManaged` 在 Native AOT 下触发 Access Violation。
  - **原生 → 托管**（回调）：`Win32NativeCallbacks.cs` 手工构建 vtable（IUnknown + Invoke）+ `[UnmanagedCallersOnly]` thunk + 手动引用计数（GCHandle 保活托管状态）。
  - **托管 → 原生**：`WebView2Com.cs` 指针包装结构按槽位直接调用（槽位序从官方 `WebView2.h` 提取核对；注意 Controller 在 remove_LostFocus 之后还有 AcceleratorKeyPressed×2、ParentWindow×2 四个方法，ICoreWebView2 共 58 个方法，_3.SetVirtualHostNameToFolderMapping 位于槽 71）。
  - 实现 [GeneratedComInterface] 的类若嵌套或使用主构造函数，CCW 生成会静默失败——已不再使用该机制。
- COM 互操作用 **`[GeneratedComInterface]`/`[GeneratedComClass]`** 的方案已废弃（见上）；保留的经验：此类接口/类必须 partial，LibraryImport 的 bool 返回值需显式 `[MarshalAs(UnmanagedType.Bool)]`。
- 内容托管 MVP 用 **WebView2 虚拟主机映射**（`SetVirtualHostNameToFolderMapping`，槽 71）+ 内嵌资源启动时解压到用户目录；内存直供的自定义 scheme 为后续优化。外部 dev server URL（Vite 等）直接 `Navigate` 支持。
- 命令参数/返回值：基元类型由生成器逐字段提取/写入（`Utf8JsonReader/Writer`）；DTO 类型经构建器注册 `JsonSerializerContext`（`.UseJsonContext(...)`），规避"生成器无法链式生成 STJ 上下文"的限制。
- WebView2Loader.dll 经 `Microsoft.Web.WebView2` 包仅取其原生位（托管程序集不引用，AOT 裁剪自动丢弃）。
- Windows 入口要求 `[STAThread]`（WebView2 COM STA），运行时检测并给出明确报错。
- 分析器（源生成器）不会随 ProjectReference 传递：消费项目需以 `OutputItemType="Analyzer" ReferenceOutputAssembly="false"` 直接引用 OrielWeb.Generators；发布 NuGet 包后此问题自动消失。

## 2026-09-28 补充与更正（对应 docs/OrielWeb-改进计划.md 的执行）

- **手工 vtable 互操作层定位为"临时绕行"，不是长期方案**：`WebView2Com.cs` 的槽位常量与 `Win32NativeCallbacks.cs`
  的手工 CCW 是为绕开 .NET 10.0.401 运行时三个缺陷（见上文）而采取的权宜手段。
  **回归条件**：待运行时缺陷修复后，重新评估 `[GeneratedComInterface]` / `[GeneratedComClass]` 路线
  （决策门 E-0 见 `docs/OrielWeb-WebView2绑定迁移计划.md`）。在此之前以 `verify-webview2-slots.py`
  作为 CI 门禁——槽位无法由编译器校验，数错一格即是 AV 或静默调用错函数。
- **thunk 异常纪律已覆盖三平台全部入口**：上文"所有 `[UnmanagedCallersOnly]` thunk 全身 try/catch"此前
  并未真正落实——macOS 侧 6 个 trampoline（`windowShouldClose:`、`windowWillClose:`、
  `webView:didFinishNavigation:`、`webView:didFailNavigation:withError:`、
  `webView:didFailProvisionalNavigation:withError:`、主线程泵）、Linux 的 `PumpIdleTrampoline`、
  Windows 的 `WindowProc` 与 `MessageWindowProc` 都缺少兜底。现已全部补齐：捕获后降级为默认语义
  （Win32 返回 `DefWindowProcW`；macOS `windowShouldClose:` 返回 1 = 允许关闭）并写 `Debug` 日志。
- **桥接 JS 已合并为单一模板**：三平台共用 `src/OrielWeb/Bridge/oriel-bridge.js`（EmbeddedResource），
  运行时替换 `__ORIEL_PLATFORM__` 与 `__ORIEL_POST__` 两个占位符。历史上三份手抄脚本曾导致缺陷同步传播。
- **命令线程模型（此前完全未文档化）**：命令实例按类型惰性单例共享，**命令方法必须线程安全**；
  执行发生在后台线程，回执切回 UI 线程投递；命令内操作 UI 需经 `OrielApp.PostToMainThread`。
- **回执与页内脚本的字符串编码统一走 `JsonText.EncodeString`**：手写 `Replace` 转义会漏掉
  `\t \b \f` 与全部 U+0000–U+001F 控制字符；而 `JsonSerializer.Serialize` 的反射重载在 AOT/裁剪下
  会触发 IL2026/IL3050（`src/` 已启用 `TreatWarningsAsErrors`，必须消除）。
- **E-0 决策门推翻上文"不使用 ComWrappers"的立论依据（2026-09-28）**：
  按 `docs/OrielWeb-WebView2绑定迁移计划.md` §3，在本机 Windows（.NET SDK 10.0.401）实测
  `smourier/WebView2Aot` 官方样例并做 Native AOT 发布，结果为——
  **AOT 发布成功、零 IL2xxx/IL3xxx 警告、单文件 3.90 MB（含内嵌 WebView2Loader）、
  运行时正常弹窗并加载页面**（窗口标题 `Hello X64 AOT - WebView2 V153.0.4234.48`，
  说明环境创建、控制器创建、页面加载与回调回传全部走通），上文三个症状**均未复现**。
  更关键的是其 CCW 实现：**回调类全部标注 `[GeneratedComClass]`，且全仓不存在任何
  `ComWrappers.RegisterForMarshalling` / `StrategyBasedComWrappers` 的使用**——
  即 `[GeneratedComInterface]` 路线的 CCW 方向在 .NET 10 AOT 下无须显式注册 marshaller 即可工作。
  因此上文三个症状应归因于当初的**用法/配置问题**而非运行时缺陷，
  "手工 vtable + 手工 CCW 是唯一可行路线"的前提不再成立。
  后续按迁移计划 §4 选定路线（A / B）推进；迁移期间 legacy 路径保留，
  可用环境变量 `ORIEL_WIN_BACKEND=legacy` 一键回退。
