# OrielWeb ↔ Tauri 2 API 对齐分析

> 分析基准：OrielWeb 0.3.x（`docs/API.md` @ 2026-10-17、`src/OrielWeb/Bridge/oriel-bridge.js`、`src/OrielWeb/Ipc/*`）
> 与 Tauri 2（`@tauri-apps/api` 2.x 源码：`core` / `event` / `app` / `path` / `window` / `webview` / `image`，
> `tauri` crate 2.x，官方插件清单）。
>
> 目的：把"后续主要工作是 API 层面对齐"拆成可执行清单——哪些已经一致、哪些只是形状不同、
> 哪些是结构性缺口、哪些明确不做。

## 判定口径

| 判定 | 含义 | 迁移代价 |
|---|---|---|
| **一致** | 名字与语义都能对上 | 只改 import |
| **语义差异** | 能力有，契约不同（单位 / 返回值 / 错误 / 事件形状） | 改调用点代码 |
| **缺失** | OrielWeb 没有对应能力 | 新增实现 |
| **不对齐** | Tauri 有，但按本库定位明确不做 | 无 |

---

## 0. 一页结论

### 0.1 已经对齐、不需要动的

| 能力 | 对齐程度 | 说明 |
|---|---|---|
| 托盘 | ✅ 完整 | 唯一做到"前后端同名同义"的能力：多托盘按 id、RGBA 图标对象、富事件、`oriel.tray` ↔ `@tauri-apps/api/tray` |
| 命令分发 | ✅ 模型级 | `[OrielCommand]` + 源生成 switch ↔ `#[tauri::command]` + `generate_handler!`；且零反射，比 Tauri 的宏更彻底 |
| 单实例 | ✅ 更完整 | Tauri 侧同为 Rust-only 插件；本库多出三态判定、`Build()` 期可问、退出码契约 |
| 图像解码（C#） | ✅ | `OrielImage` ↔ `tauri::image::Image`（RGBA + PNG/ICO 解码）；**页面侧没有对应物** |
| Shell / 通知 / 对话框 / 自启（C#） | ⚠️ 能力齐、无前端入口 | 对标 opener / notification / dialog / autostart 四个插件，但页面一行都调不到 |

### 0.2 结构性差距（不补则后面每项都要返工）

| # | 差距 | 后果 | 优先级 |
|---|---|---|---|
| S1 | **没有 npm 包与 TS 类型**，公开 API 只有注入的 `window.oriel` | 所有模块的形状无法与 Tauri 一致；前端不能 `import`；没有类型；无法做 codemod 迁移 | **P0** |
| S2 | **窗口没有 `label`** | `getCurrentWindow` / `getByLabel` / `emitTo` / 按窗口授权 / capability 的 `windows` 过滤全部做不了 | **P0** |
| S3 | **事件是裸 payload + 点分名**，无 `{event,id,payload}`、无 target、无 `once` | `event` 模块无法对齐；内置窗口事件（resize/move/focus/close-requested…）整体缺失 | **P0** |
| S4 | **几何只有 int 逻辑像素**，无 `Logical/Physical` 多态、无 `scaleFactor` | 所有 size/position API 形状不同；且 `Resize` 是外框尺寸、Tauri `setSize` 是客户区尺寸（语义相反） | **P0** |
| S5 | **错误是字符串**，无结构化 `Result` | 页面无法按错误码分支；Tauri 的 `Result<T,E>` 直接 reject 序列化后的 E | P1 |
| S6 | **无 Resource / rid 句柄表** | `Image` / `FileHandle` 这类"页面持句柄"的 API 无法实现 | P2 |
| S7 | **无内建命令命名空间**（只有 `win.*` / `tray.*`） | Tauri 的每个模块都是一族命令；现在无法按模块挂载/授权 | P1 |

### 0.3 能力缺口总览

| Tauri 模块 | 形态 | OrielWeb 现状 | 判定 | 优先级 |
|---|---|---|---|---|
| `core`（invoke） | 核心 | `oriel.invoke` | ⚠️ 语义差异 | P0 |
| `core`（Channel / Resource / convertFileSrc / isTauri） | 核心 | 无 | ❌ 缺失 | P1–P2 |
| `event` | 核心 | `oriel.on` + `postMessage` | ⚠️ 语义差异 + 缺内置事件 | **P0** |
| `window` | 核心 | `WebviewWindow` ~22 项 | ⚠️ 缺 ~60 项 | **P0** |
| `webview` | 核心 | 一窗口一 webview，无独立概念 | ⚠️ 仅 setZoom/背景色部分 | P2 |
| `webviewWindow` | 核心 | 无页面侧建窗 | ❌ 缺失 | P1 |
| `app` | 核心 | 只有 `oriel.version`（且是库版本） | ⚠️ 缺元信息/exit/show/hide | P1 |
| `path` | 核心 | 无 | ❌ 缺失 | **P1** |
| `image` | 核心 | C# 有 `OrielImage`，页面无 | ⚠️ 半缺 | P2 |
| `menu` | 核心 | C# 有完整菜单项模型，页面只有 `tray.setMenu` | ⚠️ 缺 JS 侧 + 应用菜单栏 | P2 |
| `dpi` | 核心 | 无 | ❌ 缺失 | **P0** |
| `mocks` | 核心 | 无 | ❌ 缺失 | P2 |
| `tray` | 内置 | `oriel.tray` | ✅ 一致 | — |
| `dialog` | 插件 | C# 有 4 个方法，页面只有 `win.pickFile` | ⚠️ 缺 ask/confirm/save/folder | P1 |
| `fs` | 插件 | 无 | ❌ 缺失 | P2（需决策） |
| `clipboard-manager` | 插件 | 无 | ❌ 缺失 | P1 |
| `global-shortcut` | 插件 | 无（`OrielAccelerator` 只解析菜单加速键文本） | ❌ 缺失 | P2 |
| `opener` | 插件 | C# 三个方法齐备 | ⚠️ 缺 JS 入口 | P1 |
| `shell` | 插件 | 明确不做子进程 | ⛔ 不对齐 | — |
| `notification` | 插件 | C# 有，无权限查询/点击回调 | ⚠️ 缺 JS 入口 | P1 |
| `http` / `websocket` / `upload` | 插件 | 无 | ⛔ 不对齐（页面 `fetch` 够用） | — |
| `os` | 插件 | 只有 `oriel.platform` | ⚠️ 缺 8 个查询 | P2 |
| `process` | 插件 | 无 | ❌ 缺失（exit 可并入 app） | P1 |
| `store` | 插件 | 无 | ❌ 缺失 | P2 |
| `window-state` | 插件 | 无 | ❌ 缺失 | P2 |
| `log` | 插件 | 只有 `ConsoleMessage` 单向转发 | ⚠️ 语义差异 | P2 |
| `sql` / `stronghold` / `localhost` / `deep-link` / `updater` | 插件 | 无 | ⛔ 不对齐 | — |
| `autostart` | 插件 | C# 齐备 | ⚠️ 缺 JS 入口 | P2 |
| `single-instance` | 插件（Rust-only） | C# 齐备 | ✅ 一致（不传 payload） | — |

---

## 1. 结构性差异（决定后续所有 API 的形状）

### 1.1 API 的载体：注入脚本 vs npm 包

Tauri 的公开 API 是一组 **ES 模块**（`@tauri-apps/api/core`、`/event`、`/window`…），每个模块是 `invoke` 的薄封装：

```
页面代码 → @tauri-apps/api/*（TS 类型 + 形状） → __TAURI_INTERNALS__.invoke → Rust 命令
```

OrielWeb 只有一层：注入的 `window.oriel`（`invoke` / `on` / `postMessage` / `tray` / `dragRegion`）。
它同时承担了"传输层"和"公开 API"两个角色，于是：

- 没有类型、没有模块划分、没有版本化的 API 契约；
- 前端无法 `import`，只能依赖一个全局对象；
- 任何形状调整都是**破坏性变更**（没有中间层可以兼容）。

**对齐动作**：新建 `packages/api`（`@orielweb/api`，TypeScript），**模块名与 Tauri 完全同名**，
只依赖 `window.oriel.invoke`。`window.oriel` 降级为"内部传输层"（等价于 Tauri 的 `__TAURI_INTERNALS__`），
文档不再主推。这样：

- 90% 的"形状差异"一次性消掉，**不需要改 C# 公开 API**；
- 前端迁移 = 一次 scope 替换（`@tauri-apps/api` → `@orielweb/api`），加少量语义修正；
- 类型、IDE 补全、tree-shaking、单元测试（配 `mocks`）都跟着到位。

这是**唯一一件"做了以后其他项成本减半"的事**，必须第一个做。

### 1.2 寻址：对象引用 vs label

Tauri 里一切都是"按 label 寻址"：`Window.getByLabel('main')`、`emitTo('settings', …)`、
capability 的 `"windows": ["main"]`、事件 target 的 `AnyLabel`。

OrielWeb 里窗口是 **C# 对象引用**，没有 label：

- 页面侧连"我是谁"都不知道（`oriel` 上没有 label）；
- 多窗口下页面无法把事件发给另一个窗口（只能 `invoke` 一个自定义命令，由宿主转发）；
- 能力配置无法按窗口区分（只能全应用一套）。

**对齐动作**（P0）：

| 侧 | 改动 |
|---|---|
| C# | `OrielWindowOptions.Label`（默认 `"main"`，重复抛异常）；`OrielApp.GetWindow(string label)`；`CreateWindow` 接受 label |
| 注入脚本 | `oriel.label`（本窗口 label） |
| 内建命令 | `win.*` 保持"默认作用于发起者"，新增可选 `label` 参数；新 `window.*` 族必须显式带 label（缺省=发起者） |
| 能力 | 允许 `(windowLabel, command)` 二元组；不写 label = 全窗口 |

注意：`win.*` 现在是**隐式**按发起者路由——这已经等价于"当前 label"，把它显式化即可，向后兼容。

### 1.3 事件模型

| 维度 | Tauri 2 | OrielWeb |
|---|---|---|
| 订阅 | `listen(name, handler, {target})` → `Promise<UnlistenFn>` | `oriel.on(name, handler)` → 同步返回退订函数 |
| 一次性 | `once(name, handler)` | 无 |
| 页面 → 全体 | `emit(name, payload)`（Rust 侧 `app.listen` 也能收到） | `postMessage(name, payload)` 只到宿主 `MessageReceived` |
| 定向发送 | `emitTo(target, name, payload)` | 无 |
| 回调参数 | `{ event, id, payload }` | **裸 payload**（形状不统一：`maximized` 是 bool、`theme.changed` 是字符串、`navigation.*` 是对象） |
| 内置事件 | `tauri://resize` `move` `close-requested` `destroyed` `focus` `blur` `scale-change` `theme-changed` `window-created` `drag-enter/over/drop/leave` | 只有 `navigation.starting` / `navigation.completed` / `theme.changed` / `maximized` / `tray.event` |

**对齐动作**（P0）：

1. 事件 payload 统一包一层 `{ event, id, payload }`（**新增** `oriel.onTauriEvent` 或由 JS 包在内部包装，不改旧通道）；
2. 补内置窗口事件：`resize` / `move` / `focus` / `blur` / `close-requested` / `destroyed`；
   `resize`/`move` 是 Tauri 的刚需（布局重排、窗口吸附），目前**完全没有**；
3. `close-requested` 要能"阻止关闭"：Tauri 在回调里 `api.preventDefault()`，OrielWeb 只能在 C# 的 `Closing` 里
   置 `Cancel`。JS 侧需要一个往返（`invoke('window.preventClose')` 或事件回执），否则页面无法接管关闭；
4. `once` / `emit` / `emitTo` 在 JS 包内实现（`emit` 落到一个新内建命令 `event.emit`，由宿主广播到所有窗口）。

> 注意 `tray.event` 已经是"专属订阅集合"，不与通用事件混用——这一点与 Tauri 的 `TrayIconOptions.action` 一致，
> 保持不变。

### 1.4 命令命名空间与权限标识

| 维度 | Tauri 2 | OrielWeb |
|---|---|---|
| 命令名 | `plugin:fs\|read_text_file`（插件命名空间 + `\|` + 命令） | 扁平：`todo.add`、`win.close`、`tray.new` |
| 权限标识 | 每命令一个：`core:window:allow-set-title`、`fs:allow-read-text-file` | 命令名前缀通配：`Allow("todo.*")` |
| 授权载体 | `capabilities/*.json`（`windows` / `platforms` / `remote.urls` / `permissions` + scope） | C# 代码：`UseCapabilities(c => c.Allow(...).Deny(...).AllowOrigin(...))` |
| scope | 路径 glob / URL 前缀（fs、http 必需） | 无（目前没有需要 scope 的能力） |
| 运行时查询 | `checkPermissions` / `requestPermissions` | 无（只有宿主侧 `IpcRejected` 观测） |
| 默认姿态 | capability 未声明即拒绝；`core:*:default` 放行一批只读命令 | 未配置时 Debug 全放行 / Release 全拒；`win.*`、`tray.*` 始终放行 |

**判断**：Tauri 的权限系统是为"多插件生态 + 第三方扩展"设计的，本库是单库内建命令，
**不建议照搬 JSON capability 文件与 scope 机制**。但对齐需要三件事：

1. **命名空间前缀**：内建命令按模块挂 `core:` / `window:` / `webview:` / `app:` / `path:` / `dialog:` / `tray:`，
   这样未来要加扩展时前缀已就位，且能力表能按命名空间批量授权；
2. **按窗口授权**（依赖 S2 的 label）；
3. **运行时查询**：页面侧 `checkPermissions` 的等价物（哪怕只返回"这个命令名在不在 allow 名单里"）。

> ⚠️ **安全红线**：`win.*` 现在"始终放行"是**刻意的后门**（无边框窗口没有系统标题栏，拒掉就关不掉）。
> 新增的 `window.*` 族**不能沿用这个后门**——`window.create` / `window.setIcon` / `window.setSkipTaskbar`
> 这些是真正的能力。后门只保留标题栏必需的那一组：`minimize` / `toggleMaximize` / `close` /
> `drag*` / `toggleFullscreen` / `toggleOnTop`，以及 `pickFile` / `contextMenu`。

### 1.5 几何类型

| 维度 | Tauri 2 | OrielWeb |
|---|---|---|
| 尺寸参数 | `LogicalSize` / `PhysicalSize` / `Size`（带 type 字段的多态 JSON） | `int width, int height`（逻辑像素） |
| 位置参数 | `LogicalPosition` / `PhysicalPosition` / `Position` | `int x, int y`（逻辑像素、左上角原点） |
| 查询 | `innerSize` / `outerSize` / `innerPosition` / `outerPosition` / `scaleFactor` | **全缺**（只有 `IsMaximized` / `IsVisible`） |
| 尺寸语义 | `setSize` = **客户区**（inner） | `Resize` = **整窗外框**（`SetWindowPos` 直接设窗口宽高） |
| 坐标语义 | 区分 inner / outer | 只有 outer（`MoveTo` 移动窗口左上角） |

**这是最容易"看起来一样、实际差一圈"的地方**：迁移后同样的 `setSize(800,600)` 会得到比预期小一圈的客户区
（差的是边框与标题栏）。对齐动作：

- C# 补 `double ScaleFactor`（三平台都有现成值：`GetDpiForWindow` / `GdkWindow.get_scale_factor` / `NSWindow.backingScaleFactor`）；
- C# 把 inner/outer 显式化：`Resize` 保留为 outer，新增 `SetInnerSize`（或让 `Resize` 改语义 + 破坏性说明）；
- JS 包实现 `dpi` 模块的 `LogicalSize` / `PhysicalSize` / `LogicalPosition` / `PhysicalPosition`（纯形状包装，
  传输仍是数字 + `type` 字段），`setSize`/`setPosition` 接受多态并换算。

### 1.6 错误模型

| 维度 | Tauri 2 | OrielWeb |
|---|---|---|
| 命令返回 | `Result<T, E>`，`E: Serialize` | 任意返回值 / 抛异常 |
| 失败到达页面 | reject 一个**序列化后的 E**（可以是对象、字符串、错误码结构） | reject `new Error(string)`；异常细节被净化 |
| 错误码 | 由 E 自己定义 | 无（`OrielIpcException` 只有 `Message`） |
| 超时 | 无内置超时（Promise 挂着） | 默认 30s（`oriel.timeout`，全局） |

**对齐动作**（P1）：

- `OrielIpcException` 增加 `Code`（可选字符串）与 `Data`（可选 JSON），JS 侧 reject 的 `Error` 上挂 `code` / `data`；
- `oriel.invoke(name, args, { timeout })` 支持 **per-call** 超时（现在的 `oriel.timeout` 是全局，一个慢命令会拖累所有调用）；
- 保留 30s 默认值，但文档写清"与 Tauri 不同"——迁移长任务时必须显式放宽。

### 1.7 句柄与资源表（Resource）

Tauri 的 `Image` / `FileHandle` 都是 `Resource` 子类：宿主侧持对象，页面侧持 `rid`，`close()` 释放。
OrielWeb 的 `OrielImage` 是 C# 值对象，页面侧只能传**路径**（`tray.setIcon(iconPath)`）。

后果：页面拿到的图片（`fetch` 下载的字节、canvas 生成的）无法直接用作托盘/窗口图标，必须先落盘。

**对齐动作**（P2）：在宿主侧引入 rid 表（`ConcurrentDictionary<int, object>` + 每窗口可见性），实现
`image.new(rgba,w,h)` / `fromBytes` / `fromPath` / `rgba` / `size` / `close`，以及 `Image` 类。
注意 AOT 与内存：句柄必须显式 `close()`，窗口销毁时回收该窗口创建的句柄。

### 1.8 插件模型（明确不对齐）

Tauri 的 `Builder::plugin()` + 独立 crate + npm 包 + `plugin:` 前缀，是"第三方扩展生态"的地基。
本库定位是**单库、零依赖、AOT**，引入插件 ABI 会直接摧毁这些卖点。

**结论**：不做插件模型；只保留命名空间前缀约定（见 1.4），为将来留位置。

---

## 2. 逐模块对照

> 记号：✅ 一致 ｜ ⚠️ 语义差异 ｜ ❌ 缺失 ｜ ⛔ 不对齐 ｜ `—` 不适用

### 2.1 `core`

| Tauri 2 | OrielWeb | 判定 | 说明 / 动作 |
|---|---|---|---|
| `invoke(cmd, args?, options?)` | `oriel.invoke(name, args)` | ⚠️ | 参数命名不转换（Tauri 是 camelCase → Rust snake_case）；无 per-call 选项；错误是字符串 |
| `Channel<T>` | 无 | ❌ | 流式回传（下载进度、日志流）。fs/http 落地前先不做；可用"事件 + 自造 id"过渡 |
| `Resource` | 无 | ❌ | 见 1.7 |
| `convertFileSrc(path, protocol?)` | 无 | ❌ | **高价值**：没有 asset 协议，`<img src>` / `<video>` 加载本地文件做不到。见 §4.3 P1 |
| `isTauri()` | 无（可用 `!!window.oriel`） | ⚠️ | JS 包一行实现 |
| `transformCallback` | 内部实现 | — | 不暴露 |
| `addPluginListener` | 无 | ⛔ | 与插件模型绑定 |
| `checkPermissions` / `requestPermissions` | 无 | ❌ | 见 1.4；只做"查询"不做"请求"（权限在构建期定，运行期不弹窗） |

### 2.2 `event`

| Tauri 2 | OrielWeb | 判定 | 说明 |
|---|---|---|---|
| `listen` | `oriel.on` | ⚠️ | 同步返回退订函数；回调是裸 payload |
| `once` | 无 | ❌ | JS 包内实现 |
| `emit` | 无（`postMessage` 只到宿主） | ❌ | 需新内建 `event.emit`（宿主广播到所有窗口） |
| `emitTo` | 无 | ❌ | 依赖 label |
| `TauriEvent`（15 个内置名） | 5 个点分名 | ⚠️ | 缺 `resize`/`move`/`focus`/`blur`/`close-requested`/`destroyed`/`scale-change`/`drag-enter/over/leave` |
| 事件对象 `{event,id,payload}` | 裸值 | ⚠️ | JS 包统一包装 |

### 2.3 `window`（差距最大的一块）

Tauri `Window` 约 80 个方法/属性（21 个 getter、51 个 setter、7 个 `on*` 监听器、4 个事件方法）；
OrielWeb `WebviewWindow` 的窗口自身能力约 22 个。

**生命周期与查询**

| Tauri 2 | OrielWeb | 判定 |
|---|---|---|
| `getCurrentWindow()` | 无（无 window 模块） | ❌ |
| `getAllWindows()` | C# `app.Windows` | ⚠️ 无 JS 入口 |
| `Window.getByLabel()` / `getFocusedWindow()` | 无 | ❌ |
| `show` / `hide` / `close` / `destroy` / `setFocus` | `Show` / `Hide` / `Close` / `Focus` | ⚠️ 缺 `destroy`（强制关闭、绕过 `Closing`） |
| `isFocused` / `isMinimized` / `isDecorated` / `isResizable` / `isMaximizable` / `isMinimizable` / `isClosable` / `isAlwaysOnTop` / `isFullscreen` / `isEnabled` | 只有 `IsMaximized` / `IsVisible` | ❌ |
| `title`（getter） | 只有 `TitleChanged` 事件 | ❌ |

**几何**

| Tauri 2 | OrielWeb | 判定 |
|---|---|---|
| `setSize` / `setMinSize` / `setMaxSize` / `setSizeConstraints` | `Resize` / `SetMinSize` | ⚠️ 无 max、无 constraints；语义相反（inner vs outer） |
| `setPosition` / `center` | `MoveTo` / `Center` | ⚠️ 只有 outer |
| `innerSize` / `outerSize` / `innerPosition` / `outerPosition` / `scaleFactor` | 无 | ❌ |
| `currentMonitor` / `primaryMonitor` / `availableMonitors` / `monitorFromPoint` / `cursorPosition` | 无 | ❌ 多显示器场景刚需 |

**修饰与状态**

| Tauri 2 | OrielWeb | 判定 |
|---|---|---|
| `setDecorations`（运行期去/加边框） | 只有建窗时 `WithFrameless` | ❌ 全屏切换去边框的常见需求做不了 |
| `setAlwaysOnTop` / `setAlwaysOnBottom` | `SetOnTop` | ⚠️ 缺 bottom |
| `setMaximizable` / `setMinimizable` / `setClosable` / `setEnabled` / `setFocusable` | 无 | ❌ |
| `setSkipTaskbar` / `setVisibleOnAllWorkspaces` | 无 | ❌ |
| `setContentProtected`（防截屏） | 无 | ❌ 工业场景有价值 |
| `setShadow` / `setEffects` / `clearEffects`（mica/acrylic/vibrancy） | 无 | ❌ P2 |
| `setTitleBarStyle` / `setTheme` | 无 | ❌ |
| `setBackgroundColor` | 只有建窗时 `WithBackgroundColor` | ⚠️ |

**图标 / 任务栏**

| Tauri 2 | OrielWeb | 判定 |
|---|---|---|
| `setIcon(Image)` | 只有建窗时 `WithIcon(path)` | ❌ |
| `setProgressBar` / `setBadgeCount` / `setBadgeLabel` / `setOverlayIcon` | 无 | ❌ 长任务进度是工控刚需 |
| `requestUserAttention`（任务栏闪烁） | 无 | ❌ |

**光标与拖动**

| Tauri 2 | OrielWeb | 判定 |
|---|---|---|
| `startDragging` | `BeginDrag` / `BeginDragStreaming` / `DragTo` / `EndDrag` | ✅（且比 Tauri 更细：流式拖动 + 库接管标题栏） |
| `startResizeDragging(direction)` | 无（Windows 已内建边缘热区，但没有 API） | ❌ |
| `setCursorIcon` / `setCursorGrab` / `setCursorVisible` / `setCursorPosition` | 无 | ❌ |
| `setIgnoreCursorEvents`（点击穿透） | 无 | ❌ 悬浮层场景 |

**事件**

| Tauri 2 | OrielWeb | 判定 |
|---|---|---|
| `onResized` / `onMoved` | 无 | ❌ |
| `onCloseRequested` | `Closing`（C#） | ⚠️ 页面无法接管 |
| `onFocusChanged` | 无 | ❌ |
| `onScaleChanged` | 无 | ❌ |
| `onThemeChanged` | `app.ThemeChanged`（应用级） | ⚠️ 无窗口级 |
| `onDragDropEvent`（enter/over/drop/leave） | 只有 `FileDropped`（drop） | ⚠️ |

**创建**

| Tauri 2 | OrielWeb | 判定 |
|---|---|---|
| `new WebviewWindow(label, options)` | 只有 C# `app.CreateWindow` | ❌ 页面无法开新窗口（`window.open` 也不接管，见 ROADMAP） |

### 2.4 `webview` / `webviewWindow`

| Tauri 2 | OrielWeb | 判定 |
|---|---|---|
| `getCurrentWebview` / `getAllWebviews` / `getByLabel` | 一窗口一 webview，无独立概念 | ⛔ 不对齐（多 webview 不做） |
| `setZoom(scaleFactor)` | 只有建窗时 `Zoom` 开关（禁用手势缩放） | ❌ 无程序化缩放级别 |
| `setBackgroundColor` | 建窗时可设 | ⚠️ |
| `clearAllBrowsingData` | 无 | ❌ 排障刚需（清缓存/Cookie 后重登） |
| `reparent` / `setAutoResize` / `setSize` / `setPosition` | 无 | ⛔ 多 webview 相关 |
| `WebviewWindow` 类（含 Window 全部方法） | 无 | ❌ 随 §2.3 一起补 |

### 2.5 `app`

| Tauri 2 | OrielWeb | 判定 | 说明 |
|---|---|---|---|
| `getVersion()` | 无 | ❌ | 应用版本 |
| `getTauriVersion()` | `oriel.version` | ⚠️ | **注意**：`oriel.version` 是库版本，对应 `getTauriVersion`，不是 `getVersion` |
| `getName()` / `getIdentifier()` / `getBundleType()` | 无 | ❌ | 程序集名 / 应用 id / 产物类型 |
| `show()` / `hide()`（macOS） | 无 | ❌ | 把整个应用（Dock 图标）隐藏/唤回 |
| `setTheme(theme)` | 无（只读 `Theme`） | ❌ | 应用级主题覆盖（Tauri 支持强制 dark/light） |
| `setDockVisibility`（macOS） | 无 | ❌ | 隐藏 Dock 图标（菜单栏常驻应用） |
| `defaultWindowIcon()` | 无 | ❌ | 取回默认窗口图标（`Image`） |
| `exit(code)` | C# `Quit()` | ⚠️ 无 JS 入口 |
| `supportsMultipleWindows()` | 恒 true | ⚠️ 可省 |
| `onBackButtonPress` / `fetchDataStoreIdentifiers` / `removeDataStore` | 无 | ⛔ 移动端 / macOS 数据存储 |

### 2.6 `path`

**全缺**。Tauri 有 24 个目录函数（`appDataDir` / `appConfigDir` / `appLocalDataDir` / `appCacheDir` / `appLogDir` /
`homeDir` / `desktopDir` / `documentDir` / `downloadDir` / `pictureDir` / `videoDir` / `audioDir` / `fontDir` /
`templateDir` / `publicDir` / `runtimeDir` / `tempDir` / `executableDir` / `resourceDir` / `resolveResource` …）、
7 个路径操作（`resolve` / `join` / `normalize` / `dirname` / `basename` / `extname` / `isAbsolute`）、
2 个常量（`sep` / `delimiter`）。

C# 侧只有内部的 `OrielPathResolver`（相对路径按 exe 目录解析），不对外。

**这是"成本最低、收益最高"的一块**：纯计算 + 平台目录查询，不碰 webview、不碰三平台后端，
且**没有它，fs / asset 协议 / 配置文件读写都无从谈起**（前端拿不到"该往哪写"）。

### 2.7 `image`

| Tauri 2 | OrielWeb | 判定 |
|---|---|---|
| `Image.new(rgba,w,h)` / `fromBytes` / `fromPath` | C# `OrielImage` 有等价物 | ⚠️ 页面无 |
| `rgba()` / `size()` / `close()` | C# `Rgba` / `Width` / `Height` | ⚠️ 页面无 |
| `transformImage(jsImage)`（路径/字节/句柄三态） | 只有路径 | ⚠️ |

### 2.8 `menu`

C# 侧模型相当完整：`OrielMenuItem`（`Id` / `Label` / `Role` / `Accelerator` / `IsSeparator` / `Enabled` / `Checked` / `Items`）、
17 个 `OrielMenuRole`、`OrielAccelerator.TryParse`。缺的是：

| Tauri 2 | OrielWeb | 判定 |
|---|---|---|
| `Menu.new` / `MenuItem` / `Submenu` / `CheckMenuItem` / `PredefinedMenuItem`（JS 侧构造） | 无（`tray.setMenu` 收裸 JSON 算半个） | ❌ |
| `Menu.setAsAppMenu()`（应用菜单栏） | **完全没有菜单栏** | ❌ Windows/Linux 上缺一整块 |
| `Menu.default()`（系统默认菜单） | 无 | ❌ |
| `menu.popup(at?, window?)` | `window.ShowContextMenu`（无 JS 入口） | ⚠️ |
| `IconMenuItem`（带图标的菜单项） | 无 | ❌ |

### 2.9 `tray`（唯一完整对齐项）

| Tauri 2 | OrielWeb | 判定 |
|---|---|---|
| `TrayIcon.new({icon, menu, tooltip, title, iconAsTemplate, menuOnLeftClick, action})` | `oriel.tray.new({iconPath, tooltip, title, iconAsTemplate, menuOnClick})` | ⚠️ 仅命名差异（`menuOnLeftClick` vs `menuOnClick`）、图标只收路径 |
| `getById` / `removeById` | ✅ | ✅ |
| `setIcon` / `setMenu` / `setTooltip` / `setTitle` / `setVisible` / `setIconAsTemplate` / `close` | ✅ | ✅ |
| `setTempDirPath` | 无 | — 不需要（GtkStatusIcon 直吃 pixbuf） |
| `TrayIconEvent`（type/button/buttonState/position/rect） | ✅ 同构 | ✅（`rect` 三平台均未提供） |

**这块是后续所有模块的样板**：先定义前端形状，再实现后端命令族，再用 `--selftest` 双向取证。

### 2.10 `dialog`

| Tauri 2 | OrielWeb | 判定 |
|---|---|---|
| `open({multiple, directory, filters, defaultPath, title})` | C# `ShowOpenFileDialog(options)` + `ShowFolderDialog` | ⚠️ 能力齐，页面只有 `win.pickFile`（单选、无 directory 标志） |
| `save({filters, defaultPath, canCreateDirectories})` | C# `ShowSaveFileDialog(options)` | ⚠️ 无 JS 入口 |
| `message(msg, {title, kind, buttons})` | C# `ShowMessage(text, title, icon)` | ⚠️ 只有单按钮 + 图标 |
| `ask(msg, {title, kind})` → bool | 无 | ❌ 原生是否框 |
| `confirm(msg, {title, kind})` → bool | 无 | ❌ |
| 过滤器形状 `{name, extensions: ['png']}` | C# `{Name, Patterns: ['png']}` | ⚠️ JS 侧要对齐 Tauri 形状 |

### 2.11 `fs`

**全缺**。Tauri 的 fs 是一整套：读写（文本/二进制/追加）、`exists`、`mkdir`、`remove`、`rename`、`copyFile`、
`readDir`、`stat`/`lstat`、`truncate`、`watch`、`FileHandle` + `SeekMode`，以及 **`BaseDirectory` 作用域**
（`$APPDATA` / `$RESOURCE` / `$DOWNLOAD` …）+ 路径 glob 的 allow/deny scope。

**决策点**（需要在动手前定）：

| 方案 | 优点 | 代价 |
|---|---|---|
| A. 不做 fs，应用用 `[OrielCommand]` 自己实现 | 零 scope 复杂度；保持"webview 核心库"定位 | 每个应用重写一遍；前端无法直接用；无 scope 约束意味着应用自己也要写一遍路径校验 |
| B. 做受 scope 约束的最小 fs（推荐） | 前端可直用；scope 集中在一处（比每个应用各写一遍更安全） | 要引入 scope 系统 + `BaseDirectory` + 三平台文件 API |

若选 B，建议范围：`readTextFile` / `readFile` / `writeTextFile` / `writeFile` / `exists` / `mkdir` / `remove` /
`readDir` / `stat` + `BaseDirectory` + allow/deny glob。**不做** `watch`、`FileHandle` 随机读写、`copyFile`/`rename`（先留空）。

### 2.12 `clipboard-manager`

**全缺**。Tauri：`writeText` / `readText` / `writeImage` / `readImage`（另有 HTML）。

工控场景"把某个值复制走"很常用，而右键菜单的复制只覆盖选中文本。建议 P1 做 **text 两个函数**
（Windows `OpenClipboard`/`SetClipboardData`、macOS `NSPasteboard`、Linux `gtk_clipboard_*`），图像与 HTML 后置。

### 2.13 `global-shortcut`

**全缺**。`OrielAccelerator` 只解析菜单加速键**文本**，不注册全局热键。

Tauri：`register` / `registerAll` / `unregister` / `unregisterAll` / `isRegistered`。
三平台分别落到 `RegisterHotKey` / `NSEvent addGlobalMonitorForEvents` / X11 `XGrabKey`（Wayland 下不可用，是平台事实）。
建议 P2——且必须在文档里写明 **Wayland 不支持**，否则又是一个"配了没反应"。

### 2.14 `opener` / `shell`

| Tauri 2 | OrielWeb | 判定 |
|---|---|---|
| `openUrl(url)` | `app.OpenExternal(url)` | ⚠️ 无 JS 入口；scheme 白名单默认 `http/https/mailto`（比 Tauri 的 scope 更简单也更安全） |
| `openPath(path)` | `app.OpenWithDefaultApp(path)` | ⚠️ 同上 |
| `revealItemInDir(path)` | `app.RevealInFileManager(path)` | ⚠️ 同上 |
| `Command.create` / `sidecar` / `Child` | 无 | ⛔ 明确不做（能力沙箱范畴） |

### 2.15 `notification`

| Tauri 2 | OrielWeb | 判定 |
|---|---|---|
| `sendNotification({title, body, icon, sound})` | `app.ShowNotification(options)` | ⚠️ 无 JS 入口；无 sound |
| `isPermissionGranted()` / `requestPermission()` | 无 | ❌ 页面无法判断"为什么没弹出来" |
| `onAction` / `onNotificationReceived` | 无（点击回调已整体移除） | ⛔ 平台能力限制，保持现状 |
| 通知分组/进度/持续通知 | 无 | ❌ P2 |

### 2.16 `http` / `websocket` / `upload`

**⛔ 不对齐**。理由：页面有 `fetch`；Tauri 的 http 插件价值在"绕过 CORS + Rust 侧 scope + 忽略证书"，
后两者对工业内网场景其实有用，但代价是引入整套 scope 与 TLS 配置。若要，走 C# 命令自定义（应用自己掌控证书策略）。

### 2.17 `os` / `process`

| Tauri 2 | OrielWeb | 判定 |
|---|---|---|
| `platform()` | `oriel.platform` | ✅ |
| `version()` / `arch()` / `family()` / `type()` / `locale()` / `hostname()` / `exeExtension()` / `eol()` | 无 | ❌ P2（纯查询，成本低） |
| `exit(code)` | C# `Quit()` | ⚠️ 并入 `app.exit` |
| `relaunch()` | 无 | ❌ P2（重启进程：`Process.Start(exe)` + `Quit`） |

### 2.18 `store` / `window-state` / `log` / `sql` / `deep-link` / `autostart` / `single-instance`

| 插件 | Tauri 2 | OrielWeb | 判定 |
|---|---|---|---|
| `store` | `load(path)` + `get/set/save/delete/entries/onChange` | 无 | ❌ P2（工控"记住参数"刚需；也可让应用用自定义命令） |
| `window-state` | 自动保存/恢复位置、大小、最大化 | 无 | ❌ P2（"记住上次布局"） |
| `log` | 级别 + 文件 + 滚动 + `attachConsole` | 只有 `ConsoleMessage` 单向转发 | ⚠️ P2 |
| `sql` | sqlx 封装 | 无 | ⛔ 不对齐 |
| `deep-link` | `getCurrent` / `onOpenUrl` | 无（单实例也不传 payload） | ⛔ 不对齐（移动端为主） |
| `autostart` | `enable/disable/isEnabled` | C# 齐备 | ⚠️ 缺 JS 入口（P2） |
| `single-instance` | Rust-only，`on_second_instance(args)` | C# 齐备（三态判定） | ✅ 一致；差异：**不传 payload**（见 §3） |

### 2.19 `mocks`

Tauri 提供 `mockIPC` / `mockWindows` / `mockConvertFileSrc` / `clearMocks`，让前端能在 Node 里跑单测。
OrielWeb 没有 JS 包，也就没有这个问题——**但 JS 包一建，`mocks` 必须同步提供**，
否则前端测试只能起真实窗口，CI 里跑不动。

---

## 3. 语义差异清单（迁移时会踩的点）

| # | 项 | Tauri 2 | OrielWeb | 风险 |
|---|---|---|---|---|
| 1 | `invoke` 参数命名 | JS camelCase → Rust snake_case 自动转换 | 原样透传，字段名由命令自己解析 | 中：JS 包统一 camelCase 即可 |
| 2 | `invoke` 失败值 | 序列化后的 `E`（可为对象） | `Error(string)`，细节被净化 | 中 |
| 3 | 超时 | 无 | 默认 30s 全局 | **高**：长任务会被静默超时；必须 per-call |
| 4 | 事件 payload | `{event, id, payload}` | 裸值（且形状不统一） | 高 |
| 5 | `listen` 返回 | `Promise<UnlistenFn>` | 同步函数 | 低（`await` 一个非 Promise 也成立） |
| 6 | **窗口尺寸语义** | `setSize` = 客户区 | `Resize` = 整窗外框 | **高**：迁移后客户区小一圈 |
| 7 | 位置语义 | inner / outer 分开 | 只有 outer | 中 |
| 8 | `close` vs `destroy` | `close` 可被阻止、`destroy` 不可 | 只有可阻止的 `Close` | 中：无法强关 |
| 9 | **关闭即退出** | 最后一个窗口关闭**不**退出（需自己 `exit`） | 全部窗口关闭后消息循环结束 = 退出 | **高**：迁移后"关窗即退出"行为相反 |
| 10 | 内置事件名 | `tauri://resize` 等 | `navigation.starting` 等点分名 | 低（JS 包映射） |
| 11 | 命令名 | `plugin:fs\|read_text_file` | 扁平 `todo.add` | 低（JS 包隐藏） |
| 12 | 权限默认 | capability 未声明即拒 | Debug 全放行 / Release 全拒 | 中：Debug 下的行为差异会掩盖漏配 |
| 13 | 图标入参 | 路径 / 字节 / `Image` 三态 | 只有路径 | 中 |
| 14 | 托盘选项名 | `menuOnLeftClick` | `menuOnClick` | 低（改名即可，但属破坏性） |
| 15 | 对话框过滤器 | `{name, extensions}` | `{Name, Patterns}` | 低（JS 包转换） |
| 16 | `oriel.version` | `getTauriVersion()` | 库版本 | 低，但**容易误当应用版本用** |

---

## 4. 对齐方案

### 4.1 目标形态

```
页面代码
  ↓ import
@orielweb/api/*            ← 新增：TS 包，模块名与 Tauri 完全同名
  ↓ window.oriel.invoke    ← 现有唯一通道，降级为传输层（≈ __TAURI_INTERNALS__）
内建命令族 core: / window: / webview: / app: / path: / dialog: / tray: / event:
  ↓
三平台后端（现有）
```

**关键判断**：对齐的落点在 **JS 包 + 内建命令族**，不是重写 C# 公开 API。
C# 侧的 `WebviewWindow` 方法继续作为"宿主 API"（对标 Rust 的 `Window`），
页面侧的 `window.setTitle()` 由 JS 包转成 `invoke('window.setTitle', {label, title})`。

### 4.2 命名与兼容策略

| 项 | 策略 |
|---|---|
| 包名 / 模块名 | `@orielweb/api/core` / `/event` / `/window` / `/webviewWindow` / `/app` / `/path` / `/image` / `/menu` / `/tray` / `/dpi` / `/mocks`，插件类模块用 `/dialog` / `/fs` / `/clipboard` / `/opener` / `/notification` / `/os` / `/process` / `/autostart` / `/globalShortcut` / `/store` / `/windowState` / `/log` |
| 旧 API | `window.oriel` 保留（传输层 + 兼容），文档标为"底层，不推荐直接用" |
| `win.*` | 保留为 legacy alias；新 `window.*` 族**必须过能力门禁**（见 1.4 安全红线） |
| 破坏性变更 | 只在 `@orielweb/api` 的首个稳定版之前做（`Resize` 语义、`menuOnClick` 改名、事件 payload 包装） |

### 4.3 分阶段

#### P0 — 地基（不做则后续全部返工）

| # | 事项 | 改动面 | 验收 |
|---|---|---|---|
| P0-1 | `@orielweb/api` 骨架：`core.invoke` / `isTauri` / `dpi` 类型 + `mocks` | 新增 TS 包；C# 不改 | `node --test` 跑通 mock；demo 页面改用它 |
| P0-2 | 窗口 `label`：`OrielWindowOptions.Label`、`app.GetWindow(label)`、`oriel.label` | C# + 注入脚本 + 内建命令参数 | `--selftest multiwindow` 扩展：按 label 取窗口 |
| P0-3 | 事件模块：`listen/once/emit/emitTo` + 事件对象 + 内置 `resize/move/focus/blur/close-requested/destroyed` | 三平台后端（各自已有原生事件源）+ JS 包 | `--selftest event`：八种事件逐条断言 |
| P0-4 | `window` 模块基础：`getCurrentWindow` / `getAllWindows` / `getByLabel` + 现有 22 项包装 | JS 包 + 新 `window.*` 命令族 | 同一份页面在 demo 里跑通 |
| P0-5 | 几何：`scaleFactor` + `Logical/Physical` 多态 + `inner/outer` 语义拆分 | C# 三平台 + JS 包 | `--selftest geometry`：2x 缩放下尺寸/位置逐像素断言 |

#### P1 — 高频能力

| # | 事项 | 说明 |
|---|---|---|
| P1-1 | `path` 模块（24 目录 + 7 操作 + 2 常量） | 纯计算，成本最低；fs/配置读写的前置 |
| P1-2 | `convertFileSrc` + asset 协议 | 页面加载本地图片/媒体；需 scope（只放行允许的目录） |
| P1-3 | `dialog` 全量（`open/save/message/ask/confirm` + JS 入口） | C# 已有 4 个，补 `ask/confirm` 与形状转换 |
| P1-4 | `app` 元信息 + `exit` + `show/hide` | `getName/getVersion/getIdentifier/getBundleType` |
| P1-5 | `opener` / `notification` / `autostart` 的 JS 入口 | C# 已有，只差包装 |
| P1-6 | `clipboard`（text） | 三平台剪贴板 API |
| P1-7 | 错误模型：`code` / `data` + per-call `timeout` | 影响所有命令的 reject 形状 |

#### P2 — 进阶

| # | 事项 |
|---|---|
| P2-1 | 窗口修饰：`setDecorations` / `setSkipTaskbar` / `setContentProtected` / `setAlwaysOnBottom` / `setMaximizable` 等 |
| P2-2 | 任务栏：`setProgressBar` / `setBadgeCount` / `setOverlayIcon` / `requestUserAttention` |
| P2-3 | 显示器查询：`currentMonitor` / `availableMonitors` / `monitorFromPoint` / `cursorPosition` |
| P2-4 | `Image` 资源表（rid）+ `tray.setIcon(Image)` / `window.setIcon(Image)` |
| P2-5 | 页面侧建窗：`new WebviewWindow(label, options)` |
| P2-6 | `fs` 最小子集（若选方案 B）+ scope |
| P2-7 | `global-shortcut`（注明 Wayland 不支持） |
| P2-8 | `os` 查询 / `process.relaunch` / `store` / `window-state` / `log` / `menu` JS 侧 + 应用菜单栏 |

### 4.4 安全模型随对齐演进

对齐会**扩大**页面可触达的宿主能力面（窗口创建、图标、任务栏、fs…），必须同步收紧：

1. `win.*` 的"始终放行"后门**不扩散**到 `window.*`（见 1.4）；
2. 新命令族默认**不在** allow 名单里 —— 与应用命令同一套 `Allow/Deny`，不做"库内建就放行"；
3. 引入 `fs`/`convertFileSrc` 时必须有 scope（路径 glob），否则等于把整个文件系统交给页面；
4. 每新增一族命令，`IpcRejected` 要能报出"哪一层拒的"（现有三层：Source/Token/Command，加 `Scope` 第四层）。

### 4.5 明确不对齐

| 项 | 理由 |
|---|---|
| 移动端（Android / iOS） | 桌面 webview 抽象与移动端生命周期是两套 |
| 插件模型 / `plugin:` 前缀 / 插件 ABI | 与"单库零依赖 AOT"定位冲突 |
| `tauri.conf.json` 配置清单 | C# 代码配置是优势（编译期检查、无清单漂移） |
| 多 webview（`Webview.reparent` / `setAutoResize`） | 一窗口一 webview 是当前抽象的基础 |
| `shell` 子进程 / PTY | 能力沙箱范畴，见 ROADMAP |
| `http` / `websocket` / `upload` / `sql` / `stronghold` | 页面 `fetch` + 应用命令足够 |
| `updater` | 交给 Velopack（应用侧接入待做） |
| `deep-link` | 桌面场景弱需求，且与单实例 payload 一起才有意义 |

---

## 5. 验收与回归

对齐的最终验收只有一条：**同一份前端代码，在 Tauri 2 与 OrielWeb 下都能跑**。

| 层次 | 手段 |
|---|---|
| 形状一致 | `@orielweb/api` 的类型签名与 `@tauri-apps/api` 逐文件对照（可用 `tsd` 断言） |
| 行为一致 | `tests/bridge/` 扩展为"跑同一组用例，分别 mock 两边传输层" |
| 端到端 | `samples/OrielDemo/wwwroot` 迁到 `@orielweb/api`；新增 `--selftest api` 覆盖每个模块的往返 |
| 三平台 | 现有 `verify-linux.sh` / `verify-windows.ps1` / `verify-macos.sh` 各加一段（只调一行） |
| 迁移样例 | 保留一份 `samples/OrielTauriCompat`（同一页面 + 两份 `package.json` 别名），证明"改 import 就能跑" |

**建议的下一步**：先做 P0-1（JS 包骨架）+ P0-2（label）。这两项不改三平台后端，
却能让"对齐"从"讨论"变成"可写代码"，并且后面每个模块都能独立并行。

---

## 附录 A：Tauri 2 全量模块与本库归属

| Tauri 2 | 归属 | 优先级 |
|---|---|---|
| `core` / `event` / `window` / `dpi` / `app` / `path` | 核心 | P0–P1 |
| `webviewWindow` / `image` / `menu` / `mocks` | 核心 | P2 |
| `webview`（多 webview 部分） | 核心 | ⛔ |
| `tray` | 内置 | ✅ 已完成 |
| `dialog` / `opener` / `notification` / `clipboard-manager` / `autostart` / `os` / `process` | 插件 | P1–P2 |
| `fs` / `store` / `window-state` / `log` / `global-shortcut` | 插件 | P2 |
| `single-instance` | 插件 | ✅ 已完成 |
| `shell` / `http` / `websocket` / `upload` / `sql` / `stronghold` / `localhost` / `deep-link` / `updater` / `positioner` / `cli` / `persisted-scope` | 插件 | ⛔ |
| `barcode-scanner` / `biometric` / `geolocation` / `haptics` / `nfc` | 插件 | ⛔（移动端） |

## 附录 B：迁移速查（import 替换）

| Tauri 2 | OrielWeb |
|---|---|
| `@tauri-apps/api/core` | `@orielweb/api/core` |
| `@tauri-apps/api/event` | `@orielweb/api/event` |
| `@tauri-apps/api/window` | `@orielweb/api/window` |
| `@tauri-apps/api/webviewWindow` | `@orielweb/api/webviewWindow` |
| `@tauri-apps/api/app` | `@orielweb/api/app` |
| `@tauri-apps/api/path` | `@orielweb/api/path` |
| `@tauri-apps/api/image` | `@orielweb/api/image` |
| `@tauri-apps/api/menu` | `@orielweb/api/menu` |
| `@tauri-apps/api/tray` | `@orielweb/api/tray` |
| `@tauri-apps/api/dpi` | `@orielweb/api/dpi` |
| `@tauri-apps/api/mocks` | `@orielweb/api/mocks` |
| `@tauri-apps/plugin-dialog` | `@orielweb/api/dialog` |
| `@tauri-apps/plugin-opener` | `@orielweb/api/opener` |
| `@tauri-apps/plugin-notification` | `@orielweb/api/notification` |
| `@tauri-apps/plugin-clipboard-manager` | `@orielweb/api/clipboard` |
| `@tauri-apps/plugin-os` | `@orielweb/api/os` |
| `@tauri-apps/plugin-process` | `@orielweb/api/process` |
| `@tauri-apps/plugin-autostart` | `@orielweb/api/autostart` |
| `@tauri-apps/plugin-global-shortcut` | `@orielweb/api/globalShortcut` |
| `@tauri-apps/plugin-fs` | `@orielweb/api/fs`（若做） |
| `@tauri-apps/plugin-store` | `@orielweb/api/store`（若做） |
| `@tauri-apps/plugin-window-state` | `@orielweb/api/windowState`（若做） |
| `@tauri-apps/plugin-log` | `@orielweb/api/log`（若做） |
| `@tauri-apps/plugin-shell` | ⛔ 无对应 |

> 模块名保持与 Tauri 完全同名，是这套方案最省事的地方：迁移 = 一次 scope 替换 + §3 那 16 条语义修正。
