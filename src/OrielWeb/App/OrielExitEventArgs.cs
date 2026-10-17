namespace OrielWeb;

/// <summary>退出阶段的进度信息（<see cref="OrielApp.Exiting"/> / <see cref="OrielApp.ExitCompleted"/> 的参数）。</summary>
public sealed class OrielExitEventArgs
{
    internal OrielExitEventArgs(string stage, TimeSpan elapsed)
    {
        Stage = stage;
        Elapsed = elapsed;
    }

    /// <summary>当前所处的退出阶段（库内固定文案，可直接写进日志）。</summary>
    public string Stage { get; }

    /// <summary>从"请求退出"到本阶段的时间。</summary>
    public TimeSpan Elapsed { get; }

    /// <inheritdoc />
    public override string ToString() => $"{Stage}（{Elapsed.TotalMilliseconds:F0} ms）";
}

/// <summary>
/// 退出清理超时（<see cref="OrielApp.ExitTimeout"/> 的参数）：进程即将被看门狗强制结束。
/// </summary>
/// <remarks>
/// <para>
/// 收到它就说明"正常退出这条路走不通了"——清理里有东西卡住了（等待一个永不完成的 join、
/// 一个失去响应窗口的同步调用等）。库紧接着会强制结束进程，所以这个回调是宿主**最后一次**
/// 留痕与抢救的机会（例如把关键状态刷进磁盘、关掉设备连接）。
/// </para>
/// <para>
/// 回调在**看门狗线程**上触发，不在 UI 线程上（UI 线程此时正是被卡住的那一个）。因此这里
/// 只能做与 UI 无关的事，且必须尽快返回——它跑太久只会推迟强制结束。
/// </para>
/// </remarks>
public sealed class OrielExitTimeoutEventArgs
{
    internal OrielExitTimeoutEventArgs(TimeSpan timeout, string? pendingStage)
    {
        Timeout = timeout;
        PendingStage = pendingStage;
    }

    /// <summary>本次使用的退出超时。</summary>
    public TimeSpan Timeout { get; }

    /// <summary>
    /// 退出时正在进行的阶段（库能观测到的最后一个阶段）；观测不到时为 <c>null</c>。
    /// 用来定位"卡在哪一步"。
    /// </summary>
    public string? PendingStage { get; }

    /// <inheritdoc />
    public override string ToString()
        => PendingStage is null
            ? $"退出清理超过 {Timeout.TotalSeconds:F0} 秒，即将强制结束进程"
            : $"退出清理卡在「{PendingStage}」，超过 {Timeout.TotalSeconds:F0} 秒，即将强制结束进程";
}
