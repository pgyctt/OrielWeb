using OrielWeb;

namespace OrielDemo;

/// <summary>
/// <c>oriel://</c> 这条投递通道的**事实探针**：把页面的来源与几项"来源决定的能力"如实打出来，
/// 并断言其中两条设计直接承诺的东西。
/// </summary>
/// <remarks>
/// <para>
/// 为什么需要它：三平台的自定义 scheme 能力并不对称，而且不对称的部分**恰好是看不见的**。
/// Windows 有 <c>CoreWebView2CustomSchemeRegistration.TreatAsSecure</c>、Linux 有
/// <c>webkit_security_manager_register_uri_scheme_as_secure</c>，**macOS 的公开 API 里没有对应开关**。
/// 也就是说 <c>window.isSecureContext</c>（连带 <c>crypto.subtle</c> 这类只在安全上下文可用的 API）
/// 在 macOS 上是什么值，没人量过。以前这类事实全靠"上游文档说"，那是猜测。
/// </para>
/// <para>
/// 断言什么、只报告什么（这条分界线是刻意的）：
/// </para>
/// <list type="bullet">
///   <item><b>断言</b>（不满足即失败）：页面来源是 <c>oriel://&lt;host&gt;</c>——可信前缀必须与真实来源
///     逐字节一致，否则桥接不安装；以及**相对路径 <c>fetch</c> 能取到内嵌资源**——"三平台同一套来源"
///     这个设计承诺的实际用途就在这里，取不到就等于承诺没兑现。</item>
///   <item><b>只报告</b>：<c>isSecureContext</c>、<c>crypto.subtle</c>、<c>localStorage</c>。
///     它们可能是平台差异而不是缺陷，把它们做成失败会让 CI 用一个已知的不对称长期红灯，
///     于是没人再看它。所以它们进日志 + 进 GitHub 注解（注解**不需要 token** 就能读，
///     而 CI 日志需要——2026-10-02 排查 macOS 冒烟时，正是这个差别决定了问题能不能查下去）。</item>
/// </list>
/// </remarks>
internal static class SchemeSelfTest
{
    internal static bool Failed { get; private set; }

    private static WebviewWindow? _window;
    private static string _host = "app.oriel";
    private static int _started;
    private static int _finished;

    /// <summary>在 <c>onCreated</c> 里调用（与其它 Attach 型自检一致）。</summary>
    internal static void Attach(WebviewWindow window, string host)
    {
        _window = window;
        _host = host;
        window.Loaded += () => _ = ProbeAsync();
    }

    /// <summary>
    /// 一阶段脚本：读四项同步事实，并起一次相对路径的 fetch。
    /// </summary>
    /// <remarks>
    /// fetch 是异步的，而 <c>EvaluateJs</c> **不会等 Promise**（它拿回的是一个空的 <c>{}</c>），
    /// 所以结果先写进页面全局，二阶段轮询取回。返回值是一串 <c>key=value</c>（换行分隔）——
    /// 比嵌一层 JSON 好读，日志里直接就能看。
    /// </remarks>
    private const string StartScript = """
        (function () {
          var facts = [];
          function add(k, v) { facts.push(k + '=' + v); }
          add('href', location.href);
          add('origin', location.origin);
          add('secure', window.isSecureContext === true ? 'true' : 'false');
          add('subtle', (window.crypto && window.crypto.subtle) ? 'true' : 'false');
          try {
            localStorage.setItem('oriel.probe', '1');
            add('storage', localStorage.getItem('oriel.probe') === '1' ? 'ok' : 'mismatch');
            localStorage.removeItem('oriel.probe');
          } catch (e) {
            add('storage', 'throw:' + (e && e.name));
          }
          window.__orielSchemeProbe = { fetch: 'pending' };
          fetch('about.html', { cache: 'no-store' })
            .then(function (r) {
              return r.text().then(function (t) {
                window.__orielSchemeProbe.fetch = 'ok:' + r.status + ':' + t.length;
              });
            })
            .catch(function (e) {
              window.__orielSchemeProbe.fetch = 'fail:' + (e && e.name) + ':' + (e && e.message);
            });
          return facts.join(';');
        })()
        """;

    private static async Task ProbeAsync()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return;
        }

        var facts = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (string line in Unwrap(await EvaluateAsync(StartScript)).Split(';'))
            {
                int split = line.IndexOf('=', StringComparison.Ordinal);
                if (split > 0)
                {
                    facts[line[..split]] = line[(split + 1)..];
                }
            }

            // 二阶段：等 fetch 落地。给足 3 秒——它是本机同源请求，正常是毫秒级；
            // 超时本身就说明"这条通道取不到资源"，按失败处理（也就没等到结果）。
            string fetch = "pending";
            for (int i = 0; i < 12 && fetch == "pending"; i++)
            {
                await Task.Delay(250);
                fetch = Unwrap(await EvaluateAsync("window.__orielSchemeProbe.fetch"));
            }

            facts["fetch"] = fetch;
        }
        catch (Exception ex)
        {
            facts["probe"] = $"{ex.GetType().Name}: {ex.Message}";
        }

        Report(facts);
    }

    private static void Report(Dictionary<string, string> facts)
    {
        string Get(string key) => facts.TryGetValue(key, out string? value) ? value : "(未取到)";

        string href = Get("href");
        string origin = Get("origin");
        string secure = Get("secure");
        string subtle = Get("subtle");
        string storage = Get("storage");
        string fetch = Get("fetch");

        Console.WriteLine($"[scheme-selftest] PAGE-URL: {href}");
        Console.WriteLine($"[scheme-selftest] ORIGIN: {origin}");
        Console.WriteLine($"[scheme-selftest] IS-SECURE-CONTEXT: {secure}");
        Console.WriteLine($"[scheme-selftest] CRYPTO-SUBTLE: {subtle}");
        Console.WriteLine($"[scheme-selftest] LOCAL-STORAGE: {storage}");
        Console.WriteLine($"[scheme-selftest] FETCH-RELATIVE: {fetch}");
        if (facts.TryGetValue("probe", out string? probeError))
        {
            Console.WriteLine($"[scheme-selftest] PROBE-ERROR: {probeError}");
        }

        string expectedOrigin = $"oriel://{_host}";
        var problems = new List<string>();

        if (!string.Equals(origin, expectedOrigin, StringComparison.Ordinal))
        {
            problems.Add($"页面来源是 {origin}，期望 {expectedOrigin}（可信前缀与真实来源不一致 → 桥接不会安装）");
        }

        if (!fetch.StartsWith("ok:", StringComparison.Ordinal))
        {
            problems.Add($"相对路径 fetch 没有取到内嵌资源：{fetch}");
        }

        // 平台差异如实上报，不算失败——见类注释里的分界线。
        var notes = new List<string>();
        if (secure != "true")
        {
            notes.Add("isSecureContext=false（crypto.subtle 这类 API 在页面里不可用；" +
                       "macOS 的自定义 scheme 没有公开的「标记为安全」开关，这是已知的不对称）");
        }
        if (storage is not "ok")
        {
            notes.Add($"localStorage={storage}（页面无法持久化本地状态）");
        }

        string summary = $"origin={origin} secure={secure} subtle={subtle} storage={storage} fetch={fetch}";
        Console.WriteLine($"[scheme-selftest] FACTS: {summary}");
        foreach (string note in notes)
        {
            Console.WriteLine($"[scheme-selftest] NOTE: {note}");
        }

        // 注解：CI 上把它写进 check-run，读注解不需要 token（读日志需要）。
        // 事实与结论一起给，避免"只看到 PASS 却不知道这台机器上到底是什么样"。
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") is { Length: > 0 })
        {
            Console.WriteLine(problems.Count == 0
                ? $"::notice title=oriel:// 事实探针（{Platform()}）::{summary}"
                : $"::warning title=oriel:// 事实探针（{Platform()}）::{summary}");
        }

        if (problems.Count == 0)
        {
            Console.WriteLine("SCHEME-SELFTEST: PASS");
        }
        else
        {
            Failed = true;
            Console.WriteLine($"SCHEME-SELFTEST: FAIL（{problems.Count} 项）");
            foreach (string problem in problems)
            {
                Console.WriteLine("  - " + problem);
            }
        }

        Console.Out.Flush();
        Finish();
    }

    private static string Platform()
        => OperatingSystem.IsWindows() ? "windows"
            : OperatingSystem.IsMacOS() ? "macos"
            : OperatingSystem.IsLinux() ? "linux"
            : "unknown";

    private static async Task<string> EvaluateAsync(string script)
    {
        // 与 NavSelfTest 一样给求值加超时：挂住时自检必须还能报出结论，而不是等外层被 kill。
        Task<string> task = _window!.EvaluateJs(script);
        if (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(5))) != task)
        {
            throw new TimeoutException("EvaluateJs 5 秒未返回");
        }

        return await task;
    }

    /// <summary>
    /// 剥掉 <c>EvaluateJs</c> 那层 JSON 编码（字符串值回来时带引号），拿到脚本里的原始字符串。
    /// </summary>
    /// <remarks>
    /// 刻意不用 <c>JsonSerializer</c>：示例是 **AOT 发布**的，动态反序列化会带
    /// <c>IL2026</c>/<c>IL3050</c> 两条警告，而这里根本不需要 JSON 解析——探针的返回值里只有
    /// URL、布尔与短标识，不含引号与反斜杠，所以"剥掉外层引号"就够了。多出来的好处是
    /// 分隔符可以用 <c>;</c> 而不是 <c>\n</c>（后者在 JSON 串里会变成转义的两个字符）。
    /// </remarks>
    private static string Unwrap(string raw)
    {
        string trimmed = raw.Trim();
        return trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"'
            ? trimmed[1..^1]
            : trimmed;
    }

    private static void Finish()
    {
        if (Interlocked.Exchange(ref _finished, 1) == 1)
        {
            return;
        }

        // 优雅退出：关窗口 → 消息循环结束 → Run() 返回 → Program 按 Failed 设退出码。
        // 不在求值的续体里直接 Environment.Exit（那会跳过各平台的清理）。
        WebviewWindow? window = _window;
        if (window is not null)
        {
            window.App.PostToMainThread(window.Close);
        }
    }
}
