# OrielWeb ↔ Tauri 2 API 对齐审计报告

> 审计时间：2026-10-18
> 审计对象：OrielWeb 0.3.6（`src/OrielWeb`、`packages/api`）↔ Tauri 2 / `@tauri-apps/api` **2.12.4**
> 审计方式：**实测枚举**参照实现，不依赖文档推断（命令见 §8.1）
> 审计范围：C# 宿主 API、`@orielweb/api` 前端包、桥接层 `window.oriel`、内建命令族
> 结论可执行性：每条差异都标注"迁移动作"，可直接转成任务

---

## 0. 结论

### 0.1 一句话

**名字层面的对齐已经做完（67%）且收益递减；真正的缺口是四类结构性能力，不是"再多写几个方法"。**

### 0.2 对齐度记分卡

| 维度 | 指标 | 实测值 | 判定 |
|---|---|---|---|
| **模块覆盖** | Tauri 核心 12 模块中，包内提供同名模块 | **8 / 12 = 67%**（含桥接层 `oriel.tray` 替身 → 9/12 = 75%） | 部分一致 |
| **插件覆盖** | Tauri 官方插件中，本库有可用实现 | **7 / 22 = 32%**（fs / dialog / clipboard / opener / notification / autostart / single-instance） | 部分一致 |
| **导出面** | 8 个已对齐模块的导出名覆盖率 | **64 / 95 = 67%** | 部分一致 |
| **窗口方法面** | `Window` 类公开方法覆盖率 | **57 / 84 = 68%**（剔除 3 个移动端/内部成员 → 57/81 = 70%） | 部分一致 |
| **行为一致性** | 同名 API 中的语义分歧 | **9 处同名不同义 + 5 处调用形状差异**；约 9 成同名 API 可直接迁移，但其中 3 处影响面是**全应用级**（B2 超时 / B5 关闭控制流 / C2 命令命名） | 部分一致 |
| **结构性兼容** | 句柄模型 / 插件体系 / 权限标识符 / 配置驱动 | **4 项全部缺失** | 不一致 |

### 0.3 三条最重要的判断

**判断一：方法级缺口已收敛到 0（方针内）。**
`Window` 类缺的 27 个方法里，3 个是移动端/内部成员（`activityName` / `sceneIdentifier` / `_handleTauriEvent`），1 个是有意不做（`onDragDropEvent`），剩下 **23 个全部被"跨平台不一致不做"方针排除**。也就是说：**按当前方针，没有"待补的方法"**。这与 `packages/api/tests/tauri-alignment.test.mjs` 里 `METHOD_GAP_GROUPS` 的 B 组为空一致。继续数方法名不会让对齐度上升。

**判断二：真正的差距在"能力形态"，不在"方法名"。**
四个结构性差异决定了迁移上限，且**每一项都不是加几个方法能补的**：

| # | 结构性差异 | 直接后果 |
|---|---|---|
| S1 | **无 Resource / rid 句柄表** | `Image.close()`、`fs.open`/`FileHandle`/`SeekMode`、`Channel`、`Store`、`notification.cancel` 全部无法实现 |
| S2 | **无插件体系** | Tauri 的 `@tauri-apps/plugin-*` 生态无法平移；权限标识符（`core:window:allow-set-title`）不存在 |
| S3 | **权限模型是"命令名 allow/deny"**，非 capability 文件 + ACL | `tauri.conf.json` 的 capability 无法转换；无 `checkPermissions`/`requestPermissions`；无按窗口 (`"windows": ["main"]`) 授权 |
| S4 | **无 webview 独立概念** | `@tauri-apps/api/webview` 整模块缺失；运行期 `setZoom`、`reparent`、`position` 无对应物 |

**判断三：对齐工程本身有盲区（元问题，优先级最高）。**
`tauri-alignment.test.mjs` 的 `MODULES` 只登记 14 个模块，**`tray` / `menu` / `webview` / `webviewWindow` 完全没登记**；而 `TauriEvent` 是对象、其内部常量（Tauri 16 个键 vs 本库 13 个）**不在导出面比较范围内**。这意味着：这四个模块的差异、以及所有"常量对象内部缺项"，**不会被任何测试发现**。这不是能力问题，是对账本自身的漏洞——修它的成本最低、收益最高。

---

## 1. 审计方法与判定口径

### 1.1 参照实现

| 项 | 版本 | 获取方式 |
|---|---|---|
| `@tauri-apps/api` | 2.12.4 | `packages/api/node_modules`（devDependency，CI 安装） |
| `plugin-fs` / `plugin-dialog` / `plugin-clipboard-manager` / `plugin-opener` / `plugin-notification` / `plugin-autostart` | 各自 2.x | 同上 |
| 本方 | OrielWeb 0.3.6 / `@orielweb/api` 0.3.6 | `Directory.Build.props`、`packages/api/package.json` |

**为什么必须实测**：Tauri 2 的导出面随小版本变化（2.12 新增 `setEnabled`/`isEnabled`/`setSizeConstraints` 等）。
用文档或记忆做基准，结论会在下一次 `npm update` 后失效。本报告的每个数字都可以用 §8.1 的命令重算。

### 1.2 四档判定

| 判定 | 含义 | 迁移成本 |
|---|---|---|
| **完全一致** | 名字、调用形状、返回类型、语义都能对上 | 只改 import 前缀 |
| **部分一致** | 能力存在，但契约不同（单位 / 返回形状 / 错误 / 事件形状 / 控制流） | 改调用点代码 |
| **不一致（缺失）** | Tauri 有，本库没有 | 需新增实现 |
| **不一致（明确不做）** | 按方针排除，且已写进测试钉住 | 无（迁移者自行兜底） |

### 1.3 一条重要提醒

本文的"完全一致"是**严格口径**：只要调用形状有一处不同（例如 `listen` 返回同步函数而非 `Promise`），就归入"部分一致"。
按宽松口径（"`await` 之后能跑就算一致"）比例会更高，但那样的数字对迁移者没有指导意义。

---

## 2. 维度一：功能覆盖度

### 2.1 核心模块矩阵（12 个）

| Tauri 模块 | 本库载体 | 导出覆盖 | 判定 |
|---|---|---|---|
| `core` | `@orielweb/api/core` | 3 / 11 | ⚠️ 部分（缺 Channel / Resource / 权限查询） |
| `event` | `@orielweb/api/event` | 5 / 5（+2 扩展） | ✅ 导出完整，⚠️ 内置事件仅 7/16 |
| `dpi` | `@orielweb/api/dpi` | 4 / 6（+4 扩展） | ⚠️ 部分（缺 `Size`/`Position` 类型别名） |
| `window` | `@orielweb/api/window` | 8 / 17（+2 扩展） | ⚠️ 部分（缺 5 个显示器 API + 4 个类型常量） |
| `app` | `@orielweb/api/app` | 5 / 16 | ⚠️ 部分（缺 exit / show / hide / setTheme / defaultWindowIcon） |
| `path` | `@orielweb/api/path` | 33 / 34 | ✅ 近乎完整（缺 `BaseDirectory` 枚举） |
| `image` | `@orielweb/api/image` | 2 / 2 | ⚠️ 名字完整、**语义不同**（字节容器 vs 资源句柄） |
| `mocks` | `@orielweb/api/mocks` | 4 / 4（+3 扩展） | ✅ 完整 |
| `menu` | 仅 C#（`OrielMenuItem`） | 0 / 8 | ❌ 缺失（无 JS 模块、无应用菜单栏） |
| `tray` | **桥接层** `window.oriel.tray` | 3 静态 + 10 方法 | ⚠️ 能力齐、**不在包里**（无法 `import`） |
| `webview` | 无 | 0 / 3 | ❌ 缺失（一窗口一 webview，无独立概念） |
| `webviewWindow` | 无 | 0 / 3 | ❌ 缺失（页面不能建窗） |

**汇总：包内同名模块 8/12；含桥接替身 9/12；完全缺失 3/12（menu / webview / webviewWindow）。**

### 2.2 插件矩阵（Tauri 官方 22 个）

| 插件 | 本库 | 判定 |
|---|---|---|
| `fs` | `fs.*` 14 个命令 + scope 模型 | ⚠️ 部分（无句柄式 API） |
| `dialog` | `dialog.*` 5 个 | ✅ 对齐 |
| `clipboard-manager` | `clipboard.*` 3 个（纯文本） | ⚠️ 部分（缺 image / html / secret） |
| `opener` | `opener.*` 3 个 | ✅ 对齐 |
| `notification` | `notification.*` 3 个（桌面子集） | ⚠️ 部分（无权限回调 / 句柄） |
| `autostart` | `autostart.*` 3 个 | ✅ 对齐 |
| `single-instance` | C# `SingleInstance(id, onActivate)` | ✅ 对齐（Rust-only 插件，页面无入口，两边一致） |
| `window-state` | 无 | ❌ 缺失 |
| `global-shortcut` | 无（`OrielAccelerator` 只解析菜单加速键文本） | ❌ 缺失 |
| `os` | 只有桥接的 `oriel.platform` | ❌ 缺失（8 个查询） |
| `process` | 只有 C# `app.Quit()` | ❌ 缺失（页面无 exit） |
| `store` | 无 | ❌ 缺失 |
| `log` | 只有 console 单向转发 | ❌ 缺失 |
| `deep-link` / `updater` / `http` / `websocket` / `upload` / `shell` / `sql` / `stronghold` / `localhost` | 无 | ⛔ 明确不做 |
| `drag` / `positioner` / `cli` / `barcode-scanner` / `biometric` / `geolocation` / `haptics` / `nfc` | 无 | ⛔ 不做（桌面/移动端专用） |

### 2.3 数字汇总

```
核心模块       8 / 12  (67%)   [含桥接 tray → 9/12 (75%)]
官方插件       7 / 22  (32%)   [桌面实用度加权后约 60%]
导出名        64 / 95  (67%)   我方独有 11 个（均有 EXTRAS 记录）
Window 方法   57 / 84  (68%)   缺 27：移动端 3 + 方针排除 23 + 有意不做 1
```

---

## 3. 维度二：行为一致性

### 3.1 一致的行为契约

以下契约**逐字对齐**，迁移不需要改代码（除 import 前缀）：

| 契约点 | 说明 |
|---|---|
| `invoke(cmd, args)` 形状 | 两参调用、返回 Promise、参数为对象 |
| `listen` 回调的事件对象 | `{ event, id, payload }`（Tauri 形状）；底层裸 payload 的包装在包内完成 |
| `emit` / `emitTo` 签名 | `(event, payload)` / `(target, event, payload)` |
| 内置事件名 | 页面侧用 `tauri://resize` 等 Tauri 名，包内翻译成宿主名 |
| `dialog.open` 返回 | 单选 `string`、多选 `string[]`、取消 `null` |
| `dialog.message` 返回 | 被点按钮的**文案**；外部关闭 `null` |
| `dialog.ask` / `confirm` 返回 | `boolean` |
| `fs.readDir` 返回 | `{ name, isFile, isDirectory, isSymlink }[]` |
| `fs.mkdir` 语义 | 非递归 + 已存在 → 报错；递归 + 已存在 → 正常 |
| `path` 的 Rust 口径 | `extname('a')` 不含点、`extname('.gitignore') === ''`（**不是** .NET 的行为） |
| `path` 目录解析 | 24 个目录按 Tauri 的 `dirs` crate 规则逐格对齐；不支持项返回 `not_supported` |
| `dpi` 四个类 | 构造签名、`width`/`height`/`x`/`y` 字段、`toPhysical`/`toLogical` |
| `UserAttentionType` | `Critical: 1` / `Informational: 2` |
| `mocks` | `mockIPC` / `mockWindows` / `mockConvertFileSrc` / `clearMocks` 语义一致 |
| 托盘 API 表面 | `new` / `getById` / `removeById` + `setIcon` / `setMenu` / `setTooltip` / `setTitle` / `setVisible` / `setIconAsTemplate` 同名同义 |
| 文件拖放事件载荷 | Tauri 的 `DragDropEvent` 形状（`paths` / `position`） |

### 3.2 同名不同义（9 处，迁移必须改调用点）

| # | 契约点 | 本库 | Tauri 2 | 后果 | 迁移动作 |
|---|---|---|---|---|---|
| B1 | **几何查询单位** | `innerSize()` 等返回 **Logical** | 返回 **Physical** | 直接把返回值喂给依赖物理像素的算法会偏 `scaleFactor` 倍 | 用 `toPhysical(await scaleFactor())` 或改算法 |
| B2 | **`invoke` 默认超时** | **30 秒**（可全局/逐次改） | **无超时** | 长任务（导出、下载、AI 推理）迁移后**必然** 30s 失败 | 长任务显式 `{ timeout: 0 }` 或设 `oriel.timeout` |
| B3 | **错误值形状** | `Error`，挂 `code` / `data` | Rust `Err` 的**序列化原值** | Tauri 侧 `catch (e) { e.code }` 在两边含义不同 | 逐处检查 `catch` 分支 |
| B4 | **`listen` 返回值** | **同步** `UnlistenFn` | `Promise<UnlistenFn>` | `listen(...).then(...)` 会抛 `TypeError` | 改为 `await listen(...)`（原代码多可保留） |
| B5 | **关闭接管控制流** | **反向**：页面同意才 `destroy()`；`preventDefault()` 是空操作 | `preventDefault()` 阻止关闭 | 迁移后的"确认框"逻辑会**永远关不掉**（回调里不调 destroy） | 在回调末尾补 `destroy()`，删除 `preventDefault` 依赖 |
| B6 | **`fs.stat` 时间字段** | 数字（毫秒时间戳） | `Date` 对象 | `.getTime()` 在数字上抛错 | 包一层 `new Date(x)` |
| B7 | **`fs` 二进制通道** | JSON 数字数组 | `invoke` 原始 body | 大文件内存/带宽放大数倍 | 大文件改走 `convertFileSrc` 或自定义命令 |
| B8 | **`app.getTauriVersion()`** | 返回**库版本**（`0.3.6`） | 返回**框架版本**（`2.12.4`） | 版本号比较逻辑全部失效 | 版本判断重写为能力探测 |
| B9 | **`path.normalize`** | 纯词法（`Path.GetFullPath`），路径不存在也正常 | `canonicalize`：**路径不存在会报错** | 本库更宽松 → 不会炸，但语义不同（不解析符号链接） | 无需改；需要真实路径时改用显式 API |

> **2026-10-25 更新（B5 已修正）**：上表 B5 记录的是 10-18 当时的行为。`onCloseRequested`
> 现已改成与 Tauri 同一段逻辑——handler（含 async）跑完后，没调 `preventDefault()` 就
> `destroy()`。原文保留是为了记录这处判断的修正过程；迁移时**不需要**再手工补 `destroy()`。

### 3.3 调用形状差异（5 处，影响 import 与参数拼写）

| # | 契约点 | 本库 | Tauri 2 | 迁移动作 |
|---|---|---|---|---|
| C1 | **参数名转换** | 按 C# 参数名**原样**取值 | JS camelCase ↔ Rust snake_case **自动转换** | 命令参数写 camelCase；Rust 侧命令不能直接搬 |
| C2 | **命令命名** | 点分（`todo.add`），`[OrielCommand("…")]` 显式声明 | 默认函数名 snake_case；插件用 `plugin:fs\|read_text_file` | 全部命令名重新映射 |
| C3 | **`emit` 权限** | **必须显式** `Allow("event.emit")` | 默认允许（`core:event:default`） | 加一行能力配置 |
| C4 | **内置事件可用性** | 7 / 16（`tauri://destroyed`、`DRAG_*`、`WINDOW_CREATED` 不可用） | 16 / 16 | 用到未实现事件的地方改走宿主侧回调 |
| C5 | **`convertFileSrc` 协议参数** | 只接受缺省值（文件固定走 `oriel://file.oriel/`） | 可传自定义协议名 | 删除第二参数 |

### 3.4 内置事件覆盖明细

| TauriEvent 常量 | Tauri 2 | 本库 |
|---|---|---|
| `WINDOW_RESIZED` / `WINDOW_MOVED` / `WINDOW_FOCUS` / `WINDOW_BLUR` / `WINDOW_SCALE_FACTOR_CHANGED` / `WINDOW_THEME_CHANGED` / `WINDOW_CLOSE_REQUESTED` | 7 | ✅ 7（订阅时自动打开转发） |
| `WINDOW_DESTROYED` | 1 | ⚠️ 常量存在，**永远收不到**（页面正在被拆掉） |
| `WINDOW_CREATED` | 1 | ❌ 未实现 |
| `WINDOW_SUSPENDED` / `WINDOW_RESUMED` | 2 | ❌ **常量都缺**（Tauri 2.12 有，本库 `TauriEvent` 无这两个键） |
| `WEBVIEW_CREATED` | 1 | ❌ 常量缺 |
| `DRAG_ENTER` / `DRAG_OVER` / `DRAG_DROP` / `DRAG_LEAVE` | 4 | ⛔ 有意不做（页面拿不到文件路径） |

> **注意**：`WINDOW_SUSPENDED` / `WINDOW_RESUMED` / `WEBVIEW_CREATED` 是 **Tauri 2.12 新增**的常量。
> 现有对齐测试只比"导出名"，不比对象内部键，所以这 3 个缺口**不会被测试发现**——它们是"Tauri 升级后静默漂移"的活样本。

---

## 4. 维度三：兼容性差异（结构性）

### S1 · 无 Resource / rid 句柄表

| 项 | 现状 |
|---|---|
| Tauri | `Resource` 基类 + `rid`，页面持句柄、宿主存对象；`Channel` 用于流式回传；`transformCallback` 注册回调表 |
| 本库 | IPC 是**一次性请求-回执**，无句柄、无回调表、无流式通道 |
| 受影响 API | `Image.close()`、`fs.open`/`FileHandle`/`seek`/`readTextFileLines`/`watch`、`notification.cancel`/`active`/`pending`、`Channel`、`PluginListener`、`addPluginListener`、`transformCallback` |
| 可逆性 | **低**。需要新增一套 rid 分配/回收/失效协议，并且要处理"页面刷新后句柄泄漏"——这是架构级改动 |
| 现状评价 | 这是**有意的简化**（更好迁移的代价是能力少一截）。但 `Image.close()` 缺失会让迁移代码 `await image.close()` 抛 `TypeError`，属于"当场炸"，可接受 |

### S2 · 无插件体系

| 项 | 现状 |
|---|---|
| Tauri | `Builder::plugin()` + `tauri-plugin-*` crate；插件可注册命令、生命周期钩子、权限条目 |
| 本库 | 固定内建命令族 + `AddCommands<T>()` 注册应用命令；无第三方扩展点 |
| 受影响 | 所有 `@tauri-apps/plugin-*` 的第三方生态；`os`/`store`/`log`/`window-state`/`global-shortcut` 这类"官方但独立"的能力只能硬编码进核心库 |
| 可逆性 | 中（需要定义命令命名空间 + 权限条目的注册机制，但不需要 ABI） |

### S3 · 权限模型：命令名 allow/deny vs capability + ACL

| 维度 | 本库 | Tauri 2 |
|---|---|---|
| 载体 | **C# 代码**：`UseCapabilities(c => c.Allow("todo.*"))` | **JSON 文件**：`capabilities/*.json` |
| 粒度 | 命令名（精确 / 前缀通配 / `*`） | 权限标识符 `core:window:allow-set-title` + scope |
| scope | `fs` 独立路径 scope（glob + `$APPDATA` 变量） | `allow`/`deny` scope 对象，随权限条目走 |
| 按窗口 | ❌ 全应用一套 | ✅ `"windows": ["main"]` |
| 按来源 | ✅ `AllowOrigin(url)` | ✅ `"remote": { "urls": [...] }` |
| 运行时查询 | ❌ 无 `checkPermissions` | ✅ `core.checkPermissions()` |
| 默认姿态 | Release **fail-closed**（未声明即拒） | 未匹配任何 capability 即拒（等价） |
| 可逆性 | 中高。命令名 → 权限标识符的映射可以生成，但"按窗口"需要窗口 label 参与判定（已具备） |

**评价**：语义等价度较高（都支持 allow/deny/scope/来源），但**形状完全不同**，`tauri.conf.json` 无法机械转换。
本库的"代码即配置"对 C# 开发者更顺手，但对"从 Tauri 迁移"是纯成本。

### S4 · 无 webview 独立概念

| 项 | 现状 |
|---|---|
| Tauri | `Window` 与 `Webview` 分离，一个窗口可含多个 webview，支持 `reparent` / `setAutoResize` / `position` / `size` |
| 本库 | 一窗口 = 一 webview（`WebviewWindow`），无独立概念 |
| 缺的模块 | `@tauri-apps/api/webview`（3 个导出）、`webviewWindow`（3 个导出） |
| 缺的能力 | 运行期 `setZoom`（只有创建期 `WithZoom`）、`clearAllBrowsingData`、多 webview 布局 |
| 可逆性 | 低。需要把窗口宿主层拆成 window + child webview 两层，三平台都要改 |

### S5 · 页面不能建窗

| 项 | 现状 |
|---|---|
| Tauri | `new WebviewWindow(label, options)` / `new Window(label, options)`，页面可建窗 |
| 本库 | 只有 C# `app.CreateWindow(...)`；页面**没有任何建窗入口** |
| 迁移影响 | 凡是用 `new WebviewWindow(...)` 打开设置窗/关于窗的应用，必须改成"调自定义命令 → 宿主建窗" |
| 可逆性 | 高（新增 `window.new` 命令族，能力门禁 + label 唯一性已有基础设施） |

### S6 · Rust 侧 API 对应关系

| Tauri (Rust) | 本库 (C#) | 判定 |
|---|---|---|
| `tauri::Builder` | `OrielAppBuilder` | ⚠️ 形状不同（链式配置 vs 闭包钩子） |
| `Builder::setup()` | `AddWindow(…, onCreated)` / `Loaded` 事件 | ⚠️ 无"应用级 setup 钩子" |
| `Builder::on_window_event()` | `WebviewWindow` 的细粒度事件 | ⚠️ 无统一 `WindowEvent` 枚举回调 |
| `Builder::invoke_handler(generate_handler![…])` | `AddCommands<T>()` + `[OrielCommand]` 源生成 | ✅ 模型级一致（且零反射） |
| `Builder::manage(state)` + `State<T>` | `AddCommands<T>(Func<T> factory)` / 构造函数单例 | ⚠️ 无通用 DI 容器与 `State<T>` 参数注入 |
| `Manager::get_webview_window(label)` | `app.GetWindow(label)` | ✅ 一致 |
| `Emitter::emit` / `emit_to` | `WebviewWindow.EmitEvent` / `emitTo` 命令 | ✅ 一致 |
| `WindowBuilder` / `WebviewWindowBuilder` | `OrielWindowOptions` + `WithXxx()` | ⚠️ 选项覆盖 ~60%（无 `visibleOnAllWorkspaces`、`shadow`、`effects`、`titleBarStyle`…） |
| `tray::TrayIconBuilder` | `AddTray(...)` / `CreateTray(...)` | ✅ 对齐 |
| `WindowEvent` 枚举 | `OrielWindowEventKind` + `EventForwarding` | ⚠️ 覆盖面不同 |
| 插件 `TauriPlugin` trait | 无 | ❌ 缺失 |

### S7 · 打包与更新

| 项 | 本库 | Tauri 2 |
|---|---|---|
| 打包 | Velopack（`vpk`）：Setup.exe / msi / AppImage / pkg | `tauri-bundler`：msi/nsis/dmg/appimage/deb/rpm |
| 自动更新 | **未接入**（README 明示） | `updater` 插件 + 签名清单 |
| API 层 | 无 `updater` 模块 | `@tauri-apps/plugin-updater` |
| 判定 | ⛔ 明确不对齐（交给 Velopack，职责在应用侧） | — |

---

## 5. 三张清单

### 5.1 完全一致（迁移只改 import）

| 条目 | 覆盖内容 | 备注 |
|---|---|---|
| `path` 词法族 | `sep` / `delimiter` / `join` / `dirname` / `extname` / `basename` / `isAbsolute` / `resolve` | `normalize` 除外（见 B9） |
| `path` 目录族（24 个） | `appConfigDir`…`videoDir` + `resolveResource` | 平台"不支持"项同样返回 `not_supported` |
| `dpi` 四个类 | `LogicalSize` / `PhysicalSize` / `LogicalPosition` / `PhysicalPosition` | 构造 + 字段 + 换算方法 |
| `dialog` | `open` / `save` / `message` / `ask` / `confirm` | 返回形状逐项对齐 |
| `clipboard`（纯文本） | `readText` / `writeText` / `clear` | |
| `opener` | `openUrl` / `openPath` / `revealItemInDir` | |
| `autostart` | `isEnabled` / `enable` / `disable` | |
| `mocks` | `mockIPC` / `mockWindows` / `mockConvertFileSrc` / `clearMocks` | |
| `event`（除 `listen`） | `once` / `emit` / `emitTo` / 事件对象形状 | `TauriEvent` 常量名一致，覆盖 7/16 |
| `window` 状态查询 | `isVisible` / `isMaximized` / `isMinimized` / `isFocused` / `isFullscreen` / `isAlwaysOnTop` / `isDecorated` / `isResizable` / `isMaximizable` / `isMinimizable` / `isClosable` / `isEnabled` / `theme` / `title` | |
| `window` 动作 | `show` / `hide` / `close` / `destroy` / `setFocus` / `minimize` / `maximize` / `unmaximize` / `toggleMaximize` / `setTitle` / `setResizable` / `setAlwaysOnTop` / `setFullscreen` / `setIcon` / `setBackgroundColor` / `setMaximizable` / `setMinimizable` / `setClosable` / `setEnabled` / `center` / `requestUserAttention` / `setSize` / `setPosition` / `setMinSize` / `setMaxSize` / `setSizeConstraints` | 几何**设置**一致（客户区逻辑像素）；几何**查询**不一致（B1） |
| `window` 事件 | `onResized` / `onMoved` / `onFocusChanged` / `onThemeChanged` / `onScaleChanged` | `onCloseRequested` 的**控制流**不一致（B5） |
| `tray`（桥接层） | `new` / `getById` / `removeById` + 10 个 setter | 不在包里，无法 `import` |

### 5.2 部分一致（要改调用点）

| 条目 | 差异 | 迁移动作 |
|---|---|---|
| `core.invoke` | 默认 30s 超时；错误形状不同 | 长任务显式 `timeout: 0`；重写 `catch` |
| `event.listen` | 返回同步函数 | 加 `await` |
| `event.emit` / `emitTo` | 需显式能力授权 | 加 `Allow("event.emit")` |
| `event.TauriEvent` | 13/16 常量，实际可用 7 | 未实现事件改走宿主回调 |
| `window` 几何查询 | 返回 Logical 而非 Physical | 按需 `toPhysical` |
| `window.onCloseRequested` | 反向控制流 | 回调末尾补 `destroy()` |
| `fs.stat` / `lstat` | 时间是数字 | 包 `new Date()` |
| `fs` 二进制 | JSON 数字数组 | 大文件改协议 |
| `fs` `baseDir` 选项 | **不存在**（第二个参数被静默忽略） | 传绝对路径，或见 §6 风险 R1 |
| `image.Image` | 字节容器，无 `close()` / `rid` | 删除 `close()` 调用 |
| `app.getTauriVersion` | 库版本 | 改能力探测 |
| `path.normalize` | 纯词法 | 无需改（语义不同） |
| `convertFileSrc` | 不接受协议参数 | 删第二参数 |
| `clipboard` | 无 `readImage`/`writeImage`/`writeHtml`/`writeSecret` | 需图片时走自定义命令 |
| `dialog` | `canCreateDirectories` / `recursive` 未实现（**会明确报错**） | 删除这些选项 |
| 命令命名与参数名 | 点分名 + 无 camelCase 转换 | 重写命令名与参数拼写 |

### 5.3 不一致（缺失或明确不做）

**缺失（可按优先级补）**

| 条目 | Tauri 侧 | 缺口规模 |
|---|---|---|
| `app.exit` / `show` / `hide` / `setTheme` / `setDockVisibility` / `defaultWindowIcon` / `getBundleType` | `app` 模块 | 7 个导出 |
| 显示器模型 | `currentMonitor` / `primaryMonitor` / `availableMonitors` / `monitorFromPoint` / `cursorPosition` | 5 个导出 + 底层枚举 |
| `webview` 模块 | `getCurrentWebview` / `getAllWebviews` / `Webview` 类 | 3 个导出 + 12 个方法 |
| `webviewWindow` 模块 | `new WebviewWindow(...)` / `getAllWebviewWindows` / `getCurrentWebviewWindow` | 3 个导出 |
| `menu` 模块 | `Menu` / `Submenu` / `MenuItem` / `CheckMenuItem` / `IconMenuItem` / `PredefinedMenuItem` / `NativeIcon` / `itemFromKind` | 8 个导出 + 应用菜单栏 |
| `core` 的 Channel / Resource / PluginListener / transformCallback | 流式与句柄 | 见 S1 |
| `os` / `process` / `store` / `log` / `window-state` / `global-shortcut` | 6 个插件 | 见 §2.2 |
| `notification` 的 `onAction` / `cancel` / `active` / `pending` | 通知句柄与点击回传 | 见 S1 |

**明确不做（有理由，已钉进测试）**

| 条目 | 理由 |
|---|---|
| `Window` 的 23 个方法（A 组） | 跨平台不一致（外观开关 4 / 平台特有 6 / 特效 2 / 光标 6 / 任务栏徽标 4 / `setTheme` 1）。方针：不在三平台给三个语义不同的同名方法 |
| `onDragDropEvent` | 浏览器安全模型决定页面拿不到文件路径；造"形状像但没有 paths"的事件会误导 |
| `Image.close()` / `Resource` | 无句柄表，给空方法等于骗人 |
| `Activity` / `Scene` 等移动端成员 | 桌面库无对应物 |
| `shell` / `http` / `websocket` / `sql` / `stronghold` / `deep-link` / `updater` / `localhost` | 与库定位冲突（页面 `fetch` 够用 / 打包交给 Velopack / 无插件体系） |
| `target: "就绪后请复核"` 组 | 见 §7.4 |

---

## 6. 潜在缺口与风险

### 6.1 静默失败风险（最危险的一类）

| # | 风险 | 触发场景 | 后果 | 建议 |
|---|---|---|---|---|
| **R1** | `fs.*` 的 `baseDir` 选项被**静默忽略** | 迁移代码 `readTextFile('conf.json', { baseDir: BaseDirectory.AppData })` | 相对路径按**进程 CWD** 解析 → 读错文件或 `path_not_allowed`，而错误信息不会提示"你传的 baseDir 被丢了" | 在 `fs` 各函数里检测第二个参数并**显式抛错**（"本库不支持 baseDir，请先用 `appDataDir()` 拼绝对路径"）；或实现 `BaseDirectory` 枚举 |
| **R2** | 默认 30s 超时**没有独立错误码** | 长任务迁移后超时 | 页面只能靠文案匹配区分"超时"与"业务失败" | 给超时 reject 挂 `code: 'timeout'`（`OrielIpcErrorCodes` 加一项，桥接层直接构造） |
| **R3** | `win.` 前缀**静默遮蔽** | 应用自定义 `win.xxx` 命令 | 命令注册成功但**永不可达**（被内建命令挡住） | 已有 `ORIELWEB106` 诊断（保留前缀）→ 确认这是 Warning 而非 Error；建议升级为 Error |
| **R4** | `event.emit` 未授权时**静默丢弃** | 多窗口广播未配 `Allow("event.emit")` | 返回 0 不抛错 → "发出去了但没人收到" | 保持现状（有意的时序设计），但在 `emit` 首次返回 0 时 `console.warn` 一次 |

### 6.2 对齐账本的三个盲区

| # | 盲区 | 证据 | 影响 |
|---|---|---|---|
| **M1** | `MODULES` 只登记 14 个模块，**`tray` / `menu` / `webview` / `webviewWindow` 未登记** | `tauri-alignment.test.mjs` 第 36–53 行 | 这四个模块的差异永远不会让测试变红。`tray` 实际已实现（桥接层），却没被纳入对账 |
| **M2** | 只比**导出名**，不比**常量对象内部键** | 测试用 `Object.keys(module)` | `TauriEvent` 缺 3 个键（`WINDOW_SUSPENDED`/`RESUMED`/`WEBVIEW_CREATED`）不会被发现 |
| **M3** | 只比**方法名**，不比**签名与返回类型** | `METHOD_GAP_GROUPS` 是字符串数组；`packages/api/tests/*.test.mjs` 里的形状断言是**逐个手写**的 | 形状差异只在"有人想起写断言"时被锁住。反例：`fs.stat` 的 `mtime` 是 `number` 而非 `Date`，这条没有任何运行期断言（只有 JSDoc 说明） |

> 这三条是"对齐工程"的元缺陷：**能被测到的差异才会被修**。M1–M3 的修复成本很低（各几十行测试），收益是把"Tauri 升级导致的漂移"从"人工审计"变成"CI 红灯"。

### 6.3 跨平台不对称（会影响"一致性"承诺）

| 项 | 不对称点 |
|---|---|
| `outerSize()` | Linux/Wayland 下可能等于 `innerSize()`（合成器不提供装饰尺寸） |
| `innerPosition()` | Linux 恒等于 `outerPosition()`（GTK 不暴露装饰偏移） |
| `onScaleChanged` | macOS 有已知漏报：只改系统缩放而窗口不动时不报 |
| `path` 目录族 | `executableDir`/`runtimeDir` 仅 Linux；`fontDir` 无 Windows；`templateDir` 无 macOS |
| `tray` 事件 | Linux 无 `button`、macOS 无 `position`（字段为 `null`） |
| 截图/特效/徽标 | 三平台各有实现缺口（属 A 组，已排除） |

这些**不是缺陷而是平台事实**，但迁移者会当成缺陷报 bug。建议在 `@orielweb/api` 的对应函数 JSDoc 里逐条标注（目前部分已标）。

### 6.4 版本漂移风险

本审计基准是 `@tauri-apps/api` **2.12.4**。Tauri 2.x 仍在加 API（2.12 就新增了 `setEnabled` / `isEnabled` / `setSizeConstraints` / `setBackgroundColor`）。
现有测试的价值在于"升级即红"，但 M1–M3 让它只对 14 个模块的导出名有效。**先修 M1–M3，再谈补能力**——否则补完也会在下一次升级中悄悄落后。

### 6.5 迁移风险按语言分岔：TS 有编译期保护，JS 没有

`packages/api/types/*.d.ts` 由本库 JSDoc 用 `tsc -p tsconfig.types.json` 生成（并提交进仓库、CI 校验无漂移），
**不是从 `@tauri-apps/api` 的类型派生的**。后果：

| 迁移者 | 撞到语义差异的时机 | 风险 |
|---|---|---|
| TypeScript | **编译期**：`stat().mtime` 是 `number`，按 Tauri 的 `Date` 用会报错；`innerSize()` 是 `LogicalSize` 而非 `PhysicalSize` | 低（被迫当场改） |
| JavaScript | **运行期**，且只有走到那条分支才炸：`mtime.getTime()` 在 `number` 上抛 `TypeError`；几何偏 `scaleFactor` 倍是**静默错误** | 高 |

也就是说：**"类型门禁"保护的不是对齐，而是本库自身的类型自洽**。
想在迁移前发现差异，只有两条路——(a) 用 TS 迁移；(b) 把差异写成运行期断言（P0-3）。
这条也解释了为什么 §3.2 里"偏一倍"的那类错误值得单独列：它是这四种组合里唯一**不报错**的。

---

## 7. 后续对齐建议

排序原则：**先修"会让差异隐身"的机制，再修"会让迁移静默出错"的行为，最后才是补能力。**

### 7.1 P0 · 机制与静默失败（预计改动量小、收益最高）

| # | 动作 | 验收方式 |
|---|---|---|
| P0-1 | **M1**：把 `tray` / `menu` / `webview` / `webviewWindow` 登记进 `MODULES`（缺失模块用"显式 skip + 计数"而不是"跳过不报"） | 测试输出里能看到"4 个模块未实现，已登记"；新增 `tray` 与桥接层的映射断言 |
| P0-2 | **M2**：比对常量对象内部键（`TauriEvent` 16 键、`UserAttentionType`、`ProgressBarStatus`、`Effect`/`EffectState`） | 补上 `WINDOW_SUSPENDED`/`RESUMED`/`WEBVIEW_CREATED` 三个常量（未实现就明确标 `⛔`），测试锁死键集 |
| P0-3 | **M3**：给**没有运行期断言**的形状差异补断言（`fs.stat` 的时间是 `number` 不是 `Date`；`listen` 返回值可 `.then`；`image.rgba()` 仍返回 Promise；`invoke` 的错误带 `code`） | 每条断言失败时必须能指出"这是与 Tauri 的形状差异"，而不是普通的回归。几何单位已有断言（`window-geometry.test.mjs` 的"type 必须是 Logical"），照它的写法补 |
| P0-4 | **R1**：`fs.*` 收到 `baseDir`/第二个参数时显式报错（或实现 `BaseDirectory`） | 单测：传 `{ baseDir: 1 }` 必须 reject，错误文案指出替代方案 |
| P0-5 | **R2**：超时 reject 挂 `code: 'timeout'` | `bridge.test.mjs` 断言超时错误的 `code` |

### 7.2 P1 · 高频迁移路径（真正影响"能不能迁"）

| # | 动作 | 理由 | 验收 |
|---|---|---|---|
| P1-1 | `app.exit()` / `app.show()` / `app.hide()` | 页面退出/显隐是最常见的两个需求，现在只能绕自定义命令 | 命令 + 包模块 + 能力门禁 + 单测 |
| P1-2 | `webviewWindow` 模块（页面建窗）+ `window.new` 命令族 | 凡有多窗口的应用都会撞上 | label 唯一性、跨窗口事件、能力授权三处测试 |
| P1-3 | `menu` 模块 + 应用菜单栏 | Tauri 的核心模块之一；C# 已有 `OrielMenuItem` 模型，缺 JS 层与"应用菜单"这个挂载点 | 先只做"窗口菜单栏"，macOS 顶部菜单与 Linux/Windows 窗口内菜单分别验证 |
| P1-4 | 显示器模型（`currentMonitor` / `primaryMonitor` / `availableMonitors`） | "在哪个屏上居中/全屏"是多屏用户的高频诉求 | 三平台枚举测试 + `scaleFactor` 一致性 |
| P1-5 | `listen` 返回 `Promise<UnlistenFn>` | 唯一的"形状硬伤"，改起来是纯加性（同步函数包一层 `Promise.resolve`） | 现有测试加 `listen(...).then()` 用例 |

### 7.3 P2 · 可选（有明确前置条件）

| # | 动作 | 前置条件 |
|---|---|---|
| P2-1 | `os` / `process` 模块 | 无（纯查询，成本低） |
| P2-2 | `store` / `window-state` | 需要先定"应用数据目录 + JSON 存储"的落点，`path.appDataDir` 已具备 |
| P2-3 | `global-shortcut` | 三平台各一套全局热键 API（Windows `RegisterHotKey`、Linux X11 grab / Wayland portal、macOS `NSEvent`） |
| P2-4 | `notification` 的 `onAction` / `cancel` | 依赖 S1（句柄表），否则 `cancel` 只能按 id 字符串 |
| P2-5 | `clipboard` 的 image / html | image 依赖 S1；html 在 Windows 上是 `CF_HTML` 偏移头，容易静默乱码 |
| P2-6 | `image` 的 `rid` 化 | 依赖 S1；不做的代价是 `close()` 缺失（已在 EXTRAS 记录） |

### 7.4 建议**不做**（附复核条件）

| 项 | 理由 | 何时复核 |
|---|---|---|
| S1 句柄表（Resource/Channel） | 架构级改动，收益集中在少数 API（Image/fs 句柄/通知句柄）；一次性请求-回执是当前 IPC 的核心简化 | 当出现"必须流式回传"或"必须持句柄"的真实需求（如大文件分块上传、日志流） |
| S4 多 webview | 三平台窗口宿主都要拆层 | 出现"单窗口多 webview"的真实需求 |
| S2 插件体系 | 库的定位是"核心库"而非"平台"；一旦开插件 ABI，兼容性承诺会指数上升 | 出现第三方扩展需求 |
| `Window` A 组 23 项 | 跨平台语义不一致 | 方针变更（需同时改 `METHOD_GAP_GROUPS` 断言并说明） |
| `shell` / `http` / `sql` / `updater` / `deep-link` | 与定位冲突 | 不复核（除非库定位改变） |

### 7.5 建议的对齐节奏

```
第一批（P0）：修账本盲区 + 3 处静默失败          → 让"差异"重新可见
第二批（P1-1/2/5）：页面能退出、能建窗、listen 对齐 → 打通最常见的三条迁移路
第三批（P1-3/4）：菜单栏 + 显示器                 → 补齐"桌面感"两块拼图
之后：停止按方法名对账，转为"按真实迁移案例驱动"
```

**最后一条是关键**：导出面到 67% 之后，每加一个名字的边际收益已经很低。下一步应当拿 2–3 个真实的 Tauri 2 应用做**迁移演练**，用真实卡点决定优先补什么——那比继续枚举 `@tauri-apps/api` 的导出更有效。

---

## 8. 附录

### 8.1 可复现命令

```bash
# 导出面 / 方法面 diff（本报告所有数字的来源）
node --test packages/api/tests/tauri-alignment.test.mjs

# 逐条清单（GAPS / EXTRAS / METHOD_DIVERGENCES / METHOD_GAP_GROUPS）
grep -n "const GAPS\|const EXTRAS\|const METHOD_DIVERGENCES\|const METHOD_GAP_GROUPS" \
  -A 40 packages/api/tests/tauri-alignment.test.mjs
```

本审计另用了两个一次性的枚举脚本（枚举 `@tauri-apps/api` 2.12.4 的导出与类方法、与 `@orielweb/api` 做集合差），
逻辑等价于上面的测试文件，但会打印**每个模块的参照数/我方数/缺口数**，便于在 CI 日志里看规模。

### 8.2 指标计算明细

| 模块 | 参照导出数 | 我方导出数 | 共有 | 缺失 | 独有 |
|---|---|---|---|---|---|
| `core` | 11 | 3 | 3 | 8 | 0 |
| `event` | 5 | 7 | 5 | 0 | 2 |
| `dpi` | 6 | 8 | 4 | 2 | 4 |
| `window` | 17 | 10 | 8 | 9 | 2 |
| `path` | 34 | 33 | 33 | 1 | 0 |
| `app` | 16 | 5 | 5 | 11 | 0 |
| `image` | 2 | 2 | 2 | 0 | 0 |
| `mocks` | 4 | 7 | 4 | 0 | 3 |
| **合计** | **95** | **75** | **64** | **31** | **11** |

`Window` 类方法：参照 84 / 我方 59 / 共有 57 / 缺失 27 / 独有 2。

### 8.3 未纳入包内对账的 Tauri 模块

`webview`（3 导出）、`webviewWindow`（3 导出）、`menu`（8 导出）、`tray`（1 导出 + 10 方法）。
其中 `tray` 在桥接层有同名同义实现，其余三个在本库无对应物。

### 8.4 审计未覆盖的部分

| 项 | 原因 |
|---|---|
| AOT / 裁剪兼容性 | 与 API 形状无关 |
| 三平台运行时行为差异 | 需真机；本文只引用 `docs/API.md` 与 `--selftest` 的既有结论 |
| `Velopack` 产物与 Tauri bundler 的安装语义差异 | 属分发层，不属 API 层 |
| Tauri 的 `tauri.conf.json` 全量字段映射 | 属配置层；本文只比较了 capability 一处 |
