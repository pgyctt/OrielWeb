using OrielWeb;

namespace OrielDemo;

/// <summary>
/// IPC 自检：把「页面 console → 宿主」「页面 postMessage → 宿主」「宿主 EmitEvent → 页面」三条
/// 通道变成无人交互也可判定的断言。
/// </summary>
/// <remarks>
/// 由 <c>--selftest ipc</c> 启用（它会同时打开 <see cref="OrielWindowOptions.ConsoleForwarding"/>）。
/// 与 <see cref="NavSelfTest"/> 一样写成事件驱动状态机，避免 <c>await</c> 续体落到线程池后调用
/// 窗口 API（见 NavSelfTest 的说明）。
///
/// 第三条通道刻意做成闭环：宿主 EmitEvent → 页面 oriel.on 收到 → 页面再 postMessage 回宿主。
/// 只有整条链路通了才会收到那条回显，因此它同时验证了"推事件"和"收消息"两个方向。
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
                        $"IPC 自检超时：30 秒内没等到全部三条通道（console={_console is not null}, " +
                        $"from-page={_fromPage is not null}, echo={_echo is not null}）");
                }
                _window?.PostToUiThread(Finish);
            },
            null,
            TimeSpan.FromSeconds(30),
            Timeout.InfiniteTimeSpan);

        WebviewWindow window = _window!;

        // 页面侧一次做完三件事：注册回显监听、打一条 console、发一条 postMessage。
        const string pageScript =
            "window.oriel.on('from-host', function (value) { window.oriel.postMessage('echo', value); });" +
            "console.log('from-page', 42);" +
            "window.oriel.postMessage('from-page', { n: 1 });";

        // 闭环的第二步必须等页面注册完监听再做，否则事件先于监听发出就收不到。
        // EvaluateJs 的续体不在 UI 线程上，所以回 UI 线程再调 EmitEvent——这正是
        // WebviewWindow.PostToUiThread 的用途。
        _ = window.EvaluateJs(pageScript).ContinueWith(
            _ => _window?.PostToUiThread(() => _window.EmitEvent("from-host", "{\"k\":1}")),
            TaskScheduler.Default);
    }

    private static void TryFinish()
    {
        if (_finished || _console is null || _fromPage is null || _echo is null)
        {
            return;
        }

        Check(_console.Level == "log", $"console 级别应为 log，实际为 \"{_console.Level}\"");
        Check(_console.Text == "from-page 42", $"console 文本应为 \"from-page 42\"，实际为 \"{_console.Text}\"");
        Check(_fromPage.Json.Contains("\"n\"", StringComparison.Ordinal), $"payload 应含字段 n，实际为 \"{_fromPage.Json}\"");
        Check(_echo.Json.Contains("\"k\"", StringComparison.Ordinal), $"回显 payload 应含字段 k，实际为 \"{_echo.Json}\"");
        Finish();
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
