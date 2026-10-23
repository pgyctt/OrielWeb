# 代码健康度改进计划（评审后）

> 制定时间：2026-10-23
> 输入：2026-10-23 全面评审（构建、测试、CI、架构、安全、文档一致性六项）
> 基线提交：`34442d5`（本文件里的行号以该提交为准，改动后请以符号名为准）
> 定位：**只修结构与基线，不加功能**。公开 API 只做加法，不改变既有语义。

---

## 0. 这份计划的取舍

### 0.1 为什么不是"继续补功能"

评审的核心结论不是"功能缺"，而是**功能跑得比结构快**：

- 24 天、266 次提交、19 个版本，`0.4.2` 已经覆盖 Tauri 2 对齐的绝大部分；
- 但 Platform 层占 **54.8%** 的代码（25,877 行）且几乎无托管单测，三个窗口宿主各 2,765–4,002 行；
- "零反射 IPC / O(1) 分发"是立身之本，仓库里却**没有一个性能基准**；
- 平台矩阵里已有条目强于仓库内可复现的证据。

所以顺序是：**先把结构压回可控 → 再把质量变成数字 → 最后才谈 1.0**。

### 0.2 总原则

| 原则 | 含义 |
|---|---|
| **每批独立合入** | 每批自带验收命令，不合入"半成品"，不搞"全做完再验" |
| **行为零变化** | R0–R5 不得改变任何可观测行为；现有 1,476 个测试与三平台 selftest 必须全绿 |
| **先有断言再改** | 涉及"防退化"的批次（R1/R2/R6），先写会红的测试，再改实现 |
| **不做投机重构** | 只动被本计划点名的位置；顺手重构一律拒绝，避免 diff 不可 review |
| **公开 API 只做加法** | 新增可选参数/新方法可以；改签名、改语义、改错误码不行 |

### 0.3 明确不在本计划内

| 项 | 原因 | 何时重新考虑 |
|---|---|---|
| IPC 改二进制通道（对标 Tauri v2 raw IPC） | 会动协议与桥接脚本，收益需 R3 的数据支撑 | R3 出基线后，若 JSON 编解码占比 > 30% 再评估 |
| 引入 DI 容器 / 抽象工厂重排 | 当前 builder + 工厂足够，换容器只增加间接层 | 出现第三方扩展点需求 |
| `IWindowBackend` 拆成多个接口 | 拆接口会让三平台改动面翻倍，收益主要是"好看" | R4 完成且仍有新增平台需求时 |
| 与 Tauri 对齐无关的 API 增补 | 不是本计划范围 | 走 B 系列 |
| 符号链接的**写**路径解析 | 写时目标通常不存在，`ResolveLinkTarget` 无效 | 出现真实越权路径 |

---

## 1. 基线（改进前快照）

跑法：`dotnet build OrielWeb.slnx -c Release` / `dotnet test tests/OrielWeb.Tests -c Release` / `dotnet format OrielWeb.slnx --verify-no-changes`

| 指标 | 当前值 | 取法 |
|---|---|---|
| 构建 | 0 警告 0 错误 | `src/Directory.Build.props` 已开 `TreatWarningsAsErrors` |
| 单元测试 | **1,476 通过 / 0 跳过 / 4s** | `dotnet test` |
| 格式门禁 | 干净 | `dotnet format --verify-no-changes` |
| 源码 | 143 文件 / 47,259 行 | `find src -name '*.cs' -not -path '*/obj/*'` |
| 测试 | 70 文件 / 13,940 行（1:0.29） | 同上 |
| Platform 占比 | 25,877 行 / **54.8%** | Windows 9,037 · macOS 7,925 · Linux 7,381 |
| 三个窗口宿主 | 4,002 / 3,310 / 2,765 行 | `wc -l` |
| `IWindowBackend` | ~100 个成员，三平台各实现一遍 | `src/OrielWeb/Platform/IPlatformBackend.cs` |
| 公开类型 | 70 个，无 PublicAPI 基线 | — |
| 性能基准 | **0 处** | 全仓无 BenchmarkDotNet |

**本计划成功时的目标态**（都可测量，不写"更清晰""更易维护"这类无法验收的话）：

| 目标 | 现状 → 期望 | 归属 |
|---|---|---|
| 三个窗口宿主的最大单文件 | 4,002 / 3,310 / 2,765 行 → 全部 ≤ 800 行 | R4a |
| 平台层可测的纯函数 | 零散（3 处） → ≥ 60 个，且各有单测 | R4b |
| `HandleInvokeAsync` 长度 | 303 行 → ≤ 150 行 | R1 |
| 基准项目 | 无 → 四项基准 + `main` 上趋势 | R3 |
| 平台矩阵可追溯性 | 部分条目无证据 → 每行都能追到 CI job 或带日期的取证 | R5 |
| 命令注册语义 | 隐含 → 文档写死 + 可选严格模式 | R2 |

> 刻意**不设**"Platform 代码占比降到 X%"这类指标：拆文件与抽函数都会增加总行数（注释与签名），占比不是这次要优化的东西。要减的是"单文件认知负担"，不是行数。

---

## 2. 批次总表

| 批次 | 目标 | 难度 | 改动面 | 依赖 | 可独立合入 |
|---|---|---|---|---|---|
| **R0** | 零成本清理：注释错位、CI 样板、文档缺口 | 低 | 3 个文件 | 无 | ✅ |
| **R1** | 内建命令族收成一个循环（去掉 228 行同构复制） | 中 | `OrielCommandDispatcher.cs` + 1 测试 | 无 | ✅ |
| **R2** | 命令注册边界如实化（文档 + 诊断；严格模式经评估不做） | 中 | 注册表 / builder / API.md | R1（可选） | ✅ |
| **R3** | 性能基线：BenchmarkDotNet 项目 + CI 趋势 | 中 | 新项目 + 1 个 CI job | 无 | ✅ |
| **R4** | 平台宿主分层：partial 拆分 + 纯函数化 | 高 | 3 个平台目录 + 测试 | 无 | ✅ |
| **R5** | 验证矩阵与表述对齐（含 arm64 / macOS x64） | 中 | `ci.yml` / README / 新校验脚本 | 无 | ✅ |
| **R6** | 两处边界补强：fs 读路径 symlink、`InvokeOnUiThread` 超时 | 中 | `OrielBuiltInFsCommands.cs` / `OrielApp.cs` / `WebviewWindow.cs` | 无 | ✅ |
| **R7** | API 兼容性基线（1.0 前的地基） | 低 | 1 个包 + `PublicAPI.*.txt` | 无 | ✅ |

**推荐顺序（单人）**：`R0 → R1 → R2 → R3 → R5 → R6 → R7 → R4`

R4 放在最后不是因为它最不重要，而是它改动面最大、最容易与任何一条在途分支冲突——等前面几批把"可自动验证的护栏"补齐之后再动它，返工成本最低。

**投入产出比排序**：R1（小改动，去掉最多人改到的重复）> R3（一次性投入，锁住卖点）> R5（一次性投入，消灭"文档说谎"）> R0 > R6 > R2 > R7 > R4。

---

## 3. 逐批动作与验收

### R0 — 零成本清理

**动作**

1. `src/OrielWeb/Ipc/OrielCommandDispatcher.cs:377-406`：`DescribeForPage` 的 `<summary>`/`<remarks>` 错位挂在 `ErrorReply` record 上，形成两条 `<summary>` 叠在同一成员。把第一段移到 `DescribeForPage` 上方，record 只留自己的注释。
   → 编译器不会报（`NoWarn` 里有 `CS1591`），但 XML 文档会丢条目，`dotnet build` 产出的 `.xml` 里查得到缺口。
2. `.github/workflows/ci.yml` 里 **7 处**逐字相同的 `Cache NuGet packages` 步骤（每 job 8 行）抽成 `.github/actions/setup-dotnet-nuget/action.yml`（composite）。键表达式保持 `hashFiles('**/*.csproj', 'Directory.Build.props')` 不变。
3. 公开 API 文档策略二选一并写进 README「构建与发布」：
   - **保持 `CS1591` 抑制**，在 README 注明"公开成员允许无 XML 注释，注释只写不显然的部分"；
   - 或分批补齐注释后再去掉抑制。**注意**：`src/` 开了 `TreatWarningsAsErrors`，直接删 `NoWarn` 会让构建立刻失败——这不是"暴露一批警告"，是"构建红"。所以真要走第二条，必须先补齐再删，且一次只对一个目录。

   两者选哪个都行，**不能保持沉默**——本项目的其它地方都在"把隐式约定写成显式契约"。

**验收**

```bash
dotnet build OrielWeb.slnx -c Release          # 0 警告
dotnet test tests/OrielWeb.Tests -c Release    # 1476 通过
dotnet format OrielWeb.slnx --verify-no-changes
git diff --stat .github/workflows/ci.yml       # 净减少 ≥ 30 行（7 × 9 行 → 7 行调用 + action.yml）
```

---

### R1 — 内建命令族收成一个循环

**问题**：`HandleInvokeAsync`（303 行）里有 **10 段结构完全相同**的分支——`OrielBuiltInWindowCommands`(118) / `Tray`(140) / `Event`(164) / `Path`(189) / `Fs`(207) / `Dialog`(233) / `Platform`(259) / `App`(284) / `WindowApi`(307) / `Menu`(329)。每段 20–30 行：`TryGet` → `window is null` 检查 → `try/catch` → `ReplyOk/ReplyError`。第 11 族要复制第 11 遍，且"谁先谁后"只由代码位置表达（`win.*` 必须先于应用路由）。

**动作**

1. 抽 `IBuiltInCommandFamily`：

```csharp
internal interface IBuiltInCommandFamily
{
    /// <summary>族名，只用于诊断与错误文案（"win" / "fs" …）。</summary>
    string Name { get; }
    /// <summary>是否需要来源窗口（path.* 为 false，其余为 true）。</summary>
    bool RequiresWindow { get; }
    /// <summary>命中时把窗口闭包进委托；未命中返回 false。</summary>
    bool TryGet(string commandName, IOrielWindowControl? window, out Func<JsonElement, object?> handler);
}
```

把窗口作为 `TryGet` 的入参而不是 handler 的入参，是为了让 10 个族的**调用形态完全一致**——现在它们本来就是 `(window, args)` 形状的委托，只是外层各自写了一遍判空与 try/catch。

2. 用 `static readonly IBuiltInCommandFamily[] s_families` 声明顺序，**顺序即语义**，在数组处写一行注释说明为什么 `win` 在最前。
3. `HandleInvokeAsync` 里只剩一个循环 + 统一的 `NO_SOURCE_WINDOW` / `DescribeBuiltInFailure` 处理。
4. 现有 10 个 `OrielBuiltInXxxCommands.TryGet` **原样保留**（它们已是纯函数，签名不变），只是外层从 10 段变成 1 段。

**验收**

- `HandleInvokeAsync` ≤ 150 行（现值 303）；
- **新增** `BuiltInFamilyOrderTests`：断言 `win.*` 先于应用路由被命中（构造一个同名应用命令，断言拿到的是内建实现）；断言不带窗口时 `path.*` 不被 `no_source_window` 拦下（现值如此，改名不是本批目标）、`fs.*` 报 `no_source_window`；
- 现有 `DispatcherTests`、`BuiltIn*CommandTests` 全绿，**不加不改**——它们是行为不变的证据。

---

### R2 — 命令注册边界如实化

**问题**（三条，同一根因：注册表是进程全局静态）：

1. `OrielCommandRegistry`（`src/OrielWeb/Ipc/OrielCommandRegistry.cs:11`）是 `static`，`BuildIndex()` 在 dispatcher 构造时快照。`OrielCommandDispatcher` 的类注释却说"同一进程里创建多个应用时它们互不干扰"——那只在实例与 JSON 上下文层面成立，**命令名空间是全局的**。
2. 生成器在每个程序集打 `[ModuleInitializer]` 注册**该程序集全部** `[OrielCommand]`。跨程序集同名命令在模块初始化期抛 `InvalidOperationException`，宿主看到的是 `TypeInitializationException`，栈不指向业务代码。
3. **静态命令不受 `AddCommands<T>()` 约束**：`RequiresTarget == false` 时 dispatcher 直接调用，不查 factory。`samples/OrielDemo/CapabilityProbeCommands` 就是活例——它只在 selftest 分支才 `AddCommands`，但命令一直可达。开发者会把 `AddCommands` 读成"暴露开关"，实际静态命令恒可达；配合 `Allow("*")` 就是意外暴露面。

**动作**

1. **先改文档**：API.md §4 增加一段"命令的注册语义"，明确写死两条事实——静态命令在程序集加载时自动注册、无法通过 `AddCommands` 排除；`AddCommands<T>` 只影响实例命令的实例工厂。这是当前行为的**如实描述**，不是新设计。
2. **诊断**：`OrielAppBuilder.Build()` 时扫描全局索引，把"索引里有、但本应用未 `AddCommands` 的实例命令类型"写进 `OrielDiagnostics`（仅 Debug 输出，不抛）——让"我能调但没注册"这件事可见。
3. **opt-in 严格模式**（默认关，零破坏）：`UseStrictCommandRegistration()`
   - 开启后，dispatcher 在调用前校验 `route.Router.DeclaringType` 是否在本应用的注册集合里（含静态命令）；
   - 未注册则回 `unknown_command`，**不泄露类型名**（当前文案会带类型名）；
   - 需要生成器给 `IOrielCommandRouter` 补一个 `Type DeclaringType { get; }`，属内部接口，不影响公开面。
4. **冲突聚合**：`BuildIndex()` 改为收集全部冲突后一次性抛，错误信息列出所有重名项及其类型，避免"修一个再撞一个"。

**验收**

- 新增 `CommandRegistrationTests`：两个手写 router 注册同名命令 → 断言异常文案里**同时**列出两个类型名；
- 新增 `StrictRegistrationTests`：未注册的静态命令在严格模式下报 `unknown_command`，默认模式仍可调用（锁住"默认零破坏"）；
- 三平台 selftest 全绿（demo 未开严格模式，行为不变）。

**实施结果（2026-10-23，随 0.4.6 交付）**

| 动作 | 状态 | 说明 |
|---|---|---|
| 1 文档如实化 | ✅ | API.md §4 新增「命令的注册语义」：静态/实例两种注册时机、`AddCommands` 不是暴露开关 |
| 2 诊断 | ✅ | `OrielCommandRegistry.UnregisteredTargetTypes` + `Build()` 写诊断；2 条单测 |
| 3 opt-in 严格模式 | ⏭ **不做** | 静态类**不能作泛型类型实参**（`CS0718`），`AddCommands<T>()` 的 `new()` 约束也不接受它——要做严格模式必须额外提供 `AddStaticCommands(Type)` 这种以 `Type` 为参的入口。收益只是"把刻意设计变成可选限制"，代价是一个为边缘场景而生的丑 API。等出现真实的多应用共存需求再评估 |
| 4 冲突聚合 | ⏭ **不做** | `AddRouter` 在注册期立即抛是 0.4.3 刻意建立的保证（"启动即崩，不静默"）。改成"收集后聚合"会把失败时机从程序集加载推迟到 `Build()`，削弱那条保证。同程序集内的重名已由生成器在编译期拦下，跨程序集的"修一个撞一个"属于低频路径 |

> 结论：R2 实际交付的是**"把语义写清楚 + 让差集可见"**，不是"改行为"。原本设想的严格模式缺一个可用的注册入口——这是语言约束决定的，不是实现取舍。

---

### R3 — 性能基线

**问题**：全仓 0 处 BenchmarkDotNet。"零反射 IPC""O(1) 分发"没有任何数字支撑；而热路径每条回执要经过 `ArrayBufferWriter` + `Utf8JsonWriter` + `Encoding.UTF8.GetString` 三次分配（`OrielCommandDispatcher.ReplyOk`）。

**动作**

1. 新建 `benchmarks/OrielWeb.Benchmarks`（`net10.0` + `BenchmarkDotNet`），**不进** `OrielWeb.slnx` 的默认构建路径（避免拖慢每次 `dotnet build` 与 `dotnet format`），由 benchmark job 单独 restore/build。
2. 四个基线，每个都要有"为什么是它"的一句话：

| 基准 | 断言的是什么 |
|---|---|
| `InvokeRoundTrip` | 一条简单命令从入站 JSON 到回执字符串的总分配与耗时 |
| `CommandDispatch` | `_routes` 查表是否真的 O(1)：用手写 router 注入 10 / 100 / 1000 条命令，看单次分发耗时是否随规模上升 |
| `AssetLookup` | `EmbeddedAssetStore` 的资源表查找与路径归一 |
| `PathScopeMatch` | `OrielPathScope.IsMatch` 在 10 / 100 条 glob 下的线性退化 |

3. CI 加一个 `benchmark` job（只在 `main` 与 `workflow_dispatch` 跑，PR 上不跑以免噪声），输出 Markdown 表到 `$GITHUB_STEP_SUMMARY`；**先只记录不设阈值**，积累两周后再定"回归 > 20% 告警"。
4. 在 README「验证账」里加一行指向基准结果，让"快"这个说法有落点。

**验收**

```bash
dotnet run -c Release --project benchmarks/OrielWeb.Benchmarks -- --filter '*'
```

能跑出四张表；CI 的 `benchmark` job 在 `main` 上有历史趋势。

**实施结果（2026-10-23，随 0.4.7 交付）**

四个基准全部落地，并把首次测量值写进了 README。两个发现值得记下来：

| 发现 | 数据 | 处置 |
|---|---|---|
| **1000 条路由与 1 条路由耗时相同**（1.19 vs 1.24 μs） | "O(1) 分发"第一次有了证据，不再是从 `Dictionary` 推断 | 记为基线 |
| **前缀通配匹配每次分配 32 B** | `pattern[..^1]` 是 `Substring`，而 `Allow("todo.*")` 是应用最常见的写法、每条 invoke 都过这里 | **已修**：换成 `ReadOnlySpan` 切片，32 B → 0，耗时 32 → 25 ns |
| 回执序列化每次 3.8 KB | `ArrayBufferWriter` + `Utf8JsonWriter` + `string` 三次分配 | 记为待优化，本轮不动（改动会碰回执协议路径） |

> 这条 32 B 的修复说明基准的价值：它是**每条消息都在交**的税，但没有任何测试会红——
> 只有分配列会说话。

---

### R4 — 平台宿主分层

**问题**：`Win32WindowHost` 4,002 行 / `MacOSWindowHost` 3,310 / `LinuxWindowHost` 2,765，各自 137–160 个方法，实现 `IWindowBackend` 的约 100 个成员。这一层占全仓 54.8% 的代码，却几乎没有托管测试——因为逻辑与 P/Invoke 搅在一起，构造不出测试替身。

**动作（分三步，每步都可单独合入）**

**R4a 按关注点拆 partial 文件**（纯搬运，零逻辑改动）

| 拆分文件 | 放什么 | 目标行数 |
|---|---|---|
| `XxxWindowHost.cs` | 字段、构造、生命周期、`IWindowBackend` 属性转发 | ≤ 500 |
| `XxxWindowHost.Geometry.cs` | 尺寸/位置/DPI/显示器换算、`Resize`/`MoveTo` | ≤ 700 |
| `XxxWindowHost.Chrome.cs` | 无边框、标题栏、装饰、置顶、全屏、特效 | ≤ 700 |
| `XxxWindowHost.Content.cs` | webview 装配、导航、脚本注入、资源 scheme | ≤ 800 |
| `XxxWindowHost.Input.cs` | 鼠标/键盘/拖动/拖放/光标 | ≤ 700 |
| `XxxWindowHost.WndProc.cs`（各平台对应物） | 消息/事件回调分发 | ≤ 500 |

验收：`wc -l` 每个文件 ≤ 800；**行为零变化**——三平台 `--selftest` 全绿，且输出与**拆分前的基线**逐行一致（不是三平台之间一致，它们的输出本来就不同）。

**R4b 纯函数化**（这一步才产生可测性）

把不含宿主状态的计算抽成 `internal static`：
- 逻辑 ↔ 物理像素换算与客户区/整窗转换（Windows 已有 `LogicalPixels`，把 macOS/Linux 的对应物补齐到同一形状）；
- 平台枚举 ↔ 库枚举的映射（`GdkWindowEdge`、`NSWindowStyleMask`、`WINDOWCOMPOSITIONATTRIB` 这类）；
- 消息/事件的解码（从 `nint lParam` 到 `(x, y)` 这类）。

每条抽出来就配一组单测，命名沿用 `LogicalPixelsTests` / `GdkStructLayoutTests` 的先例。

验收：平台层新增 ≥ 60 个单测；`dotnet test` 总数从 1,476 涨到 1,540+，仍 0 跳过。

**R4c 抽 conformance 项目**（可与 R4a 并行）

把 demo 里的 `--selftest nav/ipc/threading/tray/geometry/window/exit` 抽到 `tests/OrielWeb.Conformance`，demo 只保留一个转发入口。理由：这些自检是事实上的跨平台契约测试，却住在示例工程里——任何一次 demo 重构都可能顺手削弱唯一的语义守卫。

验收：三个 `tools/verify-*.sh|ps1` 仍只调用一行；`--selftest` 的输出格式不变（外部脚本按行匹配，不能改）。

**风险**：R4 是本计划唯一"可能引入回归"的批次。缓解：R4a 只做文件搬运（`git mv` + 补 `partial` 关键字），一次提交一类文件，每个提交都能单独回滚。

---

### R5 — 验证矩阵与表述对齐

**问题**：README 平台矩阵里 "Linux arm64：编译通过 + xvfb 冒烟" 在仓库内**没有对应证据**——`ci.yml:204-207` 只有 IL 交叉编译，`smoke-linux` 跑在 x64 runner 上。另外 `CHANGELOG` 自己记着"CI 只跑 `osx-arm64`，Intel Mac 的 `NSRect` ABI 崩溃它永远看不见"——那是**已经发生过**的盲区，不是假想。

**动作**

1. **先测 runner 可用性，再改矩阵**（不要先写结论）：
   - arm64：验证 `ubuntu-24.04-arm` 托管 runner 是否对本仓库可用；可用则加 job 跑 `tools/verify-linux.sh --stages selftest --rid linux-arm64`；不可用则用 `qemu-user-static` + 交叉依赖，或降级表述。
   - macOS x64：验证 `macos-13`（Intel）是否仍在托管矩阵里；在则加一个 `smoke-macos-x64`，只跑 `--selftest nav` + `--selftest window`（不必全套，目的是覆盖 ABI 与消息循环）。
2. **矩阵改成可核对的形态**：README 的每一行状态后面加一个脚注标记，指向具体 job 名或 `docs/verification/` 下的证据文件（含日期）。
3. **加一致性校验**：`tools/verify-matrix.ps1` 读 README 表格与 `ci.yml` 的 job 名，任何"声称已验但找不到 job/证据"的条目直接失败。接进 `test` job。
   → 这是本项目一贯的做法（把承诺变成断言），只是这次的对象是文档本身。

**验收**

```bash
pwsh tools/verify-matrix.ps1     # 退出码 0，且能对故意篡改的矩阵报错（用 --self-check 造一份）
```

矩阵里不再出现"仓库里无法复现"的状态描述。

**实施结果（2026-10-23，随 0.4.8 交付）**

| 动作 | 状态 | 说明 |
|---|---|---|
| 1 核实 runner | ✅ | `macos-13` 已于 **2025-12-04 退役**；Intel 的替代品是 `macos-15-intel`（可用到 2027 年秋）。Linux arm64 用 `ubuntu-24.04-arm`（公开仓库免费，原生 4 vCPU） |
| 2 补 job | ✅ | `smoke-linux-arm64` 复用 `verify-linux.sh --stages selftest`；`smoke-macos-x64` 复用 `verify-macos.sh` 全套（脚本按 `uname -m` 选 RID，Intel 上自动走 `osx-x64`） |
| 3 矩阵可核对 | ✅ | 矩阵加"证据"列，每行用 `ci:<job>` 引用 |
| 4 一致性校验 | ✅ | `tools/verify-matrix.ps1` 接进 `test` job：引用不存在的 job 直接红；反向提示"有 job 没进矩阵" |

> **两个新 job 的矩阵状态写的是"待首轮绿"，不是"已通过"**——本地无法验证它们，
> 所以不敢写 ✅。这正是这一批要消灭的那类话：状态必须与仓库里能跑出来的东西对齐。
> 首轮 CI 通过后再把 ⚠️ 改成 ✅，这是唯一诚实的顺序。
>
> `smoke-macos-x64` 顺带补上了一个**已经发生过**的盲区：0.4.3 修的 NSRect ABI 崩溃
> （Intel 上建第一个窗口就 SIGABRT）正是因为 CI 只跑 `osx-arm64`。新增这条线之后，
> 同类问题会在 PR 上就暴露。

---

### R6 — 两处边界补强

> **本批是计划里唯一有意的行为变更。** R0–R5 全部要求"可观测行为零变化"；R6.1 会让原本能通过的读操作被拒（符号链接指向 scope 外），R6.2 只加可选参数、默认行为不变。R6.1 必须写进 `CHANGELOG` 的「修复」并说明影响面，不能当内部重构处理。

**R6.1 fs 读路径解析符号链接**

现状：`OrielBuiltInFsCommands.cs:570` 明确写了"不解析符号链接"，理由是写路径目标通常不存在。这个理由对**写**成立，对**读**不成立。

威胁链：scope 通常允许写 `$APPDATA/**` → 页面（被 XSS 后）在允许目录里创建一个指向 `~/.ssh/id_rsa` 的符号链接 → `fs.readTextFile` 的文本层判定通过 → 越权读取。scope 的存在意义就是防这一层，所以这条不该留。

动作：
- 在 `AuthorizeRawPath` 内部、**`Path.GetFullPath` 之后、scope 判定之前**插一步：路径存在则 `File.ResolveLinkTarget(path, returnFinalTarget: true)`，用解析结果替代 `full` 再做 scope 判定。
  → 顺序不能反。本项目反复强调的"先归一，再判 scope"在这里同样成立：先判 scope 再解析，等于用"链接所在位置"的合法性去担保"链接指向的目标"，绕过依然存在。
- 目标不存在（写命令）时跳过解析，保持现状；
- 解析抛异常（跨卷、权限）时不静默放行，按 `path_not_allowed` 拒绝并给出原因。

验收：新增 `SymlinkScopeTests`——临时目录建 symlink 指向 scope 外文件，`fs.readTextFile` 必须报 `path_not_allowed`。**Windows 上创建符号链接需要 `SeCreateSymbolicLinkPrivilege`**：测试若因权限无法创建，必须 `Assert.Skip("缺少 SeCreateSymbolicLinkPrivilege")` 并把原因打进输出，不允许静默跳过（本项目"跳过的门禁等于没有门禁"）。

**R6.2 `InvokeOnUiThread` 加超时**

现状：`OrielApp.cs:1039` 与 `WebviewWindow.cs:904` 的 `done.Wait()` 无参 = 无限等待。已有 `IsExiting` 前置检查，但覆盖不到"UI 线程卡在一条长命令或原生调用里"。此时后台线程永久阻塞，且没有任何诊断。

动作：`InvokeOnUiThread(Action action, TimeSpan? timeout = null)`，`null` 保持无限等待（零破坏）；超时抛 `TimeoutException`，文案说明"UI 线程可能被长任务占用，见 CommandInvoked 的耗时"。`OrielApp` 与 `WebviewWindow` **两处**都要改，后者转发到前者的实现。

验收：新增 `UiThreadInvokeTimeoutTests`——假后端把投递的回调扣住不执行，断言超时抛出且不泄漏 `ManualResetEventSlim`。

**实施结果（2026-10-23，随 0.4.9 交付）**

| 动作 | 状态 | 说明 |
|---|---|---|
| R6.1 读路径解析链接 | ✅ | 先按"只解析最后一段"实现，写完测试才发现**中间目录是链接**时仍然放行（`allowed/link/passwd` 的最后一段不是链接）——改成**逐段**解析。3 条用例：文件链接越界被拒、目录链接越界被拒、链接指向 scope 内仍可读 |
| R6.2 `InvokeOnUiThread` 超时 | ✅（测试降级） | 加了可选 `timeout`，默认 `null` = 无限等待（零破坏）。**没有**按计划写"假后端"用例：`OrielApp` 没有后端注入点，而 `IPlatformBackend` 有约 100 个成员，为验证一个 `TimeSpan` 造替身不划算。改为钉 API 形状（默认值必须为 `null`）——那才是真正会伤到既有调用方的东西 |

> R6.1 的教训值得单独记：**"解析符号链接"这句话有两个实现层次**（最后一段 / 全路径），
> 只做前者时，最典型的那条攻击路径（目录链接 + 目录内文件）依然通。
> 如果不是先写了"目录链接"那条用例，这个修复会带着一个洞上线。

---

### R7 — API 兼容性基线

**问题**：70 个公开类型，无 `PublicAPI.Shipped.txt`，0.x 阶段可以随意破坏，但每次破坏都是**静默**的——没有任何东西提醒"你刚删了一个公开方法"。

**动作**

1. 引入 `Microsoft.CodeAnalysis.PublicApiAnalyzers`，生成 `PublicAPI.Shipped.txt`（当前全部公开面）与 `PublicAPI.Unshipped.txt`（空）。
2. 规则：新增 API 进 Unshipped；发版时把 Unshipped 合入 Shipped（这一步写进 `CHANGELOG` 的发布纪律）。
3. 分析器设为 **warning 而非 error**（`RS0016`/`RS0017`），避免阻塞开发；但 CI 的 `dotnet build` 会把 warning 打出来。

**验收**：删掉任意一个公开方法，`dotnet build` 出现 `RS0017`；新增一个公开方法而不登记，出现 `RS0016`。

**实施结果（2026-10-23，随 0.4.10 交付）**

| 动作 | 状态 | 说明 |
|---|---|---|
| 引入 PublicApiAnalyzers | ⏭ **换形式** | 实测：`RS0016` 在 `TreatWarningsAsErrors` 下变成 **error**，新加一个公开方法直接构建失败；要恢复得先跑 `dotnet format` 把 **1666 条**一次性灌进 `Unshipped.txt`。0.x 还在长 API，这个摩擦力大于收益 |
| 基线文件 | ✅ | `tests/OrielWeb.Tests/PublicApi.snapshot.txt`：**76 个公开类型 / 747 行**，由 `PublicApiSnapshotTests` 反射生成并比对 |
| 失败可见 | ✅ | 快照不一致 → 测试红，失败信息里直接列出"删除/改动"与"新增"两侧的清单 |

> 换形式的理由：分析器要解决的是"删掉公开 API 时有人喊"，而它在 0.x 的代价是"每加一个 API 都得改 txt 才能构建"。
> 快照测试拿到同样的信号，代价只在测试这一步。**计划里写的实现方式不是圣旨，目标才是。**
>
> 自测过一次：手动删掉快照里 `OrielApp.IsExiting` 那一行，测试如期转红并指出"新增 1 条"——
> 比对确实在工作，不是永远绿。

---

## 4. 排期建议

不按天数承诺，按"可并行单元"排：

| 阶段 | 内容 | 可并行 |
|---|---|---|
| 阶段 1 | R0 → R1 → R2 | R3 可同时开工（不碰同一批文件） |
| 阶段 2 | R3 → R5 | R6 可同时开工 |
| 阶段 3 | R4a（搬运）→ R7 | R4c 可与 R4a 并行 |
| 阶段 4 | R4b（纯化 + 补测，长尾） | — |

每个阶段结束都应该能发一个 0.4.x 补丁版——除 R6.1 外，这些批次**不产生用户可感知变化**，发版说明只需一句"内部结构与验证基线改进"；R6.1 单独按「修复」记录并写清影响面（见 R6 开头的提示）。

---

## 5. 风险与回滚

| 风险 | 触发信号 | 回滚方式 |
|---|---|---|
| R1 改了命令族顺序 | 某个 `win.*` 命令落到应用路由 | 单提交回滚；`BuiltInFamilyOrderTests` 会先红 |
| R2 严格模式被误开 | 用户报"命令找不到" | 默认关，且开启是显式调用；回滚只删一行 |
| R3 基准噪声导致误报 | 同一提交两次跑差异 > 20% | 只记录不设阈值；阈值推迟到有 10 次以上样本 |
| R4 搬运引入回归 | selftest 输出与原基线不一致 | 逐文件 `git mv` 提交，单提交可回滚；R4b 每抽一个函数一个提交 |
| R5 新 runner 不可用 | job 排队超时/镜像缺依赖 | 矩阵降级表述，不留"声称已验"的条目 |
| R6 symlink 解析误拒 | 正常读文件被 `path_not_allowed` | 只对"解析结果与原文不同"的路径重判，相同则走原路径 |

---

## 6. 与既有批次的关系

- B0–B24 是**功能对齐**线（Tauri 2），本计划的 R 系列是**结构健康**线，两者不改同一批文件的部分可以并行。
- R1/R2 会碰 `OrielCommandDispatcher`，与任何"新增内建命令族"的 B 批次冲突概率高——建议 B 系列的下一个批次等 R1 合入后再开。
- R4 与任何平台相关的 B 批次强冲突（三平台宿主），必须串行。
- R3/R5/R7 与 B 系列无交集，可长期并行。

---

## 附录 A：评审发现索引

| 编号 | 发现 | 严重度 | 落在 |
|---|---|---|---|
| 1 | 内建命令分发 228 行 10 段同构复制 | P1 | R1 |
| 2 | 注册表进程全局静态；跨程序集同名冲突在模块初始化期炸 | P1 | R2 |
| 3 | 静态命令绕过 `AddCommands`，与"注册=暴露"心智冲突 | P1 | R2 |
| 4 | 平台层 55% 代码压在三巨型单类，几乎无托管测试 | P1 | R4 |
| 5 | 无性能基准，卖点无量化 | P2 | R3 |
| 6 | macOS x64 只编译不运行（NSRect ABI 崩溃已证明代价） | P2 | R5 |
| 7 | README 的 arm64 "xvfb 冒烟"在仓库内无证据 | P2 | R5 |
| 8 | fs scope 不解析符号链接（读路径可绕过） | P2 | R6.1 |
| 9 | `InvokeOnUiThread` 无超时，UI 卡死则调用方永久阻塞 | P3 | R6.2 |
| 10 | 无 API 兼容性基线（70 个公开类型） | P2 | R7 |
| 11 | dispatcher 注释错位（两条 `<summary>` 叠在同一成员） | P3 | R0.1 |
| 12 | CI 7 处复制 NuGet 缓存样板 | P3 | R0.2 |
| 13 | `CS1591` 被抑制，公开 API 可无文档 | P3 | R0.3 |
| 14 | selftest（事实上的契约测试）住在示例工程里 | P2 | R4c |
