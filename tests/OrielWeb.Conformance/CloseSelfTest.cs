using OrielWeb;

namespace OrielConformance;

/// <summary>
/// 关闭接管自检（<c>--selftest close</c>）：页面的"可阻止关闭"。
/// </summary>
/// <remarks>
/// <para>
/// 取证 P0-3 里最难的一条（计划里的 E-3）：<c>WM_CLOSE</c> 是**同步消息**，
/// 页面回执是**异步 IPC**，所以宿主不可能"等页面答复"。管线的出路只有三条：
/// 宿主否决、页面接管（挂起）、放行。
/// </para>
/// <para>
/// 本自检逐条取证，每条都必须在真窗口上跑：
/// </para>
/// <list type="number">
///   <item><b>页面阻止</b>：页面收到 <c>close-requested</c> 后什么都不做 → 窗口必须留在打开状态；</item>
///   <item><b>页面放行</b>：页面收到后调 <c>win.destroy</c> → 窗口必须真的关掉；</item>
///   <item><b>宿主否决优先</b>：宿主在 <c>Closing</c> 里 Cancel → 页面**不该**收到事件，窗口也不关；</item>
///   <item><b>未接管</b>：InterceptClose 关着 → 直接关掉，页面不该收到事件。</item>
/// </list>
/// <para>
/// <b>这里的"页面"指裸 <c>window.oriel</c>，不是 <c>@orielweb/api</c>。</b>
/// 两层分工不同：宿主只管"挂起 + 推事件"，"关不关"由页面侧决定。API 包的
/// <c>onCloseRequested</c> 在这之上补了与 Tauri 一致的语义——handler 跑完后未调
/// <c>preventDefault()</c> 就调 <c>destroy()</c>。所以上面第 1 条（什么都不做 →
/// 窗口留着）是**宿主契约**，不是页面 API 的默认行为；页面 API 那边"什么都不做"
/// 等于"关"。两者的测试分别归这里与 <c>packages/api/tests/window.test.mjs</c>。
/// </para>
/// <para>
/// 结论行：<c>CLOSE-SELFTEST: PASS</c> / <c>FAIL（…）</c>。
/// </para>
/// </remarks>
internal static class CloseSelfTest
{
    private const string AssetHost = "app.oriel";
    private const string ProbeUrl = "https://" + AssetHost + "/close-probe.html";

    private static readonly object Gate = new();
    private static readonly List<string> Failures = [];

    private static OrielApp? _app;
    private static WebviewWindow? _main;
    private static Timer? _watchdog;
    private static bool _finished;

    /// <summary>收到的 <c>close.probe.requested</c> 回显：label → 次数。</summary>
    private static readonly Dictionary<string, int> RequestedCounts = new(StringComparer.Ordinal);

    /// <summary>已就绪的探针窗口 label。</summary>
    private static readonly HashSet<string> Ready = new(StringComparer.Ordinal);

    /// <summary>已关闭的探针窗口 label（用于断言"真的关掉了"）。</summary>
    private static readonly HashSet<string> ClosedWindows = new(StringComparer.Ordinal);

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

    /// <summary>在主窗口的 <c>Loaded</c> 里开始跑。</summary>
    internal static void Begin(OrielApp app, WebviewWindow main)
    {
        _app = app;
        _main = main;
        _ = RunAsync();
    }

    private static async Task RunAsync()
    {
        OrielApp app = _app!;

        try
        {
            // **无窗口管理器时整体跳过**：这条自检验的是"点关闭"走完整的 delete 流程，
            // 而 <c>gtk_window_close</c> 在没有 WM 的 X 会话里不产生那条流程
            // （窗口要么立即没反应、要么绕过管线直接销毁），于是四条路径会给出互相矛盾的
            // 结果——那验的是环境，不是库。CI 的 xvfb 就是这种环境。
            bool hasWindowManager =
                !OperatingSystem.IsLinux()
                || Environment.GetEnvironmentVariable("ORIEL_HAS_WM") == "1";
            if (!hasWindowManager)
            {
                Console.WriteLine(
                    "[close-selftest] SKIP: 关闭接管（当前环境没有窗口管理器，" +
                    "gtk_window_close 不会走完整的 delete 流程）");
                Console.WriteLine("CLOSE-SELFTEST: PASS（0 项，整体跳过：无窗口管理器）");
                Console.Out.Flush();
                return;
            }

            _watchdog = new Timer(_ => OnTimeout(), null, TimeSpan.FromSeconds(120), Timeout.InfiniteTimeSpan);

            // ---- 1. 页面阻止：窗口必须留开 ----
            WebviewWindow? prevented = await CreateProbeAsync(app, "close-prevent", intercept: true);
            Check(prevented is not null, "第 1 个探针窗口应建起来");
            if (prevented is null)
            {
                return;
            }

            await SetModeAsync(prevented, "prevent");
            prevented.Close();

            await WaitForAsync(() => CountRequested("close-prevent") > 0, 8000);
            Check(CountRequested("close-prevent") > 0, "页面阻止：页面应收到 close-requested");

            // 给"真的关掉了"留出时间：如果管线写错成"推事件 + 照样销毁"，
            // 那一下会在这段窗口里发生，断言才抓得到。
            await Task.Delay(800);
            Check(
                app.Windows.Contains(prevented),
                "页面阻止：窗口必须留在打开状态（若已关闭，说明挂起写成了照关）");

            // 收尾：用不可拦的 Destroy 清掉它，免得它影响后续断言
            prevented.Destroy();
            await WaitForAsync(() => !app.Windows.Contains(prevented), 5000);
            Check(!app.Windows.Contains(prevented), "页面阻止：Destroy 应当能强制关掉它（逃生口必须有效）");

            // ---- 2. 页面放行：窗口必须真的关掉 ----
            WebviewWindow? allowed = await CreateProbeAsync(app, "close-allow", intercept: true);
            Check(allowed is not null, "第 2 个探针窗口应建起来");
            if (allowed is null)
            {
                return;
            }

            await SetModeAsync(allowed, "allow");
            allowed.Close();

            await WaitForAsync(() => !app.Windows.Contains(allowed), 8000);
            Check(
                !app.Windows.Contains(allowed),
                "页面放行：页面调 win.destroy 之后窗口必须真的关掉");

            // ---- 3. 宿主否决优先：页面不该收到事件 ----
            WebviewWindow? hostVetoed = await CreateProbeAsync(app, "close-hostveto", intercept: true);
            Check(hostVetoed is not null, "第 3 个探针窗口应建起来");
            if (hostVetoed is null)
            {
                return;
            }

            await SetModeAsync(hostVetoed, "allow");

            // 宿主侧否决：应用说"不许关"就该立刻生效，不该先问页面。
            // 只挂在这个窗口上，不影响其它探针。
            hostVetoed.Closing += args => args.Cancel = true;

            hostVetoed.Close();
            await Task.Delay(800);

            Check(
                CountRequested("close-hostveto") == 0,
                "宿主否决优先：页面不该收到 close-requested（宿主的否决已经生效，不该再打扰页面）");

            Check(
                app.Windows.Contains(hostVetoed),
                "宿主否决优先：窗口必须留在打开状态");

            hostVetoed.Destroy();
            await WaitForAsync(() => !app.Windows.Contains(hostVetoed), 5000);
            Check(!app.Windows.Contains(hostVetoed), "第 3 个窗口应能被强制关掉");

            // ---- 4. 未接管：直接关掉，页面不该收到事件 ----
            WebviewWindow? plain = await CreateProbeAsync(app, "close-plain", intercept: false);
            Check(plain is not null, "第 4 个探针窗口应建起来");
            if (plain is null)
            {
                return;
            }

            await SetModeAsync(plain, "allow");
            plain.Close();

            await WaitForAsync(() => !app.Windows.Contains(plain), 8000);
            Check(!app.Windows.Contains(plain), "未接管：窗口应当直接关掉");
            Check(
                CountRequested("close-plain") == 0,
                "未接管：页面不该收到 close-requested（没开接管就不该拦）");
        }
        catch (Exception ex)
        {
            Check(false, $"自检过程抛出异常：{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _watchdog?.Dispose();
            Report();
            _main!.PostToUiThread(_app!.Quit);
        }
    }

    /// <summary>建一个探针窗口并等它的页面就绪。</summary>
    private static async Task<WebviewWindow?> CreateProbeAsync(OrielApp app, string label, bool intercept)
    {
        WebviewWindow? created = null;
        try
        {
            created = app.CreateWindow(
                w => w.WithLabel(label)
                      .WithTitle("关闭探针")
                      .WithSize(360, 260)
                      .WithFrameless()
                      .WithInterceptClose(intercept)
                      .WithUrl(ProbeUrl),
                // **订阅必须在 onCreated**：探针页面在解析时就发 ready，
                // 等 Loaded 再订阅会漏掉它（C3 的 event 自检踩过同一个坑）。
                win => win.MessageReceived += args => OnMessage(label, args));
        }
        catch (Exception ex)
        {
            Check(false, $"建探针窗口 {label} 失败：{ex.Message}");
            return null;
        }

        created.Closed += () =>
        {
            lock (Gate)
            {
                ClosedWindows.Add(label);
            }
        };

        await WaitForAsync(() => IsReady(label), 20000);
        Check(IsReady(label), $"探针窗口 {label} 的页面应回报 ready");
        return IsReady(label) ? created : null;
    }

    /// <summary>设页面的关闭响应模式（allow / prevent）。</summary>
    /// <remarks>
    /// 不用 <c>JsonSerializer.Serialize</c>：它走反射，在 Native AOT 下给出
    /// IL2026/IL3050（demo 就是 AOT 发布的，C3 的 event 自检踩过同一个坑）。
    /// 值只有两个内部常量，手写一个最小转义即可。
    /// </remarks>
    private static async Task SetModeAsync(WebviewWindow window, string mode)
    {
        await window.EvaluateJs("window.__closeMode = \"" + EscapeJson(mode) + "\";");
    }

    /// <summary>最小 JSON 字符串转义（值是我们自己的常量，但这层不该依赖那个前提）。</summary>
    private static string EscapeJson(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length + 4);
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

    private static void OnMessage(string label, OrielMessageReceivedEventArgs args)
    {
        switch (args.Name)
        {
            case "close.probe.ready":
                lock (Gate)
                {
                    Ready.Add(label);
                }

                break;

            case "close.probe.requested":
                lock (Gate)
                {
                    RequestedCounts[label] = RequestedCounts.TryGetValue(label, out int n) ? n + 1 : 1;
                }

                break;

            case "close.probe.error":
                Check(false, $"探针窗口 {label} 的 win.destroy 失败：{args.Json}");
                break;
        }
    }

    private static bool IsReady(string label)
    {
        lock (Gate)
        {
            return Ready.Contains(label);
        }
    }

    private static int CountRequested(string label)
    {
        lock (Gate)
        {
            return RequestedCounts.TryGetValue(label, out int n) ? n : 0;
        }
    }

    private static void OnTimeout()
    {
        string snapshot;
        lock (Gate)
        {
            snapshot = string.Join(", ", RequestedCounts.Select(kv => $"{kv.Key}={kv.Value}"));
        }

        Check(false, $"关闭自检超时：120 秒内没走完（已就绪={string.Join("/", Ready)}, 关闭请求回显={snapshot}）");
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
                Console.WriteLine($"[close-selftest] OK: {description}");
                return;
            }

            Failures.Add(description);
            Console.WriteLine($"[close-selftest] 失败: {description}");
        }
    }

    private static void Report()
    {
        lock (Gate)
        {
            if (_finished)
            {
                return;
            }

            _finished = true;

            if (Failures.Count > 0)
            {
                Console.WriteLine($"CLOSE-SELFTEST: FAIL（{Failures.Count} 项）");
            }
            else
            {
                Console.WriteLine("CLOSE-SELFTEST: PASS");
            }
        }

        Console.Out.Flush();
    }
}
