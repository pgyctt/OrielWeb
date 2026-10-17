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
| `path` | ✅ `sep` / `delimiter`（同步）+ 7 个路径运算（由宿主算） |
| `mocks` | ✅ `mockIPC` / `mockWindows` / `mockEmit` / `mockConvertFileSrc` / `clearMocks` |
| `app` / `menu` / `image` / `dialog` / `fs` / … | 尚未实现，见 [TAURI2-PLAN.md](../../docs/TAURI2-PLAN.md) |

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

## 怎么把它引进页面

包**没有打包器依赖**，`exports` 直接指向 `src/*.js`。两条路：

| 场景 | 做法 |
|---|---|
| 有打包器（Vite / esbuild / …） | `import { invoke } from '@orielweb/api/core'` —— 裸标识符由打包器解析 |
| 没有打包器（内嵌静态页） | 两种：配一个 import map；或直接 `import ... from './vendor/orielweb-api/core.js'` |

`samples/OrielDemo` 走的是第二条路的相对路径形式（它要把包源码内嵌进程序集，
见 `OrielDemo.csproj` 的 vendor 段落——**直接内嵌源码而不是复制一份**，所以不存在副本漂移）：

```html
<script type="module" src="app.js"></script>
```

```js
// app.js
import { invoke, isTauri } from "./vendor/orielweb-api/core.js";
import { listen } from "./vendor/orielweb-api/event.js";
```

> **import map 在 WebView2 上可用，但 WebKitGTK 的支持版本不确定**，所以示例不依赖它。
> 要用的话写成逐模块条目（不能让 `@orielweb/api/core` 解析成无扩展名的 URL——
> 内嵌资源表是按完整相对路径查的）：
>
> ```html
> <script type="importmap">
> { "imports": {
>     "@orielweb/api/core": "./vendor/orielweb-api/core.js",
>     "@orielweb/api/event": "./vendor/orielweb-api/event.js",
>     "@orielweb/api/window": "./vendor/orielweb-api/window.js"
> } }
> </script>
> ```

### 迁移时最容易漏掉的两处

1. **事件回调要取 `e.payload`** —— 包把宿主事件包成 `{ event, id, payload }`（Tauri 形状），
   而裸通道给的是裸值。`oriel.on('maximized', b => …)` 迁移后是 `listen('maximized', e => …e.payload…)`。
2. **`await window.oriel.ready` 没有对应物** —— 包与 Tauri 一样没有 `ready`：
   桥接脚本在 document 创建时就同步装好了，页面脚本运行时它已经就绪。
   该问的是"我在不在宿主里"，即 `isTauri()`。

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
