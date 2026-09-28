# OrielWeb 改进计划

> 配套文档：[`OrielWeb-WebView2绑定迁移计划.md`](./OrielWeb-WebView2绑定迁移计划.md)（阶段 E 详细版）
> 缺陷来源：本轮代码评审（含实测复现），索引见文末第 8 节
> 路径约定：所有路径相对仓库根目录；行号基于本轮评审的代码快照，实施前请以 `git grep` 复核

---

## 0. 使用约定

每个任务给出五段：**位置 / 现状 / 改法 / 验证 / 风险**。

- **改法**给出可直接套用的代码片段，但**不自动落盘**——按你的工作流，AI 只给可执行建议，改动由你手动确认后执行。
- **验证**优先给"一条命令 + 明确期望输出"的形式；涉及 CI 的给 YAML 片段。
- 阶段内任务按编号顺序执行；跨阶段依赖见第 1 节的关键路径。
- 每个阶段结束建议打一个 tag，便于回滚（`git tag stage-a-done`）。

---

## 1. 阶段总览

| 阶段 | 内容 | 前置依赖 | 完成后可验证的结果 |
|------|------|---------|------------------|
| **A** | 安全网：桥接 JS 单测 + CI 冒烟 + 槽位门禁 | — | 后续改动有回归防护 |
| **B** | P0 阻断缺陷（3 项） | A（强烈建议） | macOS/Linux 能启动；三平台就绪机制可用 |
| **C** | P1 严重缺陷（5 项） | B | 异常不外泄；配置生效；无静默冲突/泄漏 |
| **D** | P2 设计与一致性（9 项） | A | IPC 内核性能与一致性达标 |
| **E** | WebView2 自生成绑定迁移 | 独立决策门 | Windows 后端不再需要人工数槽位 |
| **F** | 工程化与交付卫生 | — | 可发布状态 |

**关键路径**：`A → B → { C ∥ D ∥ E ∥ F }`

**优先级说明**：A 和 B 是"不做完就无法自称跨平台"的部分，合计约 1 天工作量，建议最先做完并立即在 macOS/Linux 上验证一次。E 是架构级改动，风险最大，但它是**唯一能消除人工数槽位这个长期风险**的路径——先跑决策门（E-0），再决定是否投入。

---

## 2. 阶段 A：安全网

> 为什么先做：B 阶段要改三份桥接 JS，D 阶段的生成器改动会影响全部 IPC。没有自动化验证就只能靠手工点 demo，这是当前 P0 缺陷能存活下来的直接原因。

### A-1 桥接 JS 单元测试（Node）

- **位置**：新增 `tests/bridge/`（Node 测试，与 C# 测试并列）
- **现状**：`tests/OrielWeb.Tests/` 的 33 个测试 100% 覆盖 `OrielJson` + `OrielCommandDispatcher`，**桥接 JS 零覆盖**。P0-2 / P0-3 都是纯 JS 逻辑缺陷。1.1 节里 3 行 Node 代码即可复现。
- **改法**：

  从 C# 源里提取 JS 字符串，构造最小 window/document 桩后执行，断言就绪与往返行为。

  ```js
  // tests/bridge/bridge.test.mjs
  import { test } from 'node:test';
  import assert from 'node:assert/strict';
  import { readFileSync } from 'node:fs';

  // 从 C# 原始字符串字面量里抽出 JS（""" 之间）
  function extractScript(csPath) {
    const src = readFileSync(csPath, 'utf8');
    const m = src.match(/const string Script = """\r?\n([\s\S]*?)\r?\n\s*""";/);
    assert.ok(m, `未能从 ${csPath} 提取 Script`);
    return m[1];
  }

  // 最小环境桩：只需要桥接脚本用到的 API
  function runBridge(script) {
    const listeners = {};
    const posted = [];
    const domListeners = {};
    const win = {
      chrome: {
        webview: {
          postMessage: (m) => posted.push(m),
          addEventListener: (t, fn) => { (listeners[t] ||= []).push(fn); },
        },
      },
    };
    const doc = {
      readyState: 'loading',
      addEventListener: (t, fn) => { (domListeners[t] ||= []).push(fn); },
      dispatchEvent: (e) => { (domListeners[e.type] || []).forEach((f) => f(e)); return true; },
    };
    // 脚本里用到的全局
    const sandbox = { window: win, document: doc, Event: class { constructor(t){this.type=t;} } };
    new Function('window', 'document', 'Event', script)(
      win, doc, sandbox.Event);
    return { win, posted, listeners, domListeners };
  }
  ```

  至少覆盖 4 个断言：

  | 用例 | 断言 |
  |------|------|
  | 就绪 | `await win.oriel.ready` 不抛异常（直接锁住 P0-2） |
  | 事件 | 在 `DOMContentLoaded` 之后注册的 `orielready` 监听器能被触发（锁住 P0-3） |
  | 往返 | `invoke('x', {a:1})` 产生 `{__oriel:'invoke', id:1, name:'x', args:{a:1}}` |
  | 回执 | 投递 `{__oriel:'result', id:1, ok:true, value:42}` 后 Promise resolve 为 42 |
  | 错误 | `ok:false, error:'boom'` 时 Promise reject，message 为 `boom` |

  参数化跑三份桥接（`OrielBridgeJs.cs` / `MacOSBridgeJs.cs` / `LinuxBridgeJs.cs`）——**这是关键**：三份已多次出现 bug 同步传播。

- **验证**：

  ```bash
  node --test tests/bridge/
  # 期望：全部通过；把 B-2 的修复回退后，应当有 3 条失败
  ```

- **风险**：低。纯新增文件。注意 `MacOSBridgeJs` / `LinuxBridgeJs` 依赖 `window.webkit.messageHandlers.oriel`，桩里要补上。

### A-2 CI 增加"真正运行"的冒烟步骤

- **位置**：`.github/workflows/ci.yml`
- **现状**：`compile-macos` / `compile-linux` / `aot-macos` 三个 job **只 build/publish，从不运行产物**。P0-1 属于"编译 100% 通过、运行 100% 失败"，当前 CI 永远漏过。
- **改法**：

  ```yaml
    smoke-linux:
      runs-on: ubuntu-latest
      needs: [compile-linux]
      steps:
        - uses: actions/checkout@v4
        - run: |
            sudo apt-get update
            sudo apt-get install -y libwebkit2gtk-4.1-dev libgtk-3-dev xvfb
        - uses: actions/setup-dotnet@v4
          with: { dotnet-version: '10.0.x' }
        - name: Smoke run（进程存活到超时即视为通过）
          run: |
            set +e
            timeout 10s xvfb-run -a dotnet run --project samples/OrielDemo -c Release -r linux-x64
            code=$?
            echo "exit=$code"
            # 124 = 被 timeout 杀掉，说明窗口创建成功且消息循环在跑
            test $code -eq 124
  ```

  同一个模式加到 `smoke-windows`（不加 xvfb）。

- **验证**：本步骤在**修完 B-1 之前必然失败**——这正好可以作为 B-1 的前置回归证据。建议先单独提交 A-2，让 CI 红一次，记录失败输出，再提交 B-1 转绿。

- **风险**：低。`timeout` + 退出码 124 的判定方式对"启动即崩溃"和"卡死"都能区分。

### A-3 槽位校验接入 CI

- **位置**：`.github/workflows/ci.yml` 的 `aot-windows` job；脚本用已提供的 `verify-webview2-slots.py`
- **现状**：18 个 vtable 槽位全部正确（本轮已实测 18/18），但无门禁，未来扩接口时失守无人知。
- **改法**：

  ```yaml
        - name: Verify WebView2 vtable slots
          run: python3 verify-webview2-slots.py --repo . --version 1.0.2903.40
  ```

  插件版本号应跟随 `src/OrielWeb/OrielWeb.csproj` 里的 `PackageReference`。

- **验证**：本地 `python3 verify-webview2-slots.py --repo .` → 期望 `结果：全部一致（18 项），退出码 0`。
- **风险**：低。**注意**：阶段 E 完成后本任务作废（手工槽位常量被删除），届时连同脚本一起清理。

---

## 3. 阶段 B：P0 阻断缺陷

### B-1 `Run()` 的 STA 检查阻断 macOS/Linux 启动

- **位置**：`src/OrielWeb/App/OrielApp.cs:33`
- **现状**：

  ```csharp
  public void Run()
  {
      if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
      {
          throw new InvalidOperationException("OrielWeb 要求主线程为 STA 线程：...");
      }
  ```

  `ApartmentState` 是 Windows COM 概念，Unix 平台恒返回 `Unknown`，`[STAThread]` 特性在 Linux/macOS 上被忽略。**本机 Linux 实测：`ApartmentState = Unknown, IsSTA = False`** → 第一行必抛，macOS/Linux 从未真正启动过。

- **改法**：把检查提取为可测的 internal 方法，并只在 Windows 上生效。

  ```csharp
  public void Run()
  {
      EnsureApartment();
      _backend = PlatformBackendFactory.Create();
      // ... 其余不变
  }

  /// <summary>Windows 要求主线程为 STA（WebView2 COM）；Unix 平台的 GetApartmentState 恒为 Unknown，跳过。</summary>
  internal static void EnsureApartment()
  {
      if (!OperatingSystem.IsWindows())
      {
          return;
      }
      if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
      {
          throw new InvalidOperationException(
              "OrielWeb 在 Windows 上要求主线程为 STA 线程：请在 Main 方法上标注 [STAThread]。" +
              "（WebView2 的 COM 初始化与 UI 消息循环依赖 STA）");
      }
  }
  ```

- **验证**：

  ```bash
  # 1) 单元测试（任意平台）
  #    非 Windows 上 EnsureApartment() 不应抛异常
  # 2) Linux 冒烟（A-2 的 job）
  dotnet run --project samples/OrielDemo -c Release -r linux-x64   # 期望：窗口出现或进程存活
  ```

  建议在 `tests/OrielWeb.Tests/` 加一条：`if (!OperatingSystem.IsWindows()) Assert.Null(Record.Exception(OrielApp.EnsureApartment));`

- **风险**：**无**。这是纯放宽（Windows 行为完全不变）。
- **备注**：`EnsureApartment` 必须声明在 `OrielApp` 上（`internal static`），否则测试程序集访问不到；`InternalsVisibleTo("OrielWeb.Tests")` 已在 `OrielCommandDispatcher.cs:7` 声明。

### B-2 三平台桥接 JS 的 `ready` 是 rejected Promise（TDZ 自引用）

- **位置**：`src/OrielWeb/Bridge/OrielBridgeJs.cs:17`、`src/OrielWeb/Platform/MacOS/MacOSBridgeJs.cs:24`、`src/OrielWeb/Platform/Linux/LinuxBridgeJs.cs:22`（三份全中）
- **现状**：

  ```js
  const oriel = {
      ready: new Promise((resolve) => { oriel._resolveReady = resolve; }),   // ← oriel 尚在 TDZ
      ...
  };
  window.oriel = oriel;
  document.dispatchEvent(new Event('orielready'));
  oriel._resolveReady();   // ← 未定义，抛 TypeError
  ```

  Promise 构造器**同步**执行 executor，此时 `const oriel` 处于暂时性死区 → `ReferenceError` 被 Promise 捕获 → `ready` 变成 rejected，`_resolveReady` 永不赋值。

  **Node 22 实测**：`ready REJECTED: ReferenceError Cannot access 'oriel' before initialization`

- **影响**：README 首个示例、`samples/OrielDemo/wwwroot/app.js:129` 的 `await window.oriel.ready` 全部落进 catch → **demo 的 IPC 徽章会显示"IPC 失败"**。（`invoke()` 依赖 `window.chrome.webview`，不依赖 ready，所以功能大部分照常——这正是手工验证没发现它的原因。）
- **改法**：先建 Promise，再建对象。

  ```js
  let resolveReady;
  const ready = new Promise((resolve) => { resolveReady = resolve; });

  const oriel = {
      platform: 'windows',          // 三份分别为 windows / macos / linux
      version: '0.1.0',
      ready: ready,
      invoke(name, args) { /* 不变 */ },
      _onResult(id, ok, payload) { /* 不变 */ }
  };

  window.oriel = oriel;
  /* ... message 监听 / document.dispatchEvent 见 B-3 ... */
  resolveReady();
  ```

  同时删除对象字面量里的 `ready: new Promise(...)` 行。
- **验证**：A-1 的就绪用例；Windows 上跑 demo，徽章应显示 **"IPC 已连接"**。
- **风险**：无。

### B-3 就绪事件 `orielready` 永远派发不到监听者

- **位置**：`OrielBridgeJs.cs:48`、`MacOSBridgeJs.cs:49`、`LinuxBridgeJs.cs:47`
- **现状**：桥接脚本注入时机是 document 创建时（Windows `AddScriptToExecuteOnDocumentCreated`、macOS `WKUserScriptInjectionTimeAtDocumentStart`、Linux `WEBKIT_USER_SCRIPT_INJECT_AT_DOCUMENT_START`）。此时页面自身脚本尚未执行，`document.dispatchEvent(new Event('orielready'))` **没有任何监听器**；等页面注册监听时事件早已派发完毕。

  于是两套就绪机制同时失效：`ready` 被 TDZ 搞坏（B-2），`orielready` 死于时序。
- **改法**：改为在页面可观测的时机派发。

  ```js
  const announceReady = () => document.dispatchEvent(new Event('orielready'));
  if (document.readyState === 'loading') {
      document.addEventListener('DOMContentLoaded', announceReady, { once: true });
  } else {
      queueMicrotask(announceReady);
  }
  resolveReady();   // B-2 引入
  ```

  `DOMContentLoaded` 时机晚于所有同步/`defer`/`type=module` 脚本，页面注册的监听器能收到。
- **验证**：A-1 的事件用例；demo 里加一行 `document.addEventListener('orielready', () => console.log('READY'))`，控制台应打印。
- **风险**：低。若页面在 `DOMContentLoaded` **之后**才注册监听（例如动态注入的脚本），仍收不到——文档中应主推 `await window.oriel.ready`，把事件定位为兼容性补充。

---

## 4. 阶段 C：P1 严重缺陷

### C-1 三处 `[UnmanagedCallersOnly]` 入口补兜底 try/catch

- **位置**：
  | 文件:行 | 逃逸路径 |
  |---------|---------|
  | `Platform/Windows/Win32WindowHost.cs:185` | `Host.HandleMessage` → `Closing?.Invoke()`（**用户事件处理器**） |
  | `Platform/Windows/WindowsPlatformBackend.cs:87` | `action()`，来源是 **public** 的 `OrielApp.PostToMainThread` |
  | `Platform/Linux/LinuxSignalHandlers.cs:161` | `action()`，同样来自 public API |
- **现状**：`DECISIONS.md` 明确写着"所有 `[UnmanagedCallersOnly]` thunk 全身 try/catch，异常一律转 HRESULT，绝不外泄（外泄 = 进程 fail-fast）"。`WebView2NativeCallbacks.cs` 里 7 个 thunk 全部遵守，但这三处遗漏。任何一处抛出托管异常 → 进程 fail-fast，**不可捕获**。
- **改法**：

  ```csharp
  // 1) Win32WindowHost.WindowProc —— 整体包 try/catch
  [UnmanagedCallersOnly]
  private static unsafe nint WindowProc(nint hwnd, uint message, nuint wParam, nint lParam)
  {
      try
      {
          nint userData = Win32.GetWindowLongPtrW(hwnd, Win32Constants.GWLP_USERDATA);
          if (message == Win32Constants.WM_NCCREATE && userData == 0)
          {
              var createStruct = (CREATESTRUCTW*)lParam;
              if (createStruct->lpCreateParams != 0)
              {
                  Win32.SetWindowLongPtrW(hwnd, Win32Constants.GWLP_USERDATA, createStruct->lpCreateParams);
                  userData = createStruct->lpCreateParams;
              }
          }
          if (userData == 0)
          {
              return Win32.DefWindowProcW(hwnd, message, wParam, lParam);
          }
          var host = (Win32WindowHost)GCHandle.FromIntPtr(userData).Target!;
          return host.HandleMessage(hwnd, message, wParam, lParam);
      }
      catch
      {
          // 用户回调（Closing 等）异常不得穿越原生边界。降级为默认处理。
          return Win32.DefWindowProcW(hwnd, message, wParam, lParam);
      }
  }

  // 2) WindowsPlatformBackend.MessageWindowProc —— 先释放 GCHandle 再执行，避免异常泄漏句柄
  [UnmanagedCallersOnly]
  private static nint MessageWindowProc(nint hwnd, uint message, nuint wParam, nint lParam)
  {
      if (message == Win32Constants.WM_APP_DISPATCH)
      {
          var handle = GCHandle.FromIntPtr(lParam);
          Action? action;
          try
          {
              action = (Action)handle.Target!;
          }
          finally
          {
              handle.Free();
          }
          try
          {
              action?.Invoke();
          }
          catch
          {
              // PostToMainThread 是 public API，用户 Action 异常不得外泄
          }
          return 0;
      }
      return Win32.DefWindowProcW(hwnd, message, wParam, lParam);
  }

  // 3) LinuxSignalHandlers.PumpIdleTrampoline —— 单条失败不影响后续排空
  [UnmanagedCallersOnly]
  internal static int PumpIdleTrampoline(nint data)
  {
      while (MainThreadQueue.TryDequeue(out var action))
      {
          try
          {
              action();
          }
          catch
          {
              // 用户 Action 异常不得穿越原生边界
          }
      }
      return 0;
  }
  ```

- **设计权衡（需你确认）**：Win32 侧 catch 后返回 `DefWindowProcW` 会恢复**默认语义**——若异常来自 `WM_CLOSE` 的 `Closing` 回调，窗口会被真的销毁（"取消关闭"的意图失效）。这是"异常时不确定用户意图"的合理降级。若你希望更保守（异常时**不**执行默认行为），可改为 `return 0;`。**建议先按上面写，并在文档里注明这一语义**。
- **验证**：加两条测试/demo：
  1. `Closing += _ => throw new Exception("boom");` → 点关闭 → 进程**不崩**，窗口按降级语义处理。
  2. `app.PostToMainThread(() => throw new Exception("boom"));` → 进程不崩。
- **风险**：低。加宽而非收窄。唯一需注意的是吞掉异常后**没有日志**——建议同时接 `Trace`/`Debug.WriteLine` 便于诊断（生产可关）。

### C-2 `_assetHost` 硬编码，忽略构建器配置

- **位置**：`src/OrielWeb/Platform/Windows/Win32WindowHost.cs:58`
- **现状**：

  ```csharp
  _assetHost = "app.oriel"; // 与构建器默认一致；M1 用固定虚拟主机
  ```

  而 `OrielAppBuilder.UseEmbeddedAssets(string host = "app.oriel", ...)`（`OrielAppBuilder.cs:27`）明确允许自定义 host 并存入 `AssetHost`（已是 `internal`），但平台层无视它。用户写 `.UseEmbeddedAssets("myapp.local")` → 虚拟主机映射到 `myapp.local`，`Navigate` 却指向 `https://app.oriel/index.html` → **白屏 / 404**。
- **改法**：`OrielApp` 暴露访问器，宿主从 app 读取。

  ```csharp
  // src/OrielWeb/App/OrielApp.cs，加在 Windows 属性附近
  internal string AssetHost => _builder.AssetHost;

  // Win32WindowHost.cs:58
  _assetHost = app.AssetHost;
  ```

  同时检查 macOS（`MacOSWindowHost.cs`）与 Linux（`LinuxWindowHost.cs`）是否有同类硬编码——**建议一并 grep `"app.oriel"` 全仓**。
- **验证**：`.UseEmbeddedAssets("myapp.local")` 后 demo 正常加载（而非仅默认 host 可用）。
- **风险**：低。注意自定义 host 不要用 `localhost` 等保留名。

### C-3 路由索引化：同时解决"命令名冲突静默通过"与 O(n) 遍历

- **位置**：`Ipc/OrielCommandRegistry.cs`、`Ipc/OrielCommandDispatcher.cs:54-73`、`Generators/OrielCommandGenerator.cs`
- **现状**：两个问题共用一套改动，建议合并处理。

  1. `HandleInvokeAsync` 每条消息遍历 **全部** router 调 `Route(name)`：`O(router 数)` + 每次 `lock` + 一次数组分配（`Snapshot()` 里 `[.. s_routers]`）。README 声称的"O(1) switch 分发"实际不成立——O(1) 只在单个 router 内部。
  2. 两个类注册同名命令（如都写 `todo.add`）时，第一个命中的生效，另一个**永远不可达且无任何警告**；顺序取决于 `[ModuleInitializer]` 执行序，不保证稳定。

- **改法**：

  ```csharp
  // 1) IOrielCommandRouter 增加可枚举能力
  public interface IOrielCommandRouter
  {
      IReadOnlyList<string> CommandNames { get; }   // 新增
      int Route(string name);
      Type? TargetType { get; }
      bool RequiresTarget { get; }
      ValueTask<object?> InvokeAsync(int index, object? target, JsonElement args);
  }
  ```

  ```csharp
  // 2) 生成器为每个 Router 生成静态命令名表（EmitRouter 内追加）
  source.AppendLine("        private static readonly global::System.String[] s_names =");
  source.AppendLine("        [");
  foreach (var c in commands)
  {
      source.AppendLine($"            \"{Escape(c.CommandName)}\",");
  }
  source.AppendLine("        ];");
  source.AppendLine("        public global::System.Collections.Generic.IReadOnlyList<global::System.String> CommandNames => s_names;");
  ```

  ```csharp
  // 3) OrielCommandRegistry.AddRouter 建索引并检测冲突
  private static readonly Dictionary<string, IOrielCommandRouter> s_index = new(StringComparer.Ordinal);

  public static void AddRouter(IOrielCommandRouter router)
  {
      ArgumentNullException.ThrowIfNull(router);
      lock (s_gate)
      {
          foreach (var name in router.CommandNames)
          {
              if (s_index.TryGetValue(name, out var existing) && !ReferenceEquals(existing, router))
              {
                  throw new InvalidOperationException(
                      $"命令名冲突：'{name}' 同时注册于 {existing.GetType().Name} 与 {router.GetType().Name}。" +
                      "请修改其中一处的 [OrielCommand] 名称。");
              }
              s_index[name] = router;
          }
          s_routers.Add(router);
      }
  }
  ```

  ```csharp
  // 4) Dispatcher 构造时快照索引，热路径变 O(1) 且无锁无分配
  private readonly Dictionary<string, (IOrielCommandRouter Router, int Index)> _routes;

  public OrielCommandDispatcher(Dictionary<Type, Func<object>> factories)
  {
      _factories = factories;
      _routes = OrielCommandRegistry.BuildIndex();   // 新增：一次性构建，含冲突检测
  }
  ```

  `HandleInvokeAsync` 中把 `foreach (var router in Snapshot())` 换成 `_routes.TryGetValue(name, out var route)` 单次命中。

- **验证**：现有 33 个 IPC 测试应全绿（含 `UnknownCommand_ErrorReply`、`ConcurrentInvocations_AllSucceed`）。**新增一条**冲突用例：定义两个类都用 `[OrielCommand("dup.same")]`，断言 `BuildIndex()` 抛 `InvalidOperationException` 且消息含两个类型名。
- **风险**：中。改动触及生成器 + 接口 + 注册表 + 分发器，属于 IPC 内核。**必须在 A-1 与现有 33 测试的保护下进行**，建议单独一个 commit。
- **附带收益**：消除 P2 中"O(1) 说法不准确"的问题。

### C-4 Linux/macOS 状态注册表永不清理（泄漏 + ABA 风险）

- **位置**：
  - Linux：`Platform/Linux/LinuxSignalHandlers.cs:27-29`（`UnregisterWindow/Webview/Manager` **定义了但零调用**，已全仓 grep 确认）；注册点在 `LinuxWindowHost.cs:104-106`；销毁点在 `LinuxWindowHost.cs:160-164`
  - macOS：`Platform/MacOS/MacOSObjCClasses.cs:27-29` 的三个静态字典 `WindowDelegateStates` / `NavigationDelegateStates` / `ScriptHandlerStates`（同样未见移除）
- **现状**：`OnDestroyTrampoline` 通过 `WindowStates.Remove(widget, out host)` 顺带清掉了 `WindowStates`，但 `WebviewStates` 与 `ManagerStates` 只增不减 → 每个 `nint → LinuxWindowHost` 条目都是强引用，**窗口关闭后整棵对象树永不释放**。

  更危险的是指针复用：GTK 释放 webview 后同地址可能被新窗口复用，陈旧映射会把新窗口的信号路由到**已销毁的 host**（ABA），表现为随机崩溃或串窗口。
- **改法**：

  ```csharp
  // LinuxWindowHost.OnWindowDestroyed()：在调用 backend 之前清理
  internal void OnWindowDestroyed()
  {
      Closed?.Invoke();
      LinuxSignalHandlers.UnregisterWebview(_webview);
      LinuxSignalHandlers.UnregisterManager(_userContentManager);
      _webview = 0;
      _userContentManager = 0;
      _backend.OnWindowDestroyed();
  }
  ```

  macOS 侧同构：在 `MacOSWindowHost.OnWindowWillClose()`（`MacOSWindowHost.cs:250-254`）里用持有的 delegate/handler 指针移除三个字典条目。**注意**：`Closed?.Invoke()` 应在清理之后还是之前，取决于用户回调是否需要访问窗口能力——建议**先清理指针、再触发 Closed**，避免用户在回调里发起 IPC 时走到已释放的 host。

  另外给 `LinuxSignalHandlers` 的三个静态 `Dictionary` 加注释说明"仅主线程访问，不跨线程"，或换成 `ConcurrentDictionary`（当前所有访问都在 GTK 主线程，安全，但需注明）。
- **验证**：多窗口反复开关（如 `App 中循环 AddWindow + Close` ×20），用任务管理器/`dotnet-counters` 观察内存不持续增长；日志断言 `WebviewStates.Count == 0`（可临时加 internal 计数属性供测试）。
- **风险**：中。清理时机若早于 GTK 真实销毁，可能触发空指针——**必须在 `destroy` 信号回调内做**（当前 `OnDestroyTrampoline` 就是这个时机，是对的）。

### C-5 `ICoreWebView2_3` 查询失败时静默降级 → 白屏无提示

- **位置**：`Platform/Windows/Win32WindowHost.cs:344-352`
- **现状**：

  ```csharp
  if (WebView2Native.TryQueryInterface(webview.Self, WebView2Iids.ICoreWebView2_3, out var webview3Ptr))
  {
      // 映射虚拟主机
  }
  // ← 没有 else
  ```

  `ICoreWebView2_3` 需要 WebView2 Runtime ≥ 1.0.864.35。Evergreen 用户基本满足，但企业内固定版本、Windows Server、离线镜像可能更旧。QI 失败 → 虚拟主机不映射 → `Navigate("https://app.oriel/index.html")` 失败 → **白屏，零错误提示**。而 `WebView2ComHelper.ThrowIfFailed` 只拦有 HRESULT 返回的调用，这条路径完全绕过错误处理。

- **改法**：

  ```csharp
  if (WebView2Native.TryQueryInterface(webview.Self, WebView2Iids.ICoreWebView2_3, out var webview3Ptr))
  {
      var webview3 = new WebView2_3Ptr(webview3Ptr);
      const int HOST_RESOURCE_ACCESS_KIND_DENY_CORS = 2;
      WebView2ComHelper.ThrowIfFailed(
          webview3.SetVirtualHostNameToFolderMapping(_assetHost, _assetDirectory, HOST_RESOURCE_ACCESS_KIND_DENY_CORS),
          "映射虚拟主机");
  }
  else
  {
      throw new InvalidOperationException(
          "当前 WebView2 运行时过旧，不支持 ICoreWebView2_3（虚拟主机映射），内嵌资源无法加载。" +
          "请升级 WebView2 Runtime 至 1.0.864.35 以上。");
  }
  ```

  可进一步用 `WebView2LoaderNative` 的 `GetAvailableCoreWebView2BrowserVersionString` 把**实际运行时版本**拼进异常消息，诊断体验更好。

- **验证**：正常环境不受影响；可用 `ORIEL_WEBVIEW2_FOLDER`（`Win32WindowHost.cs:273` 已有该环境变量）指向一个过旧版本的固定运行时来复现该分支。
- **风险**：低。
- **生命周期说明**：**阶段 E 完成后，本段代码整体被生成绑定替换**，此任务是为 E 之前争取到的临时可用性。

---

## 5. 阶段 D：P2 设计与一致性

> 这些不阻断使用，但影响长期维护与一致性。建议在 B/C 之后、E 之前或并行完成。

### D-1 Windows 回执的错误 JSON 手写转义不完整

- **位置**：`Platform/Windows/Win32WebView2Ipc.cs:61-62`
- **现状**：

  ```csharp
  private static string Encode(string text)
      => "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r") + "\"";
  ```

  漏掉 `\t`、`\b`、`\f` 及全部 U+0000–U+001F 控制字符。异常消息含制表符时生成**非法 JSON**，`PostWebMessageAsJson` 直接失败，前端连错误都收不到。

  对照 Linux/macOS 版用的是正解：`JsonSerializer.Serialize(ex.Message)`。
- **改法**：统一为

  ```csharp
  PostJson($"{{\"__oriel\":\"result\",\"id\":0,\"ok\":false,\"error\":{JsonSerializer.Serialize(ex.Message)}}}");
  ```

  并删除 `Encode` 方法。
- **验证**：构造一个 message 含 `\t` 与 `\u0001` 的异常，断言前端能收到合法 JSON。
- **风险**：无。

### D-2 `SetFullscreen(false)` 恢复样式时丢失原始窗口样式

- **位置**：`Platform/Windows/Win32WindowHost.cs:538-541`
- **现状**：退出全屏时无条件 `style |= (WS_OVERLAPPEDWINDOW & ~WS_VISIBLE)` → **无边框窗口全屏后再退出会变成带边框**。
- **改法**：进入全屏前保存 `GWL_STYLE`，退出时还原（当前只保存了 `_savedPlacement`）。
- **验证**：`.WithFrameless()` + `win.toggleFullscreen` 两次 → 仍无边框。
- **风险**：低。

### D-3 窗口类注册的"先置标志后注册"顺序

- **位置**：`Platform/Windows/Win32WindowHost.cs:159`、`Platform/Windows/WindowsPlatformBackend.cs:102`
- **现状**：`Interlocked.Exchange(ref flag, 1) == 1` 在注册**之前**置位。若 `RegisterClassExW` 失败抛异常，标志已是 1，后续调用直接跳过 → 用未注册的类名去 `CreateWindowExW`，错误信息误导。
- **改法**：注册成功后再置位；或用 `Lazy<T>` / 三态（0=未注册, 1=注册中, 2=已注册）。
- **验证**：难以自然复现；代码审查 + 极端场景（类名冲突）验证错误信息正确。
- **风险**：低。

### D-4 文件对话框 filter 未生成双 `\0` 终止

- **位置**：`Platform/Windows/Win32WindowHost.cs:710-718`
- **现状**：`filter.Replace('|', '\0') + "\0"` 只补一个 null。Win32 要求以 `\0\0` 结尾。默认分支 `"所有文件\0*.*\0"` 经 `StringToHGlobalUni` 会自动再加终止符故侥幸可用；**用户自定义 filter 依赖 API 容错**。
- **改法**：`return filter.Replace('|', '\0') + "\0\0";`（并处理 filter 已以 `|` 结尾的情况）。
- **验证**：`.ShowOpenFileDialog(filter: "文本|*.txt|全部|*.*")` → 筛选下拉正确显示两项。
- **风险**：低。

### D-5 死代码与冗余清理

| 位置 | 内容 |
|------|------|
| `Platform/Windows/Win32WindowHost.cs:33,268` | `_creationThreadId` 赋值后从未使用 |
| `App/OrielAppBuilder.cs:23,74` | `WindowOptionsList` 与 `PendingWindows` 信息重复，无人读取 |
| `App/OrielAppBuilder.cs:106` | `CreateBuilder(string[]? args)` 接收 `args` 后完全忽略（签名误导） |
| `Platform/Windows/WindowsPlatformBackend.cs:17,26,31,81` | `_selfHandle` 传给 `CreateWindowExW` 作 `lpParam` 但 proc 未使用 |
| `Platform/Windows/Win32WindowHost.cs:103-107` | 同上（窗口侧） |

- **改法**：删除；`CreateBuilder` 保留 `args` 形参但用 `_ = args;` 或直接改签名为无参并加 `[Obsolete]` 过渡。
- **验证**：编译无警告；现有测试全绿。
- **风险**：低（注意 `_selfHandle` 的释放路径要连同 `Dispose` 一起清理，别只删字段）。

### D-6 三平台桥接 JS 合并为单一模板

- **位置**：`Bridge/OrielBridgeJs.cs`、`Platform/MacOS/MacOSBridgeJs.cs`、`Platform/Linux/LinuxBridgeJs.cs`
- **现状**：三份 50 行 JS 约 95% 重复，唯一实质差异是 `platform` 字段与消息通道（`chrome.webview` vs `webkit.messageHandlers`）。手抄三份**已经导致 bug 同步传播**（B-2 三份全中）。
- **改法**（推荐，分两步）：

  1. 抽出 `src/OrielWeb/Bridge/oriel-bridge.js` 单文件，用占位符标注差异（如 `__ORIEL_PLATFORM__`、`__ORIEL_POST__`）；
  2. 用 `<EmbeddedResource>` 嵌入，运行期替换占位符后注入（替换成本极低，只在建窗口时一次）。

  这样顺带解决 A-1 的测试问题——测试可以直接跑**同一个 .js 文件**，不用从 C# 字符串里正则抠。
- **验证**：A-1 的测试改为直接读 `.js` 文件，三平台各跑一遍；Windows demo 功能不变。
- **风险**：中。需要三平台都真机验证一次注入是否仍正常（尤其 macOS/Linux 的 `evaluateJavaScript` 路径）。
- **建议时机**：**B 完成之后再合并**。理由：B-2/B-3 的修复需要立刻在三平台生效并可验证；若先合并，一旦出问题无法区分是"合并引入"还是"修复引入"。

### D-7 前端 `pending` Map 无超时清理

- **位置**：`samples/OrielDemo/wwwroot/app.js` 及三份桥接的 `pending` Map
- **现状**：命令永不回执时（例如 C# 侧异常导致 sink 未投递），Promise 永不 settle，`pending` 条目永久驻留 → 前端内存泄漏。
- **改法**：`invoke` 内加超时（如 30s）`setTimeout` → `reject(new Error('oriel.invoke 超时'))` 并 `pending.delete(id)`；收到回执时 `clearTimeout`。
- **验证**：临时让一个命令 hang，断言 30s 后 reject 且 `pending.size` 归零。
- **风险**：低。超时时长应可配置（`oriel.timeout = ms`）。

### D-8 demo `sys.info` 只认 Windows

- **位置**：`samples/OrielDemo/TodoCommands.cs:48`
- **现状**：`Platform: OperatingSystem.IsWindows() ? "windows" : "unknown"` —— 与跨平台定位不符（虽然当前 demo 未调用它）。
- **改法**：补 macOS / Linux / 其他分支。
- **验证**：无（示例代码）。
- **风险**：无。

### D-9 生成器类型名碰撞导致静默生成错误代码

- **位置**：`Generators/OrielCommandGenerator.cs:107-118`
- **现状**：

  ```csharp
  foreach (var c in text.ToString())
      if (!char.IsLetterOrDigit(c) && c != '_') text.Replace(c, '_');
  ```

  非字母数字一律换成 `_` → `A.B` 与 `A_B` 归一为同一标识符；随后 `GroupBy(m => m.TypeNameSafe)`（`Emit` 内）把两者并入同一组，而 `model.TypeDisplay` 取自 `commands.First()` → **路由指向错误类型**，静默生成错误代码。
- **改法**：用完全限定名做分组 key，生成的类名附加稳定短哈希后缀：

  ```csharp
  string typeKey = containingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
  string typeSafe = MakeSafeIdentifier(containingType) + "_" + StableHash(typeKey);
  ```

  `StableHash` 用 FNV-1a 之类的确定性算法（不能用 `string.GetHashCode()`——跨进程不稳定）。
- **验证**：新增生成器测试：构造 `Ns.A.B` 与 `Ns.A_B` 两个类各带命令，断言生成两个独立 Router 且各自 `TargetType` 正确。
- **风险**：中。会改变生成的类名（仅内部实现细节，不影响公开 API）。

### D-10 `OrielJson` 的 DTO 双重解析

- **位置**：`Ipc/OrielJson.cs:174`
- **现状**：`JsonSerializer.Deserialize(element.GetRawText(), typeInfo)` —— `GetRawText()` **分配完整字符串副本**，STJ 再解析一次。热路径上的实打实浪费。
- **改法**：

  ```csharp
  return (T)element.Deserialize(typeInfo)!;   // 零中间字符串
  ```

  同理 `WriteResult`（`OrielJson.cs:264-265`）的 `value.GetType()` + `Resolve(type)` 每次查表，可由生成器直接写入具体 `JsonTypeInfo`。
- **验证**：现有 `Dto_RoundTrip_CamelCase` 等测试全绿。
- **风险**：低。

### D-11 生成器直接生成强类型读取（消除运行期 `typeof` 分派）

- **位置**：`Generators/OrielCommandGenerator.cs` 的参数提取段 + `Ipc/OrielJson.cs:78-161`
- **现状**：生成器**编译期就知道**每个参数的类型，却生成 `OrielJson.GetRequiredArg<T>(...)`，进到内部用 `switch (typeof(T))` + 16 个 `case var t when t == typeof(X)` 做运行期分派。README 写的"基元类型手写 Utf8Json 读写"实际是"读 `JsonElement` 后按 `typeof` 分派"。
  绝对耗时在纳秒级，**不是性能瓶颈**，但浪费了源生成器的核心价值。
- **改法**：生成器按参数类型直接生成 `element.GetInt32()` / `element.GetString()` 等（含 kind 校验），把运行期分派变成编译期已决。
- **验证**：`OrielJsonTests` 全绿（注意这批测试直接测 `OrielJson` 的泛型入口，若改为生成器直出，测试要改为测**生成后的行为**，或者保留 `OrielJson` 入口作为 fallback）。
- **风险**：中。改动面覆盖生成器与全部 IPC 参数路径。**依赖 A-1 完成、且建议在 C-3 之后做**（避免两个生成器改动互相干扰）。
- **说明**：优先级最低。若时间紧张可跳过——它是"设计正确性"问题而非缺陷。

---

## 6. 阶段 E：WebView2 自生成绑定迁移（摘要）

> 详细步骤、决策门、并行切换策略与回滚方案见 **[`OrielWeb-WebView2绑定迁移计划.md`](./OrielWeb-WebView2绑定迁移计划.md)**。

| 步骤 | 内容 | 关键判断 |
|------|------|---------|
| **E-0** | **决策门**：Windows 上跑通 `WebView2Aot` 的 Hello 样例（AOT 发布，约 40 行） | 能跑通 → 说明 `[GeneratedComInterface]` 在你环境可行，当初放弃该路线的依据需要复核；跑不通 → 记录版本与现象，本次迁移不启动 |
| E-1 | 选定路线（A 用现成 `WebView2Aot` / B 用 `Win32InteropBuilder` 自生成 / C 维持手工 + A-3 门禁） | 若要保持"零第三方运行时依赖"的定位，选 B |
| E-2 | 获取 `Microsoft.Web.WebView2.Win32.winmd` | 由 `smourier/webview2-win32md` 从官方 SDK 生成 |
| E-3 | 生成 `[GeneratedComInterface]` 绑定 | 彻底替代人工数槽位 |
| E-4 | 新增 `Win32WindowHostV2` **并行存在**，用环境变量开关切换 | 可 A/B 对比，出问题立刻切回 |
| E-5 | 替换 COM 调用点（`WebView2Com.cs` 的 Ptr 结构） | **只迁 COM，保留 Win32 P/Invoke**（理由见详细文档） |
| E-6 | 回调替换为 `[GeneratedComClass]`（替代手工 CCW） | 这是原始三个 bug 的所在层 |
| E-7 | 引用计数改为自动（`IComObject<T>` 或 `UniqueComInterfaceMarshaller`） | 解掉现有的 AddRef 永不 Release 泄漏 |
| E-8 | 切换默认实现并删除旧代码（含 `WebView2Slots`、`verify-webview2-slots.py`、A-3 门禁） | 保留 Win32 部分 |
| E-9 | 验收：AOT 单文件发布 + IPC 往返 + 无边框 + 多窗口 | 与当前 demo 功能对齐 |

**范围界定的核心判断**：只迁移 COM 互操作层，**不动 Win32 P/Invoke**。理由——Win32 那部分用的是 `[LibraryImport]`，本来就是 AOT 安全、无反射问题；而动它会引入 `DirectNAot` 全量依赖、把风险面扩大到窗口/消息循环/对话框，收益为零。

---

## 7. 阶段 F：工程化与交付卫生

### F-1 打包排除构建产物

- **现状**：交付的 zip 20 MB 中源码仅 376 KB，**98% 是 bin/obj**（含 `tests/OrielWeb.Tests/bin` 7.2 MB 与 PDB 符号文件）。`.gitignore` 已正确排除，说明是**打包方式**问题（直接压缩了工作目录）。
- **改法**：

  ```bash
  git archive --format=zip -o OrielWeb-src.zip HEAD
  ```

  或显式白名单。
- **验证**：产物解压后不含 `bin/` `obj/`；体积降到 ~0.4 MB。

### F-2 测试覆盖补齐

- **现状**：33 个测试 100% 集中在 `OrielJson` + `OrielCommandDispatcher`；窗口生命周期、主线程调度、平台后端、**桥接 JS** 全部零覆盖。P0-2/P0-3 正是纯 JS 逻辑缺陷。
- **改法**：A-1 已建立 JS 测试；再补
  - `Run()` 跨平台行为测试（B-1 附带）
  - 命令名冲突测试（C-3 附带）
  - 窗口销毁后状态注册表为空的测试（C-4 附带）

### F-3 CI 门禁加固

- **改法**：
  - `dotnet format --verify-no-changes` 加入 CI
  - 把 `Directory.Build.props:9` 的 `<TreatWarningsAsErrors>false</TreatWarningsAsErrors>` 改为对 `src/` 生效（`true`），`samples/` 可放宽
  - Linux job 增加 `xvfb` + A-2 的冒烟步骤

### F-4 文档校正

- **现状**：`README.md` 平台矩阵标注 macOS/Linux 为"✅ 编译通过，待真机验证"，实际是"未验证，且已知会在 `Run()` 抛异常"。措辞会让人以为"大概率能跑"。
- **改法**：
  - B-1 完成后如实更新平台矩阵
  - 新增"最低 WebView2 Runtime 版本"章节（对应 C-5）
  - 新增"命令线程模型"说明（**命令实例被并发 invoke 共享，方法必须是线程安全的**——当前文档完全没写，而 `samples/OrielDemo/TodoCommands.cs` 的 `_items` / `_nextId++` 自己就违反了）
  - 在 `DECISIONS.md` 中标注手工 vtable 层为**临时绕行**，写明回归条件（`.NET` 运行时修复后回归 `[GeneratedComInterface]`），避免技术债固化

### F-5 NuGet 发布准备

- **现状**：`OrielWeb.csproj` 已有 `PackageId` / `PackageLicenseExpression` / `PackageReadmeFile` / `GenerateDocumentationFile` / snupkg 配置。
- **待办**：首次发布前确认 `OrielWeb.Generators` 的 `analyzers/dotnet/cs` 路径正确（`NoWarn NU5128` 已抑制）、`RepositoryUrl` 有效、`README` 内嵌（`Pack=True PackagePath="\"`）无警告。

---

## 8. 缺陷索引（评审发现 → 任务）

| 严重度 | 缺陷 | 任务 |
|--------|------|------|
| P0 | `Run()` STA 检查阻断 Unix 启动 | B-1 |
| P0 | 三平台桥接 `ready` 为 rejected Promise（TDZ） | B-2 |
| P0 | `orielready` 事件永远派发不到监听者 | B-3 |
| P1 | 三处 `[UnmanagedCallersOnly]` 缺兜底 try/catch | C-1 |
| P1 | `_assetHost` 硬编码，忽略构建器配置 | C-2 |
| P1 | 命令名冲突静默通过 + 分发 O(n) | C-3 |
| P1 | Linux/macOS 状态注册表永不清理（泄漏 + ABA） | C-4 |
| P1 | `ICoreWebView2_3` QI 失败静默降级 → 白屏无提示 | C-5 |
| P1 | `AddRefComObject` / `GetSettings` / `get_CoreWebView2` 从不 Release | E-7 |
| P1 | 回调参数生命周期靠人脑记账（曾致浏览器进程树退出） | E-6 |
| P2 | Windows 回执手写 JSON 转义不完整 | D-1 |
| P2 | `SetFullscreen(false)` 丢失无边框样式 | D-2 |
| P2 | 窗口类注册"先置标志后注册" | D-3 |
| P2 | 对话框 filter 无双 `\0` 终止 | D-4 |
| P2 | 死代码与冗余（5 处） | D-5 |
| P2 | 三份桥接 JS 重复，bug 同步传播 | D-6 |
| P2 | 前端 `pending` Map 无超时 | D-7 |
| P2 | demo `sys.info` 只认 Windows | D-8 |
| P2 | 生成器 `MakeSafeIdentifier` 类型名碰撞 | D-9 |
| P2 | DTO 双重解析（`GetRawText`） | D-10 |
| P2 | 运行期 `typeof` 分派（零反射打折扣） | D-11 |
| P2 | `WM_MOVE` 不调 `NotifyParentWindowPositionChanged`（槽位 23 正确，疑为重入问题） | 待 E 阶段重新评估 |
| 工程 | 包内 98% 构建产物 | F-1 |
| 工程 | 测试覆盖偏斜（桥接 JS 零覆盖） | A-1 / F-2 |
| 工程 | CI 只编译不运行，断言强度不足 | A-2 / F-3 |
| 工程 | 文档与平台实际状态不符 | F-4 |
| 工程 | 命令线程模型未文档化，demo 自身违反 | F-4 |

---

## 9. 建议的执行顺序（含提交粒度）

| 序 | 提交 | 内容 | 说明 |
|----|------|------|------|
| 1 | `test: bridge js unit tests` | A-1 | 先红后绿：先写测试，确认能捕获现有 bug |
| 2 | `ci: add smoke run + slot verification` | A-2 / A-3 | 预期 CI 因 P0-1 变红，记录为回归证据 |
| 3 | `fix(linux,macos): sta check only on windows` | B-1 | CI 转绿 |
| 4 | `fix(bridge): resolve ready promise and event timing` | B-2 / B-3 | 三平台一次改完 |
| 5 | `fix: guard unmanaged callers, asset host, route index, state cleanup` | C-1 ~ C-5 | 可拆多个 commit |
| 6 | `refactor: generator and ipc polish` | D-1 ~ D-11 | 按需 |
| 7 | `feat: webview2 generated bindings` | E-0 ~ E-9 | 先跑决策门 |
| 8 | `chore: packaging, ci, docs` | F-1 ~ F-5 | — |

阶段 1–4 合计约 1 天，且完成后 macOS/Linux 首次真正可用——**建议优先做掉**，因为它把"跨平台"从宣称变成事实。
