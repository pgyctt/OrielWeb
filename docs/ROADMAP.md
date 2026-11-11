# OrielWeb 路线图

只记**还没做**的和**还没在真机上验过**的。已落地的能力、用法与取舍记录见 [API.md](API.md)。

> 过程文档不留在仓库里。2026-10 那轮的评审计划、对齐审计与三份阶段计划
> （`CODE-REVIEW-PLAN-2026-10-23`、`TAURI2-ALIGNMENT-AUDIT-2026-10-18`、
> `TAURI2-ALIGNMENT-EXECUTION-PLAN-2026-10-18`、`TAURI2-ALIGNMENT-DECISIONS-2026-10-20`）
> 已移除：它们记录的是"怎么排的"，而结论已经沉淀到 API.md 的对应小节。
>
> 2026-11-11 那轮同理：`CODE-REVIEW-2026-11-11` 与 `OPTIMIZATION-PLAN` 的 P0/P1 项
> （T1–T11）已随 `0.5.5` 落地，安全语义的结论沉淀进 API.md 第 4/6 节的威胁模型表，
> 互操作层的真机待验项并入下面「待真机验证清单」，未完成的部分（`clipboard.writeImage`
> 的路径 scope、P2 的 T12–T15）并入「已知缺口」。两份文件随之移除。

## API 与 Tauri 2 对齐（已完成）

B0–B24 全部完成，从 `0.4.0` 起分批发版。分阶段清单与每批的取舍记录随那批计划文档一并移除。

对齐的**结果**不记在这份文件里：每个模块的形状、与 Tauri 的语义差异（含刻意保留的那些）在
[API.md](API.md) 对应小节，迁移最容易踩的点在 [MIGRATION-FINDINGS](MIGRATION-FINDINGS.md)。
这条线只剩下"还没在真机上验过"的部分，都在下面的清单里。

## 明确不做

| 项 | 为什么 |
|---|---|
| `dotnet new` 模板 | 包内 `buildTransitive/OrielWeb.targets` 已经让"手写 csproj"这件事没有坑；模板的维护成本（每个 SDK 版本都要跟）与收益不成比例 |
| updater（自己做） | 需要密钥管理、清单托管、下载与替换流程，是一整块独立工程。**改用 Velopack**：它自带安装器与自更新，我们只负责把产物打出来（见 API.md §12）；应用侧接入（`Main` 首行 `VelopackApp.Build().Run()`）尚未做 |
| `oriel doctor` | 曾有一版独立 CLI 做本机体检 / 项目体检 /"人眼验证清单"指引，随打包链路改造被**整体撤掉**（2026-10-01：CLI 项目删除，打包改用 Velopack）。它的检查项仍散落在 README 的平台要求、API.md 与 `tools/verify-*.sh` 里；是否以别的形式做回来待定 |
| `shell.execute` / PTY | 属于能力沙箱范畴，与"跨平台 webview 核心库"的定位无关 |
| 移动端 | 三个后端都是桌面系统 webview；移动端（Android WebView / WKWebView-iOS）是另一套抽象与生命周期 |
| 兼容不带 `LogicalName` 的旧内嵌写法 | 那种资源名无法区分"目录"与"含点的文件名"，库只能猜，而猜错的表现是运行期白屏。2026-10-03 起在**构建期**报 `ORIELWEB001` 并给出正确写法（见 README「内嵌页面资源」） |

## 已知缺口（可机器断言，尚未做）

不是"要人眼"，而是**还没做**、做完就能进自检的部分。

#### 自定义 scheme 下的 Range 请求
- 现状：三个 scheme 处理器都整份应答，不解析 `Range` 请求头（三者的回调其实都拿得到请求头）。
- 影响：`wwwroot` 里放 `<video>`/`<audio>` 时 seek 会退化成"整段重下或直接失败"，流式大文件同理。
- 为什么没顺手做：内嵌资源以小文件为主（HTML/CSS/JS/图片/字体），分段读取与 206 语义的成本目前没有真实用例支撑。真要用媒体资源，先在自检里加一条"seek 后仍能播"的断言，再去实现。

#### 升级后旧的解压目录不会被清理
- 现状：0.3.0 起不再解压到磁盘，但**也不删除**历史版本留下的 `<LocalApplicationData>/OrielWeb/<程序集>/www`。
- 影响：升级过的机器上会留一份过期的前端文件（不再被加载，只是占位）。
- 为什么没顺手做：自动递归删除目录的风险大于收益——那条路径拼错一次的代价是删掉用户别处的数据，而这份残留无害。真要清理应当做成显式的（例如给应用一个 API，在 `Run()` 之前自己调），待定。

#### 页面里的 `window.open` / `target="_blank"`
- **已完成**（2026-11-11）：策略定为**一律忽略**（用户 2026-11-11 决定），三平台显式弃绝，
  页面侧 `window.open` 返回 `null`，并在 API.md §3 新增「页面发起的新窗口请求：一律忽略」一节。
  实现方式各随平台：Windows `put_Handled(1)` 且不给 `NewWindow`、macOS 让
  `webView:createWebViewWithConfiguration:…` 返回 `nil`、Linux 让 `create` 信号返回 `NULL`。
  - 为什么必须显式接，而不是"不理会"：不接时各引擎走自己的默认处置，而三者并不一致
    （有的什么都不做，有的会开一个空白窗口）。那种差异表现为"点击链接没反应"，
    而页面代码看起来完全正确——最难查的一类。
  - 每次弃绝都记一条诊断（带被丢弃的目标 URL）。**这条日志是"库弃绝"与"引擎弹窗拦截"
    的唯一区分依据**：两者对页面都表现为 `null`。Windows 侧第一版漏了它，
    是 `--selftest newwindow` 抓出来的。
  - 真机自检 `--selftest newwindow` 断言三件事：页面侧拿到 `null`、宿主诊断出现、窗口数不变。
    三条缺一不可——只看第一条无法区分"库弃绝"与"引擎顺手拦了"。
  - **一条反直觉的实测结论，记下来免得再推断错**：本库**没有**打开 WebKit 的
    `javaScriptCanOpenWindowsAutomatically`（macOS 的 `WKPreferences` / Linux 的
    `WebKitSettings`，两者默认都是 false），而程序化的 `window.open` **仍然会**走到
    `createWebViewWithConfiguration:` / `create` 信号——三平台的 CI 日志里都能看到库那条
    诊断，`--selftest newwindow` 全 PASS（run 43017750271）。
    曾按"默认 false ⇒ 请求被引擎直接拒绝 ⇒ 走不到我们的处理器"改过一版（打开那个开关），
    实测证明是多余的，已撤销。真正会被引擎按默认处置的是**不接**这些入口的情况。
  - 顺带修掉 `verify-windows.ps1` 里一处 **PowerShell 优先级陷阱**（第三处同类）：
    `Test-Stage 'pack' -or Test-Stage 'selftest'` 实测只调用第一个、返回 `False` 就结束，
    于是 `-Stages selftest -Publish` **从不重新发布**，自检对着 `dist/demo` 里的旧二进制跑。
    那种"改了代码、自检却验的是旧产物"的假绿比直接报错危险得多（本轮正是被它坑了一次）。

#### 取消令牌（`invoke` / `EvaluateJs`）
- **已完成**（2026-11-11，0.5.6）：策略与实现都按"页面驱动"落地——
  - **触发源**：页面超时（`oriel.timeout` / per-call `{ timeout }`）与窗口销毁。
    导航离开**不**取消（命令的副作用可能仍有意义，如"保存后跳转"）；应用退出不取消（进程要走了）。
  - **宿主侧不定超时**：页面超时是可配的，宿主不知道对端配了多久。唯一能对齐的时机是页面
    放弃的**那一刻**——桥接脚本在超时 reject 的同时发 `{ __oriel:'cancel', id }`。
  - **键是（窗口, id 原文）**：页面侧 id 每窗口各自自增，不带窗口会让 A 窗口的取消砍到 B 的
    命令上；用原文而非解析成整数，避免 `1.5` / `1e30` 这类值撞车或抛异常。
  - **命令方法加 `CancellationToken` 参数**（不参与 JSON 绑定，由宿主注入）。为此撤掉
    0.5.5 刚加的 `ORIELWEB109`（当时理由是"通路没贯通，收下就是假承诺"），
    并让 `ORIELWEB110` 豁免 `CancellationToken ct = default`。
  - **不破坏手写路由**：`IOrielCommandRouter` 新增带令牌的重载，给默认实现转调旧方法；
    手写路由因此自动获得"不支持取消"的降级，而不是编译失败。
  - **取消后的回执**：仍发一条 `code: "cancelled"`。页面此时多半已 settle，这条的价值是让日志
    能区分"被取消"与"跑完了但结果丢了"；取消**不**计入 `IpcFailed`（那不是应用的错）。
  - **`EvaluateJs(script, ct)` 只停止等待**，不停止脚本——三个引擎都没有取消已发出求值的能力。
    文档写清了这点，并给原 Task 挂了观察者，避免放弃后它的异常留到 GC 才冒出来。
  - **踩到一个真 bug**：`default(JsonElement).GetRawText()` 会抛 `InvalidOperationException`，
    一条缺 `id` 的消息就能炸掉整条分发路径。`DispatcherTests.MissingId_FallsBackToZero` 抓到了，
    已改为 `IdKeyOf`（缺失时用不会撞的 `"missing"`）。
  - 真机自检 `--selftest cancel` 四断言：长命令真的开始执行、页面收到 `code:'timeout'`、
    **宿主侧 token 真的被触发**、并发短命令不受影响。第三条是核心——只看页面超时，
    取消有没有生效完全看不出来。三平台全 PASS（Windows 26 模式；macOS/Linux 走 CI）。

#### 显式缓存头（`oriel://`）
- 现状：三平台响应只有 `Content-Type`，没有 `Cache-Control` / `ETag` / `Last-Modified`。Windows（`CreateWebResourceResponse` 的 headers 串）与 macOS（`headerFields` 字典）都能加，**Linux 加不了**——`webkit_uri_scheme_request_finish` 的签名里没有响应头参数，要换 WebKitGTK 2.40+ 的 `finish_with_response`。
- 影响：缓存行为目前完全由引擎的默认策略决定，库层没有契约。
- 为什么没顺手做：为一个三平台不一致的显式头去动其中两个平台，收益低于把"不设缓存头是隐式行为"写进 API.md（已写）。

#### `clipboard.writeImage({ path })` 的路径 scope：声明了才强制
- 现状：**已收口一半**。只要应用为这条命令声明过路径 scope，路径就必须通过它
  （`Allow("clipboard.writeImage", s => s.AllowPath("$APPDATA/**"))`）；
  没声明过则维持既有行为（可读任意图片文件），并在宿主诊断里告警指路。
- 为什么不做成无条件强制：H1 对 `opener.openPath` 那么做，是因为它等于任意程序执行。
  这条需要显式授权、且"写一张自己的图"本就是常见用法——无条件强制会让
  "只声明命令、不给 scope"的既有配置在**小版本升级**里被打断，那是破坏性变更，
  该在 0.6.0 的 CHANGELOG 里显式宣布，不该混进修复批次。
- 0.6.0 要做的：把"没声明 scope 就拒绝"打开（连同 `clipboard.readImage` 的回读语义），
  并同步 API.md 与 CHANGELOG 的破坏性变更段。
- 验收：`PlatformCommandTests` 的 `WriteImagePath_*` 三条 + `HasPathScopeFor_DistinguishesDeclaredFromUndeclared`。

#### 生成器诊断
- **已完成**：
  - `ORIELWEB108` 泛型命令方法。此前会生成引用 `T` 的代码，而路由类没有类型参数 →
    报错定位在 `.g.cs`，用户看不到自己那行。而且泛型在运行期也没有类型实参来源
    （IPC 参数是 JSON）——必须由生成器拦下，且**不生成路由**（与 106/107 不同：
    那两条是"注册成功但不可达"，仍要生成以避免次生错误；这条生成出来的代码根本编译不过）。
  - `ORIELWEB109` `CancellationToken` 参数。此前落到泛型读取路径，报的是"缺少 JSON 上下文"——
    那条提示把人引向"去补一个序列化上下文"，而真正的问题是类型不支持。
    误导性诊断比没有诊断更费时间。选拒绝而不是实现：贯通取消语义是 API 设计
    （见下方"取消令牌"条），在贯通之前收下参数只会让人以为取消有效。
  - `ORIELWEB110` C# 默认值。**选拒绝而不是支持**：支持需要让生成的读取代码区分
    "参数缺失"与"传了等于默认值的值"，对值类型要引入 `int?` 中间态再填源码字面量，
    等于在同一件事上并存两种"可选"表达（`int? a` 与 `int a = 5`），收益只是省一次签名改动。
    关键是不能静默——只忽略默认值的话，页面不传时得到 `default(T)`，而宿主代码里写着 `= 5`。
    **例外**：`int? a = null` / `string? a = null` 放行（语义与 `int? a` 一致，
    报错只会逼人删掉无害写法）；NRT 未开启的 `string a = null` 仍报（类型上没表达 null 合法）。
- 验收：`GeneratorDiagnosticsTests` 新增 5 组用例（108 一条 + 109 一条 + 110 两条 +
  可空默认值放行两条 + NRT 边界一条）；`AnalyzerReleases.Unshipped.md` 已登记
  108/109/110（未登记会被 RS2000 挡在构建期——这正是它的作用）。

#### Tauri 类型导出与缺失成员披露
- **已完成（0.6.0 批次的第一项）**：`event.d.ts` 补齐 `Event<T>` / `EventCallback<T>` /
  `UnlistenFn` / `EventName` / `EventTarget` / `Options`，与 `@tauri-apps/api/event`
  逐字符一致（`packages/api/tests/published-types.test.mjs` 钉住）。
  顺带发现并修掉一处：`Subscription` 此前作为公开类型被导出了——TypeScript 对 `.js`
  的声明发射会把模块级每个 `@typedef` 都变成 `export type`，`stripInternal` 挡不住；
  改法是内联那个形状。
- 剩下的：core/fs 的缺失成员已在 `src/core.js` 头部清单里如实标注（`Resource` /
  `transformCallback` / `PluginListener` / `SERIALIZE_TO_IPC_FN`），无需再补。
- `checkPermissions` / `requestPermissions`：**决定不实现**（本库的能力是构建期配置，
  没有运行时查询面）。这条在 `tauri-alignment.test.mjs` 的 `GAPS` 里带原因登记，
  不算未披露的缺口。

#### 平台细节（L3/L4/L5/L7/L8/L9/L10）
- macOS 来源缺失的退化要写进 API.md 威胁模型（与 Linux 同等披露），并评估"不透明来源直接拒绝"。
- WebView2 loader DLL 复用前校验内容哈希（现在只比大小），或写入后设 ACL；至少披露 `%TEMP%` 路径风险。
- **已完成**：`event.js` 订阅失败回调的竞态——`listen` 是同步的、失败回执要等微任务，
  这段时间里第二个订阅者可能已经加入（它看到 count > 0，于是不再发 IPC）。
  原先无条件 `delete` 会把它的计数一并抹掉；现在只减自己那一份，减到 0 才删表。
  测试见 `packages/api/tests/event.test.mjs` 的 L5 用例。
- DComposition vtable 顺序、`GetAllowedOrigins` 回读、`OleUninitialize`、`_messageHwnd` 销毁、
  macOS pump 诊断文案。
  - **已完成**：`OleInitialize` / `OleUninitialize` 配对（用字段门控，避免在初始化失败的线程上
    误减别人的计数）、`_messageHwnd` 在 `Dispose` 里销毁（它是 `HWND_MESSAGE` 隐藏窗口，
    **不会**随消息循环结束而消失——原注释是错的）、`QueryInterface` 的 `*riid` 判空
    （解引用空指针在 `[UnmanagedCallersOnly]` 帧里等于进程直接消失）、
    `g_idle_add_full` 失败时按引用回收**刚入队**的动作而不是摘队首。
    测试见 `WindowsBackendLifetimeTests` 与 `LinuxMainThreadQueueTests`。
  - **已完成**（2026-11-11）：`IDCompositionVisual` 的 vtable 槽位顺序——原声明与
    `dcomp.h` 完全不符（写成了不存在的 `SetOffset(float,float)`），真实顺序是
    `SetOffsetX(动画对象) → SetOffsetX(float) → SetOffsetY(动画对象) → SetOffsetY(float) → …`。
    COM 早期绑定按序号寻址，错一个槽不报错、只做错事。测试按 `MetadataToken` 钉住前 4 槽。
  - **已完成**（2026-11-11）：`CoreWebView2CustomSchemeRegistration.GetAllowedOrigins` 的回读——
    它此前是个 stub（永远回 `count = 0`、不看 `_allowedOrigins`），而 `SetAllowedOrigins`
    已经把值存下来了。之所以一直没暴露：origins 目前刻意留空（`Win32AssetScheme` 里页面本身
    就在该 scheme 下，同源请求不需要 CORS 条目），于是"回读 0"碰巧是对的。
    这类"实现了但没生效"的属性最麻烦的是**没有任何报错**——将来谁用它，症状是
    "设了跨源放行却依然被 CORS 拦"，排查方向会被引到别处。
    按契约实现（`CoTaskMemAlloc` 数组 + 每个字符串，所有权转移给调用方），
    测试照着调用方的义务真的释放一遍。
  - **已完成**（2026-11-11）：macOS pump 诊断文案——原文只说"pump helper 不可用"，
    读的人既不知道那是什么、也不知道该去查什么。现在写清了"这是什么 / 影响 / 常见原因 /
    怎么排查"。顺带对齐语义：失败时回收刚入队的动作（与 Linux 一致；macOS 此前**根本不回收**）。
    回收逻辑抽成 `Platform/MainThreadDispatchQueue.cs` 共享（两边各写一份就会有一边漏边界）。
  - 剩下的：无（这一组全部收口）。

#### 工程化（CI / 打包 / 版本闸）
- **已完成**（2026-11-11）：README 的 Windows arm64 按证据规则**降级**为
  "⚠️ 仅 IL 交叉编译；未在 arm64 真机运行"。此前它与 x64 合并声明为"✅ 真机验证"，
  而 `compile-windows` / `smoke-windows` 都只跑 `win-x64`——那一格是空的。
  这是 `verify-matrix.ps1` 增强后**首次运行就抓到的**。
  若要升级为真机验证：补一个跑在 `windows-11-arm` 上的 job 并写进那一行的证据列。
- **已完成**（2026-11-11）：`verify-matrix.ps1` 增强了三件事——
  ① `smoke-*` 的名字平台/架构必须与 `runs-on` 一致（`compile-*` 是交叉编译，不受此约束：
  `compile-macos` 刻意跑在 windows-latest 上）；
  ② README 行声明的架构必须被该行引用的 job 真正覆盖（**引用存在 ≠ 覆盖对**）；
  ③ 声称"真机验证"却没有 `ci:` 引用的行直接失败。
  顺带修掉三处 `("A{0}" + "B" -f $x)` 的 PowerShell 优先级陷阱——那样写的 `-f`
  只作用于第二个字符串，错误消息里会留下字面的 `{0}`，等于丢掉排查线索。
- **已完成**（2026-11-11）：`verify-package.ps1` 补了两个场景——
  ① **场景 D 工程路径含空格与中文**：资源名由 targets 的 LogicalName 决定（固定前缀），
  与工程目录无关，所以期望是"与场景 A 逐字符一致"；真出事的地方是路径里的空格与非 ASCII
  穿过 MSBuild 的 glob / `%(RecursiveDir)`，而那种错的表现是"少嵌了某个文件"，不是显式报错。
  ② **场景 E NuGet 包消费 + 原生 AOT 发布**（`-Aot`，默认关）：上一步只 `dotnet build`，
  **AOT 发布那条路此前没被验证**，而裁剪会不会把嵌入资源带走正是它要回答的问题。
  断言 AOT 产物问出来的资源名与托管构建**逐字符一致**（首次跑通即证明：4 条资源一条不少）。
  CI 里只在 Windows 的 test job 开它：MSVC 是 runner 自带的，而 Linux 的 test job
  没装 clang/zlib1g-dev（那是 compile-linux 才装的）；这个风险是平台无关的 IL/裁剪行为，
  一个平台直测就够。
  - **踩到一个坑并记录**：样例是 `OutputType=WinExe`（PE Subsystem=2，GUI 而非 Console），
    它的 AOT 产物在"变量捕获"路径上 stdout 会丢——同一个 exe 直接在控制台跑能看到 4 行，
    而 `@(& exe --list-resources)` 得到 0 条；`| Set-Content` / `| Out-String` / `cmd /c`
    都能拿到。**第一版断言因此假红**，看起来像"裁剪把 wwwroot 带走了"。
    现在场景 E 经文件中转再读回，并加了两条"两边都非空"的守卫，防止"两边都空"式的假通过。
    这个坑写进了 `samples/OrielMinimal/Program.cs` 的无头通道注释（那是手工验证入口），
    免得下一个人重踩。
- **已完成**（2026-11-11）：Linux Wayland 的证据列改指 `CROSS-PLATFORM-VERIFICATION.md`
  的 VM 记录（Ubuntu 22.04 Wayland，19/19 PASS），并如实标注 CI 那两条跑在 Xvfb、
  **只覆盖 X11**——此前那一行写着"X11 与 Wayland 双后端"却只挂 CI job，
  与 Windows arm64 那一格同类的"引用存在但覆盖不对"。
- **已完成**：`release.yml` 的 demo/publish 补 `timeout-minutes`（30 / 20），
  Linux 的 apt 步骤与 ci.yml 逐字对齐（外层 `timeout` + 三次重试）——
  release 是串行不取消的，卡住的那次会挡住后面所有 tag。
- **已完成**：版本闸加入 `packages/api` 的三处比对（`package.json` +
  `package-lock.json` 的顶层与 `packages[""]`）。此前那个包的版本完全没被这条链路校验过，
  0.5.5 发版时发现它与主包只是"碰巧一致"。
- **已完成**（2026-11-11）：fs 句柄表跨窗口共享在 API.md 记为已知设计（S2）——
  在 §4 威胁模型表后新增"刻意不隔离的东西"一节，写清了"为什么这样 / 影响与边界 /
  真要隔离怎么做"。生成器增量缓存优化（S5）列为性能项。

> 这一节至此**全部收口**：剩下的是上面"平台细节"里 macOS pump 之外没有别的，
> 以及 `clipboard.writeImage` 的无条件强制（属 0.6.0 的破坏性变更批次，见上）。

## 待真机验证清单

这些项**只能在有桌面会话的真机上、靠人眼**判定（无头环境造不出它们要的输入或显示）。
每一项都写了命令与预期——照着做即可，没有工具会替你判定它们。

#### DevTools 开关（macOS / Linux）
- 环境：有图形会话的 macOS（Safari 的「开发」菜单）或带桌面的 Linux（WebKitGTK 右键菜单）。
- 步骤：以 `Debug = true` 启动（demo 默认 `.UseDebug()`）；macOS 从 Safari → 开发选中该进程，Linux 在页面内右键 → 检查元素。
- 预期：能打开 Web Inspector；把 `Debug` 置 false 后同一入口不再出现。
- 若不符：macOS 先确认系统 ≥ 13.3（`isInspectable` 是 13.3+ 的 API，更早的系统上库会跳过设置）；Linux 看 `webkit_settings_set_enable_developer_extras` 是否被调用。

#### 主题切换的实时性（未验证）
- 环境：任意桌面环境（Linux 需要真实桌面而非 xvfb）。
- 步骤：不带参数运行 demo，在系统设置里切换深色/浅色。
- 预期：一秒内页面主题跟随，宿主侧 `ThemeChanged` 触发一次。
- 若不符：Windows 看 `WM_SETTINGCHANGE` + `ImmersiveColorSet`；Linux 看 GtkSettings 的 `notify::`；macOS 看通知是否到达。

#### 窗口图标（三平台）
- 环境：三平台桌面各一次。
- 步骤：直接运行 demo（不传 `--icon`）——Linux 看任务栏、macOS 看 Dock、Windows 看任务栏与 Alt-Tab；GNOME 上再装一份 `.desktop`（`StartupWMClass` 必须等于 `xprop WM_CLASS` 的 res_class）。
- 预期：任务栏/Dock 显示项目图标；`xprop -id <窗口id> _NET_WM_ICON` 有图标数据。
- 若不符：先确认产物是**新构建的**（`dist/<rid>/app.png` 在不在）；GNOME 上 `StartupWMClass` 是否与 `WM_CLASS` 逐字符相同。
- 状态：**图标文件缺失的降级路径与相对路径解析基准已在 Windows 真机取证**（2026-10-14：`--icon probe-icon.ico --require-icon` 从 cwd `E:\` 启动仍成功 ⇒ 按 exe 目录解析；缺失 + 不开严格模式 ⇒ 降级且退出码 0；缺失 + 严格模式 ⇒ 如期抛 `FileNotFoundException`）。**图标最终显示效果仍是人眼项**。

#### 缩放变化事件（macOS 优先，2026-10-18 新增）
- 环境：macOS 桌面（有两块缩放不同的显示器最好）。
- 步骤：页面 `getCurrentWindow().onScaleChanged(e => console.log(e.payload))`，然后
  (a) 把窗口从内置屏拖到外接屏（或反之）；(b) **只**在"系统设置 → 显示器"里改缩放，窗口不动。
- 预期：两种情况都收到 `{ scaleFactor, size }`，`size` 是**物理像素**客户区。
- 若不符：`windowDidResize:` / `windowDidMove:` 覆盖的是 (a)，
  `windowDidChangeBackingProperties:` 覆盖的是 (b)——两者都是 `NSWindowDelegate` 的方法，
  所以先确认 delegate 真的挂上了（`_nsWindowDelegate`），再看选择器名有没有拼错。
  **选择器名写错不会报错，只会静默不被调用**，这是本项最难查的一点。
- 状态：Windows 侧已真机取证（`--selftest window` 覆盖 forwarding 开关，但**缩放变化本身造不出来**——
  要跨屏拖动或改系统缩放）；macOS/Linux **未在真机跑过**（2026-10-18）。

#### 窗口图标与底色的最终显示（三平台，2026-10-18 补充）
- 环境：三平台桌面各一次。
- 步骤：页面调用 `setIcon('/path/app.png')` 与 `setIcon(bytes)`（字节形式同一张图），
  以及 `setBackgroundColor('#2f2f2f')`。
- 预期：图标真的变了（任务栏/Dock）；透明页面上能看到底色。
- 说明：这三条的**命令层与解码**都有自动化覆盖（`--selftest window` 70 余项，含非法 base64 被拒、
  坏字节不抛），但**"看起来对不对"只能人眼判定**：
  - 宿主没有"读回当前图标/底色"的接口（加一个就是偷偷发明 API，会被对齐测试的 `EXTRAS` 拦住）；
  - macOS 的 `setIcon` 影响的是**整个应用**的 Dock 图标，不是单个窗口。

#### macOS 隐藏启动

- 环境：macOS 桌面。
- 步骤：`OrielDemo --hidden`，用 `CGWindowList` 断言窗口不在 on-screen 列表里。
- 预期：窗口不上屏，但页面照常加载、IPC 照常往返（Linux 侧已在 WSL 验证过同样的语义）。

#### 退出看门狗与退出事件（Linux / macOS 未验）
- 状态：**Windows 已真机取证**（2026-10-14）：`--selftest exit` 正常退出两个事件都触发、退出码 `0`；
  `--selftest exit-timeout` 在 `Run()` 返回后堵死 UI 线程，看门狗 3 秒后强杀、退出码 `2`、
  `ExitTimeout` 事件先于强杀触发（并指出了卡住的阶段）。Linux/macOS 侧实现同源但**未在真机跑过**。
- 环境：Linux（GTK）与 macOS（AppKit）桌面各一次。
- 步骤：`OrielDemo --selftest exit`，再 `--selftest exit-timeout`（后者 3 秒后以退出码 2 结束是**预期**）。
- 预期：与 Windows 一致——`Exiting` → `ExitCompleted`，超时模式下看门狗强杀且退出码为 `ForceExitCode`。
- 若不符：先确认该平台的 `Quit` 真的结束了消息循环（Linux `GtkMainQuit`、macOS `NSApplication.terminate:`），
  再看 `Thread.Sleep` 的看门狗线程是否真的起来了（它是后台线程，不该阻止进程退出）。

#### 线程归属与编组（Linux / macOS 未验）
- 状态：**Windows 已真机取证**（2026-10-14，`--selftest threading` 八项全过）。Linux/macOS 同源未验。
- 环境：Linux 与 macOS 桌面各一次。
- 步骤：`OrielDemo --selftest threading`。
- 预期：`THREADING-SELFTEST: PASS`——UI 线程上 `IsOnUiThread` 为 `true`、后台线程为 `false`、
  `InvokeOnUiThread` 的动作确实执行在 UI 线程上、`PostToUiThread` 返回 `true` 且回调落在 UI 线程。
- 若不符：macOS 看 `_mainManagedThreadId` 是否在 `NSApplication.run` 之前就记下了（构造函数里记的，
  应当正确）；Linux 看 `Environment.CurrentManagedThreadId` 与 GTK 主循环线程是否同一个。

#### 窗口尺寸语义在高 DPI 下的逐像素正确性（Windows 未验；Linux/macOS 未跑）
- 背景：`WithSize` / `Resize` / `WithMinSize` 现在统一为**逻辑像素的客户区**语义。Windows 侧走
  "逻辑 → 物理 → 客户区转整窗（`AdjustWindowRectExForDpi`）"，frameless 不换算（客户区已铺满整窗）。
  100% 缩放下已在 Windows 真机取证（`784x561` → `800x600`，运行期 `Resize(640,480)` 同样精确）。
- 环境：**有缩放显示器的机器**——Windows 125% / 150% / 200% 各一次（含跨不同缩放的副屏拖动窗口后
  再 `Resize`）；Linux X11 与 Wayland 的 200%；macOS Retina。
- 步骤：`OrielDemo --selftest geometry`，再 `--selftest geometry-frameless`。
- 预期：两者都 `GEOMETRY-SELFTEST: PASS`，客户区与请求值相差 ≤ 2 逻辑像素。
- 若不符：先确认 `ClientToWindowSize` 传的是**窗口所在显示器**的 DPI（`GetDpiForWindow`），
  不是系统 DPI——用错在副屏上会明显偏小。再确认 frameless 分支真的跳过了换算
  （跳过失败的症状是"无边框窗口比请求的大一圈"，即多了一整套边框 + 标题栏）。
- 状态：Windows 100% 已验；**高 DPI 与另两个平台均未取证**。

#### 几何查询在 Linux / macOS 上的实际行为（`--selftest geometry`）
- 背景：新增四个查询（`InnerSize` / `OuterSize` / `InnerPosition` / `OuterPosition`）。
  Windows 100% 缩放下已取证，数值与边框开销**交叉吻合**（`Outer - Inner = 16x39`、
  `InnerPos - OuterPos = 8,31`）。
- 环境：Linux **X11 与 Wayland 各一次**（两者行为差别很大）、macOS 装饰窗口与无边框窗口各一次。
- 步骤：`OrielDemo --selftest geometry`，再 `--selftest geometry-frameless`。
- 预期：两者都 `GEOMETRY-SELFTEST: PASS`。**Wayland 下允许部分关系不成立**
  （代码里已对 Linux 放宽"外框必须严格更大"那条）：`OuterSize` 可能等于 `InnerSize`、
  `InnerPosition` 恒等于 `OuterPosition`、`OuterPosition` 恒为 `(0,0)`
  ——合成器出于隐私不告诉客户端这些。
- 若不符：X11 下若 `OuterSize == InnerSize`，检查 `gdk_window_get_frame_extents` 是否真被调到
  （它要求窗口已 realize）；macOS 下若 `InnerPosition.Y == OuterPosition.Y` 而窗口有标题栏，
  检查 `contentView.frame` 读出来的是不是内容区高度（应当比整窗矮一个标题栏）。
- 状态：**未取证**。上述平台限制已写进 API.md 与 CHANGELOG，不假装支持。

#### Linux 高 DPI 下的拖动是否跟手（拖动已在库侧，不要折算）
- 环境：Linux 桌面 + 真实高 DPI 缩放（X11 与 Wayland 各一次；`GDK_SCALE=2` 只在 X11 下生效）。
- 步骤：在 200% 缩放下跑 demo；按住标题栏拖动；双击标题栏。
- 预期：拖动跟手、双击最大化正常、单击标题栏不移动窗口（拖动与双击都由库实现，页面只标了拖动区域）。
- 若不符：窗口比指针**快约 scale 倍**说明增量被设备像素污染——拖动逻辑在库侧
  （`src/OrielWeb/Bridge/oriel-bridge.js`），不要改成在宿主侧乘 scale；慢到 1/scale 则说明宿主侧多折了一次。

#### 多窗口（运行时新建的窗口）
- 环境：三平台桌面各一次。
- 步骤：跑 `OrielDemo --manual-check`，点「打开 todo 窗口」（可连点几次）；关掉其中一个；
  在任一窗口里执行 `oriel.invoke('win.close')`。
- 预期：新窗口正常加载同一个 todo 页面（标题栏显示「窗口 N」）；关掉一个窗口应用**不退出**；
  `win.close` 只关掉**发起调用的那个**窗口。
- 状态：三平台都已有机器断言（`--selftest multiwindow`：运行时建窗 → 各自会话 → 关一个不退出 →
  `win.close` 只关发起者 → 窗口列表摘除）——`smoke-linux` / `smoke-windows` / `verify-macos.sh` 都跑它，
  2026-10-02 三平台全绿。剩下的是人眼确认窗口位置/大小/标题这类可见效果。
- 若不符：先看新窗口是否装配成功——Windows 上"同一个 user data folder 上两个 WebView2 环境"
  曾使第二个窗口起不来（现已改为全进程共享一个环境，见 API.md §1）。

#### 标题栏拖动与双击（改为库接管之后）
- 环境：三平台桌面各一次。
- 步骤：按住标题栏拖动；双击标题栏；单击标题栏上的最小化/关闭按钮；在标题栏里的输入框上拖选文字。
- 预期：拖动跟手、双击切换最大化/还原、按钮照常可点、可交互元素不被当成拖动区域。
- 若不符：先确认标题栏元素带了 `data-oriel-drag-region`（`oriel.dragRegion()` 返回的区域数应 ≥ 1）；
  再确认注入脚本装上了（**不可信来源根本不安装桥接**，`window.oriel` 不存在——见 API.md §4）；
  `win.*` 由库内建、应用无需注册，拖动实现本身在 `src/OrielWeb/Bridge/oriel-bridge.js`。

#### 托盘图标的可见性
- 环境：有托盘区的桌面（Windows 任务栏；macOS 菜单栏；Linux 用 Xfce/KDE，或装了 AppIndicator 扩展的 GNOME）。
- 步骤：`OrielDemo --selftest shell`（建托盘并设一份含分隔线/勾选/禁用/子菜单的菜单），在托盘区点开。
- 预期：图标出现、悬停有 tooltip、菜单按设置渲染、点 Quit 退出。
- 若不符：Linux 上 GNOME Shell 未装扩展、或 Wayland 会话下本就不显示，属平台事实。
- 状态：**托盘 API 的机器断言已补齐**（2026-10-14）：`--selftest tray` 覆盖多托盘、图标对象、
  模板标记、菜单、显隐、标题、按 id 移除（Windows 真机 26 项全过）；`--selftest tray-js` 覆盖
  页面经 `oriel.tray` 的完整命令往返。2026-10-25 又补了**订阅链路**：用后端的合成派发入口
  断言"后端事件 → `OrielTray.Clicked` / `TrayEvent` / `MenuItemClicked`"这一段没断。
  **图标最终显示效果、悬停事件的真实送达、以及左键单击本身仍是人眼项**（见下一节）。

#### 托盘点按的真实送达（Windows）
- 环境：Windows 任务栏有通知区的桌面。
- 步骤：`OrielDemo --manual-check`，对托盘图标左键单击、右键单击、再点一个菜单项。
- 预期：左键触发 `Clicked`（操作台会回显）；右键弹出菜单；点菜单项触发 `MenuItemClicked` 并带上该项 id。
- 为什么只能人眼：`--selftest tray` 的合成派发只证明"事件从后端发出后能到达公开事件"，
  **不能**证明系统真的把点击送进来了。0.3.5 把回调切到 `NOTIFYICON_VERSION_4`（图标 id 移到
  `HIWORD(lParam)`、`NIN_SELECT` 取代旧的 `WM_LBUTTONUP`）——这类"换了底层消息、上层语义不变"
  的重构，只有真机能判定。
- 若不符：先订阅 `RawEvent` 看有没有原始回调（有 = 送达正常，问题在识别或弹菜单；
  没有 = 回调通道本身有问题，检查 `uCallbackMessage` 与专属消息窗口）。
- 状态：**未复验**（0.5.1 之后未在真机点过）。

#### 托盘事件在 Linux / macOS 上的送达
- 环境：Linux（GTK）与 macOS（AppKit）桌面各一次。
- 步骤：`OrielDemo --selftest tray` 之后，用操作台/页面订阅 `oriel.tray.onEvent`，把指针移入、
  在图标上移动、移出、单击、双击，观察事件序列。
- 预期：**macOS** 能收到 Click / Enter / Move / Leave（`NSTrackingArea`），按键与坐标为 `null`；
  **Linux** 能收到 Click / DoubleClick 且**带按键与坐标**（`button-press/release-event`），
  Enter/Move/Leave **收不到**——`GtkStatusIcon` 没有悬停信号，是平台事实（Tauri 2 同样标注 Unsupported）。
- 若不符：macOS 先确认跟踪区域装上了（`addTrackingArea:` 是否被调用、`NSTrackingArea` 是否返回 nil）；
  Linux 确认 `button-press-event` 信号真的连上了（`g_signal_connect_data` 的返回值）。
- 状态：**Windows 侧已真机取证**（V4 回调：`NIN_POPUPOPEN/CLOSE` 给 Enter/Leave、
  `WM_MOUSEMOVE` 给 Move、`GET_X/Y_LPARAM` 给坐标）。Linux/macOS **未在真机跑过**。

#### 托盘图标在 Windows 上的 PNG 支持
- 环境：Windows 桌面（WebView2 运行时）。
- 步骤：`OrielDemo --selftest tray` 已断言 `app.png` 与 `app.ico` **都能解码**（托管解码器）；
  再给一个托盘传 PNG 图标，看它是否真的显示出来。
- 预期：PNG 图标正常显示——此前 Windows 走 `LoadImageW`（**只认 ICO/BMP**），PNG 会静默失败；
  现在统一走 `OrielImageCodec` → `CreateIconIndirect`，格式支持三平台一致。
- 状态：解码与 HICON 构造已在真机跑通（自检里用 PNG 建托盘未报错），**视觉确认仍是人眼项**。

#### 通知的展示
- 环境：三平台桌面各一次。
- 步骤：`OrielDemo --selftest shell`，看通知横幅。
- 预期：横幅显示标题与正文。**点击横幅不会有任何回调**——该能力已整体移除。
- 若不符：Windows 看系统的通知/专注助手开关；macOS 看通知权限（未打包运行时通知归属于 Script Editor，可能被静音）。

#### 上下文菜单的外观与交互
- 环境：三平台桌面各一次。
- 步骤：devtools 控制台执行 `oriel.invoke('win.contextMenu')`（**库内建**的标准编辑菜单）；
  覆盖各形态的那份丰富菜单在操作台里（`manual.contextMenu`：自定义项、勾选、禁用项、子菜单）。
- 预期：标准菜单只含剪切/复制/粘贴/全选/关闭且能作用于选中内容；丰富菜单按设置渲染
  （子菜单可展开、勾选项带勾、禁用项灰、分隔线正确）；role 里的 Close 能关掉窗口。
- 若不符：macOS 看 `MacOSMenu.Popup` 的"没有当前 NSEvent"日志（后台回调里调用时无法定位菜单）。

#### 对话框的外观与交互
- 环境：三平台桌面各一次（弹出后无人操作会一直等，因此不进自检）。
- 步骤：调用 `ShowOpenFileDialog`（开 `AllowMultiple`）、`ShowSaveFileDialog`、`ShowFolderDialog`。
- 预期：过滤器下拉显示传入名称；多选后每一项都在返回数组里；保存时补上 `DefaultExtension`。
- 若不符：Windows 的文件夹选择器**不支持初始目录**，属已知取舍；Linux 看 `ReadSelectedPaths` 的 `GSList` 遍历。

#### 文件拖放（整体未验证）
- 环境：三平台桌面各一次（Linux 需要真实桌面，xvfb 里没有可发起拖拽的文件管理器）。
- 步骤：拖入 1 个文件、多个文件、一个文件夹、一段选中的文本。
- 预期：前三种触发 `FileDropped` 且路径逐字符正确，拖文本**不触发**。
- 若不符：先确认**落点是否被接管**（Windows 的 `AllowExternalDrop`、macOS 的 `registerForDraggedTypes:`、Linux 的 `gtk_drag_dest_get_target_list`）——URI 解析本身已有单测，路径错了先怀疑落点。

#### 内建右键菜单（接管式）
- 环境：三平台桌面各一次。
- 步骤：普通区域右键；在输入框里选中后右键；切到「平台原样」「完全不弹」各试一次；**连点右键 5～6 次**。
- 预期：默认只出现剪切/复制/粘贴且真的作用于选中内容；另两种策略符合设置；连点不崩。
- 若不符：先看策略是否生效（操作台右上角日志会打印当前策略），再按平台看钩子是否被调用（Windows 需要较新的 WebView2 运行时才有 `ContextMenuRequested`）。

#### 打包产物与安装（三平台各一套）
- 状态：打包已从"自研打包器"换成 **Velopack**（`vpk`）。三平台**打包都跑过**：Windows
  （`publish.ps1 -Bundle` → Setup.exe / .msi / Portable.zip）、Linux（`wsl_publish.ps1 -Bundle` →
  `OrielDemo.AppImage`，且在 WSLg 里运行它跑通了 `--selftest nav`）、macOS（release 工作流，
  2026-10-02 的 `v0.1.8` 出 `OrielDemo-osx-Setup.pkg` + `-osx-Portable.zip`）。产物只留"能装的东西"
  ——更新包与更新清单打包后即删（本仓库不发更新源）。
  但**安装与卸载没有验过**：原先那条"MSI 静默装卸 + 查快捷方式"的机器断言随自研打包器一起去掉了，
  现在机器层只剩"打包成功 + 产物齐备"。
- 环境：三平台各自一次（与 AOT 一样，`vpk` 只能产出所在平台的产物）。
- 步骤：`pwsh tools/publish.ps1 -Bundle`；然后**真装一次**——跑 `Setup.exe`（或 `.msi`），
  确认装得上、开始菜单有快捷方式、能启动、卸得干净。macOS 先
  `bash tools/make-macos-app.sh dist/demo/OrielDemo dist <版本>` 再 `vpk pack --packDir dist/OrielDemo.app`
  （**别传 `--icon`**：macOS 上只认 `.icns`，另外 `.app` 里必须有 `Contents/Resources/`——两条都在
  README 的打包一节里写了），最后打开 `.app`；Linux 跑那份 `.AppImage`。
- 预期：安装后能启动且页面正常；卸载后安装目录消失。
- 若不符：先看 `vpk` 的输出（未签名时它会明确警告），再按平台看系统事件日志。

#### Windows 上指针移出窗口后的 hover 复位
- 环境：Windows 桌面（WebView2 运行时）。
- 步骤：跑 demo，把指针悬停在标题栏或某个按钮上让 `:hover` 亮起；然后把指针**快速移到窗口外**（另一个应用上），别再回窗口内移动鼠标。
- 预期：悬停态立刻消失，tooltip 一并收掉。库在 `WM_MOUSELEAVE` 时向 WebView2 补发 `COREWEBVIEW2_MOUSE_EVENT_KIND_LEAVE`（2026-10-11 加的）；若仍滞留，说明 WebView2 对这个事件的处理与预期不符，得换清 hover 的手段。
- 若不符：先确认 `WM_MOUSELEAVE` 真的到了（`TrackMouseEvent` 的 `TME_LEAVE` 是否武装成功、`_trackingMouseLeave` 是否被复位），再看补发 LEAVE 的前提是否满足（移动过至少一次鼠标，才有可用的落点）。

#### Windows 上隐藏窗口后不再残留"吃点击"的渲染窗口
- 环境：Windows 桌面（WebView2 运行时），单显示器与多显示器各一次。
- 步骤：`Hide()`（或托盘「隐藏主界面」）之后，用 `EnumWindows` 找类名 `Chrome_WidgetWin_1`、属主是本进程树的顶层窗口，断言其 `IsWindowVisible == false`；再用 `WindowFromPoint` 探测原窗口区域四角与中心，命中的应是桌面（`WorkerW`/`Progman`）而不是那个窗口。`Show()` 后界面完整重现。
- 预期：隐藏后 `WebviewWindow.IsVisible == false`（`oriel.invoke('win.isVisible')` 回 `false`）；探测点全部落到桌面；显示后无空白框、无位置错位；托盘「隐藏 ↔ 显示」连续切换 10 次无累积偏移或残留；最大化 / 还原 / 边缘缩放 / 关闭入托四条路径各测一遍。
- 另测 `WithHidden()` 闸门：起手隐藏、等前端 `ui.ready` 再 `Show()` 的窗口，在闸门期间做同样探测，不应出现吃点击的窗口。
- 若不符：先确认走的是组合宿主（`CreateCoreWebView2CompositionController`），再确认 `Hide()` 是否真的驱动了 `ICoreWebView2Controller.IsVisible`（而非只 `SW_HIDE` 宿主）。
