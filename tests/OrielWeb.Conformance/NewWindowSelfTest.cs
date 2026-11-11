using System.Text.Json;
using OrielWeb;

namespace OrielConformance;

/// <summary>
/// 新窗口请求的弃绝自检（<c>--selftest newwindow</c>）。
/// </summary>
/// <remarks>
/// <para>
/// <b>被钉住的行为</b>：页面发起 <c>window.open</c> / <c>target="_blank"</c> 时，
/// 库在三个平台上**统一弃绝**——不开新窗口，页面侧拿到 <c>null</c>。
/// 策略与理由见 API.md 的导航一节。
/// </para>
/// <para>
/// <b>为什么需要这个自检</b>：这三个平台的实现方式完全不同（Windows 置
/// <c>put_Handled(1)</c>、macOS 让 <c>createWebViewWithConfiguration:</c> 返回 <c>nil</c>、
/// Linux 让 <c>create</c> 信号返回 <c>NULL</c>），而"不实现"与"实现成弃绝"在**页面侧
/// 看不出区别**——直到某个引擎自己开出一个空白窗口。只有真机跑一遍才能确定三平台一致。
/// </para>
/// <para>
/// <b>三条断言，缺一不可</b>：
/// <list type="number">
///   <item>页面侧 <c>window.open</c> 返回 <c>null</c>——弃绝对页面可见；</item>
///   <item>宿主诊断里出现库自己那条"已忽略页面发起的新窗口请求"——证明是**我们的**
///     处理器弃绝的，而不是引擎的弹窗拦截器顺手拦了（两者对页面都表现为 null，
///     只有诊断能区分）；</item>
///   <item>应用窗口数不变——没有新窗口被悄悄创建出来。</item>
/// </list>
/// </para>
/// <para>
/// 结论行：<c>NEWWINDOW-SELFTEST: PASS</c> / <c>FAIL（…）</c>。
/// </para>
/// </remarks>
internal static class NewWindowSelfTest
{
    private const string Echo = "newwindow-echo";

    /// <summary>目标 URL 用不可解析的保留域名：即使某个引擎真去加载，也不会碰到网络。</summary>
    private const string TargetUrl = "https://example.invalid/newwindow-probe";

    /// <summary>诊断里那句"是库弃绝的"的标识（与三平台的实现文案一致）。</summary>
    private const string DiagnosticMarker = "已忽略页面发起的新窗口请求";

    private static readonly List<string> Failures = [];
    private static readonly object Gate = new();

    private static WebviewWindow? _main;
    private static Timer? _watchdog;
    private static bool _finished;
    private static bool _diagnosticSeen;
    private static int _windowsBefore;

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

    /// <summary>在 <c>Loaded</c> 里调用：这时 <c>App</c> 才可用（能数窗口）。</summary>
    internal static void Begin(OrielApp app, WebviewWindow main)
    {
        _main = main;
        _windowsBefore = app.Windows.Count;

        // 订阅宿主诊断：这是"弃绝由库完成"的唯一证据（页面侧 null 无法区分是谁拦的）。
        OrielDiagnostics.Reported += OnDiagnostic;

        // 看门狗：本自检没有轮询，只有一条页面回执；没回来就说明整条链断了。
        _watchdog = new Timer(
            _ => Fail("10 秒内没有收到页面回执（window.open 之后页面没消息回来）"),
            null,
            TimeSpan.FromSeconds(10),
            Timeout.InfiniteTimeSpan);

        // 用 try/finally 包住：页面若因为安全策略直接抛异常，回执里也要带上原因。
        _ = main.EvaluateJs($$"""
            (function () {
                try {
                    var w = window.open('{{TargetUrl}}', '_blank');
                    var kind = (w === null) ? 'null' : (w === undefined ? 'undefined' : 'object');
                    oriel.postMessage('{{Echo}}', { step: 'opened', kind: kind });
                } catch (e) {
                    oriel.postMessage('{{Echo}}', {
                        step: 'opened', kind: 'threw', error: String((e && e.message) || e) });
                }
            })();
            """);
    }

    private static void OnDiagnostic(OrielDiagnosticEventArgs args)
    {
        if (args.Message.Contains(DiagnosticMarker, StringComparison.Ordinal))
        {
            Volatile.Write(ref _diagnosticSeen, true);
        }
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

        if (step != "opened")
        {
            return;
        }

        string kind = root.TryGetProperty("kind", out JsonElement kindElement)
            ? kindElement.GetString() ?? string.Empty
            : string.Empty;

        if (kind == "threw")
        {
            string error = root.TryGetProperty("error", out JsonElement errorElement)
                ? errorElement.GetString() ?? string.Empty
                : string.Empty;

            Fail($"window.open 在页面里抛异常（本库不应当让它抛）：{error}");
            Finish();
            return;
        }

        // 断言 1：页面侧拿到 null。
        if (kind != "null")
        {
            Fail($"window.open 应返回 null（请求被弃绝），实际是 {kind}");
        }
        else
        {
            Console.WriteLine("[newwindow-selftest] OK: window.open 返回 null（请求被弃绝）");
        }

        // 断言 2：库自己的诊断出现过——证明是库弃绝的，不是引擎的弹窗拦截器。
        if (!Volatile.Read(ref _diagnosticSeen))
        {
            Fail("宿主诊断里没有「" + DiagnosticMarker + "」——请求可能被引擎默认处置拦下，" +
                 "而不是由库的处理器弃绝（两者对页面都表现为 null）");
        }
        else
        {
            Console.WriteLine("[newwindow-selftest] OK: 宿主诊断证明弃绝由库的处理器完成");
        }

        // 断言 3：没有新窗口被创建。
        _main?.PostToUiThread(() =>
        {
            int after = _main.App.Windows.Count;
            if (after != _windowsBefore)
            {
                Fail($"窗口数从 {_windowsBefore} 变成了 {after}——新窗口请求不该真的建窗");
            }
            else
            {
                Console.WriteLine($"[newwindow-selftest] OK: 窗口数未变（{after}）");
            }

            Finish();
        });
    }

    private static void Fail(string description)
    {
        lock (Gate)
        {
            Failures.Add(description);
        }

        Console.WriteLine($"[newwindow-selftest] 失败: {description}");
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
        OrielDiagnostics.Reported -= OnDiagnostic;

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
            Console.WriteLine($"NEWWINDOW-SELFTEST: FAIL（{count} 项）");
        }
        else
        {
            Console.WriteLine("NEWWINDOW-SELFTEST: PASS");
        }

        Console.Out.Flush();
    }
}
