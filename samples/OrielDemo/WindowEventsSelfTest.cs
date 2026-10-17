using OrielWeb;

namespace OrielDemo;

/// <summary>
/// 窗口几何/焦点事件自检（<c>--selftest windowevents</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 取证 P0-3 的宿主侧事件：<see cref="WebviewWindow.Resized"/> / <see cref="WebviewWindow.Moved"/> /
/// <see cref="WebviewWindow.FocusChanged"/>。这三者在三平台的原生来源完全不同
/// （Windows <c>WM_SIZE</c>/<c>WM_MOVE</c>/<c>WM_SETFOCUS</c>+<c>WM_KILLFOCUS</c>、
/// Linux <c>configure-event</c>/<c>focus-in-event</c>+<c>focus-out-event</c>、
/// macOS <c>windowDidResize:</c>/<c>windowDidMove:</c>/<c>windowDidBecomeKey:</c>+<c>windowDidResignKey:</c>），
/// 所以必须逐平台真机取证——"我接上了信号"与"事件真的到了"是两件事。
/// </para>
/// <para>
/// <b>断言的是"确实变化了"，不是"恰好等于某个值"</b>：窗口管理器会做吸附、约束、
/// 甚至拒绝移动（Wayland 下合成器说了算），钉死具体坐标会在别人的桌面上假红。
/// 唯一的例外是尺寸——那是库自己的契约（客户区语义），由 <c>--selftest geometry</c> 逐像素断言。
/// </para>
/// <para>
/// 结论行：<c>WINDOWEVENTS-SELFTEST: PASS</c> / <c>FAIL（…）</c>。
/// </para>
/// </remarks>
internal static class WindowEventsSelfTest
{
    private static int _failures;

    /// <summary>本项是否失败——demo 的 Main 据此设置退出码。</summary>
    internal static bool Failed => Volatile.Read(ref _failures) > 0;

    private static readonly List<string> Log = [];

    /// <summary>在窗口 <c>Loaded</c>（UI 线程、页面已就绪）时挂上本自检。</summary>
    internal static void Attach(OrielApp app, WebviewWindow window)
    {
        _ = RunAsync(app, window);
    }

    private static async Task RunAsync(OrielApp app, WebviewWindow window)
    {
        try
        {
            // ---- 1. Resized：程序调 Resize 必须触发 ----
            var sizeEvents = new List<OrielSizeChangedEventArgs>();
            var positionEvents = new List<OrielPositionChangedEventArgs>();
            var focusEvents = new List<bool>();

            void OnResized(OrielSizeChangedEventArgs e)
            {
                lock (sizeEvents)
                {
                    sizeEvents.Add(e);
                }
            }

            void OnMoved(OrielPositionChangedEventArgs e)
            {
                lock (positionEvents)
                {
                    positionEvents.Add(e);
                }
            }

            void OnFocus(bool focused)
            {
                lock (focusEvents)
                {
                    focusEvents.Add(focused);
                }
            }

            window.Resized += OnResized;
            window.Moved += OnMoved;
            window.FocusChanged += OnFocus;

            try
            {
                // 先等一拍，让建窗期可能已排队的 configure/resize 事件落地，
                // 否则它们会混进"程序调 Resize 之后"的计数里，把断言变得不可靠。
                await Task.Delay(300);

                int sizeBefore = Count(sizeEvents);
                window.Resize(700, 520);
                await WaitForAsync(() => Count(sizeEvents) > sizeBefore, 2000);

                (double lastWidth, double lastHeight) = LastSize(sizeEvents);
                Check(
                    Count(sizeEvents) > sizeBefore,
                    "Resize(700, 520) 应触发 Resized 事件");
                Check(
                    Math.Abs(lastWidth - 700) <= 2 && Math.Abs(lastHeight - 520) <= 2,
                    $"Resized 的参数应是逻辑像素的客户区（期望 ≈700x520，实际 {lastWidth:0.#}x{lastHeight:0.#}）");

                // ---- 2. Moved：程序调 MoveTo 必须触发 ----
                int moveBefore = Count(positionEvents);
                window.MoveTo(120, 90);
                await WaitForAsync(() => Count(positionEvents) > moveBefore, 2000);

                (double lastX, double lastY) = LastPosition(positionEvents);
                Check(
                    Count(positionEvents) > moveBefore,
                    "MoveTo(120, 90) 应触发 Moved 事件");

                // 位置只断言"报出来了且接近请求值"：窗口管理器可能吸附/约束，
                // 硬钉精确坐标会在别人的桌面上假红。
                Check(
                    Math.Abs(lastX - 120) <= 40 && Math.Abs(lastY - 90) <= 40,
                    $"Moved 的参数应是屏幕逻辑坐标（期望 ≈120,90，实际 {lastX:0.#},{lastY:0.#}；" +
                    "偏差过大可能被窗口管理器吸附或约束）");

                // ---- 3. FocusChanged：失焦/获焦必须触发 ----
                // 造焦点的唯一可靠办法是**再建一个窗口并让它获得焦点**——那个新窗口会抢走焦点，
                // 本窗口收到 false；关掉它，本窗口重新获焦，收到 true。
                // 直接调 SetFocus 测不出"失焦"，而失焦正是这个事件最容易写错的一半。
                int focusBefore = Count(focusEvents);
                WebviewWindow? other = null;
                try
                {
                    other = app.CreateWindow(
                        w => w.WithLabel("focus-probe")
                              .WithTitle("焦点探针")
                              .WithSize(320, 240)
                              .WithFrameless(),
                        created => created.Loaded += () => created.Focus());
                }
                catch (Exception ex)
                {
                    Check(false, $"为取证焦点变化而建窗失败：{ex.Message}");
                }

                if (other is not null)
                {
                    await WaitForAsync(() => Count(focusEvents) > focusBefore, 3000);
                    Check(
                        Count(focusEvents) > focusBefore,
                        "另一个窗口获得焦点时，本窗口应收到 FocusChanged(false)");

                    int afterBlur = Count(focusEvents);
                    other.Close();
                    await WaitForAsync(() => Count(focusEvents) > afterBlur, 3000);
                    Check(
                        Count(focusEvents) > afterBlur,
                        "另一个窗口关闭后，本窗口应收到 FocusChanged(true)");
                }

                // ---- 4. 去重：重复设同一尺寸不该反复触发 ----
                // 拖动时原生回调每个像素来一次，不去重会把一次拖动变成流量风暴。
                // 这里只验"同一个值连设两次只报一次"——那是去重逻辑最小的可观测形态。
                await Task.Delay(200);
                int dedupeBefore = Count(sizeEvents);
                window.Resize(700, 520);   // 与上面同一个尺寸
                await Task.Delay(300);
                Check(
                    Count(sizeEvents) == dedupeBefore,
                    $"重复设置同一尺寸不应再触发 Resized（之前 {dedupeBefore} 次，现在 {Count(sizeEvents)} 次）");
            }
            finally
            {
                window.Resized -= OnResized;
                window.Moved -= OnMoved;
                window.FocusChanged -= OnFocus;
            }
        }
        catch (Exception ex)
        {
            Check(false, $"自检过程抛出异常：{ex}");
        }
        finally
        {
            Report();
            window.PostToUiThread(app.Quit);
        }
    }

    private static int Count<T>(List<T> list)
    {
        lock (list)
        {
            return list.Count;
        }
    }

    private static (double Width, double Height) LastSize(List<OrielSizeChangedEventArgs> list)
    {
        lock (list)
        {
            return list.Count == 0 ? (0, 0) : (list[^1].Width, list[^1].Height);
        }
    }

    private static (double X, double Y) LastPosition(List<OrielPositionChangedEventArgs> list)
    {
        lock (list)
        {
            return list.Count == 0 ? (0, 0) : (list[^1].X, list[^1].Y);
        }
    }

    /// <summary>轮询等待条件成立（最多 <paramref name="timeoutMs"/> 毫秒）。</summary>
    /// <remarks>
    /// 不用固定 <c>Task.Delay</c>：原生事件是异步到达的，固定延时要么不够（假红）
    /// 要么太长（白等）。轮询 + 上限是唯一稳的形态。
    /// </remarks>
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
        lock (Log)
        {
            if (condition)
            {
                Console.WriteLine($"[windowevents-selftest] OK: {description}");
                return;
            }

            Interlocked.Increment(ref _failures);
            Log.Add(description);
            Console.WriteLine($"[windowevents-selftest] 失败: {description}");
        }
    }

    private static void Report()
    {
        if (Failed)
        {
            Console.WriteLine($"WINDOWEVENTS-SELFTEST: FAIL（{Volatile.Read(ref _failures)} 项）");
        }
        else
        {
            Console.WriteLine("WINDOWEVENTS-SELFTEST: PASS");
        }

        Console.Out.Flush();
    }
}
