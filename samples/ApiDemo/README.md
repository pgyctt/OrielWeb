# ApiDemo — 已实现 API 的主验证台

对标 Tauri 的 [`examples/api`](https://github.com/tauri-apps/tauri/tree/dev/examples/api)：把本库**已经实现**的
页面 API 逐个做成可点的入口。它的作用不是"再写一遍文档"，而是回答两个具体问题：

1. 这个函数**真的能跑**吗（不是"类型里有"）？
2. 它在**这台机器**上给出的回执是什么（成功、`not_supported`、还是环境限制）？

## 与 OrielDemo 的分工

| 工程 | 谁在跑 | 跑什么 |
|---|---|---|
| `samples/OrielDemo` | CI（无人值守） | `--selftest` 契约自检、冒烟、打包验证；页面只有 Todo 与手动操作台 |
| `samples/ApiDemo`（本工程） | 人（也可以被 CDP 脚本驱动） | 按模块列出每个已实现的方法，点了就看回执 |

两者都不进 NuGet 包。ApiDemo 不替代自检——它没有断言，只有"看得见的回执"。

## 跑

```bash
dotnet run --project samples/ApiDemo/ApiDemo.csproj
```

启动后侧栏列出 17 个模块（动作数实时计算，当前 300+ 条）。几点副作用要先知道：

- 首次启动会在 `%APPDATA%/com.orielweb.samples.apidemo/sandbox` 写演示文件；
- 通知面板会**真的发系统通知**，自启面板会**真的改系统状态**；
- 对话框、菜单、托盘、外部打开都需要人点，自动化脚本不该碰。

## 结构

```
ApiDemo/
  Program.cs             宿主装配：能力、fs/asset 两条 scope、demo.* 诊断命令
  wwwroot/
    index.html           壳（侧栏 + 面板 + 底部日志）
    app.js               路由、覆盖统计、日志
    ui.js                声明式渲染：一条 API 一行（名字 / 参数 / 运行 / 回执）
    app.css
    panes/
      common.js          沙箱目录、内联 PNG（不依赖 executableDir）
      coverage.js        覆盖总览（实时统计 + 刻意没搬的清单）
      core.js app.js window.js webviewWindow.js event.js path.js fs.js
      dialog.js clipboard.js opener.js notification.js autostart.js
      menu.js tray.js image.js dpi.js dragdrop.js
```

**新增一个 API** = 在对应 `panes/*.js` 的 `groups[].items` 里加一行：

```js
{ name: "someMethod(arg)", note: "它做什么、有什么平台差异", run: () => win.someMethod("x") }
```

`node` 形态留给不方便压成一行按钮的东西（进度条、拖放区）。侧栏数字与覆盖页都从这份声明里算，
不需要另维护一张清单。

## 怎么读回执

| 回执 | 含义 |
|---|---|
| 绿色 | 调用成功，后面是返回值与耗时 |
| 红色 | 调用失败，带 `message` 与可选的 `[code=…]` / `data=…` |
| 标「预期失败」 | 本来就是用来展示错误形状的（未知命令、缺参数、类型不符、被 Deny、超时、越界读） |
| 标「平台差异」 | 换个平台会 `not_supported`；本库宁可报错也不静默忽略 |
| 标「自动复原」 | 会改变窗口状态的操作在几秒后自己恢复，否则验证台会把自己禁用掉 |

## 自动化验证（做过什么）

用 WebView2 的远程调试端口 + CDP 逐条点击。脚本在 [`tools/verify-api-demo.mjs`](../../tools/verify-api-demo.mjs)：

```bash
# 1) 带调试端口启动（端口号自选）
WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS="--remote-debugging-port=9333" \
  samples/ApiDemo/bin/Debug/net10.0/ApiDemo.exe

# 2) 另一个终端里点
node tools/verify-api-demo.mjs --panes core,path,dpi,image,event,fs,window
node tools/verify-api-demo.mjs --panes app --skip "exit|hide"
```

它会绕过 `close()` / `destroy()` / `startDragging`，其它按钮逐个点、等回执、汇总。
「预期失败」与 `code=not_supported` 不算回归；`code=internal` 算——那可能是环境限制，
但应当由人判一次，而不是让脚本替它开脱。

本轮实测（Windows 11 / .NET 10）：

| 面板 | 结果 |
|---|---|
| `core` `path` `dpi` `image` `event` `fs` `window` | 首轮 199 条，暴露两个问题（`Monitor` 缺陷、fs 重命名不可重复执行）；修复后重跑相关面板 141 条，剩余失败全是「预期失败」或平台差异 |
| `tray` `webviewWindow` `fs`（第二轮） `image` | 56 条，唯一失败是预期的越界读 |
| `app`（跳过 `exit` / `hide`） | 19 条，4 条 `not_supported`（Dock、数据存储标识符——macOS 专属） |

其中 fs 的「重命名不可重复」是**示例自己的问题**（目标已存在时第二次会报 `AlreadyExists`），
已在动作里加"目标存在先删除"，让它可重复执行；`Monitor` 那条是库的缺陷，见下。

剩余的失败按原因归类：

- `executableDir` / `fontDir` / `runtimeDir`：Windows 上按设计不存在（与 Tauri 一致）；
- `setVisibleOnAllWorkspaces` / `setTitleBarStyle` / `setBadgeCount` / `setBadgeLabel`：Windows 没有对应概念；
- `setProgressBar` / `setOverlayIcon`：本会话里没有 `ITaskbarList3`（Explorer 任务栏不可用），属于环境限制；
- `setEffects`：Mica/Acrylic 需要 Windows 11 22H2+，本机返回 `0x80070057`；
- `setDockVisibility` / 数据存储标识符：macOS 专属；
- `readTextFile('C:/Windows/win.ini')`：命令已授权但路径不在 scope 内，`path_not_allowed`。

**这轮点击抓到并修掉了一个库缺陷**：`primaryMonitor()` / `currentMonitor()` / `availableMonitors()`
返回的 `Monitor.position` 与 `Monitor.size` 字段全是 `undefined`。原因是宿主从 B14 起给的是
Tauri 2 的外部标记线格式 `{ Physical: { x, y } }`，而 `toMonitor()` 还在读旧的内联形状
`raw.position.x`；单测夹具也停在旧形状，所以一直是绿的。修复在
`packages/api/src/window.js`（改走 `normalizePosition` / `normalizeSize`），夹具改成真实线格式
并补了一条"旧形状仍可读"的回归用例。

## 需要人工点的面板

`dialog`（模态）、`clipboard`（会覆盖系统剪贴板）、`opener`（拉起浏览器/文件管理器）、
`notification`（系统通知）、`autostart`（改注册表/LaunchAgent）、`menu`（Windows 上 `popup`
阻塞主线程）、`dragdrop`（需要真实拖放）。这些不是没实现，是**不该被无人脚本点**。

`menu` 的构造、容器操作、`items()` 都已可点；`popup` 只负责发起，回执不等待用户选择——
可靠的信号是 `action` 回调（看底部日志）。

## 两个诊断命令

- **`demo.ready`**：页面加载完成后回一条 IPC，宿主在 stderr 打
  `[api-demo] page ready: 17 个模块 / N 个动作`。没有这一行 = 前端没起来，而不是窗口没建出来。
- **`demo.pageerror`**：`index.html` 里挂了 `error` / `unhandledrejection` 监听，
  未捕获错误会进 stderr（`[page:uncaught] …`）。二十来个 ES 模块，任何一处 import 拼错都会白屏，
  这两个通道是排查白屏的第一手证据。

## 能力配置

```csharp
c.Allow("*").Deny("demo.blocked");                                   // 验证台全开，只留一条 deny 演示拒绝形状
c.Allow("fs.*", s => s.AllowPath("$APPDATA/**"));                   // 文件读写 scope
c.Allow(OrielAssetFileServer.ScopeCommand, s => s.AllowPath("$APPDATA/**")); // convertFileSrc 的独立 scope
```

`$APPDATA` 是**本应用**的数据目录，不是整个 AppData。fs 与 asset 是两张独立的 scope：
"以程序方式读写文件"与"把文件显示出来"不是同一种意图（见 `docs/API.md` §11）。
