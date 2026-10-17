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
| `event` | ✅ `listen` / `once` / `emit` / `emitTo` + `TauriEvent` 常量 |
| `dpi` | ✅ 四个类 + 归一 + 换算 |
| `mocks` | ✅ `mockIPC` / `mockWindows` / `mockEmit` / `mockConvertFileSrc` / `clearMocks` |
| `window` / `app` / `path` / `tray` / … | 尚未实现，见 [TAURI2-PLAN.md](../../docs/TAURI2-PLAN.md) |

## `event` 的现状（迁移必读）

| 项 | 状态 |
|---|---|
| `listen` / `once` / `emit` / `emitTo` | ✅ 可用 |
| 事件对象 `{ event, id, payload }` | ✅ **包装在包内做**——直接调 `window.oriel.on` 的既有页面不受影响 |
| `emit` / `emitTo` 的宿主命令 | ✅ `event.emit`，**必须显式 `Allow("event.emit")`**（不走 `win.*` 的后门） |
| `TauriEvent` 的内置事件名 | ⚠️ 常量已就位，但 `tauri://resize` 等**尚未推到页面通道**——订阅它们现在收不到 |

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
