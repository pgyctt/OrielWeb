using System.Text.Json;
using OrielWeb;

namespace OrielDemo;

/// <summary>
/// 菜单栏自检（<c>--selftest menubar</c>）：<c>Menu.setAsWindowMenu</c> / <c>setAsAppMenu</c>
/// 真的把菜单栏装上了，并且**没有把客户区尺寸契约弄坏**。
/// </summary>
/// <remarks>
/// <para>
/// 为什么这条自检的重点是几何而不是"菜单画出来没有"：B6b 是全计划唯一动到窗口结构的一批
/// （Linux 要把 <c>GtkWindow</c> 直装 webview 改成 <c>GtkBox{menubar, webview}</c>），
/// 计划里点名的风险就是"所有尺寸/几何换算都要重新验一遍"。菜单栏**占客户区高度**，
/// 而页面看到的 <c>innerSize</c> 只该是 webview 那块——这两件事一旦对不上，
/// 表现是"页面布局矮了一行"或"Resize(800,600) 实际拿到 600-N"，而且这类偏差
/// 在视觉上很容易被当成页面自己的问题。
/// </para>
/// <para>
/// 断言（结论行 <c>MENUBAR-SELFTEST: PASS</c> / <c>FAIL（…）</c>）：
/// <list type="number">
///   <item><b>装得上</b>：<c>setAsWindowMenu</c> 不报错（Windows/Linux）。</item>
///   <item><b>吃掉客户区高度</b>：装上之后 <c>InnerSize.Height</c> 变小，且小的那部分
///     与菜单栏高度同量级（Windows/Linux）。</item>
///   <item><b>拆得掉</b>：传空菜单之后 <c>InnerSize.Height</c> 回到原值附近。</item>
///   <item><b>应用菜单不报错</b>：<c>setAsAppMenu</c> 在三平台都成功
///     （macOS 上它才是唯一能设菜单的路）。</item>
///   <item><b>平台语义如实</b>：macOS 上 <c>setAsWindowMenu</c> 必须报
///     <c>not_supported</c>（菜单在 macOS 是应用级的，与 Tauri 一致），而不是别的错。</item>
/// </list>
/// </para>
/// <para>
/// <b>这里不验"点击回传"</b>：那要真实用户点一下菜单项，CI 上没有人。
/// 点击的路由逻辑（id 前缀 → action）由 <c>menu.test.mjs</c> 与
/// <c>BuiltInMenuCommandTests</c> 覆盖；"原生点击 → 事件"那一段只能交互验证，
/// 这条自检把能自动验的（装/拆/几何/平台语义）都验掉，剩下的如实留作人工项。
/// </para>
/// </remarks>
internal static class MenuBarSelfTest
{
    private const string Echo = "menubar-echo";

    /// <summary>内高变化的容差（逻辑像素）。菜单栏高度随主题/字号变，不能要求精确值。</summary>
    private const double HeightTolerance = 1.0;

    private static readonly List<string> Failures = [];
    private static readonly object Gate = new();

    private static WebviewWindow? _main;
    private static Timer? _watchdog;
    private static bool _finished;

    private static bool _windowSeen;
    private static bool _appSeen;
    private static bool _removeSeen;
    private static bool _platformSeen;

    /// <summary>装菜单栏之前的内高；装之后必须变小，拆掉之后必须回到附近。</summary>
    private static double _heightBefore;
    private static double _heightWithBar;
    private static double _heightAfterRemove;

    internal static bool Failed
    {
        get
        {
            lock (Gate)
            {
                return Failures.Count > 0;
            }
        }
    }

    /// <summary>在 <c>onCreated</c> 里调用：订阅页面回执（早于导航，避免漏掉最早那条）。</summary>
    internal static void Attach(WebviewWindow main)
    {
        _main = main;
        main.MessageReceived += OnMessage;
    }

    /// <summary>在 <c>Loaded</c> 里调用：这时后端已就位，菜单栏能落到真实原生对象上。</summary>
    internal static void Begin(OrielApp app, WebviewWindow main)
    {
        _main = main;
        _ = app;

        // 菜单栏**不阻塞**（与 popup 不同）：装/拆都是立即返回的，没有模态循环，
        // 所以这里不需要 ORIELWEB_MENU_AUTODISMISS_MS 那套自动关闭。
        _heightBefore = main.InnerSize.Height;

        _watchdog = new Timer(
            _ =>
            {
                lock (Gate)
                {
                    if (_finished)
                    {
                        return;
                    }

                    Failures.Add(
                        $"菜单栏自检超时（60 秒）：装窗口菜单栏={_windowSeen}，装应用菜单={_appSeen}，" +
                        $"拆菜单栏={_removeSeen}，平台语义={_platformSeen}");
                }

                main.PostToUiThread(Finish);
            },
            null,
            TimeSpan.FromSeconds(60),
            Timeout.InfiniteTimeSpan);

        // 走构建时内嵌到 wwwroot/vendor 的那份 src/menu.js，与 ApiSelfTest / MenuSelfTest 同一路径
        // （页面里没有 importmap，包名解析不了）。
        _ = main.EvaluateJs("""
            (async () => {
                const vendor = './vendor/orielweb-api';
                const post = (payload) => oriel.postMessage('menubar-echo', payload);
                const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

                // **每次改完布局要等一拍再发回执**：菜单栏插/摘只是改了 GTK 布局盒的
                // 子部件表，webview 的分配尺寸要等下一次布局走完才更新。宿主收到回执时
                // 立刻量 InnerSize，不等这一拍就会量到改动之前的值——表现是
                // "装了菜单栏但内高没变"或"拆了没还回来"，看起来像几何契约坏了。
                // 宿主侧另有 gtk_widget_queue_resize 显式排队重算，两道一起上。
                const settle = () => sleep(200);

                try {
                    const { Menu } = await import(vendor + '/menu.js');

                    // ① 装窗口菜单栏。传一个真实存在的 label（本窗口自己），
                    //    验的是"目标是自己"这条最常见的路径。
                    const bar = await Menu.new({
                        items: [
                            { id: 'open', text: 'Open' },
                            { item: 'Separator' },
                            { text: 'More', items: [{ id: 'inner', text: 'Inner' }] },
                        ],
                    });

                    let windowError = null;
                    try {
                        await bar.setAsWindowMenu({ label: 'main' });
                    } catch (e) {
                        windowError = String((e && e.message) || e);
                    }
                    await settle();
                    post({ step: 'window', error: windowError });

                    // ② 装应用菜单（三平台都该成功——macOS 上这还是唯一的路）。
                    let appError = null;
                    try {
                        const appMenu = await Menu.new({
                            items: [{ id: 'about', text: 'About' }],
                        });
                        await appMenu.setAsAppMenu();
                    } catch (e) {
                        appError = String((e && e.message) || e);
                    }
                    await settle();
                    post({ step: 'app', error: appError });

                    // ③ 拆掉窗口菜单栏（空菜单 = 移除，不是错误）。
                    let removeError = null;
                    try {
                        const empty = await Menu.new({ items: [] });
                        await empty.setAsWindowMenu({ label: 'main' });
                    } catch (e) {
                        removeError = String((e && e.message) || e);
                    }
                    await settle();
                    post({ step: 'remove', error: removeError });

                    // ④ 平台语义：macOS 上 setAsWindowMenu 必须是 not_supported。
                    //    单独再走一次，让 C# 能区分"①的失败是不是预期的那个"。
                    let platformError = null;
                    try {
                        const probe = await Menu.new({ items: [{ id: 'p', text: 'P' }] });
                        await probe.setAsWindowMenu({ label: 'main' });
                    } catch (e) {
                        platformError = String((e && e.message) || e);
                    }
                    await settle();
                    post({ step: 'platform', error: platformError });
                } catch (e) {
                    post({ step: 'fatal', error: String((e && e.message) || e) });
                }
            })();
            """);
    }

    private static void OnMessage(OrielMessageReceivedEventArgs args)
    {
        if (args.Name != Echo || args.Json is not { Length: > 0 } json)
        {
            return;
        }

        JsonElement root;
        try
        {
            root = JsonDocument.Parse(json).RootElement;
        }
        catch (JsonException)
        {
            return;
        }

        string step = root.TryGetProperty("step", out JsonElement stepElement)
            ? stepElement.GetString() ?? string.Empty
            : string.Empty;

        bool isMac = OperatingSystem.IsMacOS();

        switch (step)
        {
            case "window":
                _windowSeen = true;
                _heightWithBar = _main?.InnerSize.Height ?? 0;
                HandleWindow(root, isMac);
                break;

            case "app":
                _appSeen = true;
                HandleApp(root);
                break;

            case "remove":
                _removeSeen = true;
                _heightAfterRemove = _main?.InnerSize.Height ?? 0;
                HandleRemove(root);
                break;

            case "platform":
                _platformSeen = true;
                HandlePlatform(root, isMac);
                break;

            case "fatal":
                Check(false, $"菜单栏自检脚本抛错：{Error(root)}");
                _main?.PostToUiThread(Finish);
                break;
        }

        // 三步都到齐就收尾（第四步 platform 是复验，晚到不要紧，但也不必等它）。
        if (_windowSeen && _appSeen && _removeSeen && _platformSeen)
        {
            _main?.PostToUiThread(Finish);
        }
    }

    private static void HandleWindow(JsonElement root, bool isMac)
    {
        string? error = OptionalError(root);

        if (isMac)
        {
            // macOS：菜单是应用级的，这条本来就该失败。失败的形态由 platform 那一步验。
            Check(error is not null, "macOS 上 setAsWindowMenu 竟然成功了——菜单在那边是应用级的，与 Tauri 语义不符");
            return;
        }

        Check(error is null, $"Windows/Linux 上 setAsWindowMenu 失败：{error}");

        // 几何：装上菜单栏之后客户区必须变小——变小的量就是菜单栏高度。
        // 只要求"确实变小了"而不要求精确值：菜单栏高度随主题/字号/缩放变。
        double dropped = _heightBefore - _heightWithBar;

        // Wayland 例外：窗口尺寸由合成器决定，装上菜单栏后窗口不会跟着缩，
        // 客户区自然不变——这条验的是"能不能改窗口尺寸"，而那件事在 Wayland 上做不到
        // （详见 docs/CROSS-PLATFORM-VERIFICATION.md）。跳过而不是失败。
        if (dropped <= HeightTolerance && LinuxDisplay.IsWayland)
        {
            Console.WriteLine(
                $"[menubar-selftest] SKIP: 装上菜单栏后内高没有变小（装前 {_heightBefore:0.#}，装后 {_heightWithBar:0.#}）" +
                "——本会话是 Wayland，窗口尺寸由合成器决定，菜单栏占用无法观测");
            return;
        }

        Check(
            dropped > HeightTolerance,
            $"装上菜单栏后内高没有变小（装前 {_heightBefore:0.#}，装后 {_heightWithBar:0.#}）——" +
            "菜单栏要么没装上，要么被算进了客户区（页面会少一行）");
    }

    private static void HandleApp(JsonElement root)
    {
        string? error = OptionalError(root);
        Check(error is null, $"setAsAppMenu 失败：{error}");
    }

    private static void HandleRemove(JsonElement root)
    {
        string? error = OptionalError(root);

        if (OperatingSystem.IsMacOS())
        {
            // macOS 上 ① 就没装上，"拆"这一步同样会以 not_supported 告终。
            Check(error is not null, "macOS 上 setAsWindowMenu(空) 竟然成功了");
            return;
        }

        Check(error is null, $"拆菜单栏失败：{error}");

        // 拆掉之后内高要回到装之前的水平。允许一点余量：菜单栏高度是整数像素，
        // 而 InnerSize 走的是逻辑像素（除以缩放比），两边可能差半个逻辑像素。
        double restored = Math.Abs(_heightAfterRemove - _heightBefore);
        Check(
            restored <= 2.0,
            $"拆掉菜单栏后内高没回到原值（原 {_heightBefore:0.#}，现在 {_heightAfterRemove:0.#}）——" +
            "菜单栏被拆掉了但客户区没还回来，页面会一直矮一行");
    }

    private static void HandlePlatform(JsonElement root, bool isMac)
    {
        string? error = OptionalError(root);

        if (!isMac)
        {
            // Windows/Linux：setAsWindowMenu 是可用的，这一步不该报错。
            Check(error is null, $"Windows/Linux 上 setAsWindowMenu 不该报错，实际：{error}");
            return;
        }

        Check(error is not null, "macOS 上 setAsWindowMenu 竟然没报错");
        if (error is not null)
        {
            Check(
                error.Contains("not_supported", StringComparison.OrdinalIgnoreCase) ||
                error.Contains("macOS", StringComparison.OrdinalIgnoreCase),
                $"macOS 上的报错没说清是「平台不支持」，页面无法按平台分支：{error}");
        }
    }

    /// <summary>读 <c>error</c> 字段；没有该字段或值为空时返回 null（表示"没报错"）。</summary>
    private static string? OptionalError(JsonElement root)
    {
        if (!root.TryGetProperty("error", out JsonElement error))
        {
            return null;
        }

        string? text = error.GetString();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    private static string Error(JsonElement root) => OptionalError(root) ?? string.Empty;

    private static void Check(bool condition, string description)
    {
        if (condition)
        {
            return;
        }

        lock (Gate)
        {
            Failures.Add(description);
        }
    }

    private static void Finish()
    {
        lock (Gate)
        {
            if (_finished)
            {
                return;
            }

            _finished = true;

            // 每一步都必须走到：漏一步说明脚本在中途抛了，那本身就是失败。
            Check(_windowSeen, "装窗口菜单栏那一步没有回执（命令失败或页面脚本中断）");
            Check(_appSeen, "装应用菜单那一步没有回执");
            Check(_removeSeen, "拆菜单栏那一步没有回执");
            Check(_platformSeen, "平台语义那一步没有回执");

            int failureCount = Failures.Count;
            Console.WriteLine(failureCount == 0
                ? "MENUBAR-SELFTEST: PASS"
                : $"MENUBAR-SELFTEST: FAIL（{failureCount} 项）");
            foreach (string failure in Failures)
            {
                Console.WriteLine("  - " + failure);
            }

            Console.WriteLine(
                $"[menubar-selftest] InnerSize 高度：装前={_heightBefore:0.#} " +
                $"装后={_heightWithBar:0.#} 拆后={_heightAfterRemove:0.#} " +
                $"（macOS=菜单不占窗口：{OperatingSystem.IsMacOS()}）");
        }
        Console.Out.Flush();

        _watchdog?.Dispose();

        WebviewWindow? main = _main;
        main?.PostToUiThread(main.App.Quit);
    }
}
