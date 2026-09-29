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

## 阶段 A —— 三平台一致性缺口（进行中）

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

| 项 | 做法要点 | 验证方式 |
|---|---|---|
| 前进 / 后退 / 刷新 + 可用性查询 | Windows：`GoBack`/`GoForward`/`Reload` + `CanGoBack`/`CanGoForward`；macOS：`goBack:`/`goForward:`/`reload` + `canGoBack`；Linux：`webkit_web_view_go_back/forward/reload` | demo 加按钮 + smoke 断言 URL 或标题变化 |
| 导航事件：开始 / 完成 / 失败（含错误信息） | 复用宿主→页面事件通道（Windows `__oriel:event`、Linux/macOS `_onEvent`），同时暴露公开 C# 事件 | bridge 单测 + 三平台 smoke（页面写标记、宿主断言收到） |
| 页面 console 转发 | Windows：WebView2 的 console 消息事件；macOS/Linux：注入 console hook 后走已有消息通道 | demo 打印 console → CI 断言宿主收到 |
| 公开的自定义事件发送 API | `EmitEvent(name, payload)`：payload 走 STJ 源生成上下文（AOT 安全），落到已有的 `_onEvent` | 单测 + bridge 测试 + smoke |
| 通用「页面 → 宿主」消息 | `oriel.postMessage(name, payload)` + C# `MessageReceived` 事件 | 同上 |

## 阶段 C —— 平台集成外壳

功能面照 Tauri，验证账按本仓库的约定记（见下）。

| 项 | 可机器判定？ |
|---|---|
| 剪贴板（文本 / HTML 读写） | ✅ CI 可测 |
| 单实例（第二实例激活首实例并退出） | ✅ 可测：起两个进程断言行为 |
| 系统主题检测（dark/light + 变更事件） | ✅ 可测：在 CI 里用系统设置造两种值 |
| 拖放（文件拖入 → 路径列表 + 事件） | ⚠️ 部分：逻辑可注入事件测，真实拖拽需真机 |
| 托盘 / 应用菜单 / 上下文菜单 / 全局快捷键 / 通知 / deep link / 开机自启 | ❌ 无头环境不可测 |

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
