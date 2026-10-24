using OrielWeb;

namespace OrielConformance;

/// <summary>
/// 主题自检：宿主能否读到系统主题，以及主题能否经 <c>theme.changed</c> 事件送达页面。
/// </summary>
/// <remarks>
/// <para>
/// 由 <c>--selftest theme</c> 启用。
/// </para>
/// <para>
/// **"造两种值"不靠自检自己造**：同一进程内没法把系统主题改来改去。Linux 上用 <c>GTK_THEME</c>
/// 环境变量（GTK 认可的入口）分别跑浅色与深色（<c>Adwaita:dark</c>）两次，再由 CI 比对两次的结论
/// **不同**——这才算真的走了深/浅两条路径（只跑一次只能证明"读得到"，证明不了"判得对"）。
/// </para>
/// <para>
/// <b>怎么证明"事件到了页面"</b>：读页面自己落到 <c>&lt;html data-theme&gt;</c> 上的值。
/// 早先是让页面 <c>postMessage</c> 回显一条，迁移到前端包时改掉了——那个回显是**测试钩子**，
/// 夹在示例页里既不属于"怎么用本库"的示范，验的也是旁路而不是可见效果。
/// 现在验的是"页面真的应用了主题"，那正是用户能看到的东西。
/// </para>
/// <para>
/// 结论行：<c>THEME-SELFTEST: PASS（light|dark）</c> / <c>FAIL（…）</c>。
/// </para>
/// </remarks>
internal static class ThemeSelfTest
{
    private static bool _finished;
    private static bool _failed;

    /// <summary>自检是否失败——demo 的 Main 据此设置退出码。</summary>
    internal static bool Failed => _failed;

    /// <summary>
    /// 挂载自检。**调用时机由调用方保证**：必须在窗口门面可用之后（例如窗口的 <c>Loaded</c> 回调里）——
    /// onCreated 阶段后端尚未 attach，那时连 <c>window.App</c> 都取不到（实测会 fail-fast）。
    /// </summary>
    internal static void Attach(OrielApp app, WebviewWindow window)
    {
        string expected = app.Theme == OrielTheme.Dark ? "dark" : "light";

        Console.WriteLine($"[theme-selftest] 宿主读到的主题：{expected}（{app.Theme}）");

        _ = ProbeAsync(window, expected);
    }

    /// <summary>
    /// 轮询页面上的 <c>&lt;html data-theme&gt;</c>，直到它等于期望值。
    /// </summary>
    /// <remarks>
    /// 轮询而不是固定延时：宿主在**导航完成后**才补推主题（见 <c>OrielApp.AttachWindow</c>），
    /// 而导航完成与页面脚本执行之间没有可依赖的先后关系。固定延时要么不够（假红）
    /// 要么白等（CI 变慢）。上限 20 秒，超时给 FAIL——不能把 CI 挂住。
    /// </remarks>
    private static async Task ProbeAsync(WebviewWindow window, string expected)
    {
        const int TimeoutMs = 20000;
        const int StepMs = 100;

        for (int waited = 0; waited < TimeoutMs; waited += StepMs)
        {
            if (_finished)
            {
                return;
            }

            string? actual = null;
            try
            {
                // EvaluateJs 返回的是 JSON 编码的结果（字符串带引号），也可能是字面量 null
                string raw = await window.EvaluateJs("document.documentElement.dataset.theme || null");
                actual = raw.Trim('"');
                if (actual is "null" or "undefined" or "")
                {
                    actual = null;
                }
            }
            catch (Exception ex)
            {
                // 页面正在导航/销毁时求值会失败，重试即可——真失败会被超时抓到
                Console.WriteLine($"[theme-selftest] 求值失败（将重试）：{ex.Message}");
            }

            if (actual == expected)
            {
                Finish(window, ok: true, detail: $"页面已应用 data-theme={actual}");
                return;
            }

            await Task.Delay(StepMs);
        }

        if (!_finished)
        {
            // 失败时把**页面侧的可观测状态**打出来：这条失败过很多次，而"20 秒内没设置"
            // 这一个现象指向好几种完全不同的根因——桥接没注入（window.oriel 缺失）、
            // 页面脚本没执行（readyState 停在 loading）、或事件到了但监听没注册。
            // 不打出这些，下一轮还是只能猜。
            string diagnosis = await DiagnoseAsync(window);
            Finish(window, ok: false,
                detail: $"{TimeoutMs / 1000} 秒内页面没有把 data-theme 设为 {expected}{diagnosis}");
        }
    }

    /// <summary>读页面侧的关键状态，拼成一段可读的诊断（失败时才调用）。</summary>
    private static async Task<string> DiagnoseAsync(WebviewWindow window)
    {
        try
        {
            string raw = await window.EvaluateJs(
                "JSON.stringify({"
                + "oriel: typeof window.oriel,"
                + "onEvent: typeof (window.oriel && window.oriel._onEvent),"
                + "readyState: document.readyState,"
                + "theme: document.documentElement.getAttribute('data-theme'),"
                + "badge: (document.getElementById('ipc-badge') || {}).textContent || null"
                + "})");

            // EvaluateJs 返回的是 JSON 编码的字符串（外面还套一层引号），去掉它便于阅读
            return $"；页面状态：{raw.Trim('"').Replace("\\\"", "\"")}";
        }
        catch (Exception ex)
        {
            return $"；读取页面状态也失败：{ex.Message}";
        }
    }

    /// <summary>收尾：打印结论行并关掉窗口（关窗 → 所有窗口关完 → 消息循环退出）。</summary>
    private static void Finish(WebviewWindow window, bool ok, string detail)
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        _failed = !ok;

        Console.WriteLine($"[theme-selftest] {detail}");

        string expected = window.App.Theme == OrielTheme.Dark ? "dark" : "light";
        Console.WriteLine(ok
            ? $"THEME-SELFTEST: PASS（{expected}）"
            : $"THEME-SELFTEST: FAIL（{detail}）");
        Console.Out.Flush();

        // 用 Quit 而不是 Close 收尾：自检的退出**不能依赖窗口的关闭流程**——
        // Linux 上 `gtk_window_close` 要走 delete-event，而没有窗口管理器的环境
        // （CI 的 xvfb）里那条路不一定成立，进程会挂到脚本的 timeout 才被杀，
        // 表现为"结论已 PASS 但退出码 124"。Quit 直接停消息循环，三平台都可靠。
        window.PostToUiThread(window.App.Quit);
    }
}
