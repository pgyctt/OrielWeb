using OrielWeb;
using OrielWeb.Ipc;

namespace OrielConformance;

/// <summary>
/// 取消自检用的两条命令：一条会被取消的长命令、一条不受影响的短命令。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么要两条</b>：取消最容易出的错不是"取消不了"，而是<b>取消错了对象</b>——
/// 分发器按（窗口, id）匹配在飞调用，键一旦写错，A 的取消会砍到 B 的命令上。
/// 只有同时跑一条会被取消的和一条不该被取消的，才能把这两种情况分开。
/// </para>
/// <para>
/// 长命令刻意用 <c>Task.Delay(5 秒, token)</c>：它<b>只在 token 触发时才提前返回</b>。
/// 所以自检在 1.5 秒时观察到 <see cref="SlowCancelled"/> 为真，就已经证明
/// "5 秒的等待被真的打断了"，而不只是"页面那边超时了"。
/// </para>
/// </remarks>
internal sealed class CancelProbeCommands
{
    /// <summary>会被取消的长命令。</summary>
    internal const string SlowCommand = "conf.slow";

    /// <summary>不该被取消的短命令。</summary>
    internal const string FastCommand = "conf.fast";

    private static volatile bool s_slowStarted;
    private static volatile bool s_slowCancelled;

    /// <summary>长命令是否真的开始执行过（防止"命令压根没跑"被当成"取消成功"）。</summary>
    internal static bool SlowStarted => s_slowStarted;

    /// <summary>长命令的取消令牌是否真的被触发过。</summary>
    internal static bool SlowCancelled => s_slowCancelled;

    /// <summary>复位（自检开始时调一次，避免上一次运行的残留影响判定）。</summary>
    internal static void Reset()
    {
        s_slowStarted = false;
        s_slowCancelled = false;
    }

    /// <summary>
    /// 等 5 秒，可被取消。
    /// </summary>
    /// <param name="cancellationToken">宿主注入的取消令牌（页面超时 / 窗口关闭时触发）。</param>
    /// <returns>没被取消时返回 <c>"completed"</c>。</returns>
    [OrielCommand(SlowCommand)]
    public static async Task<string> Slow(CancellationToken cancellationToken)
    {
        s_slowStarted = true;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            return "completed";
        }
        catch (OperationCanceledException)
        {
            s_slowCancelled = true;
            // 重新抛出：让分发器走"取消"那条分支（回执带 cancelled 码、不计入 Failed）。
            // 吞掉的话，这次调用会以"成功返回 null"结束，自检就验不出取消路径了。
            throw;
        }
    }

    /// <summary>立即返回，用来证明取消没有波及无关命令。</summary>
    [OrielCommand(FastCommand)]
    public static string Fast() => "fast";
}
