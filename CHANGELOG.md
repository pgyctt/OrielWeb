# 更新日志

本文件记录 OrielWeb 各版本的用户可感知变化。格式参照 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)。

**发布纪律**（2026-10-05 评审 P2-18 后成文）：

- 版本号唯一来源是根 `Directory.Build.props` 的 `<Version>`；tag 形如 `vX.Y.Z` 与之一一对应，`release.yml` 会校验一致性。
- **改版本的提交与打 tag 是同一次提交**（0.1.8 之后已是这个形态）。历史上 0.1.3–0.1.7 有发布/排障提交但没有对应 tag（0.1.5 被整体跳过），这些版本在仓库里已不可回溯—— NuGet 上仍可查，但源码状态只能靠提交区间近似还原。下表 0.1.3–0.1.7 的条目即按提交区间还原的近似记录。

## Unreleased

第三轮全面评审（[docs/REVIEW-2026-10-05.md](REVIEW-2026-10-05.md)）后的修复批次。

### 新增

- **源生成器编译期诊断 ORIELWEB101–106**：空白/含控制字符命令名、同程序集内同名命令、不可访问的命令方法、泛型宿主类、`ref`/`out` 参数此前要么被静默吞掉、要么变成生成文件里定位不了的 CS 错误，现在全部在编译期指回用户方法；`win.` 保留前缀给出警告（应用同名命令会被内建静默遮蔽）。
- **逐消息来源纵深加固**（IPC 安全第二层）：Windows 用 `WebMessageReceived.Source`、macOS 用 `frameInfo.securityOrigin`（10.15+，缺失时优雅回退）判定发消息文档的真实来源，顶级导航 URL 快照降级为兜底。Linux 的经典 script-message 信号不带 frame 信息，保持快照判定（已知差距，注入期自检与令牌两层不受影响）。
- Linux/macOS 的 async IPC 命令 `await` 之后**回 UI 线程**：与 Windows 语义一致，命令里 `await` 后可直接操作窗口与平台对象。

### 变更（破坏性 / 行为可见）

- **窗口尺寸统一为逻辑像素**：Windows 此前把 `WithSize`/`WithMinSize` 当物理像素用，高分屏（150%/200%）上窗口比 Linux/macOS 小一半/三分之一；现在按窗口 DPI 折算，三平台语义一致（高分屏用户会看到窗口变大）。
- **macOS `MoveTo` 改为左上角原点**：此前用的是 cocoa 左下角坐标，与 API.md 的屏幕坐标契约（和另外两个平台）相反；`At(x, y)` 同时补齐 Linux/macOS 支持（此前仅 Windows）。
- `publish.ps1 -Bundle` 明确为 **Windows 打包脚本**：非 `win-*` RID 直接报错并指路（macOS 走 `release.yml` / `tools/make-macos-app.sh`，Linux 走 `tools/wsl_publish.ps1`）。
- `AllowOrigin` 归一化：自动补尾斜杠；裸 host（漏 scheme）、带 query/fragment 的前缀直接报错——缺尾斜杠时 `http://localhost:5173` 会逐字节命中 `http://localhost:5173.evil.com/`，形似域名即可获得完整桥接与令牌。

### 修复

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
