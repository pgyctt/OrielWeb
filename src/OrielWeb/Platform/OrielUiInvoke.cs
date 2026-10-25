namespace OrielWeb.Platform;

/// <summary>
/// <c>InvokeOnUiThread</c> 的共享实现——<see cref="OrielWeb.OrielApp"/>（平台后端通道）与
/// <see cref="OrielWeb.WebviewWindow"/>（窗口后端通道）各有一个公开包装，逻辑完全同构。
/// </summary>
/// <remarks>
/// 抽出来是因为两份各约 50 行的实现曾逐字重复：改一处忘另一处不会报错，只会让
/// "同一个库的两条编组路径行为悄悄不同"（2026-10-25 评审 P3）。差异只剩文案与通道，
/// 全部参数化：是否已在 UI 线程、投递函数、退出建议文案。
/// </remarks>
internal static class OrielUiInvoke
{
    /// <summary>
    /// 在 UI 线程上同步执行：已在 UI 线程直接跑，否则投递并阻塞等待完成。
    /// </summary>
    /// <param name="action">要执行的动作。</param>
    /// <param name="timeout">等待上限；null = 无限等待。超时抛 <see cref="TimeoutException"/>。</param>
    /// <param name="isOnUiThread">调用线程是否已是 UI 线程（由调用方问自己的后端）。</param>
    /// <param name="isExiting">应用是否正在退出（退出中投递不会被处理，立刻失败而不是永久阻塞）。</param>
    /// <param name="post">投递函数（平台后端的 PostToMainThread 或窗口后端的 PostToUiThread）；返回 false 表示通道已失效。</param>
    /// <param name="exitingAdvice">退出异常文案里"该怎么办"的那半句（两个入口指不同的尽力投递 API）。</param>
    /// <param name="postFailedMessage">投递失败（通道失效）的异常文案。</param>
    public static void Run(
        Action action,
        TimeSpan? timeout,
        bool isOnUiThread,
        bool isExiting,
        Func<Action, bool> post,
        string exitingAdvice,
        string postFailedMessage)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (isOnUiThread)
        {
            action();
            return;
        }

        if (isExiting)
        {
            throw new InvalidOperationException(
                "应用正在退出：消息循环已停止，InvokeOnUiThread 投递的动作不会被处理。" + exitingAdvice);
        }

        using var done = new ManualResetEventSlim(false);
        Exception? failure = null;
        if (!post(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                done.Set();
            }
        }))
        {
            throw new InvalidOperationException(postFailedMessage);
        }

        if (!done.Wait(timeout ?? Timeout.InfiniteTimeSpan))
        {
            throw new TimeoutException(
                $"InvokeOnUiThread 在 {timeout} 内没有完成：UI 线程可能被长任务占用" +
                "（一条慢命令、一个不返回的模态原生调用）。");
        }

        if (failure is not null)
        {
            // 原样抛出：编组不该改写调用方异常的类型与栈（用 ExceptionDispatchInfo 保住栈）
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
