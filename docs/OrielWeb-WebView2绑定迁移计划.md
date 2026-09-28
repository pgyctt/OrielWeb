# OrielWeb · WebView2 自生成绑定迁移计划（阶段 E）

> 本文件是 [`OrielWeb-改进计划.md`](./OrielWeb-改进计划.md) 阶段 E 的详细展开。
> **前置阅读**：改进计划第 1 节（阶段总览）与第 8 节（缺陷索引）。
> **重要**：本计划的 E-0 是**决策门**。在 E-0 得出结论前，不要开始 E-2 之后的任何工作。

---

## 1. 目标

把 Windows 平台层的 **COM 互操作**从"手工 vtable + 手工 CCW"迁移到 **`[GeneratedComInterface]` / `[GeneratedComClass]` + 代码生成**，以消除三类长期成本：

| 当前状态 | 迁移后 | 依据 |
|---------|-------|------|
| 18 个硬编码 vtable 槽位，来自人工数 `WebView2.h`，**无编译期校验**，扩 API 时线性累积风险 | 从 `.winmd` 自动生成，编译期类型安全 | 本文 §2 |
| 手工 CCW（`Win32NativeCallbacks.cs` 构建 vtable + 手动引用计数） | 源生成的 CCW | — |
| `AddRefComObject` 后永不 Release；`GetSettings()` / `get_CoreWebView2()` 返回值不 Release | 自动引用计数（`IComObject<T>` / marshaller） | 评审 P1 |
| 调用时机/重入类风险只能靠真机暴露（如 `WM_MOVE` 调 `NotifyParentWindowPositionChanged` 触发 AV） | 部分缓解（更强的类型约束 + 生成的 marshalling） | 评审 §二.5 |

**注意**：迁移**不会**解决"槽位布局被微软改坏"这类问题——那不存在（COM 不变式）。它解决的是**人工维护成本与无编译期兜底**。

---

## 2. 前提核查（本计划的立论基础）

以下事实已在本轮调研中核实，作为决策依据：

| 事实 | 来源 | 可信度 |
|------|------|--------|
| `smourier/WebView2Aot` 现在使用 `[GeneratedComInterface]`，**不是**手工 vtable；接口从 `.winmd` 由 `Win32InteropBuilder` 生成 | 项目 README + DeepWiki 架构页 | 高 |
| 该项目 NuGet 包 `WebView2Aot` **1.6.1**，下载 **82,683**，MIT，最近更新 **2026-09-24**（修引用泄漏） | NuGet API 实测 | 高 |
| **Uno Platform 6.7 把 WebView2Aot 作为 .NET 10 桌面端的默认后端**，原 `Microsoft.Web.WebView2` 后端在 Uno 7 中移除 | Uno 官方文档 | 高 |
| 该项目面向 `.NET 10+`，覆盖 `ICoreWebView2` ~ `_28`、`Environment` ~ `_14`、`Settings` ~ `_9`，且**声明与 WinRT 无关** | 项目 README | 高 |
| 该项目提供 `Task`-based `...Async`、真实 .NET 事件、`IComObject<T>` 可释放对象、C# 14 扩展属性 | 项目 README | 高 |
| 官方 `Microsoft.Web.WebView2` 包从 1.0.2849.39 起在 WinUI3/WinAppSDK 场景修复了 CsWinRT 的 AOT 问题；MAUI 场景仍有报告问题 | 中文技术博客 / MAUI discussion | **中**（建议自测） |

> ⚠️ 我在 Linux 环境评审，**无法复现任何 Windows COM 行为**。本计划中所有关于"能不能跑"的结论都设计为**由你在 Windows 上验证**（E-0），而非我替你断言。

### 一个必须复核的历史判断

`docs/DECISIONS.md` 写道：

> 参考 [smourier/WebView2Aot](https://github.com/smourier/WebView2Aot)（**手工互操作路线**验证）

而该项目当前明确使用 `[GeneratedComInterface]`。这说明当初放弃 `[GeneratedComInterface]` 的依据可能来自该项目 2025-04 的初版形态或误读。**E-0 的作用就是重新校验这个判断**——如果 `[GeneratedComInterface]` 在 .NET 10.0.401 上确实可用，那么当初记录的三个 ComWrappers bug 很可能属于**用法/配置问题**而非运行时缺陷。

---

## 3. E-0 决策门（必须先做）

**目的**：用最小成本回答一个问题——在你当前环境（.NET 10.0.401 + Native AOT + Windows）下，`[GeneratedComInterface]` 路线的 WebView2 能否跑通？

**做法**：不要写自己的代码，直接跑官方的现成样例，排除自己实现的干扰。

### 3.1 方案 1（首选）：跑 WebView2Aot 官方样例

```powershell
git clone https://github.com/smourier/WebView2Aot
cd WebView2Aot

# 确认样例的目标框架与 SDK
Get-Content .\HelloWebView2\HelloWebView2.csproj

dotnet publish .\HelloWebView2 -c Release -r win-x64
# 运行产物（约 40 行 C# 的 hello world）
.\HelloWebView2\bin\Release\net10.0\win-x64\publish\HelloWebView2.exe
```

**期望**：单文件 exe 弹出窗口并加载网页（样例默认导航到外部 URL）。

**记录以下信息**（这些是决策依据）：

| 观察项 | 记录内容 |
|--------|---------|
| 编译是否产生 IL2xxx / IL3xxx 警告 | 有则抄录警告码与类型 |
| AOT 发布是否成功、exe 体积 | 便于与现有 demo（约 2 MB + loader）对比 |
| 运行时是否弹窗、是否显示网页 | 关键 |
| 是否出现你当初记录的三类症状 | ①`GetOrCreateComInterfaceForObject` 返空 ② 查 `IReferenceTarget`/`IReferenceTrackerTarget` 时 NRE ③`ConvertToManaged` AV |
| 项目是否包含 `ComWrappers.RegisterForMarshalling` 或类似的显式注册 | **这一点最关键**——见 §3.3 |

### 3.2 方案 2（对照）：自建最小探针

若想排除"官方样例恰好绕开了某个坑"，自建一个最小项目跑同样的流程：

```powershell
dotnet new console -o wv2probe
cd wv2probe
dotnet add package WebView2Aot --version 1.6.1
```

`wv2probe.csproj` 关键属性：

```xml
<PropertyGroup>
  <OutputType>WinExe</OutputType>
  <TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>
  <PublishAot>true</PublishAot>
  <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
  <IsAotCompatible>true</IsAotCompatible>
</PropertyGroup>
```

`Program.cs` 按该包 README 的调用顺序走：`WebView2Utilities.Initialize()` → 创建环境 → 创建控制器 → `get_CoreWebView2()` → `Navigate()`（**具体签名以包内生成的接口与 README 为准**，不要照抄本文档——我无法在 Linux 上验证这些签名）。

发布并与方案 1 交叉比对：

```powershell
dotnet publish -c Release -r win-x64
```

### 3.3 关键观察点：CCW 侧如何注册 marshaller

你当初的三个 bug 全部集中在 **CCW 方向**（托管对象暴露给 COM）和 **RCW 方向**的 marshaller 选择上。这两个方向对 `[GeneratedComInterface]` 的要求不同：

| 方向 | 场景 | 需要的机制 |
|------|------|-----------|
| **RCW**（托管→原生） | 调用 `ICoreWebView2.Navigate` 等 | `[GeneratedComInterface]` 生成的存根即可，通常无需显式注册 |
| **CCW**（原生→托管） | 把 `WebMessageReceived` 等回调对象交给 WebView2 | **需要源生成的 CCW 支持**（`[GeneratedComClass]` 或对应的 marshaller 注册） |

你记录的 bug 1（`GetOrCreateComInterfaceForObject` 返空）和 bug 2（`AsRuntimeDefined` NRE）都发生在 CCW 路径，且现象指向 `StrategyBasedComWrappers`——那是运行时的**内置通用实现**，与源生成的专用实现是两条路。

**因此 E-0 必须确认**：官方样例是用什么机制把回调对象交给 WebView2 的（`[GeneratedComClass]`？显式 `ComWrappers.RegisterForMarshalling`？还是别的）。把样例中涉及该处的**代码原文**抄下来，这是后续 E-6 的直接模板。

### 3.4 决策规则

| E-0 结果 | 决策 |
|---------|------|
| 样例跑通 | **当初放弃 `[GeneratedComInterface]` 的判断不成立**。按 §4 选定路线并继续 E-2。同时把 E-0 的观察记录补进 `DECISIONS.md`，覆盖原结论 |
| 样例在最新 SDK 下跑通，但你环境（10.0.401）失败 | 记录具体 SDK 版本与失败点，**可能是 SDK 版本回归**——值得开 issue；本阶段暂缓，继续用 A-3 的槽位门禁兜底 |
| 样例失败且可复现 | 保留手工 vtable 路线（路线 C）。把槽位校验脚本升级为**必需门禁**，并把"每次扩接口"写入贡献指南 |

---

## 4. 路线选择（E-1）

| 路线 | 做法 | 优点 | 代价 | 适配场景 |
|------|------|------|------|---------|
| **A：用现成 `WebView2Aot` 包** | `dotnet add package WebView2Aot` | 最省力；8 万+ 下载、Uno 背书、活跃维护；附带 `Task`/事件/`IComObject<T>` 高质量包装；`WebView2Utilities.Initialize` 支持 loader 嵌入（正好匹配你的单文件发布需求） | 引入 `DirectNAot` 依赖；把 COM 层交给第三方绑定 | 想最快拿到稳定可用的 Windows 后端 |
| **B：`Win32InteropBuilder` 自生成** | 自建一个 InteropBuilder CLI 包装，输入 `.winmd` 产出自己的 `[GeneratedComInterface]` 集 | **保住"零第三方运行时依赖"的定位**；生成物是你自己的代码；升级 SDK 只需重跑生成器 | 需要自己维护生成配置（`Patches.json` 等）；一次性投入较大 | 保持项目定位与自主性（**推荐**） |
| **C：维持手工 vtable** | 现状 + A-3 槽位门禁 | 零迁移成本；已验证可用 | 人工数槽位的长期成本不消失；引用计数仍需人脑记账 | E-0 失败时 |

**我的建议：优先 B，E-0 通过后先用 A 做快速验证再转 B。** 理由：

1. B 保住了 `DECISIONS.md` 里"纯 C# P/Invoke、零 C++ 组件"的定位，同时消除了唯一真实的长期风险。你已经在维护 `WebView2Slots` 常量表了——那本质就是人工版代码生成，把它换成真生成器是划算的。
2. 但 B 的生成器配置需要试错，先用 A 跑通一个端到端的 Windows 后端（拿到可工作的 `Win32WindowHostV2`），再替换底层绑定来源，风险最小。
3. 注意：**Windows 上的"纯 C# P/Invoke"已不是差异化卖点**——`[GeneratedComInterface]` 本身就是纯 C# 且 AOT 友好，不需要 C++ 也不需要 WinRT。真正的差异化在 macOS/Linux（GTK/WebKitGTK、WKWebView），那也是目前唯一还没跑起来的部分。

---

## 5. 范围界定（很重要）

### 只迁 COM，不动 Win32

| 层 | 迁移？ | 理由 |
|----|--------|------|
| WebView2 COM 调用（`Interop/WebView2Com.cs` 的 8 个 `*Ptr` 结构） | ✅ 迁 | 手工槽位、手工引用计数、无编译期校验 |
| COM 回调（`Win32NativeCallbacks.cs` 手工 CCW） | ✅ 迁 | 原始三个 bug 所在层 |
| WebView2 加载器调用（`WebView2LoaderNative`） | ⚠️ 可选 | 已是 `[LibraryImport]`，AOT 安全；若沿用 A 路线可交给 `WebView2Utilities.Initialize` |
| Win32 窗口/消息循环/DPI（`Interop/Win32Native.cs`） | ❌ 不迁 | 已是 `[LibraryImport]`，AOT 安全，无反射问题。迁它只会引入 `DirectNAot` 全量依赖、把风险面扩大到窗口/消息循环/对话框，收益为零 |
| Win32 对话框（`comdlg32`、`MessageBoxW`） | ❌ 不迁 | 同上。`IFileDialog` 的升级是 P2 路线图项，与本次迁移无关 |
| `Win32WindowHost` 的窗口逻辑（样式、全屏、拖动） | ⚠️ 保留，仅替换其中 COM 调用点 | 逻辑本身与互操作技术无关 |

**这个界定的实际意义**：迁移后的 `Win32WindowHostV2` 会保留绝大部分现有代码，只把"调 COM 的方式"换掉。这大幅降低了风险与工作量。

---

## 6. 实施步骤

### E-2 获取元数据

```powershell
git clone https://github.com/smourier/webview2-win32md
# 该工具从官方 WebView2 SDK 生成 Microsoft.Web.WebView2.Win32.winmd
```

或直接从 `WebView2Aot.InteropBuilder.Cli/` 目录取现成的 `Microsoft.Web.WebView2.Win32.winmd`。

**版本对齐**：确认 winmd 对应的 WebView2 SDK 版本 ≥ 你 `OrielWeb.csproj` 当前的 `Microsoft.Web.WebView2` 包版本（`1.0.2903.40`），否则会缺接口。

### E-3 生成绑定

```powershell
git clone https://github.com/smourier/Win32InteropBuilder
# 参照 WebView2Aot 的 WebView2Aot.InteropBuilder.Cli 写一个自己的包装：
#   - 输入：Microsoft.Web.WebView2.Win32.winmd
#   - 配置：Patches.json（方法名/参数修正）、自定义 json（命名空间、输出路径）
#   - 输出：src/OrielWeb/Platform/Windows/Interop/Generated/*.cs
```

**产出物纳入版本管理**（生成物入库，不在构建时生成）——这样 CI 不依赖生成器，且升级时能看清 diff。

**验收**：生成的 `ICoreWebView2` 包含 58 个方法；`ICoreWebView2_3.SetVirtualHostNameToFolderMapping` 存在。

### E-4 新增 `Win32WindowHostV2`（并行存在，不删旧的）

```csharp
// src/OrielWeb/Platform/Windows/WindowsPlatformBackend.cs
public IWindowBackend CreateWindow(WebviewWindow window, OrielWindowOptions options, OrielApp app, string? assetDirectory)
{
    var useLegacy = string.Equals(
        Environment.GetEnvironmentVariable("ORIEL_WIN_BACKEND"),
        "legacy", StringComparison.OrdinalIgnoreCase);

    var host = useLegacy
        ? Win32WindowHost.Create(window, options, app, assetDirectory, this)
        : Win32WindowHostV2.Create(window, options, app, assetDirectory, this);

    _aliveWindows++;
    return host;
}
```

`IWindowBackend` 抽象已经就位，两个实现可互换。**这是本次迁移最重要的安全机制**：同一个 exe 内可 A/B 对比，出问题只需设一个环境变量切回。

### E-5 替换 COM 调用点

| 原（手工） | 新（生成） |
|-----------|-----------|
| `WebView2EnvironmentPtr.CreateCoreWebView2Controller(hwnd, handler)` | `environment.CreateCoreWebView2Controller(hwnd, handler)` |
| `WebView2ControllerPtr.put_Bounds(rect)` | `controller.put_Bounds(rect)` 或包装后的 `controller.Bounds = rect` |
| `WebView2Ptr.Navigate(url)` | `webView.Navigate(url)`（或 `webView.Navigate(url)` 的包装版，含错误抛出） |
| `WebView2Native.TryQueryInterface(...)` + `WebView2_3Ptr` | 直接 `webView.Object` 或生成的接口（版本化接口成员在包装层可直达） |
| 手写 `ushort*` 固定与 `PtrToStringAndFree`（`CoTaskMemFree`） | 生成器/marshaller 处理字符串封送 |
| `WebView2Slots` 全部 18 个常量 | **删除** |

顺手解掉评审中的引用计数缺陷：`GetSettings()` / `get_CoreWebView2()` 返回的接口对象在包装层是 `IComObject<T>`（可释放），不再泄漏。

### E-6 回调替换为 `[GeneratedComClass]`

把 `Win32NativeCallbacks.cs` 的 7 个手工 CCW thunk（`EnvironmentInvoke` / `ControllerInvoke` / `WebMessageInvoke` / `NavigationCompletedInvoke` / `DocumentTitleInvoke` / `ExecuteScriptInvoke` / `AddScriptInvoke`）替换为带 `[GeneratedComClass]` 的回调类。

**这一步直接对治你当初的三个 bug**，也是 E-0 §3.3 要抄模板的地方。

替换后同时获得：

- 不再需要手工维护 `NativeComObject` 的 vtable / `Iid` / `RefCount`
- 不再需要"thunk 全身 try/catch"这条人工纪律（C-1 在这三处的问题自动消失）
- `QueryInterface` 的覆盖面由生成器决定，不再是"只认 IUnknown + 自身 IID"

> ⚠️ **不要在迁移后立刻删除 `Win32NativeCallbacks.cs`**——保留到 E-9 验收通过、并且 legacy 后端至少稳定运行一个发布周期。

### E-7 引用计数

- 所有返回的 COM 对象走可释放包装（`IComObject<T>` 或 `[MarshalUsing(typeof(UniqueComInterfaceMarshaller<T>))]`）
- 删除 `WebView2Native.AddRefComObject` 及其两处调用（`Win32WindowHost.cs:309` / `:324`）
- **验证点**：多窗口反复开关 ×20，`Environment` / `Controller` / `Settings` 的引用计数不累积（可用 `GetReferenceCount` 风格的诊断，或观察内存曲线）

### E-8 切换默认并清理

| 清理对象 | 说明 |
|---------|------|
| `Interop/WebView2Com.cs` 的 8 个 `*Ptr` 结构 + `WebView2Slots` + `WebView2Iids` | 被生成代码取代 |
| `Win32NativeCallbacks.cs` | 被 `[GeneratedComClass]` 取代 |
| `Interop/Win32Native.cs` 中的 `WebView2LoaderNative` | 若采用路线 A 的 `WebView2Utilities.Initialize` 则删除 |
| 改进计划 A-3（槽位校验 CI 步骤） | 手工槽位已不存在，门禁作废 |
| 改进计划 C-5（`ICoreWebView2_3` QI 失败诊断） | 已有更好的版本管理机制 |
| `Microsoft.Web.WebView2` 的 `PackageReference` | 若路线 A 自带 loader 处理则可去依赖（注意 `WebView2Loader.dll` 的原生分发仍需解决） |
| `Win32WindowHost`（legacy） | 待稳定后删除，并移除 `ORIEL_WIN_BACKEND` 开关 |

**保留**：`Win32Native.cs` 的窗口/消息/DPI/dialog 部分、`Win32WindowHostV2` 的窗口逻辑、`Win32WebView2Ipc.cs` 的 `WebMessageReceivedHandler`（改为回调类的宿主，逻辑可复用）。

### E-9 验收

见 §7。

---

## 7. 验收标准

| # | 项目 | 判据 |
|---|------|------|
| 1 | AOT 单文件发布 | `dotnet publish samples/OrielDemo -c Release -r win-x64` 成功；产物体积与现有（约 2 MB + loader）可比或更小 |
| 2 | 无 IL2xxx / IL3xxx 警告 | CI 输出中无 trim/AOT 警告 |
| 3 | 窗口与 IPC | 与当前 demo 功能对齐：窗口显示、页面加载、`invoke` 往返、Promise resolve |
| 4 | 就绪机制 | demo 徽章显示"IPC 已连接"（依赖 B-2/B-3 已修复） |
| 5 | 无边框窗口 | 拖动、双击最大化、最小化/关闭、最大化不遮挡任务栏 |
| 6 | 窗口能力 | 全屏切换、置顶切换、对话框 |
| 7 | 多窗口 | 2+ 窗口独立生命周期；反复开关 ×20 无泄漏（引用计数不累积） |
| 8 | 三架构 | win-x64 / win-arm64 / win-x86 均可发布运行 |
| 9 | 旧运行时降级 | 用旧版 WebView2 Runtime 运行时，给出明确错误而非白屏 |
| 10 | A/B 对照 | 设置 `ORIEL_WIN_BACKEND=legacy` 能切回旧后端且行为一致 |

---

## 8. 回滚方案

| 场景 | 动作 |
|------|------|
| V2 在某功能上不如 V1 | 设 `ORIEL_WIN_BACKEND=legacy`，无需改代码 |
| V2 有严重缺陷需整体回退 | `git revert` 对应的迁移 commit（E-4 之后、E-8 之前的所有改动都是**纯新增**，revert 成本低——这也是为什么把清理放在 E-8 最后一步） |
| 生成物有问题 | 重新运行生成器；生成物入库所以可逐行 diff |
| 决策门失败 | 不进入 E-2，保留路线 C + A-3 门禁 |

**关键设计**：E-4 到 E-7 全部是**新增代码**，不改动 legacy 路径。这保证任何时刻都能一键切回。真正的破坏性改动集中在 E-8（删除），因此 E-8 必须等 E-9 全部验收通过后再做。

---

## 9. 工作量与依赖

| 步骤 | 预估 | 依赖 |
|------|------|------|
| E-0 决策门 | 0.5–1 天（含环境准备与观察记录） | Windows + .NET 10 SDK + MSVC 链接器 |
| E-1 选路线 | 0.5 天 | E-0 结果 |
| E-2 取 winmd | 0.5 天 | — |
| E-3 生成绑定 | 1–2 天（生成器配置有试错） | E-2 |
| E-4 V2 骨架 + 开关 | 0.5 天 | E-3 |
| E-5 替换 COM 调用 | 1–2 天 | E-4 |
| E-6 回调改 `[GeneratedComClass]` | 1–2 天（**风险最高**，原始 bug 所在层） | E-5 |
| E-7 引用计数 | 0.5 天 | E-6 |
| E-8 清理 | 0.5 天 | E-9 通过 |
| E-9 验收 | 1 天 | 全部 |
| **合计** | **约 7–10 天** | |

**与其它阶段的耦合**：

- **强依赖 A 阶段**（安全网）。没有冒烟与 JS 测试，E 阶段的改动无法快速判定回归。
- **C-5 在 E 完成后作废**，不必为它投入过多精力（做一版即可）。
- **C-1 关于 `Win32WindowHost.WindowProc` 的那处兜底在 E 中**仍然需要——它保护的是 Win32 消息回调，而 Win32 层不在迁移范围内。`Win32NativeCallbacks.cs` 里的 7 处则随 E-6 自然消失。
- **A-3 槽位门禁**在 E 期间仍然有用（保护 legacy 路径），E-8 后删除。

---

## 10. 并行建议

Windows 后端迁移（E）与 macOS/Linux 修复（B）**没有代码冲突**，可以并行推进：

- B-1 是 1 行改动，立刻解锁 Linux/macOS 的真机验证 → **最高优先级**
- E-0 是纯实验（不碰仓库代码），可以随时插入
- E-2 之后才需要动 `Platform/Windows/` 目录

因此推荐节奏：**先做 A + B（约 1 天，解锁跨平台），E-0 并行做掉，再根据 E-0 结论决定是否投入 E-2 起的工作量。**
