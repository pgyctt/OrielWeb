# OrielWeb 路线图

只记**还没做**的和**还没在真机上验过**的。已落地的能力、用法与取舍记录见 [API.md](API.md)。

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
- 现状：库不处理"新窗口请求"（WebView2 的 `NewWindowRequested`、WebKit 的 `createWebViewWithConfiguration:` / `new-window` 都没接），页面发起时由引擎按各自默认行为处置。
- 影响：前端若用 `target="_blank"` 打开内嵌资源页，行为**三平台不一致且未定义**；外链应当走 `app.OpenExternal`（受 API.md §7 的 scheme 白名单约束）。
- 最小做法：把新窗口请求路由到系统浏览器，或忽略并上报一个事件——二选一都要先定策略，所以先记在这里。

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

#### macOS 隐藏启动
- 环境：macOS 桌面。
- 步骤：`OrielDemo --hidden`，用 `CGWindowList` 断言窗口不在 on-screen 列表里。
- 预期：窗口不上屏，但页面照常加载、IPC 照常往返（Linux 侧已在 WSL 验证过同样的语义）。

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
