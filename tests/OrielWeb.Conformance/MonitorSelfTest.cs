using OrielWeb;

namespace OrielConformance;

/// <summary>
/// 显示器 API 自检（对标 Tauri 2 的 <c>Monitor</c> 与五个查询函数）。
/// </summary>
/// <remarks>
/// <para>
/// 由 <c>--selftest monitors</c> 启用。取证的是**宿主侧**能拿到什么：枚举、主屏、
/// 按点查、窗口所在屏、光标。页面侧的命令往返由单元测试（<c>BuiltInMonitorCommandTests</c>）
/// 与 <c>window.test.mjs</c> 覆盖——这里不重复造一遍页面脚本，因为这一批真正会出问题的是
/// **平台原生枚举**（三个系统三套 API），而那一层只有真机能证明。
/// </para>
/// <para>
/// 结论行：<c>MONITORS-SELFTEST: PASS</c>。失败时逐条打印 <c>失败:</c> 行。
/// </para>
/// <para>
/// <b>已知会失败的环境</b>：无头 X11（Xvfb）也能报出一台虚拟屏，这是预期；
/// 但若 GDK 完全拿不到 display（<c>DISPLAY</c> 未设且不是 Wayland），本自检会失败，
/// 因为那时"没有显示器"是真的——不是本库的缺陷。
/// </para>
/// </remarks>
internal static class MonitorSelfTest
{
    private static int _failures;

    /// <summary>本项是否失败——demo 的 Main 据此设置退出码。</summary>
    internal static bool Failed => Volatile.Read(ref _failures) > 0;

    private static void Check(bool condition, string description)
    {
        if (condition)
        {
            Console.WriteLine($"[monitors-selftest] OK: {description}");
            return;
        }

        Interlocked.Increment(ref _failures);
        Console.WriteLine($"[monitors-selftest] 失败: {description}");
    }

    /// <summary>在 <c>Loaded</c> 里调用：那时窗口已 realize，<c>currentMonitor</c> 才有答案。</summary>
    internal static void Begin(OrielApp app, WebviewWindow window)
    {
        try
        {
            Console.WriteLine("[monitors-selftest] 开始（宿主侧显示器 API）");

            // macOS 没有图形会话时 NSScreen.screens 是空的（机器停在登录窗口、走 SSH 调用）。
            // 那时"没有显示器"是真的，验的是环境——跳过而不是失败。
            if (OperatingSystem.IsMacOS() && !MacDisplay.HasScreenSession)
            {
                Console.WriteLine(
                    "[monitors-selftest] SKIP: 本进程没有图形会话（macOS 停在登录窗口），"
                    + "NSScreen 枚举为空——显示器 API 无从验证");
                return;
            }

            IReadOnlyList<OrielMonitor> monitors = app.AvailableMonitors;

            // 1) 至少一台
            Check(monitors.Count > 0, $"availableMonitors() 至少返回一台（实际 {monitors.Count} 台）");

            // 2) 每台的尺寸/缩放都可信
            foreach (OrielMonitor monitor in monitors)
            {
                string label = monitor.Name ?? "(无名)";
                Check(monitor.Size.Width > 0 && monitor.Size.Height > 0,
                    $"{label} 的尺寸有效（{monitor.Size.Width}x{monitor.Size.Height}）");
                Check(monitor.ScaleFactor >= 1.0,
                    $"{label} 的缩放比 ≥ 1（{monitor.ScaleFactor}）");
            }

            // 3) 主屏必须存在且能在列表里找到（按位置+尺寸匹配，不按对象引用）
            OrielMonitor? primary = app.PrimaryMonitor;
            Check(primary is not null, "primaryMonitor() 非 null");
            Check(primary is not null && Contains(monitors, primary),
                "主屏出现在 availableMonitors() 里");

            if (primary is not null)
            {
                // 4) 主屏中心点必须落回主屏自己
                double centerX = primary.Position.X + (primary.Size.Width / 2);
                double centerY = primary.Position.Y + (primary.Size.Height / 2);
                OrielMonitor? fromPoint = app.GetMonitorFromPoint(centerX, centerY);

                Check(fromPoint is not null && SameBounds(fromPoint, primary),
                    $"monitorFromPoint(主屏中心 {centerX},{centerY}) 返回主屏");

                // 5) 远离所有屏幕的点必须是 null（不能吸附到最近的屏）
                Check(app.GetMonitorFromPoint(-100000, -100000) is null,
                    "monitorFromPoint(屏幕之外) 返回 null（不吸附）");
            }

            // 6) 本窗口所在的屏：必须答得出来，且是枚举里的一台
            OrielMonitor? current = window.CurrentMonitor;
            Check(current is not null, "window.currentMonitor 非 null（窗口已显示）");
            Check(current is not null && Contains(monitors, current),
                "窗口所在屏出现在 availableMonitors() 里");

            // 7) 光标：只验"是个有限数"。跨平台没有统一的合法区间（Wayland 下甚至可能是应用像素），
            //    但 NaN/Infinity 一定是实现错误。
            OrielPosition cursor = app.CursorPosition;
            Check(double.IsFinite(cursor.X) && double.IsFinite(cursor.Y),
                $"cursorPosition() 是有限数（{cursor.X},{cursor.Y}）");

            // DoD 要求的一行摘要：显示器数 / 主屏尺寸 / 缩放
            string summary = string.Join(
                " | ",
                monitors.Select(m =>
                    $"{(m.IsPrimary ? "*" : string.Empty)}{m.Name ?? "(无名)"} " +
                    $"{m.Size.Width}x{m.Size.Height}@{m.ScaleFactor:0.##} " +
                    $"@{m.Position.X},{m.Position.Y}"));
            Console.WriteLine($"[monitors-selftest] 共 {monitors.Count} 台：{summary}");
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _failures);
            Console.WriteLine($"[monitors-selftest] 失败: 异常 {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            Report("MONITORS-SELFTEST");
            window.PostToUiThread(() => app.Quit());
        }
    }

    /// <summary>两台显示器在列表里是否是同一台（按矩形比，不按引用比）。</summary>
    private static bool SameBounds(OrielMonitor a, OrielMonitor b)
        => a.Position == b.Position && a.Size == b.Size;

    private static bool Contains(IReadOnlyList<OrielMonitor> monitors, OrielMonitor target)
        => monitors.Any(m => SameBounds(m, target));

    private static void Report(string name)
    {
        if (Failed)
        {
            Console.WriteLine($"{name}: FAIL（{Volatile.Read(ref _failures)} 项）");
        }
        else
        {
            Console.WriteLine($"{name}: PASS");
        }

        Console.Out.Flush();
    }
}
