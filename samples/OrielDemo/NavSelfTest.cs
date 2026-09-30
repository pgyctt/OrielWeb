using OrielWeb;

namespace OrielDemo;

/// <summary>
/// 导航自检：把「前进 / 后退 / 刷新 + 导航事件（开始 / 完成 / 失败）」变成无人交互也可判定的断言。
/// </summary>
/// <remarks>
/// 由 <c>--selftest nav</c> 启用。放在 demo 而不是测试工程，是因为这些行为只在**真实 webview** 里
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
    /// 触发失败导航的目标候选，按顺序尝试，**任一候选真的上报失败即算通过**。
    /// </summary>
    /// <remarks>
    /// 为什么不赌单一形式：三个引擎对"不可达地址"的处理差异极大，且各自踩过坑——
    /// 跨协议/跨源地改 <c>location</c>（如从 <c>https://app.oriel/</c> 跳到 <c>file:///…</c>）会被
    /// 按安全策略改写成"重新加载首页并成功"（Windows、Linux 均实测）；页面里用
    /// <c>new URL(相对路径, location.href)</c> 在 macOS 上静默无响应；而 macOS 用
    /// <c>loadFileURL:allowingReadAccessToURL:</c> 加载的沙箱内，导航到不存在的文件同样无事件。
    /// 占位 <c>{base}</c> 替换为刚完成那次导航的 URL 目录部分。
    /// </remarks>
    private static readonly string[] UnreachableCandidates =
    [
        "{base}oriel-selftest-nonexistent-page.html",   // 同源同目录但不存在（Windows 与 Linux 实测可行）
        "https://127.0.0.1:1/oriel-selftest",           // 环回端口 1：连接必定被拒绝，不依赖 DNS 与同源
        "https://oriel-selftest.invalid/page.html",     // RFC 6761 保留 TLD：域名必定解析失败
    ];

    /// <summary>取 URL 的目录部分（含末尾 '/'）；取不到时返回空串（候选里的 {base} 即退化为相对路径）。</summary>
    private static string BuildBaseUrl(string url)
    {
        int slash = url.LastIndexOf('/');
        return slash < 0 ? string.Empty : url[..(slash + 1)];
    }

    /// <summary>
    /// 试下一个候选目标：15 秒内没等到完成事件就换下一个。
    /// 计时器在线程池触发，所以回调必须经 <see cref="WebviewWindow.PostToUiThread"/> 切回 UI 线程。
    /// </summary>
    private static void TryNextCandidate()
    {
        if (_candidateIndex >= UnreachableCandidates.Length)
        {
            Check(false, "没有任何候选目标产生导航失败事件（逐个尝试的过程见上方输出）");
            Finish();
            return;
        }

        string target = UnreachableCandidates[_candidateIndex].Replace("{base}", _baseUrl, StringComparison.Ordinal);
        _candidateIndex++;
        Console.WriteLine($"[nav-selftest] 尝试失败目标 #{_candidateIndex}：{target}");

        _startingBeforeStep = Volatile.Read(ref _startingCount);
        _step = 5;
        _ = _window!.EvaluateJs($"location.href='{target}'");

        _candidateTimer?.Dispose();
        _candidateTimer = new Timer(
            _ => _window?.PostToUiThread(TryNextCandidate),
            null,
            TimeSpan.FromSeconds(15),
            Timeout.InfiniteTimeSpan);
    }

    private static readonly List<string> Failures = [];
    private static readonly object Gate = new();

    private static WebviewWindow? _window;
    private static Timer? _watchdog;
    private static Timer? _candidateTimer;
    private static int _startingCount;
    private static int _startingBeforeStep;
    private static int _step;
    private static int _candidateIndex;
    private static string _baseUrl = string.Empty;
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
                    _baseUrl = BuildBaseUrl(args.Url);
                    _candidateIndex = 0;
                    TryNextCandidate();
                    break;

                case 5: // 尝试一个应当失败的目标
                    _candidateTimer?.Dispose();
                    if (args.Success)
                    {
                        // 引擎把这次导航改写成了"成功"（例如跨源时重载了当前页）——换下一个候选，
                        // 不能把这种结果当成"失败路径已验证"。
                        Console.WriteLine("[nav-selftest] 该目标被引擎改写成了成功导航，换下一个候选");
                        TryNextCandidate();
                        break;
                    }

                    // Linux 上加载失败后 WebKit 仍会渲染错误页并发一次 load-changed(FINISHED)，
                    // 实现里已抑制那一次"成功"上报，所以这里期望正好是一条失败。
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
        _candidateTimer?.Dispose();

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
