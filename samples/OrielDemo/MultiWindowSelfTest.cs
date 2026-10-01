using OrielWeb;

namespace OrielDemo;

/// <summary>
/// 多窗口自检：运行时新建窗口、每个窗口各有一套会话、关窗语义。
/// </summary>
/// <remarks>
/// 由 <c>--selftest multiwindow</c> 启用。与其它自检一样写成事件驱动状态机
/// （见 <see cref="NavSelfTest"/> 的说明）：窗口事件都在 UI 线程上，而 <c>await</c> 的续体会落到线程池。
/// <para>
/// 断言的四处恰好是多窗口最容易错的四个地方：
/// <list type="number">
/// <item>运行时新建的窗口真的建起来了（<see cref="OrielApp.CreateWindow"/>）；</item>
/// <item>每个窗口的页面各有自己的一套会话——消息从哪个窗口发出，就只有那个窗口收得到；</item>
/// <item>关掉其中一个窗口应用**不退出**，另一个窗口仍然可用；</item>
/// <item>内建的 <c>win.close</c> 只关掉**发起调用的那个**窗口，而不是所有窗口。</item>
/// </list>
/// </para>
/// </remarks>
internal static class MultiWindowSelfTest
{
    private const string SecondEcho = "mw-second";
    private const string FirstEcho = "mw-first";
    private const string AfterCloseEcho = "mw-after-close";

    private static readonly List<string> Failures = [];
    private static readonly object Gate = new();

    private static WebviewWindow? _first;
    private static WebviewWindow? _second;
    private static Timer? _watchdog;
    private static bool _finished;
    private static bool _secondEchoed;
    private static bool _firstEchoed;
    private static bool _afterCloseEchoed;
    private static bool _askedFirst;
    private static bool _closedSecond;

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

    /// <summary>在第一个窗口的 <c>onCreated</c> 里调用。</summary>
    internal static void Attach(WebviewWindow first)
    {
        _first = first;
        first.MessageReceived += args =>
        {
            if (args.Name == FirstEcho)
            {
                _firstEchoed = true;
            }
            else if (args.Name == AfterCloseEcho)
            {
                _afterCloseEchoed = true;
            }

            TryAdvance();
        };
        first.Loaded += Begin;
    }

    private static void Begin()
    {
        WebviewWindow first = _first!;

        _watchdog = new Timer(
            _ => OnTimeout(),
            null,
            TimeSpan.FromSeconds(60),
            Timeout.InfiniteTimeSpan);

        Check(first.App.Windows.Count == 1, $"启动时应只有 1 个窗口，实际 {first.App.Windows.Count}");

        try
        {
            // 新窗口指向**同一个 todo 页面**（内嵌资源首页）：多窗口要验证的是"各窗口有各自的页面
            // 与会话，而命令、能力、令牌是共用的"，以及内建 win.* 只作用到发起调用的那个窗口。
            _second = first.App.CreateWindow(
                w => w.WithTitle("Oriel Demo — 窗口 2")
                      .WithSize(560, 640)
                      .WithMinSize(420, 360)
                      .WithFrameless(),
                created =>
                {
                    created.MessageReceived += args =>
                    {
                        if (args.Name == SecondEcho)
                        {
                            _secondEchoed = true;
                        }

                        TryAdvance();
                    };
                    created.Loaded += OnSecondLoaded;
                    created.Closed += OnSecondClosed;
                });
        }
        catch (Exception ex)
        {
            Check(false, $"运行时新建窗口失败：{ex.Message}");
            Finish();
            return;
        }

        Check(_second is not null, "CreateWindow 应返回窗口对象");
        Check(first.App.Windows.Count == 2, $"新建后应有 2 个窗口，实际 {first.App.Windows.Count}");
    }

    private static void OnSecondLoaded()
        => _ = _second!.EvaluateJs($"window.oriel.postMessage('{SecondEcho}', {{ n: 2 }})");

    /// <summary>
    /// 按"收到第二个窗口的回显 → 问第一个窗口还在不在 → 从第二个窗口里调 win.close"推进。
    /// </summary>
    /// <remarks>
    /// 每一步都靠**上一步的回显**触发，而不是靠固定延时：多窗口装配是异步的（各窗口各自装配引擎），
    /// 用 sleep 就是在赌机器的快慢。
    /// </remarks>
    private static void TryAdvance()
    {
        if (_finished)
        {
            return;
        }

        if (_secondEchoed && !_askedFirst)
        {
            _askedFirst = true;
            // 此刻两个窗口都在：第一个窗口仍能收发，说明多窗口不是"后者顶替前者"
            _ = _first!.EvaluateJs($"window.oriel.postMessage('{FirstEcho}', {{ n: 1 }})");
            return;
        }

        if (_firstEchoed && !_closedSecond)
        {
            _closedSecond = true;
            // 从**第二个窗口**里调内建 win.close：它只该关掉发起调用的那个窗口。
            // 页面调它而不是宿主侧 _second.Close()，正是为了验证按窗口路由这条路径。
            _ = _second!.EvaluateJs("window.oriel.invoke('win.close')");
            return;
        }

        if (_afterCloseEchoed)
        {
            Check(
                _first!.App.Windows.Count == 1,
                $"关闭第二个窗口后应剩 1 个（关闭的窗口要从 Windows 里摘掉），实际 {_first.App.Windows.Count}");
            _first.PostToUiThread(Finish);
        }
    }

    private static void OnSecondClosed()
        // 关掉一个窗口后应用没退出（这一条由"下面这条回显还能收到"表达），且第一个窗口仍然可用。
        // **窗口计数不在这里查**：摘除发生在 Closed 的簿记里，而本处理器与它同属一次事件分发，
        // 顺序上本章可能先跑（见 WebviewWindow.ClosedBookkeeping）——计数留到回显之后再查。
        => _ = _first!.EvaluateJs($"window.oriel.postMessage('{AfterCloseEcho}', {{ n: 1 }})");

    private static void OnTimeout()
    {
        lock (Gate)
        {
            if (_finished)
            {
                return;
            }

            Failures.Add(
                $"多窗口自检超时：60 秒内没走完（second 回显={_secondEchoed}, first 回显={_firstEchoed}, " +
                $"第二个窗口已关={_closedSecond}, 关窗后回显={_afterCloseEchoed}）");
        }

        _first?.PostToUiThread(Finish);
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

            Check(_secondEchoed, "第二个窗口的页面没有回显（它自己的会话没跑通）");
            Check(_firstEchoed, "第一个窗口在第二个窗口存在时没有回显（多窗口下前者被顶掉了？）");
            Check(_afterCloseEchoed, "关掉第二个窗口后第一个窗口不再回显（关窗把应用也带走了？）");
        }

        _watchdog?.Dispose();

        int failureCount;
        lock (Gate)
        {
            failureCount = Failures.Count;
            Console.WriteLine(failureCount == 0
                ? "MULTIWINDOW-SELFTEST: PASS"
                : $"MULTIWINDOW-SELFTEST: FAIL（{failureCount} 项）");
            foreach (string failure in Failures)
            {
                Console.WriteLine("  - " + failure);
            }
        }
        Console.Out.Flush();

        // 关掉最后一个窗口 → 消息循环退出（Windows 侧由存活窗口计数归零触发 WM_QUIT）
        _first?.Close();
    }
}
