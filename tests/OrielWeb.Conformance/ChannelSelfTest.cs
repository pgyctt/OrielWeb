using System.Text.Json;
using OrielWeb;
using OrielWeb.Ipc;

namespace OrielConformance;

/// <summary>
/// 流式回传自检（<c>--selftest channel</c>）：页面 <c>new Channel()</c> → 作为参数发出去 →
/// 宿主在命令执行期间逐条推回 → 命令返回后通道自动关闭。
/// </summary>
/// <remarks>
/// <para>
/// 为什么这条自检不能只靠单测：链路上有两段只有真跑才看得见——
/// </para>
/// <list type="number">
///   <item><description><b>参数序列化</b>：桥接层对 args 做 <c>JSON.stringify</c>，
///     <c>Channel</c> 靠 <c>toJSON()</c> 变成一个字符串。这一步在单测里是手写的字符串，
///     真实路径上却依赖桥接脚本的行为。</description></item>
///   <item><description><b>宿主推事件</b>：C# 侧用 <c>EmitEvent("oriel://channel/&lt;id&gt;")</c> 推，
///     页面用同名的 <c>transport.on</c> 收。名字对不上时，两边各自都"成功"，只是永远收不到。</description></item>
/// </list>
/// <para>
/// 断言（结论行 <c>CHANNEL-SELFTEST: PASS</c> / <c>FAIL（…）</c>）：
/// <list type="number">
///   <item>命令执行期间收到 3 条增量，值依次是 0/1/2（顺序与条数都要对）。</item>
///   <item>命令的返回值仍然正常（通道不影响回执）。</item>
///   <item>命令返回后 <c>channel.closed === true</c>——结束事件到了，页面侧没有留下监听。</item>
///   <item>命令抛异常时同样收到增量、同样是关闭的（生成器把关闭放在 <c>finally</c> 里）。</item>
/// </list>
/// </para>
/// </remarks>
internal sealed class ChannelSelfTest
{
    private const string Echo = "channel-echo";

    private static readonly List<string> Failures = [];
    private static readonly object Gate = new();

    private static WebviewWindow? _main;
    private static Timer? _watchdog;
    private static bool _finished;

    private static bool _streamSeen;
    private static bool _failSeen;

    internal static bool Failed
    {
        get
        {
            lock (Gate)
            {
                return Failures.Count > 0;
            }
        }
    }

    /// <summary>在 <c>onCreated</c> 里调用：订阅页面回执（早于导航，避免漏掉最早那条）。</summary>
    internal static void Attach(WebviewWindow main)
    {
        _main = main;
        main.MessageReceived += OnMessage;
    }

    /// <summary>在 <c>Loaded</c> 里调用：后端已就位，命令路由与事件推送都能用了。</summary>
    internal static void Begin(OrielApp app, WebviewWindow main)
    {
        _main = main;
        _ = app;

        _watchdog = new Timer(
            _ =>
            {
                lock (Gate)
                {
                    if (_finished)
                    {
                        return;
                    }

                    Failures.Add(
                        $"通道自检超时（60 秒）：流式回执={_streamSeen}，异常路径回执={_failSeen}");
                }

                main.PostToUiThread(Finish);
            },
            null,
            TimeSpan.FromSeconds(60),
            Timeout.InfiniteTimeSpan);

        // 走构建时内嵌到 wwwroot/vendor 的那份 src/core.js（页面里没有 importmap）。
        _ = main.EvaluateJs("""
            (async () => {
                const vendor = './vendor/orielweb-api';
                const post = (payload) => oriel.postMessage('channel-echo', payload);
                const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

                try {
                    const { Channel, invoke } = await import(vendor + '/core.js');

                    // ① 正常路径：命令推 3 条后返回。
                    //
                    //    注意 Channel 必须在 invoke **之前**构造——宿主在命令里随时可能推，
                    //    等 promise 落地再订阅就晚了。
                    //
                    //    另外再挂一个**原始**监听：它不参与业务，只用来在失败时回答
                    //    "页面到底收到了什么"。通道自动关闭失败时，光看 closed=false
                    //    分不清"结束事件没发"与"发了但没到"。
                    const values = [];
                    const channel = new Channel((v) => values.push(v));
                    const raw = [];
                    oriel.on('oriel://channel/' + channel.id, (v) => raw.push(['data', v]));
                    oriel.on('oriel://channel-end/' + channel.id, () => raw.push(['end']));

                    let error = null;
                    let result = null;
                    try {
                        result = await invoke('channel.stream', { count: 3, channel });
                    } catch (e) {
                        error = String((e && e.message) || e);
                    }

                    // 结束事件与回执之间没有跨线程顺序保证，**轮询**而不是睡固定时长：
                    // 睡 150ms 在慢一点的机器上会把"晚到"误判成"没来"。
                    const started = Date.now();
                    while (!channel.closed && Date.now() - started < 2000) {
                        await sleep(20);
                    }
                    const closedAtMs = channel.closed ? Date.now() - started : -1;

                    post({ step: 'stream', error, result, values, raw, closed: channel.closed, closedAtMs });

                    // ② 异常路径：推到一半抛错，通道也必须关掉（生成器的 finally）。
                    const values2 = [];
                    const channel2 = new Channel((v) => values2.push(v));

                    let error2 = null;
                    try {
                        await invoke('channel.streamFail', { channel: channel2 });
                    } catch (e) {
                        error2 = String((e && e.message) || e);
                    }

                    const started2 = Date.now();
                    while (!channel2.closed && Date.now() - started2 < 2000) {
                        await sleep(20);
                    }

                    post({
                        step: 'fail',
                        error: error2,
                        values: values2,
                        closed: channel2.closed,
                        closedAtMs: channel2.closed ? Date.now() - started2 : -1,
                    });
                } catch (e) {
                    post({ step: 'fatal', error: String((e && e.message) || e) });
                }
            })();
            """);
    }

    /// <summary>
    /// 演示命令：推 <paramref name="count"/> 条递增整数后返回条数。
    /// </summary>
    /// <remarks>
    /// 每推一条让出一次线程：证明"通道在命令**执行期间**就能推"，而不是等命令返回后一起补上
    /// （后者在页面看来顺序一样，但流式输出的意义恰恰在于提前到达）。
    /// </remarks>
    [OrielCommand("channel.stream")]
    public static async Task<int> Stream(int count, OrielChannel channel)
    {
        for (int i = 0; i < count; i++)
        {
            channel.Send(i);
            await Task.Delay(20);
        }

        return count;
    }

    /// <summary>演示命令：推一条之后抛错（验证通道仍然被关掉）。</summary>
    [OrielCommand("channel.streamFail")]
    public static void StreamFail(OrielChannel channel)
    {
        channel.Send("before");
        throw new OrielIpcException("boom");
    }

    private static void OnMessage(OrielMessageReceivedEventArgs args)
    {
        if (args.Name != Echo || args.Json is not { Length: > 0 } json)
        {
            return;
        }

        JsonElement root;
        try
        {
            root = JsonDocument.Parse(json).RootElement;
        }
        catch (JsonException)
        {
            return;
        }

        string step = root.TryGetProperty("step", out JsonElement stepElement)
            ? stepElement.GetString() ?? string.Empty
            : string.Empty;

        switch (step)
        {
            case "stream":
                _streamSeen = true;
                CheckStream(root);
                break;

            case "fail":
                _failSeen = true;
                CheckFailPath(root);
                break;

            case "fatal":
                Check(false, $"通道自检脚本抛错：{Text(root, "error")}");
                _main?.PostToUiThread(Finish);
                break;
        }

        if (_streamSeen && _failSeen)
        {
            _main?.PostToUiThread(Finish);
        }
    }

    private static void CheckStream(JsonElement root)
    {
        string? error = Text(root, "error");
        Check(error is null, $"channel.stream 报错：{error}");

        Check(Int(root, "result") == 3, $"命令返回值应当是 3，实际 {Int(root, "result")}");

        List<double> values = Numbers(root, "values");
        Check(
            values is [0, 1, 2],
            $"增量应当是 [0,1,2]，实际 [{string.Join(",", values)}]");

        Check(Bool(root, "closed"),
            "命令返回后通道应当已自动关闭，但 closed=false（2 秒内没等到结束事件）。" +
            $"closedAtMs={Int(root, "closedAtMs")}，页面收到的原始事件：[{Raw(root)}]");
    }

    private static void CheckFailPath(JsonElement root)
    {
        string? error = Text(root, "error");
        Check(
            error is not null && error.Contains("boom", StringComparison.Ordinal),
            $"异常路径应当把命令的错误透给页面，实际 error={error ?? "null"}");

        // 这一条推的是字符串 "before"，所以用**元素个数**而不是数值来判断。
        int count = Count(root, "values");
        Check(count == 1, $"异常之前推的那一条应当到达，实际 {count} 条");

        Check(Bool(root, "closed"), "命令抛异常后通道同样应当关闭（结束事件在 finally 里）");
    }

    private static void Check(bool condition, string description)
    {
        if (condition || description.Length == 0)
        {
            return;
        }

        lock (Gate)
        {
            Failures.Add(description);
        }
    }

    private static string? Text(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement e) && e.ValueKind == JsonValueKind.String
            ? e.GetString()
            : null;

    private static int Int(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement e) && e.ValueKind == JsonValueKind.Number
            ? e.GetInt32()
            : -1;

    private static bool Bool(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement e) && e.ValueKind == JsonValueKind.True;

    /// <summary>数组里的元素个数（不关心类型）。</summary>
    private static int Count(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement array) && array.ValueKind == JsonValueKind.Array
            ? array.GetArrayLength()
            : 0;

    /// <summary>页面收到的原始事件，格式化成 <c>data:0, data:1, end</c>（失败时用来定性）。</summary>
    private static string Raw(JsonElement root)
    {
        if (!root.TryGetProperty("raw", out JsonElement array) || array.ValueKind != JsonValueKind.Array)
        {
            return "（页面没有回传原始事件）";
        }

        var parts = new List<string>();
        foreach (JsonElement pair in array.EnumerateArray())
        {
            if (pair.ValueKind != JsonValueKind.Array || pair.GetArrayLength() < 1)
            {
                continue;
            }

            string kind = pair[0].GetString() ?? "?";
            parts.Add(kind == "data"
                ? $"data:{pair[1].ToString()}"
                : kind);
        }

        return string.Join(", ", parts);
    }

    /// <summary>把数字数组读成列表；非数字项跳过（异常路径里推的是字符串）。</summary>
    private static List<double> Numbers(JsonElement root, string name)
    {
        var result = new List<double>();
        if (!root.TryGetProperty(name, out JsonElement array) || array.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (JsonElement item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Number)
            {
                result.Add(item.GetDouble());
            }
        }

        return result;
    }

    private static void Finish()
    {
        lock (Gate)
        {
            if (_finished)
            {
                return;
            }

            _finished = true;

            Check(_streamSeen, "正常路径那一步没有回执（命令失败或页面脚本中断）");
            Check(_failSeen, "异常路径那一步没有回执");

            int failureCount = Failures.Count;
            Console.WriteLine(failureCount == 0
                ? "CHANNEL-SELFTEST: PASS"
                : $"CHANNEL-SELFTEST: FAIL（{failureCount} 项）");
            foreach (string failure in Failures)
            {
                Console.WriteLine("  - " + failure);
            }
        }
        Console.Out.Flush();

        _watchdog?.Dispose();

        WebviewWindow? main = _main;
        main?.PostToUiThread(main.App.Quit);
    }
}
