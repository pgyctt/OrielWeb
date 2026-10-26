using System.Text.Json.Serialization;
using OrielWeb;
using OrielWeb.Ipc;

namespace TauriCommands;

/// <summary>
/// 入口。与 Tauri 原文的对应关系：<c>tauri::Builder::default()</c> 的链式装配
/// 就是这里的 <see cref="Oriel.CreateBuilder"/> 链；<c>generate_handler!</c> 那串
/// 命令清单被编译期生成器取代——<c>[OrielCommand]</c> 一标，命令名就进了索引，
/// 不需要再维护一张"要暴露哪些命令"的清单。
/// </summary>
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Oriel.CreateBuilder(args)
            // 前端资源从程序集里取：oriel://app.oriel/index.html
            .UseEmbeddedAssets()
            // DTO 参数的序列化入口。Native AOT 下没有反射兜底，没注册的类型会直接失败。
            .UseJsonContext(AppJsonContext.Default)
            // 标识符会成为用户数据目录的一部分，五个示例各用一个，避免共用 WebView2 profile
            .WithAppInfo(name: "Tauri 示例 · Commands", identifier: "com.orielweb.samples.tauri.commands")
            .AddCommands<CommandSamples>()
            // Release 下未声明的命令一律拒绝（fail-closed）；Debug 全放行。
            // 这里写全，是为了让示例在两种构建下行为一致。
            .UseCapabilities(c => c.Allow(
                "greet",
                "async_simple_command",
                "simple_command_with_result",
                "command_arguments_struct",
                "renamed_command_new"))
            .AddWindow(w => w
                .WithTitle("Tauri 示例 · Commands")
                .WithSize(960, 780)
                .WithMinSize(720, 560)
                .Centered())
            .Run();
    }
}

/// <summary>
/// 与 Rust 侧 <c>Person&lt;'a&gt;</c> 对应。字段名在 JSON 里是 camelCase——
/// 由下面的 <see cref="JsonSourceGenerationOptionsAttribute"/> 统一指定。
/// </summary>
public sealed record Person(string Name, int Age);

/// <summary>
/// 命令类。实例由 <c>AddCommands&lt;T&gt;()</c> 惰性创建、全应用共享一份，
/// 所以实例字段会被所有 invoke（以及所有窗口）看到——这也是 state 示例的基础。
/// </summary>
public sealed partial class CommandSamples
{
    /// <summary>
    /// 最朴素的一条：同步、有返回值。对应原文的 <c>borrow_cmd</c>，
    /// 也是 Tauri 教程里 <c>greet</c> 的形状。
    /// </summary>
    [OrielCommand("greet")]
    public string Greet(string name) => $"你好，{name}！这条回执由 C# 的 Greet() 生成。";

    /// <summary>
    /// 异步、无返回值。对应 <c>async fn async_simple_command</c>。
    /// 页面侧拿到的是 <c>null</c> 回执——"命令成功但没结果"与"失败"仍然分得开。
    /// </summary>
    [OrielCommand("async_simple_command")]
    public async Task AsyncSimple(string theArgument)
    {
        // 故意睡一下：页面上能看出 await 期间界面没有卡住
        await Task.Delay(400);
        _ = theArgument;
    }

    /// <summary>
    /// 返回 Result 的形态。Rust 的 <c>Result&lt;String, MyError&gt;</c> 在本库对应
    /// "正常返回 string，出错抛 <see cref="OrielIpcException"/> 并带 code"。
    /// </summary>
    /// <remarks>
    /// 与 Tauri 的差别：那边 <c>Err</c> 会被序列化成枚举值（<c>"FooError"</c>），
    /// 页面拿到的是字符串；本库的异常文案与 <c>code</c> 是两件独立的事——
    /// 文案给人看，code 给程序判别（<c>error.code === 'foo_error'</c>）。
    /// </remarks>
    [OrielCommand("simple_command_with_result")]
    public string SimpleCommandWithResult(string theArgument)
    {
        if (string.IsNullOrEmpty(theArgument))
        {
            throw new OrielIpcException("theArgument 为空：对应 Tauri 侧的 Err(MyError::FooError)。")
            {
                Code = "foo_error",
            };
        }

        return theArgument;
    }

    /// <summary>
    /// DTO 参数的形态。对应原文的 <c>command_arguments_struct</c>。
    /// </summary>
    /// <remarks>
    /// <b>这是本组里唯一一处调用的形状差异</b>：Tauri 会把整个 args 对象反序列化进那个
    /// 结构体参数（页面写 <c>{ name, age }</c>）；本库按<b>参数名</b>取值，所以要写成
    /// <c>{ person: { name, age } }</c>。原因见 README：本库没有"把参数摊平"的约定，
    /// 摊平会让"少传一个字段"和"字段拼错"都变成静默的默认值。
    /// </remarks>
    [OrielCommand("command_arguments_struct")]
    public string CommandArgumentsStruct(Person person)
        => $"收到 person：name={person.Name} | age={person.Age}";

    /// <summary>
    /// 命令名与 C# 方法名解耦。Tauri 用 <c>#[command(rename = "renamed_command_new")]</c>，
    /// 本库直接在特性里写最终名字——两边页面调的都是 <c>renamed_command_new</c>。
    /// </summary>
    [OrielCommand("renamed_command_new")]
    public string RenamedCommand()
        => "renamed_command 在 Tauri 侧靠 rename 改名为 renamed_command_new；本库在特性里写最终名字。";
}

/// <summary>
/// STJ 源生成上下文：AOT 下 DTO 序列化的唯一入口。
/// CamelCase 是<b>约定</b>，不是默认值——不写这一行，页面会收到 <c>Person.Name</c> 这种形状。
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Person))]
internal sealed partial class AppJsonContext : JsonSerializerContext;
