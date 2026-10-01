# OrielWeb 路线图

> 与 `docs/DECISIONS.md` 的分工：DECISIONS 记录**已发生**的取舍与实测结论；本文件记录**要做**的能力、
> 顺序与验证方式。新能力落地后，其取舍与实测结果仍写进 DECISIONS，本文件只保留"尚未完成"的部分。

## 定位与参照

OrielWeb 是"跨平台系统 webview 核心库"：纯 C# P/Invoke、无 C++ 中间层、Native AOT 友好、零反射 IPC。
三平台（Windows/WebView2、Linux/WebKitGTK 4.1、macOS/WKWebView）均已真机跑通。

后续能力面参照三个项目，各自可取之处：

| 参照 | 值得拿的东西 |
|---|---|
| **AOTrino**（技术路线最接近：.NET 10 Native AOT + WebView2 单 exe，Windows-only） | `dotnet new` 模板；包内 build logic（`WebRoot\dist` 嵌入、自动 `npm build`、默认 DPI manifest）；npm 客户端（类型化桥接）；**运行时注入的同步 API**（拖动要在 `mousedown` 里同步判断双击，不能 await） |
| **Tauri 2** | `capabilities/*.json` 权限文件与按命令授权；插件化（tray/menu/updater 均以插件形态提供）；托盘/菜单/updater/CLI 的**功能面** |
| **Ryn** | 源生成 IPC（本库已有）、自定义 scheme、**能力安全模型**、AOT CI |

## 贯穿全程的工程约定

1. **三平台同时做**：新能力必须在三个后端一次落地。历史上"部分有"的项（DevTools、窗口图标、隐藏启动）
   就是这样攒出来的技术债，本路线图阶段 A 的一部分工作就是还这笔债。
2. **无法验证的必须显式标注**：仓库吃过"没验证就以为对"的亏（macOS 后端四个缺陷一次暴露），
   因此凡是没有机器断言或人眼证据的改动，一律在 README 标「未验证」并在本文件留「待真机验证清单」。
3. **收尾口径**：编译 0 警告 0 错误 + 单测 + 桥接测试 + 三平台 smoke 全绿。
4. **优先复用已打通的通道**：宿主→页面事件、页面回环消息、源生成 IPC，不新造轮子。

## 阶段 A —— 三平台一致性缺口（已完成）

还清"部分有"的技术债，为后续改动铺好平台一致性。**这一批最便宜、且基本都能在现有环境验证**。

| 项 | 状态 | 做法要点 | 验证结果 |
|---|---|---|---|
| Linux 隐藏启动 | ✅ 已完成 | `show_all` 后立刻 `hide`（GTK 的 realize 自上而下，不 show 则 webview 不被 realize，页面可能推迟到可见才开始加载） | WSL 实测：`--hidden` 下 `Map State: IsUnMapped`，同时 `WebKitWebProcess` 照常出现（页面照常加载） |
| macOS/Linux DevTools | ⚠️ 已实现未验证 | macOS：`WKWebView.isInspectable`（13.3+，先 `respondsToSelector:` 探测）；Linux：`WebKitSettings.enable_developer_extras` | CI 三平台冒烟全绿（run 13 / `265c9f0`），只证明开关不影响启动；**面板本身需人眼**（见待真机清单） |
| 窗口图标（统一 `WithIcon`） | ✅ Linux 已验证；Windows/macOS 未验证 | 各平台落到真正有图标槽的位置：Linux 窗口图标（`gtk_window_set_icon_from_file`）、Windows `WM_SETICON` 覆盖 exe 图标、macOS Dock 图标 | WSL 对照实测：带 `--icon` 时 `_NET_WM_ICON` 出现 `Icon (48 x 48)`，不带时 `not found`。CI 三平台冒烟全绿（run 13），但那只证明该代码路径不破坏启动——**图标是否真的显示出来仍需人眼**（见待真机清单） |
| Linux HiDPI 拖动偏移 | ⏳ 未开始 | 用 `gdk_window_get_scale_factor` 折算增量（只针对 X11 那条流式路径；Wayland 下的拖动已交给合成器，不存在宿主侧折算） | **需真实高 DPI 缩放环境** → 见待真机清单 |

**已定**：图标来源用显式的 `OrielWindowOptions.WithIcon(path)`——不做"从 exe 取图标"的跨平台抽象，
因为 macOS 的 `NSWindow` 根本没有窗口级图标槽（它设的是 Dock 图标），硬凑一个统一语义只会在某个平台上失真。

### 待真机验证清单（阶段 A）

#### Linux 隐藏启动
- 状态：**已在 WSL 验证**（`--app-arg --hidden` → `Map State: IsUnMapped`，同时 `WebKitWebProcess` 照常出现），
  不再是待验证项。

#### DevTools 开关（macOS / Linux）
- 状态：**已实现未验证**（无头环境看不到 Inspector 面板；机器侧只能证明"不崩、页面照常加载"）。
- 环境：有图形会话的 macOS（Safari 的「开发」菜单）或带桌面的 Linux（WebKitGTK 右键菜单）。
- 步骤：1) 以 `Debug = true` 启动（`samples/OrielDemo` 默认 `.UseDebug()`）；
  2) macOS：Safari → 开发 → 选中该进程的 webview；Linux：在页面内右键 → 「检查元素」。
- 预期：能打开 Web Inspector；把 `Debug` 置 false 后同一入口不再出现。
- 若不符：macOS 先确认系统 ≥ 13.3（`isInspectable` 是 13.3+ 的公开 API，更早的系统上代码会因
  `respondsToSelector:` 探测失败而跳过设置，此时依赖 Safari 的默认行为）；Linux 看
  `LinuxWindowHost.Create` 里 `webkit_settings_set_enable_developer_extras` 是否被调用。

#### 剪贴板的跨进程互操作（未验证）
- 状态：自检只验证了**同进程内**写→读回。与其它应用互相粘贴（真实剪贴板互操作）**未验证**。
- 环境：任意桌面环境。
- 步骤：1) 用 `--selftest clipboard` 跑一次（它会写剪贴板）；2) 在别的应用里粘贴，确认拿到文本/富文本；
  3) 反过来在别的应用里复制带格式的内容，再用 `window.ClipboardHtml` 读。
- 预期：文本能互相粘贴；HTML 粘贴到富文本编辑器（Word / LibreOffice）应保留粗体等格式。
- 若不符：先看 `Win32Clipboard.BuildCfHtml` 的偏移是否正确（CF_HTML 的偏移按**字节**计），
  Linux 侧看 target 列表是否包含对方请求的类型。

#### 主题切换的实时性（未验证）
- 状态：`--selftest theme` 验证了"读得到 + 判得对"，但**没验证**"在系统设置里切换后事件是否立刻到达"。
- 环境：任意桌面环境（Linux 需要桌面环境而不是 xvfb；macOS / Windows 均可）。
- 步骤：1) 启动 demo（不带自检参数）；2) 在系统设置里切换深色/浅色；3) 观察页面是否跟随
  （`app.js` 会把主题写到 `<html data-theme>`）。
- 预期：切换后一秒内页面主题跟随；宿主侧 `ThemeChanged` 触发一次。
- 若不符：Windows 看是否有 `WM_SETTINGCHANGE` + `"ImmersiveColorSet"`；Linux 看 GtkSettings 的
  `notify::` 信号是否被触发；macOS 看通知是否到达（`orielThemeChanged:`）。

#### 窗口图标（Windows / macOS）
- 状态：**Linux 已验证**（上面的 `_NET_WM_ICON` 对照实验）；Windows 与 macOS **只做了编译验证**。
- 环境：Windows 桌面 / macOS 桌面。
- 步骤：1) 用 `--icon <png|ico 路径>` 启动 `samples/OrielDemo`（或代码里 `.WithIcon(path)`）；
  2) Windows：看任务栏按钮与 Alt-Tab 缩略图；macOS：看 Dock 图标。
- 预期：显示为指定图片；不传 `--icon` 时 Windows 仍显示 exe 图标、macOS 仍显示 .app bundle 图标。
- 若不符：Windows 看 `LoadImageW` 是否返回 0（路径不存在或格式不支持时会静默保持原图标）；
  macOS 看 `NSImage` 是否构造成功（`initWithContentsOfFile:` 返回 0 时跳过设置，不报错）。

#### macOS 隐藏启动
- 状态：**已实现未验证**（Linux 侧已在 WSL 验证；macOS 的 `Hidden` 走"不 orderFront"分支，
  需要通过 `CGWindowList` 断言窗口不在 on-screen 列表里才算验证）。

#### macOS 内嵌资源虚拟主机 URL（2026-09-30 修复后待验证）
- 状态：**已修复未验证**。Linux 侧已在 WSL2 + WSLg 真机确认（默认模式的手动验证台完整渲染）；
  macOS 与它**同一根因、同一修法**（`AssetUrlResolver` + `loadFileURL:allowingReadAccessToURL:`），
  但尚未在真机跑过。根因与修法见 `docs/DECISIONS.md` 的「内嵌资源虚拟主机 URL 在 Linux/macOS 上白屏」。
- 环境：macOS 桌面。
- 步骤：直接跑 `samples/OrielDemo`（默认模式即加载 `https://app.oriel/manual-check.html`）。
- 预期：手动验证台完整渲染——标题、环境面板（平台 darwin）、托盘与通知按钮、右下角徽章「已连接」。
- 若不符：先看窗口标题是否停在 `WithTitle` 设的「Oriel Demo — 手动验证」（那说明页面 `<title>` 没生效、
  页面根本没加载），再确认解压目录里 `manual-check.html` 存在，最后看 `Navigate()` 里
  `AssetUrlResolver` 是否返回了 null（返回 null 就会退回 `loadRequest:`，重新撞上 DNS 失败）。

## 阶段 B —— 内容与 IPC 深度

应用价值最直接的一批，全部能在 bridge 单测与三平台 smoke 里断言。

| 项 | 状态 | 做法要点 | 验证结果 |
|---|---|---|---|
| 前进 / 后退 / 刷新 + 可用性查询 | ✅ 完成 | 三平台各自的原生历史：Windows `GoBack`/`GoForward`/`Reload` + `CanGoBack`/`CanGoForward`；macOS `goBack:`/`goForward:`/`reload` + `canGoBack`；Linux `webkit_web_view_go_back/forward/reload`（本轮新增 P/Invoke） | `--selftest nav` 在 **Windows 与 Linux 两台真机**上跑通：跳转 → 后退 → 前进 → 刷新各一步，每一步的 URL 都符合预期 |
| 导航事件：开始 / 完成 / 失败（含错误信息） | ✅ 完成 | 公开 C# 事件 `NavigationStarting`/`NavigationCompleted`（带 `Success`/`Url`/`Error`）+ 页面事件 `navigation.starting`/`navigation.completed`。Windows 订阅包装层的 `NavigationStarting`；Linux 新增 `load-failed` 信号（GError.message 按结构偏移取）；macOS 新增 `didStartProvisionalNavigation:` 与 NSError 解析 | 同上；失败分支**逐个尝试多个候选的不可达目标**（同源缺失页 / 环回端口 / 保留 TLD 域名，每个 15 秒），任一真的上报失败即通过——因为三个引擎各自接受的形态不同、且会静默改写不接受的形态（完整对照表见 DECISIONS）。三平台均通过（CI run #18）；并确认 Linux 上错误页那一次 `FINISHED` 不会重复上报"成功" |
| 页面 console 转发 | ✅ 完成 | **三平台共用一份 hook**：实现写在桥接模板里，注入时由 `__ORIEL_CONSOLE_ENABLED__` 决定是否执行（默认关闭），C# 侧不再抄一份 | bridge 单测（3 平台 × 2 用例：开启时投递且原 console 方法仍被调用 / 未开启时不投递）+ `--selftest ipc` 真机验证 |
| 公开的自定义事件发送 API | ✅ 完成 | `EmitEvent(name, jsonPayload)` 与 `EmitEvent<T>(name, payload, JsonTypeInfo<T>)`（后者走源生成上下文，AOT 安全）→ 落到已有的 `_onEvent` | `--selftest ipc` 的闭环：宿主 EmitEvent → 页面 `oriel.on` 收到 → 页面回 postMessage → 宿主收回 |
| 通用「页面 → 宿主」消息 | ✅ 完成 | `oriel.postMessage(name, payload)` + C# `MessageReceived`（payload 以原始 JSON 文本给出，反序列化由调用方决定） | 同上闭环 + bridge 单测（协议形状、未给 payload、空 name 校验） |
| 跨 `await` 回 UI 线程 | ✅ 完成（实现中发现） | `WebviewWindow.PostToUiThread(Action)`。做自检时踩到：`await` 续体在线程池上调 GTK 会直接 abort，而库此前没有提供回 UI 线程的手段 | 两个自检都依赖它；真实崩溃与修法见 DECISIONS |

## 阶段 C —— 平台集成外壳

功能面照 Tauri，验证账按本仓库的约定记（见下）。

| 项 | 状态 | 做法要点 | 验证结果 |
|---|---|---|---|
| 剪贴板（文本 / HTML 读写） | ✅ 完成 | Windows `CF_UNICODETEXT` + `HTML Format`（CF_HTML，偏移按字节）；macOS `NSPasteboard`；Linux `gtk_clipboard_*`（HTML 走自定义 target）。三平台写 HTML 时都带**纯文本回退** | `--selftest clipboard` 三平台 CI 通过；**跨进程互操作**只在同进程内验证过，见待真机清单 |
| 单实例（第二实例激活首实例并退出） | ✅ 完成 | `SingleInstance(id, onActivate)`：**独占文件锁**判定 + 命名管道通知；第二个实例通知后立即以退出码 0 退出 | 三平台 CI 的双进程断言通过（第二个 0 秒退出并通知、第一个收到激活） |
| 系统主题检测（dark/light + 变更事件） | ✅ 完成 | `OrielApp.Theme` + `ThemeChanged` + 页面 `theme.changed`（每次导航后补推）。检测：注册表 + `WM_SETTINGCHANGE` / GtkSettings + `notify::` / `NSUserDefaults` + 系统通知 | `--selftest theme` 三平台通过；Linux 另跑两次（`GTK_THEME` 造值）断言深浅结论**不同**；**切换实时性**见待真机清单 |
| 拖放（文件拖入 → 路径列表 + 事件） | ✅ 已实现 | Windows：窗口加 `WS_EX_ACCEPTFILES` → `WM_DROPFILES` → `DragQueryFileW`（本库是 Composition 宿主，WebView2 **不是子窗口**，拖放会落到本窗口——不必手写 OLE `IDropTarget`、不必 OLE 初始化），并关掉 WebView2 的 `AllowExternalDrop` 以免它截走拖放；macOS：自定义 `NSView` 子类承载 `NSDraggingDestination`，webview 作为其子视图（**不碰 `WKWebView` 的方法表**，AppKit 沿父视图链查找落点）；Linux：`gtk_drag_dest_set(webview, "text/uri-list")` + `drag-data-received` | **23 个用例**覆盖 URI→本地路径（百分号编码含非 ASCII、`+` 不等于空格、Windows 盘符、`localhost` 与远程主机、非 file 协议、批量保序）；`--selftest shell` 走完注册路径并断言事件订阅可用（`FILE-DROP-SUBSCRIBED`）。**真实拖拽整体未验证**——无头环境造不出 XDND/OLE 会话，见下 |
| 系统托盘（`AddTray` / `app.Tray`） | ✅ 已实现；Linux 取证通过 | Windows `Shell_NotifyIconW` + `TrackPopupMenuEx`；macOS `NSStatusBar`/`NSMenu`；Linux GTK3 `GtkStatusIcon`/`GtkMenu`（用 GTK 自带而非 AppIndicator，理由见 DECISIONS） | Windows/macOS 编译验证；Linux 由 `--selftest shell` 断言"托盘创建 + 一份含分隔线/勾选/禁用/子菜单/role 的菜单能设进去、进程不崩"，并由 `tools/verify-linux-shell.sh` 采集证据。**图标可见性需人眼**（见下） |
| 系统通知（`ShowNotification`） | ✅ 已实现；Linux 硬断言 | Windows 经一个短命的 **Windows PowerShell 5.1** 进程调 WinRT `ToastNotificationManager`（Native AOT 下没有 WinRT 投影；**不依赖托盘是否存在**——早先的托盘气球方案已废弃，理由见 `Win32ToastNotification` 的类注释）；macOS `osascript`；Linux `notify-send`。**应用标识**（Windows AUMID / Linux `--app-name`）默认取入口程序集名，可用 `UseNotificationAppId` 覆盖——早先硬编码为 `OrielWeb`，导致所有基于本库的应用在系统通知设置里同名、既分不清也关不掉某一个 | Linux：真 `notify-send` → 会话总线 → 假通知服务，断言**标题与正文逐字符正确**（`tools/verify-linux-shell.sh`）。点击上报**三平台均不支持**，三处都是**显式空实现**（不是"忘触发"） |
| 对话框（消息框 / 打开 / 保存 / 选文件夹） | ✅ 已实现（扩展版） | 打开可多选、可给结构化过滤器、可指定初始目录；新增文件夹选择。Windows `GetOpenFileNameW`（`OFN_ALLOWMULTISELECT`+`OFN_EXPLORER`）/ `SHBrowseForFolderW`；macOS `NSOpenPanel`（`URLs` 数组）/ `NSSavePanel`；Linux `GtkFileChooserDialog`（多选读 `GSList`）。过滤器在三种平台形状间的转换与 Win32 多选缓冲区的解析都在纯函数里（`OrielFileFilter` / `OrielFileDialogSupport`） | **30 个单测**（在 Linux CI 上跑，含为 Windows 写的用例）：解析旧字符串、Win32 双 null 渲染、GTK 的 `*.*` 归一、Cocoa 扩展名提取、Win32 多选「单段 vs 多段」两种形状、补扩展名。**对话框外观与交互需人眼**（见下） |
| 内建右键菜单策略 | ✅ 已实现；Linux 接管链路已实测 | 默认 `Editing`（只留剪切/复制/粘贴）、`Native`（平台原样）、`Disabled`。**Windows 是过滤式**：订阅 `CoreWebView2.ContextMenuRequested` 按 `Name` 保留三项（`ICoreWebView2_11`，由 WebView2Aot 包内部转换）。**macOS / Linux 是接管式**：自己弹只含三项的菜单，编辑命令走引擎的原生通道（macOS `sendAction:to:from:` + `cut:/copy:/paste:`；Linux `webkit_web_view_execute_editing_command`）。早先"就地增删引擎菜单"的写法在 WebKitGTK 4.1 上会破坏内存，理由与证据见 DECISIONS | **37 个单测**（Linux CI 上跑）：接管菜单的三项与 role 归类边界、Windows 保留名单（`copyImage`/`copyLink` 这类陷阱）。Linux 另**实测**：连点右键 6 次不崩、菜单按指针位置弹出、`"Copy"` 把页面选中内容送进系统剪贴板。**macOS 全部未验证**；**菜单外观与真机整链路交互需人眼**（见下） |
| 窗口上下文菜单 | ✅ 已实现 | 三平台都支持（Windows 阻塞、另两个异步）。菜单构建按平台抽成共享类（`Win32Menu`/`GtkMenu`/`MacOSMenu`），role 由 `OrielMenuRoles` 统一解释 | 三平台编译 ✓；Linux `--selftest shell` 走一遍菜单构建并断言不崩。**菜单外观与上下文菜单的弹出交互需人眼**（见下） |
| 开机自启 | ✅ 已实现 | Windows 写 HKCU 的 Run 键、macOS 写 LaunchAgent plist（不调 `launchctl load`，避免立刻再拉起一个实例）、Linux 写 freedesktop 的 autostart `.desktop`。配置文本由共用的纯函数生成 | **B 批里最硬的一条**：取证脚本断言"启用 → 查得到 → 禁用 → 查不到"的闭环，并逐项核对 `.desktop` 的内容（Desktop Entry 头、带引号的 Exec、参数、GNOME 启用标志）；另有 12 个单测覆盖三段文本。**"下次开机真的起来了"仍需真机重启** |
| Shell（打开外链 / 在文件管理器里显示） | ✅ 已实现 | 用系统默认程序打开 URL 与文件、在文件管理器里显示；**默认拒绝式的 scheme 白名单**（只放 http/https/mailto）。**不含** Ryn 的 `shell.execute`/PTY——那属能力沙箱范畴 | 取证脚本用 `xdg-open` 替身断言两点：URL 真的交出去了、危险目标一次都没调出去；24 个单测覆盖校验与三平台命令翻译 |

### 托盘与通知的待真机清单

#### 托盘图标的可见性
- 环境：有托盘区的桌面（Windows 任务栏；macOS 菜单栏；Linux 用 Xfce/KDE，或装了 AppIndicator 扩展的 GNOME）。
- 步骤：1) `OrielDemo --selftest shell`（会建托盘并设一份含分隔线/勾选/禁用/子菜单的菜单）；
  2) 在托盘区找到图标，点开菜单。
- 预期：图标出现、悬停显示 tooltip、菜单按设置渲染（禁用项是灰的、勾选项带勾、子菜单能展开）、
  点 Quit 项应用退出。
- 若不符：按平台看后端——Windows `Win32TrayBackend.Add()`（`Shell_NotifyIconW` 失败时只写调试输出）、
  Linux `GtkTrayBackend`（GNOME Shell 未装扩展、或 Wayland 会话下本就不显示，属平台事实）、
  macOS `MacOSTrayBackend`（没给图标时会显示占位字符 `●`）。

#### 上下文菜单的外观与交互
- 环境：三平台桌面各一次。
- 步骤：用 devtools 控制台执行 `oriel.invoke('win.contextMenu')` 看上下文菜单。
- 预期：菜单按设置渲染（子菜单可展开、勾选项带勾、禁用项是灰的、分隔线正确）；
  role 项里的 `Close` 能关掉窗口。
- 若不符：macOS 看 `MacOSMenu.Popup` 的"没有当前 NSEvent"日志（后台回调里调用时无法定位菜单）。

#### 通知的展示与点击
- 环境：三平台桌面各一次。
- 步骤：1) `OrielDemo --selftest shell`；2) 看通知横幅；3) Windows 上点一下横幅本体。
- 预期：横幅显示标题与正文；Windows 上点击后触发 `NotificationClicked`（回传 `Id`）。
- 若不符：Windows 看系统"专注助手/通知"设置里的开关；macOS 看"通知"权限
  （未打包运行时 `osascript` 的通知归属于 Script Editor，可能在系统设置里被静音）。

#### 对话框的外观与交互
- 环境：三平台桌面各一次（对话框弹出后无人操作会一直等，无头环境里跑不出来，因此不进自检）。
- 步骤：1) 在示例里调 `ShowOpenFileDialog`（开 `AllowMultiple`）、`ShowSaveFileDialog`、`ShowFolderDialog`；
  2) 看过滤器下拉、多选行为、初始目录、保存时是否补上扩展名。
- 预期：过滤器下拉显示传入的名称；多选后每一项都出现在返回数组里；
  保存时输入不带扩展名的名字，落盘文件名带 `DefaultExtension`。
- 若不符：按平台看后端——Windows `Win32WindowHost.ShowFileDialog`（多选返回值走
  `OrielFileDialogSupport.ParseWin32MultiSelect`，那里的单段/多段分支有单测；
  文件夹选择器**不支持初始目录**，属已知取舍）、Linux `LinuxWindowHost.ReadSelectedPaths`
  （`GSList` 遍历）、macOS `MacOSWindowHost.RunPanel`（读 `URLs` 数组；`allowedFileTypes` 已废弃但仍可用）。

#### 文件拖放（整体未验证）
- 状态：**落点注册路径已被自检走到**（窗口创建时不崩、事件可订阅），但"把文件真的拖进去"**完全未验证**。
  无头环境造不出拖放会话：Linux 侧是 XDND 协议交互、Windows 侧是 OLE 拖放会话，都不是"注入一个事件"能模拟的。
- 环境：三平台桌面各一次（Linux 需要真实桌面；xvfb 里没有可发起拖拽的文件管理器）。
- 步骤：1) 从文件管理器拖 **1 个文件**进窗口 → 看 `FileDropped` 是否触发、路径是否正确；
  2) 拖 **多个文件** → 看 `Paths` 是否齐全、顺序是否与拖入一致；
  3) 拖 **一个文件夹** → 看路径是否是目录本身（按设计目录也会出现在列表里）；
  4) 拖**一段选中的文本** → 应**不触发**（只受理文件 URL）。
- 预期：前 3 步都触发事件且路径逐字符正确；第 4 步不触发。
- 若不符：按平台但别急着改解析——先确认**落点是否被接管**：
  Windows 看 `AllowExternalDrop` 是否真的被设为 false（老 WebView2 运行时没有 `ICoreWebView2Controller4`，
  那时转换会跳过、拖放可能仍被 webview 截走）；macOS 看 `registerForDraggedTypes:` 是否收到了
  `public.file-url`（`performDragOperation:` 没被调用就是落点没命中）；Linux 看 `gtk_drag_dest_get_target_list`
  是否非 0。**URI 解析本身已有单测，若路径错了先怀疑落点而不是解析。**

#### 内建右键菜单（接管式）
- 环境：三平台桌面各一次。
- 步骤：1) 在操作台页面**普通区域**右键（默认策略）→ 菜单里应**只有**剪切/复制/粘贴；
  2) 在输入框里输入文字、选中、右键 → 这三项应当可用，且**真的能作用在选中内容上**
  （粘贴要能把系统剪贴板内容送进去）；3) 切到 `平台原样` 再右键 → 应恢复出「后退/刷新/另存为/检查元素」等；
  4) 切到 `完全不弹` 再右键 → 应当什么都不出现；
  5) **连点右键 5～6 次** → 进程不得崩溃（这一条是为 WebKitGTK 那处内存破坏加的回归项）。
- 预期：五步都符合。第 2、5 步是关键——前者验证"接管没把原生编辑行为弄坏"，后者是回归。
- 若不符：先看**策略是否生效**（操作台右上角日志会打印当前策略），再按平台看钩子是否被调用：
  Windows 看 WebView2 运行时版本是否够新（老运行时没有 `ICoreWebView2_11`，`ContextMenuRequested` 不会来）；
  macOS 看 `setUIDelegate:` 是否设上（`willOpenMenu:` 未被调用就是委托没生效）、系统是否 ≥ 11；
  Linux 看 `context-menu` 信号是否连上（可用 `g_signal_lookup` 或直接看菜单是否还是原样的）。
  **判断名单本身已有单测，若菜单里留下了"刷新"之类先怀疑编号/名称映射之外的东西（策略、钩子）。**

### C 的验证账规则（必守）

每一项完成后**同时**做两件事：

1. 在 `README.md` 对应位置标注 **「未验证（无真机环境）」**，不留默认假设；
2. 在本文件追加一节「待真机验证清单」，格式：

   ```
   ### <能力名>（未验证）
   - 环境：<需要的系统/桌面环境>
   - 步骤：1) … 2) … 3) …
   - 预期：<具体现象>
   - 若不符：先看 <排查入口：哪个文件/哪个 os_log 关键字/哪个断言>
   ```

   有真机后逐项跑完，把结论升级为「已验证」并写进 `docs/DECISIONS.md`。

## 明确不在本路线图内

- **安全与能力模型**（`capabilities` 权限文件、按命令授权、CSP 处理、三平台统一的 scheme 读权限边界）：
  参照 Tauri/Ryn 的方向已记在案，但排在 A/B/C 之后，届时单独立项。
- **工具链与打包**（CLI、`dotnet new` 模板、前端构建集成、msi/dmg/AppImage 打包器、updater、
  代码签名与公证）：参照 AOTrino/Tauri 的方向已记在案，同样排在之后。
