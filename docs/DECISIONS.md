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
- 内容托管 MVP 用 **WebView2 虚拟主机映射**（`SetVirtualHostNameToFolderMapping`，槽 71）+ 内嵌资源启动时解压到用户目录；内存直供的自定义 scheme 为后续优化。外部 dev server URL（Vite 等）直接 `Navigate` 支持。**Linux 与 macOS 没有等价的引擎机制**——那两个引擎都只能注册自定义 scheme，`https` 是保留 scheme，所以指向虚拟主机的 URL 由库映射回本地文件（见文末「内嵌资源虚拟主机 URL 在 Linux/macOS 上白屏」）。
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

### Wayland 下无边框窗口不可拖动（2026-09-30 已修）

无边框拖动原来靠 JS 流式坐标 → 宿主 `gtk_window_move`；而 Wayland 协议不允许客户端自行移动窗口，
`gtk_window_move` 在 Wayland 下是空操作。**对照取证**：同一个 exe 强制 `GDK_BACKEND=x11` 后拖动正常
→ 坐实为"后端差异"而非"拖动链路缺陷"。

修法：按 GDK 后端分叉。X11 保留流式（`gtk_window_move`，已在真机验证过，不动它）；Wayland 改调
`gtk_window_begin_move_drag`，由 GDK 转成 `xdg_toplevel.move` 交给合成器——跟手、边缘吸附与贴边平铺
随之成为原生行为。此后宿主不该再插手：合成器接管时指针被 grab，页面收不到 `mousemove`，`DragTo` 直接
返回；顺带一提，`root_x`/`root_y` 在 Wayland 下也拿不到（协议没有全局坐标），能给的只有 `button`。

后端判据取 `gdk_display_get_name()` 的前缀（X11 形如 `:0`，Wayland 形如 `wayland-0`），并且刻意**不**看
环境变量：Wayland 会话里用 `GDK_BACKEND=x11` 时，X11 恰是我们想要的那一侧（那时客户端能自己摆窗口）。
判据本身是纯函数（`LinuxDragSupport.IsWaylandDisplay`）并带单测——分叉的依据不该只能靠真机验证。

**遗留风险（只能真机验证）**：`xdg_toplevel.move` 要求带一个有效 serial，GTK3 取的是按钮按下时记录的
隐式抓取 serial，所以这个调用必须落在鼠标按住期间。本库的拖动是"页面 JS → IPC → 宿主"，已经离开 GTK 的
事件处理栈——页面侧在 `mousedown` 里立刻发起（不像 Windows 那样等移动超过阈值）是把这段延迟压到最短的
做法，但它是否足够仍要看真机。另有一处副作用：Wayland 下页面收不到 `mouseup`，页面侧的拖动状态要等
下一次 `mousedown` 复位；宿主侧不受影响（`DragTo` 已是 no-op）。

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
  **2026-09-30 补充：这些警告与渲染无关**——同环境下未经加速的 GTK+WebKit 程序照常渲染；排查白屏时
  不要把它们当成线索（见「内嵌资源虚拟主机 URL 在 Linux/macOS 上白屏」）。

### 一处工具侧踩坑（非产品代码）

`pkill -f "WebKitWebProcess|WebKitNetworkProcess"` 会**匹配到承载该命令的 bash 自身**（其命令行里含同样字符串），
于是把执行环境一起杀掉、命令静默返回空输出（表现为"窗口没起来"）。查/杀进程一律用 `pgrep -x` / `pkill -x`
精确进程名，不要用会自匹配的 `-f` 模式。

## 内嵌资源虚拟主机 URL 在 Linux/macOS 上白屏（2026-09-30 修复）

**现象**：Linux 与 macOS 上启动 demo（默认打开手动验证台）**窗口一片空白**，连页面背景色都没有；
宿主侧没有任何异常、日志为空，进程存活、WebKit 子进程照常出现。而 `--todo` 与各 `--selftest` 完全正常。

**根因**：`UseEmbeddedAssets(host)` 把内嵌资源挂在虚拟主机 `https://app.oriel/` 下，demo 的手动验证台用
`WithUrl($"https://{host}/manual-check.html")` 指定页面。三个平台里**只有 Windows 有引擎级的虚拟主机机制**
（WebView2 的 `SetVirtualHostNameToFolderMapping`）；WebKitGTK 与 WKWebView 都只能注册**自定义** scheme，
而 `https` 是保留 scheme、注册不了。于是该 URL 变成一次**真实的网络请求**，DNS 解析失败后引擎渲染错误页——
就是那片空白。它没有任何宿主侧信号，因此从 `a649a1d`（引入手动验证台）起一直存在；`--todo` 与 `--selftest`
都不设 `Url`（走 `file://` 直读解压目录），正好把它挡住了。

**排查中被误导过的两件事**（记下来，避免下次重走）：

- WSLg 下没有 `/dev/dri`，stderr 稳定出现 `MESA: error: ZINK: failed to choose pdev` 与
  `libEGL warning: egl: failed to create dri2 screen`。看着像"渲染坏了"，但**与渲染无关**：同一环境下
  一个只调 `load_html` 的最小 GTK+WebKit 程序画得好好的。这些警告此后按**性能噪音**看待。
- 用一个**不经本库**的最小探针做对照，是这次唯一奏效的分诊手段——它把"环境"与"本库"一刀切开。
  在它之前我先后误判为"虚拟机 GL 问题"和"页面文件被改坏"，两次都错。

**修法**：新增 `AssetUrlResolver.TryResolveLocalFile(url, assetHost, assetDirectory)`（纯函数、带单测），
把命中虚拟主机的 URL 解析回解压目录里的本地文件。Linux 与 macOS 在 `Navigate()` 的"显式 `Url`"分支里先试
映射，命中则改用本地文件加载——macOS 走 `loadFileURL:allowingReadAccessToURL:`（读权限限定在资源目录内；
只把一个 `file://` 交给 `loadRequest:` 是不够的，同目录的 css/js 会被拦下）。Windows 不动，它本来就靠引擎映射。

不改调用方语义：同一个 URL 在三个平台上落到同一份资源。外部 URL（Vite dev server 等）原样加载；
host 必须**精确**匹配（`app.oriel.example.com` 这类不命中），解析出的路径必须仍在资源目录内（挡 `../` 与百分号编码）。

**验证**：解析器 22 个用例（host 精确匹配与大小写、http/https 同义、站点根、query/fragment、目录穿越与
`%2F`、文件不存在、无资源目录、非法 URL），全量单测 258 通过。WSL2 + WSLg 真机跑修复后的产物，默认模式
（手动验证台）**完整渲染**：标题、「已连接」徽章、环境面板（平台 linux / 托盘已创建 / 通知可用否 / 开机自启未启用）、
托盘与通知按钮、日志三行。macOS 为同一根因的同一修法，**尚未真机验证**（见 ROADMAP 的待真机清单）。

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

## 阶段 B：导航与页面通信（2026-09-29 实现并验证）

**结论**：导航（前进 / 后退 / 刷新 + 开始 / 完成 / 失败事件）与三条 IPC 通道（console 转发、页面
postMessage、宿主 EmitEvent）全部落地；Linux 上以**无人交互的自检**验证通过（`--selftest nav`、
`--selftest ipc`），并接入 CI 的三平台冒烟。

### 自检为什么写成事件驱动状态机，而不是 async/await

第一版自检写成 `await window.EvaluateJs(...)` 顺序流程，跳转到第二个页面之后**进程直接 abort**。
根因不在库：`await` 的续体落在**线程池**，而 GTK 只能在主线程上调用——从线程池调
`gtk_window_is_maximized` 之类就是崩溃。GLib/AppKit 不提供 .NET 的 SynchronizationContext，所以
"await 之后仍在 UI 线程"这个在 WinForms/WPF 里天经地义的假设，在这里不成立。

**修法（两层）**：

1. 自检改为事件驱动状态机，每一步都发生在 `NavigationCompleted` 回调里（= UI 线程）；
2. 库补上缺口——新增 `WebviewWindow.PostToUiThread(Action)`：跨 `await` 编排的应用靠它回到 UI 线程
   再碰窗口。自检的看门狗计时器回调就是它的用例（计时器在线程池触发，收尾动作必须切回 UI 线程）。

**教训**：跨平台 UI 库必须显式提供"回 UI 线程"的入口并写进文档，否则每个消费方都会各踩一次。

### 失败导航的构造：必须同源（一次被纠正的错误归因）

自检最后一步要验证"加载失败 → 上报 `Success=false` + `Error` 非空"。这一项前后改了三次：

1. 假域名 `https://oriel-selftest.invalid/`：一次通过（TLS 握手失败），下一次**挂到 TCP 超时**
   （90 秒看门狗先到）——同一台机器上都不稳定。
2. 环回 discard 端口 `http://127.0.0.1:9/index.html`：出现极反直觉的结果——注入的脚本与 `eval` 返回值
   都是该地址（已打印确认），但引擎**重新加载了首页并上报成功**，`failed` 一次都没发出。当时在 WSL 上，
   我把它归因为"该环境的既有行为"——**这个归因是错的**。
3. 换成"不存在的本地文件" `file:///nonexistent-…/page-xyz.html`：在 WSL 上通过，但**在 Windows 上重现了
   同样的问题**——第 6 步变成 `starting: https://app.oriel/index.html` / `completed: success=True`。
   两个引擎表现一致，说明问题出在我的构造方式上，而不是环境。

**真正的根因**：这几次都是从**当前页面的源**跳到**另一个协议 / 源**（`https://app.oriel/` → `file:///…`、
`file:///…` → `http://127.0.0.1:9/`）。跨协议 / 跨源的 `location` 变更会被引擎按安全策略处理成别的导航
（实测表现为"重新加载首页并上报成功"），而不是那个地址的加载失败。

**最终做法**：在页面里用**自身基址**拼一个不存在的相对路径——

```js
location.href = new URL('oriel-selftest-nonexistent-page.html', location.href).href
```

于是 Windows 上是 `https://app.oriel/oriel-selftest-nonexistent-page.html`（错误码
`COREWEBVIEW2_WEB_ERROR_STATUS_UNKNOWN`），Linux 上是
`file:///…/www/oriel-selftest-nonexistent-page.html`（`Error opening file …: No such file or directory`），
两边都稳定失败并带上错误信息。

**教训（两条）**：① 验证失败路径时，失败要由一个**同源且确定不存在**的目标制造；② 更重要的——**在一个
平台上得到的反直觉结论，不要急着归因给环境**。这次若不是坚持在 Windows 上再跑一遍，就会把一个真实的
构造缺陷当成"WSL 的怪癖"写进文档，还会把那个错误的做法固化进 CI。

### 追加：三个引擎接受的"失败目标"各不相同（同日，macOS 真机）

把自检接进 CI 之后，macOS 连续两轮都只失败一项——**第 6 步（失败导航）一个导航事件都没有**，
连 `starting` 都没发出（看门狗 90 秒兜底）。逐步排查的结果：

| 目标形式 | Windows（WebView2） | Linux（WebKitGTK） | macOS（WKWebView） |
|---|---|---|---|
| 跨协议 / 跨源绝对 URL（https 页面 → `file:///…`、file 页面 → `http://127.0.0.1:9/`） | 被改写成"重载首页并成功" | 同左 | — |
| 页面内 `location.href = new URL(相对路径, location.href).href` | 正常失败 | 正常失败 | **静默无响应**：无事件、无报错 |
| C# 拼的同源绝对 URL（指向不存在的文件） | 失败，`COREWEBVIEW2_WEB_ERROR_STATUS_UNKNOWN` | 失败，`Error opening file …: No such file or directory` | **静默无响应**：`loadFileURL:allowingReadAccessToURL:` 的沙箱内，导航到不存在的文件被吞掉 |
| 环回端口（`https://127.0.0.1:1/`） | — | — | 失败（CI run #18 通过） |

**结论**："用一个不可达地址制造导航失败"没有三平台通用的写法——每个引擎都有自己接受的形态，
也有它静默改写的形态（而且是**无报错**地改写）。自检因此不再赌单一目标，改为**候选列表逐个尝试**：
同源缺失页 → 环回端口 1 → 保留 TLD 域名，每个给 15 秒，任一候选真的上报失败即通过，每次尝试都打进日志；
被引擎改写成"成功"的候选会被记录并跳过，而不是当成"失败路径已验证"。

**教训**：跨引擎的行为断言若依赖某个"大家都应该这样"的细节，往往只在一个平台上成立。把不确定的部分
做成"多方案 + 打印过程"，比继续猜机制收敛得更快（这一处前后烧了 4 轮 CI，每轮约 4 分钟）。

## 阶段 C-1/C-2/C-3：剪贴板、系统主题、单实例（2026-09-29 实现并验证）

三项都落地，并有机器断言：`--selftest clipboard`、`--selftest theme`、单实例的双进程断言
（三平台 CI 都跑）。实现过程中有三处值得记下。

### 单实例：判定不能用"命名管道能否创建成功"

第一版用 `NamedPipeServerStream` 的创建失败来判定"已有实例"。Windows 上通过；**Linux 上两个进程
都以为自己 是首实例**——两边各自等对方，15 秒后双双打印"没有收到通知"。

原因：Unix 上 .NET 会**先删掉已存在的 socket 文件再绑定**，第二个实例于是也能"成功"创建。

改成**独占文件锁**（`FileShare.None`）判定：三平台语义一致（占用者存活期间第二次打开必然失败），
进程退出（含崩溃）由操作系统释放句柄，不留需要手工清理的陈旧状态。命名管道只留给首实例做
"接收通知"——那时它只有一个所有者，不会冲突。

### `onCreated` 阶段既拿不到应用、也拿不到后端

主题自检需要 `OrielApp`，而挂载点在 `AddWindow(configure, onCreated)` 的 `onCreated` 里。但
`onCreated` 是在 `CreateWindow` **内部**被调用的，而 `window.Attach(backend)` 要等它返回。于是：

- 用 `app` 变量：`app = builder.Build()` 的赋值尚未完成 → 实测读到 null；
- 用 `window.App`：窗口门面还没有后端，直接抛异常（fail-fast，进程静默退出、无任何输出）。

其它自检没踩到，是因为它们只做**事件订阅**——事件订阅不需要后端。
最终解法：**推迟到 `Loaded`**（那时后端已就位），并新增 `WebviewWindow.App`（经窗口反查应用，
本身也是常用能力）。

**教训**：`onCreated` 是"窗口对象已存在但门面尚未接好"的中间态；在这个回调里能做的只有订阅事件。

### Linux 剪贴板：写 HTML 时忘了声明文本 target

`gtk_clipboard_set_with_data` 的 target 列表里最初只放了 `text/html`，于是只认文本的应用（以及
我们自己的 `ClipboardText`）读不到回退内容——Windows/macOS 都写了回退，只有 Linux 漏了。
`CLIPBOARD-SELFTEST` 第一次跑就抓到了它（`FAIL：写 HTML 时应同时给出纯文本回退，实际读到 ""`）。

另一个坑：`GTK_THEME=Adwaita:dark` **不会**反映到 `gtk-theme-name` 或
`gtk-application-prefer-dark-theme` 属性上（实测两者都还是浅色的值），所以深色判定必须单独读这个
环境变量——它同时也是 CI 里"造两种值"的入口。

### 主题自检为什么要跑两次

`--selftest theme` 断言"宿主读到的主题"与"页面回显的主题"一致，这只证明**读得到 + 通道通**。
"深浅判得对"是另一回事，所以 Linux 上用 `GTK_THEME` 造值跑两次，并断言两次结论**不同**——
同一进程内改不了系统主题，只能这样造。

### Linux 失败路径的两个实现要点

1. **`load-failed` 之后必然还会来一次 `load-changed(FINISHED)`**（WebKitGTK 文档明确：错误页也要"加载完成"）。
   若照 FINISHED 上报成功，调用方会先收到"失败"、再收到一条自相矛盾的"成功"。实现用
   `_failedSinceLoadStart` 抑制那一趟，并在下一次 `STARTED` 时清除。
2. **GError 的 message 按结构偏移取**：`{ GQuark domain; gint code; gchar* message; }`（64 位平台偏移 8），
   不引入完整结构体映射——只需要这一个字段。实测取到的错误文本完整正确。

### console 转发：一份实现，注入时开关

原计划三平台各写一份（Windows 用 WebView2 的 console 事件，macOS/Linux 注入 hook），最终统一为
**注入 hook**：实现写在共用的桥接模板里，注入时把 `__ORIEL_CONSOLE_ENABLED__` 替换成 `true`/`false`，
未启用时代码保留但不执行。

- **为什么不用 WebView2 的 console 事件**：那要走 `CallDevToolsProtocolMethod` 或 DevTools 协议事件
  接收器，与另外两个平台是两套语义，也就无法用同一份 bridge 单测覆盖。
- **为什么默认关闭**：包装 console 会改变页面对它的可观测行为（`console.log.toString()` 不再是原生实现），
  且高频输出会变成持续的 IPC 流量。开发时用 `WithConsoleForwarding()` 打开。

### 验证账（Linux 真机 + CI）

- `--selftest nav`：跳转 → 后退 → 前进 → 刷新 → 失败，共 6 次导航；每次 `starting`/`completed` 与 URL
  都符合预期，失败那条带真实错误信息。
- `--selftest ipc`：console(`log`, `from-page 42`)、页面 postMessage(`{"n":1}`)、以及**闭环**——宿主
  `EmitEvent("from-host")` → 页面 `oriel.on` 收到 → 页面 `postMessage('echo')` → 宿主收回 `{"k":1}`。
- bridge 单测：新增 postMessage 与 console 转发用例（每平台 4 个），并把 console 桩注入脚本沙箱，
  避免包装到 Node 的全局 console 上而污染测试进程。
- CI：`smoke-linux` / `smoke-windows` 直接跑两个自检；`smoke-macos` 经 `tools/verify-macos.sh` 跑
  （必须在 .app bundle 内运行——WKWebView 是进程架构，宿主需要 bundle 身份）。自检失败会让该 job 失败。

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

## 托盘与通知：能力面、平台选择与验证法（2026-09-30）

参照 Ryn 0.38.0 的 `Ryn.Plugins.Tray` / `Ryn.Plugins.Notification` 补齐这批能力。**借它的能力面，
不借它的分发形态**：Ryn 是 DI 容器 + 12 个独立插件包；本库按既定决策保持单包、无 DI，
能力挂在 `OrielAppBuilder`（配置）与 `OrielApp` / `OrielTray`（运行期）上，
将来真要拆包时，这些扩展方法可以整体搬到 `OrielWeb.Plugins.*` 而调用方代码不变。

### 为什么菜单项类型只有一个、加速键解析也只有一个

托盘菜单、窗口上下文菜单的表达能力本来就相同（自定义项 / 平台 role / 分隔线 / 子菜单 / 勾选 / 禁用），
拆成两个类型只会把相同字段与校验抄两遍，所以共用 `OrielMenuItem`。
`OrielAccelerator` 同理：窗口上下文菜单与托盘菜单的加速键是同一套语法（`"CmdOrCtrl+Shift+A"`），
没有理由写两个解析器——它也是第一批就落地并带单测的原因。

### Linux 托盘：用 GTK3 自带的 `GtkStatusIcon`，而不是 AppIndicator 或纯 D-Bus

- **AppIndicator**：额外原生依赖，且会与 GTK 的进程级类型注册表打架（Ryn 正是因此放弃它）。
- **纯 D-Bus（`org.kde.StatusNotifierItem`）**：Ryn 走的路，但它依赖 `Tmds.DBus.Protocol` 这类客户端库或手写协议，
  与"零额外依赖"的定位冲突。
- **`GtkStatusIcon`**：libgtk-3 自带（在 GTK3 里标 deprecated 但可用），代价是**显示与否取决于宿主桌面**——
  GNOME Shell 默认不显示（需 AppIndicator 扩展）、Wayland 会话下多数合成器也不显示。
  这个代价不藏着：`OrielTray.IsVisible` 把"当前到底可不可见"暴露成可读状态
  （Linux 上是 `gtk_status_icon_is_embedded`；WSLg 取证里它是 `false`，属平台事实而非缺陷）。

### Windows 通知：托盘气球，而不是 WinRT Toast

- NativeAOT 下**没有 WinRT 投影**（Ryn 在同一处得出同一结论，并因此改走 PowerShell 子进程发 Toast）。
- "未打包应用的可点击 Toast"还要求开始菜单快捷方式携带 AUMID 并注册 COM 激活器——那是**打包器**的职责，
  不是库该做的（本库尚无打包器）。
- 托盘气球（`NIF_INFO`）只需一个图标句柄，且点击能可靠回调。
- 载体是**独立的隐藏托盘项**（`NIS_HIDDEN`），不是用户的托盘：否则"没启用托盘就发不了通知"会成为隐藏前提。

### Linux 通知：`notify-send` 子进程；macOS 通知：`osascript`

- libnotify 的 P/Invoke 能拿到点击/关闭回调，但要连引用计数与 GLib 信号一起管。这一批先交付"发得出去"，
  代价是拿不到点击。**（2026-10-01 更新：当时在 Linux/macOS 上做成"显式空实现"（`add {} remove {}`），
  而不是留一个看起来"忘了触发"的自动事件；后来整个能力被移除了——见文末
  「通知点击上报：直接删掉，而不是留一个空事件」。）**
- macOS 未打包运行时没有 `CFBundleIdentifier`，`UNUserNotificationCenter` 直接拒绝，`osascript` 是唯一可行路径
  （Ryn 同样分了"打包 / 未打包"两条路）。**转义是安全关键**：标题与正文来自应用（可能是页面数据），
  会拼进一段 AppleScript 源码——必须转义反斜杠与双引号，且用 argv 传参不经 shell。
- Linux 侧 `notify-send` 同理用 `ArgumentList` 逐项传参，标题里的分号、引号都不会变成命令注入。

### macOS：设了菜单之后 `Clicked` 不再触发

`statusItem.setMenu:` 之后系统不再派发按钮 action——这是 macOS 的惯例（左键点击即弹菜单），
不是实现遗漏。`OrielTrayOptions.MenuOnClick` 因此只在 Windows 上有意义（Linux 的 `popup-menu` 与 `activate`
是两个独立信号，两者都能收到）。

### 验证法：假通知服务 + `notify-send` 替身

无头 CI 里没有通知守护，通知"发出去了没有"本来无法断言。我们注册一个最小的
`org.freedesktop.Notifications` 服务（`tools/fake-notification-service.py`）顶替守护进程，
于是**真 `notify-send` → 会话总线 → 假服务**这条路能把"标题与正文逐字符正确"变成机器判定
（`tools/verify-linux-shell.sh`）。缺 libnotify 的环境再用 `tools/fake-notify-send.py` 补上最后那一步
D-Bus 调用（只补这一步，参数解析与真品对齐）。

两处实测踩到的坑，记下来省下一次：

- **`dbus.service.BusName` 必须保存引用**：不保存会被 GC，name 随之从总线上消失，
  调用方拿到的是 `ServiceUnknown: ... was not provided by any .service files`——
  看起来像"服务没起来"，实际是"name 被回收了"。现场查了 name owner 才定位到。
- **工作区里的 `.py` 可能是 CRLF**：shebang 变成 `#!/usr/bin/env python3\r`，内核找不到解释器
  （execve 返回 ENOENT）→ bash 回退去解释该文件 → 报出与真实原因毫不相干的语法错误。
  `.gitattributes` 的 `* text=auto eol=lf` 只在**提交往返**时规范化，工作区文件仍是写入时的行尾，
  所以本地直接执行会失败（`.sh` 没踩到是因为一律用 `bash x.sh` 调用，不看 shebang）。

### 这批能证明什么、不能证明什么

- **能**：托盘与通知的 API 通路可用、菜单能按各形态构建、通知投递的**内容**正确、缺客户端时如实报告"不支持"、
  三平台编译通过。
- **不能**：图标是否真的出现在托盘区、菜单的外观、通知横幅的展示与点击——都需要人眼。
  清单在 `docs/ROADMAP.md`，`tools/verify-linux-shell.sh` 的结论里也把这条边界写明。

## 菜单：role 集中解释，构建按平台抽取（2026-09-30）

窗口上下文菜单与托盘菜单的表达能力本来就相同（自定义项 / 平台 role / 分隔线 / 子菜单 /
勾选 / 禁用），于是做了一次合并而不是两处重复：

1. **role 的语义集中在 `OrielMenuRoles`**。三平台后端只负责"把菜单画出来"和"把选择报回来"；
   `quit` 退出应用、`close` 关窗口、`copy` 交给页面这类判断只写一遍。
   macOS 本可以让 AppKit 走响应链（nil target）白拿系统标准行为，但那样三平台的 role 行为会分叉，
   而响应链能做的恰好就是这几个动作——统一走自己解释器，代价只是默认文案要自己给（已由文案表覆盖）。
2. **菜单构建按平台各抽一个类**（`Win32Menu` / `GtkMenu` / `MacOSMenu`）。抽之前，
   托盘那一份构建代码已经在三个平台各写了一遍，再加一份上下文菜单就是六份。

两条实测得到的取舍：

- **弹出语义三平台不同，且写进了 API 文档**：Windows 的 `TrackPopupMenuEx` **阻塞**到用户选择
  （自带模态消息循环），macOS/Linux 是异步弹出。所以文档明确写"不要从需要立刻继续的路径里调用它"——
  三平台唯一的共同保证是"用户选中自定义项后会触发事件"，没有"调用返回时用户已选完"。
- **macOS 每个菜单自带 ObjC target**：点击时系统只回报"哪个 target 的哪个 tag 被点了"，
  tag 的编号空间天然属于单个菜单，映射放在菜单对象内部就不需要全局表，也不会两个菜单互相串味。
  位置来自 `NSApplication.currentEvent`；**没有当前事件时明确不弹**（记一条日志），
  因为 AppKit 此时无法定位，弹到随机位置比不弹更糟。

## 退出时必须撤销托盘图标（2026-09-30）

真机验证里发现"关掉应用后托盘图标还留在通知区"（幽灵图标）。根因不在托盘实现，而在**生命周期**：
`OrielApp.Run()` 里消息循环一结束就 `return`，**从来没有清理过**——托盘图标的 `NIM_DELETE` 只在
调用方显式 `Dispose()` 时才会发生，而 demo（以及任何按文档写的应用）都不会主动调它。

现在 `Run()` 在消息循环结束后自己调一次 `Dispose()`：

- **这是库的责任**：托盘图标是系统级资源，只属于本进程却由 explorer 持有；一旦进程退出而不撤销，
  它就留在通知区，用户只能把鼠标划过去等它自己消失。"记得 Dispose"不该是调用方的义务。
- **顺序**：`Dispose()` 里是"先托盘、后后端"——托盘的原生资源要靠后端所在的消息循环/主线程清理；
  而 Windows 托盘后端的专属消息窗口要等 `NIM_DELETE` 做完才销毁。
- **异常不外抛**：退出路径不该因为清理失败而崩，所以包了 try/catch 并记调试日志。
- **强杀仍会留图标**：`Environment.Exit`、崩溃、任务管理器结束进程都跳过了清理路径，
  这是进程模型的固有限制（除非另外挂 job object 或调试钩子）。这条写进 README 的边界说明。

## 一次 CI 失败的诊断：别被工作区的行尾带偏（2026-09-30）

`test (windows-latest)` 在快捷键那批之后失败，注解只有一句 `Process completed with exit code 1`
（拿不到步骤日志时，注解能给的就这么少）。本地复现的第一步是跑
`dotnet format --verify-no-changes`：它报了 46 条 `ENDOFLINE` 和 46 条 `WHITESPACE`。

很容易就此收工——"是 CRLF 惹的祸"，把工作区文件转成 LF 了事。**但转 LF 是无效操作**：
`git add` 早就把索引里的行尾规范化成 LF 了（git 每次提交都打印 "CRLF will be replaced by LF
the next time Git touches it"），CI 检出的从来就是 LF，**`ENDOFLINE` 只在本地工作区成立**。
判据是 `git diff` 有没有因此变化——实测转完一行 diff 都没有。

真正让 CI 失败的是那 46 条 `WHITESPACE`：macOS 的键码映射表我按列用空格对齐了，而
`.editorconfig` 要求一行一项。跑一次不带 `--verify-no-changes` 的 `dotnet format` 就修好了
（改动的正是那一张表），CI 随之转绿。

教训：**门禁工具的输出要按"哪些能在 CI 复现"过一遍**，而不是看到红字就动手。
工作区与索引的行尾差异属于"只在本地成立"的那一类——它会稳定地制造一次假诊断。

## 开机自启：落点选择与"延迟失败"的应对（2026-09-30）

- **三平台的落点**：Windows 写 `HKCU\...\Run` 的一个值（不需要管理员权限，而且"开机自启"本就是当前用户的
  偏好；不走"启动"文件夹的 `.lnk`——那要 COM 的 `IShellLink`，而这条只需一次 `RegSetValueExW`）；
  macOS 写 `~/Library/LaunchAgents/<id>.plist`；Linux 写 `$XDG_CONFIG_HOME/autostart/<id>.desktop`
  （freedesktop 约定，各主流桌面都遵守）。
- **macOS 只写 plist，不调用 `launchctl load`**：手动 load 会立刻把应用再拉起一遍（当前这个进程还在跑），
  那是用户没要求的副作用；位于 LaunchAgents 的 plist 会在下次登录时被 launchd 自动拾取。
  代价是"启用后要等下次登录才生效"，已写进 README。
- **`IsAutoStartEnabled` 读平台而不是内存标记**：用户可能在"任务管理器 → 启动"或系统设置里关掉它，
  也可能手工删了文件；标记留在内存里就会说谎。
- **配置文本抽成纯函数（`AutoStartContent`）**：自启项写错**不会当场失败**——它要等用户下次开机
  才发现应用没起来。这种"延迟失败"的格式只能靠逐字符断言，所以三段文本（desktop entry / plist /
  Run 命令行）都由可单测的纯函数生成，12 个单测覆盖引号、参数、XML 转义与 desktop 转义。
  这也是这批里唯一能做**硬断言**的项：Linux 侧读回文件逐项核对内容，加上三平台都成立的
  "启用 → 查得到 → 禁用 → 查不到"闭环。
- 顺带修掉一个自检里的顺序错误：第一版先 `DisableAutoStart()` 再去读文件内容，读到的自然是空——
  "读回"必须在"删除"之前，这条写进了自检的注释以免下次再犯。

## Shell 集成：白名单、argv 与"不做执行"（2026-09-30）

- **默认拒绝的 scheme 白名单**：只放行 `http`/`https`/`mailto`。这类 API 的风险不在"自己执行了什么"，
  而在**"它决定让别的程序去打开什么"**——未经校验的 `file:` 会被系统默认处理器以它自己的权限打开。
  裸路径一并拒绝（系统会把它当文件打开），含控制字符的也拒绝（换行会污染下游对命令行的解析）。
- **argv 逐项传递，不经 shell**：用 `ProcessStartInfo.ArgumentList` 而不是拼接命令行字符串，
  目标里的空格、引号、分号都不会变成第二个命令。
- **命令翻译抽成纯函数（`OrielShellCommand.Build`）**：三条系统命令各有怪癖——
  explorer 的 `/select,` 逗号后**不能有空格**（写成 `"/select, path"` 会被当成两个参数而静默失败）、
  `open` 用 `-R` 表示"显示而不打开"、`xdg-open` 根本没有"选中"这个入口（只能退一步打开父目录）。
  这些都是逐一踩过才知道的细节，所以让它可单测（6 个逐平台用例）。
- **不做 `shell.execute`/PTY**：Ryn 有那一层，但它属于能力沙箱的范畴（"哪些命令允许被执"是个策略问题，
  需要 scope + argv 校验一整套），与"跨平台 webview 核心库"的定位无关。
  需要执行进程的应用应当自己用 `System.Diagnostics.Process`——权限边界留在应用手里更清楚。
- **验证方式**：`xdg-open` 替身把参数记进文件，于是"真的交给了系统默认程序"与
  "危险目标一次都没调出去"都成了可断言的事实。后者是**安全边界的断言**，比"能用"更值得写。

## 对话框扩展：把平台差异收敛到纯函数里（2026-09-30）

在既有的「消息框 / 打开 / 保存」（单文件 + 字符串过滤器）之上补齐参照 Ryn 的能力面：
**多选、文件夹选择、结构化过滤器**。

### 为什么要有 `OrielFileFilter` 这个类型

三种平台对"过滤器"的表示完全不同，而它们之间的转换是**同一条信息换三种形状**：

| 平台 | 形状 |
|---|---|
| Win32 | `名称\0模式1;模式2\0…\0`（**双 null 结尾**） |
| GTK | 逐条 `gtk_file_filter_add_pattern`，多个模式就是多次调用 |
| Cocoa | 扩展名数组（`*.tar.gz` → `tar.gz`），且**没有**"任意文件"的写法 |

集中到 `OrielFileFilter` 的三个渲染方法之后，平台后端只剩"把数据搬进原生调用"。
好处不只是少写重复代码，而是**这些转换能在 Linux 的 CI 上被单测覆盖，包括为 Windows 写的那两个**——
对话框本身弹出后无法在无头环境断言，但转换规则可以，这一批的机器可判定部分全在这里。

顺带发现的三个平台事实（都已写成单测，省下将来的调试时间）：

- **Win32 的过滤器串必须是双 null 结尾**。少一个会读到越界；既有实现在此处曾"靠
  `StringToHGlobalUni` 自动补的终止符侥幸成立"——现在显式补足。
- **GTK 的 `*.*` 会漏掉无扩展名文件**。pattern 走 fnmatch 语义，`*.*` 要求文件名里**含点**，
  于是「所有文件」看不到 `README`、`Makefile`。必须归一成 `*`。
- **Cocoa 的 `*` 反而是限制**。表示"不限类型"的方式是**根本不设** `allowedFileTypes`；
  塞一个 `*` 进去会把面板锁成只能选无扩展名文件。所以 `CocoaExtensions` 会**剔除** `*` / `*.*`，
  调用方在结果为空时跳过设置。

### Win32 多选：缓冲区有两种形状，这是最容易写错的一处

`OFN_ALLOWMULTISELECT` 成功返回后，缓冲区的内容**按选中数量变形状**：

- 选中**一个**文件：整块就是完整路径 `C:\dir\file.txt`（单段）。
- 选中**多个**文件：第一段是目录，之后每段是一个文件名（`C:\dir\0a.txt\0b.txt\0\0`），要自己拼回去。

把单段结果当"目录 + 文件名"处理会得到一个空的文件名；把多段当完整路径则只拿到目录。
两条分支都写进了 `OrielFileDialogSupport.ParseWin32MultiSelect` 并各有单测。

还有一个**必须按长度读**的坑：多选时用 `Marshal.PtrToStringUni(ptr)` 会在**第一个 `\0` 处截断**，
只能读到目录段。必须用带长度的重载 `PtrToStringUni(ptr, 32768)` 把整块读出来再解析。

`OFN_EXPLORER` 也必须与 `OFN_ALLOWMULTISELECT` 同带：否则原生对话框按旧格式返回。

### 解析函数刻意不用 `System.IO.Path`

`ParseWin32MultiSelect` 与 `EnsureExtension` 全是手写字符串处理。理由与 `OrielShellPolicy.ParentDirectory`
同一处：这些函数要**在任意平台上被测试**（Windows 的多选解析必须能在 Linux 的 CI 上跑），
而 `Path` 的行为随平台变——`Path.Combine("C:\\dir", "a.txt")` 在 Linux 上会拼成 `C:\dir/a.txt`，
测试立刻失真。纯函数一旦调了平台相关 API，就不再纯。

### 为什么多选返回数组而不是 `null`

`ShowOpenFileDialog` 取消时返回**空数组**。多选下"没选"与"选了一个"的区分本来就在长度上，
用 `null` 还要额外区分三种情况（取消 / 没选 / 出错），而空数组在这些情形下语义一致：
**一个路径都没有**。旧的单文件重载仍返回 `string?`，行为不变。

### 打开与保存分成两个选项类型

不是共用一个大的选项类：两者能用的参数本来就不同（打开可多选但没有 `DefaultExtension`，
保存反之）。合成一个就得靠运行时忽略无关字段，调用方看不出"这个字段在这里没用"。

旧字符串写法（`"文本文件|*.txt|所有文件|*.*"`）继续可用——门面层的旧重载内部走
`OrielFileFilter.Parse`，所以**存量调用方一行都不用改**。

### Windows 的文件夹选择用老 API，并且不支持初始目录

`SHBrowseForFolderW` 是老的，但另一个选择 `IFileOpenDialog` + `FOS_PICKFOLDERS` 是 **COM 接口**——
本库没有 COM 互操作基础（NativeAOT 下要自己搭 vtable 或引入 ComWrappers），为一个文件夹对话框
引入那套机制不划算。代价是**设不了初始目录**（那需要一个 `BFFM_INITIALIZED` 回调），
代码里显式丢弃这个参数并注明，而不是假装支持。要补的话已记在 ROADMAP。

### 补扩展名：三平台不会替我们做同一件事

Windows 的原生对话框有 `lpstrDefExt` 会自动补；GTK 与 Cocoa 不会。
不补的话用户在 Linux/macOS 上存「报告」得到的是无扩展名文件，双击时系统不知道该用什么程序打开。
所以 GTK 侧走 `OrielFileDialogSupport.EnsureExtension`（纯函数、有单测，含"目录名里的点不算扩展名"
这个边界）。

### 这一批能证明什么、不能证明什么

- **能**：过滤器的三种平台形状转换正确、Win32 多选缓冲区的两种形状解析正确、扩展名补齐正确、
  三平台编译通过、既有能力无回归（`tools/verify-linux-shell.sh` 仍全绿）。
- **不能**：对话框的样子、多选交互、过滤器下拉的内容——都要人眼。
  对话框**不进自检**：它弹出后会一直等用户操作，在无头环境里只会把自检挂住。
  清单在 `docs/ROADMAP.md`。

## 内建右键菜单：默认只留剪切/复制/粘贴（2026-09-30）

需求原话是"默认过滤右键菜单，仅保留剪切、复制、粘贴"。

### 为什么是这三项，而不是全留或全禁

内建菜单默认带着「后退 / 前进 / 刷新 / 另存为 / 打印 / 检查元素」。对应用窗口来说多数是噪音，
而且有几项**会造成真实损失**：

- **刷新**在单页应用里等于丢掉整页状态（用户填了一半的表单、正在编辑的内容）；
- **另存为**存下来的是一份孤立的 HTML 外壳——它引用的脚本与样式不在里面；
- **后退**会跑出应用自己的路由（页面路由跟浏览器的会话历史不是一回事）。

留下的三项则是**只有原生侧能给**的能力：粘贴要把系统剪贴板的内容送进页面编辑区，
而页面自己做不到（`document.execCommand('paste')` 在现代浏览器里被禁用），
原生菜单项的行为由渲染引擎直接完成。**这决定了实现方式**：不能"禁掉默认菜单、自己画一个"，
必须**改内建菜单本身**——另造一个菜单就失去了这三项的真实行为。

### 三平台各自的钩子

| 平台 | 钩子 | 关键点 |
|---|---|---|
| Windows | `CoreWebView2.ContextMenuRequested` | 事件在 `ICoreWebView2_11` 上，但 **WebView2Aot 包内部已完成接口转换**，不需要自己 QueryInterface。过滤后菜单仍由 WebView2 弹 |
| macOS | `WKUIDelegate` 的 `webView:willOpenMenu:withEvent:` | 拿到的是现成的 `NSMenu`（`v@:@@@`，**三个**对象参数）。此前项目没有任何 UIDelegate，为此新建了一个类 |
| Linux | `context-menu` 信号 | 语义与另两个平台**相反**：返回 `TRUE` 表示"应用自己接管"，WebKit 反而什么都不弹。要的是 `FALSE`——让 WebKit 用它自己的、已被我们改过项的菜单去弹 |

三处都没有复用现有的 `Win32Menu`/`GtkMenu`/`MacOSMenu`——那三个类只做"构造并弹出自己的菜单"，
没有遍历/删除能力，而这里要操作的是**别人构造好的**菜单对象。

macOS 侧同样**不碰 `WKWebView` 的方法表**（与拖放同一个理由）：`class_addMethod` 加不上
（WebKit 已实现那些 selector），`method_setImplementation` 又会替换掉 WebKit 的全局实现。
委托才是标准扩展点。

### 判断"这是哪一项"：三平台用的都是**未本地化**的标识

这是本功能最容易写错的地方——三平台各有一套"给代码看的标识"和"给用户看的文本"，
拿后者做判断就会在换语言时静默失效：

| 平台 | 该用 | 不该用 |
|---|---|---|
| Windows | `Name`（如 `"copy"`，未本地化的英文小驼峰） | `Label`（中文环境下是「复制」） |
| macOS | `identifier`（`WKMenuItemIdentifierCopy`） | `title`（中文环境下是「拷贝」） |
| Linux | `stock action` 编号（`WebKitContextMenuAction`） | 项的文字 |

顺带纠正一个**我一开始就认错的维度**：Windows 的 `COREWEBVIEW2_CONTEXT_MENU_ITEM_KIND`
说的是**控件种类**（Command / CheckBox / Radio / Separator / Submenu），不是"是不是复制"。
拿它做判断会编译通过但语义完全错——这类错误编译器帮不上忙。

### 为什么把名单抽成纯函数

Cocoa 的字符串与 WebKitGTK 的整数在 C# 里都**没有编译期检查**：写错了不报错，
只表现为"右键菜单里多一项或少一项"，而且要人眼去看。所以它们被集中到
`OrielContextMenuSupport`，于是这些判断能在 Linux 的 CI 上被单测覆盖，**包括为 Windows 与 Cocoa 写的那两组**。

48 个用例里，最有价值的是两类「相邻/相似」陷阱：

- `copyImage` / `copyLink` / `copyImageUrl`——都以 `copy` 开头，但不是"复制选区"；
- WebKitGTK 的 `13`（RELOAD）与 `17`（DELETE）——**就贴在** `14/15/16` 两侧，编号数错一位就会把
  「刷新」或「删除」留在菜单里。两个边界值因此被显式写成用例。

`Name` 的比较保持**大小写敏感**（官方口径是小驼峰）：若平台哪天改成 `"Copy"`，测试会立刻红，
而不是静默地"看起来也能用"。

### 默认值改了行为，这是有意的

在加这个功能之前，内建菜单是**原样弹出**的（各平台的默认）。改成默认 `Editing` 是一个**行为变更**——
但正是需求要的"默认过滤"。需要原样的调用方一个属性就能改回去（`Native`），
运行时也能随时切（过滤发生在每次弹出时，不缓存策略）。

### 这一批能证明什么、不能证明什么

- **能**：三平台保留名单的正确性（含相邻边界与相似名称的陷阱）、三平台编译通过、既有能力无回归。
- **不能**：菜单弹出后实际剩下哪几项、菜单的样子、以及"剪切/复制/粘贴是否真的作用于页面选区"。
  这三件事都要人眼在真机上点一次。清单在 `docs/ROADMAP.md`。

> **同日晚些时候被取代**：上面这套「就地增删引擎菜单」的实现在 WebKitGTK 4.1 上会破坏内存
> （连点几次右键即崩溃），已整体换成「自己弹菜单」的接管式。macOS 一并改了，Windows 保留过滤式。
> 见下一节。

## 内建右键菜单：改用「接管式」（2026-09-30）

上一节的做法（就地增删渲染引擎构造好的菜单）在 **WebKitGTK 4.1** 上会破坏内存：Linux 上连点几次
右键，进程就以 `double free` / 栈保护被破坏 / 段错误退出。这一节记的是换成「自己弹菜单」的原因与代价。

### 症状与定位

机器侧稳定复现：连点右键，**第 3～4 次**必崩。三种报错形式都出现过（随环境与内存布局变化）：

```
free(): double free detected in tcache 2
*** stack smashing detected ***: terminated
Segmentation fault (core dumped)
```

分层二分（每轮连点右键 5～6 次）把范围压到最小：

| 改动 | 结果 |
|---|---|
| `Native`：完全不碰菜单对象 | **6 次全存活** |
| 只调 `webkit_context_menu_get_items`，不用返回值 | **6 次全存活** |
| 遍历 + `g_list_free`（不 remove） | 第 3 次崩 |
| 遍历 + `stock_action` + `g_list_free` | 第 3 次崩 |
| 遍历 + `stock_action` + `remove`（不 free） | 第 4 次崩 |

结论：**只要真的去读/释放/移除引擎的菜单项就会崩，只拿到链表的头而不使用它是安全的**。
也就是说 `webkit_context_menu_get_items` 返回的那个 `GList` 在本版本 WebKitGTK 上**不是**
"归调用方所有的容器副本"——`g_list_free` 释放的是引擎的内部节点，`webkit_context_menu_remove` 同理。
这不是我们某一行写错，是这条路本身不该走。

（另有一个信号：`webkit_context_menu_item_get_stock_action` 自 WebKitGTK 2.24 起已废弃，
那份"编号 → 是不是编辑项"的对应表也失去了可靠性。）

> 定位过程中一度被误导：在 `GDK_BACKEND=x11` 下**不做任何操作**也会崩（撞在 .NET GC 惰性初始化
> 读 cgroup 的 `fclose` 上），据此以为与右键无关。经确认那是同一处内存破坏的另一个触发点
> （谁先碰到谁崩），而用户报告的是"操作右键/拖动才崩"——按真实操作路径复现后立刻对上。

### 新做法：自己弹菜单，编辑命令走引擎 API

`Editing` 策略下不再改引擎的菜单，而是**接管**：

- **Linux**：`context-menu` 信号的 trampoline **返回 TRUE**（"我自己弹，你别弹"），
  宿主用已有的 `GtkMenu` 弹一个只含剪切/复制/粘贴的菜单。
- **macOS**：`willOpenMenu:` 没有"拒绝弹出"的返回值，所以接管方式是 `removeAllItems` 之后
  换上我们自己的三项——action 用 AppKit 的标准 selector、target 留空，选择时由响应链执行，
  顺带白拿 Cmd+X/C/V。
- **Windows 不动**：WebView2 没有公开的 cut/copy/paste 编程接口，自建菜单的编辑项只能退回
  `document.execCommand`，粘贴会失效。它保留过滤式（保留下来的项由引擎自己执行），这条路本来就通。

编辑命令必须走**引擎的原生通道**，不能退回 `execCommand`：

| 平台 | 原生通道 |
|---|---|
| Linux | `webkit_web_view_execute_editing_command(webview, "Cut"/"Copy"/"Paste")` |
| macOS | `NSMenuItem` 的 `cut:` / `copy:` / `paste:`，经 `sendAction:to:from:`（target = nil）走响应链 |

为此给 `OrielMenuRoles.TryActivate` 加了一个可选的 `nativeEditing` 通道：编辑类 role 先交给平台，
平台不认识才退回 `execCommand`。`OrielMenuRoles.IsEditingRole` 与 `EditingMenuItems()` 集中定义
"哪些 role 算编辑类""接管菜单该弹哪三项"，两个后端都从那里取。

### 顺带修掉的两个缺陷

接管式第一次跑通时日志里有两个警告，都是 `GtkMenu` 自身的问题，一并修了：

- `A floating object was finalized` —— `gtk_menu_new()` 返回的是 **floating 引用**
  （GTK widget 都继承 `GInitiallyUnowned`）。顶层菜单没有父容器替我们 sink，直接 `g_object_unref`
  会打乱引用计数，连续弹几次就可能双重销毁。现在在 `GtkMenu.Build` 里 `g_object_ref_sink` 取走它
  （子菜单不在此列，`set_submenu` 会 sink）。
- `no trigger event for menu popup` / `gtk_menu_popup_at_rect` 断言失败 —— 在信号处理器里
  `gtk_menu_popup_at_pointer(menu, NULL)` 拿不到触发事件。信号本身就带 `GdkEvent`，
  现在把它一路传到 `GtkMenu.Popup(triggerEvent)`。

### 删掉的东西

`OrielContextMenuSupport` 里为 Cocoa（`identifier` 常量）与 WebKitGTK（`stock action` 编号）
准备的两张识别表连同它们的单测一起删了——接管式不再需要"认出引擎的菜单项是哪一项"。
只剩 WebView2 那张（Windows 仍在过滤）。这顺带消掉了那两个"没有编译期检查、只能靠人眼"的风险点。

### 这一批能证明什么、不能证明什么

- **能**（Linux，WSLg + GTK3）：
  - 连点右键 6 次不崩，stderr 无任何警告；
  - 菜单确实弹出——X 树里出现 `102x83` 的菜单窗口，位置就是指针位置，尺寸与三项吻合；
  - `webkit_web_view_execute_editing_command(view, "Copy")` 真的把页面选中内容送进了系统剪贴板
    （C 探针实测：先 JS 聚焦选中，再执行命令，最后从剪贴板读回 `ORIEL-EDITING-PROBE`）；
  - 247 个单测通过，其中内建右键菜单相关 37 个（含接管式菜单内容与 role 归类的新用例）。
- **不能**：macOS 侧的一切（本机没有 macOS）；Windows 侧未改动，行为同前。
  接管式菜单在真机上的外观与整链路交互仍需人眼，清单在 `docs/ROADMAP.md`。

## 文件拖放：三平台各走哪条路，以及为什么这条最省（2026-09-30）

能力面：把外部文件拖进窗口 → 得到**本地路径列表**（`window.FileDropped`）。
参照对象是 Tauri 的 `onDragDropEvent`；Ryn 没有拖放插件，所以没有可抄的实现。

### Windows：`WS_EX_ACCEPTFILES` + `WM_DROPFILES`，而不是自建 OLE `IDropTarget`

这是既有决策（**Composition 宿主**）带来的红利。因为窗口用 `WS_EX_NOREDIRECTIONBITMAP` 创建、
WebView2 通过 DirectComposition 合成进来（**不是子窗口**），所以没有子 HWND 抢走拖放——
父窗口自己收 `WM_DROPFILES` 就够了。对比之下：

- 自建 `IDropTarget` + `RegisterDragDrop`：要 `OleInitialize`、要手写 COM 接口（本库没有 COM 互操作基础），
  而且**同一 HWND 只能注册一个 drop target**，与 WebView2 可能的内建注册相争。
- WebView2 的拖放事件（`CoreWebView2CompositionController` 上的那些）：能拿到拖放数据，
  但数据形状意味着还要再解析一层，而且它把外部拖放**默认接管**过去。

**代价与配套**：必须把 WebView2 的 `AllowExternalDrop` 设为 `false`，否则拖放会被它接走、而且页面
拿不到路径。这个成员在 `ICoreWebView2Controller4` 上，老运行时没有——因此写成可选增强（转换失败即跳过），
跳过时 `WM_DROPFILES` 这条路仍然可用（不搞"运行时版本决定行为"的静默降级）。

### macOS：自定义容器视图承载 `NSDraggingDestination`

拖放协议方法必须由**注册了 dragged types 的那个 view** 实现。给 `WKWebView` 加方法有两种写法，
都不可取：`class_addMethod` 会失败（WebKit 已经实现了这些 selector），`method_setImplementation`
则会**替换掉 WebKit 自己的拖放实现**——那是全局的、影响所有实例的改动。

于是插一层自己的 `NSView` 子类：`contentView → OrielDropView → WKWebView`，
`registerForDraggedTypes:@["public.file-url"]` 设在容器上。AppKit 查找拖放目标时会**沿父视图链向上**
找第一个注册过类型的 view，所以拖到页面区域（命中 webview）也能被容器收到。webview 的类完全没被碰。

取路径用 `draggingPasteboard.readObjectsForClasses:@[NSURL.class]` 而不是废弃的
`propertyListForType:@"NSFilenamesPboardType"`（后者在新系统里不保证还有提供方写出）。
拿的是 NSURL 的 `path` 而不是 `absoluteString`——后者带 `file://` 前缀与百分号编码。

### Linux：落点设在 webview 上

`gtk_drag_dest_set(webview, GTK_DEST_DEFAULT_ALL, targets("text/uri-list"), 1, GDK_ACTION_COPY)`
配 `drag-data-received` 信号。设在 webview（而不是窗口）上：它铺满客户区，两处都设会产生两份事件。
无论成败都要调 `gtk_drag_finish`——不调的话源端（文件管理器）会一直等结果，表现为"拖完卡住"。

### URI → 本地路径：手写解析，不用 `System.Uri.LocalPath`

同一个 `file:///C:/x.txt` 在 Windows 上给 `C:\x.txt`、在 Linux 上给 `/C:/x.txt`。
于是"为 Windows 写的解析"没法在 Linux 的 CI 上验证——这已经是本仓库第三次踩同一个坑
（前两次是 `OrielShellPolicy.ParentDirectory` 与对话框的 `ParseWin32MultiSelect`）。
手写解析虽然啰嗦，但结果与运行平台无关，因此 **23 个用例全都能在 Linux CI 上跑**。

顺带钉住的两个细节：百分号解码**不**把 `+` 当空格（那是 form-urlencoded 的规则，混了会得到
"文件名里明明有加号、读出来却没有"的怪现象）；`file://server/share` 是网络路径，如实返回 null
而不是拼出一个看着像本地路径的串。

### 为什么不暴露落点坐标

三平台的坐标系与 y 轴方向都不同（Cocoa 原点在左下、GTK 的 y 轴向下、Win32 还要算进 DPI 缩放），
要一致就得再引入一层换算并逐平台验证。而拖放最常见的用途是"导入文件"，用不到落点；
需要视觉反馈的场合，页面自己的 `dragover` 就能拿到位置——**路径才必须来自原生侧**（浏览器的安全模型
不给页面文件路径）。所以落点坐标留在 ROADMAP，先把能确定的部分交付。

### 这一批能证明什么、不能证明什么

- **能**：URI → 本地路径的全部边界（23 个用例，跨平台可跑）、窗口创建时的落点注册不崩、
  事件订阅通路可用。
- **不能**：**真实拖拽**。它在 Linux 是 XDND 协议交互、在 Windows 是 OLE 拖放会话，
  都不是"注入一个事件"能模拟的——`xdotool` 能模拟鼠标，但造不出一个带 FileList 的拖放源。
  所以这一项**整体不声称已验证**，如实列进 ROADMAP 的待真机清单；自检里也只打印
  `FILE-DROP-SUBSCRIBED` 这一条能确定的事实。

## 手动验证：一个停在那里的操作台（2026-09-30）

CI 能断言的东西与人眼要确认的东西是两批。为此 demo 加了一个 `--manual-check` 模式：
把每项能力做成页面按钮，并把**托管侧的回调**（托盘菜单项、通知点击、拖放路径）
推回页面显示。之所以回显而不是打印：demo 在 Windows 上是 `WinExe`，**没有控制台**，
自检的 stdout 在那儿根本看不见——而这一批恰好全是"操作了才有回调"的东西，
看不见回调就等于没验证。

与 `--selftest shell` 的分工：那个是**无人自检**（跑完即退，给 CI），这个是**给人看的**（停在那里等你点）。

### 通知：把"提交成功"与"用户看见"分开

`ShowNotification` 现在返回 `bool`，语义是**已成功提交给系统**——不等于用户看见了。
这是被实际验证逼出来的：横幅没出现时，"没发出去"与"发出去了但被系统挡住"是两种完全不同的原因，
没有这个返回值就只能靠猜。Windows 回传 `Shell_NotifyIconW(NIM_MODIFY)` 的结果，
Linux/macOS 回传子进程退出码（没有通知守护时 `notify-send` 会以非 0 退出）。

同时修掉一个真 bug：`NOTIFYICONDATA.uTimeoutOrVersion` 是联合体，语义由 `NIM_SETVERSION` 决定。
我们已把该图标设成 V4，却仍往那个成员里填 `10000`（当成"停留 10 秒"）——在 V4 下它被读作**版本号**，
等于告诉 shell"版本 10000"，这种自相矛盾的值很可能让整条通知被丢掉。现在填 `NOTIFYICON_VERSION_4`。

### 布局问题别靠推理，用真实浏览器量

操作台第一次交付时"滚不动、看不到日志"，我按 flex 的常见成因（子项 `min-height: auto`）加了
`min-height: 0`——**没生效**。真正的原因在更上一层：`styles.css` 里 `body` 只有 `min-height: 100vh`，
内容一多 body 就跟着长高，而它又是 `overflow: hidden`，于是超出的部分被直接裁掉、
祖先链高度始终不受约束 → 谁来都滚不了。

定案的手段是**用 Edge 无头模式截一张图**
（`msedge --headless=new --screenshot=… --window-size=1024,720 file:///…`）：
图里日志区根本不在视口内、也没有滚动条，一眼就能看出"不是滚动容器的问题，是整个页面被裁了"。
涉及真实渲染的问题，量一次比推十次快——这条对以后调布局同样适用。

（顺带记一个坑：Windows 上 `dotnet build` 的输出是**本地化**的，`Select-String "Build succeeded"`
匹配不到中文的"已成功生成"，会得到空输出。空输出必须当成"没验证过"，不能当成通过。）

### 托盘右键：V4 下它不在窗口消息里（一个真 bug）

手动验证时"托盘图标点不出菜单"暴露了一处实现错误。原实现只在**调度窗口的窗口过程**里等
`WM_CONTEXTMENU`——那是**旧式**托盘的送法。本库用的是 `NOTIFYICON_VERSION_4`，
而 V4 起所有托盘事件都经 `uCallbackMessage` 送达，右键的 `wParam` **就是** `WM_CONTEXTMENU`
（`0x007B`），窗口根本收不到那条消息。于是右键永远没人处理，菜单自然弹不出来。
修法是在 `Win32TrayBackend.HandleCallback` 里按事件类型分派；窗口过程里那条留着兼容旧模式，
但注释已标明它不是主路径。

顺带注意：`TrackPopupMenuEx` 之前要 `SetForegroundWindow`、之后补一条 `WM_NULL` 收尾——
这两处早就在 `Win32Menu.Popup` 里做了，所以问题不在"菜单弹得不对"，而在"根本没弹"。
**先分清"没触发"与"触发了但画错"，能省一半排查时间。**

### 日志区把操作区挤没：`flex-basis` 别给百分比或 auto

同一次验证里，操作台"日志一多就把上面的按钮挤没"。这次没再按 flex 的常见成因去猜，
而是做了个**探针页**：复制操作台页 + 注入假 `oriel` + 灌 60 条日志 + 把实测尺寸画在页面上，
再用 Edge 无头截图读数。数字一眼定案：

```
scroll   = 12 (内容 1393)     ← 操作区被压成 12px
logpanel = 1627               ← 日志区撑到 1627px
```

根因是 `flex: 0 0 34%`：**百分比 basis 在祖先高度不确定时会退回 `auto`（= 内容高度）**，
于是日志越多、日志区越高。改成 `flex: 3 1 0` / `flex: 2 1 0`（basis 0 + 按 grow 比例分），
并给日志容器显式 `min-height: 0`（flex 子项默认 `min-height: auto` 表示"不小于内容"，
这是"容器被内容撑开"的另一个入口）。改后同一位置的数字变成
`scroll = 266 (内容 1393)`、`logpanel = 197`——60 条日志下操作区纹丝不动。

**教训**：布局问题别靠推理，量一次比推十次快。看不见页面的时候，探针页 + 截图能把
"我以为的布局"变成"实际的尺寸"。

### 自启：`RegSetValueEx` 没有子键参数（值被写到 HKCU 根下）

手动验证里"启用返回 true、紧接着查询返回 false"——这是**最省事的 bug 形状**：两个返回值直接
把矛盾摆出来了。根因是 Win32 声明写错：`RegSetValueEx(HKEY, lpValueName, …)` 的第二个参数是
**值名**，不是子键路径（它根本没有子键参数，子键必须先 `RegCreateKeyEx` 打开）。我们却把
`"Software\Microsoft\Windows\CurrentVersion\Run"` 当值名传了进去，于是：

- 写入**成功**（值名可以是任意字符串），`Enable` 如实返回 true；
- 值落在 **HKCU 根**下，名字是那个路径字符串；
- 查询走 `RegGetValueW`——它**确实**有子键参数——去正确的 Run 键下找 id，永远找不到 → false。

真机核对证实了这一点：HKCU 根下躺着那个错值（内容正是 `OrielDemo.exe" --minimized`），
而真正的 Run 键里没有 OrielDemo。另外 `RegDeleteValueW` 的声明也多了一个参数
（真实 API 是 `(HKEY, valueName)`），所以 `Disable` 删的是同一个错值而非自启项。

两处声明都已改正，补齐 `RegCreateKeyExW` / `RegCloseKey`，用户机器上那个错值也清掉了。

**教训**：把错误命名的参数当好名字用，编译器不会拦。这个声明里参数叫 `subKey`，
实际却是 `lpValueName`——**声明里的参数名必须与平台文档一致**，否则调用方照着名字就会传错东西。
另外这个 bug 在 Linux 上永远测不出来（那边走 `LinuxAutoStart`），只能被真机手动验证抓到。

### 托盘回调：不要假定 `NIM_SETVERSION` 生效

"托盘图标点不出菜单"在修了一处之后**依然复现**。上一轮修的是"右键事件不在窗口消息里"（那确实是
一个 bug），但没解释为什么还是不行。真正的盲区是：**V4 是否生效**取决于 `NIM_SETVERSION` 的返回，
而那个返回值一直被忽略。一旦它失败，事件的形态就是旧式的——`wParam` 是图标 ID、
事件类型跑到 `lParam` 里（`WM_LBUTTONUP` / `WM_RBUTTONUP`），于是只认 V4 的分派逻辑
**一个分支都匹配不上**，左键与右键一起静默落空。

现在两种形态都认：`wParam == 本托盘 ID` 即旧式，否则按 V4 解读。`NIM_SETVERSION` 也改成复用
`NIM_ADD` 的 flags（官方示例就是这么写的）并检查返回值。

为了让这类问题下次能一次定位，加了 `OrielTray.RawEvent`：把原始 `wParam`/`lParam` 与解析出的
事件类型报出来。"点了没反应"的三种可能——事件没送达 / 送达了没识别 / 识别了但菜单没弹出——
靠它就能分开，在此之前只能靠猜。

### 真正的根因：托盘回调的窗口与 WebView2 共享消息空间

`RawEvent` 一上就出结果了，而且是意料之外的那种：日志里刷出**大量**"托盘事件"，形如

```
wParam=0x42A0567 lParam=0x10200     ← lParam 低字 0x200 是 WM_MOUSEMOVE
wParam=0x0       lParam=0x10407     ← lParam 低字 0x407 是 WM_USER+7
```

`wParam` 像指针、还在连续变化，`lParam` 里是鼠标消息——**这些根本不是托盘回调**。
原因是：托盘的回调窗口一直借用**平台的调度窗口**，而那个窗口的消息空间是**与 WebView2 共享**的。
WebView2 的合成宿主自己也在用 `WM_APP` 范围的私有消息，于是我们自选的 `WM_APP + 2`
与它撞在一起：真正的托盘回调淹没在一堆无关消息里，左键右键都匹配不上。

**参考实现 Ryn 的做法正是给托盘单开一个窗口**（自己注册窗口类、自己建窗、自己的消息循环），
消息号仍用 `WM_APP + n`，但只在**自己的**窗口上，不与任何第三方组件共享。本次照做：
新增 `Win32MessageWindow`（不可见的普通窗口——**不能**用 message-only 窗口，
因为弹菜单前要 `SetForegroundWindow`，而 message-only 窗口无法成为前台窗口）。

顺带按 Ryn 停在了**旧式回调**上（不调 `NIM_SETVERSION`）：形态简单可靠
（`wParam` = 图标 ID、`lParam` 低字 = 鼠标消息），而且避开了 `uTimeoutOrVersion` 那个联合体的
二义性——不碰版本设置，这个字段的语义就始终是它字面上的意思。

### 通知也照 Ryn 重做：PowerShell + WinRT toast

原来的托盘气球（`NIF_INFO`）有两个问题：一要求托盘图标存在，二在实测里**根本没显示出来**
（换来的是能回传点击，但看不见的通知谈不上"能用"）。Ryn 走的
PowerShell + `ToastNotificationManager` 被它的注释称为 "reliable for any app"，本次照搬。

代价是**拿不到点击激活**：未打包应用的 toast 激活需要开始菜单快捷方式携带 AUMID
并注册 COM 激活器，那是打包器的职责（Ryn 也把这一项列为已知缺口）。因此三平台在这一点上如实一致——
都拿不到点击。**（2026-10-01 更新：当时做成了"显式空实现"，后来整个能力被移除了，
理由见文末「通知点击上报：直接删掉，而不是留一个空事件」。）**

**教训**：与第三方组件（尤其是 WebView2 这种"住进你窗口里"的组件）共享窗口时，
`WM_APP + n` 这种"看起来安全"的自选消息号并不安全。要么用 `RegisterWindowMessage`，
要么（更简单）**给需要回调的组件一个自己的窗口**。

## IPC 的 JSON 上下文：从全局静态改为按应用实例持有（2026-10-01）

`OrielJson` 原来用一个 `private static JsonSerializerContext? s_context` 保存应用注册的 STJ 上下文
（`OrielJson.Use(...)` 写入，`OrielAppBuilder.UseJsonContext(...)` 是它唯一的调用点）。
这在"一个进程一个应用"下看不出问题，但同一进程里创建第二个应用时，后者会**覆盖**前者要用的上下文，
而且**不会报错**——只是静默地拿错类型信息（表现可能是 DTO 反序列化出错误结果，或莫名其妙地报
"类型未注册"）。测试、以及"一个进程托管多个窗口宿主"的场景都会踩到。

### 改法：上下文随调用显式传递，不落任何静态字段

- `OrielJson` 里需要上下文的入口（`GetRequiredArg<T>` / `GetOptionalArg<T>` / `WriteResult`）各多收一个
  `JsonSerializerContext?` 参数。只做 JSON 元素层面校验的 `RequireArgElement` / `TryGetArgElement` /
  `RequireArgKind` **不需要**，保持原样——它们是"直出路径"，本来就与上下文无关。
- `IOrielCommandRouter.InvokeAsync` 增加 `JsonSerializerContext?` 参数，由 `OrielCommandDispatcher`
  （每个 `OrielApp` 一个实例）透传；分发器在构造时从 `OrielAppBuilder.JsonContext` 取到它。
- `OrielJson.Use(JsonSerializerContext)` 与那个静态字段**一并删除**。删掉而不是留个空壳：
  留一个不生效的 `Use` 会让调用方以为注册成功了、实际什么都没发生——**编译错误才是对的信号**。

### 取舍

- **代价**：`OrielJson` 的三个方法签名变了，`IOrielCommandRouter` 也变了。前者虽是 `public`，
  但它的公开性只是"生成器要在消费方程序集里调用它"的技术需要，不是面向使用者的 API——
  README 里从头到尾只出现 `UseJsonContext`，没出现过 `OrielJson.Use`。
- **顺带的收益**：`OrielJsonTests` 与 `DispatcherTests` 原来因为共享这个静态字段必须放进同一个
  xUnit 集合串行执行，现在可以并行。
- 基元类型不需要上下文这一点被**显式测了**（传 `null` 也能用）：否则很容易在重构里把
  "没有上下文"错误地升级成"所有命令都必须有上下文"，那是把可用性白白收紧。

### 这一批能证明什么、不能证明什么

- 能证明：DTO 命令在两个上下文不同的分发器上行为互不干扰；无上下文时分发器仍能服务基元命令；
  生成器产出的路由确实带上了新参数（导出生成代码核对过
  `InvokeAsync(int index, object? target, JsonElement args, JsonSerializerContext? jsonContext)`）。
- 不能证明：真实进程里同时跑两个 `OrielApp` 的端到端行为——那需要两个窗口宿主，无头环境跑不出来。

## 通知点击上报：直接删掉，而不是留一个空事件（2026-10-01）

`NotificationClicked` 曾经在三个后端里都是**显式空实现**（`add {} remove {}`）。
当时的理由写得很正当：让"这里确实没有触发源"在代码里可见，而不是看起来像"忘了触发"。

**但那个理由只对了一半。** 显式空实现让**实现者**看清了事实，却没有让**调用方**看清：

- 订阅一个永不触发的事件**不会编译报错**，运行时也不会有任何提示——调用方只会在
  "为什么点了没反应"里耗时间。README 的「验证账」当时确实把这一项写成"三平台均不支持"，
  但那是**文档层面**的事实，不是**编译器层面**的事实，而后者才是能拦住人的那个。
- 它还给了一个错误的心理暗示：API 面上留着这个事件，等于承诺"将来某个平台可能支持"。
  实际上三个平台都没有可行的路——Windows 要打包器注册 COM 激活器；`notify-send` 与 `osascript`
  根本没有回调入口（Linux 要改用 libnotify 的 action 回调，macOS 要进 .app bundle 后用
  `UNUserNotificationCenter`，都是换实现而非补一个开关）。

**做法**：把事件从 `OrielApp` 与 `IPlatformBackend` 里一并删掉，三个后端的空实现也删掉。
于是"不支持"变成了**编译错误**——这是调用方最早、也最便宜的发现时机。

**代价**：将来真要支持时（例如 macOS 打包后走 UN），那是一次"重新引入一个 API"的决定，
而不是把空事件加回来。这个代价可以接受：真到那天，API 形态大概率也不一样
（可能要带通知类别、动作按钮、`UNNotificationResponse` 的那些字段）。

**可以推广的一条**：凡是"实现侧明知拿不到、但 API 面上还留着"的能力，**删掉比留空壳好**。
留空壳的收益（让缺失可见）远小于它的代价（调用方无法在编译期发现）。

> 同时更正了 README 里一处与之相关的**错误声明**：无边框窗口的边缘 resize 在 Linux 上
> 并不是"由系统原生处理"——Ubuntu 22.04 实测无效（去掉窗口装饰后 WM 不再提供 resize 边框，
> 而库里没有 `gdk_window_begin_resize_drag` 绑定）。已作为**已确认的缺陷**记入 ROADMAP。

## 无边框窗口的边缘 resize：Windows 白送，Linux 得自己做（2026-10-01）

### 症状与根因

Ubuntu 22.04 虚拟机实测：无边框窗口拖边缘不改变大小。

根因不是"漏了一行调用"，而是**平台能力差异**。窗口一旦 `gtk_window_set_decorated(false)`，
窗口管理器就不再提供 resize 边框。Windows 上没这个问题，因为那边靠的是 **Composition 宿主**——
窗口能收到 `WM_NCHITTEST` 并显式给出边缘命中值，WM 于是照样给你边缘 resize（代价是组合托管的
WebView 收不到系统输入，鼠标消息改由宿主转发）。GTK 没有等价机制：**系统不会白送这份能力**，
只能自己判命中，再把 resize 交回给 WM/合成器。

### 做法

- 在 **webview** 上接 `motion-notify-event` 与 `button-press-event`——它铺满客户区，边缘命中在它身上判最自然。
- 显式 `gtk_widget_add_events(webview, GDK_POINTER_MOTION_MASK)`：motion 事件**默认不投递**，
  不加这一位，热区永远收不到指针移动。
- 命中边缘就把光标换成对应的 resize 形状，并**吞掉该 motion 事件**。
  吞掉是必需的：我们的 handler 排在 WebKit 的默认处理**之前**（`RUN_LAST` 信号），但一旦返回 FALSE，
  WebKit 紧接着就会按"指针下面是链接还是文本"重设光标——表现为光标闪烁。
- 左键按下且命中边缘 → `gtk_window_begin_resize_drag(window, edge, button, rootX, rootY, timestamp)`。
  文档原话是 "When GDK can support it, the resize will be done using the standard mechanism for the
  window manager or windowing system"——即 X11 交给 WM（保留边缘吸附/贴边平铺）、
  Wayland 转成 `xdg_toplevel.resize` 交给合成器。
  **必须在 button-press 里同步调用**，与 `gtk_window_begin_move_drag` 同一个理由：
  Wayland 协议只吃 seat + serial，而 GTK3 取的是最近一次隐式抓取的 serial。
- 光标设在 **webview 自己的 GdkWindow** 上（`gdk_window_set_cursor`）。设在顶层窗口上会被子窗口
  自己的光标盖掉，等于没设。

### 两个必须写对、而且"看起来对"验证不了的常量

这两个枚举的值都被**直接当整数**传给 P/Invoke，错一位的后果都只在真机上才看得见：

| 枚举 | 值 | 写错的后果 |
|---|---|---|
| `GdkWindowEdge` | NW=0、N=1、NE=2、W=3、E=4、SW=5、S=6、SE=7 | 拖右下角却在改左边 |
| `GdkCursorType` | `TOP_LEFT_CORNER = 134`（**不是 132**——132 是 `TOP_LEFT_ARROW`）等 | 显示成另一个形状的光标 |

两者都逐值抄自 GTK3 头文件，并各有一个单测把数值钉死。**我第一版就是照记忆把
`TOP_LEFT_CORNER` 写成 132 的**——查文档才发现差这两位。这类"数值型 ABI"必须查，不能推。

### 顺带修掉的一处文档失实

README 原来写着"窗口完全无边框，边缘拖动调整大小由系统原生处理，无需在页面里实现任何热区"——
这句话**只在 Windows 成立**，在 Linux 上是错的。已改成三平台对照表
（Windows ✅ / Linux 已实现未验证 / macOS 未实现）。

### 这一批能证明什么、不能证明什么

- 能证明：边缘命中判定（边界半开、角优先于边、小窗收窄热区）与两个枚举的数值——共 18 个单测。
- 不能证明：GTK 侧的接线——信号是否真的投递、Wayland 的 serial 时序、光标会不会被 WebKit 抢回、
  X11 下的边缘吸附是否如期出现。
- **后续（同日）**：用户在 Ubuntu 22.04 虚拟机实测 **resize 有效**，上面那几条随之从"待验证"升级为
  **已验证**（ROADMAP 的缺陷表里记了这一条）。

## 两个"状态晚一拍"的缺陷：都是信号/协议的时序问题（2026-10-01）

同一次真机验证还报出两件事：**双击标题栏不能最大化/还原**、**最大化/还原图标在"还原"那一步不更新**
（要点一下窗口外面才变）。两个都不是"逻辑写错了"，而是**读到的东西比实际晚一拍**。

### 一、在信号 handler 里同步读 GTK 状态，读到的是旧值

`window-state-event` 一来就调 `gtk_window_is_maximized()` ——看起来天经地义，实际拿到的是**旧值**。
原因在信号的分发顺序：GTK 把新状态写进 `GtkWindow` 私有字段是在**它自己的 class closure** 里，
而普通 handler（`g_signal_connect` 系）排在 class closure **之前**。

症状的形状正好是它导致的：

| 步骤 | 现象 | 为什么 |
|---|---|---|
| 初始 | 图标正确 | 页面加载完成时 `force: true` 上报过一次 |
| 最大化 | 看起来正确 | **被盖住了**——页面同时用 `win.toggleMaximize` 的返回值（意图值）驱动图标 |
| 还原 | 图标不变 | 同步读到的是"仍然最大化" → 与上次值相同 → 不上报 |
| 点窗口外面 | 图标才变 | 焦点变化本身也带一个 `window-state-event`（`FOCUSED` 位变了），那一次才读到正确值 |

**改法**：handler 里不再直接读，改为把读取排到**下一轮主循环**（复用已有的
`PostToMainThread` → `g_idle_add_full` 通道），那时 class closure 已经跑完。连续事件合并成一次读
（`SyncMaximizedState` 内部的状态比对会去重）。顺带加了"窗口句柄为 0 就直接返回"的护栏——
排队的读取可能落到窗口销毁之后。

**没有采用的两个替代方案**（记下来，免得以后有人再想一遍）：
- **解析事件结构**（`GdkEventWindowState::new_window_state`）：能拿到新值、还不用等一拍，
  但要在托管侧按偏移读 GDK 结构体。本文件里已有先例（GError 按结构偏移取），但那属于"没有别的办法"时的下策。
- **改用 `g_signal_connect_after`**：让 handler 排在 class closure 之后，同样能拿到新值，而且更贴 GTK 的习惯。
  没选它是因为它依赖"该信号确实是 RUN_LAST"这个前提——推迟到下一轮主循环则**与信号标志无关**，更稳。

### 二、Wayland 下"按下就把指针交给合成器"，把第二次点击一起吞了

无边框标题栏的拖动在 Wayland 上是**一次性交给合成器**的（`xdg_toplevel.move`）。原来
`BeginDragStreaming` 在 Wayland 分支里**立刻**就交了，于是指针被合成器 grab，页面再也收不到后续事件
——**第二次点击也被吞掉，双击永远凑不齐，`dblclick` 不触发**。

X11 没有这个问题：那边是按增量自己摆窗口（`gtk_window_move`），不抢指针，页面照常收事件。
所以这个坑只在 Wayland 会话里出现，而 Ubuntu 22.04 默认就是 Wayland。

**改法**：Wayland 分支只记下"按下了"，等 `DragTo` 的增量**超过阈值（4 逻辑像素）**才真正交给合成器
——那时按键仍按着，serial 依然有效。于是"按下一动不动"的单击与双击都不会触发拖动。
阈值判定抽成纯函数（`LinuxDragSupport.ExceedsMoveThreshold`）。

**这一条同时暴露了 README 里的一句错话**：原文写"macOS/Linux 用流式拖动，可立即开始"。
Linux/Wayland 不能立即开始——已改成按平台分别说明。

**可推广的一条**：凡是"把控制权一次性交给外部系统"的操作（合成器的 move/resize、原生模态循环、
模态对话框），都要问一句"交出去之后，我这边还能不能收到后续输入？"。Windows 上那个"拖动要等阈值"
的老经验，本质是同一个问题，只是当年只在 Windows 上踩到。

---

## 包内 MSBuild build logic：两处时机陷阱（2026-10-01）

把 `<EmbeddedResource>` + `LogicalName` 搬进随包分发的 `buildTransitive/OrielWeb.targets`
（ROADMAP 阶段 D）时，同一个文件里踩到两次"在错误的时机做了正确的事"。两次都**不报错**，
只给出看起来无关的症状，值得单独记下。

### 陷阱一：自引用元数据放进 Target 会批出空值

LogicalName 写成 `$(AssemblyName).wwwroot/%(RecursiveDir)%(Filename)%(Extension)`。
把 `Include` 放进 `<Target>`（先想到"放进 Target 才能看见消费方已声明的项"）时，
csc 报 `CS1508 此程序集中已使用了资源标识符 "MyApp.wwwroot/"`。

看一眼 csc 命令行就明白：`/resource:...\app.min.js,MyApp.wwwroot/` ——
`%(RecursiveDir)`、`%(Filename)`、`%(Extension)` **全部展开成了空**。

根因：`<Target>` 里属性中的 `%()` 元数据引用触发的是**批处理**，而它批处理的对象是
"当时已存在的该类型项"。消费方自己没声明过资源时那个集合是空的，于是批出一批空值，
四个文件共用一个资源名。**自引用元数据只在求值期（顶层 ItemGroup）解析正确。**

### 陷阱二：求值期读 `@(EmbeddedResource)` 拿到的是字面量

既然注入必须在求值期，那"消费方是否已声明 wwwroot"也在求值期判断就好——**不成立**。
实测在求值期读：

```
<_OrielWebDeclaredResources>@(EmbeddedResource)</_OrielWebDeclaredResources>
```

得到的是**字面量字符串** `@(EmbeddedResource)`，**不是空串**。这个区别很关键：
按"空串即未声明"写判据，会得到与事实相反的结论，而且它静默得没有任何提示。

根因：NuGet 把包内 targets 放在 `obj/*.nuget.g.targets` 里导入，而那个导入点
**早于项目体的项求值**——此时 `EmbeddedResource` 这个项类型还没被任何 `<ItemGroup>` 建立，
MSBuild 就把项引用原样留在属性值里。

### 由此定下的形态

- **注入在求值期**（要 `%(RecursiveDir)`），无条件、并打上 `OrielWebAutoEmbed="true"` 标记；
- **判定在执行期**（要看见消费方的项），挂在 `BeforeTargets="PrepareForBuild"`，
  发现消费方也声明了 wwwroot 就 `Remove` 掉自己那份。

**可推广的两条**：
1. MSBuild 的"求值期 / 执行期"不是实现细节，而是**语义分界**——同一个文件里两种时机都要用时，
   就得接受"先无条件加、再按需撤"这种看起来绕的形态。
2. 排障时**去看 csc 的命令行**（`-v:n`，编译行末尾的 `/resource:` 与 `LogicalName`），
   比读 targets 代码快得多——上面陷阱一的根因在命令行里一眼可见，而 targets 里看不出任何异样。

另外一处小坑：`Message` 的 `Importance="low"` 只在 `-v:detailed` 及以上显示，
而 `-v:n` 是最常用的排障档位。诊断信息用 `normal`（`dotnet build` 默认的 minimal 仍看不到）。

## 能力模型：默认姿态、保留前缀与"注入期判来源"（2026-10-01）

阶段 D 的第二项。此前本库的 IPC 命令**谁都能调**——没有来源校验、没有令牌、没有按命令授权。
这一节记的是取舍与实测结论，不是 API 说明（那是 README 的事）。

### 默认姿态：Debug 全放行、Release 全拒绝

- 判据是**"应用有没有声明能力"**（有没有调 `UseCapabilities`），而不是"某条命令在不在名单里"。
- 未声明时 Debug 放行、Release 拒绝；声明之后 Debug / Release 一视同仁。
- 为什么不一律 fail-closed：本地开发时每条命令都要先去名单里加一行，实际结果是所有人都在 Debug 下
  加一个 `Allow("*")` 然后忘了删——那比现在更糟，因为它是**写进代码的**例外。
- 为什么不一律放行：用户装的是 Release 产物，"没配就是没有"才是安全默认。

### "这次是 Debug 还是 Release"取的是**消费方**的构建配置

本库是**以 Release 发布的**，它自己的 `#if DEBUG` 永远是 false，"库是不是 Debug 构建"这个问题无意义。
有用的问题是"**用库的这个应用**是不是 Debug 构建"，答案只存在于消费方。两条取值路径：

1. 包内 `buildTransitive/OrielWeb.targets` 给消费方程序集注入 `AssemblyMetadata("OrielBuildConfiguration")`，
   值为 `$(Configuration)`。**最准，也是这一项与阶段 D 第 1 项共用的同一份 targets。**
2. 回退到入口程序集的 `DebuggableAttribute.IsJITOptimizerDisabled`——Debug 构建会关掉 JIT 优化，
   这个位就是那个开关。它不依赖任何包内资产，因此**仓库内用 `ProjectReference` 的场景**
   （拿不到 `build/`、`buildTransitive/`，见 README）也判定得到。

两条都取不到时按 **Release** 算：判不出来就当生产构建，方向与 fail-closed 一致。
元数据存在但值认不出来（有人把 `Configuration` 改成了 `Shipping`）时记一笔调试输出——
静默回退会让"为什么 Release 下全被拒了"无从查起。

### deny 优先

两条规则都命中时按拒绝算。理由不是偏好："先写宽泛的 allow、再用 deny 挖掉个别例外"是最常见的写法，
若 allow 优先，那条 deny 就等于没写，**而且这个错误是静默的**。
同理，拒绝时的回执要能区分"落进了 Deny 名单"与"不在 Allow 名单里"——两句话混成一句，
"我明明加进 Allow 了却不生效"就无从排查。

### `win.*` 始终放行，并因此成为**保留前缀**

无边框窗口的标题栏按钮（最小化 / 最大化 / 关闭 / 拖动 / 上下文菜单）走的就是这一组。
Release 下未配置即拒绝这条规则若把它们一起拒掉，无边框窗口会**直接变成关不掉的窗口**——
自绘标题栏，没有系统标题栏可替代，用户只能去任务管理器。

代价是 `win.` 成了保留前缀：应用自己的命令用它开头就会绕过能力配置。库不阻止（那是应用自己的选择），
但 README 里写明了。

### 来源校验放在**注入期**，不是宿主侧

桥接脚本在安装之前先看自己的 `location.href` 是否命中可信前缀数组；不命中就**整个退出**，
连 `window.oriel` 都不存在。这样"远程页面不接 IPC"是**结构性成立**的，不必依赖宿主侧每次再判一次，
也不需要担心"远程页面拿到了令牌"——它拿不到脚本，自然拿不到令牌。

前缀判定用的是 `startsWith`，而内嵌资源的前缀带尾斜杠（`https://app.oriel/`），
所以 `https://app.oriel.evil.com/` 这种"把 host 拼进自己域名"的形态不会命中（有单测钉住）。

**踩到的一处**：Linux / macOS 上内嵌资源的 `https://<host>/` 注册不了，导航前会被改写成
`file://<解压目录>/`（见本文的「内嵌资源虚拟主机 URL」一节）。可信前缀若只加 https 那一份，
页面会被**自己的**门禁拒掉——表现为"命令全都没反应"，而且没有任何一处会报错。
所以 `OrielApp` 在解压之后把解压目录也补进可信前缀。这条路径在 WSL2 + WSLg 上实测跑通
（`--selftest capability` 在那里 PASS，若前缀没补上，两条回显都收不到）。

### 令牌：防的是"通道被别的脚本塞消息"，不是网络攻击

每次进程启动生成一个 128 位随机串，随桥接脚本注入页面，每条入站消息回带；宿主侧不匹配即丢弃。
它**不做**过期、刷新、轮换——消息不经过网络，攻击者要拿到令牌得先能在这个进程的地址空间里执行代码，
那时令牌已经不重要了。比较仍用 `CryptographicOperations.FixedTimeEquals`：
这一处本不构成真实威胁，但固定时长比较没有代价，省下的是"将来有人把它当凭据复用"这个隐患。

### 刻意没做 Ryn 的那两件事

Ryn 的 `scope` 用**路径 glob + 符号链接规范化**、`scopedCommands` 用 **argv 模板 + regex**——
那两样是给"文件读写 / 执行命令"这类**沙箱能力**当判据的。本库不提供这类能力面
（`shell.execute` / PTY 明确不在路线图内，见 ROADMAP），能力面是"应用自己注册的命令名"，
标识符匹配就够；把一个不需要的 glob 规范化搬进来只会多一处能写错的地方。
模式因此只有两种：精确名与 `todo.*` 这样的前缀通配（`*` 表示全部）。**不做正则**——
命令名配错的代价是静默放行，而正则的边界情况多到没法靠 review 保证。

同理，`OrielCapabilityOptions` 里也**不做**"未给任何模式即拒绝一切"这种隐性语义：
空字符串会直接抛，因为静默放进名单只会让调用方以为配了却没生效。

### 这一批能证明什么、不能证明什么

- **能**：三层门禁（来源 / 令牌 / 命令授权）的放行与拒绝路径都有机器断言——`CapabilityTests` 60 例
  覆盖模式匹配（`todo.*` 不匹配 `todo`、大小写按 Ordinal）、deny 优先、令牌形状与比较、
  Debug/Release 判定、以及门禁三层各自的正反路径；`bridge.test.mjs` 按三平台各断言
  "不可信来源下 `window.oriel` 不存在"与"每条出站消息都带令牌"；
  `--selftest capability` 从**页面真实 invoke** 走完端到端（Windows 与 WSL2 + WSLg 各一次 PASS），
  并断言被拒的命令**没有执行到**——"被拒"与"压根没跑到"是两件事。
- **不能**：真实第三方远程页面尝试打 IPC（无头环境里造不出这样一个文档；它由注入期的来源判定
  结构性挡住，但没有端到端的负例）；macOS 侧（已接进 `verify-macos.sh`，待 CI 的 smoke-macos 首次跑通）。

**顺带踩到的一处**：桥接单测的脚本是用 `new Function(...)` 加参数执行的，脚本现在读 `location.href`,
而 Node 没有这个全局——不加参数注入就会以 `ReferenceError` 加载失败，表现与"脚本写错"完全一样。
