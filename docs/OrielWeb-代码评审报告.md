# OrielWeb 代码评审报告

> 评审对象：`OrielWeb.zip`（20 MB，源码 5,865 行 C# + 3 份桥接 JS）
> 评审日期：基于交付包快照
> 评审方式：全量源码走查 + 关键结论实测复现（Linux `GetApartmentState` 实机验证、桥接 JS 用 Node 22 复现）

---

## 一、总体结论

**方向正确，工程骨架扎实，但存在 3 个阻断级缺陷，导致 macOS/Linux 完全无法启动、三平台的就绪机制全废。**

这个项目最有价值的部分不是 API 设计，而是 `DECISIONS.md` 里那几条用真金白银换来的互操作教训——手工 CCW 的 AddRef 规则、ComWrappers 在 AOT 下的三个致命问题、ObjC msgSend 的寄存器对齐策略、GTK 信号 trampoline。这是教科书上买不到的一手资料，说明作者是真的动手跑过、崩过、再爬起来的。

但也正因为**验证只覆盖了 Windows 主路径**，跨平台代码停留在"编译通过"的层面，而编译通过和能运行之间，隔着三个必然崩溃点。

| 维度 | 评价 | 说明 |
|------|------|------|
| 架构分层 | ★★★★☆ | App/Window/Ipc/Bridge/Assets/Platform 边界干净，抽象合理 |
| 平台互操作 | ★★★★☆ | 纯 P/Invoke 路线真功夫，踩坑记录含金量高 |
| IPC 设计 | ★★★☆☆ | 零反射路线选得对，但实现打了折扣，全局静态状态破坏隔离 |
| 跨平台可用性 | ★☆☆☆☆ | **macOS/Linux 的 `Run()` 必然抛异常，从未真正启动过** |
| 前端桥接 | ★★☆☆☆ | **三平台 `ready` Promise 全部是 rejected，事件时序错误** |
| 测试覆盖 | ★★☆☆☆ | 33 个测试全部集中在 IPC 纯逻辑层，平台层与 JS 层零覆盖 |
| 交付卫生 | ★★☆☆☆ | 20 MB 包里 98% 是构建产物 |

**一句话总结**：Windows 主路径做到了可运行，跨平台部分是"写完了但没通电"。三个 P0 缺陷修复成本都很低（合计约 20 行代码），但修之前，README 里"平台支持"那张表格的 ✅ 是不成立的。

---

## 二、阻断级缺陷（P0）

### P0-1 · `Run()` 的 STA 检查让 macOS/Linux 100% 无法启动

**位置**：`src/OrielWeb/App/OrielApp.cs:33-38`

```csharp
public void Run()
{
    if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
    {
        throw new InvalidOperationException(
            "OrielWeb 要求主线程为 STA 线程：请在 Main 方法上标注 [STAThread]。");
    }
    _backend = PlatformBackendFactory.Create();
    ...
```

**问题**：`ApartmentState` 是 Windows COM 的概念，在 Unix 平台上 .NET 永远返回 `Unknown`。`[STAThread]` 特性在 Linux/macOS 上被完全忽略。

**实测验证**（本机 Linux 6.6 内核 + .NET 运行时）：

```
ApartmentState = Unknown
IsSTA = False
OS = Linux 6.6.117-45.11.3.tl4.x86_64
```

这意味着 `Run()` 的**第一行**就抛异常。macOS 同理（同为 Unix）。这不是"待真机验证"，是"一旦运行必然失败"。

**为什么 CI 没拦住**：`.github/workflows/ci.yml` 里 `compile-macos` / `compile-linux` / `aot-macos` 三个 job 只执行 `build` / `publish`，**没有任何一步运行产物**。编译通过 ≠ 能启动，这是典型的 CI 断言强度不足。

**修复**（约 1 行）：

```csharp
// STA 只对 Windows 的 COM/WebView2 有意义；Unix 平台 GetApartmentState 恒为 Unknown
if (OperatingSystem.IsWindows() && Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
{
    throw new InvalidOperationException(
        "OrielWeb 在 Windows 上要求主线程为 STA 线程：请在 Main 方法上标注 [STAThread]。");
}
```

---

### P0-2 · 三平台桥接 JS 的 `oriel.ready` 是 rejected Promise（TDZ 自引用）

**位置**：`src/OrielWeb/Bridge/OrielBridgeJs.cs:17`、`Platform/MacOS/MacOSBridgeJs.cs:24`、`Platform/Linux/LinuxBridgeJs.cs:22`（三份全中）

```js
const oriel = {
    platform: 'windows',
    ready: new Promise((resolve) => { oriel._resolveReady = resolve; }),   // ← 这里
    ...
};

window.oriel = oriel;
document.dispatchEvent(new Event('orielready'));
oriel._resolveReady();   // ← _resolveReady 是 undefined，抛 TypeError
```

**问题**：对象字面量求值期间，`const oriel` 处于 TDZ（暂时性死区）。Promise 构造器会**同步**执行 executor，此时读取 `oriel` 抛 `ReferenceError`——该异常被 Promise 捕获，于是 `ready` 变成一个 rejected Promise，`_resolveReady` 从未被赋值。

**实测复现**（Node 22.13，一行即可验证）：

```js
const oriel = {
  ready: new Promise((r) => { oriel._resolveReady = r; }),
};
// → ready REJECTED: ReferenceError Cannot access 'oriel' before initialization
```

**影响范围**：
- `README.md:72` 推荐的首个用法 `await window.oriel.ready;` → 直接抛错
- `samples/OrielDemo/wwwroot/app.js:129` 的 `await window.oriel.ready` → 落进 catch，demo 的 IPC 徽章会显示 **"IPC 失败：Cannot access 'oriel' before initialization"**
- 后续 `oriel._resolveReady()` 抛 TypeError 中断 IIFE 尾部（`window.oriel` 和 message listener 已装好，所以 `invoke()` 本身仍可用——这解释了为什么手工验证时"IPC 往返通过"却没发现这个 bug）

**修复**（先建 Promise，再建对象）：

```js
let resolveReady;
const ready = new Promise((resolve) => { resolveReady = resolve; });

const oriel = { platform: 'windows', version: '0.1.0', ready, invoke(...) {...} };

window.oriel = oriel;
resolveReady();
```

---

### P0-3 · 就绪事件 `orielready` 永远派发不到监听者

**位置**：三份 BridgeJs 的 `document.dispatchEvent(new Event('orielready'))`

**问题**：桥接脚本的注入时机是 **document 创建时**：
- Windows：`AddScriptToExecuteOnDocumentCreated`
- macOS：`WKUserScriptInjectionTimeAtDocumentStart`（`MacOSWindowHost.cs:145`）
- Linux：`webkit_user_script_new`（`LinuxWindowHost.cs:95`）

此时页面自己的脚本尚未执行，没有任何监听器注册。等到用户脚本执行 `document.addEventListener('orielready', ...)` 时，事件早已派发完毕。**页面永远收不到这个事件。**

所以三平台实际上存在两套都坏掉的就绪机制：`ready` Promise 被 TDZ 搞成 rejected（P0-2），`orielready` 事件死于时序。文档里承诺的就绪语义完全不可用。

**修复建议**：不要用一次性事件。修好 P0-2 后，`ready` Promise 本身语义就足够了；如果确实需要事件，应在页面可观测的时机（`DOMContentLoaded` 或 `queueMicrotask`）派发：

```js
const announce = () => document.dispatchEvent(new Event('orielready'));
if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', announce, { once: true });
} else {
    queueMicrotask(announce);
}
```

---

## 三、严重缺陷（P1）

### P1-1 · 三处 `[UnmanagedCallersOnly]` 入口缺少 try/catch，违反了自家纪律

`DECISIONS.md` 明确写着：

> 所有 `[UnmanagedCallersOnly]` thunk 全身 try/catch，异常一律转成 HRESULT 返回，绝不外泄（外泄 = 进程 fail-fast）。

但有三处遗漏，且都能被用户代码触发：

| 位置 | 逃逸路径 | 后果 |
|------|---------|------|
| `Win32WindowHost.WindowProc:185` | → `HandleMessage` → `Closing?.Invoke(closeArgs)`（**用户的事件处理器**） | 用户 `Closing` 里抛异常 → 进程 fail-fast，不可捕获 |
| `WindowsPlatformBackend.MessageWindowProc:87` | → `action()`，而 `OrielApp.PostToMainThread(Action)` 是 **public API** | 用户回调抛异常 → 进程崩溃 |
| `LinuxSignalHandlers.PumpIdleTrampoline:161` | → `action()`，同样来自 public 的 `PostToMainThread` | 同上 |

对比之下，`WebView2NativeCallbacks.cs` 里 7 个 thunk 全部老老实实 try/catch 了——说明纪律是有的，只是没有贯彻到 Win32 proc / 主线程泵这几处。

**修复**：给这三个入口各加一层兜底 try/catch，异常写成 HRESULT（Win32 侧返回 0 或 `E_FAIL`）并记录。

---

### P1-2 · `_assetHost` 硬编码，忽略构建器上用户配置的 host

**位置**：`Platform/Windows/Win32WindowHost.cs:58`

```csharp
_assetHost = "app.oriel"; // 与构建器默认一致；M1 用固定虚拟主机
```

而 `OrielAppBuilder.UseEmbeddedAssets(string host = "app.oriel", ...)`（`OrielAppBuilder.cs:27`）**明确允许用户自定义 host**，且该值存进了 `AssetHost` 属性——然后被平台层无视。

**后果**：用户写 `.UseEmbeddedAssets("myapp.local")` → 虚拟主机映射到 `myapp.local`，但 `Navigate` 依然指向 `https://app.oriel/index.html` → 白屏 / 404。

**修复**：从 `OrielApp` 或 options 取 `builder.AssetHost` 传入 host 构造函数。顺带检查 macOS/Linux 宿主是否有同样问题。

---

### P1-3 · 全局静态可变状态，破坏多实例隔离

| 静态状态 | 位置 | 风险 |
|---------|------|------|
| `OrielJson.s_context` | `Ipc/OrielJson.cs:17` | 进程级单例；两个 `OrielApp` 用不同 DTO 上下文会互相覆盖 |
| `OrielCommandRegistry.s_routers` | `Ipc/OrielCommandRegistry.cs:9` | 进程级全局路由表，任何 App 都无法隔离命令空间 |

`OrielAppBuilder.Build()` 是公开的、可以构造多个 App，但这些全局状态决定了它们实际上共享同一个 JSON 上下文。测试侧被迫用 `[Collection("IpcSerial")]` 把 `OrielJsonTests` 和 `DispatcherTests` **串行化**来规避污染（见两个测试文件顶部的注释）——这是设计信号：需要靠禁用并行来让测试跑通时，通常意味着状态该被实例化了。

**建议**：把 `JsonSerializerContext` 挂到 `OrielApp` 实例上，经 dispatcher 传递；`AddRouter` 的注册表也应该是 App 级（源生成器的 `[ModuleInitializer]` 可以只做"登记到类型清单"，实例化再绑定）。

---

### P1-4 · 命令名冲突静默通过，且生效顺序不确定

**位置**：`Ipc/OrielCommandDispatcher.cs:54-73`

```csharp
foreach (var router in OrielCommandRegistry.Snapshot())
{
    var index = router.Route(name);
    if (index < 0) continue;
    // ... 命中即执行并 return
}
```

若两个命令类都标注了 `[OrielCommand("todo.add")]`，第一个命中的生效，另一个**永远不可达，且没有任何警告或异常**。而"第一个"取决于 `[ModuleInitializer]` 的执行顺序——这是编译器/运行时的实现细节，不保证稳定。

这类拼写冲突在实际项目里相当常见（尤其多人协作时）。**建议**：在 `AddRouter` 或 dispatcher 构造时建一张全局命令名索引，发现重复立即抛异常。

---

### P1-5 · Linux 平台的窗口状态注册表永不清理（泄漏 + 潜在错乱）

**位置**：`Platform/Linux/LinuxSignalHandlers.cs:23-29`

```csharp
internal static void UnregisterWindow(nint window) => WindowStates.Remove(window);
internal static void UnregisterWebview(nint webview) => WebviewStates.Remove(webview);
internal static void UnregisterManager(nint manager) => ManagerStates.Remove(manager);
```

这三个方法**定义了但全项目零调用**（已全量 grep 确认）。

- `WindowStates` 在 `OnDestroyTrampoline` 里通过 `Remove(widget, out host)` 顺带清掉了，OK
- 但 `WebviewStates` 和 `ManagerStates` 只增不减 → 每个 `nint → LinuxWindowHost` 条目都是强引用，**窗口关闭后整棵对象树（含 webview 托管包装、拖拽状态、标题缓存）永不释放**

更危险的是指针复用：GTK 释放 webview 后，同一地址可能被新窗口的 webview 复用。此时字典里的陈旧映射会把新窗口的信号路由到**已销毁的 host**——属于 ABA 问题，表现为随机崩溃或串窗口行为。

macOS 侧（`MacOSObjCClasses` / `MacOSWindowHost.OnWindowWillClose`）也有类似的注册表，建议一并排查。

---

## 四、设计与性能问题（P2）

### P2-1 · "零反射"打了折扣：把编译期可知的类型分派推迟到运行期

生成器**编译期就完全知道**每个参数的类型（`OrielCommandGenerator.cs:66` 已经在读 `p.Type`），但生成的代码是：

```csharp
var __arg_text = global::OrielWeb.Ipc.OrielJson.GetRequiredArg<global::System.String>(args, "text");
```

进到 `OrielJson.DeserializeArg<T>` 后，用 `switch (typeof(T))` + 16 个 `case var t when t == typeof(X)` 做运行期类型分派（`OrielJson.cs:78-161`）。这里有一个认知落差：

> README 写的"基元类型手写 Utf8Json 读写" —— 实际是"读 `JsonElement` 后按 `typeof` 分派"。

**生成器完全可以直接生成 `element.GetInt32()` / `Utf8JsonReader` 直读代码**，把运行期分派变成编译期已决，这才是"零反射"应有的水平。当前实现每次参数提取要走一条最长 16 级的 `if-else` 比较链。

绝对耗时在纳秒级（`typeof` 比较是指针比较），**不是性能瓶颈**，但它浪费了源生成器的核心价值，属于方向性问题。

### P2-2 · DTO 参数存在双重解析与多余字符串分配

**位置**：`Ipc/OrielJson.cs:174`

```csharp
return (T)JsonSerializer.Deserialize(element.GetRawText(), typeInfo)!;
```

`element.GetRawText()` 会**分配一个完整字符串副本**，然后 STJ 再解析一次。而 `JsonElement` 本身已经可以直接反序列化：

```csharp
return (T)element.Deserialize(typeInfo)!;   // 零中间字符串
```

热路径上的实打实浪费。同理 `WriteResult` 里 `value.GetType()` + `Resolve(type)`（`OrielJson.cs:264-265`）每次 DTO 返回都要查一次 `JsonTypeInfo` 表，也可由生成器直接写入具体 `JsonTypeInfo`。

### P2-3 · "O(1) switch 分发"的说法不准确

`HandleInvokeAsync` 每条消息的完整成本：

1. `OrielCommandRegistry.Snapshot()` → `lock` + **数组分配**（`OrielCommandRegistry.cs:21-27`）
2. 遍历**所有** router，逐个调用 `Route(name)` 做字符串 switch
3. 命中后执行

所以实际复杂度是 **O(router 数量)**，每次 invoke 还有一次锁和一次数组分配。生成器已经给每个命令分配了全局唯一 index（`globalIndex`），完全有能力生成一张全局 `Dictionary<string, (router, index)>` 或一个全局 switch，做到真正的单次 O(1) 命中。修复成本很低，收益直观。

### P2-4 · 命令实例的线程模型未定义，demo 自己就违反了

`_targets` 里的命令单例被所有并发 invoke 共享；`router.InvokeAsync(...)` 走的是 `ConfigureAwait(false)`，**命令体可能在线程池线程上执行**。

`samples/OrielDemo/TodoCommands.cs` 的 `_items`（`List<TodoItem>`）和 `_nextId++` 都没有同步保护：

```csharp
return new TodoItem(_nextId++, text, Done: false, ...);   // 并发下会产出重复 Id
_items.Add(item);                                          // List<T> 非线程安全
```

而这个类的 `[OrielCommand]` 方法正被前端并发点击触发。README 和文档**完全没有说明"命令方法必须是线程安全的"**。这属于必须写进文档的契约，否则用户会踩坑。

### P2-5 · 生成器 `MakeSafeIdentifier` 存在类型名碰撞

**位置**：`OrielCommandGenerator.cs:107-118`

```csharp
foreach (var c in text.ToString())
    if (!char.IsLetterOrDigit(c) && c != '_') text.Replace(c, '_');
```

非字母数字字符一律替换为 `_`，于是 `A.B` 和 `A_B` 归一成同一个标识符。随后：

```csharp
.GroupBy(m => m.TypeNameSafe)              // 两者被并进同一组
...
var model = commands.First();              // TypeDisplay 取自组内第一个
source.AppendLine($"public global::System.Type? TargetType => typeof({model.TypeDisplay});");
```

结果：路由指向**错误的类型**，静默生成编译不通过或行为错误的代码。虽然触发条件较窄（需要命名空间/嵌套类型名恰好碰撞），但属于静默错误，很难排查。

**修复**：用完全限定名（`global::Ns.A.B`）作为分组 key，生成的类名附加稳定 hash 后缀。

### P2-6 · `ReplyError` 手写 JSON 转义不完整

**位置**：`Platform/Windows/Win32WebView2Ipc.cs:61-62`

```csharp
private static string Encode(string text)
    => "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r") + "\"";
```

只处理了 `\\ \" \n \r`。**未处理 `\t`、`\b`、`\f` 和所有 U+0000–U+001F 控制字符**。异常消息里若出现制表符或控制字符（比如嵌套了第三方库的格式化异常文本），生成的就是非法 JSON，`PostWebMessageAsJson` 会直接失败，前端连错误提示都收不到。

有意思的是 Linux/macOS 版本用的是正解：

```csharp
PostJson($"{{\"__oriel\":\"result\",\"id\":0,\"ok\":false,\"error\":{JsonSerializer.Serialize(ex.Message)}}}");
```

直接 `JsonSerializer.Serialize` 就完了，没必要手写。**建议统一成 Linux 的写法。**

### P2-7 · 其它细节问题

| 位置 | 问题 |
|------|------|
| `Win32WindowHost.cs:507` | `SetFullscreen(false)` 恢复时写死 `WS_OVERLAPPEDWINDOW`，无边框窗口全屏退出后会**变回带边框**（原始 style 丢失）。应保存初始 style 并还原 |
| `WindowsPlatformBackend.cs:102` / `Win32WindowHost.cs:159` | `Interlocked.Exchange(ref flag, 1)` **先置标志后注册**。若 `RegisterClassExW` 失败抛异常，标志已是 1，后续调用直接跳过 → 用未注册的类名 `CreateWindowExW`，错误信息误导 |
| `Win32WindowHost.cs:710-718` | `ConvertFilter` 未生成 Windows 要求的双 `\0` 终止。默认分支靠 `StringToHGlobalUni` 自动补终止符侥幸可用；用户自定义 filter 依赖 API 容错 |
| `Win32WindowHost.cs:33,268` | `_creationThreadId` 赋值后从未使用（死代码） |
| `OrielAppBuilder.cs:23,74` | `WindowOptionsList` 与 `PendingWindows` 信息重复，且无人读取 |
| `OrielAppBuilder.cs:106` | `CreateBuilder(string[]? args)` 接收 `args` 后完全忽略——签名有误导性 |
| `Bridge` ×3 | 三份 50 行 JS 95% 重复，唯一实质差异是 `platform` 字段。手抄三份已经导致 bug 同步传播（P0-2 三份全中）。应改为一份模板 + 平台变量 |
| `samples/OrielDemo/TodoCommands.cs:48` | `sys.info` 的 `Platform` 只认 Windows，其他平台返回 `"unknown"`，与跨平台定位不符 |
| 前端 `pending` Map | 无超时清理。若某条命令永不回执，`pending` 条目永久驻留 → 前端内存泄漏，且 Promise 永不 settle |

---

## 五、交付卫生

### 5.1 包内 98% 是构建产物

```
总计       20 MB
├── bin/obj 约 19.6 MB   （含 tests/OrielWeb.Tests/bin 7.2 MB）
└── 源码      376 KB
```

`.gitignore` 里其实已经正确排除了 `[Bb]in/` 和 `[Oo]bj/`——说明这不是仓库问题，而是**打包方式**问题（直接压缩了整个工作目录）。顺带把 PDB 符号文件也发出去了。

**建议**：用 `git archive` 或显式白名单打包源码。附带的好处是评审者/使用者第一眼看到的就是代码，而不是 300 个 `obj/` 中间文件。

### 5.2 测试覆盖严重偏斜

33 个测试 **100%** 集中在 `OrielJson` 和 `OrielCommandDispatcher`（IPC 纯逻辑层）。质量本身不错——覆盖了错误路径、协议形状、并发 50 路，不只是 happy path。

但以下区域**零自动化覆盖**：

- 窗口生命周期与事件时序
- 主线程调度（三个平台各自的 PostToMainThread）
- 平台后端与 COM/ObjC/GTK 互操作
- **桥接 JS**（P0-2、P0-3 正是纯 JS 逻辑 bug）

P0-2 那条 TDZ bug，用 Node 跑 3 行代码就能抓到。加一组桥接 JS 的 Node 单测（把脚本字符串喂进去、断言 `ready` 能 resolve、断言 `invoke` 往返），成本极低，却能挡住这一类问题。

### 5.3 CI 断言强度不足

`aot-macos` 只证明"能在 macOS 上链接出可执行文件"，`compile-linux` 只证明"能编译"。**没有任何一步运行产物**。P0-1 这类"编译 100% 通过、运行 100% 失败"的缺陷，在当前 CI 下永远漏过。

建议至少加一个冒烟步骤：启动后 3 秒内检查进程存活 + 打印一行成功日志，然后退出。

另外 `Directory.Build.props:9` 显式设置 `<TreatWarningsAsErrors>false</TreatWarningsAsErrors>`，也没有 `dotnet format` / Roslyn 分析器门禁，代码规范层面是敞开的。

### 5.4 文档与实际状态不符

`README.md` 平台矩阵：

| 平台 | Webview | README 标注 | 实际情况 |
|------|---------|------------|---------|
| Windows | WebView2 | ✅ 已验证 | 属实 |
| macOS | WKWebView | ✅ 编译通过，待真机验证 | **未验证，且已知会在 `Run()` 抛异常** |
| Linux | WebKitGTK 4.1 | ✅ 编译通过，待环境验证 | **同上** |

"待验证"这个措辞会让人以为"大概率能跑，只是没测"。但 P0-1 是确定性的必然失败。建议如实标注，或直接修复后再更新。

---

## 六、值得肯定的设计决策

客观上，这个项目有几处做得确实漂亮，不该被上面的问题掩盖：

1. **纯 P/Invoke 不引 C++ 中间层**——对比 Ryn 的 C++ 底座、IgniteView 在 Linux 上捆 QtWebEngine，OrielWeb 的"三平台零 C++ 组件"路线在 AOT 友好度和部署体积上有真实优势，且完全避开了 `ComWrappers` 在 .NET 10 上的三个坑（`DECISIONS.md:37-44` 的记录很珍贵）。

2. **`DECISIONS.md` 的横向对比质量高**——对 Ryn / pywebview / IgniteView 三个参考项目分别明确了"抄什么、不抄什么"，并给出理由。这种先做技术选型再动手的做法，在个人项目里并不多见。

3. **源生成器 + `[ModuleInitializer]` 自动注册**的零反射 IPC 路线选得对。API 形态（`Oriel.CreateBuilder(...).AddWindow(...).Run()`）简洁，`AddWindow(configure, onCreated)` 那个重载解决了"订阅事件必须在 Run 之前"的时序问题，考虑得很细。

4. **三平台代码形态统一**——回环消息（避 ObjC block）、状态注册表、流式拖动、`[UnmanagedCallersOnly]` trampoline，同一套模式落地三次，抽象是成功的。

5. **IPC 测试的覆盖面**——覆盖了参数提取全基元类型、DTO 往返、错误路径、协议形状、50 路并发唯值断言。不是只写 happy path 的敷衍测试。

6. **`(OrielCloseRequestEventArgs.Cancel)` 可取消关闭**、`WM_GETMINMAXINFO` 把最大化限制到工作区（避开任务栏）这类细节，说明对 pywebview 基本面是真的研究过的。

---

## 七、修复优先级建议

**第一批（不做完项目无法自称跨平台）**

| # | 修复项 | 成本 | 位置 |
|---|--------|------|------|
| P0-1 | STA 检查加 `OperatingSystem.IsWindows()` 条件 | 1 行 | `App/OrielApp.cs:33` |
| P0-2 | 桥接 JS 重构 `ready` 的构造顺序（3 处） | 每处 4 行 | 3 份 BridgeJs |
| P0-3 | 修掉 `orielready` 的派发时机 | 每处 5 行 | 3 份 BridgeJs |

**第二批（健壮性，建议紧随其后）**

| # | 修复项 | 成本 |
|---|--------|------|
| P1-1 | 三处 `[UnmanagedCallersOnly]` 补兜底 try/catch | 小 |
| P1-2 | `_assetHost` 改为读取构建器配置 | 小 |
| P1-4 | 命令名冲突检测并抛异常 | 小 |
| P1-5 | Linux 窗口销毁时清理 `WebviewStates`/`ManagerStates` | 小 |
| P2-6 | Windows `ReplyError` 改用 `JsonSerializer.Serialize` | 3 行 |

**第三批（架构改良，可排期）**

P1-3（全局静态状态实例化）、P2-1/P2-2（生成器直接产出强类型读写、`element.Deserialize`）、P2-3（全局命令索引做到真 O(1)）、P2-5（标识符碰撞）、桥接 JS 三份合一。

**第四批（工程化）**

CI 加冒烟运行、补桥接 JS 的单测、打包排除构建产物、文档如实标注平台状态。

---

## 附：验证方法与可复现命令

本报告中标注"实测"的结论，复现方式如下：

**P0-1（Linux `GetApartmentState`）**：

```bash
mkdir -p /tmp/sta && cd /tmp/sta
cat > sta.csproj <<'EOF'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType><TargetFramework>net6.0</TargetFramework>
  </PropertyGroup>
</Project>
EOF
cat > Program.cs <<'EOF'
using System.Threading;
System.Console.WriteLine("ApartmentState = " + Thread.CurrentThread.GetApartmentState());
System.Console.WriteLine("IsSTA = " + (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA));
EOF
dotnet run
# 输出：ApartmentState = Unknown / IsSTA = False
```

**P0-2（桥接 JS 的 TDZ）**：

```bash
node -e 'const o = { ready: new Promise((r) => { o._r = r; }) };
o.ready.catch(e => console.log("REJECTED:", e.constructor.name, e.message));'
# 输出：REJECTED: ReferenceError Cannot access 'o' before initialization
```

**P1-5（`Unregister*` 零调用）**：全量 grep `Unregister(Window|Webview|Manager)`，仅命中 3 处定义，无任何调用点。
