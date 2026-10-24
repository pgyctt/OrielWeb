using System.Text.Json;
using OrielWeb;

namespace OrielConformance;

/// <summary>
/// 页面建窗自检（<c>--selftest windownew</c>）：<c>window.new</c> 真的能建出一个窗口。
/// </summary>
/// <remarks>
/// <para>
/// 单元测试只能验到"命令认得出、门禁拦得住、未 Run 时明确报错"——**"窗口真的建出来了"
/// 只能在真实消息循环里取证**，这正是这个自检存在的理由。
/// </para>
/// <para>
/// 四条断言：
/// <list type="number">
///   <item>页面发 <c>window.new</c> 后，宿主侧窗口数从 1 变成 2；</item>
///   <item>返回的 label 与请求的一致（页面的寻址单位就是它）；</item>
///   <item>新窗口的 label 能在 <see cref="OrielApp.GetWindow"/> 查到（真的进了应用记账）；</item>
///   <item>用同一个 label 再建一次必须**失败**——label 是寻址单位，重复会让
///     <c>emitTo</c> / 能力配置指向不确定的窗口。</item>
/// </list>
/// </para>
/// <para>
/// 结论行：<c>WINDOWNEW-SELFTEST: PASS</c> / <c>FAIL（…）</c>。
/// </para>
/// </remarks>
internal static class WindowNewSelfTest
{
    private const string Echo = "windownew-echo";
    private const string SpawnedLabel = "spawned";

    private static readonly List<string> Failures = [];
    private static readonly object Gate = new();

    private static WebviewWindow? _main;
    private static Timer? _watchdog;
    private static bool _finished;
    private static bool _createdSeen;
    private static bool _dupSeen;

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

    /// <summary>在 <c>Loaded</c> 里调用：这时 <c>App</c> 才可用（能建窗）。</summary>
    internal static void Begin(OrielApp app, WebviewWindow main)
    {
        _main = main;

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
                        $"页面建窗自检超时（60 秒）：created 回执={_createdSeen}，重复 label 回执={_dupSeen}");
                }

                main.PostToUiThread(Finish);
            },
            null,
            TimeSpan.FromSeconds(60),
            Timeout.InfiniteTimeSpan);

        _ = app;

        // 用 EvaluateJs 驱动页面发命令，而不是在宿主侧直接 CreateWindow：
        // 本自检验的正是"页面 → IPC → 建窗"这条路径，绕过 IPC 就等于没验。
        _ = main.EvaluateJs("""
            oriel.invoke('window.new', {
                label: 'spawned',
                title: 'Spawned',
                width: 420,
                height: 320,
                url: 'https://app.oriel/about.html'
            })
              .then((id) => oriel.postMessage('windownew-echo', { step: 'created', label: id }))
              .catch((e) => oriel.postMessage('windownew-echo', {
                  step: 'created', error: String((e && e.message) || e) }));
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
            return;
        }

        string step = root.TryGetProperty("step", out JsonElement stepElement)
            ? stepElement.GetString() ?? string.Empty
            : string.Empty;

        if (step == "created")
        {
            HandleCreated(root);
        }
        else if (step == "dup")
        {
            HandleDuplicate(root);
        }
    }

    private static void HandleCreated(JsonElement root)
    {
        lock (Gate)
        {
            if (_createdSeen)
            {
                return;
            }

            _createdSeen = true;
        }

        if (root.TryGetProperty("error", out JsonElement error))
        {
            Check(false, $"页面建窗失败：{error.GetString()}");
            _main?.PostToUiThread(Finish);
            return;
        }

        string? label = root.TryGetProperty("label", out JsonElement labelElement)
            ? labelElement.GetString()
            : null;

        Check(label == SpawnedLabel, $"window.new 应当返回请求的 label「{SpawnedLabel}」，实际「{label}」");

        OrielApp? app = _main?.App;
        Check(app is not null, "拿不到应用实例");
        if (app is not null)
        {
            Check(app.Windows.Count == 2, $"建窗后应当有 2 个窗口，实际 {app.Windows.Count}");
            Check(app.GetWindow(SpawnedLabel) is not null, "新窗口没有进应用记账（GetWindow 查不到）");
        }

        // 第二个断言：**同一个 label 再建一次必须失败**。
        _ = _main!.EvaluateJs($$"""
            oriel.invoke('window.new', { label: '{{SpawnedLabel}}' })
              .then(() => oriel.postMessage('windownew-echo', { step: 'dup', label: 'no-error' }))
              .catch((e) => oriel.postMessage('windownew-echo', {
                  step: 'dup', error: String((e && e.message) || e) }));
            """);
    }

    private static void HandleDuplicate(JsonElement root)
    {
        lock (Gate)
        {
            if (_dupSeen)
            {
                return;
            }

            _dupSeen = true;
        }

        if (root.TryGetProperty("label", out JsonElement labelElement)
            && labelElement.GetString() == "no-error")
        {
            Check(false, "重复 label 竟然建成功了——label 是寻址单位，重复会让 emitTo 指向不确定的窗口");
        }
        else
        {
            string message = root.TryGetProperty("error", out JsonElement error)
                ? error.GetString() ?? string.Empty
                : string.Empty;

            Check(message.Contains("label", StringComparison.OrdinalIgnoreCase),
                $"重复 label 的报错没提到 label，页面无法归因：{message}");
        }

        _main?.PostToUiThread(Finish);
    }

    private static void Check(bool condition, string description)
    {
        if (condition)
        {
            return;
        }

        lock (Gate)
        {
            Failures.Add(description);
        }
    }

    private static void Finish()
    {
        int failureCount;
        lock (Gate)
        {
            if (_finished)
            {
                return;
            }

            _finished = true;

            // 建出来的窗口要收掉：自检退出时它是唯一可能阻止进程结束的东西
            // （Windows 上由存活窗口计数归零触发 WM_QUIT）。
            _main?.App.GetWindow(SpawnedLabel)?.Close();

            failureCount = Failures.Count;
            Console.WriteLine(failureCount == 0
                ? "WINDOWNEW-SELFTEST: PASS"
                : $"WINDOWNEW-SELFTEST: FAIL（{failureCount} 项）");
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
