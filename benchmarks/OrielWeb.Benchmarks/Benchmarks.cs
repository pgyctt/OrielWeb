using System.Text.Json;
using BenchmarkDotNet.Attributes;
using OrielWeb.Ipc;

namespace OrielWeb.Benchmarks;

/// <summary>
/// 一条命令的端到端往返：门禁（来源 + 令牌 + 授权）→ 路由 → 执行 → 回执序列化。
/// </summary>
/// <remarks>
/// <para>
/// 这是本库最热的路径，也是"零反射 IPC"这句承诺唯一有意义的度量点——
/// 单看路由表查找（O(1)）说明不了什么，真正要盯的是整条链的分配。
/// </para>
/// <para>
/// 消息**预解析**成 <see cref="JsonElement"/> 再进基准：后端交给分发器的就是已解析的元素，
/// 把 <c>JsonDocument.Parse</c> 算进来会掩盖分发本身的开销。
/// </para>
/// </remarks>
[MemoryDiagnoser]
public class InvokeRoundTripBenchmarks
{
    private OrielCommandDispatcher _dispatcher = null!;
    private JsonElement _message;
    private NullReplySink _sink = null!;

    [GlobalSetup]
    public void Setup()
    {
        BenchRouter.EnsureRegistered();
        _dispatcher = new OrielCommandDispatcher(
            new Dictionary<Type, Func<object>>(),
            jsonContext: null,
            guard: new OrielIpcGuard(new OrielCapabilityOptions().Allow("*"), isDebugBuild: false, "app.oriel"));

        string json = $$"""
            { "__oriel": "invoke", "id": 1, "name": "bench.echo",
              "args": { "text": "hello" }, "token": "{{_dispatcher.Guard.Token}}" }
            """;
        using var document = JsonDocument.Parse(json);
        _message = document.RootElement.Clone();
        _sink = new NullReplySink();
    }

    [Benchmark]
    public ValueTask Invoke()
        => _dispatcher.HandleInvokeAsync(_message, _sink, "oriel://app.oriel/index.html");
}

/// <summary>
/// 同一操作，但注册表里有 1000 条命令——用来验证"O(1) 分发"在规模下不退化。
/// </summary>
/// <remarks>
/// 与 <see cref="InvokeRoundTripBenchmarks"/> 分成两个类是因为注册表是**进程级**的：
/// 两类各跑一个基准进程，互不污染。同一进程里先注册 1000 条再测 1 条，测出来的就不是 1 条。
/// </remarks>
[MemoryDiagnoser]
public class ManyRoutesDispatchBenchmarks
{
    private OrielCommandDispatcher _dispatcher = null!;
    private JsonElement _message;
    private NullReplySink _sink = null!;

    [GlobalSetup]
    public void Setup()
    {
        BulkRouter.EnsureRegistered(1000);
        _dispatcher = new OrielCommandDispatcher(
            new Dictionary<Type, Func<object>>(),
            jsonContext: null,
            guard: new OrielIpcGuard(new OrielCapabilityOptions().Allow("*"), isDebugBuild: false, "app.oriel"));

        string json = $$"""
            { "__oriel": "invoke", "id": 1, "name": "bench.bulk999",
              "args": null, "token": "{{_dispatcher.Guard.Token}}" }
            """;
        using var document = JsonDocument.Parse(json);
        _message = document.RootElement.Clone();
        _sink = new NullReplySink();
    }

    [Benchmark]
    public ValueTask Dispatch()
        => _dispatcher.HandleInvokeAsync(_message, _sink, "oriel://app.oriel/index.html");
}

/// <summary>
/// 内嵌资源查找：URL → 相对路径归一 → 资源表查表。
/// </summary>
/// <remarks>
/// 每次页面请求静态资源都要走一遍（一个中等页面几十次），且它同时是路径穿越的防线——
/// 归一逻辑变复杂时，这里要能立刻看出代价。
/// </remarks>
[MemoryDiagnoser]
public class AssetLookupBenchmarks
{
    private EmbeddedAssetStore _store = null!;

    [GlobalSetup]
    public void Setup() => _store = EmbeddedAssetStore.Create(null);

    /// <summary>最常见形态：子目录里的一个文件。</summary>
    [Benchmark]
    public bool Lookup_SubPath() => _store.TryGet("/sub/page.html", out _);

    /// <summary>带 query 与 fragment（缓存破坏的典型写法）。</summary>
    [Benchmark]
    public bool Lookup_WithQueryAndFragment() => _store.TryGet("/sub/page.html?v=2#top", out _);

    /// <summary>URL 编码的路径：要做一次解码再查表。</summary>
    [Benchmark]
    public bool Lookup_PercentEncoded() => _store.TryGet("/sub/pa%67e.html", out _);

    /// <summary>不存在的路径（404 也走同一条查找）。</summary>
    [Benchmark]
    public bool Lookup_Miss() => _store.TryGet("/nope/missing.html", out _);
}

/// <summary>
/// 能力判定的名字匹配：每条 invoke 至少跑一次（allow 名单逐个比）。
/// </summary>
/// <remarks>
/// 纯字符串运算、无分配，适合做"回归哨兵"：一旦有人在匹配里加正则或 LINQ，分配列会立刻跳。
/// </remarks>
[MemoryDiagnoser]
public class CapabilityMatchBenchmarks
{
    private static readonly string[] AllowList =
    [
        "todo.add", "todo.list", "todo.toggle", "sys.info", "config.get",
        "config.set", "user.profile", "user.settings", "report.export", "sync.*",
    ];

    private static readonly string[] DenyList = ["todo.remove", "sync.reset"];

    [Benchmark]
    public bool PrefixMatch() => OrielCapabilityRules.IsAllowed("sync.push", AllowList, DenyList);

    [Benchmark]
    public bool ExactMatch() => OrielCapabilityRules.IsAllowed("config.get", AllowList, DenyList);

    [Benchmark]
    public bool Denied() => OrielCapabilityRules.IsAllowed("todo.remove", AllowList, DenyList);
}

/// <summary>一条命令的路由（静态、无实例）。</summary>
internal sealed class BenchRouter : IOrielCommandRouter
{
    private static readonly object Gate = new();
    private static bool s_registered;

    public IReadOnlyList<string> CommandNames { get; } = ["bench.echo"];

    public Type? TargetType => null;

    public bool RequiresTarget => false;

    public int Route(string name) => name == "bench.echo" ? 0 : -1;

    public ValueTask<object?> InvokeAsync(
        int index,
        object? target,
        JsonElement args,
        System.Text.Json.Serialization.JsonSerializerContext? jsonContext,
        OrielEventSink? sink)
        => new("hello");

    /// <summary>
    /// 注册到全局表（进程内只需一次）。BDN 的每个基准类各用独立进程，所以这里的静态标志是安全的。
    /// </summary>
    public static void EnsureRegistered()
    {
        lock (Gate)
        {
            if (s_registered)
            {
                return;
            }

            OrielCommandRegistry.AddRouter(new BenchRouter());
            s_registered = true;
        }
    }
}

/// <summary>1000 条命令的路由：验证规模不改变单次分发成本。</summary>
internal sealed class BulkRouter(int count) : IOrielCommandRouter
{
    private static readonly object Gate = new();
    private static bool s_registered;
    private readonly string[] _names = [.. Enumerable.Range(0, count).Select(i => $"bench.bulk{i}")];

    public IReadOnlyList<string> CommandNames => _names;

    public Type? TargetType => null;

    public bool RequiresTarget => false;

    public int Route(string name)
    {
        // 只在 BuildIndex 时调一次，不参与热路径；用 IndexOf 保持实现简单。
        return Array.IndexOf(_names, name);
    }

    public ValueTask<object?> InvokeAsync(
        int index,
        object? target,
        JsonElement args,
        System.Text.Json.Serialization.JsonSerializerContext? jsonContext,
        OrielEventSink? sink)
        => new(index);

    public static void EnsureRegistered(int count)
    {
        lock (Gate)
        {
            if (s_registered)
            {
                return;
            }

            OrielCommandRegistry.AddRouter(new BulkRouter(count));
            s_registered = true;
        }
    }
}

/// <summary>不记录回执的 worker：基准只关心分发本身的开销，不想把 string 堆留成垃圾。</summary>
internal sealed class NullReplySink : IIpcReplySink
{
    public void PostJson(string json)
    {
    }
}
