using OrielWeb;

namespace OrielDemo;

/// <summary>
/// 主题自检：宿主能否读到系统主题，以及主题能否经 <c>theme.changed</c> 事件送达页面（页面回显确认）。
/// </summary>
/// <remarks>
/// 由 <c>--theme-selftest</c> 启用。
///
/// **"造两种值"不靠自检自己造**：同一进程内没法把系统主题改来改去。Linux 上用 <c>GTK_THEME</c>
/// 环境变量（GTK 认可的入口）分别跑浅色与深色（<c>Adwaita:dark</c>）两次，再由 CI 比对两次的结论
/// **不同**——这才算真的走了深/浅两条路径（只跑一次只能证明"读得到"，证明不了"判得对"）。
///
/// 页面侧的回显监听写在 <c>wwwroot/app.js</c> 里（顺带演示这个事件的用法）。
/// 结论行：<c>THEME-SELFTEST: PASS（light|dark）</c> / <c>FAIL（…）</c>。
/// </remarks>
internal static class ThemeSelfTest
{
    private static bool _finished;

    /// <summary>自检是否失败——demo 的 Main 据此设置进程退出码。</summary>
    internal static bool Failed { get; private set; }

    /// <summary>
    /// 挂载自检。**调用时机由调用方保证**：必须在窗口门面可用之后（例如窗口的 Loaded 回调里）——
    /// onCreated 阶段后端尚未 attach，那时连 <c>window.App</c> 都取不到（实测会 fail-fast）。
    /// </summary>
    internal static void Attach(OrielApp app, WebviewWindow window)
    {
        string expected = app.Theme == OrielTheme.Dark ? "dark" : "light";
        string expectedJson = "\"" + expected + "\"";

        Console.WriteLine($"[theme-selftest] 宿主读到的主题：{expected}（{app.Theme}）");

        window.MessageReceived += args =>
        {
            if (args.Name != "theme-echo" || _finished)
            {
                return;
            }

            _finished = true;
            Console.WriteLine($"[theme-selftest] 页面回显：{args.Json}");
            Failed = args.Json != expectedJson;
            Console.WriteLine(Failed
                ? $"THEME-SELFTEST: FAIL（回显 {args.Json} ≠ 期望 {expectedJson}）"
                : $"THEME-SELFTEST: PASS（{expected}）");
            Console.Out.Flush();
            window.Close();
        };

        // 兜底：页面没回显（例如事件通道断了）时也必须给出结论，不能把 CI 挂住。
        // 计时器在线程池触发，所以经 PostToUiThread 回 UI 线程再碰窗口。
        _ = new Timer(
            _ => window.PostToUiThread(() =>
            {
                if (_finished)
                {
                    return;
                }

                _finished = true;
                Failed = true;
                Console.WriteLine("THEME-SELFTEST: FAIL（20 秒内没有收到页面的 theme-echo 回显）");
                Console.Out.Flush();
                window.Close();
            }),
            null,
            TimeSpan.FromSeconds(20),
            Timeout.InfiniteTimeSpan);
    }
}
