# 跨平台真机验证（Hyper-V 实验室）

> 目的不是"再跑一遍 CI"，而是跑 **CI 结构上覆盖不到的那一半**：不同发行版的库版本、
> 不同 CPU 架构、不同的桌面会话类型（X11 / Wayland / 无会话）。
> 结论先给：这一轮抓到 **4 个真问题**，其中 2 个是**进程级崩溃**，1 个是"应用在 Ubuntu 22.04
> 上根本起不来"。三个都在 CI 上永远是绿的。

## 1. 环境

宿主 Windows 10（19044）+ Hyper-V 内部交换机 `vLab`（192.168.100.0/24），三台机器走 SSH
自动执行同一套 demo 的自检。工装见 `hyperv-vm/`（`test-lab.ps1` / `start-lab.ps1` /
`deploy-run.ps1`）。

| 机器 | 系统 | 架构 | 关键版本 | 图形会话 |
|---|---|---|---|---|
| 宿主 | Windows 10 19044 | x64 | WebView2 运行时 | 有（桌面） |
| `lab-ubuntu` | Ubuntu 22.04 LTS | x86_64 | 内核 6.8；**WebKitGTK 4.0**（`libwebkit2gtk-4.0.so.37`）；GTK 3 | **Wayland**（gnome-shell，另有 XWayland） |
| `lab-macos` | macOS 15.8.1 | **x86_64（Intel）** | 无 .NET、无 Xcode CLT | **无**（停在登录窗口，`console user: root`） |

三台都**没有装 .NET**：产物用 `dotnet publish -r <rid> --self-contained true -p:PublishAot=false`
从宿主交叉发布，再 `tar` 流式推过去（`deploy-run.ps1` 的做法）。AOT 需要目标平台工具链，
在 Windows 上给 Linux/macOS 做不出来，所以这一轮验的是"自包含非 AOT"，与 CI 的 AOT 是两条路径
——两者都验到，覆盖面才完整。

## 2. 结果

`--selftest <mode>` 的结论行（`<MODE>-SELFTEST: PASS`）与退出码：

| 平台 / 会话 | 跑的模式 | 结果 |
|---|---|---|
| Windows 10 x64（桌面） | 20 | **20/20 PASS** |
| Ubuntu 22.04 **X11**（`GDK_BACKEND=x11`） | 20 | **20/20 PASS** |
| Ubuntu 22.04 **Wayland** | 19 | **19/19 PASS** |
| macOS 15 **Intel，无图形会话** | 20 | **20/20 PASS** |

四组全绿，但**其中一部分是 SKIP**——下面第 4 节说清哪些断言在什么环境里被跳过、
为什么跳过不等于放过。

## 3. 找到并修掉的问题

### 3.1 Ubuntu 22.04 上应用**起不来**：只认 WebKitGTK 4.1

现象：建第一个窗口时

```
System.DllNotFoundException: Unable to load shared library 'libwebkit2gtk-4.1.so.0'
   at GtkNative.WebkitWebContextGetDefault()
   at LinuxAssetScheme.Register(...)
```

根因：WebKitGTK 在 2.40 把 GTK3 那一支的 soname 从 `libwebkit2gtk-4.0.so.37` 改成了
`libwebkit2gtk-4.1.so.0`，但**发行版不跟着改**——Ubuntu 22.04 至今仍以 4.0 这个名字发安全更新
（里面的 WebKit 是新的，只是文件名旧）。本库按 4.1 硬加载，于是在 22.04 上一行界面都出不来。

**为什么 CI 抓不到**：`ubuntu-latest` 是 24.04，只有 4.1。

修法：`Interop/LinuxLibraryResolver.cs` —— 用 `NativeLibrary.SetDllImportResolver` 把
"库名 → 句柄"的解释权拿回来，按 `4.1 → 4.0(.so.37) → 4.0(.so.0)` 依次尝试；
注册时机挂在 `GtkNative` 的静态构造函数上（`DllImport` 的解析发生在第一次调用时，
早一步晚一步都不行）。**换库是安全的**：本库用到的 32 个 `webkit_*` 与 1 个 `jsc_*` 符号
在 4.0 里逐个探过，一个不缺——两个名字背后是同一套 C API。

首版还踩了个坑：回退名写成 `libwebkit2gtk-4.0.so.0`（照 4.1 的 `.so.0` 类推），
真机上照样找不到——4.0 的 libtool 版本号是 `.so.37`（JSC 是 `.so.18`）。

### 3.2 Intel Mac 上建窗即崩：`NSRect` 的 ABI 不是一个架构的事

现象（macOS 15 / x86_64）：

```
*** Terminating app due to uncaught exception 'NSInternalInconsistencyException',
    reason: 'Invalid parameter not satisfying: CGRectContainsRect(..., frame).
             self=<NSWindow: …> frame={{1024, 0}, {nan, 0}}'
```

根因：`NSRect` 是 32 字节的 double 聚合。**arm64 把它当 HFA 放进 v0–v3，x86_64 的 SysV 把它
整个压到栈上**。我们此前把矩形拆成 4 个 `double` 参数——在 arm64 上"碰巧对"，
在 Intel 上 4 个 double 进了 xmm 寄存器、栈上是垃圾，AppKit 收到 `{nan, 0}` 的 frame。

**为什么 CI 抓不到**：`macos-latest` 是 arm64。

修法：引入 `ObjCRuntime.NSRect` 结构体与一组 `SendIdRect*` / `SendVoidRect*` 签名，
矩形一律走结构体（封送层按平台 ABI 处理）。受影响的共 5 处：`NSWindow` 建窗、
`WKWebView initWithFrame:configuration:`、`NSVisualEffectView initWithFrame:`、
两处 `setFrame:`、`setFrame:display:`（简化全屏 / 跨屏全屏）、`NSTrackingArea initWithRect:…`。
`SendIdDouble4` 保留给"四个**独立标量**"的 selector（`+[NSColor colorWithSRGBRed:…]`），
并在注释里写明这个区别。

> 顺带收获：修好之后 macOS 上 86 条窗口断言一次过——说明 arm64 那条路径的实现是好的，
> 坏的只有 ABI 声明。

### 3.3 没有图形会话时，菜单与托盘会让进程 abort

现象：`--selftest menu` 在 macOS VM 上 `exit=134`（SIGABRT）：

```
NSInternalInconsistencyException: 'Nil screen'
  at -[NSPopupMenuWindow _availablePositioningRectForMenuType:onScreen:]
```

根因：`NSMenu` 弹出与 `NSStatusBar` 状态项都要求有窗口服务器。没有时不是"不生效"，
而是抛 ObjC 异常——托管侧接不住，进程直接没了，调用方连错误码都拿不到。

修法：`MacOSSession.RequireScreen(...)` —— 调这两个能力之前先问 `NSScreen.screens`，
为空就抛 `NotSupportedException`（分发器统一翻成 `not_supported`）。硬崩溃变成可处理的失败。

### 3.4 `path.downloadDir()` 的断言假设了英文目录名（自检错，不是库错）

现象：中文 Ubuntu 上 `path.downloadDir()` 返回 `/home/pgyctt/下载`，而自检断言
`/[\\/]Downloads$/`，于是红。

结论：**库是对的**（Linux 上这个目录来自 XDG 用户目录，本来就随系统语言变），
错的是断言。改成"绝对路径且不是家目录本身"——那才是这条断言真正要保证的东西。

## 4. 自检里的环境判据：从"猜"改成"问"

这一轮最大的方法论收获：**判断平台能力不能看环境变量**。

- `WAYLAND_DISPLAY` 在 Wayland 会话里**可以不设**：GDK 会默认连 `wayland-0`
  （用最小的 GTK 探针实测：只设 `DISPLAY=:0`，`gdk_display_get_name` 仍返回 `wayland-0`）。
  原来自检按这个变量判"是不是 Wayland"，于是把 Wayland 当 X11，`setCursorPosition`
  的断言就红了。现在改成**发出去看结果**：成功走 X11 路径、明确 `not_supported` 走 Wayland 路径。
- macOS 的"有没有图形会话"用 `CGSessionCopyCurrentDictionary()`（不在会话里返回 NULL）。
  `NSScreen.screens.count` 是更弱的信号——实测停在登录窗口时它**仍然非空**，
  只能当退路。
- Wayland 下窗口尺寸由**合成器**决定：`gtk_window_resize`、`gtk_window_set_default_size`、
  子控件 `set_size_request`，甚至 `gdk_window_resize` 都不改变 GTK 的分配尺寸
  （连建窗时请求的 800×600 都会被换成 852×689）。所以 `geometry` / `windowevents` /
  `menubar` 里依赖"改尺寸"的断言在 Wayland 下 SKIP，并打印实测值。

**SKIP 不等于放过**：每条跳过都带判据（`LinuxDisplay.IsWayland` / `MacDisplay.HasScreenSession`），
在判据不成立的平台上断言照旧严格。X11 与 Windows 走的是完整断言路径。

## 5. 复现

```powershell
cd hyperv-vm\scripts
.\test-lab.ps1                 # 先看三平台是否连通
.\start-lab.ps1 -TimeoutSec 300

# 发布 + 推送 + 执行（每个平台一次）
dotnet publish samples\OrielDemo -c Release -r linux-x64 --self-contained true -p:PublishAot=false -o .drill\vm\linux-x64
.\deploy-run.ps1 -Payload .drill\vm\linux-x64 -Command './OrielDemo --selftest window'

# macOS 上要逐个模式跑（没有 timeout 命令，用脚本自带看门狗）
```

Linux 侧要显式给显示后端：`DISPLAY=:0 XAUTHORITY=$HOME/.Xauthority`，
验 X11 时再加 `GDK_BACKEND=x11`。

## 6. 已知限制

- **macOS VM 未登录图形会话**：21 个模式里 20 个能跑（其中几何/窗口事件/显示器/菜单/托盘
  按判据 SKIP），但**真机观感**（窗口位置、装饰、材质、Dock、托盘图标长什么样）**没有验证**。
  登录桌面会话后应复跑一遍——那需要 VM 的登录密码，本轮没做。
- **架构覆盖**：Linux 与 macOS 都是 x64；arm64 由 CI 覆盖（`osx-arm64`）。两边都覆盖到了，
  但**同一平台的两个架构没有在同一台机器上对比过**（那正是 3.2 那类 bug 的温床）。
- **Linux 只验了 Ubuntu 22.04**：Debian / Fedora / Arch 的 GTK 与 WebKit 版本组合更多，
  尤其 Fedora 的 WebKitGTK 更新更快（更容易撞上"4.1 存在但符号变了"）。
- **没有验 AOT**：VM 里跑的是自包含非 AOT（宿主交叉发布的限制）。AOT 由 CI 覆盖。
- macOS VM 是 Intel + macOS 15；更老的系统（11/12）没有取证，而本库有若干
  "macOS 13.3+ 才有"的分支（`WKWebView.setInspectable:` 等），那些分支只做了存在性探测。
