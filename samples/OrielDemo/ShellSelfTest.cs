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
