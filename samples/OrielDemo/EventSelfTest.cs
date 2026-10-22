using System.Text.Json;
using OrielWeb;

namespace OrielDemo;

/// <summary>
/// 事件模块自检（<c>--selftest event</c>）：<c>event.emit</c> 的广播与定向投递。
/// </summary>
/// <remarks>
/// <para>
/// 取证 P0-3 的"页面 → 宿主 → 页面"闭环。三件事必须在**真窗口**上验，单测覆盖不到：
/// </para>
/// <list type="number">
///   <item>广播：A 发 <c>target: null</c>，A 与 B 的页面都收到；</item>
///   <item>定向：A 发 <c>target: 'event-probe'</c>，只有 B 收到，A 收不到；</item>
///   <item>定向到不存在的窗口：返回 0 且不抛（多窗口应用的正常时序）。</item>
/// </list>
/// <para>
/// 断言靠**探针页面的回显**：谁收到事件，谁的 label 就出现在宿主的 MessageReceived 里。
/// 这比"宿主自己 EmitEvent 然后自己确认"强——它证明整条 IPC 通路（含页面订阅）是通的。
/// </para>
/// <para>
/// 结论行：<c>EVENT-SELFTEST: PASS</c> / <c>FAIL（…）</c>。
/// </para>
/// </remarks>
internal static class EventSelfTest
{
    private const string ProbeEvent = "probe.event";
    private const string SecondLabel = "event-probe";

    /// <summary>内嵌资源的虚拟主机名，必须与 <c>UseEmbeddedAssets(host)</c> 一致（见 Program.AssetHost）。</summary>
    private const string AssetHost = "app.oriel";

    /// <summary>当前处于哪个阶段；回显按阶段分别计数，避免两个阶段的计数互相污染。</summary>
    private enum Phase
    {
        /// <summary>等探针页面就绪。</summary>
        Waiting,

        /// <summary>已发出广播，正在收集回显。</summary>
        Broadcast,

        /// <summary>已发出定向，正在收集回显。</summary>
        Targeted,

        /// <summary>收尾。</summary>
        Done,
    }

    private static readonly object Gate = new();
    private static readonly List<string> Failures = [];

    private static WebviewWindow? _first;
    private static WebviewWindow? _second;
    private static Timer? _watchdog;

    private static Phase _phase = Phase.Waiting;
    private static bool _firstReady;
    private static bool _secondReady;

    private static int _broadcastFromFirst;
    private static int _broadcastFromSecond;
    private static int _targetedFromFirst;
    private static int _targetedFromSecond;

    /// <summary>定向到不存在窗口时页面回显的投递数；-1 表示还没回显。</summary>
    private static int _unknownTargetResult = -1;

    /// <summary>页面侧 invoke 失败时的错误文案（成功时为 null）。</summary>
    private static string? _unknownTargetError;

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

    /// <summary>
    /// 订阅消息（在窗口 <c>onCreated</c> 里调用，**早于导航**）。
    /// </summary>
    /// <remarks>
    /// <b>必须在 onCreated 订阅，不能等 Loaded</b>：探针脚本在页面解析时就发出 <c>probe.ready</c>，
    /// 而 <c>Loaded</c> 在导航完成后才触发——订阅晚于消息，那个 ready 会永远收不到。
    /// 这个竞态是第一次跑本自检时实测出来的（症状是"页面明明加载了，宿主说没收到 ready"）。
    /// </remarks>
    internal static void AttachEvents(WebviewWindow window)
    {
        _first = window;
        window.MessageReceived += args => OnMessage(fromFirst: true, args);
    }

    /// <summary>
    /// 开始跑自检（在 <c>Loaded</c> 里调用，此时 <c>App</c> 与后端都已就位）。
    /// </summary>
    internal static void Begin() => _ = RunAsync();

    private static async Task RunAsync()
    {
        WebviewWindow first = _first!;

        try
        {
            _watchdog = new Timer(_ => OnTimeout(), null, TimeSpan.FromSeconds(90), Timeout.InfiniteTimeSpan);

            // 探针页面的就绪回显是唯一可靠的"两个窗口都装好了"信号——
            // 用固定延时就是在赌引擎装配速度（多窗口下各窗口各自装配，差得很远）。
            await WaitForAsync(() => _firstReady, 20000);
            Check(_firstReady, "第一个窗口的探针页面应回报 ready");
            if (!_firstReady)
            {
                return;
            }

            // 第二个窗口指向同一个探针页面，label 必须显式给（默认 "main" 已被占用）。
            // **Url 必须显式给**：不写就回落到内嵌资源首页（todo 页），那个页面不订阅
            // probe.event 也不发 probe.ready——症状是"第二个窗口的 ready 永远收不到"，
            // 而看起来像是投递坏了。这个坑是第一次跑本自检时实测出来的。
            _second = first.App.CreateWindow(
                w => w.WithLabel(SecondLabel)
                      .WithTitle("事件探针")
                      .WithSize(420, 320)
                      .WithFrameless()
                      .WithUrl($"https://{AssetHost}/event-probe.html"),
                created => created.MessageReceived += args => OnMessage(fromFirst: false, args));

            await WaitForAsync(() => _secondReady, 20000);
            Check(_secondReady, "第二个窗口的探针页面应回报 ready");
            if (!_secondReady)
            {
                return;
            }

            // ---- 1. 广播：两个窗口都该收到 ----
            _phase = Phase.Broadcast;
            await EmitFromPageAsync(first, target: null);

            // 跨窗口投递要 20 秒，不是 8 秒：0.4.2 发版时这一条在 CI 的 Linux runner 上
            // 偶发超时（同一提交重跑就过，另一侧窗口的 evaluateJavaScript 排在主循环后面）。
            // 快是真的快（正常几十毫秒），但"慢"的上界不由我们决定——CI 的软件渲染
            // 环境下偶发几秒的停顿是常态，而这条断言的**结论**（"该收到"）与超时长短无关。
            // 把上界放宽只是让偶发不变成红灯，不会让"真的没收到"漏过去。
            await WaitForAsync(() => _broadcastFromFirst > 0 && _broadcastFromSecond > 0, 20000);

            Check(_broadcastFromFirst > 0, "广播：发起窗口自己也该收到（与 Tauri 的全局 emit 一致）");
            Check(_broadcastFromSecond > 0,
                $"广播：另一个窗口该收到（等待 20 秒；实际第一个窗口 {_broadcastFromFirst} 次、" +
                $"第二个 {_broadcastFromSecond} 次）");

            // ---- 2. 定向：只有目标窗口收到 ----
            _phase = Phase.Targeted;
            int firstBefore = _targetedFromFirst;
            await EmitFromPageAsync(first, target: SecondLabel);

            // 与广播同一条通路，同样放宽到 20 秒（理由见上）
            await WaitForAsync(() => _targetedFromSecond > 0, 20000);
            Check(_targetedFromSecond > 0, "定向：目标窗口该收到");

            // 给"误投递"留出到达时间，否则"多发了一次"会被下一行断言之前的时序漏掉
            await Task.Delay(600);
            Check(
                _targetedFromFirst == firstBefore,
                $"定向：非目标窗口不该收到（发出前 {firstBefore} 次，现在 {_targetedFromFirst} 次）");

            // ---- 3. 定向到不存在的窗口：返回 0，不抛 ----
            //
            // **返回值必须经回显拿，不能直接读 EvaluateJs 的结果**：
            // window.oriel.invoke 返回 Promise，而 ExecuteScriptAsync 不会替我们 await 它
            // ——直接读会得到 `{}`（Promise 的序列化形态）。第一次跑本自检就栽在这里，
            // 症状是"实际 -1、原始回显 {}"，看起来像命令返回了空对象。
            _unknownTargetResult = -1;
            string script =
                "window.oriel.invoke('event.emit', " + BuildArgs("probe.event", "no-such-window") + ")" +
                ".then(function (n) { window.oriel.postMessage('probe.result', { delivered: n }); })" +
                ".catch(function (e) { window.oriel.postMessage('probe.result', { error: String(e) }); });";
            await first.EvaluateJs(script);

            await WaitForAsync(() => _unknownTargetResult != -1, 8000);

            Check(
                _unknownTargetResult == 0,
                $"定向到不存在的窗口应返回 0（实际 {_unknownTargetResult}"
                + (_unknownTargetError is null ? "）" : $"，错误：{_unknownTargetError}）")
                + "——静默丢弃是语义，但\"丢弃了\"这件事本身要可查询");
        }
        catch (Exception ex)
        {
            Check(false, $"自检过程抛出异常：{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _phase = Phase.Done;
            _watchdog?.Dispose();

            try
            {
                _second?.Close();
            }
            catch
            {
                // 收尾失败不该改变结论
            }

            Report();
            first.PostToUiThread(first.App.Quit);
        }
    }

    /// <summary>从页面发起一次 <c>event.emit</c> 并等它完成。</summary>
    /// <remarks>
    /// 直接从宿主侧调 <c>window.EmitEvent</c> 不行：那样验的是"宿主 → 页面"这一段，
    /// 而我们要验的是"页面 → 宿主 → 页面"，中间的 <c>event.emit</c> 命令必须真的被执行。
    /// </remarks>
    private static async Task EmitFromPageAsync(WebviewWindow from, string? target)
    {
        string script = "window.oriel.invoke('event.emit', " + BuildArgs(ProbeEvent, target) + ")";
        await from.EvaluateJs(script);
    }

    /// <summary>拼 <c>event.emit</c> 的参数对象（JSON 文本，直接嵌进脚本）。</summary>
    /// <remarks>
    /// <b>不用 <c>JsonSerializer.Serialize</c></b>：它走反射，在 Native AOT 下会给出
    /// IL2026/IL3050（demo 就是 AOT 发布的）。内建命令的参数走自己的基元序列化路径，
    /// 这里只需要拼一个含两个字符串的小对象，手写转义比引入源生成上下文更轻。
    /// </remarks>
    private static string BuildArgs(string eventName, string? target)
    {
        string targetLiteral = target is null ? "null" : "\"" + EscapeJson(target) + "\"";

        return "{\"event\": \"" + EscapeJson(eventName) + "\", " +
               "\"payload\": { \"seq\": 1 }, " +
               "\"target\": " + targetLiteral + "}";
    }

    /// <summary>
    /// 最小 JSON 字符串转义（只处理会让脚本/JSON 语法出错的字符）。
    /// </summary>
    /// <remarks>
    /// 事件名与 label 在正常用法下只含安全字符（label 被 <c>ValidateLabel</c> 限成
    /// <c>[a-z0-9._-]</c>），但"正常用法"不是不转义的理由：漏掉引号转义的表现是
    /// 注入脚本语法错误 → 自检莫名超时，而那种症状指向不到转义上。
    /// </remarks>
    private static string EscapeJson(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length + 8);
        foreach (char c in value)
        {
            switch (c)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (c < 0x20)
                    {
                        builder.Append("\\u").Append(((int)c).ToString("x4"));
                    }
                    else
                    {
                        builder.Append(c);
                    }

                    break;
            }
        }

        return builder.ToString();
    }

    private static void OnMessage(bool fromFirst, OrielMessageReceivedEventArgs args)
    {
        if (args.Name == "probe.ready")
        {
            if (fromFirst)
            {
                _firstReady = true;
            }
            else
            {
                _secondReady = true;
            }

            return;
        }

        if (args.Name == "probe.result")
        {
            // 页面回显的 invoke 结果。成功给 { delivered: n }、失败给 { error: "..." }。
            // 用回显而不是读 EvaluateJs 的返回值：那个返回值是 Promise 的序列化形态（`{}`）。
            try
            {
                using var doc = JsonDocument.Parse(args.Json);
                JsonElement root = doc.RootElement;

                if (root.TryGetProperty("error", out JsonElement error))
                {
                    _unknownTargetError = error.GetString();
                    _unknownTargetResult = -2;
                }
                else if (root.TryGetProperty("delivered", out JsonElement delivered)
                         && delivered.ValueKind == JsonValueKind.Number)
                {
                    _unknownTargetResult = delivered.GetInt32();
                }
            }
            catch (JsonException)
            {
                _unknownTargetError = $"回显不是可解析的 JSON：{args.Json}";
                _unknownTargetResult = -2;
            }

            return;
        }

        if (args.Name != "probe.received")
        {
            return;
        }

        // **按阶段计数**：同一份回显在不同阶段含义不同。混在一起数会让
        // "定向时发起窗口多收了一份"这类缺陷被广播阶段的计数掩盖。
        switch (_phase)
        {
            case Phase.Broadcast:
                if (fromFirst)
                {
                    Interlocked.Increment(ref _broadcastFromFirst);
                }
                else
                {
                    Interlocked.Increment(ref _broadcastFromSecond);
                }

                break;

            case Phase.Targeted:
                if (fromFirst)
                {
                    Interlocked.Increment(ref _targetedFromFirst);
                }
                else
                {
                    Interlocked.Increment(ref _targetedFromSecond);
                }

                break;
        }
    }

    private static void OnTimeout()
    {
        Check(false,
            $"事件自检超时：90 秒内没走完（阶段={_phase}, ready={_firstReady}/{_secondReady}, " +
            $"广播回显={_broadcastFromFirst}/{_broadcastFromSecond}, " +
            $"定向回显={_targetedFromFirst}/{_targetedFromSecond}）");
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs)
    {
        int waited = 0;
        while (waited < timeoutMs)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(50);
            waited += 50;
        }
    }

    private static void Check(bool condition, string description)
    {
        lock (Gate)
        {
            if (condition)
            {
                Console.WriteLine($"[event-selftest] OK: {description}");
                return;
            }

            Failures.Add(description);
            Console.WriteLine($"[event-selftest] 失败: {description}");
        }
    }

    private static void Report()
    {
        lock (Gate)
        {
            if (Failed)
            {
                Console.WriteLine($"EVENT-SELFTEST: FAIL（{Failures.Count} 项）");
            }
            else
            {
                Console.WriteLine("EVENT-SELFTEST: PASS");
            }
        }

        Console.Out.Flush();
    }
}
