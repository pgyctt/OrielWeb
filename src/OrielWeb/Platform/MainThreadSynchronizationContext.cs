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
/// <b>Send 实现为 Post（异步），不可依赖同步完成。</b>真同步需要"跨线程投递 + 阻塞等待"的
/// 编组原语，还要处理 UI 线程互等的经典死锁面；库内没有任何调用方依赖 Send 的同步语义，
/// 把它做成同步等待只会引入死锁风险而不换来收益。调用方（含 WebView2 互操作）请只依赖
/// Post 语义。
/// </para>
/// </remarks>
internal sealed class MainThreadSynchronizationContext(Action<Action> postToMainThread) : SynchronizationContext
{
    private readonly Action<Action> _postToMainThread = postToMainThread;

    public override void Post(SendOrPostCallback callback, object? state)
        => _postToMainThread(() => callback(state));

    public override void Send(SendOrPostCallback callback, object? state)
        => _postToMainThread(() => callback(state));

    /// <summary>在主线程上安装本上下文（必须在主线程调用；重复安装只是换一个等价实例）。</summary>
    internal static void Install(Action<Action> postToMainThread)
        => SetSynchronizationContext(new MainThreadSynchronizationContext(postToMainThread));
}
