using OrielWeb;

namespace OrielDemo;

/// <summary>
/// 窗口观感自检（对标 Tauri 2 的 <c>setTitleBarStyle</c> / <c>setEffects</c> / <c>setShadow</c>）。
/// </summary>
/// <remarks>
/// <para>
/// <b>它补的是"看起来对不对"这一层</b>：单元测试能验枚举解析与参数校验，
/// 但验不了"消息真的落到了 AppKit 上"。这里逐档切换，然后用
/// <see cref="MacWindowProbe"/> 绕开库、直接读 <c>NSWindow</c> 的状态——
/// 库说"我设了透明标题栏"与 AppKit 说"标题栏确实透明"是两件事。
/// </para>
/// <para>
/// <b>为什么要停留</b>：断言只证明状态位对了，"材质看起来对不对"（模糊、染色、
/// 圆角、红绿灯位置）只能靠眼睛。设置环境变量 <c>ORIEL_VISUALS_DWELL=4</c> 会让每一档
/// 停 4 秒，期间窗口保持真实渲染，便于在虚拟机控制台截图取证。
/// 默认 0 秒（CI 上不需要等）。
/// </para>
/// <para>
/// <b>页面要求</b>：本自检载入 <c>visuals.html</c>，它的背景是透明的——
/// 页面一旦有不透明底色，材质就被盖住，看起来"设置没生效"。
/// </para>
/// <para>
/// 结论行：<c>VISUALS-SELFTEST: PASS</c>。
/// </para>
/// </remarks>
internal static class VisualSelfTest
{
    private static int _failures;

    /// <summary>
    /// 本自检窗口的标题。
    /// </summary>
    /// <remarks>
    /// <b>它是一条断言输入，不是装饰</b>：<see cref="MacWindowProbe"/> 靠标题在
    /// <c>NSApp.windows</c> 里找到被观测的那个窗口。Program 与自检必须用同一个值，
    /// 所以抽成常量——两处各写一份字面量，改一处忘另一处的症状是"窗口找不到 → 全项失败"。
    /// </remarks>
    internal const string WindowTitle = "Oriel Demo — 观感验证";

    /// <summary>本项是否失败——demo 的 Main 据此设置退出码。</summary>
    internal static bool Failed => Volatile.Read(ref _failures) > 0;

    /// <summary>每一档在屏幕上停留的秒数（<c>ORIEL_VISUALS_DWELL</c>，缺省 0）。</summary>
    private static int DwellSeconds
    {
        get
        {
            string? raw = Environment.GetEnvironmentVariable("ORIEL_VISUALS_DWELL");
            if (!int.TryParse(raw, out int seconds) || seconds <= 0)
            {
                return 0;
            }

            return Math.Min(seconds, 30);
        }
    }

    private static void Check(bool condition, string description)
    {
        if (condition)
        {
            Console.WriteLine($"[visuals-selftest] OK: {description}");
            return;
        }

        Interlocked.Increment(ref _failures);
        Console.WriteLine($"[visuals-selftest] 失败: {description}");
    }

    /// <summary>在 <c>Loaded</c> 里调用。</summary>
    internal static void Begin(OrielApp app, WebviewWindow window, string windowTitle)
    {
        // 页面档位注入钩子：截图取证时靠页面上的大字辨认"这一张是哪一档"。
        // 用 textContent 而不是 innerHTML：档位说明里有中文与括号，不值得为它做 HTML 转义。
        s_windowHook = (phase, detail) => window.EvaluateJs(
            "(() => {"
            + $"const p = document.getElementById('phaseText'); if (p) p.textContent = {SelfTestJson.String(phase)};"
            + $"const d = document.getElementById('detail'); if (d) d.textContent = {SelfTestJson.String(detail)};"
            + "})();");

        // Loaded 在 UI 线程上触发。async 方法在 await 期间让出消息循环——
        // 那正是"停留时窗口仍在真实渲染"的前提（同步 Sleep 会冻住画面，截出来是空窗）。
        _ = RunAsync(app, window, windowTitle);
    }

    private static async Task RunAsync(OrielApp app, WebviewWindow window, string windowTitle)
    {
        try
        {
            Console.WriteLine("[visuals-selftest] 开始（窗口观感 / 标题栏 / 材质）");

            if (!OperatingSystem.IsMacOS())
            {
                // 观感取证依赖 AppKit 的状态读取；Windows/Linux 的对应实现见各自后端，
                // 但本自检没有为它们写探针（DWM / GTK 的对应检查在各自平台单独做）。
                Console.WriteLine(
                    "[visuals-selftest] SKIP: 本自检目前只在 macOS 上取证"
                    + "（Windows 的 DWM 材质与 GTK 的窗口装饰需各自的探针）");
                return;
            }

            if (!MacDisplay.HasScreenSession)
            {
                Console.WriteLine(
                    "[visuals-selftest] SKIP: 本进程没有图形会话（机器停在登录窗口），"
                    + "窗口观感无从验证");
                return;
            }

            MacWindowProbe.WindowState initial = MacWindowProbe.Capture(windowTitle);
            if (!initial.Found)
            {
                // 让 AppKit 走完首帧再读一次：Loaded 紧跟着 webview 建好，
                // 窗口可能还没走到 orderFront，NSApp.windows 里也还可能没有它。
                await Task.Delay(600);
                initial = MacWindowProbe.Capture(windowTitle);
            }

            if (!initial.Found)
            {
                Interlocked.Increment(ref _failures);
                string known = string.Join(" | ", MacWindowProbe.WindowTitles());
                Console.WriteLine(
                    $"[visuals-selftest] 失败: 在 NSApp.windows 里找不到标题为"
                    + $"「{windowTitle}」的窗口（观感状态读不到，后续断言全部无意义）");
                Console.WriteLine(
                    $"[visuals-selftest] 诊断：NSApp.windows 共 {MacWindowProbe.WindowCount()} 个"
                    + $"，标题依次为：{(known.Length == 0 ? "(空)" : known)}");
                return;
            }

            // ---- 1) 装饰窗口的默认形态 ----
            Check(initial.HasSystemTitlebar, "初始形态：有系统标题栏（styleMask 含 Titled 且标题可见）");
            Check(!initial.TitlebarTransparent, "初始形态：标题栏不透明（titlebarAppearsTransparent = NO）");
            Check(!initial.ContentExtendsUnderTitlebar, "初始形态：内容从标题栏下方开始（无 fullSizeContentView）");
            Check(!initial.CloseButtonHidden && !initial.MiniaturizeButtonHidden && !initial.ZoomButtonHidden,
                "初始形态：红绿灯三键可见");
            // 透明是 setEffects 的副作用，不该在初始形态就存在：窗口一建出来就透明，
            // 意味着某处把 opaque 设成了 NO，而症状是"整个窗口看起来像贴纸"。
            Check(initial.IsOpaque, "初始形态：窗口不透明（isOpaque = YES）");

            // ---- 2) titleBarStyle 三档 ----
            await StyleAsync(window, windowTitle, OrielTitleBarStyle.Visible, "visible",
                "系统标题栏 + 标题可见 + 内容不侵入标题栏",
                s => s.HasSystemTitlebar && !s.TitlebarTransparent && !s.ContentExtendsUnderTitlebar);

            await StyleAsync(window, windowTitle, OrielTitleBarStyle.Transparent, "transparent",
                "标题栏透明但标题仍可见、内容仍从标题栏下开始",
                s => s.TitlebarTransparent && s.TitleVisibility == 0 && !s.ContentExtendsUnderTitlebar);

            await StyleAsync(window, windowTitle, OrielTitleBarStyle.Overlay, "overlay",
                "标题栏透明 + 标题隐藏 + 内容延伸到标题栏下（只剩红绿灯浮在上面）",
                s => s.TitlebarTransparent && s.TitleVisibility == 1 && s.ContentExtendsUnderTitlebar);

            // overlay 下红绿灯仍应可见——否则用户没法关窗口
            MacWindowProbe.WindowState overlay = MacWindowProbe.Capture(windowTitle);
            Check(!overlay.CloseButtonHidden, "overlay 形态：关闭按钮仍可见（否则关不掉窗口）");

            // 回到 visible，后续材质在正常标题栏下观察更接近日常形态
            window.SetTitleBarStyle(OrielTitleBarStyle.Visible);
            await Task.Delay(80);

            // ---- 3) 材质 ----
            await EffectsAsync(window, windowTitle, "sidebar", [OrielEffect.Sidebar], expectedMaterial: 7,
                "sidebar 材质（NSVisualEffectMaterial = 7）");

            await EffectsAsync(window, windowTitle, "hudWindow", [OrielEffect.HudWindow], expectedMaterial: 13,
                "hudWindow 材质（NSVisualEffectMaterial = 13）");

            // 跨平台写法：Windows 材质在前、macOS 材质在后 → 取第一个本平台支持的
            await EffectsAsync(window, windowTitle, "mica+sidebar", [OrielEffect.Mica, OrielEffect.Sidebar],
                expectedMaterial: 7,
                "材质列表 [mica, sidebar]：跳过本平台不支持的 mica，取 sidebar");

            // 只有 Windows 材质 → 必须报错而不是静默 succeed
            bool threw = false;
            try
            {
                window.SetEffects(new OrielEffects([OrielEffect.Mica], OrielEffectState.FollowsWindowActiveState, null, null, false));
            }
            catch (NotSupportedException)
            {
                threw = true;
            }

            Check(threw, "只有 mica（Windows 材质）时抛 NotSupportedException，不静默 succeed");

            // 液态玻璃：macOS 26+ 才有，本库明确不实现
            bool glassThrew = false;
            try
            {
                window.SetEffects(new OrielEffects([OrielEffect.LiquidGlassRegular], OrielEffectState.FollowsWindowActiveState, null, null, false));
            }
            catch (NotSupportedException)
            {
                glassThrew = true;
            }

            Check(glassThrew, "液态玻璃（需 macOS 26）抛 NotSupportedException，不假装支持");

            // ---- 4) 清掉材质后必须完全复原 ----
            window.ClearEffects();
            await Task.Delay(120);
            MacWindowProbe.WindowState cleared = MacWindowProbe.Capture(windowTitle);
            Check(cleared.EffectViewCount == 0, "clearEffects 后材质视图被移除（NSVisualEffectView 不再挂在 contentView 上）");
            Check(cleared.IsOpaque, "clearEffects 后窗口恢复不透明（opaque = YES）");
            Check(!cleared.WebviewTransparent, "clearEffects 后 webview 恢复不透明（isOpaque = YES 或背景不透明）");

            // ---- 5) 阴影 ----
            window.SetShadow(false);
            await Task.Delay(60);
            Check(!MacWindowProbe.Capture(windowTitle).HasShadow, "setShadow(false) 后 hasShadow = NO");
            window.SetShadow(true);
            await Task.Delay(60);
            Check(MacWindowProbe.Capture(windowTitle).HasShadow, "setShadow(true) 后 hasShadow = YES");

            // ---- 6) Dock 显隐（应用级） ----
            nint before = MacWindowProbe.ActivationPolicy();
            Check(before == 0, $"初始 activationPolicy = Regular(0)，Dock 里有图标（实际 {before}）");

            app.SetDockVisibility(false);
            await Task.Delay(150);
            nint hidden = MacWindowProbe.ActivationPolicy();
            Check(hidden == 1, $"setDockVisibility(false) → activationPolicy = Accessory(1)，Dock 图标消失（实际 {hidden}）");
            await DwellAsync("dock-hidden", "Dock 图标已隐藏（activationPolicy = accessory）");

            app.SetDockVisibility(true);
            await Task.Delay(150);
            nint shown = MacWindowProbe.ActivationPolicy();
            Check(shown == 0, $"setDockVisibility(true) → activationPolicy = Regular(0)，Dock 图标回来（实际 {shown}）");

            // ---- 7) 状态栏托盘图标 ----
            // 断言的是"状态栏里真的多了一个可见的项"，不是"托盘对象不为 null"。
            // 后者为真而前者为假的典型情形：图标没设、且状态项宽度为 0——用户看到的是
            // "托盘没工作"，而所有 API 都成功。
            if (app.Tray is { } tray)
            {
                Check(tray.IsVisible, "托盘对象存在且 IsVisible = true");

                int items = MacWindowProbe.StatusItemCount();
                Check(items > 0, $"NSStatusBar 里有状态项（实际 {items} 个）");

                // 不在这里读 statusItem.button.image：NSStatusBar 没有公开的 items 列表，
                // 只能经私有 KVC 拿，而多探一层（button → image）在部分系统上会抛 ObjC 异常，
                // 直接把进程带走。可见性改由截图确认（见 docs/CROSS-PLATFORM-VERIFICATION.md）。
            }
            else
            {
                Interlocked.Increment(ref _failures);
                Console.WriteLine("[visuals-selftest] 失败: 托盘未创建（AddTray 没生效），状态栏图标无从验证");
            }

            Console.WriteLine("[visuals-selftest] 完成：所有档位均已切换并读回 AppKit 状态");
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _failures);
            Console.WriteLine($"[visuals-selftest] 失败: 异常 {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            Report("VISUALS-SELFTEST");
            window.PostToUiThread(() => app.Quit());
        }
    }

    /// <summary>切一档标题栏样式：设置 → 读回 → 断言 → 停留。</summary>
    private static async Task StyleAsync(
        WebviewWindow window,
        string windowTitle,
        OrielTitleBarStyle style,
        string phase,
        string description,
        Func<MacWindowProbe.WindowState, bool> expectation)
    {
        window.SetTitleBarStyle(style);
        await Task.Delay(80); // 让 AppKit 走完一次重排（改 styleMask 会触发）

        MacWindowProbe.WindowState state = MacWindowProbe.Capture(windowTitle);
        Check(expectation(state),
            $"setTitleBarStyle({phase}) → {description}"
            + $"（实测 titlebarTransparent={state.TitlebarTransparent}、"
            + $"titleVisibility={state.TitleVisibility}、"
            + $"fullSizeContentView={state.ContentExtendsUnderTitlebar}）");

        await DwellAsync($"titleBarStyle = {phase}", description);
    }

    /// <summary>设一组材质：设置 → 读回 → 断言 → 停留。</summary>
    private static async Task EffectsAsync(
        WebviewWindow window,
        string windowTitle,
        string phase,
        IReadOnlyList<OrielEffect> effects,
        int expectedMaterial,
        string description)
    {
        window.SetEffects(new OrielEffects(effects, OrielEffectState.FollowsWindowActiveState, null, null, false));
        await Task.Delay(120);

        MacWindowProbe.WindowState state = MacWindowProbe.Capture(windowTitle);
        Check(
            state.EffectViewCount == 1
            && state.EffectMaterial == expectedMaterial
            && !state.IsOpaque
            && state.WebviewTransparent,
            $"setEffects({phase}) → {description} 真的挂上了"
            + $"（材质视图 {state.EffectViewCount} 个、material={state.EffectMaterial}、"
            + $"windowOpaque={state.IsOpaque}、windowBgAlpha={state.WindowBackgroundAlpha:0.###}、"
            + $"webviewTransparent={state.WebviewTransparent}"
            + $"（isOpaque={state.WebviewIsOpaque}、bgAlpha={state.WebviewBackgroundAlpha:0.###}）"
            + $" | 材质 frame={state.EffectFrame}"
            + $" | contentView 子视图：{state.SubviewSummary}）");

        await DwellAsync("setEffects: " + phase, description);

        if (state.WebviewInnerSummary.Length > 0)
        {
            Console.WriteLine($"[visuals-selftest] WKWebView 内部：{state.WebviewInnerSummary}");
        }

        // 材质能不能透出来，最终取决于页面自己有没有画底色：
        // 页面铺了不透明背景时，材质视图在它下面，怎么设都看不见。
        // 这里把 CSS 的**实际计算值**读回来（不是看源码写了什么）。
        try
        {
            string probe = await window.EvaluateJs(
                "JSON.stringify({html: getComputedStyle(document.documentElement).backgroundColor,"
                + "body: getComputedStyle(document.body).backgroundColor,"
                + "colorScheme: getComputedStyle(document.documentElement).colorScheme})");
            Console.WriteLine($"[visuals-selftest] 页面计算背景：{probe}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[visuals-selftest] 提示: 读页面背景失败（{ex.GetType().Name}）");
        }
    }

    /// <summary>把当前档位写到页面上（截图时靠它辨认），按需截图，然后按配置停留。</summary>
    private static async Task DwellAsync(string phase, string detail)
    {
        // 顺序不能变：页面先显示档位 → 再截图（否则照片里看不出是第几档）→ 最后才是停留。
        await InjectPhaseAsync(phase, detail);

        string? snapshotDirectory = Environment.GetEnvironmentVariable("ORIEL_VISUALS_SNAPSHOT");
        if (!string.IsNullOrWhiteSpace(snapshotDirectory))
        {
            await SnapshotAsync(snapshotDirectory, phase);
        }

        if (DwellSeconds <= 0)
        {
            return;
        }

        // ORIEL_VISUALS_ONLY：只在这一档停留（其余档位照常断言与截图，只是不停）。
        // 用途：人工盯着某一档看/截图时，不必等完整巡演。
        string? only = Environment.GetEnvironmentVariable("ORIEL_VISUALS_ONLY");
        if (!string.IsNullOrWhiteSpace(only)
            && !phase.Contains(only.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Console.WriteLine($"[visuals-selftest] 停留 {DwellSeconds}s：{phase}");
        await Task.Delay(TimeSpan.FromSeconds(DwellSeconds));
    }

    /// <summary>把档位文字写到页面上（给截图当标尺）。</summary>
    private static async Task InjectPhaseAsync(string phase, string detail)
    {
        if (s_windowHook is null)
        {
            return;
        }

        try
        {
            await s_windowHook(phase, detail);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[visuals-selftest] 提示: 页面档位文字注入失败（{ex.GetType().Name}），不影响断言");
        }
    }

    /// <summary>把当前窗口按真实颜色存成 PNG（<c>ORIEL_VISUALS_SNAPSHOT</c> 指定目录）。</summary>
    /// <remarks>
    /// 与虚拟机控制台缩略图的区别：那条路是 RGB565，实测 R 通道恒为 0，颜色不可用；
    /// 这条走窗口服务器合成结果，材质（模糊/染色）会被真实拍下来。
    /// </remarks>
    private static async Task SnapshotAsync(string directory, string phase)
    {
        try
        {
            Directory.CreateDirectory(directory);

            // 让 AppKit 走完这一档的布局与合成（改 styleMask 触发的重排不是同步的）
            await Task.Delay(220);

            uint windowNumber = MacWindowProbe.WindowNumber(WindowTitle);
            string name = Sanitize(phase);
            string path = Path.Combine(directory, name + ".png");

            if (MacScreenCapture.TrySaveWindow(windowNumber, path))
            {
                Console.WriteLine($"[visuals-selftest] 截图: {path}");
            }
            else
            {
                Console.WriteLine(
                    $"[visuals-selftest] 提示: 窗口截图失败（windowNumber={windowNumber}）——"
                    + "本档只有断言结果，没有图像证据");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[visuals-selftest] 提示: 截图异常 {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>把档位名变成安全文件名。</summary>
    private static string Sanitize(string phase)
    {
        var builder = new System.Text.StringBuilder(phase.Length);
        foreach (char c in phase)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(c);
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        return builder.ToString().Trim('-').ToLowerInvariant();
    }

    /// <summary>
    /// 页面注入钩子（由 <see cref="Begin"/> 绑定）：把档位写到 visuals.html 上。
    /// </summary>
    /// <remarks>
    /// 做成字段而不是在 <see cref="DwellAsync"/> 里直接捕获 window：
    /// 那几个辅助方法只关心"这一档叫什么"，不该各自持有一份窗口引用。
    /// </remarks>
    private static Func<string, string, Task>? s_windowHook;

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
