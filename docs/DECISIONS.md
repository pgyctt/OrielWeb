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
  - **2026-09-29 更正**：这一条只做到了 C# 侧——**"宿主 → 页面"的事件通道当时只有 Windows 真正接上**
    （`__oriel:"event"` 的接收分支仅在 Windows 的 `chrome.webview` 路径）。macOS/Linux 的页面插件在
    `window.oriel.on("maximized", …)` 上等于**死订阅**，且 Linux 的返回值因 GTK 异步语义是旧值，
    导致"图标与窗口状态恰好相反"。详见文末「Linux 最大化图标与窗口状态相反」。

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

## Linux 首次真机运行验证（2026-09-28，WSL2 + WSLg）

**结论**：Linux 后端由"仅编译通过"推进到"真机跑通"——窗口创建、页面渲染（含中文）、一次完整 IPC 往返
（页面右下角徽章由「IPC 连接中…」变为「IPC 已连接」）均已在 WSL2 + WSLg 确认，X11 与 Wayland 两条后端路径各跑通一次。

### 缺陷 1（阻塞级）：进程存活，但窗口从未被创建

`LinuxPlatformBackend.CreateWindow` 只 `new LinuxWindowHost(...)`，**全仓没有任何地方调用 `host.Create()`**。
Windows 后端的建窗在静态工厂内完成，而 Linux/macOS 后端是实例方法：构造函数只装配宿主，真正的
`gtk_window_new` → `show_all` → 首次导航都在 `Create()` 里——不被调用，则窗口从未存在。

表现极具迷惑性：进程存活到观察窗结束、不崩不报错、CPU 占用 0、无 WebKit 子进程、屏幕上什么都没有。

**修法**：`CreateWindow` 中在 `return host` 之前显式调用 `host.Create()`。

**同构缺陷已核实并同步修复**：macOS 后端（`MacOSPlatformBackend.CreateWindow`）与 Linux 修复前完全同构——同样只
`new MacOSWindowHost(...)` 就返回，而建窗逻辑在 `MacOSWindowHost.Create()`。已按同一处修复补上 `host.Create()`。
macOS **无真机**，该修复仅靠代码一致性保证，**未经运行验证**（后续有 Mac 环境时优先复验此处）。

附：全仓 `host.Create()` 的调用点只有 Linux 与 macOS 两个后端的 `CreateWindow`——Windows 后端的建窗在静态工厂内完成，
不属此两段式形态，不要照搬。

**教训**：新建宿主 ≠ 创建窗口。"构造函数只装配、`Create()` 才建窗"这类两段式初始化必须有唯一入口兜底，
否则遗漏是静默的——进程照常活着，只是什么都没做。

### 缺陷 2（环境依赖，非代码缺陷）：中文渲染为方框

取证：`fc-list :lang=zh` 输出为空、`fc-match sans-serif` 回落 `DejaVu Sans`（无 CJK 字形，故汉字无字形可绘）；
同时 demo 的字体栈 `"Segoe UI", "Microsoft YaHei", system-ui, sans-serif` 全是 Windows 专有字体名，Linux 上一个都不存在。

**修法**：① 装字体——`sudo` 需密码，故改走免 root 路径：`apt-get download fonts-noto-cjk`（62 MB）+
`dpkg-deb -x` 解包，把 TTC 拷到 `~/.local/share/fonts` 后 `fc-cache -f`；② demo 字体栈补上
`"Noto Sans CJK SC"` / `"Noto Sans SC"` / `"Source Han Sans SC"` 等跨平台族。**库层对字体零干预**（字体选择属应用与系统职责）。

### 已知限制（本轮仅记录，未修）：Wayland 下无边框窗口不可拖动

无边框拖动靠 JS 流式坐标 → 宿主 `gtk_window_move`；而 Wayland 协议不允许客户端自行移动窗口，
`gtk_window_move` 在 Wayland 下是空操作。**对照取证**：同一个 exe 强制 `GDK_BACKEND=x11` 后拖动正常
→ 坐实为"后端差异"而非"拖动链路缺陷"。修法方向：Wayland 下改调 `gtk_window_begin_move_drag`
（交合成器接管），X11 保留流式；本轮未实施。

### 取证方法：`tools/verify-linux.sh` 已升级为双路径

- 断言与后端无关的硬证据：**进程存活至观察窗结束** + **出现 `WebKitWebProcess`/`WebKitNetworkProcess`
  子进程**（后者只在 webview 真的开始加载页面时才出现）。用运行前的进程基线做差集，避免把残留进程算作本次证据。
- WSLg 下 GTK 默认走 Wayland，窗口注册在 Weston 合成器而非 XWayland，`xwininfo` 枚举不到；因此再跑一次
  `GDK_BACKEND=x11`，用 `xwininfo -root -tree` 做窗口断言。两条路径观察同一份页面，人眼结论可互相印证。
- 本次实测：两条路径均在第 2s 拉起 WebKit 子进程，进程存活至 45s 观察窗结束；X11 路径枚举到
  `0x60000e "Oriel Demo": ("OrielDemo" "OrielDemo") 1024x720`；徽章人眼确认为「IPC 已连接」。
  字体栈修复后重新发布，在**最终产物**上再跑一次（20s 窗）结论一致（两条路径均 PASS）。
- 环境噪音（不影响功能，仅性能）：WSLg 无 GPU，MESA/ZINK 回落软件渲染，stderr 有
  `libEGL warning: MESA-LOADER: failed to retrieve device information` / `MESA: error: ZINK: failed to choose pdev` 等警告。

### 一处工具侧踩坑（非产品代码）

`pkill -f "WebKitWebProcess|WebKitNetworkProcess"` 会**匹配到承载该命令的 bash 自身**（其命令行里含同样字符串），
于是把执行环境一起杀掉、命令静默返回空输出（表现为"窗口没起来"）。查/杀进程一律用 `pgrep -x` / `pkill -x`
精确进程名，不要用会自匹配的 `-f` 模式。

## Linux 最大化图标与窗口状态相反（2026-09-29 修复）

**现象**：Linux 上点「最大化」，窗口确实最大化了，但标题栏按钮显示的是「还原」图标（反之亦然）——
图标与实际状态恰好差一格。窗口行为本身完全正常。

**根因（三件事叠加，缺一不成病）**：

1. **把异步语义当同步读**：`LinuxWindowHost.ToggleMaximize` 在调用 `gtk_window_maximize` /
   `gtk_window_unmaximize` **之后立刻**读 `gtk_window_is_maximized` 作为返回值。而 GTK 的这两个调用只是
   向窗口管理器**发请求**，状态要等 WM 确认（`window-state-event`）才更新——此处读到的必然是**切换前的旧值**。
   页面正是用这个返回值驱动图标（`app.js` 的 `win.toggleMaximize` 分支），于是图标必然相反。
   Windows（`IsZoomed`）与 macOS（`zoom:`）都是同步生效，故只有 Linux 出现此现象。
2. **没有任何状态变化信号**：Linux 后端从未连接 `window-state-event`（也未接 `notify::window-state`），
   用户经原生路径（WM 快捷键、拖边框到屏幕边缘）最大化时宿主完全不知情。
3. **事件从未送到页面**：`MaximizedChanged` 只走到 C# 事件（`WebviewWindow.MaximizedChanged`），
   Linux/macOS 都没有把它编码成页内调用；桥接脚本里 `__oriel:"event"` 的接收分支只存在于 Windows 的
   `chrome.webview` 路径。因此 `app.js` 的 `oriel.on("maximized", …)` 在 Linux 上是**死订阅**——
   一旦 (1) 给出反值，没有任何通道能纠正。Windows 之所以看不出问题，是因为它靠 `WM_SIZE` 推的那次事件兜底。

**修法（Linux）**：

- 连接 GTK 的 `window-state-event`。回调**刻意不解析 `GdkEventWindowState` 结构**（其布局细节随 GDK 版本有异，
  且我们只需要"状态可能变了"这个可靠信号），权威状态一律用 `gtk_window_is_maximized` 读；回调返回
  `FALSE` 不吞事件。读到的值与上次上报值比对，变化才上报（去重）。
- `ToggleMaximize` 的返回值改为**意图值**（`!当前状态`），不再"切换后立刻读"。真实状态由上述信号确认后上报；
  万一 WM 拒绝请求，那次上报会把页面纠正回真实值。
- 新增页面事件推送 `window.oriel._onEvent("maximized", true|false)`（`evaluateJavaScript`），与回执通道分开
  （回执那条 `PostWebMessageOnUi` 只认 `{"id","ok"}` 形态，不能复用）。
- 每次加载完成**强制补推一次**当前状态（与 Windows 的 `OnNavigationCompleted` 对齐），修正初始图标——
  否则以 `Maximized=true` 启动的窗口初始图标必然是错的。页面未就绪（桥接脚本尚未注入）时的事件直接丢弃：
  不会漏状态，因为加载完成必补推。`app.js` 是普通同步脚本（`<script src="app.js">`），其 `oriel.on(...)`
  注册一定早于 load 完成，故此补推必被收到。

**macOS 同步对齐**（无真机，仅编译与代码一致性保证）：新增 `windowDidResize:` 委托回调（覆盖系统菜单 Zoom
等不经过 `win.toggleMaximize` 的原生路径）、统一走 `SyncMaximizedState`、导航完成后强制补推。
`zoom:` 本身同步生效，故 `ToggleMaximize` 的返回值语义不变（仍是读回的真实值）。

**验证（Linux，WSL2 + WSLg，人眼）**：点最大化 → 图标变「还原」；再点 → 变回「最大化」；双击标题栏
（**丢弃返回值、只靠事件**的那条路径）图标同样同步。编译 0 警告 0 错误，单测 47/47，`tools/verify-linux.sh`
双路径 PASS（两条路径均存活至观察窗结束 + 第 2s 拉起 WebKit 子进程 + X11 路径窗口断言成立）。
复跑期间有一次两条路径均报"进程提前退出"、case 日志为空，未能复现；随后两次复跑均 PASS，暂归因为
WSLg 显示服务的偶发波动（与本次改动无因果关系：那次连"存活"都没到，而改动只涉及状态信号）。

**遗留**：macOS 的 `windowDidResize:` 是新增的原生回调，待有 Mac 环境时确认它不干扰拖拽/缩放路径。

## macOS 首次真机运行验证（2026-09-29，GitHub 托管 macOS runner）

**背景**：没有 Mac 硬件。在普通 PC 上虚拟化 macOS 违反 Apple 许可（只授权在 Apple 硬件上运行），故不走
那条路，改用 GitHub Actions 的托管 macOS runner。但**"托管 runner 能否运行本机 AppKit 窗口应用"没有官方
明文**——"能跑 iOS 模拟器上的 XCUITest"不能作为证据，模拟器有自己的渲染路径。所以先探，再验证。

### 第 0 步：先探清 runner 能不能建窗

用一个最小 AppKit/WKWebView 探针直接问，而不是靠猜。该探针属**一次性诊断**，结论落地后已删除
（连带那个手动触发的 workflow）；下表就是它当时的原始输出：

| 组 | 探针结果 | 结论 |
|---|---|---|
| A 会话 | `managername=Aqua`、`/dev/console` 属主=`runner`、`autoLoginUser=runner`、WindowServer 与 Dock 在跑 | 有图形登录会话 |
| B 建窗 | `NSScreen.screens.count=1`（1024×768）、`isVisible=true`、`windowNumber=28` | 确实能建出窗口 |
| C 截图 | `screencapture` 成功，1024×768 真实像素 | 能截到像素（屏幕录制授权已具备） |
| D WebKit | `didFinish` + `document.title=oriel-probe`，且出现 `com.apple.WebKit.WebContent`/`Networking` | WKWebView 在该环境可用 |
| E 字体 | 无 `PingFang.ttc`（macOS 26 挪了位置），但有 `STHeiti Light.ttc`/`Hiragino Sans GB.ttc` | 中文不会缺字形 |

结论：托管 runner 具备完整 GUI 能力——有图形登录会话、能建出可见窗口、能截到真实像素、WKWebView
能加载页面，因此可以做窗口级验证。注意这是 **GitHub 侧的前提**而非本库的保证：若哪天 runner 镜像
去掉图形会话，`smoke-macos` 会失败，而那时要按"环境变了"而不是"代码坏了"来排查。

### 真机上依次暴露的四个缺陷

只有编译验证时，这四条一条都看不出来。

**① `CreateWindow` 从不调用 `host.Create()`**（与 Linux 同源）
进程不崩、不报错，但窗口从未被创建。Linux 那轮已修，macOS 侧一并补上。

**② 纯 P/Invoke 不链接任何框架，也从不 `dlopen`**
macOS 后端只 `[LibraryImport]` 到 `libobjc`，产物是无 bundle 的裸可执行文件，因此
`objc_getClass("NSApplication"/"NSWindow"/"WKWebView")` 全部返回 nil。而 **ObjC 向 nil 发消息是静默
no-op**：所有建窗调用什么都不做，`NSApplication.run` 也立即返回。现象是"**进程以 0 退出、无窗口、无
WebKit 子进程、无任何输出**"——最难查的一类失败。
**修法**：后端初始化最先 `dlopen` Foundation/AppKit/WebKit（`ObjCRuntime.LoadFrameworks`，失败时带
`dlerror` 文本抛异常），并把窗口/webview 的类查找改为 `GetClassOrThrow`，让"类不存在"变成一条明确异常。

**③ 裸可执行文件缺少 bundle 身份**
AppKit 先给出线索：`Cannot index window tabs due to missing main bundle identifier`。
`WKWebView` 是多进程架构，宿主需要有效的 bundle 身份才能与 `WebContent`/`Networking` 的 XPC 服务通信。
**修法（属打包层，不是库配置）**：在产物旁构造最小 `.app`（`Info.plist` 含
`CFBundleIdentifier`/`CFBundleExecutable`）并从 bundle 内启动。

**④ 把 `char*` 当 `NSString*` 传给 WebKit**
`WKUserScript initWithSource:` 与 `+[NSURL URLWithString:]` 都期望 `NSString*`，但调用走了
`StringMarshalling.Utf8` 的重载：托管字符串被 marshal 成 UTF-8 字节指针，WebKit 一问对象类型就
**在 CoreFoundation 的 `__CF_IS_OBJC` 里 `__builtin_trap`（`EXC_BREAKPOINT`，退出码 133）**。
**修法**：先用 `MakeNSString` 造出真对象、走收 `id` 的重载；删掉诱导误用的 `char*` 重载，并在
`SendIdUtf8` 上注明"只用于确实要 `const char*` 的 selector（如 `stringWithUTF8String:`）"。

### 取证手法（`tools/verify-macos.sh`，CI job `smoke-macos`）

- **机器断言**：进程存活至观察窗结束 + 出现 `com.apple.WebKit.WebContent` 子进程 + `CGWindowList`
  （内联 Swift 小程序）枚举到标题含 `Oriel Demo` 的窗口。
- **人眼证据**：截图作 artifact 上传——页面渲染、中文与 IPC 徽章没有宿主侧可观测信号。
- **退出码是第一分叉**：`wait` 返回 0 = 逻辑性退出（当时对应"全部 no-op"），>128 = 被信号杀死
  （133 = SIGTRAP，对应缺陷 ④）。
- **崩溃报告**：`~/Library/Logs/DiagnosticReports/*OrielDemo*`（先 `sleep 3` 再找，ReportCrash 是异步写）。
- **os_log**：托管运行时的 FailFast 在 macOS 上经 `os_log` 上报而不写 stderr，所以 `run.log` 会是空的；
  脚本把命中关键词的行直接打进 CI 日志，省去下载 artifact。
- **lldb 复跑抓栈（本次的突破口）**：`lldb -b -o run -o "thread backtrace" -o quit -- <exe>`。进程由
  lldb 自己启动（不是 attach），且产物是 ad-hoc 签名、未启用 hardened runtime，故不需要
  `get-task-allow`。AOT 二进制没有符号，但栈帧的镜像名足够定位——本次直接指向
  ``CoreFoundation`__CF_IS_OBJC``，把"读日志猜"变成"看栈定位"。
- **结论**：CI run #10（`3b4efbe`）**success**；截图确认页面渲染正常、中文正常、徽章为「IPC 已连接」。

### 工具侧教训

- **变量紧贴全角字符**：`发布 AOT（$RID）` 在 CI 的 macOS bash 3.2 + 非 UTF-8 locale 下会把全角括号吞进
  变量名，配合 `set -u` 直接 `unbound variable`；本地 bash 5.x + UTF-8 复现不出来。**脚本里变量一律写
  `${VAR}`**（`verify-linux.sh` 也潜伏过 4 处，已一并修掉）。
- **Apple Silicon 上任何可执行文件都至少要 ad-hoc 签名**，否则内核直接 `Killed: 9`；dotnet 的 AOT 产物
  通常已带，但脚本仍做一次幂等补签，避免把签名问题误读成代码缺陷。
