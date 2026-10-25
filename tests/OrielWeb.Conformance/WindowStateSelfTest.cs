using OrielWeb;

namespace OrielConformance;

/// <summary>
/// 窗口状态自检（<c>--selftest windowstate</c>）：<c>WithHidden() + WithMaximized()</c> 的组合，
/// 以及 <see cref="WebviewWindow.IsVisible"/> 的隐藏/显示往返。
/// </summary>
/// <remarks>
/// <para>
/// 两条断言各自对应一个**已经发生过的缺陷**，不是"顺手多测一点"：
/// </para>
/// <list type="bullet">
///   <item><description>
///   <b>Hidden + Maximized</b>：创建期把"可见性"与"最大化"塞进一条三元链，Hidden 优先时
///   <c>SW_SHOWMAXIMIZED</c> 从未执行，<c>WS_MAXIMIZE</c> 因此从未建立——之后 <c>Show()</c>
///   的 <c>SW_SHOW</c> 只能把窗口按还原态显示（下游 2026-10-25 报，Windows 0.5.1）。
///   这对组合正好踩在 API.md 推荐的"首帧闸门"上，而工控/看板类应用几乎都要最大化启动，
///   命中率不低，却到 0.5.1 都没有任何断言。
///   </description></item>
///   <item><description>
///   <b><see cref="WebviewWindow.IsVisible"/></b>：它是为 0.3.4 那个 P0（<c>Hide()</c> 只对宿主发
///   <c>SW_HIDE</c>，WebView2 的顶层渲染窗口继续浮在桌面上吃点击）专门加的观测属性，
///   但加进来之后**没有任何断言**——同一段代码再退化一次，CI 不会红，只会等下游反馈
///   "点桌面没反应"。
///   </description></item>
/// </list>
/// <para>
/// 建窗形态由 <see cref="ConformanceRun.ConfigureWindow"/> 决定（<c>WithHidden().WithMaximized()</c>），
/// 本类只在 <c>Loaded</c> 之后驱动状态并断言。
/// </para>
/// <para>
/// <b>能力边界要说清</b>：<see cref="WebviewWindow.IsVisible"/> 在 Windows 上读的是宿主 HWND 的
/// <c>WS_VISIBLE</c>，它证明"宿主确实被藏了"，**不证明** WebView2 的渲染面也一起藏了
/// （那是另一个顶层窗口，只有 <c>ICoreWebView2Controller.IsVisible</c> 能答）。
/// 这条断言挡的是"<c>Hide()</c>/<c>Show()</c> 整体失效或调用链断了"，不是渲染面的残余。
/// </para>
/// <para>
/// 结论行：<c>WINDOWSTATE-SELFTEST: PASS</c> / <c>FAIL（…）</c>。
/// </para>
/// </remarks>
internal static class WindowStateSelfTest
{
    /// <summary>状态变更后等待其生效的上限（毫秒）。</summary>
    /// <remarks>
    /// Windows/Linux 的查询是同步的，通常第一轮就过；留出余量是给 macOS 的 zoom 动画
    /// 与窗口管理器的异步状态确认。
    /// </remarks>
    private const int SettleTimeoutMs = 2000;

    /// <summary>轮询间隔（毫秒）。</summary>
    private const int PollIntervalMs = 25;

    private static int _failures;

    /// <summary>本项是否失败——demo 的 Main 据此设置退出码。</summary>
    internal static bool Failed => Volatile.Read(ref _failures) > 0;

    /// <summary>在窗口 <c>Loaded</c>（UI 线程）时挂上本自检。</summary>
    internal static void Attach(OrielApp app, WebviewWindow window) => _ = RunAsync(app, window);

    private static async Task RunAsync(OrielApp app, WebviewWindow window)
    {
        try
        {
            // macOS 无图形会话时（停在登录窗口），NSWindow 的状态不由本进程决定；
            // 建窗配置照常生效但查询没有意义——整套跳过，别把环境问题报成库的问题。
            if (OperatingSystem.IsMacOS() && !MacDisplay.HasScreenSession)
            {
                Console.WriteLine(
                    "[windowstate-selftest] SKIP: 本进程没有图形会话（macOS 停在登录窗口），"
                    + "窗口状态不由本进程决定——无从验证");
                return;
            }

            Console.WriteLine(
                $"[windowstate-selftest] 起手配置 Hidden+Maximized：Loaded 时 " +
                $"IsVisible={window.IsVisible} IsMaximized={window.IsMaximized}");

            // ① 隐藏启动：Loaded 时窗口必须还没上屏（页面照常加载，这是 --hidden 的契约）
            Check(!window.IsVisible, "WithHidden() 起手：Loaded 时窗口不应可见");

            // ② 核心回归：Show() 之后必须**直接最大化**。
            //    修复前这里是 false（SW_SHOW 按"当前大小和位置"显示，WS_MAXIMIZE 从未建立）。
            window.Show();

            Check(
                await WaitAsync(() => window.IsVisible),
                "Show() 之后 IsVisible 应为 true");

            bool maximizedAfterShow = await WaitAsync(() => window.IsMaximized);
            if (MaximizeCheckable)
            {
                Check(
                    maximizedAfterShow,
                    "Hidden+Maximized：Show() 之后应直接最大化（实现把最大化意图丢在了创建期）");
            }
            else
            {
                Console.WriteLine(
                    "[windowstate-selftest] SKIP: 本环境没有窗口管理器，最大化由 WM 决定"
                    + "（gkt_window_maximize 不会被兑现）——最大化断言无从验证");
            }

            // ③ 可见性往返：Hide → 不可见 → Show → 可见。
            //    这条是 0.3.4 那个 P0 的守卫：IsVisible 就是为它加的属性。
            window.Hide();

            Check(
                await WaitAsync(() => !window.IsVisible),
                "Hide() 之后 IsVisible 应为 false");

            window.Show();

            Check(
                await WaitAsync(() => window.IsVisible),
                "再次 Show() 之后 IsVisible 应为 true");

            // ④ 最大化状态跨 Hide/Show 往返不得丢失（SW_HIDE 不动 WS_MAXIMIZE，
            //    这条守着"修复只处理了首次 Show"这类半吊子改法）。
            if (MaximizeCheckable)
            {
                Check(
                    await WaitAsync(() => window.IsMaximized),
                    "Hide→Show 往返之后仍应保持最大化");
            }
        }
        catch (Exception ex)
        {
            // 抛异常本身就是失败：静默跳过会让这个自检"永远绿"，比没有更糟
            Check(false, $"窗口状态自检异常：{ex.Message}");
        }
        finally
        {
            Report();

            // 退出必须在 UI 线程上（Quit 是线程相关的）。await 之后的续体已回到 UI 线程，
            // 仍用 PostToUiThread 兜一层：万一将来 SynchronizationContext 的语义变了，
            // 这里不会变成"自检跑完了但进程不退"。
            window.PostToUiThread(app.Quit);
        }
    }

    /// <summary>
    /// 本环境能否验证最大化。
    /// </summary>
    /// <remarks>
    /// Linux 的 <c>gtk_window_maximize</c> 只是向窗口管理器发请求：CI 的 xvfb 里没有 WM，
    /// 状态永远不会变成最大化。那验的是环境而不是库——与 <c>--selftest geometry</c>
    /// 用同一判据（<c>ORIEL_HAS_WM</c>，由验证脚本探测后设置）。
    /// </remarks>
    private static bool MaximizeCheckable => !OperatingSystem.IsLinux() || GeometrySelfTest.HasWindowManager;

    /// <summary>轮询直到条件成立或超时，返回最后一次结果。</summary>
    private static async Task<bool> WaitAsync(Func<bool> condition)
    {
        for (int waited = 0; ; waited += PollIntervalMs)
        {
            if (condition())
            {
                return true;
            }

            if (waited >= SettleTimeoutMs)
            {
                return false;
            }

            await Task.Delay(PollIntervalMs);
        }
    }

    private static void Check(bool condition, string description)
    {
        if (condition)
        {
            Console.WriteLine($"[windowstate-selftest] OK: {description}");
            return;
        }

        Interlocked.Increment(ref _failures);
        Console.WriteLine($"[windowstate-selftest] 失败: {description}");
    }

    private static void Report()
    {
        if (Failed)
        {
            Console.WriteLine($"WINDOWSTATE-SELFTEST: FAIL（{Volatile.Read(ref _failures)} 项）");
        }
        else
        {
            Console.WriteLine("WINDOWSTATE-SELFTEST: PASS");
        }

        Console.Out.Flush();
    }
}
