# commands

原文：[`tauri/examples/commands`](https://github.com/tauri-apps/tauri/tree/dev/examples/commands)（Apache-2.0 / MIT）。

原文有三十多条命令，绝大多数是同几个机制的排列组合（`snake_case` 变体、裸 future、
`Request`/`Response` 逃生口）。这里只留五条——它们各自代表一种**页面能感知到的差异**。
第一条用了教程里的 `greet`：它与原文的 `borrow_cmd` 是同一形状（一个字符串进、一个字符串出），
只是名字更眼熟。

## 逐条对照

| Tauri 原文 | 本库 | 页面调用 |
|---|---|---|
| `#[tauri::command] fn borrow_cmd(the_argument: &str) -> &str` | `[OrielCommand("greet")] string Greet(string name)` | `invoke('greet', { name })` |
| `async fn async_simple_command(the_argument: String)` | `async Task AsyncSimple(string theArgument)` | `invoke('async_simple_command', { theArgument })` |
| `fn ..._with_result(...) -> Result<String, MyError>` | 正常返回 `string`，出错抛 `OrielIpcException` | 同名同参；失败看 `error.code` |
| `fn command_arguments_struct(Person { .. })` | `CommandArgumentsStruct(Person person)` | `{ person: { name, age } }` ← **多一层** |
| `#[command(rename = "renamed_command_new")]` | `[OrielCommand("renamed_command_new")]` | 无变化 |

命令名全部保持原文的 snake_case：本库不要求点分命名（`todo.add` 只是示例里的风格）。

## 三处必须知道的差异

**1. DTO 参数要多包一层。**

Tauri 把**整个 args 对象**反序列化进那个结构体参数，所以页面写 `{ name, age }`。
本库按**参数名**取值，写 `{ person: { name, age } }`。

这不是"本库的写法更啰嗦"就完了：摊平意味着"少传一个字段"和"字段名拼错"都会变成
静默的默认值（Rust 侧靠类型系统兜住一部分，C# 的反序列化不会）。本库选择了显式的那条路。

**2. `Result` 变成异常，`Err` 变成 `code`。**

Tauri 的 `Err(MyError::FooError)` 会序列化成 `"FooError"` 到页面；本库抛
`OrielIpcException`，`message` 给人看、`Code` 给程序判别（`error.code === 'foo_error'`）。
两个字段互不影响——文案可以随便改，码不会跟着漂。

**3. 命令里拿不到 `Window`。**

原文的 `fn window_label(window: Window)` 靠参数注入。本库的命令参数只从 JSON 读，
不注入宿主对象——注入会让"这个参数是数据还是句柄"变成第二套规则。
label 在页面侧直接可读：`getCurrentWindow().label`（对标 Tauri 的 `Window.label`）。

## 跑

```bash
dotnet run --project samples/TauriExamples/Commands/TauriCommands.csproj
```

窗口右侧是回执日志：成功显示返回值与耗时，失败显示文案与 `code`。
