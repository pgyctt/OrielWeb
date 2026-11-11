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
///     <c>emitTo</c> / 能力配置指向不确定的窗口；</item>
///   <item>建完**立刻**销毁不崩、且该窗口从应用记账里消失（2026-11-11 评审 M9 的取证，
///     见 <c>HandleDuplicate</c> 末尾的说明——它证明的是"销毁与装配并发不会崩"）。</item>
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

    /// <summary>第五段"建完立刻销毁"用的 label。</summary>
    private const string LateLabel = "spawned-late";

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

        // ---- 第五段：建完立刻销毁（2026-11-11 评审 M9 的取证）----
        //
        // WebView2 的装配是一串 await（环境 → 控制器 → 脚本注入）。若销毁路径不清 _hwnd，
        // 那条链路醒来仍认为窗口活着，会拿一个已释放的 HWND 去建组合控制器——
        // 系统可能已经把那个值复用给别的窗口。
        //
        // 自检能取证的部分：**建完立刻销毁，进程不崩、窗口记账归零**。
        // 造不出的是"恰好卡在某个 await 中间"——那需要精确控制 WebView2 的回调时机，
        // 没有稳定的注入点。所以这里给的是"最短存活期"这个最接近的近似，并如实标注：
        // 它证明的是"销毁与装配并发不会崩"，不是"每个 await 都复查了"。
        // 后者由单测 ComOwnershipTests 按结构钉住（await 数与 WindowAlive 复查数必须匹配）。
        //
        // **用 Destroy 而不是 Close**（这一条在 Linux 上翻过车）：Close 走的是"关闭请求"
        // 语义——Linux 上是 gtk_window_close → delete-event，而窗口此刻还没映射到屏幕、
        // 页面也没就绪，GTK 那条路不保证走到 destroy（实测窗口留在记账里 5 秒以上）。
        // Destroy 绕过关闭请求直接销毁，三平台语义一致，正是这段要的"最短存活期"。
        _main?.PostToUiThread(() =>
        {
            OrielApp? app = _main?.App;
            if (app is null)
            {
                return;
            }

            WebviewWindow? late = app.CreateWindow(
                w => w.WithLabel(LateLabel).WithTitle("朝生暮死").WithSize(240, 180).WithUrl("about:blank"));

            Check(late is not null, "建窗返回 null（应当拿到窗口对象）");

            // **立刻**销毁：不 await、不延迟，尽量贴近装配还没完成的那一刻。
            late?.Destroy();

            // 等它从记账里消失。**轮询而不是固定等一拍**：销毁的落地时机是平台相关的
            // ——Windows 的 DestroyWindow 是同步的（WM_DESTROY 当场跑完清理），
            // 而 GTK 的销毁排在事件循环里，可能跨几帧。第一版写死 1200ms，Linux arm64
            // 上就是这么红的（断言本身没错，错的是"多久算够"）。
            //
            // 上限 5 秒：真泄漏的话等多久都不会消失，那时候再红才是对的。
            PollUntil(
                () => _main?.App.GetWindow(LateLabel) is null,
                TimeSpan.FromSeconds(5),
                disappeared =>
                {
                    Check(disappeared, "建完立刻销毁后，该窗口仍留在应用记账里（销毁路径没跑完）");
                    Finish();
                });
        });
    }

    /// <summary>在 UI 线程上轮询一个条件，直到成立或超时，然后回调一次结果。</summary>
    /// <remarks>
    /// 用 <see cref="Timer"/> 周期投递而不是 <c>await Task.Delay</c> 循环：
    /// 自检跑在宿主消息循环里，每次检查都必须回到 UI 线程
    /// （<see cref="OrielApp.GetWindow"/> 与窗口记账都不是线程安全的）。
    /// <paramref name="done"/> 保证只被调用一次——重复调用会把一条失败记成两条。
    /// </remarks>
    private static void PollUntil(Func<bool> condition, TimeSpan timeout, Action<bool> done)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        bool completed = false;
        Timer? timer = null;

        timer = new Timer(
            _ =>
            {
                _main?.PostToUiThread(() =>
                {
                    if (Volatile.Read(ref completed))
                    {
                        return;
                    }

                    bool ok;
                    try
                    {
                        ok = condition();
                    }
                    catch (Exception ex)
                    {
                        Check(false, $"轮询窗口记账时抛异常：{ex.GetType().Name}: {ex.Message}");
                        ok = true; // 不再重试，直接收尾
                    }

                    if (!ok && DateTime.UtcNow < deadline)
                    {
                        return; // 还没到，等下一次 Tick
                    }

                    if (Interlocked.Exchange(ref completed, true))
                    {
                        return;
                    }

                    timer!.Dispose();
                    done(ok);
                });
            },
            null,
            TimeSpan.FromMilliseconds(200),
            TimeSpan.FromMilliseconds(200));
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
