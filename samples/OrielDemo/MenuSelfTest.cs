using System.Text.Json;
using OrielWeb;

namespace OrielDemo;

/// <summary>
/// 页面菜单自检（<c>--selftest menu</c>）：<c>menu.popup</c> 真的能弹出一个原生菜单。
/// </summary>
/// <remarks>
/// <para>
/// 单元测试能验到"菜单树序列化成什么形状""action 怎么路由"——但**"宿主真的把菜单画出来了"
/// 只能在真实消息循环里取证**，而且三平台的弹出路径完全不同（Win32 <c>TrackPopupMenuEx</c> /
/// GTK <c>gtk_menu_popup_at_rect</c> / AppKit <c>popUpMenuPositioningItem</c>）。
/// 这三条路各有各的坑（坐标系、DPI、flipped 视图），只能真跑。
/// </para>
/// <para>
/// <b>怎么在不点鼠标的前提下验证</b>：菜单弹出后由自检自己把它关掉——
/// 宿主侧对当前窗口发一个 <c>Escape</c>，或者直接调 <c>menu.popup</c> 的取消路径。
/// 本自检验的是"命令没报错、宿主没崩、三平台都能走到原生弹出"，不是"用户能点"。
/// 点击回传另有单测覆盖（<c>menu.test.mjs</c> 的 action 路由）。
/// </para>
/// <para>
/// 四条断言：
/// <list type="number">
///   <item>页面发 <c>menu.popup</c> 后**不报错**（命令认得、参数解析通过、宿主接受了这棵树）；</item>
///   <item>带子菜单的树也能弹（递归序列化正确——这条曾是真实缺陷：子菜单的 items 是空的）；</item>
///   <item>空菜单不弹也不报错（页面可能算出空菜单，那是正常情形不是错误）；</item>
///   <item>非法参数**明确报错**（<c>items</c> 不是数组），而不是静默什么都不做。</item>
/// </list>
/// </para>
/// <para>
/// 结论行：<c>MENU-SELFTEST: PASS</c> / <c>FAIL（…）</c>。
/// </para>
/// </remarks>
internal static class MenuSelfTest
{
    private const string Echo = "menu-echo";

    private static readonly List<string> Failures = [];
    private static readonly object Gate = new();

    private static WebviewWindow? _main;
    private static Timer? _watchdog;
    private static bool _finished;
    private static bool _popupSeen;
    private static bool _submenuSeen;
    private static bool _emptySeen;
    private static bool _invalidSeen;

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

    /// <summary>在 <c>Loaded</c> 里调用：这时后端已就位，菜单能落到真实原生对象上。</summary>
    internal static void Begin(OrielApp app, WebviewWindow main)
    {
        _main = main;
        _ = app;

        // **让宿主自己定时关菜单**，不依赖页面发 menu.close。
        //
        // 为什么：Windows 的 popup 在 UI 线程上同步阻塞（TrackPopupMenuEx 的模态循环），
        // 而 menu.close 也是一条 IPC 消息、要排进同一套消息泵——实测这条回路不可靠
        // （60 秒超时、零回执）。自动关闭由宿主后台线程投 WM_CANCELMODE，不需要页面配合。
        //
        // 环境变量而不是页面参数：这是给自检用的，不该成为 menu.popup 的公开契约。
        Environment.SetEnvironmentVariable("ORIELWEB_MENU_AUTODISMISS_MS", "300");

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
                        $"菜单自检超时（60 秒）：普通弹出={_popupSeen}，子菜单={_submenuSeen}，" +
                        $"空菜单={_emptySeen}，非法参数={_invalidSeen}");
                }

                main.PostToUiThread(Finish);
            },
            null,
            TimeSpan.FromSeconds(60),
            Timeout.InfiniteTimeSpan);

        // 用页面侧的真实模块驱动：走的是构建时内嵌到 wwwroot/vendor 的那份 src/menu.js，
        // 验的是"页面 → IPC → 原生菜单"整条路径。
        //
        // **路径必须是相对 URL，不能用包名**：页面里没有 importmap，也没有 node_modules，
        // `import('@orielweb/api/menu')` 会抛 "Failed to resolve module specifier"。
        // 与 ApiSelfTest 用同一个 vendor 路径（它是构建时由 csproj 内嵌过去的）。
        //
        // **Windows 上 popup 会阻塞到用户选择**，所以每次弹出后必须把它关掉。
        // 关键设计：**关菜单由宿主侧做，不由页面做**。
        //
        // 为什么不能靠页面发 `menu.close`：popup 的 IPC handler 是**同步**的
        // （`TrackPopupMenuEx` 阻塞 UI 线程），而 `menu.close` 也是一条 IPC 消息——
        // 它要排在同一套消息泵里。实测这条回路不可靠（60 秒超时、零回执），
        // 所以自检改走宿主侧：`PostToUiThread` 投递一个"等菜单起来后投 WM_CANCELMODE"的动作。
        // 这条路径不依赖页面的消息能否在阻塞期间被处理，是稳定的。
        _ = main.EvaluateJs("""
            (async () => {
                const vendor = './vendor/orielweb-api';
                const post = (payload) => oriel.postMessage('menu-echo', payload);

                try {
                    const { Menu } = await import(vendor + '/menu.js');

                    // await 是安全的：宿主会在 300ms 后自己取消跟踪
                    // （ORIELWEB_MENU_AUTODISMISS_MS，见 Begin）。三平台都靠这条——
                    // Windows 投 WM_CANCELMODE，macOS 用事件跟踪模式下的定时器。

                    // ① 普通三项菜单（含一条分隔线与一个勾选项）
                    const simple = await Menu.new({
                        items: [
                            { id: 'a', text: 'Alpha' },
                            { id: 'b', text: 'Beta', checked: true },
                            { item: 'Separator' },
                            { id: 'c', text: 'Gamma', enabled: false },
                        ],
                    });
                    await simple.popup({ x: 40, y: 40 });
                    post({ step: 'popup' });

                    // ② 带子菜单（递归序列化——这条曾是真实缺陷：子菜单的 items 是空的）
                    const nested = await Menu.new({
                        items: [{
                            text: 'Outer',
                            items: [
                                { id: 'inner1', text: 'Inner 1' },
                                { text: 'Deeper', items: [{ id: 'deep', text: 'Deep' }] },
                            ],
                        }],
                    });
                    await nested.popup({ x: 40, y: 40 });
                    post({ step: 'submenu' });

                    // ③ 空菜单：应当静默成功（没有可弹的东西不是错误）
                    const empty = await Menu.new({ items: [] });
                    await empty.popup();
                    post({ step: 'empty' });

                    // ④ 非法参数：items 不是数组 → 必须报错
                    try {
                        await oriel.invoke('menu.popup', { items: 'not-an-array' });
                        post({ step: 'invalid', error: 'no-error' });
                    } catch (e) {
                        post({ step: 'invalid', error: String((e && e.message) || e) });
                    }
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

        switch (step)
        {
            case "popup":
                Mark(ref _popupSeen, "普通菜单");
                break;

            case "submenu":
                Mark(ref _submenuSeen, "子菜单");
                break;

            case "empty":
                Mark(ref _emptySeen, "空菜单");
                break;

            case "invalid":
                HandleInvalid(root);
                break;

            case "fatal":
                Check(false, $"菜单自检脚本抛错：{Error(root)}");
                _main?.PostToUiThread(Finish);
                break;
        }
    }

    private static void HandleInvalid(JsonElement root)
    {
        lock (Gate)
        {
            if (_invalidSeen)
            {
                return;
            }

            _invalidSeen = true;
        }

        if (root.TryGetProperty("error", out JsonElement error) &&
            error.GetString() == "no-error")
        {
            Check(false, "items 不是数组竟然成功了——非法参数必须当场报错，不能静默当空菜单");
        }
        else
        {
            string message = Error(root);
            Check(message.Contains("items", StringComparison.OrdinalIgnoreCase),
                $"非法参数的报错没提到 items，页面无法归因：{message}");
        }

        _main?.PostToUiThread(Finish);
    }

    /// <summary>记一次"这一步没抛错"；重复到达只记第一次。</summary>
    private static void Mark(ref bool flag, string label)
    {
        lock (Gate)
        {
            if (flag)
            {
                return;
            }

            flag = true;
        }

        _ = label;
    }

    private static string Error(JsonElement root)
        => root.TryGetProperty("error", out JsonElement error)
            ? error.GetString() ?? string.Empty
            : string.Empty;

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

            // 四步都必须走到：漏一步说明脚本在中途抛了，那本身就是失败。
            Check(_popupSeen, "普通菜单的 popup 没有回执（命令失败或页面脚本中断）");
            Check(_submenuSeen, "子菜单的 popup 没有回执");
            Check(_emptySeen, "空菜单的 popup 没有回执");
            Check(_invalidSeen, "非法参数那一组没有回执");

            int failureCount = Failures.Count;
            Console.WriteLine(failureCount == 0
                ? "MENU-SELFTEST: PASS"
                : $"MENU-SELFTEST: FAIL（{failureCount} 项）");
            foreach (string failure in Failures)
            {
                Console.WriteLine("  - " + failure);
            }
        }
        Console.Out.Flush();

        _watchdog?.Dispose();

        WebviewWindow? main = _main;
        main?.PostToUiThread(main.App.Quit);
    }
}
