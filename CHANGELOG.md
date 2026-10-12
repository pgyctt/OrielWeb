# 更新日志

本文件记录 OrielWeb 各版本的用户可感知变化。格式参照 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)。

**发布纪律**（2026-10-05 评审 P2-18 后成文）：

- 版本号唯一来源是根 `Directory.Build.props` 的 `<Version>`；tag 形如 `vX.Y.Z` 与之一一对应，`release.yml` 会校验一致性。
- **改版本的提交与打 tag 是同一次提交**（0.1.8 之后已是这个形态）。历史上 0.1.3–0.1.7 有发布/排障提交但没有对应 tag（0.1.5 被整体跳过），这些版本在仓库里已不可回溯—— NuGet 上仍可查，但源码状态只能靠提交区间近似还原。下表 0.1.3–0.1.7 的条目即按提交区间还原的近似记录。

## 0.3.4 - 2026-10-12

Windows 组合宿主下"隐藏窗口仍吃点击"的修复批次。**宿主侧针对 0.3.3 的临时兜底（按类名 `Chrome_WidgetWin_1` 隐藏残留顶层窗口、并在 `Show()` 时对称还原）可以整段删除**——本版起 `Hide()` / `Show()` 已覆盖全部承担呈现与命中测试的窗口。

### 修复

- **Windows：`Hide()` 之后 WebView2 渲染窗口仍浮在桌面上吃点击**。本窗口走 `CoreWebView2CompositionController`，WebView2 是 DirectComposition 合成树里的一个视觉，其浏览器 HWND（类名 `Chrome_WidgetWin_1`，属主 `msedgewebview2.exe`）是一个**顶层窗口**、**不是**宿主 HWND 的子窗口——对宿主发 `SW_HIDE` 带不走它，它会继续参与合成与命中测试，把整块区域的鼠标点击全部吃掉。现场表现为"点桌面没反应、点图标无反应"，极易被误判成桌面/系统故障（实测：宿主 `Process.MainWindowHandle = 0`、`explorer.exe` 正常、桌面图标未隐藏，`WindowFromPoint` 四个探测点全部命中那个残余窗口）。现在 `Hide()` / `Show()` 一并驱动 `ICoreWebView2Controller.IsVisible`（隐藏时先置 `false` 再 `SW_HIDE` 宿主；显示时先 `SW_SHOW` 宿主再置 `true`），顺带停掉 Chromium 合成、省一份全屏 GPU 开销；**不引入任何新的原生依赖**，`WithFrameless` / `WithHidden` / `WithMaximized` 调用方无需改动。
- **Windows：`WithHidden()` 首帧闸门期间同样在吃点击**。隐藏启动时窗口在 `PostCreate` 就 `SW_HIDE` 了，但 WebView2 装配完成后原本**无条件**把控制器置为可见——于是闸门期间（等前端 `ui.ready`，可达 8 秒）那个顶层渲染窗口就已经浮在桌面上吃点击，"程序还没显示出来就已经在吃点击"。现在装配按**可见性意图**恢复，闸门由宿主 `Show()` 打开。

### 新增

- **`WebviewWindow.IsVisible { get; }`**（以及内建 `win.isVisible` 命令）。此前宿主没有任何手段确认窗口"真被藏住了"——上面的缺陷只能靠外部 `WindowFromPoint` 探测才看得见。三平台语义一致：`Hide()` 之后为 `false`，**最小化仍算显示中**（不清可见位）。Windows 用 `IsWindowVisible`、Linux 用 `gtk_widget_get_visible`、macOS 用 `NSWindow.isVisible`。纯新增，向后兼容。

### 文档

- API.md §3 窗口成员表补 `IsVisible`；§9 内建命令表补 `win.isVisible`（内建里唯一的查询类命令）。
- 代码里写明一条**跨平台不变量**：`Show()` / `Hide()` 必须覆盖**全部**承担呈现与命中测试的表面，藏好后不留任何仍在参与合成与命中测试的残余窗口（见 `Win32WindowHost.Hide` 的 XML 文档），避免以后再退化。

## 0.3.3 - 2026-10-11

第三轮评审 P3 遗留里"值得做"的四条（2026-10-11 对着代码复核后判定：同批的另三条——ModuleInitializer 时序、托盘解析散落、`EnsureClassRegistered` 无锁——前提不成立，未动代码）。

### 修复

- **Windows：指针移出窗口后页面的悬停态滞留**。组合托管下 WebView 收不到任何系统输入，"指针离开"同样要宿主转发——此前 `WM_MOUSELEAVE` 只复位了内部跟踪标志、**没有**把 `COREWEBVIEW2_MOUSE_EVENT_KIND_LEAVE` 送进 WebView，于是 `:hover` / tooltip 会停在被移出前的位置上，直到指针再次进入并移动。现在离开时按窗口内最后一个有效落点补发 LEAVE；失焦（`WM_KILLFOCUS`）**刻意不补**——失焦不等于指针离开，补了会凭空清掉页面状态。**待真机确认**（见 ROADMAP）。
- **macOS：关窗后仍在途的 `oriel://` 请求永不回执**。`AssetSchemeStart` 在"状态表里没有这个 handler"（窗口销毁时整表移除）与"请求取不出 URL"这两条路径上原本直接返回，引擎就在那条 scheme task 上一直等——eval 回环早有 `FailPendingEvals` 兜底，scheme 侧漏了同一手。现在两条路径都给出终结（`NSURLErrorCancelled` / `NSURLErrorBadURL`）；同时记住被 `stopURLSchemeTask:` 停过的 task，**对已停的 task 不再回执**（Apple 的契约，撞上会触发 WebKit 断言）。
- **Linux/macOS：资源"读失败"被报成"不存在"**。`oriel://` 读到表里存在、但流读不出来的资源时，Linux 归 303（`FILE_DOES_NOT_EXIST`）、macOS 归 -1100，排障会被告知"资源没打包"，真因只在诊断文本里。现在分开归类：读失败 → Linux 399（`FAILED`）、macOS `NSURLErrorCannotOpenFile`（-3001）；Windows 本来就是 500 / 404 分开的。
- **入站消息规模闸（1 MiB）**。三平台的入站路径都是「整串读进托管内存 → `JsonDocument.Parse`」，此前没有任何上限。现在超过 1 MiB 的入站消息**不解析即丢弃**，并经 `IpcRejected` 的新层级 `OrielIpcRejectionLayer.Size` 上报（页面侧收不到回执——`id` 就在那份被丢弃的 JSON 里）。出站方向不设闸：回执 JSON 由宿主自己构造。

### 文档

- API.md §4 增补**威胁模型**一段，把三条口径写实：入站 1 MiB 上限、**没有取消令牌**（`invoke` / `EvaluateJs` 发出去只能等，页面侧 30 秒超时是唯一出路；命令函数也拿不到取消信号）、"可信页面 DoS 不在防线内"。
- API.md 的资源行写明：**不设任何缓存头**（没有 `Cache-Control`/`ETag`/`Last-Modified`，缓存行为由引擎默认策略决定，不是契约）；以及"读失败"与"不存在"的分平台归类。
- ROADMAP 增两条已知缺口（取消令牌、显式缓存头——Linux 的 `webkit_uri_scheme_request_finish` 没有响应头参数，加不了）与一条待真机项（Windows hover 复位）。

## 0.3.2 - 2026-10-11

下游反馈（一个真实工业上位机的迁移复盘）后的修复批次。

### 新增

- **`OrielWindowOptions.BackgroundColor` / `WithBackgroundColor(uint argb)`（以及 `(byte r, byte g, byte b)` 重载）**：页面渲染出来之前那一帧的**底色**，用来消掉所有 webview 应用都有的启动闪色（工业界面尤其刺眼）。三平台各落到引擎的等价开关——Windows `DefaultBackgroundColor`、macOS 窗口底色 + `WKWebView.underPageBackgroundColor`（12+）、Linux `webkit_web_view_set_background_color`。Windows 路径已真机验证（探针窗口实测渲染成设定的深色底）。
- **`OrielDiagnostics.Reported`（库内诊断通道）**：进程级事件，库自己的告警与失败留痕都走它（IPC 被拒、shell 路径被拒、单实例锁不可用、托盘/通知失败、原生 trampoline 兜底异常……）。它取代了 `Debug.WriteLine`——后者带 `[Conditional("DEBUG")]`、条件在**调用方程序集的编译期**求值，于是发布包里这些调用**根本不存在**（实测 0.3.1 的 Release 程序集里 `[OrielWeb]` 命中 0）。无订阅者时不构造参数、不分配；有订阅者时逐个调用并各自兜异常。**全库 74 处调用点已迁移。**
- **`OrielApp.CommandInvoked` / `CommandFailed`**：应用命令的执行观测——命令名、耗时、成功与否（前者），失败时异常**原样**带出（后者，只给宿主，页面拿到的仍是净化文案）。内建 `win.*` 不计入（拖动期间高频，会淹没"哪条命令慢"）。
- **`OrielWindowOptions.Zoom` / `WithZoom(bool = true)`**：关掉用户手势缩放（Ctrl/Cmd + 滚轮、Ctrl/Cmd + 加减号/0、触摸板捏合）。宿主侧设引擎专有开关（Windows `IsZoomControlEnabled` + `IsPinchZoomEnabled`、macOS `allowsMagnification`），文档层再由桥接脚本做捕获期拦截——**Linux 的 WebKitGTK 没有等价单开关，靠的正是那道拦截**。
- **`OrielApp.IsSecondaryInstance`**：`Build()` 之后即可查询"本实例是不是第二个实例"。

### 变更（破坏性 / 行为可见）

- **单实例判定从 `Run()` 提前到 `Build()`**：宿主可以在装配配置/数据库/设备连接**之前**分流第二实例（此前第二实例会先把这些副作用跑一遍）。代价是 `Build()` 不再纯构造（会抢一次进程级锁）；接收"激活请求"的管道仍在 `Run()` 建立，因此 `Build()` 与 `Run()` 之间到达的第二实例会被正确判定并退出，但它的激活请求送不进来。
- **`SingleInstance` 的 `onActivate` 签名**：`Action<WebviewWindow?>` → `Action<WebviewWindow?, IReadOnlyList<string>>`，第二实例的启动参数（`Oriel.CreateBuilder(args)` 的那份）随激活请求带回，用于"用命令行让已运行的程序打开某个页面"。

### 修复

- **[反馈 P1-6] `UseJsonContext` 的边界写清**：只有 POCO 参数/返回值真正需要它；命令用 `JsonElement` 收/发时生成器原样透传 payload，不必把 DTO 搬进上下文。
- **[反馈 P1-2 / P1-4] 两处已知取舍/边界写进 API.md**：Windows 边缘调整大小的厚度与生效条件（系统默认值、`Frameless && Resizable`、全屏/最大化不生效）；托盘气泡（`NIF_INFO`）**不做**的理由与后果（toast 可被"专注助手/权限"关掉，而"关闭即入托"的场景需要可见反馈）。
- **[反馈 P2-1] 内嵌资源名不再混用分隔符**：包内 `targets` 把 `%(RecursiveDir)` 归一成 `/`（Windows 上它带反斜杠，于是子目录资源名长成 `MyApp.wwwroot/assets\img\logo.svg` 这种混合形态）——资源名从此与 README 承诺的形状逐字符一致，`samples/OrielDemo` 的自声明写法同步，`tools/verify-pack.ps1` 新增两条**硬断言**（原始资源名不含反斜杠、自声明与零配置的原始形态逐字符一致）。此前那条比较会先把名字归一，等于放过了这个问题。
- **[反馈 P2-2 / P2-3 / P2-4 / P2-5] 文档补全**：`onCreated` 是配置期回调（访问 `App`/`Show` 会抛 `InvalidOperationException`，不是返回 null）；`win.` 只保留**命令名**段（事件名不受限）；13 个 `With*` 方法补 XML 文档，并说明 XML 文档包含 `internal` 成员；首帧闸门的推荐做法（`WithHidden` + 页面就绪消息 + 超时兜底）。

## 0.3.1 - 2026-10-08

第三轮全面评审后的修复批次。

### 新增

- **`OrielApp.IpcRejected` 事件**（`OrielIpcRejectedEventArgs`）：入站 IPC 被门禁拒绝时触发，带层级（`Source` / `Token` / `Command`）、原因、文档 URL 与命令名。Release 下拒绝原本只写 `Debug.WriteLine`，宿主无从观测；非 `invoke` 的消息被拒时页面也没有任何反馈，只有这个事件能看到。
- **源生成器编译期诊断 ORIELWEB101–106**：空白/含控制字符命令名、同程序集内同名命令、不可访问的命令方法、泛型宿主类、`ref`/`out` 参数此前要么被静默吞掉、要么变成生成文件里定位不了的 CS 错误，现在全部在编译期指回用户方法；`win.` 保留前缀给出警告（应用同名命令会被内建静默遮蔽）。
- **逐消息来源纵深加固**（IPC 安全第二层）：Windows 用 `WebMessageReceived.Source`、macOS 用 `frameInfo.securityOrigin`（10.15+，缺失时优雅回退）判定发消息文档的真实来源，顶级导航 URL 快照降级为兜底。Linux 的经典 script-message 信号不带 frame 信息，保持快照判定（已知差距，注入期自检与令牌两层不受影响）。
- Linux/macOS 的 async IPC 命令 `await` 之后**回 UI 线程**：与 Windows 语义一致，命令里 `await` 后可直接操作窗口与平台对象。

### 变更（破坏性 / 行为可见）

- **窗口尺寸统一为逻辑像素**：Windows 此前把 `WithSize`/`WithMinSize` 当物理像素用，高分屏（150%/200%）上窗口比 Linux/macOS 小一半/三分之一；现在按窗口 DPI 折算，三平台语义一致（高分屏用户会看到窗口变大）。
- **macOS `MoveTo` 改为左上角原点**：此前用的是 cocoa 左下角坐标，与 API.md 的屏幕坐标契约（和另外两个平台）相反；`At(x, y)` 同时补齐 Linux/macOS 支持（此前仅 Windows）。
- `publish.ps1 -Bundle` 明确为 **Windows 打包脚本**：非 `win-*` RID 直接报错并指路（macOS 走 `release.yml` / `tools/make-macos-app.sh`，Linux 走 `tools/wsl_publish.ps1`）。
- `AllowOrigin` 归一化：自动补尾斜杠；裸 host（漏 scheme）、带 query/fragment 的前缀直接报错——缺尾斜杠时 `http://localhost:5173` 会逐字节命中 `http://localhost:5173.evil.com/`，形似域名即可获得完整桥接与令牌。

### 修复

- **[审核发现] Windows 窗口位置语义**：`At`/`MoveTo` 与尺寸统一按窗口 DPI 折算（与 `Resize`、另两个平台一致）——此前只有尺寸折算，150%/200% 屏上位置会偏。
- **[审核发现] Windows 创建期失败的收尾**：`Create` 中段抛异常时不再二次释放根引用（此前会把真正的失败原因顶成 `InvalidOperationException`），也不再对"应用从没见过的窗口"触发 `Closed` 与窗口计数（此前计数会被打成负数并误发退出消息）。
- **[P3 批次] 生命周期收尾与一致性**：Linux `ExecuteScriptAsync` 的投递闭包补 `_webview == 0` 判断；Linux/macOS 的窗口计数改在建窗**成功之后**自增（此前 `Create` 抛异常会让计数虚高、把"全关退出"的时点推早）；Win32 销毁时清零 `GWLP_USERDATA`（不再靠 WndProc 的兜底 catch 过日子）；`WindowsPlatformBackend.Dispose` 清空静态 `s_current`；`GetMessageW` 失败时不再静默退出消息循环；MIME 表补 `.m4a/.m4v/.aac/.flac/.heic/.avifs`。
- **[P3 批次] 参数与协议容错**：`UseEmbeddedAssets` 的 host 做字符校验（此前含空格/斜杠时症状是白屏且宿主无异常）；`gtk_window_get_position` 的签名改回 GTK3 的 `void`；回执层对非整数 `id` 不再让异常逃逸（此前那条 eval 会干等到 30 秒超时）；包内 targets 的 wwwroot 判定改为大小写不敏感（目录写成 `WWWRoot` 时不再漏报）。
- **[P1] IPC 异常净化**：只有 `OrielIpcException` 的 `Message` 原样回传页面，其余异常回通用文案（此前与 API.md 承诺不符，非受控异常的内部路径可被页面探测）。
- **[P1] Windows 默认（framed）窗口**：`WM_NCCALCSIZE` 不再压掉非客户区——系统标题栏/边框与原生边缘 resize 恢复（此前默认路径窗口无标题栏且无法拖边调整大小）。
- **[P1] Linux/macOS `EvaluateJs` 挂死**：窗口销毁或页面导航离开原文档时，挂起的 ExecuteScript 立即以明确异常失败，不再永久挂起。
- **[P1] Linux oriel:// 回调**：trampoline 全身异常保护——托管异常逃逸 GLib 即进程 fail-fast，现在兜底为该次资源请求失败。
- **[P1] 类库 wwwroot**：引用类库的 wwwroot 现在与入口程序集合并提供（同一相对路径入口优先）；此前运行期只扫入口程序集，类库资源静默 404。
- **[P2]** macOS 资源应答不再每请求泄漏一个 `NSHTTPURLResponse`；Win32 `Create` 中段失败回收 HWND/GCHandle；两平台窗口销毁时释放保留的上下文菜单；Linux 销毁后 `_gtkWindow` 清零并全入口守卫（迟到调用 no-op 而非悬空指针）；macOS/Linux 全屏状态的原生路径回灌（绿钮/ESC/WM 快捷键进出全屏后 `ToggleFullscreen` 行为不再颠倒）；`WM_KILLFOCUS` 不再反向塞焦点（IME/caret 错乱风险）。
- **[P2]** `release.yml`：手动触发旁路版本闸的洞已封（publish job 仅对 tag ref 执行），加 `concurrency: release`；CI 补 linux-arm64 交叉编译。

## 0.3.0 - 2026-10-03

### 破坏性

- **`oriel://` 自定义 scheme 取代文件系统方案**：内嵌资源不再解压落盘，三平台统一经 `oriel://<host>/` 提供（只读介质/沙箱可用、无陈旧目录残留）。`https://<host>/…` 作为兼容别名仅影响入参解析。
- **删除不带 `LogicalName` 的 `EmbeddedResource` 旧写法**：那种资源名无法区分"目录"与"含点文件名"（`app.min.js` 被推成 `app/min.js` → 白屏），构建期报 `ORIELWEB001`；零配置自动内嵌继续可用，自声明必须带显式 `/` 分隔符的 `LogicalName`。

## 0.2.0 - 2026-10-02

### 破坏性

- **移除库内剪贴板 API**（`feat!` 提交 `d000e05`）：页面经 `navigator.clipboard` 自理，库不再包一层。

### 变更

- 文档收敛：README / API.md / ROADMAP 与代码树重新对齐，API.md 各章改为真实签名引用。

## 0.1.8 - 2026-10-02

- Windows 托盘与通知的修复批次（tag 存在的最近一个 0.1.x 稳定点）。

## 0.1.3 - 0.1.7（2026-09-30 → 10-02，无 tag，按提交区间近似）

- NuGet 上有这几个版本，但仓库里没有对应 tag；提交区间内的主题：CLI 撤销（`65f0d80`）、自研打包器替换前的过渡修复、多轮文档拉齐。逐版本差异已不可靠，不再展开——这也是本文件与发布纪律存在的原因。
