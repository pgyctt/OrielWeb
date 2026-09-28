# OrielWeb

类 Tauri 的 C# 跨平台系统 webview 核心库。零 C++ 组件、零 GUI 框架依赖、Native AOT 友好、零反射 IPC。

## 特性

- **纯 C# P/Invoke**：Windows（WebView2 COM）、macOS（WKWebView + ObjC runtime）、Linux（GTK3 + WebKitGTK）——全部手写互操作，无 saucer/C++ 中间层
- **零反射 IPC**：`[OrielCommand]` + Roslyn 源生成器在编译期生成分发代码，`[ModuleInitializer]` 自动注册，运行期零反射
- **Native AOT**：全局 `IsAotCompatible`/`IsTrimmable`，发布为原生单文件可执行文件
- **无边框窗口**：自绘标题栏 + 流式/原生拖动 + 最大化/全屏/置顶切换
- **系统 webview**：Windows 用 WebView2、macOS 用 WKWebView、Linux 用 WebKitGTK——不捆绑浏览器内核

## 快速开始

```xml
<!-- OrielDemo.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <PublishAot>true</PublishAot>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\OrielWeb\OrielWeb.csproj" />
    <ProjectReference Include="..\..\src\OrielWeb.Generators\OrielWeb.Generators.csproj"
                      OutputItemType="Analyzer" ReferenceOutputAssembly="false" PrivateAssets="all" />
  </ItemGroup>
  <ItemGroup>
    <EmbeddedResource Include="wwwroot\**\*" />
  </ItemGroup>
</Project>
```

```csharp
using System.Text.Json.Serialization;
using OrielWeb;
using OrielWeb.Ipc;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Oriel.CreateBuilder(args)
            .UseEmbeddedAssets()                        // https://app.oriel/ ← wwwroot/**
            .UseJsonContext(AppJsonContext.Default)     // STJ 源生成上下文（DTO）
            .AddCommands<TodoCommands>()                // [OrielCommand] 命令类
            .UseDebug()                                 // DevTools
            .AddWindow(w => w.WithTitle("Demo")
                             .WithSize(1024, 720)
                             .WithFrameless()           // 无边框
                             .Centered())
            .Run();
    }
}

// IPC 命令：页面 JS 调 window.oriel.invoke('todo.add', { text: '...' })
public sealed partial class TodoCommands
{
    [OrielCommand("todo.add")]
    public TodoItem Add(string text) => new(DateTime.Now.GetHashCode(), text, false, "");
}

// STJ 源生成上下文（DTO 序列化的 AOT 安全入口）
[JsonSerializable(typeof(TodoItem))]
internal partial class AppJsonContext : JsonSerializerContext;
```

```html
<!-- wwwroot/app.js（页面侧） -->
<script>
    await window.oriel.ready;
    const item = await window.oriel.invoke('todo.add', { text: '买牛奶' });
    // → IPC 往返：JSON 参数 → 编译期路由 → C# 方法 → JSON 回执 → Promise resolve
</script>
```

## 无边框窗口

```html
<div class="titlebar">
    <div class="drag-region">标题</div>
    <button onclick="oriel.invoke('win.minimize')">─</button>
    <button onclick="oriel.invoke('win.toggleMaximize')">□</button>
    <button onclick="oriel.invoke('win.close')">×</button>
</div>
```

```js
// 拖动：mousedown 时调用
dragRegion.addEventListener('mousedown', () => oriel.invoke('win.drag'));
// macOS/Linux 使用流式拖动（dragStart/dragTo/dragEnd）
```

## 构建

```bash
# Windows
dotnet publish samples/OrielDemo -c Release -r win-x64

# macOS（需要 Mac）
dotnet publish samples/OrielDemo -c Release -r osx-arm64

# Linux（需要 libwebkit2gtk-4.1）
dotnet publish samples/OrielDemo -c Release -r linux-x64
```

## 平台支持

| 平台 | Webview | 状态 |
|------|---------|------|
| Windows x64/arm64 | WebView2 (Evergreen) | ✅ 已运行验证（demo IPC 往返、单测、AOT 发布） |
| Linux x64/arm64 | WebKitGTK 4.1 | ⚠️ 编译通过；CI 在 xvfb 下冒烟（进程存活）；尚未在真机完整验证 |
| macOS x64/arm64 | WKWebView | ⚠️ 仅编译通过；尚未在真机运行过 |

> Linux/macOS 此前会因 `Run()` 的 STA 前置检查直接抛异常（Unix 上 `ApartmentState` 恒为 `Unknown`），
> 该阻断已排除；但两者的运行期行为仍需真机确认，故上表不做超出证据的声明。

### 最低 WebView2 Runtime 版本

- 使用**内嵌资源**（`UseEmbeddedAssets`）需要 `ICoreWebView2_3`，即 WebView2 Runtime **≥ 1.0.864.35**。
- 运行时过旧时会抛出明确异常并附带当前已安装版本，而非静默白屏。
- 环境变量 `ORIEL_WEBVIEW2_FOLDER` 可指定固定版本运行时目录（调试与离线镜像场景）。

## 命令线程模型

- **命令实例是共享的**：`AddCommands<T>()` 注册的类型只创建一次（惰性单例），所有 invoke 都作用于同一实例。
  因此**命令方法必须线程安全**——并发 invoke 可能同时进入同一方法。
- 命令执行发生在**后台线程**（不阻塞 UI 消息循环）；回执由分发器切回 UI 线程后投递。
- 命令内需要操作 UI 时，请经 `OrielApp.PostToMainThread(...)` 切回主线程。
- 反例：`samples/OrielDemo` 的 `TodoCommands` 直接读写 `List<T>` 与 `_nextId++`，并发下并不安全；
  示例为保持简洁如此编写，实际项目请自行加锁或改用线程安全结构。

## 许可

MIT
