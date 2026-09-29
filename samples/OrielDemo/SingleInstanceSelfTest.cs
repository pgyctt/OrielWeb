using OrielWeb;

namespace OrielDemo;

/// <summary>
/// 单实例自检（**双进程协作**，由脚本驱动）。
/// </summary>
/// <remarks>
/// 由 <c>--single-instance-selftest</c> 启用。这一项没法在单进程里自证，所以契约定成：
///   * 首实例：启动后等 15 秒，收到第二实例的通知即打印 <c>SINGLE-INSTANCE: PASS</c>；
///   * 第二实例：检测到已有实例 → 打印 <c>SINGLE-INSTANCE-SECONDARY</c> → 立即退出（退出码 0）。
/// 脚本起两个进程，断言"第二个快速且成功地退出"且"第一个打印 PASS"。
/// </remarks>
internal static class SingleInstanceSelfTest
{
    internal const string InstanceId = "oriel-demo-single-instance-selftest";

    private static bool _received;

    /// <summary>首实例是否失败（15 秒内没等到通知）——demo 的 Main 据此设置退出码。</summary>
    internal static bool Failed { get; private set; }

    /// <summary>首实例启动后调用：起一个看门狗，避免"没有第二实例"时永远挂着。</summary>
    internal static void StartWatchdog()
    {
        _ = new Timer(
            _ =>
            {
                if (_received)
                {
                    return;
                }

                Failed = true;
                Console.WriteLine("SINGLE-INSTANCE: FAIL（15 秒内没有收到第二实例的通知）");
                Console.Out.Flush();
                Program.Window?.PostToUiThread(() => Program.Window?.Close());
            },
            null,
            TimeSpan.FromSeconds(15),
            Timeout.InfiniteTimeSpan);
    }

    /// <summary>首实例收到第二实例的激活请求（由 SingleInstance 的回调驱动）。</summary>
    internal static void OnActivatedBySecondInstance(WebviewWindow? window)
    {
        _received = true;
        Console.WriteLine("SINGLE-INSTANCE: PASS（首实例收到第二实例的激活请求）");
        Console.Out.Flush();
        window?.Close();
    }
}
