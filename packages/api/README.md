# @orielweb/api

OrielWeb 的页面侧 API。**模块名与 `@tauri-apps/api` 一一对应**，迁移时只需替换 import 前缀。

```js
// Tauri
import { invoke } from '@tauri-apps/api/core';
// OrielWeb
import { invoke } from '@orielweb/api/core';
```

## 设计取舍

| 取舍 | 理由 |
|---|---|
| **JS + JSDoc，不用 TypeScript 源码** | 本库的卖点是零依赖、AOT、单文件；前端包也不该引入打包器。`exports` 直接指向 `src/*.js`，`tsc` 只用来校验类型（`--noEmit`）。 |
| **`window.oriel` 降级为传输层** | 它等价于 Tauri 的 `__TAURI_INTERNALS__`——能用，但不是公开 API。文档不再主推。 |
| **不做参数名自动转换** | Tauri 把 JS 的 camelCase 转成 Rust 的 snake_case；本库的 `[OrielCommand]` 按方法参数名**原样**取值。转了反而对不上，所以不转。 |
| **`mocks` 与包同时提供** | 没有它，前端单测只能起真窗口，CI 里跑不动。 |

## 当前状态

| 模块 | 状态 |
|---|---|
| `core` | ✅ `invoke` / `isTauri` / `convertFileSrc`（显式未实现） |
| `event` | ✅ `listen` / `once` / `emit` / `emitTo` + `TauriEvent`（含名字映射与按需转发） |
| `window` | ✅ `getCurrentWindow` / `getAllWindows` / `getByLabel` / `getFocusedWindow`（恒 `null`）+ `Window` 类 |
| `dpi` | ✅ 四个类 + 归一 + 换算 |
| `mocks` | ✅ `mockIPC` / `mockWindows` / `mockEmit` / `mockConvertFileSrc` / `clearMocks` |
| `app` / `path` / `menu` / `image` / … | 尚未实现，见 [TAURI2-PLAN.md](../../docs/TAURI2-PLAN.md) |

## 关闭接管：语义与 Tauri **相反**

Tauri 在回调里 `preventDefault()` 阻止关闭；本库是**宿主挂起、页面同意才关**：

```js
const win = getCurrentWindow();
const un = await win.onCloseRequested(async () => {
  if (await confirmDiscard()) await win.destroy();   // 同意关闭
  // 不同意：什么都不做，窗口留在打开状态
});
```

传进回调的 `event.preventDefault` 是**空操作**——保留它只是为了让迁移代码不抛 `TypeError`。
为什么不做成 Tauri 那样：Windows 的 `WM_CLOSE` 是同步消息、页面回执是异步 IPC，
宿主没法"等你回调跑完再决定"。订阅会自动打开宿主侧的关闭接管开关。

## `window` 方法的能力要求

标题栏必需的那组（`minimize` / `toggleMaximize` / `toggleFullscreen` / `toggleOnTop` /
`close` / `destroy` / `drag*` / `pickFile` / `contextMenu`）与三条只读查询
（`isVisible` / `isMaximized` / `title`）**无条件放行**；其余要宿主
`Allow("window.*")` 或逐条声明。清单与逐条理由见 [API.md §11](../../docs/API.md)。

## `event` 的现状（迁移必读）

| 项 | 状态 |
|---|---|
| `listen` / `once` / `emit` / `emitTo` | ✅ 可用 |
| 事件对象 `{ event, id, payload }` | ✅ **包装在包内做**——直接调 `window.oriel.on` 的既有页面不受影响 |
| `emit` / `emitTo` 的宿主命令 | ✅ `event.emit`，**必须显式 `Allow("event.emit")`**（不走 `win.*` 的后门） |
| 内置事件名 | ✅ `tauri://resize` / `move` / `focus` / `blur` / `close-requested` 可用（高频的三个会**自动打开**宿主转发）；⛔ `tauri://destroyed` **永远收不到**（窗口销毁时页面正在被拆掉） |

`mocks` 里的 `mockEmit(name, payload)` 可以模拟宿主推事件，写前端测试时用它造"窗口被 resize 了"这类场景。

## 开发

```bash
cd packages/api
npm install          # 只装 typescript（devDependency）
npm test             # node --test tests/*.test.mjs
npm run typecheck    # tsc -p . --noEmit
```

## 与 Tauri 的语义差异（迁移必读）

1. **默认 30 秒超时**（Tauri 没有）。长任务必须显式放宽：`window.oriel.timeout = 300000`。
   per-call `timeout` 尚未实现（P1-7），传了会**明确报错**而不是静默忽略。
2. **失败值是字符串**。`catch (e) { e.message }` 拿到宿主文案；结构化 `code`/`data` 尚未实现。
3. **参数名原样透传**，不做 camelCase → snake_case。
4. **`isTauri()` 在装了 mock 时返回 `true`**——单测应走真实分支，而不是被环境判断带去另一条没有测试覆盖的路。
