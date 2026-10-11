namespace OrielWeb;

/// <summary>
/// 库内诊断输出（进程级）：库自己的告警与失败留痕都走这里。
/// </summary>
/// <remarks>
/// <para>
/// IPC 被拒、shell 路径被拒、单实例锁不可用、托盘/通知失败、原生 trampoline 里的兜底异常……
/// 此前这些是 <c>Debug.WriteLine</c>，而它带 <c>[Conditional("DEBUG")]</c>——条件在**调用方程序集
/// 的编译期**求值，本库又是以 Release 发布的，于是这些调用在发布包里**根本不存在**
/// （实测：Release 程序集里 <c>[OrielWeb]</c> 串命中 0，Debug 版命中 26）。生产环境排障因此
/// 完全没有宿主侧入口——下游反馈（2026-10-11）P0-3 就是被这一点逼着在基类里给每条命令补 try/catch。
/// </para>
/// <para>
/// 做成**静态**通道而不是挂在 <see cref="OrielApp"/> 上：绝大多数调用点在静态的平台 trampoline
/// 与静态辅助类里，拿不到 app 实例；诊断本身也是进程级现象（与命令注册表同样是进程级全局，
/// 理由见其类说明）。
/// </para>
/// <para>
/// 无订阅者时**不构造事件参数、不分配**（<c>Reported?.Invoke(...)</c> 的参数在接收者为 null 时不求值）。
/// 注意调用点的消息串（多为插值字符串）仍会照常构造——本通道的 74 个调用点全部落在错误/罕见路径上
/// （拒绝、失败、兜底），这点开销与"生产环境零日志"相比是划算的；不要把热路径日志挂进来。
/// </para>
/// <para>
/// 有订阅者时**逐个调用并各自兜异常**：调用点包括原生 trampoline，一个订阅者写崩了不该把库拖下水
/// （异常逃出 trampoline 是进程级后果）。
/// </para>
/// <para>
/// 订阅者请在应用退出前自行退订：静态事件会活到进程结束。
/// </para>
/// </remarks>
public static class OrielDiagnostics
{
    /// <summary>库报告一条诊断时触发。</summary>
    public static event Action<OrielDiagnosticEventArgs>? Reported;

    /// <summary>库内报告入口；消息自带 <c>[OrielWeb]</c> 前缀（与库的历史日志格式一致）。</summary>
    internal static void Report(string message)
    {
        Action<OrielDiagnosticEventArgs>? handler = Reported;
        if (handler is null)
        {
            return;
        }

        var args = new OrielDiagnosticEventArgs(message);
        foreach (Action<OrielDiagnosticEventArgs> subscriber in handler.GetInvocationList())
        {
            try
            {
                subscriber(args);
            }
            catch (Exception ex)
            {
                // 诊断通道自身的故障不能再经诊断通道上报（会递归），只能尽力留个痕
                System.Diagnostics.Debug.WriteLine($"[OrielWeb] 诊断订阅者抛出异常：{ex}");
            }
        }
    }
}

/// <summary>一条库内诊断（<see cref="OrielDiagnostics.Reported"/> 的参数）。</summary>
public sealed class OrielDiagnosticEventArgs
{
    internal OrielDiagnosticEventArgs(string message) => Message = message;

    /// <summary>诊断文本，形如 <c>[OrielWeb] …</c>。</summary>
    public string Message { get; }
}
