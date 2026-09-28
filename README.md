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
| Windows x64/arm64 | WebView2 (Evergreen) | ✅ 已验证 |
| macOS x64/arm64 | WKWebView | ✅ 编译通过，待真机验证 |
| Linux x64/arm64 | WebKitGTK 4.1 | ✅ 编译通过，待环境验证 |

## 许可

MIT
