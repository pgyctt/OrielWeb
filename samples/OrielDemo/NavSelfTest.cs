using OrielWeb;

namespace OrielDemo;

/// <summary>
/// 导航自检：把「前进 / 后退 / 刷新 + 导航事件（开始 / 完成 / 失败）」变成无人交互也可判定的断言。
/// </summary>
/// <remarks>
/// 由 <c>--nav-selftest</c> 启用。放在 demo 而不是测试工程，是因为这些行为只在**真实 webview** 里
/// 存在——WebView2 / WebKitGTK / WKWebView 的历史与加载失败语义各不相同，单测覆盖不到。
///
/// 刻意写成事件驱动状态机而不是 async/await：窗口 API 必须在其 UI 线程上调用，而 <c>await</c> 之后
/// 的续体会落到线程池（GLib/AppKit 没有 .NET 的 SynchronizationContext），从线程池调 GTK/AppKit 会
/// 直接崩——第一版正是这么写的，跳转成功后就 abort 了。需要跨 await 编排的应用，用
/// <see cref="WebviewWindow.PostToUiThread"/> 回到 UI 线程再碰窗口（本类的看门狗演示了这种用法）。
///
/// 结论打在 stdout 上，并以进程退出码表达成败，CI 的冒烟脚本据此判定。
/// </remarks>
internal static class NavSelfTest
{
    private const string AboutUrl = "about.html";

    /// <summary>
    /// 必定加载失败的地址：一个不存在的本地文件。
    /// 刻意不用网络地址：在 WSL + WSLg 下实测过一次极反直觉的行为——请求
    /// <c>http://127.0.0.1:9/index.html</c>（连接必然被拒绝）时，WebKit 反而提交加载了**本地的
    /// about/index 页面**并报成功，`load-failed` 一次都没发出。本地不存在的 file: 路径不经过
    /// 网络与代理，失败的语义是确定的。
    /// </summary>
    private const string UnreachableUrl = "file:///nonexistent-oriel-selftest/page-xyz.html";

    private static readonly List<string> Failures = [];
    private static readonly object Gate = new();

    private static WebviewWindow? _window;
    private static Timer? _watchdog;
    private static int _startingCount;
    private static int _startingBeforeStep;
    private static int _step;
    private static bool _finished;

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

        window.NavigationStarting += url =>
        {
            Interlocked.Increment(ref _startingCount);
            Console.WriteLine($"[nav-selftest] starting: {url}");
        };

        window.NavigationCompleted += OnNavigationCompleted;

        // Loaded 只触发首次；此后每一步都由 NavigationCompleted 推进。
        window.Loaded += Begin;
    }

    private static void Begin()
    {
        // 看门狗：自检是无人值守路径，卡住时不能永远挂着。它在计时器线程上触发，
        // 所以收尾动作必须经 PostToUiThread 回到 UI 线程（窗口 API 不能跨线程调用）。
        _watchdog = new Timer(
            _ =>
            {
                lock (Gate)
                {
                    if (_finished)
                    {
                        return;
                    }
                    Failures.Add("导航自检超时：90 秒内没有走完全部步骤");
                }
                _window?.PostToUiThread(Finish);
            },
            null,
            TimeSpan.FromSeconds(90),
            Timeout.InfiniteTimeSpan);

        // 首次加载发生在后端 CreateWindow 内部，可能早于本类订阅事件——所以不做"首次一定收到
        // starting"的断言，只把计数当基线；后面每一步都断言事件确实又发生了。
        Check(!_window!.CanGoBack, "刚打开时应无法后退");

        _startingBeforeStep = Volatile.Read(ref _startingCount);
        _step = 1;
        _ = _window.EvaluateJs($"location.href='{AboutUrl}'");
    }

    private static void OnNavigationCompleted(OrielNavigationCompletedEventArgs args)
    {
        Console.WriteLine(
            $"[nav-selftest] completed: success={args.Success} url={args.Url} error={args.Error ?? "-"}");

        if (_finished)
        {
            return;
        }

        WebviewWindow window = _window!;
        try
        {
            switch (_step)
            {
                case 1: // 页面内跳转
                    CheckStartingSeen("跳转应触发 navigation.starting");
                    Check(args.Success, $"跳转到 {AboutUrl} 应成功（error={args.Error ?? "-"}）");
                    Check(args.Url.Contains(AboutUrl, StringComparison.Ordinal),
                        $"完成事件的 URL 应是 {AboutUrl}，实际为 \"{args.Url}\"");
                    Check(window.CanGoBack, "跳转之后应能后退");
                    Advance();
                    window.GoBack();
                    break;

                case 2: // 后退
                    CheckStartingSeen("后退应触发 navigation.starting");
                    Check(args.Success, "后退应成功");
                    Check(!args.Url.Contains(AboutUrl, StringComparison.Ordinal),
                        $"后退后应回到首页，实际为 \"{args.Url}\"");
                    Check(window.CanGoForward, "后退之后应能前进");
                    Advance();
                    window.GoForward();
                    break;

                case 3: // 前进
                    CheckStartingSeen("前进应触发 navigation.starting");
                    Check(args.Success, "前进应成功");
                    Check(args.Url.Contains(AboutUrl, StringComparison.Ordinal),
                        $"前进后应回到 {AboutUrl}，实际为 \"{args.Url}\"");
                    Advance();
                    window.Reload();
                    break;

                case 4: // 刷新
                    CheckStartingSeen("刷新应触发 navigation.starting");
                    Check(args.Success, "刷新应成功");
                    Advance();
                    _ = window.EvaluateJs($"location.href='{UnreachableUrl}'");
                    break;

                case 5: // 不可达地址
                    // WebKit 加载失败后仍会渲染错误页并发一次 load-changed(FINISHED)，实现里已抑制
                    // 那一次"成功"上报，所以这里期望正好是一条失败。
                    Check(!args.Success, "不可达地址应上报失败");
                    Check(!string.IsNullOrEmpty(args.Error), "失败应带上错误信息");
                    Finish();
                    break;
            }
        }
        catch (Exception ex)
        {
            lock (Gate)
            {
                Failures.Add($"自检过程抛出异常：{ex.GetType().Name}: {ex.Message}");
            }
            Finish();
        }
    }

    private static void Advance()
    {
        _startingBeforeStep = Volatile.Read(ref _startingCount);
        _step++;
    }

    private static void CheckStartingSeen(string description)
        => Check(Volatile.Read(ref _startingCount) > _startingBeforeStep, description);

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
                ? "NAV-SELFTEST: PASS"
                : $"NAV-SELFTEST: FAIL（{failureCount} 项）");
            foreach (string failure in Failures)
            {
                Console.WriteLine("  - " + failure);
            }
        }
        Console.Out.Flush();

        // 优雅退出：关窗口 → 消息循环结束 → Run() 返回 → Main 按 Failed 设置退出码。
        // 不在 GLib/AppKit 的调用栈里 Environment.Exit：那会跳过各自的清理（实测 GTK 下直接 abort）。
        _window?.Close();
    }
}
