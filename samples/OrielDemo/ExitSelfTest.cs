using OrielWeb;

namespace OrielDemo;

/// <summary>
/// 退出语义自检（**子进程取证**，由脚本驱动）。
/// </summary>
/// <remarks>
/// <para>
/// 由 <c>--selftest exit</c> 与 <c>--selftest exit-timeout</c> 启用。为什么必须放在子进程里：
/// 看门狗超时后走的是 <see cref="Environment.Exit(int)"/>，它会**把测试运行器一起带走**——
/// 在 xunit 进程里断言这件事不可能，只能起一个真实进程、看它的退出码与打印。
/// </para>
/// <para>
/// 两个模式分别取证下游反馈 2026-10-14 的 P0-1 的两半：
/// <list type="bullet">
/// <item><c>exit</c>：正常退出。断言 <c>Exiting</c> 与 <c>ExitCompleted</c> 都触发，且进程以 0 结束
/// （证明"Quit 请求 + 清理"这条正常路径没有被看门狗误伤）。</item>
/// <item><c>exit-timeout</c>：清理卡死。在 <c>Run()</c> **返回之后**把 UI 线程堵死（这正是现场
/// 的形态：消息循环已停，宿主的同步清理卡在那里），断言看门狗在超时后强制结束进程、
/// 退出码是 2（不是 0），且 <c>ExitTimeout</c> 事件先于强杀触发。</item>
/// </list>
/// </para>
/// <para>
/// 结论行：<c>EXIT-SELFTEST: PASS</c> / <c>EXIT-TIMEOUT-SELFTEST: PASS（退出码 2）</c>。
/// </para>
/// </remarks>
internal static class ExitSelfTest
{
    private static bool _exitingSeen;
    private static bool _completedSeen;

    /// <summary>本次是否是超时模式（<c>--selftest exit-timeout</c>）。</summary>
    internal static bool TimeoutMode { get; set; }

    /// <summary>挂上退出观测。必须在 <c>Run()</c> 之前调用（事件要在退出发生前订阅好）。</summary>
    internal static void Attach(OrielApp app)
    {
        app.Exiting += args =>
        {
            _exitingSeen = true;
            Console.WriteLine($"[exit-selftest] Exiting：{args}");
        };
        app.ExitCompleted += args =>
        {
            _completedSeen = true;
            Console.WriteLine($"[exit-selftest] ExitCompleted：{args}");
        };
        app.ExitTimeout += args =>
        {
            // 这个回调在看门狗线程上：只能做与 UI 无关的事，且要快。
            //
            // **结论行必须在这里打印**：超时模式的进程紧接着就会被 Environment.Exit 强杀，
            // Main 里 Run() 返回之后的代码**永远执行不到**。所以这是唯一能留下结论的时机——
            // 也正是这个事件存在的意义（让宿主知道"这次是被强杀的"，而不是静默消失）。
            // 早先结论行写在 Run() 之后，结果是：退出码对了（2），但**日志里没有结论行**，
            // CI 按结论行判定就会永远红——这个缺陷是在把自检接进 CI 时才暴露出来的。
            Console.WriteLine($"[exit-timeout-selftest] EXIT-TIMEOUT：{args}");
            Console.WriteLine("EXIT-TIMEOUT-SELFTEST: PASS（看门狗在超时后强制结束进程）");
            Console.Out.Flush();
        };
    }

    /// <summary>窗口加载完成后发起退出（正常模式）或先把 UI 线程堵死再等看门狗（超时模式）。</summary>
    internal static void OnLoaded(WebviewWindow window)
    {
        if (TimeoutMode)
        {
            Console.WriteLine("[exit-timeout-selftest] 已加载，发起 Quit；Run() 返回后将堵死 UI 线程");
            Console.Out.Flush();

            // 关键：这里**故意不做**清理超时前该做的事，让"宿主的收尾卡住"真实发生
            window.App.Quit();
            return;
        }

        Console.WriteLine("[exit-selftest] 已加载，发起 Quit");
        Console.Out.Flush();
        window.App.Quit();
    }

    /// <summary>
    /// <c>Run()</c> 返回之后的收尾——**超时模式在这里堵死**，复现"清理卡住 → 进程驻留"。
    /// </summary>
    /// <returns>正常模式返回 true（可继续判定结论）；超时模式永不返回（等看门狗强杀）。</returns>
    /// <remarks>
    /// 超时模式的结论行**不在这里**打印，而在 <c>ExitTimeout</c> 回调里——因为强杀之后
    /// 这里的代码永远执行不到（见该回调里的说明）。
    /// </remarks>
    internal static bool FinishAfterRun()
    {
        if (TimeoutMode)
        {
            // 现场形态：宿主的同步清理卡在一个永不完成的等待上（这里用无限睡眠代表）。
            // 消息循环此时已停，UI 线程被占住 → 任何需要 UI 线程的清理都不可能完成。
            // 结论行已由 ExitTimeout 回调打印（强杀前最后的机会）。
            Console.WriteLine("[exit-timeout-selftest] Run() 已返回，现在堵死 UI 线程（等看门狗强杀）");
            Console.Out.Flush();
            Thread.Sleep(Timeout.Infinite);
            return false; // 不可达；真被强杀时进程在这里结束
        }

        if (_exitingSeen && _completedSeen)
        {
            Console.WriteLine("EXIT-SELFTEST: PASS");
            Console.Out.Flush();
            return true;
        }

        Console.WriteLine(
            $"EXIT-SELFTEST: FAIL（Exiting={_exitingSeen}，ExitCompleted={_completedSeen}）");
        Console.Out.Flush();
        return false;
    }
}
