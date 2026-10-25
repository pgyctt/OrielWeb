namespace OrielWeb.Platform;

/// <summary>
/// 基于"向主线程队列投递闭包"的 <see cref="SynchronizationContext"/>，三平台共用：
/// GTK 主循环（g_idle_add）、NSApplication 主循环（performSelectorOnMainThread）、
/// Win32 消息循环（PostMessage）各自提供 <paramref name="postToMainThread"/> 闭包。
/// </summary>
/// <remarks>
/// <para>
/// 原生主循环都没有 <see cref="SynchronizationContext"/>，不安装的话 <c>await</c> 的续体会
/// 落在线程池线程——而平台对象几乎全部只能在主线程碰（WebView2/GTK/ObjC 调用都是），并且
/// 命令模型对使用者的承诺是"async 命令 await 之后回到 UI 线程"（三平台一致，见 API.md 的
/// 线程模型一节）。安装后，<c>await</c> 的续体经 <see cref="Post"/> 回主线程队列，由各平台的
/// PostToMainThread 派发执行。
/// </para>
/// <para>
/// <b>Send 的两级语义</b>：已在主线程时**同步执行**（这是 Send 契约里无争议的那一半——
/// 同线程无需编组，直接跑，零死锁面）；不在主线程时退化为 Post（异步）并留一条一次性诊断。
/// 真正的跨线程同步 Send 需要"投递 + 阻塞等待"编组原语，还要处理 UI 线程互等的经典死锁面；
/// 库内没有调用方依赖它，依赖它的第三方基础设施（同步封送、某些 COM 回调路径）至少能从
/// 诊断里看到时序为什么错位，而不是静默地错。2026-10-25 评审前连"同线程同步执行"都没有。
/// </para>
/// </remarks>
internal sealed class MainThreadSynchronizationContext(Action<Action> postToMainThread) : SynchronizationContext
{
    private readonly Action<Action> _postToMainThread = postToMainThread;

    /// <summary>安装本上下文的线程 = 主线程（Install 只在主线程调用，见其注释）。</summary>
    private readonly Thread _mainThread = Thread.CurrentThread;

    /// <summary>跨线程 Send 被异步化这件事只提醒一次：基础设施常会连发，刷屏之后没人看。</summary>
    private static int _warnedAsyncSend;

    public override void Post(SendOrPostCallback callback, object? state)
        => _postToMainThread(() => callback(state));

    public override void Send(SendOrPostCallback callback, object? state)
    {
        if (ReferenceEquals(Thread.CurrentThread, _mainThread))
        {
            callback(state);
            return;
        }

        if (Interlocked.Exchange(ref _warnedAsyncSend, 1) == 0)
        {
            OrielDiagnostics.Report(
                "[OrielWeb] SynchronizationContext.Send 在非 UI 线程被调用，已退化为异步投递（Post 语义）：" +
                "依赖 Send 同步完成的调用方会观察到时序错位。跨线程同步编组有 UI 线程互等的死锁面，" +
                "本库选择不提供；请改用 OrielApp.InvokeOnUiThread。");
        }

        _postToMainThread(() => callback(state));
    }

    /// <summary>在主线程上安装本上下文（必须在主线程调用；重复安装只是换一个等价实例）。</summary>
    internal static void Install(Action<Action> postToMainThread)
        => SetSynchronizationContext(new MainThreadSynchronizationContext(postToMainThread));
}
