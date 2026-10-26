# state

原文：[`tauri/examples/state`](https://github.com/tauri-apps/tauri/tree/dev/examples/state)（Apache-2.0 / MIT）。

原文四条命令：`increment` / `decrement` / `reset` / `get`，状态是一个
`Counter(Mutex<isize>)`，用 `app.manage(...)` 放进全局容器，命令再用 `State<'_, Counter>` 取回来。

## 迁移动作

```rust
// Tauri
struct Counter(Mutex<isize>);

tauri::Builder::default()
  .manage(Counter(Mutex::new(0)))
  .invoke_handler(generate_handler![increment, decrement, reset, get])

#[tauri::command]
fn increment(counter: State<'_, Counter>) -> isize { ... }
```

```csharp
// OrielWeb
var counter = new Counter();          // 对应 .manage(Counter(...))

Oriel.CreateBuilder(args)
  .AddCommands(() => new CounterCommands(counter))   // 工厂只跑一次
  .UseCapabilities(c => c.Allow("increment", "decrement", "reset", "get"));

internal sealed partial class CounterCommands(Counter counter)
{
    [OrielCommand("increment")]
    public long Increment() => counter.Add(1);
}
```

**命令类的实例就是容器。** `AddCommands` 的工厂只调用一次，之后每条 invoke
都作用在同一个对象上——包括来自不同窗口的 invoke。所以：

| Tauri | 本库 |
|---|---|
| `.manage(state)` | 普通对象，构造后交给命令类 |
| `State<'_, T>` 形参 | 构造函数参数 |
| `Mutex` 保证并发安全 | **仍然要自己做**：共享实例会被并发调用，示例用 `lock` |

页面侧**零改动**：四条命令名、参数、返回值类型都与原文一致（`isize` → `long`）。

## 一个容易误会的点

`AddCommands` 不是"暴露开关"。命令名在编译期就进了索引；`AddCommands` 登记的是
**实例工厂**。真正的门禁是 `UseCapabilities` 的 `Allow` / `Deny`
（Release 下未声明即拒绝）。所以"我没注册它，为什么能从页面调到"这类困惑不会出现——
调不到是因为没 Allow，不是因为没 AddCommands。

## 跑

```bash
dotnet run --project samples/TauriExamples/State/TauriState.csproj
```

刷新页面计数器不会归零：状态在宿主侧，不在页面里。
