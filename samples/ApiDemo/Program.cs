using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using OrielWeb;
using OrielWeb.Ipc;

namespace ApiDemo;

/// <summary>
/// 入口。这个应用是"本库已实现 API 的主验证台"，对标 Tauri 的 <c>examples/api</c>。
/// </summary>
/// <remarks>
/// <para>
/// 它和 <c>samples/OrielDemo</c> 的分工：OrielDemo 是**契约自检的载体**（<c>--selftest</c>
/// 由 CI 无人值守地跑），页面只有 Todo 与手动操作台；这个应用是**给人点的**——
/// 每个已实现的方法各占一行，点了就看回执或错误。两者都不进 NuGet 包。
/// </para>
/// <para>
/// <b>能力配置刻意写成"全开 + 一条 deny"</b>：验证应用要把每个入口都点一遍，
/// 逐条列名单会让"新增一个 API 忘了加名单"变成一次假失败。真正的门禁演示留给那条
/// <c>demo.blocked</c>——它被 <c>Deny</c> 挡下，页面能看到完整的 <c>not_allowed</c> 形状。
/// </para>
/// </remarks>
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Oriel.CreateBuilder(args)
            .UseEmbeddedAssets()
            .UseJsonContext(AppJsonContext.Default)
            .WithAppInfo(name: "Oriel API Demo", identifier: "com.orielweb.samples.apidemo")
            .AddCommands<DemoCommands>()
            .UseCapabilities(c =>
            {
                // 验证台：所有命令放行（Debug/Release 一致），只用一条 deny 演示拒绝形状。
                c.Allow("*").Deny("demo.blocked");

                // fs 与 asset 是**两条独立的 scope**（见 docs/API.md §11）：
                // 文件读写与"把文件显示出来"不是同一种意图，不共用一张表。
                // $APPDATA 是**本应用**的数据目录（.../com.orielweb.samples.apidemo），不是整个 AppData。
                c.Allow("fs.*", s => s.AllowPath("$APPDATA/**"));
                c.Allow(OrielAssetFileServer.ScopeCommand, s => s.AllowPath("$APPDATA/**"));
            })
            .UseDebug()
            .AddWindow(w => w
                .WithTitle("Oriel API Demo")
                .WithSize(1180, 800)
                .WithMinSize(820, 600)
                // 页面 console 转发给宿主：验证台自己的脚本出错时，从命令行启动能看到
                // 一行 [page:error]，而不是对着一片白屏猜。
                .WithConsoleForwarding()
                .Centered(),
                win => win.ConsoleMessage += args =>
                    Console.Error.WriteLine($"[page:{args.Level}] {args.Text}"))
            .Run();
    }
}

/// <summary>回显用的 DTO：用来验证"结构化参数进、结构化结果出"。</summary>
public sealed record Payload(string Text, int Number, bool Flag);

/// <summary>流式通道推给页面的单条进度。</summary>
public sealed record StreamTick(int Index, int Total, string Text);

/// <summary>宿主信息（验证"命令能问到运行期事实"）。</summary>
public sealed record HostInfo(
    string Framework,
    string Os,
    string Architecture,
    bool DynamicCode,
    string LibraryVersion);

/// <summary>
/// 验证台自己的命令。命名统一用 <c>demo.</c> 前缀，与内建的 <c>app.</c> / <c>window.</c> 分开。
/// </summary>
public sealed partial class DemoCommands
{
    /// <summary>最小回执：验证 invoke 往返与参数绑定。</summary>
    [OrielCommand("demo.ping")]
    public string Ping(string message) => $"pong: {message}";

    /// <summary>DTO 进、DTO 出：验证源生成 JSON 上下文的双向序列化。</summary>
    [OrielCommand("demo.echo")]
    public Payload Echo(Payload payload)
        => payload with { Text = payload.Text.ToUpperInvariant() };

    /// <summary>抛一条带错误码的异常：验证页面侧 <c>error.code</c> / <c>error.data</c>。</summary>
    [OrielCommand("demo.fail")]
    public void Fail(string kind) =>
        throw new OrielIpcException($"命令按 kind='{kind}' 主动失败（这条文案来自应用）。")
        {
            Code = kind,
            // 走源生成上下文，避免在 AOT 下落到反射序列化
            Details = System.Text.Json.JsonSerializer.SerializeToElement(kind, AppJsonContext.Default.String),
        };

    /// <summary>慢命令：配合 <c>invoke(..., { timeout })</c> 验证逐次超时。</summary>
    [OrielCommand("demo.slow")]
    public async Task<string> Slow(int milliseconds)
    {
        await Task.Delay(Math.Clamp(milliseconds, 0, 10_000));
        return $"{milliseconds} ms 后返回（宿主侧实际等待了这么久）";
    }

    /// <summary>
    /// 被 <c>Deny("demo.blocked")</c> 挡住的命令。
    /// 它**永远不该被执行**；看到它的返回值说明能力门禁出了问题。
    /// </summary>
    [OrielCommand("demo.blocked")]
    public string Blocked() => "门禁失效：这条命令本不该被调用。";

    /// <summary>
    /// 流式回传：命令返回前多次 <c>Send</c>，页面用 <c>Channel</c> 接。
    /// 对应 Tauri 文档里的 <c>Channel&lt;T&gt;</c>（不是 <c>examples/streaming</c>，那个是自定义 scheme）。
    /// </summary>
    [OrielCommand("demo.stream")]
    public async Task<int> Stream(int count, OrielWeb.Ipc.OrielChannel channel)
    {
        int total = Math.Clamp(count, 1, 50);
        for (int i = 1; i <= total; i++)
        {
            await Task.Delay(120);
            channel.Send(new StreamTick(i, total, $"第 {i}/{total} 块"));
        }

        return total;
    }

    /// <summary>运行期事实：验证"宿主能回答自己跑在什么之上"。</summary>
    [OrielCommand("demo.info")]
    public HostInfo Info() => new(
        Framework: RuntimeInformation.FrameworkDescription,
        Os: RuntimeInformation.OSDescription,
        Architecture: RuntimeInformation.ProcessArchitecture.ToString(),
        DynamicCode: RuntimeFeature.IsDynamicCodeSupported,
        LibraryVersion: typeof(OrielApp).Assembly.GetName().Version?.ToString(3) ?? "0.0.0");

    /// <summary>
    /// 页面自报"模块图加载完、路由已渲染"。宿主只在启动时打一行到 stderr。
    /// </summary>
    /// <remarks>
    /// 这条命令的存在理由很具体：ApiDemo 的前端是二十来个 ES 模块，任何一处 import 拼错
    /// 都会让页面**白屏但不抛宿主异常**。让页面加载完成后主动回一条 IPC，
    /// 从命令行启动时就能凭一行日志判断"是前端没起来"还是"宿主没建窗"。
    /// </remarks>
    [OrielCommand("demo.ready")]
    public bool Ready(int modules, int actions)
    {
        Console.Error.WriteLine($"[api-demo] page ready: {modules} 个模块 / {actions} 个动作");
        return true;
    }

    /// <summary>页面的未捕获错误转发到 stderr（诊断白屏用，不参与业务）。</summary>
    [OrielCommand("demo.pageerror")]
    public bool PageError(string message)
    {
        Console.Error.WriteLine($"[page:uncaught] {message}");
        return true;
    }
}

/// <summary>
/// STJ 源生成上下文。命令参数与返回值里出现的每个 DTO 都要在这里登记；
/// 漏登记的失败是**运行期**一条明确异常（AOT 下没有反射兜底）。
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Payload))]
[JsonSerializable(typeof(StreamTick))]
[JsonSerializable(typeof(HostInfo))]
[JsonSerializable(typeof(string))]
internal sealed partial class AppJsonContext : JsonSerializerContext;
