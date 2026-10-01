using System.Text.Json;
using OrielWeb;

namespace OrielDemo;

/// <summary>
/// IPC 自检：把「页面 console → 宿主」「页面 postMessage → 宿主」「宿主 EmitEvent → 页面」三条
/// 通道变成无人交互也可判定的断言，并顺带确认注入脚本里的标题栏拖动接管可用。
/// </summary>
/// <remarks>
/// 由 <c>--selftest ipc</c> 启用（它会同时打开 <see cref="OrielWindowOptions.ConsoleForwarding"/>）。
/// 与 <see cref="NavSelfTest"/> 一样写成事件驱动状态机，避免 <c>await</c> 续体落到线程池后调用
/// 窗口 API（见 NavSelfTest 的说明）。
///
/// 第三条通道刻意做成闭环：宿主 EmitEvent → 页面 oriel.on 收到 → 页面再 postMessage 回宿主。
/// 只有整条链路通了才会收到那条回显，因此它同时验证了"推事件"和"收消息"两个方向。
///
/// 第四条（标题栏拖动接管）放在这里而不是单开一个自检：它断言的是"注入的脚本模板能跑通、
/// 拖动区域真的被登记上了"，与上面三条同属"桥接脚本可用"，不需要发动整个窗口一轮。
/// </remarks>
internal static class IpcSelfTest
{
    private static readonly List<string> Failures = [];
    private static readonly object Gate = new();

    private static WebviewWindow? _window;
    private static Timer? _watchdog;
    private static bool _finished;
    private static OrielConsoleMessageEventArgs? _console;
    private static OrielMessageReceivedEventArgs? _fromPage;
    private static OrielMessageReceivedEventArgs? _echo;
    private static OrielMessageReceivedEventArgs? _dragProbe;
    private static OrielMessageReceivedEventArgs? _builtIn;

    /// <summary>自检是否失败——demo 的 Main 据此设置进程退出码。</summary>
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

    /// <param name="window">窗口外观对象。</param>
    internal static void Attach(WebviewWindow window)
    {
        _window = window;

        window.ConsoleMessage += args =>
        {
            Console.WriteLine($"[ipc-selftest] console[{args.Level}] {args.Text}");
            _console ??= args;
            TryFinish();
        };

        window.MessageReceived += args =>
        {
            Console.WriteLine($"[ipc-selftest] message {args.Name} {args.Json}");
            if (args.Name == "from-page")
            {
                _fromPage ??= args;
            }
            else if (args.Name == "echo")
            {
                _echo ??= args;
            }
            else if (args.Name == "drag-region")
            {
                _dragProbe ??= args;
            }
            else if (args.Name == "builtin")
            {
                _builtIn ??= args;
            }
            TryFinish();
        };

        window.Loaded += Begin;
    }

    private static void Begin()
    {
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
                        $"IPC 自检超时：30 秒内没等到全部四条通道（console={_console is not null}, " +
                        $"from-page={_fromPage is not null}, echo={_echo is not null}, " +
                        $"drag-region={_dragProbe is not null}, builtin={_builtIn is not null}）");
                }
                _window?.PostToUiThread(Finish);
            },
            null,
            TimeSpan.FromSeconds(30),
            Timeout.InfiniteTimeSpan);

        WebviewWindow window = _window!;

        // 页面侧一次做完五件事：注册回显监听、打一条 console、发一条 postMessage、
        // 回传"拖动接管是否就位"（API 是不是函数 + 标题栏有没有被登记成拖动区域）、
        // 以及调用两次内建窗口命令（应用**没有**注册任何 win.* —— 它们由库内建）。
        // 用 toggleOnTop 而不是 minimize/close 这类：它可逆（调两次回原状），不会把窗口弄没。
        const string pageScript =
            "window.oriel.on('from-host', function (value) { window.oriel.postMessage('echo', value); });" +
            "console.log('from-page', 42);" +
            "window.oriel.postMessage('from-page', { n: 1 });" +
            "window.oriel.postMessage('drag-region', " +
            "{ api: typeof window.oriel.dragRegion, regions: window.oriel.dragRegion() });" +
            "window.oriel.invoke('win.toggleOnTop').then(function (a) {" +
            "  return window.oriel.invoke('win.toggleOnTop').then(function (b) {" +
            "    window.oriel.postMessage('builtin', { first: a, second: b });" +
            "  });" +
            "}, function (e) { window.oriel.postMessage('builtin', { error: String(e) }); });";

        // 闭环的第二步必须等页面注册完监听再做，否则事件先于监听发出就收不到。
        // EvaluateJs 的续体不在 UI 线程上，所以回 UI 线程再调 EmitEvent——这正是
        // WebviewWindow.PostToUiThread 的用途。
        _ = window.EvaluateJs(pageScript).ContinueWith(
            _ => _window?.PostToUiThread(() => _window.EmitEvent("from-host", "{\"k\":1}")),
            TaskScheduler.Default);
    }

    private static void TryFinish()
    {
        if (_finished || _console is null || _fromPage is null || _echo is null
            || _dragProbe is null || _builtIn is null)
        {
            return;
        }

        Check(_console.Level == "log", $"console 级别应为 log，实际为 \"{_console.Level}\"");
        Check(_console.Text == "from-page 42", $"console 文本应为 \"from-page 42\"，实际为 \"{_console.Text}\"");
        Check(_fromPage.Json.Contains("\"n\"", StringComparison.Ordinal), $"payload 应含字段 n，实际为 \"{_fromPage.Json}\"");
        Check(_echo.Json.Contains("\"k\"", StringComparison.Ordinal), $"回显 payload 应含字段 k，实际为 \"{_echo.Json}\"");
        CheckDragRegion(_dragProbe);
        CheckBuiltIn(_builtIn);
        Finish();
    }

    /// <summary>
    /// 断言内建窗口命令可用：应用**没有**注册任何 <c>win.*</c>，两次 <c>win.toggleOnTop</c>
    /// 都拿到了布尔回执。
    /// </summary>
    /// <remarks>
    /// 只断言"回执是布尔"，不写死 true/false：置顶是窗口管理器的自由裁量，值本身可能不听话；
    /// 这条要证明的是"内建命令被路由到了窗口、回执带回了它的返回值"——那条链路上没有一行应用代码。
    /// </remarks>
    private static void CheckBuiltIn(OrielMessageReceivedEventArgs probe)
    {
        try
        {
            JsonElement root = JsonDocument.Parse(probe.Json).RootElement;
            if (root.TryGetProperty("error", out JsonElement error))
            {
                Check(false, $"内建命令 win.toggleOnTop 调用失败：{error.GetString()}");
                return;
            }

            JsonElement first = root.GetProperty("first");
            JsonElement second = root.GetProperty("second");
            Check(
                first.ValueKind is JsonValueKind.True or JsonValueKind.False,
                $"内建命令第一次的返回值应是布尔，实际为 {first}");
            Check(
                second.ValueKind is JsonValueKind.True or JsonValueKind.False,
                $"内建命令第二次的返回值应是布尔，实际为 {second}");
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            Check(false, $"内建命令探针不是预期形状：{ex.Message}（原始：{probe.Json}）");
        }
    }

    /// <summary>
    /// 断言注入脚本里的标题栏拖动接管可用：API 是函数，且标题栏真的被登记成了拖动区域。
    /// </summary>
    /// <remarks>
    /// 这两条分别是"脚本模板里有这套逻辑"与"DOM 里标的 <c>data-oriel-drag-region</c> 被扫到了"
    /// （属性拼错、或扫描没在 DOMContentLoaded 时跑，区域数都会是 0）。
    /// 而拖动本身的**行为**（跟手、双击最大化、让开按钮）只能在真机上人眼验证——见 docs/ROADMAP.md。
    /// </remarks>
    private static void CheckDragRegion(OrielMessageReceivedEventArgs probe)
    {
        try
        {
            JsonElement root = JsonDocument.Parse(probe.Json).RootElement;
            string api = root.GetProperty("api").GetString() ?? string.Empty;
            int regions = root.GetProperty("regions").GetInt32();

            Check(api == "function", $"window.oriel.dragRegion 应当是函数，实际为 {api}");
            Check(regions >= 1, $"应当至少登记 1 个拖动区域（demo 的标题栏），实际为 {regions}");
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            Check(false, $"拖动区域探针不是预期形状：{ex.Message}（原始：{probe.Json}）");
        }
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
        lock (Gate)
        {
            if (_finished)
            {
                return;
            }
            _finished = true;
        }

        _watchdog?.Dispose();

        int failureCount;
        lock (Gate)
        {
            failureCount = Failures.Count;
            Console.WriteLine(failureCount == 0
                ? "IPC-SELFTEST: PASS"
                : $"IPC-SELFTEST: FAIL（{failureCount} 项）");
            foreach (string failure in Failures)
            {
                Console.WriteLine("  - " + failure);
            }
        }
        Console.Out.Flush();

        _window?.Close();
    }
}
