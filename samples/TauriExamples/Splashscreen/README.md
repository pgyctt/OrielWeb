# splashscreen

原文：[`tauri/examples/splashscreen`](https://github.com/tauri-apps/tauri/tree/dev/examples/splashscreen)（Apache-2.0 / MIT）。

原文的两个窗口写在 `tauri.conf.json` 里：

```json
{ "label": "main", "visible": false, "width": 800, "height": 600 }
{ "label": "splashscreen", "width": 400, "height": 200, "decorations": false }
```

命令也只有一条：

```rust
#[tauri::command]
fn close_splashscreen(app: AppHandle) {
  app.get_webview_window("splashscreen").unwrap().close().unwrap();
  app.get_webview_window("main").unwrap().show().unwrap();
}
```

## 搬过来是什么样

| Tauri | 本库 |
|---|---|
| `tauri.conf.json` 的 windows 数组 | 两次 `AddWindow(...)` |
| `"visible": false` | `WithHidden()` |
| `"decorations": false` | `WithFrameless()` |
| `"url": "splashscreen.html"` | `WithUrl("oriel://app.oriel/splashscreen.html")` |
| 注入 `AppHandle` | `Build()` 之后把 `OrielApp` 交给命令类 |
| `get_webview_window(label)` | `App.GetWindow(label)` |

命令名 `close_splashscreen` 不变，页面里的调用也不变。

## 为什么用 `Build()` 而不是 `Run()`

命令类要拿应用级句柄，只能由外部在 `Build()` 之后注入——本库的命令参数只从 JSON 取，
不注入 `AppHandle` / `Window`。命令类在第一次 invoke 时才实例化，所以
`Build()` → `Attach(app)` → `Run()` 这个顺序是安全的，也是"命令需要应用级句柄"的标准写法。

## 两个平台细节

- **隐藏窗口照常跑页面**。主窗口在前 2 秒是隐藏的，但 DOM 已就绪、定时器照走、
  IPC 照常往返。所以"先隐藏预热、准备好再显示"不需要额外机制。
- **启动画面是无边框的**，没有系统标题栏可拖。页面把整块背景标成
  `data-oriel-drag-region`，于是它也能拖动——这是 `Drag` 示例里的同一个属性。

启动画面上额外放了一个"跳过"按钮。原文没有它；加它是因为"主页面加载失败 → 启动画面永远在
→ 看起来像死机"是这类结构最容易出的事故。它调用的还是同一条命令，不引入第二套语义。

## 跑

```bash
dotnet run --project samples/TauriExamples/Splashscreen/TauriSplashscreen.csproj
```
