# B7 · 迁移演练结论（2026-10-19）

> 这份文档是执行计划里 B7 的产出。它的用途不是"记录我们做了什么"，而是**决定下一轮做什么**：
> 从这一批开始，优先级由真实迁移卡点决定，不再由导出面比对决定。

## 0. 一句话结论

三个真实 Tauri 2 应用、228 个源文件、16 个模块说明符、24 个导入符号：
**机械改写（换 import 前缀）能覆盖 13/16 个模块**，剩下 **7 条编译错误分布在 6 个缺口**上，
而**真正的迁移成本不在这 6 个缺口里**——在三类编译器看不见的地方（§3）。

按名字对账只能告诉我们"缺 Channel"，它无法告诉我们"Channel 这条链要重写"与
"onBackButtonPress 删掉两行 Android 分支"的成本差十倍。这就是 B7 存在的理由。

> **后续进展**：§3 的 #3（`Channel`）与 #4（拖放）已分别在 **B9 / B8** 补上，
> 对应条目已改写成"迁移改法"。这份清单保留原始取证与频率，不因为做掉了就删——它是
> 下一轮排序的依据。

## 1. 取样与方法

### 1.1 取样（固定到 commit，结论可复现）

| 应用 | 仓库 @ commit | Tauri | 源文件 | 覆盖点 |
|---|---|---|---|---|
| random-frame | `amokrzycki/random-frame` @ `686613be` | 2.12.1 | 87 | 单窗口工具；插件面最宽（dialog / fs / clipboard / opener / updater / process）；**vanilla TS**（无框架） |
| pier-dock | `pier-dock/pier-dock` @ `2eb10f34` | 2.1.0 | 96 | 多窗口/停靠；menu / webview / webviewWindow / mocks / autostart；React + framer-motion |
| lumos | `DaiYiqq/lumos` @ `7887793e` | 2.12.4 | 45 | 对话框 + 流式长任务（`Channel`）；React |

清单在 `tools/migration-drill/apps.json`。**第三方源码不入库**：工具按 SHA 下载到 `.drill/`
（已 gitignore），仓库里只留清单与结论。

### 1.2 三条检查，为什么是这三条

| 检查 | 回答什么 | 为什么不能只靠它 |
|---|---|---|
| 模块映射 | 16 个 `@tauri-apps/*` 说明符里，几个能改写成 `@orielweb/api/*` | 只看得到"有没有这个模块"，看不到符号 |
| 符号对账 | 24 个被导入的名字，几个真的存在 | 只看得到"有没有这个名字"，看不到调用形状 |
| tsc | 调用形状对不对 | 真实应用没有 `node_modules` 时先吐几千条"找不到 react" |

tsc 那一步的过滤规则因此是按**符号解析来源**判定（诊断位置的符号是否解析到本库的 `d.ts`），
而不是"这一行出现了某个导入名"。后者实测把 4 条真信号埋进了 50 多条 React 噪音里——
**过滤器的失败形态不是漏报，而是让真信号看不见**。

### 1.3 复现

```bash
node tools/migration-drill/drill.mjs                 # 全流程，报告写到 .drill/report.json
node tools/migration-drill/drill.mjs --no-typecheck  # 只做模块/符号检查（秒级）
node tools/migration-drill/drill.mjs --app lumos     # 只跑一个
```

### 1.4 局限（必须写清楚，否则结论会被误用）

* **调用形状的验证只在 vanilla TS 的应用上成立**。random-frame 没有框架依赖，tsc 真的
  解析了本库的类型；pier-dock / lumos 的 React 组件在缺依赖时符号退化成 `any`，
  那部分只验到模块级与符号级。
* 演练的是**前端**。Rust 侧 `#[tauri::command]` 要改写成 C# 命令，本批没有真做（成本是
  每个命令一段 C#，与"迁移"无关，属于"重写后端"）。
* 三个应用都不使用托盘、多显示器、窗口状态记忆，这几块**本批没有证据**。

## 2. 结果：模块面（按使用文件数）

| Tauri 模块 | 本库 | 文件数 | 应用 |
|---|---|---|---|
| `plugin-opener` | `opener` | 13 | random-frame, pier-dock |
| `api/event` | `event` | 13 | 三个都有 |
| `api/core` | `core` | 9 | 三个都有 |
| `api/app` | `app` | 5 | random-frame, pier-dock |
| `plugin-clipboard-manager` | `clipboard` | 3 | random-frame |
| `api/window` | `window` | 3 | random-frame, pier-dock |
| `plugin-dialog` | `dialog` | 3 | random-frame, lumos |
| `api/menu` | `menu` | 3 | pier-dock |
| `api/image` | `image` | 1 | random-frame |
| `plugin-fs` | `fs` | 1 | random-frame |
| `api/mocks` | `mocks` | 1 | pier-dock |
| `api/webviewWindow` | `webviewWindow` | 1 | pier-dock |
| `plugin-autostart` | `autostart` | 1 | pier-dock |
| `plugin-process` | **缺** | 1 | random-frame |
| `plugin-updater` | **缺** | 1 | random-frame |
| `api/webview` | **缺**（无 webview 层） | 1 | pier-dock |

**18/24 个符号直接可用。** 其中 `Menu.new({ items: [{ id, text, action }] })`、`Image.new(rgba, w, h)`、
`writeFile(path, Uint8Array)`、`save({ filters })` 的调用形状**零差异**——B6a/B6b 的菜单对齐
是按真实用法验过的，这条在演练里得到了独立确认。

## 3. 按频率排序的卡点清单

排序依据：**波及应用数 → 调用点数 → 修复成本**。`编译器可见` 那一列是重点：
"是"意味着迁移时一定会被拦下，"否"意味着**会静默改变行为**。

| # | 卡点 | 波及 | 编译器可见 | 类别 | 迁移时的改法 | 补齐成本 |
|---|---|---|---|---|---|---|
| 1 | `__TAURI_INTERNALS__` 环境探测 | **3/3 应用**，5 处（应用代码） | **否** | 语义差异 | 换成 `isTauri()` | 0（改调用点） |
| 2 | 测试夹具 mock 线格式 | random-frame：32 处 `plugin:x\|y` + 34 处全局桩 | **否** | 命名/线格式 | 改用 `mockIPC()`，命令名照 §4 换 | 0（改测试） |
| 3 | `Channel`（core） | 1 应用，1 处，但那是整条流式链路 | 否（**B9 已补**） | 缺能力 | 已实现，import 改前缀即可 | 0 |
| 4 | 页面拖放事件（`api/webview.onDragDropEvent`） | 1 应用 | 是 | 缺能力 | 改成 `getCurrentWindow().onDragDropEvent(...)`（**B8 已补**，但仍只有 `drop`） | 0（改调用点） |
| 5 | `plugin-updater` | 1 应用 | 是 | 缺能力 | 删除更新功能或自建 | **高** |
| 6 | `plugin-process.relaunch` | 1 应用 | 是 | 缺能力 | 暂时只能 `exit` 后手动重开 | 中 |
| 7 | `clipboard.writeImage` | 1 应用 | 是 | 缺能力 | 关掉"复制图片"按钮 | 中 |
| 8 | `onBackButtonPress` | 1 应用，2 处，**两处都在 Android 分支** | 是 | 命名/平台 | **删掉 Android 分支** | 0（本库桌面专用） |

### 3.1 逐条说明

**#1 环境探测（最要紧的一条）。** 三个应用全都用 `"__TAURI_INTERNALS__" in window` 判断
"我是不是在桌面壳里跑"，random-frame 还进一步探测 `__TAURI_INTERNALS__.transformCallback`
来决定要不要 `listen`。这套标记是本库**不提供**的（也不该提供——那是另一个库的内部结构）。

漏改的后果不是报错，是**应用静默走进浏览器降级分支**：mock 数据、禁用的按钮、不注册的监听。
改法是一行：`isTauri()`（本库 `core` 导出，判的是 `window.oriel.invoke`）。

**#2 测试夹具。** random-frame 的测试直接给 `window.__TAURI_INTERNALS__` 装一个假 `invoke`，
按 **Tauri 的线格式**断言（`plugin:fs|write_file` / `plugin:dialog|save`）。迁移后运行时会读
`window.oriel.invoke`，命令名也不同——**这套测试会全红**。这不是缺陷，是"测试贴着线格式写"的
必然成本；改法是换成本库的 `mockIPC()`，并把命令名按 §4 换掉。

这条配方现在**可执行**：`tools/migration-drill/fixtures/mock-ipc.test.mjs`（3 条，进 CI）钉住了
"按本库命令名分支"与"仍用线格式时失败长什么样"。演练报告里的"测试改动点"一节会告诉你
这次要改多少个测试文件（random-frame：32 处 / 12 个文件，全部在测试里）。

**#3 Channel。** 唯一被真实用到的流式通道：`new Channel<ChatEvent>()` → `channel.onmessage = cb`
→ 作为 `invoke` 参数发出去，宿主按 id 回推。

**已在 B9 补上**，迁移改法仍然只是 import 改前缀（构造与用法与 Tauri 一致）。补的时候没有动协议：
线格式是把实例序列化成一个字符串参数（`toJSON`），宿主→页面复用现成的事件通道。
两处与 Tauri 不同、需要迁移者知道：C# 侧参数是**非泛型** `OrielChannel`（`Send(value)` 走与命令
返回值同一套序列化）；页面侧**不保证重排**（Tauri 带 index 重排，本库靠宿主投递顺序）。
见 `docs/API.md` §11.3。

**#4 拖放。** pier-dock 用 `getCurrentWebview().onDragDropEvent(...)` 接"把 .exe/.lnk 拖到 dock
上固定"。宿主侧其实**已经有数据**（`IPlatformBackend.FileDropped` → `WebviewWindow.FileDropped`），
缺的只是把它变成页面事件。

**已在 B8 补上**，迁移改法是一行：`getCurrentWebview().onDragDropEvent(...)` →
`getCurrentWindow().onDragDropEvent(...)`。补的时候只做 `drop` 一种——三平台的原生投放
都在松手那一刻回调一次，`enter/over/leave` 要么改用 COM 的 `IDropTarget`、要么各平台再写
一套坐标换算；payload 也不带 `position`（宁可 `undefined` 也不要一个会被当成"左上角"的 0）。
这两处差异写进了 `docs/API.md` §9.1 与 `TauriEvent` 的状态表——**补一半要写在明面上，
否则它就退化成下一轮的 #1 类静默差异**。

**#5–#7** 都是整块能力，本批只确认了"真实应用的确在这么用"，不低估它们的成本。

**#8** 值得单说：两处调用一行在 `android-navigation.ts`、一行在
`if (platform.platform === "android")` 里。**这不是"缺 API"，是"这个库不做移动端"。**
迁移时的正确动作是删掉那两个分支——给桌面库加一个永不触发的空实现，只是把
"你根本没有返回键"这件事藏起来。

## 4. 命令名映射表（B7 的副产物）

迁移时**必须换的不是 import，而是这些命令名**：它们出现在 `invoke()` 的字面量里、
测试夹具里、以及 Rust 侧的 emit 调用里。表里左边是 Tauri 的线格式，右边是本库命令。

| Tauri 线格式 | 本库命令 | 备注 |
|---|---|---|
| `plugin:fs\|read_text_file` | `fs.readTextFile` | |
| `plugin:fs\|write_file` | `fs.writeFile` | Tauri 的 `Content` 是数字数组/Uint8Array/字符串；本库收 `Uint8Array \| number[]` |
| `plugin:fs\|read_dir` | `fs.readDir` | |
| `plugin:dialog\|open` | `dialog.open` | |
| `plugin:dialog\|save` | `dialog.save` | |
| `plugin:opener\|open_url` | `opener.openUrl` | |
| `plugin:opener\|open_path` | `opener.openPath` | |
| `plugin:opener\|reveal_item_in_dir` | `opener.revealItemInDir` | |
| `plugin:clipboard-manager\|write_text` | `clipboard.writeText` | |
| `plugin:clipboard-manager\|read_text` | `clipboard.readText` | |
| `plugin:notification\|notify` | `notification.sendNotification` | |
| `plugin:autostart\|enable` / `disable` / `is_enabled` | `autostart.enable` / `disable` / `isEnabled` | |
| `plugin:app\|version` / `name` | `app.getVersion` / `getName` | |
| `plugin:window\|is_maximized` / `close` | `window.isMaximized` / `window.close` | |
| `plugin:event\|listen` / `emit` | `event.listen` / `event.emit` | |
| `plugin:updater\|check` | —— | 本库没有 |
| `plugin:process\|relaunch` | —— | 本库只有 `app.exit` |
| `#[tauri::command] fn add_item(item_id: String)` | `[OrielCommand("add.item")] AddItem(string itemId)` | 参数名写 **camelCase**：页面按名字传，C# 侧按名字取 |

**自定义事件名不用换**：三个应用监听的全是自己 Rust 侧 emit 的名字
（`sync-state-changed` / `windows-changed` / `library://changed` / `ingest://progress` ……），
没有一条 Tauri 内建事件。只要 C# 侧 emit 同名即可。

## 5. 对下一轮的输入

按证据排，不按名字排：

1. **B8：页面事件面**（✅ 2026-10-20 完成）
   * `Window.onDragDropEvent` + `tauri://drag-drop` 事件——宿主早已有数据，只缺事件通路，
     是本清单里**性价比最高**的一项；
   * 同一批把"只有 drop、没有 enter/over/leave""payload 没有 position"写进了
     `docs/API.md` §9.1、`event.js` 的状态表与两处单测，而不是留一句"以后补"。
2. **B9：`Channel` 的取舍**（✅ 2026-10-20 完成，选了"真实现"）
   * 三条路当时是：①协议加一路带 id 的推送；②只支持"命令内多次回推"的受限形态；
     ③明确不支持，把 `event + requestId` 写成官方迁移配方。
   * 最终走了 ① 的**低成本形态**：线格式只是一个字符串参数（`toJSON`），宿主→页面复用
     现成事件通道，因此**没动 IPC 协议、没动桥接脚本、没动三个平台后端**。
     代价是宿主侧要多一个生成器分支（`OrielChannel` 参数的绑定 + `finally` 关闭）。
   * 结论：这条最贵的缺口，实际成本低于预算——**因为找到了"协议已有能力"的接法**。
     下次评估"要动协议"的缺口时，先问一遍"现有通道能不能承载它"。
3. **不做**：`plugin-updater`（整块生态能力）、`clipboard.writeImage`（三平台原生剪贴板）、
   `onBackButtonPress`（桌面库不该有）。这三项写进账本的"明确不做"档，附理由。

## 6. 这次演练对"对齐策略"本身的修正

* **账本会给出错误的任务粒度**。`Channel` / `onBackButtonPress` / `writeImage` 在账本上
  都是"缺 1 个符号"，实际成本从 0 行到改协议不等。下一轮的计划必须带上"迁移改法"这一列，
  否则会按符号个数分配预算。
* **编译器看不见的那两类（#1/#2）比所有缺能力项加起来更影响"迁移是否成功"**：它们不报错，
  只是让应用行为变了。这类差异只能在文档里显式写、在演练里显式扫——所以演练工具保留了
  `__TAURI_INTERNALS__` 与 `plugin:` 字面量的扫描，而不是只做类型检查。
* **"缺能力"里有一条其实该删代码**（#8）。判断标准是"目标平台有没有这个概念"，
  而不是"Tauri 有没有这个导出"。
