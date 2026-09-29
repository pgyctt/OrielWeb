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
| Linux HiDPI 拖动偏移 | ⏳ 未开始 | 用 `gdk_window_get_scale_factor` 折算增量 | **需真实高 DPI 缩放环境** → 见待真机清单 |

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
- 步骤：1) 用 `--clipboard-selftest` 跑一次（它会写剪贴板）；2) 在别的应用里粘贴，确认拿到文本/富文本；
  3) 反过来在别的应用里复制带格式的内容，再用 `window.ClipboardHtml` 读。
- 预期：文本能互相粘贴；HTML 粘贴到富文本编辑器（Word / LibreOffice）应保留粗体等格式。
- 若不符：先看 `Win32Clipboard.BuildCfHtml` 的偏移是否正确（CF_HTML 的偏移按**字节**计），
  Linux 侧看 target 列表是否包含对方请求的类型。

#### 主题切换的实时性（未验证）
- 状态：`--theme-selftest` 验证了"读得到 + 判得对"，但**没验证**"在系统设置里切换后事件是否立刻到达"。
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

## 阶段 B —— 内容与 IPC 深度

应用价值最直接的一批，全部能在 bridge 单测与三平台 smoke 里断言。

| 项 | 状态 | 做法要点 | 验证结果 |
|---|---|---|---|
| 前进 / 后退 / 刷新 + 可用性查询 | ✅ 完成 | 三平台各自的原生历史：Windows `GoBack`/`GoForward`/`Reload` + `CanGoBack`/`CanGoForward`；macOS `goBack:`/`goForward:`/`reload` + `canGoBack`；Linux `webkit_web_view_go_back/forward/reload`（本轮新增 P/Invoke） | `--nav-selftest` 在 **Windows 与 Linux 两台真机**上跑通：跳转 → 后退 → 前进 → 刷新各一步，每一步的 URL 都符合预期 |
| 导航事件：开始 / 完成 / 失败（含错误信息） | ✅ 完成 | 公开 C# 事件 `NavigationStarting`/`NavigationCompleted`（带 `Success`/`Url`/`Error`）+ 页面事件 `navigation.starting`/`navigation.completed`。Windows 订阅包装层的 `NavigationStarting`；Linux 新增 `load-failed` 信号（GError.message 按结构偏移取）；macOS 新增 `didStartProvisionalNavigation:` 与 NSError 解析 | 同上；失败分支**逐个尝试多个候选的不可达目标**（同源缺失页 / 环回端口 / 保留 TLD 域名，每个 15 秒），任一真的上报失败即通过——因为三个引擎各自接受的形态不同、且会静默改写不接受的形态（完整对照表见 DECISIONS）。三平台均通过（CI run #18）；并确认 Linux 上错误页那一次 `FINISHED` 不会重复上报"成功" |
| 页面 console 转发 | ✅ 完成 | **三平台共用一份 hook**：实现写在桥接模板里，注入时由 `__ORIEL_CONSOLE_ENABLED__` 决定是否执行（默认关闭），C# 侧不再抄一份 | bridge 单测（3 平台 × 2 用例：开启时投递且原 console 方法仍被调用 / 未开启时不投递）+ `--ipc-selftest` 真机验证 |
| 公开的自定义事件发送 API | ✅ 完成 | `EmitEvent(name, jsonPayload)` 与 `EmitEvent<T>(name, payload, JsonTypeInfo<T>)`（后者走源生成上下文，AOT 安全）→ 落到已有的 `_onEvent` | `--ipc-selftest` 的闭环：宿主 EmitEvent → 页面 `oriel.on` 收到 → 页面回 postMessage → 宿主收回 |
| 通用「页面 → 宿主」消息 | ✅ 完成 | `oriel.postMessage(name, payload)` + C# `MessageReceived`（payload 以原始 JSON 文本给出，反序列化由调用方决定） | 同上闭环 + bridge 单测（协议形状、未给 payload、空 name 校验） |
| 跨 `await` 回 UI 线程 | ✅ 完成（实现中发现） | `WebviewWindow.PostToUiThread(Action)`。做自检时踩到：`await` 续体在线程池上调 GTK 会直接 abort，而库此前没有提供回 UI 线程的手段 | 两个自检都依赖它；真实崩溃与修法见 DECISIONS |

## 阶段 C —— 平台集成外壳

功能面照 Tauri，验证账按本仓库的约定记（见下）。

| 项 | 状态 | 做法要点 | 验证结果 |
|---|---|---|---|
| 剪贴板（文本 / HTML 读写） | ✅ 完成 | Windows `CF_UNICODETEXT` + `HTML Format`（CF_HTML，偏移按字节）；macOS `NSPasteboard`；Linux `gtk_clipboard_*`（HTML 走自定义 target）。三平台写 HTML 时都带**纯文本回退** | `--clipboard-selftest` 三平台 CI 通过；**跨进程互操作**只在同进程内验证过，见待真机清单 |
| 单实例（第二实例激活首实例并退出） | ✅ 完成 | `SingleInstance(id, onActivate)`：**独占文件锁**判定 + 命名管道通知；第二个实例通知后立即以退出码 0 退出 | 三平台 CI 的双进程断言通过（第二个 0 秒退出并通知、第一个收到激活） |
| 系统主题检测（dark/light + 变更事件） | ✅ 完成 | `OrielApp.Theme` + `ThemeChanged` + 页面 `theme.changed`（每次导航后补推）。检测：注册表 + `WM_SETTINGCHANGE` / GtkSettings + `notify::` / `NSUserDefaults` + 系统通知 | `--theme-selftest` 三平台通过；Linux 另跑两次（`GTK_THEME` 造值）断言深浅结论**不同**；**切换实时性**见待真机清单 |
| 拖放（文件拖入 → 路径列表 + 事件） | ⏳ 未开始 | — | 逻辑可注入事件测，真实拖拽需真机 |
| 托盘 / 应用菜单 / 上下文菜单 / 全局快捷键 / 通知 / deep link / 开机自启 | ❌ 无头环境不可测 | — | 只做实现并在 README 标注「未验证（无真机环境）」 |

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
