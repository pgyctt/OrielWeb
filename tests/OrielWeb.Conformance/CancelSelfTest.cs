using System.Text.Json;
using OrielWeb;

namespace OrielConformance;

/// <summary>
/// 取消令牌自检（<c>--selftest cancel</c>）。
/// </summary>
/// <remarks>
/// <para>
/// <b>被钉住的链路</b>：页面 <c>oriel.invoke(..., { timeout })</c> 超时 → 桥接脚本发
/// <c>{ __oriel:'cancel', id }</c> → 分发器按（窗口, id）找到取消源 →
/// 命令方法里那个 <see cref="CancellationToken"/> 被触发 → 命令提前返回。
/// </para>
/// <para>
/// <b>四条断言，缺一不可</b>：
/// <list type="number">
///   <item>长命令**真的开始执行**了——否则"取消成功"可能只是"命令没跑"；</item>
///   <item>页面侧收到 <c>code: 'timeout'</c>——超时判定与错误码是页面能依赖的契约；</item>
///   <item>宿主侧 <see cref="CancelProbeCommands.SlowCancelled"/> 为真——取消**真的送达**了命令。
///     这一条是核心：只看页面超时，取消有没有生效完全看不出来；</item>
///   <item>并发的那条短命令正常返回——取消没有波及无关调用（键写错时正是这个会挂）。</item>
/// </list>
/// </para>
/// <para>
/// 第 3 条之所以成立，是因为长命令等的是 <c>Task.Delay(5 秒, token)</c>：
/// 1.5 秒时它还在等，只有 token 被触发才会提前返回。所以"1.5 秒时观察到已取消"
/// 等价于"5 秒的等待被打断了"，而不是"页面那边自己超时了"。
/// </para>
/// <para>
/// 结论行：<c>CANCEL-SELFTEST: PASS</c> / <c>FAIL（…）</c>。
/// </para>
/// </remarks>
internal static class CancelSelfTest
{
    private const string Echo = "cancel-echo";

    /// <summary>页面侧超时。要明显短于命令的 5 秒，否则自检会跑很久。</summary>
    private const int PageTimeoutMs = 400;

    /// <summary>
    /// 收到页面两条回执后再等这么久，才检查宿主侧的取消标志。
    /// </summary>
    /// <remarks>
    /// 取消是跨进程（页面 → 宿主）再进入命令的一条链，不是同步的：
    /// <c>postMessage</c> 与宿主的消息处理之间隔着一次事件循环。
    /// 留 1.2 秒既足够（实测远小于此），又远小于命令自己的 5 秒——
    /// 所以"这期间观察到已取消"仍然只可能来自取消，而不是命令自然结束。
    /// </remarks>
    private static readonly TimeSpan CancelPropagation = TimeSpan.FromSeconds(1.2);

    private static readonly List<string> Failures = [];
    private static readonly object Gate = new();

    private static WebviewWindow? _main;
    private static Timer? _watchdog;
    private static bool _finished;
    private static bool _slowReplySeen;
    private static bool _fastReplySeen;
    private static string _slowCode = string.Empty;
    private static string _fastValue = string.Empty;
    private static bool _fastFailed;

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

    /// <summary>在 <c>Loaded</c> 里调用。</summary>
    internal static void Begin(OrielApp app, WebviewWindow main)
    {
        _main = main;
        CancelProbeCommands.Reset();

        // 看门狗：两条回执都该在 400ms 后很快到齐；没到就是整条链断了。
        _watchdog = new Timer(
            _ => Fail("10 秒内没有收齐页面回执（超时 / 取消链路没跑起来）"),
            null,
            TimeSpan.FromSeconds(10),
            Timeout.InfiniteTimeSpan);

        // 两条一起发：长命令会被取消，短命令必须不受影响。
        // per-call timeout 用的是桥接脚本的第三个参数（supportsPerCallTimeout）。
        _ = main.EvaluateJs($$"""
            oriel.invoke('{{CancelProbeCommands.SlowCommand}}', {}, { timeout: {{PageTimeoutMs}} })
              .then(function () {
                  oriel.postMessage('{{Echo}}', { step: 'slow', ok: true, code: '' });
              })
              .catch(function (e) {
                  oriel.postMessage('{{Echo}}', {
                      step: 'slow', ok: false, code: (e && e.code) ? String(e.code) : '' });
              });

            oriel.invoke('{{CancelProbeCommands.FastCommand}}', {})
              .then(function (v) {
                  oriel.postMessage('{{Echo}}', { step: 'fast', ok: true, value: String(v) });
              })
              .catch(function (e) {
                  oriel.postMessage('{{Echo}}', {
                      step: 'fast', ok: false, value: String((e && e.message) || e) });
              });
            """);
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
            Fail("页面回执不是合法 JSON");
            return;
        }

        string step = root.TryGetProperty("step", out JsonElement stepElement)
            ? stepElement.GetString() ?? string.Empty
            : string.Empty;
        bool ok = root.TryGetProperty("ok", out JsonElement okElement)
                  && okElement.ValueKind == JsonValueKind.True;

        switch (step)
        {
            case "slow":
                _slowReplySeen = true;
                _slowCode = root.TryGetProperty("code", out JsonElement codeElement)
                    ? codeElement.GetString() ?? string.Empty
                    : string.Empty;
                if (ok)
                {
                    Fail($"长命令不该成功返回（它等 5 秒，页面在 {PageTimeoutMs}ms 就超时了）");
                }

                break;

            case "fast":
                _fastReplySeen = true;
                _fastFailed = !ok;
                _fastValue = root.TryGetProperty("value", out JsonElement valueElement)
                    ? valueElement.GetString() ?? string.Empty
                    : string.Empty;
                break;

            default:
                return;
        }

        if (!_slowReplySeen || !_fastReplySeen)
        {
            return;
        }

        // 两条都到了。再等一会儿让取消传播进命令，然后一次性判完。
        _watchdog?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        Timer? settle = null;
        settle = new Timer(
            _ =>
            {
                settle?.Dispose();
                _main?.PostToUiThread(Evaluate);
            },
            null,
            CancelPropagation,
            Timeout.InfiniteTimeSpan);
    }

    private static void Evaluate()
    {
        // 断言 1：长命令真的跑起来了。
        if (!CancelProbeCommands.SlowStarted)
        {
            Fail("长命令压根没开始执行——那样「取消成功」没有意义");
        }
        else
        {
            Console.WriteLine("[cancel-selftest] OK: 长命令确实开始执行了");
        }

        // 断言 2：页面侧拿到 timeout 码。
        if (_slowCode != "timeout")
        {
            Fail($"页面侧应收到 code='timeout'，实际是 '{_slowCode}'");
        }
        else
        {
            Console.WriteLine("[cancel-selftest] OK: 页面侧收到 code='timeout'");
        }

        // 断言 3（核心）：取消真的送达了命令的 CancellationToken。
        if (!CancelProbeCommands.SlowCancelled)
        {
            Fail("命令的 CancellationToken 没有被触发——页面超时了，但宿主没有取消命令" +
                 "（5 秒的等待仍在继续，这正是要修的那个问题）");
        }
        else
        {
            Console.WriteLine("[cancel-selftest] OK: 取消已送达命令的 CancellationToken");
        }

        // 断言 4：并发命令不受影响。
        if (_fastFailed)
        {
            Fail($"并发短命令失败了（取消波及了无关调用）：{_fastValue}");
        }
        else if (_fastValue != "fast")
        {
            Fail($"并发短命令返回值不对：'{_fastValue}'");
        }
        else
        {
            Console.WriteLine("[cancel-selftest] OK: 并发短命令正常返回（取消没有波及无关调用）");
        }

        Finish();
    }

    private static void Fail(string description)
    {
        lock (Gate)
        {
            Failures.Add(description);
        }

        Console.WriteLine($"[cancel-selftest] 失败: {description}");
    }

    private static void Finish()
    {
        if (_finished)
        {
            return;
        }

        _finished = true;

        _watchdog?.Dispose();
        _watchdog = null;

        Report();
        _main?.App.Quit();
    }

    private static void Report()
    {
        int count;
        lock (Gate)
        {
            count = Failures.Count;
        }

        if (count > 0)
        {
            Console.WriteLine($"CANCEL-SELFTEST: FAIL（{count} 项）");
        }
        else
        {
            Console.WriteLine("CANCEL-SELFTEST: PASS");
        }

        Console.Out.Flush();
    }
}
