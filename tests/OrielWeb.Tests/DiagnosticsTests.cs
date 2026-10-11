using Xunit;

namespace OrielWeb.Tests;

/// <summary>
/// 库内诊断通道（<see cref="OrielDiagnostics"/>）。
/// </summary>
/// <remarks>
/// 它存在的理由是可实测的：这些留痕此前是 <c>Debug.WriteLine</c>，在 Release 程序集里被整个编译掉
/// （实测过 Release DLL 里 <c>[OrielWeb]</c> 命中 0），生产环境因此没有宿主侧入口。
/// 这里钉住"订阅者收得到"与"一个订阅者抛异常不拖累别的订阅者"两条。
/// </remarks>
public sealed class DiagnosticsTests
{
    [Fact]
    public void Report_ReachesAllSubscribers_EvenIfOneThrows()
    {
        var seen = new List<string>();
        Action<OrielDiagnosticEventArgs> throwing = _ => throw new InvalidOperationException("订阅者自己炸了");
        Action<OrielDiagnosticEventArgs> collecting = e => seen.Add(e.Message);

        // 抛异常的那个排在前面：多播委托默认会在它那里中断，逐个调用 + 各自兜异常才不会
        OrielDiagnostics.Reported += throwing;
        OrielDiagnostics.Reported += collecting;
        try
        {
            OrielDiagnostics.Report("unit-test-diagnostic");
        }
        finally
        {
            OrielDiagnostics.Reported -= throwing;
            OrielDiagnostics.Reported -= collecting;
        }

        Assert.Contains("unit-test-diagnostic", seen);
    }

    [Fact]
    public void Report_WithoutSubscribers_IsANoOp()
    {
        // 没有订阅者时不该抛（调用点包括原生 trampoline，抛出去就是进程级后果）
        OrielDiagnostics.Report("无订阅者时也不该抛");
    }
}
