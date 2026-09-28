namespace OrielWeb.Platform.Windows;

/// <summary>
/// 基于 Win32 消息循环的 <see cref="SynchronizationContext"/>。
/// </summary>
/// <remarks>
/// Win32 消息循环本身没有 <see cref="SynchronizationContext"/>，因此在
/// <c>await</c> 之后（例如 WebView2Aot 的环境/控制器异步创建）续体会落到线程池线程，
/// 而 WebView2 的全部调用都必须在创建它的 UI 线程上进行。
/// 安装本上下文后，<c>await</c> 的续体会被 Post 回 UI 线程的队列，
/// 由 <see cref="WindowsPlatformBackend.PostToMainThread"/> 派发执行。
/// </remarks>
internal sealed class Win32SynchronizationContext(Action<Action> postToMainThread) : SynchronizationContext
{
    private readonly Action<Action> _postToMainThread = postToMainThread;

    public override void Post(SendOrPostCallback callback, object? state)
        => _postToMainThread(() => callback(state));

    public override void Send(SendOrPostCallback callback, object? state)
        => _postToMainThread(() => callback(state));

    /// <summary>在 UI 线程上安装本上下文（幂等：重复调用无副作用）。</summary>
    internal static void Install(Action<Action> postToMainThread)
        => SetSynchronizationContext(new Win32SynchronizationContext(postToMainThread));
}
