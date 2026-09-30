using OrielWeb;

namespace OrielDemo;

/// <summary>
/// 托盘与通知的自检（无人交互）：创建托盘、设置一份覆盖各形态的菜单、尝试投递一条通知，
/// 打印结论并让应用退出——CI 据此把"外壳能力"变成机器断言。
/// </summary>
/// <remarks>
/// 它能断言的与不能断言的（照仓库的验证账规矩写清楚）：
/// 能——API 通路可用（创建托盘、构建含分隔线/勾选/禁用/子菜单/role 的菜单不抛异常）、
/// 通知能力被**如实报告**（环境缺客户端时必须报 false 而不是静默假装成功）、进程不崩。
/// 不能——图标是否真的出现在托盘区、菜单是否画对、通知横幅是否弹出：那些需要人眼，
/// 见 docs/ROADMAP.md 的待真机清单。
/// </remarks>
internal static class ShellSelfTest
{
    private static OrielApp? _app;
    private static Timer? _timer;

    /// <summary>自检是否失败（Program 据此决定进程退出码）。</summary>
    internal static bool Failed { get; private set; }

    /// <summary>
    /// 在 <c>Run()</c> 之前调用。延迟几秒再跑：那时消息循环已在转、托盘也已创建。
    /// </summary>
    internal static void Start(OrielApp app)
    {
        _app = app;
        // 计时器回调在线程池线程上，碰托盘前必须切回 UI 线程（GTK/AppKit 只能在各自主线程调用）
        _timer = new Timer(
            _ => app.PostToMainThread(Run),
            null,
            TimeSpan.FromSeconds(3),
            Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// 读回 Linux 的 autostart 项，逐项核对它写对了没有。
    /// </summary>
    /// <remarks>
    /// 这条断言是这批里最"硬"的：自启项写错不会当场失败，而是等用户下次开机才发现应用没起来——
    /// 所以这里不满足于"文件存在"，而是核到具体键（Exec 带引号的可执行路径 + 参数 + GNOME 的启用标志）。
    /// </remarks>
    private static bool CheckLinuxAutoStartFile()
    {
        string id = Path.GetFileNameWithoutExtension(Environment.ProcessPath) ?? "OrielWeb";
        string configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg
            ? xdg
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        string desktopFile = Path.Combine(configHome, "autostart", id + ".desktop");

        if (!File.Exists(desktopFile))
        {
            Console.WriteLine($"[shell-selftest] 找不到 autostart 文件：{desktopFile}");
            return false;
        }

        string content = File.ReadAllText(desktopFile);
        return content.StartsWith("[Desktop Entry]", StringComparison.Ordinal)
            && content.Contains("Exec=\"", StringComparison.Ordinal)
            && content.Contains("--minimized", StringComparison.Ordinal)
            && content.Contains("X-GNOME-Autostart-enabled=true", StringComparison.Ordinal);
    }

    private static void Run()
    {
        try
        {
            OrielTray? tray = _app!.Tray;
            if (tray is null)
            {
                Console.WriteLine("[shell-selftest] AddTray 未生效：OrielApp.Tray 为 null");
                Failed = true;
                return;
            }

            tray.Tooltip = "OrielWeb self-test";
            tray.SetMenu(
            [
                OrielMenuItem.Item("hello", "Hello"),
                OrielMenuItem.Separator(),
                new OrielMenuItem { Id = "checked", Label = "Checked item", Checked = true },
                new OrielMenuItem { Id = "disabled", Label = "Disabled item", Enabled = false },
                new OrielMenuItem
                {
                    Label = "Submenu",
                    Items = [OrielMenuItem.Item("sub-1", "Sub item 1")],
                },
                OrielMenuItem.Separator(),
                // quit 是唯一在托盘里有明确语义的 role（应用级动作）
                OrielMenuItem.RoleItem(OrielMenuRole.Quit),
            ]);
            Console.WriteLine("[shell-selftest] 托盘已创建，菜单已设置（分隔线/勾选/禁用/子菜单/quit role）");

            // 可见性单独报一行：无头环境（xvfb 里没有托盘宿主）下它是 false 且**属正常**，
            // 但把它打出来，才能区分"没建起来"与"建起来了但这个桌面不显示托盘"
            Console.WriteLine($"[shell-selftest] TRAY-VISIBLE: {(tray.IsVisible ? "true" : "false")}");

            bool notifications = _app.NotificationsSupported;
            Console.WriteLine($"[shell-selftest] NOTIFICATIONS-SUPPORTED: {(notifications ? "true" : "false")}");

            if (notifications)
            {
                _app.ShowNotification(new OrielNotificationOptions
                {
                    Title = "OrielWeb 自检",
                    Body = "看到这条通知说明通知投递可用。",
                    Id = "self-test",
                });
                Console.WriteLine("[shell-selftest] 已投递一条通知");
            }
            else
            {
                // 缺通知客户端时报"不支持"是期望行为而不是失败：关键在于如实报告，
                // 而不是假装发出去（CI 装了 libnotify-bin 后这一条会变成 true）
                Console.WriteLine("[shell-selftest] 环境缺通知客户端（notify-send）：按设计报告不支持");
            }

            // 应用菜单：macOS 上真的会替换主菜单栏，Windows 上（无边框窗口）按设计跳过，Linux 是空操作。
            // 上下文菜单不在这里测：它会弹出并等待用户选择（Windows 上还会阻塞），
            // 交互式的东西不进无人自检——它的可判定部分（菜单构建）已由上面托盘菜单覆盖。
            _app.SetAppMenu(
            [
                OrielMenuItem.Item("app-hello", "Hello"),
                OrielMenuItem.Separator(),
                OrielMenuItem.RoleItem(OrielMenuRole.Copy),
                OrielMenuItem.RoleItem(OrielMenuRole.Quit),
            ]);
            Console.WriteLine("[shell-selftest] 应用菜单已设置（macOS 生效 / Windows 无边框窗口跳过 / Linux 空操作）");

            // 全局快捷键：Linux 上按平台事实返回 false（X11 可做但未实现、Wayland 无解）。
            // 这里断言的是"如实报告"而不是"注册成功"——两种结果都能 PASS，但输出不同，
            // 取证脚本据平台断言具体取值。
            bool shortcutRegistered = _app.RegisterGlobalShortcut("CmdOrCtrl+Shift+F12");
            Console.WriteLine($"[shell-selftest] GLOBAL-SHORTCUT-REGISTERED: {(shortcutRegistered ? "true" : "false")}");

            if (shortcutRegistered)
            {
                // 注册成功时必须查得到（写法不同但等价也算同一个），且注销要成功——
                // 这两条在三平台都能机器断言，与"按键能否真的触发"无关。
                bool found = _app.IsGlobalShortcutRegistered("ctrl+shift+f12");
                bool unregistered = _app.UnregisterGlobalShortcut("CmdOrCtrl+Shift+F12");
                Console.WriteLine($"[shell-selftest] GLOBAL-SHORTCUT-LOOKUP: {(found ? "true" : "false")}");
                Console.WriteLine($"[shell-selftest] GLOBAL-SHORTCUT-UNREGISTERED: {(unregistered ? "true" : "false")}");

                if (!found || !unregistered)
                {
                    Failed = true;
                    Console.WriteLine("SHELL-SELFTEST: FAIL —— 注册成功但查不到或注销失败");
                    return;
                }
            }

            // 内建右键菜单：能断言的只有"默认策略确实是只留剪切/复制/粘贴"这一条——
            // 过滤动作发生在渲染引擎内部，无头环境里既弹不出菜单也看不到剩下哪几项。
            // 但"默认值"本身就是需求的核心（默认过滤），值得钉住：它被谁改成 Native 会立刻显现。
            string policy = _app.Windows.Count > 0
                ? _app.Windows[0].ContextMenuPolicy.ToString()
                : "（无窗口）";
            Console.WriteLine($"[shell-selftest] CONTEXT-MENU-POLICY: {policy}");

            // 开机自启：三平台都能形成"启用 → 查得到 → 禁用 → 查不到"的闭环，因此这是强断言。
            // 自检里立即禁用，不在环境里留下自启项。
            bool autoStartOn = _app.EnableAutoStart(["--minimized"]);
            bool autoStartSeen = _app.IsAutoStartEnabled;

            // 读回内容必须在禁用**之前**：禁用会把文件删掉（第一版就是在这里读空的）
            bool desktopOk = true;
            if (OperatingSystem.IsLinux() && autoStartOn)
            {
                desktopOk = CheckLinuxAutoStartFile();
                Console.WriteLine($"[shell-selftest] AUTOSTART-DESKTOP-OK: {(desktopOk ? "true" : "false")}");
            }

            bool autoStartOff = _app.DisableAutoStart();
            bool autoStartGone = !_app.IsAutoStartEnabled;
            Console.WriteLine(
                $"[shell-selftest] AUTOSTART: enable={autoStartOn} seen={autoStartSeen} disable={autoStartOff} gone={autoStartGone}");

            if (!(autoStartOn && autoStartSeen && autoStartOff && autoStartGone && desktopOk))
            {
                Failed = true;
                Console.WriteLine("SHELL-SELFTEST: FAIL —— 开机自启的启用/查询/禁用没有形成闭环");
                return;
            }

            // Shell：真调一次"打开外部链接"（CI 里 PATH 上的 xdg-open 是取证脚本放的替身，参数会被记下来），
            // 再确认危险目标被白名单挡在门外——**安全边界也要有断言**，不能只断言"能用"。
            bool openOk = _app.OpenExternal("https://example.com/oriel-selftest");
            bool rejected = !_app.OpenExternal("file:///etc/passwd")
                && !_app.OpenExternal("/etc/passwd")
                && !_app.OpenExternal("javascript:alert(1)");
            Console.WriteLine($"[shell-selftest] SHELL-OPEN-EXTERNAL: {(openOk ? "true" : "false")}");
            Console.WriteLine($"[shell-selftest] SHELL-REJECTED-DANGEROUS: {(rejected ? "true" : "false")}");

            if (!openOk || !rejected)
            {
                Failed = true;
                Console.WriteLine("SHELL-SELFTEST: FAIL —— 外部打开失败，或白名单没有拦住危险目标");
                return;
            }

            // 拖放：这里只做两件机器能判定的事——订阅事件不抛（API 通路形状可用），
            // 以及把边界如实打出来。真实拖拽是 XDND/OLE 会话，无头环境造不出来（不是"没做"），
            // 所以这一项**整体不声称已验证**，见 docs/ROADMAP.md 的待真机清单。
            int subscribed = 0;
            foreach (WebviewWindow window in _app.Windows)
            {
                window.FileDropped += _ => { };
                subscribed++;
            }
            Console.WriteLine($"[shell-selftest] FILE-DROP-SUBSCRIBED: {subscribed}（落点已随窗口创建注册，真实拖拽需人眼）");

            Console.WriteLine("SHELL-SELFTEST: PASS");
        }
        catch (Exception ex)
        {
            Failed = true;
            Console.WriteLine($"SHELL-SELFTEST: FAIL — {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            // 一次性自检：结论出来就退出，别让 CI 干等到超时
            _timer?.Dispose();
            _app?.Quit();
        }
    }
}
