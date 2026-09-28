# OrielWeb · 决策记录

> 2026-09-27 经四轮决策确认达成共识。状态：已共识。

## 决策清单

| # | 决策点 | 结论 | 备注 |
|---|--------|------|------|
| 1 | 产品定位 | 跨平台 webview **核心库**先行，发布 NuGet；CLI 脚手架/打包器等框架能力后续迭代 | Tauri 亦为"核心库 + 工具链"分离形态 |
| 2 | 目标平台 | 桌面三平台，系统 webview 路线：Windows=WebView2、macOS=WKWebView、Linux=WebKitGTK+GTK | 不捆绑浏览器内核 |
| 3 | 参考策略 | **Ryn**=架构蓝本（分层/源生成 IPC/自定义 scheme/AOT 属性）；**pywebview**=API 基本面与多引擎桥接经验；**IgniteView**=仅借鉴窗口特效、按 RID 加载原生库等细节 | IgniteView 的反射 IPC、本地 HTTP 托管、Linux QtWebEngine 路线**不采用** |
| 4 | 里程碑 | 第一个里程碑 = **Windows Native AOT demo 跑通**（窗口+IPC+内嵌资源+AOT 发布）；macOS/Linux 随后 | |
| 5 | 技术底座 | 仅 **net10.0**；**纯 C# P/Invoke**——无 C++ 组件、无 GUI 框架依赖；Windows = Win32 + WebView2 COM 互操作 | 与同期的 C++ 底座路线有意区分 |
| 6 | MVP 范围 | 最小核心（窗口、URL/内嵌资源、自定义 scheme/虚拟主机、双向 IPC、事件）+ **对齐 pywebview 基本面**（全屏/置顶/尺寸位置/无边框、loaded/closing 等事件、evaluate_js、文件与消息对话框、debug） | |
| 7 | IPC 模型 | `[OrielCommand]` 特性 + **Roslyn 源生成器**编译期生成分发代码；System.Text.Json 源生成序列化；**运行期零反射** | AOT 硬性要求；Ryn 已验证此路线 |
| 8 | API 形态 | **App 生命周期构建器风格**（`OrielApp.CreateBuilder(...)...Run()`），多窗口一等公民，窗口类内部可独立使用 | 对齐 Ryn / Tauri 体验 |
| 9 | 包结构与许可 | **单包先行**（OrielWeb + OrielWeb.Generators）；**MIT** | 后续视需要拆平台包 |
| 10 | 命名 | **OrielWeb**（Oriel=凸窗，用户定名） | NuGet 0 同名包（已核实）；GitHub 有跨生态同名（低代码构建器等），不冲突 |

## 事实依据（三参考项目分析结论，2026-09-27）

- **Ryn 0.38.0**（MIT，.NET 10）：与本项目定位几乎相同的现有实现——C++ 底座 + ClangSharp 绑定 + 源生成 IPC（`[RynCommand]`，零反射）+ `ryn://` scheme + 能力安全模型，有专门 AOT CI。缺陷：Alpha、单人维护、与该 C++ 底座强耦合、原生二进制需预编译分发。→ 作为架构蓝本，但底座换纯 C#。
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
- COM 互操作用 **`[GeneratedComInterface]`/`[GeneratedComClass]`** 的方案曾据此废弃；**该结论已于 2026-09-28 被推翻并撤销**（见文末"E 阶段执行结果"）。保留的通用经验：此类接口/类必须 partial，LibraryImport 的 bool 返回值需显式 `[MarshalAs(UnmanagedType.Bool)]`。
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

## 2026-09-28 E 阶段执行结果（WebView2 绑定迁移已落地）

- **路线选定：A（使用 `WebView2Aot` 现成包），B 经实测否决。** 否决 B 的原因不是工作量，而是**产物不可用**：
  `Win32InteropBuilder` 是单一 winmd 输入，只喂 WebView2 winmd 时 `Windows.Win32.Foundation` /
  `System.Com` 的基础类型无法解析，生成物出现 **1016 处类型退化为 `object`**（形如 `object Navigate(object uri)`、
  `ref object token`），不可调用。上游之所以可用，是因为 `WebView2Aot` 自带约 1800 行生成器定制
  （`Builder.cs` 142 行 + `WrapperGenerator.cs` 1095 行 + `Patches.json` 193 行），且**其便利层建立在 `DirectNAot` 之上**
  （`WrapperGenerator` 产出 `IComObject` 风格 API）。因此"自主绑定且零第三方依赖"必须重写该便利层
  （约 500–800 行），收益不抵成本。已开发出的公共基础设施（winmd 生成、生成器 vendoring、自建生成入口）
  已清理，未入库。
- **手工 vtable 层已整体删除**（迁移计划 E-8）：`Interop/WebView2Com.cs`（手写槽位 + IID 表 + loader 入口）、
  `Win32NativeCallbacks.cs`（手写 CCW thunk）、`Win32WebView2Ipc.cs`（已内联为 `UiThreadReplySink`）、
  `Win32WindowHostV2.cs`（其实现已内联进共享基类 `Win32WindowHost`），以及只为派生类存在的泛型
  `CreateHost<T>` 工厂。净删除约 1190 行。`ORIEL_WIN_BACKEND` 开关与 A-3 槽位门禁
  （`verify-webview2-slots.py` 及 CI 步骤）随之删除——手工槽位常量已不存在，这些防护失去对象。
- **保留 `Microsoft.Web.WebView2` 包**（仍为 `ExcludeAssets="compile"`）：`WebView2Utilities.Initialize`
  需要其中分发的 `WebView2Loader.dll` 原生位；托管互操作程序集依旧不被引用，AOT 裁剪自动丢弃。
- **回退点**：`git tag stage-b-done` 是手工实现被删除前的最后提交；`stage-e8-done` 为删除后状态。

## 无边框窗口的边缘 resize（2026-09-28 修复）

**问题**：`WithFrameless()` 的窗口拖边框无法改变尺寸。

**根因有两层，缺一都不足以解释**：
1. `WM_NCCALCSIZE` 返回 0 令客户区等于整个窗口，而 `WS_THICKFRAME` 提供的边缘热区**恰恰位于被消除掉的非客户区**，
   `DefWindowProc` 因此不再产生 `HTLEFT`/`HTTOP` 之类的命中值（此前注释里"保留窗口样式即可保住热区"的说法是错的）。
2. 更关键：WebView2 的 Chromium 子窗口铺满客户区，且对 `WM_NCHITTEST` 返回 `HTCLIENT`（**不是** `HTTRANSPARENT`），
   系统据此**终止**向父窗口的上溯询问——父窗口的 `WindowProc` 连这条消息都收不到。

**外部调研佐证**（`参考项目/AOTrino` 与 `Ryn-0.38.0`）：
- **AOTrino** 与本项目同栈（C# + Native AOT + `WebView2Aot`），同样在窗口自己的 `WM_NCHITTEST` 里算命中值。
  实测其 `HwndWebViewWindow`（HWND 宿主，与本项目同类）**完全无法 resize**——那段代码只在 `CompositionWebViewWindow`
  下有效，因为 Composition 模式下 WebView 是**视觉而非子窗口**，输入由宿主 `TryForwardPointerInput` 手动转发。
- **Ryn** 在原生层只用 `DECORATION_PARTIAL`（只去标题栏、**保留边框**），因而系统的边缘 resize 天然可用，
  自己完全不碰 Win32 消息。

**采用方案**：`WM_NCCALCSIZE` 改为「客户区 = 窗口矩形让出四条边框」（即标准客户区，但**不减标题栏**）。
边框仍属非客户区 → 系统的边缘 resize、光标形状、双击边框等全部原生恢复；标题栏不参与 → 窗口顶部仍由页面自绘。
代价是四周保留一条系统宽度的细边框（`SM_C*SIZEFRAME + SM_C*PADDEDBORDER`，按 DPI 缩放）。

**已尝试并放弃的路线**（代码已移除，勿重走）：
- 子类化 WebView2 窗口链并转发 `WM_NCLBUTTONDOWN`：深层 `Chrome_WidgetWin_1` / `Chrome_RenderWidgetHostHWND`
  **不在本进程**，`SetWindowSubclass` 对它们返回 `false`；只挂得上 `Chrome_WidgetWin_0`，
  而 `WM_LBUTTONDOWN` 不冒泡、只投递给链路最深处，收不到。
- 让子窗口对边缘返回 `HTTRANSPARENT` 期望上溯到父窗口：**实测父窗口收到 `WM_NCHITTEST` 的次数为 0**。
  MSDN 所述"传给同线程的 underlying window"指的是 z-order 上被覆盖的窗口，不含父窗口。
- 子窗口直接返回 `HTLEFT`：子窗口没有 `WS_THICKFRAME`，系统既不会替它启动模态循环，也不再投递客户区消息。

**验证**：四条边各拖动 100px 均精确生效、正交方向不变；连续 3 次稳定；编译 0 警告 0 错误，单测 39/39，桥接 19/19。

### 后续：改用 Composition 宿主（同日晚，最终方案）

上述"保留边框"方案虽然 functionally 正确，但用户反馈**窗口四周出现一圈可见边框（白边）**，
与"无边框窗口"的定位冲突。最终改为 **Composition 宿主**，彻底消除边框：

- **窗口**：以 `WS_EX_NOREDIRECTIONBITMAP` 创建，内容完全由 DirectComposition 提供。
- **合成树**：直接用 **dcomp.h 的 COM 接口**（`DCompositionCreateDevice` +
  `IDCompositionDevice` / `IDCompositionTarget` / `IDCompositionVisual`），**不用** WinRT 的
  `Windows.UI.Composition` —— 后者需要 `net10.0-windows` TFM 与 CsWinRT 投影，而本库面向跨平台的
  `net10.0`；`DirectNAot` 正是 Win32 COM 的 AOT 绑定，恰好覆盖 `dcomp.h`。
- **WebView**：改用 `ICoreWebView2CompositionController`，经 `RootVisualTarget` 作为视觉接入合成树。
  **它从此不再是子窗口**——这正是关键：此前 `Chrome_*` 子窗口铺满客户区并对 `WM_NCHITTEST` 返回
  `HTCLIENT`，阻断了向父窗口的上溯。现在 `WM_NCCALCSIZE` 恢复返回 0（客户区铺满、无任何边框），
  边缘命中由窗口自己的 `WM_NCHITTEST` 显式给出。
- **输入**：组合托管的 WebView 收不到系统输入，因此由宿主转发鼠标
  （`WM_MOUSEMOVE` / `LBUTTON*` / `RBUTTON*` / `MBUTTON*` / `MOUSEWHEEL` / `MOUSEHWHEEL` →
  `SendMouseInput`，含按键状态位、滚轮量与 `TME_LEAVE` 跟踪）并同步光标
  （`CursorChanged` → `SetCursor`）。**键盘不需要转发**，WebView2 自行处理——与 AOTrino 的
  Composition 宿主一致（它也只转发鼠标/指针）。
- **一个易错点**：挂上 `RootVisualTarget` 之后必须**再 `Commit` 一次**，否则合成树不生效，
  表现为"窗口完全透明"（能截到桌面而不是窗口内容）。

**验证**：四边各拖 100px 精确生效且正交方向不变；点击页面"全屏"按钮触发 IPC 往返使窗口变为
1920x1080（证明鼠标链路 `WM_LBUTTONDOWN → SendMouseInput → 页面 → IPC → 宿主` 完整）；
截图确认完全无边框；进程不再有 `Chrome_*` 子窗口；编译 0 警告 0 错误，单测 39/39，桥接 19/19。

### 无边框标题栏：双击最大化与图标同步

**问题**：`WithFrameless()` 的窗口双击标题栏没有反应；最大化/还原按钮的图标不随状态变化。

**双击失效的根因（两层）**：
1. Windows 的标题栏拖动走的是**程序发起**的 `WM_NCLBUTTONDOWN` + `HTCAPTION`。系统的标题栏双击语义
   （在 `HTCAPTION` 的模态循环里处理）**只对系统自身的按下生效**，不会识别程序发起的这一次。
2. 更关键：若在 `mousedown` 时立刻发起拖动，它会进入**原生模态循环并阻塞消息处理**，第二次点击被
   该循环吞掉，浏览器因此**永远凑不满一个双击序列**，`dblclick` 不触发。

**修法**：**拖动改为"指针移动超过阈值（3px）后才发起"**。原地双击根本不进入拖动，双击语义得以保留；
真正的拖动只是晚几像素接管——模态循环以当前光标为基点，窗口不会跳。
另外补上遗漏的 `WM_LBUTTONDBLCLK` / `WM_RBUTTONDBLCLK` / `WM_MBUTTONDBLCLK` 转发
（组合宿主下所有鼠标消息都必须手动注入），让 Chromium 能合成 `dblclick`。

**图标不变化的根因**：既没有查询状态的接口，也没有"宿主 → 页面"的通知通道；用户经原生路径
（双击标题栏、拖边框到屏幕顶端、`Win+↑`）最大化时，页面无从察觉。

**修法**：
- `IWindowBackend` / `WebviewWindow` 暴露 `IsMaximized`；`ToggleMaximize()` 改为返回切换后的状态。
- Windows 在 `WM_SIZE` 里比较 `IsZoomed`，变化时经 `PostMessageOnUi` 推送
  `{__oriel:'event', name:'maximized', value}`；新文档加载后补推一次初始值。
- 桥接 JS 新增 **`oriel.on(name, handler)`** 事件通道（返回退订函数；单个处理器抛异常不影响其余），
  三平台共用同一份模板。
- macOS/Linux 同步实现 `IsMaximized` 与返回值（Linux 缺事件字段一度导致编译失败，已补）。

**验证**：双击标题栏 1024x720 → 1920x1040；图标 `□` → `❐`；标题栏拖动仍移动窗口 (135,135) 且尺寸不变；
最大化按钮双向切换；四边 resize 精确 100px；桥接测试 28/28（新增 9 个事件用例），单测 39/39。

### WebView2 运行时加载器：内嵌进库（发布产物由此变为单文件）

**问题**：AOT 发布产物不是单文件。`OrielDemo.exe` 旁少一份 `WebView2Loader.dll` 就会在启动时弹
`初始化 WebView2 失败：Cannot load WebView2Loader.dll. Make sure it's present in the current's process path.`

**根因**：`WebView2Aot` 的绑定是 `[LibraryImport("WebView2Loader")]`，运行期依赖微软的 loader。
`WebView2Utilities.Initialize` 只查两个位置：exe 同目录、exe 同目录下 `runtimes\win-{arch}\native\`，
或**传入程序集的嵌入资源**。本库此前两边都没做，只能靠随包复制的那份文件。

**决定：三个架构的 loader 全部内嵌进 `OrielWeb.dll`**，`Initialize` 改传本库程序集
（`typeof(Win32WindowHost).Assembly`）而非入口程序集。上游样例是在**应用**里内嵌 loader；放进库里则
反过来成立——所有引用本库的项目都自动拿到单文件产物，各自不必再复制那段 csproj。资源名需含架构串
且以 `WebView2Loader.dll` 结尾，故显式指定 `LogicalName`（源文件在 NuGet 缓存中，默认命名不可预测）。

**代价**：库是 AnyCPU 构建，且 RID 不随 `ProjectReference` 传播，库里无法预知消费方的架构，故三个都留；
单架构产物会多带另外两份（约 273 KB）。

**顺带清掉的发布噪音**：`Microsoft.Web.WebView2` 的包引用改为 `ExcludeAssets="all"` +
`PrivateAssets="all"`（该包在本库只作 loader 文件来源，靠 `GeneratePathProperty` 定位）。此前它带来
4 份 loader（顶层 + 三个 runtimes 子目录）、约 780 KB 的 WPF/WinForms XML 文档，并牵入与 .NET 10 版本
不一致的 WindowsBase（MSB3277）；排除后三者一并消失，两个 csproj 里的 MSB3277 抑制也失去对象而删除。
**注意**：官方开关 `WebView2NeverCopyLoaderDllToOutputDirectory` 拦不住运行时资产解析复制的那份 loader，
而其 `buildTransitive/` 目标还会作用到消费方，所以必须用 Exclude/PrivateAssets。

**另一条路线（实测可链接成功，未采用）**：Native AOT 的 `DirectPInvoke` 静态链接。生成的绑定模块名是
`WebView2Loader`（无 `.dll`），正合该机制；包内自带 `WebView2LoaderStatic.lib`，配
`<DirectPInvoke Include="WebView2Loader" />` + `<NativeLibrary Include="…WebView2LoaderStatic.lib" />`
+ `version.lib` 后链接通过（无 LNK2019，静态库净增约 15 KB），可免去 `%TEMP%` 解压。未采用的原因：这是
**应用级**配置，库无法替消费方设置；且需跳过 `WebView2Utilities.Initialize`，否则仍会解压内嵌那份。

**发布产物（win-x64，全新构建）**：

| 命令 | 产物 |
|---|---|
| `dotnet publish -c Release -r win-x64` | `OrielDemo.exe` 4.63 MB + `OrielDemo.pdb` 20.7 MB + `OrielWeb.pdb` + `OrielWeb.xml` |
| `… -p:DebugType=none` | `OrielDemo.exe` + `OrielWeb.xml` |
| `… -p:AllowedReferenceRelatedFileExtensions=.pdb\;.pri` | 仅 `OrielDemo.exe`（4.63 MB） |

PDB 由原生链接步骤产出，与托管 `DebugType` 无关，故 `-p:DebugType=none` 只在全新构建下去掉它
（增量发布会复用旧链接结果——这一点最初误判过一次）。库内嵌三架构 loader 使 exe 比内嵌前大约 413 KB。

**验证**：把**仅 exe** 复制到隔离目录、从中立工作目录启动 —— 进程存活、只出现 `OrielWeb_Window`
（无 `#32770` 失败对话框）、loader 由内嵌资源解压到 `%TEMP%\{guid}\`（162.1 KB）、截图显示页面完整渲染
且 IPC 绿标正常。编译 0 警告 0 错误，单测 39/39，桥接 28/28。
（早前的"单 exe 可运行"是误判：失败对话框本身也让进程存活，改以窗口类名判定后才排除。）

### WebView2 运行时缺失：先检测再引导（库不自行安装）

**问题**：运行时缺失时用户看到的是系统级文案，无从判断该装什么、去哪装。实测（把
`ORIEL_WEBVIEW2_FOLDER` 指向不存在的目录，复现同一条错误路径）：

```
[#32770 title='OrielWeb']
    child[Static] = 初始化 WebView2 失败：Unable to find the specified file.
```

根因：`CreateCoreWebView2EnvironmentWithOptions` 找不到运行时时只回一个"找不到文件"的 HRESULT，
`WebView2Aot` 的 Task 包装再用 `Marshal.GetExceptionForHR` 把它转成托管异常。

**做法**：装配前先用 loader 探测可用运行时版本
（`WebView2Utilities.GetAvailableCoreWebView2BrowserVersionString`，它内部吞掉 HRESULT、取不到时返回空，
正是官方建议的"先探测再创建"顺序），取不到则进入 `OnWebView2RuntimeMissing`：

- 应用注册了 `OrielAppBuilder.OnWebView2RuntimeMissing` → 交给它，库不弹窗；
- 未注册、或回调自身抛异常 → 弹库的默认提示，文案区分"系统未装 Evergreen 运行时"与
  "`ORIEL_WEBVIEW2_FOLDER` 指向的固定版本目录无效"；
- 除非回调置 `KeepWindowOpen = true`，窗口随后被销毁（没有 WebView2 的窗口没有内容可按）。

**为什么不自动装**：提示方式与是否静默安装属应用策略（企业环境常禁止联网安装）。Tauri 的默认
`webviewInstallMode: downloadBootstrapper` 是在**安装器**层下载并运行微软 bootstrapper；本项目没有安装器
（只有 NuGet 包 + 单文件 exe），因此只能在应用内引导。新增公开面：`OrielWebView2RuntimeMissingEventArgs`
（含 `Window` / `BrowserExecutableFolder` / `KeepWindowOpen` 与 `DownloadUrl` 常量）与一个 builder 方法。

**踩到的坑（值得记下）**：首版把处置放在装配失败的同一时刻**同步**执行，结果回调里
`e.Window.ShowMessage(...)` 抛"窗口后端尚未初始化"，被兜底逻辑吞掉、退回默认提示——真机验证时唯一的线索
就是"弹出来的是库的默认文案而非 demo 的"。原因是 `OrielApp.Run()` 要等 `CreateWindow` 返回后才
`window.Attach(backend)`，而装配正发生在 `CreateWindow` 内部。改为 `_backend.PostToMainThread(...)`
延到消息循环启动后处理，回调用窗口门面即可正常工作。

**验证**：缺失路径 + 注册回调 → 弹出 demo 引导框（标题「缺少 WebView2 运行时」，含下载地址）；
缺失路径 + 不注册 → 弹出库默认提示（标题「OrielWeb」，说明指定目录无效）；正常路径 → 主窗口照常渲染、
IPC 正常、loader 仍由内嵌资源解压。编译 0 警告 0 错误，单测 47/47（新增 8 个契约用例），桥接 28/28。

**已知未覆盖**：真正的"系统未装 Evergreen 运行时"这一文案分支只能在未安装运行时的机器上验证，本次用
无效固定版本目录复现的是同一条错误路径。另外"运行时过旧"不会进入本流程——它在后续调用上失败并走通用
错误提示，README 中"附带已安装版本号"的旧声明已一并更正。

### 运行期任务栏图标：必须显式设到窗口类上

**现象**：demo 的 csproj 已配 `ApplicationIcon`、exe 也确实带图标（从发布产物反提取得到），但运行时
任务栏按钮显示的是**通用应用图标**。

**根因**（与直觉相反，实测确认）：任务栏 / Alt-Tab 的按钮图标取自**窗口图标**。窗口完全没有图标时，
shell 退回的是通用应用图标——**不是** exe 自带的图标。而 `Win32WindowHost` 注册窗口类时
`hIcon`/`hIconSm` 都是 0，所以 csproj 里配的图标只在资源管理器里生效，运行期看不到。
（定位过程：`WM_GETICON` 三个尺寸与 `GCLP_HICON`/`GCLP_HICONSM` 全为 0；任务栏按钮的放大截图显示的
是通用图标。注意 `WM_GETICON` 只能跨进程读回句柄值，HICON 在别的进程里无意义，判断要以类图标与
屏幕截图为准。）

**修法**：`EnsureWindowClass` 里用 `ExtractIconEx(Environment.ProcessPath, 0, …)` 取本进程 exe 的
大/小图标，设到窗口类上；注册失败时 `DestroyIcon` 释放，无图标（返回 0）时保持 0。用 `ExtractIconEx`
而非 `LoadIcon(hInstance, 32512)` 是为了不依赖图标资源 ID——.NET SDK 用 32512，原生 `.rc` 可以是别的，
而 `ExtractIconEx` 走的是 shell 自身的解析逻辑。

**验证**：`GCLP_HICON`/`GCLP_HICONSM` 变为非零；任务栏按钮的放大截图由通用图标变为本应用图标。
无图标文件（`kernel32.dll`、`version.dll`）上 `ExtractIconEx` 返回 0 且句柄为空，防御分支正确。
编译 0 警告 0 错误，单测 47/47，桥接 28/28。
