# multiwindow

原文：[`tauri/examples/multiwindow`](https://github.com/tauri-apps/tauri/tree/dev/examples/multiwindow)（Apache-2.0 / MIT）。

原文的 Rust 侧只有 `setup` 里建一个 `Third` 窗口；真正有意思的部分全在页面：
**建窗、按 label 发消息、监听消息**。本库把这三件事一一对上，页面代码是五份示例里
最接近"只改 import 前缀"的一份。

## 对照

| Tauri | 本库 | 备注 |
|---|---|---|
| `new WebviewWindow(label, { title })` | 同名同参 | 见下：结果在 `created` 上 |
| `emitTo(label, 'message', payload)` | 同名同参 | 目标不存在时静默丢弃（两边一致） |
| `getCurrentWebviewWindow().listen('message', cb)` | 同名同参 | 回调形状 `{ event, id, payload }` |
| `tauri.conf.json` 的 windows 数组 | `AddWindow(...)` | 主窗口 label 默认 `main` |
| ACL 里的 `plugin:webview\|create_webview_window` | `Allow("window.new")` | 内建命令，但不在始终放行名单 |
| ACL 里的 `plugin:event\|emit_to` | `Allow("event.emit")` | 同上 |

## 唯一的调用形状差异

```js
// Tauri：同步返回实例，再等事件
const w = new WebviewWindow(label, { title });
w.once('tauri://created', () => { ... });
w.once('tauri://error', (e) => { ... });

// 本库：构造时立刻发起 window.new，结果在 created 上
const w = new WebviewWindow(label, { title });
await w.created;
```

本库**没有**宿主→页面的"窗口创建"事件（`tauri://created` 不会来），所以改成
一个显式的 Promise。构造时挂了一个空 `catch`，没 await 也不会变成未处理拒绝。
多写这一行的收益是：label 重复、窗口创建失败这类问题当场可见，而不是"点了创建没反应"。

## 两个容易踩的点

- **label 必须应用内唯一**。重复会在建窗时抛 `InvalidOperationException`（不会自动改名——
  自动改名会让 `emitTo('main', …)` 发到错误窗口且毫无症状）。
- **`emitTo` 不保证送达**。目标窗口可能已经关了；`emitTo` 只表示宿主接受了这次广播。
  这与 Tauri 一致——多窗口应用里窗口随时会消失，把"发晚了"变成异常只会让收尾代码随机炸掉。

## 跑

```bash
dotnet run --project samples/TauriExamples/MultiWindow/TauriMultiWindow.csproj
```

1. 在"新建窗口"里填 `second` / `第二个窗口`，点创建；
2. 在新窗口的"目标 label"里填 `main`，发一条消息；
3. 回到主窗口，消息会出现在下面——每个新窗口加载的都是同一个 `index.html`。
