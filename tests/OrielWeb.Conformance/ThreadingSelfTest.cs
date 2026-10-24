using OrielWeb;

namespace OrielConformance;

/// <summary>
/// 线程归属与编组自检（<c>--selftest threading</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 取证下游反馈 2026-10-14 的 P2-6：库此前只有 internal 的 <c>IsOnUiThread</c>，
/// 宿主想判断"要不要编组"就得自己维护一份主线程 ID。现在公开了
/// <see cref="OrielApp.IsOnUiThread"/> / <see cref="OrielApp.InvokeOnUiThread"/>，
/// 这个自检在**真实消息循环**里把它们的语义钉住。
/// </para>
/// <para>
/// 结论行：<c>THREADING-SELFTEST: PASS</c> / <c>FAIL（…）</c>。
/// </para>
/// </remarks>
internal static class ThreadingSelfTest
{
    private static int _failures;

    /// <summary>本项是否失败——demo 的 Main 据此设置退出码。</summary>
    internal static bool Failed => Volatile.Read(ref _failures) > 0;

    /// <summary>在窗口 <c>Loaded</c>（UI 线程）时挂上本自检。</summary>
    internal static void Attach(OrielApp app, WebviewWindow window)
    {
        // Loaded 回调本身就在 UI 线程上：先用它确认"真值"这一半
        Check(app.IsOnUiThread, "UI 线程上 app.IsOnUiThread 应为 true");
        Check(window.IsOnUiThread, "UI 线程上 window.IsOnUiThread 应为 true");

        _ = Task.Run(() =>
        {
            Check(!app.IsOnUiThread, "后台线程上 app.IsOnUiThread 应为 false");
            Check(!window.IsOnUiThread, "后台线程上 window.IsOnUiThread 应为 false");

            // 同步编组：动作必须真的**在 UI 线程上**执行（这是 InvokeOnUiThread 的全部意义）
            bool appRanOnUi = false;
            app.InvokeOnUiThread(() => appRanOnUi = app.IsOnUiThread);
            Check(appRanOnUi, "app.InvokeOnUiThread 应把动作执行在 UI 线程上");

            bool windowRanOnUi = false;
            window.InvokeOnUiThread(() => windowRanOnUi = window.IsOnUiThread);
            Check(windowRanOnUi, "window.InvokeOnUiThread 应把动作执行在 UI 线程上");

            // 异步投递：正常路径必须返回 true，且回调落在 UI 线程上
            bool posted = window.PostToUiThread(() =>
            {
                Check(app.IsOnUiThread, "PostToUiThread 的回调应在 UI 线程上执行");
                Report();
                app.Quit();
            });
            Check(posted, "PostToUiThread 在正常路径应返回 true");
        });
    }

    private static void Check(bool condition, string description)
    {
        if (condition)
        {
            Console.WriteLine($"[threading-selftest] OK: {description}");
            return;
        }

        Interlocked.Increment(ref _failures);
        Console.WriteLine($"[threading-selftest] 失败: {description}");
    }

    private static void Report()
    {
        if (Failed)
        {
            Console.WriteLine($"THREADING-SELFTEST: FAIL（{Volatile.Read(ref _failures)} 项）");
        }
        else
        {
            Console.WriteLine("THREADING-SELFTEST: PASS");
        }

        Console.Out.Flush();
    }
}
