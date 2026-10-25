using OrielWeb;

namespace OrielConformance;

/// <summary>
/// 托盘 API 自检（对标 Tauri 2 的托盘能力面）。
/// </summary>
/// <remarks>
/// <para>
/// 由 <c>--selftest tray</c> 启用。取证的是**后端**（C# 侧）能做的事：多托盘、按 id 取回、
/// 图标解码成对象、模板标记、菜单、显隐、标题、移除，以及**事件订阅链路**
/// （用后端的合成派发入口验证"后端事件 → 公开事件"这一段没断）。前端（<c>oriel.tray</c>）那一半
/// 由 <c>--selftest tray-js</c> 取证——它驱动页面真实调用内建 <c>tray.*</c> 命令。
/// </para>
/// <para>
/// 为什么拆成两个：后端 API 要能在**没有页面**的情况下独立验证（原生资源真的被创建/撤销），
/// 而前端 API 要能在**真实页面**里验证（命令往返、事件推送）。
/// 合并成一个会把两边的失败混在一起，看不出是哪一层坏了。
/// </para>
/// <para>
/// 结论行：<c>TRAY-SELFTEST: PASS</c> / <c>TRAY-JS-SELFTEST: PASS</c>。
/// </para>
/// </remarks>
internal static class TraySelfTest
{
    private static int _failures;

    /// <summary>本项是否失败——demo 的 Main 据此设置退出码。</summary>
    internal static bool Failed => Volatile.Read(ref _failures) > 0;

    private static void Check(bool condition, string description)
    {
        if (condition)
        {
            Console.WriteLine($"[tray-selftest] OK: {description}");
            return;
        }

        Interlocked.Increment(ref _failures);
        Console.WriteLine($"[tray-selftest] 失败: {description}");
    }

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

    /// <summary>
    /// 后端自检：在 UI 线程（窗口 Loaded 时）跑一遍托盘生命周期。
    /// </summary>
    internal static void RunBackend(OrielApp app, WebviewWindow window)
    {
        try
        {
            Console.WriteLine("[tray-selftest] 开始（后端 API）");

            // macOS 无图形会话时创建状态项会让 AppKit 直接终止进程（见 MacOSSession），
            // 库因此提前拒绝。那验的是环境——跳过整套，别把"没有桌面"报成"托盘坏了"。
            if (OperatingSystem.IsMacOS() && !MacDisplay.HasScreenSession)
            {
                Console.WriteLine(
                    "[tray-selftest] SKIP: 本进程没有图形会话（macOS 停在登录窗口），"
                    + "NSStatusBar 不可用——托盘 API 无从验证");
                Report("TRAY-SELFTEST");
                window.PostToUiThread(() => app.Quit());
                return;
            }

            // 1) 图标解码：仓库里的 app.png / app.ico 应当都能解出非空 RGBA
            OrielImage? png = DecodeRelative("app.png");
            Check(png is not null, "app.png 解码成功（PNG）");
            Check(png is null || png.Width > 0 && png.Height > 0, "app.png 尺寸有效");
            Check(png is null || png.Rgba.Length == png.Width * png.Height * 4, "app.png 的 RGBA 长度与宽高一致");

            OrielImage? ico = DecodeRelative("app.ico");
            Check(ico is not null, "app.ico 解码成功（ICO）");

            // 2) 多托盘：建两个，各自 id 能取回
            OrielTray first = app.CreateTray(o =>
            {
                o.Id = "selftest-a";
                o.Tooltip = "OrielWeb tray A";
                o.Icon = png;
                o.IconAsTemplate = false; // 彩色图不该被 macOS 反色
            });
            OrielTray second = app.CreateTray(o =>
            {
                o.Id = "selftest-b";
                o.Tooltip = "OrielWeb tray B";
                o.IconAsTemplate = true;
                o.Title = "42";
            });

            Check(first.Id == "selftest-a", "第一个托盘 id 正确");
            Check(second.Id == "selftest-b", "第二个托盘 id 正确");
            Check(app.Trays.Count >= 2, $"托盘表里有多个托盘（{app.Trays.Count} 个）");
            Check(ReferenceEquals(app.GetTray("selftest-a"), first), "GetTray 按 id 取回同一个句柄");
            Check(app.GetTray("no-such-tray") is null, "GetTray 对不存在的 id 返回 null");

            // 2b) 订阅链路：CreateTray 之后，句柄必须真的挂上了后端的事件。
            //
            // 真实的托盘点击要由系统把鼠标事件送进托盘区，**无法无头自动化**；但"接线断了"
            // 恰是这类代码最常见的回归（0.4.1 就有一次 CreateTray 漏接 MenuItemClicked，
            // 症状是"托盘菜单点了没反应"）。这里用后端内置的合成派发入口把"后端 → 句柄"
            // 这一段走一遍：它不证明系统真的送来了点击，只证明事件发出来之后一路能到达公开事件。
            int clickedCount = 0;
            int trayEventCount = 0;
            string? menuItemId = null;

            first.Clicked += () => clickedCount++;
            first.TrayEvent += _ => trayEventCount++;
            first.MenuItemClicked += id => menuItemId = id;

            if (first.EventProbe is { } probe)
            {
                probe.RaiseSynthetic(TraySyntheticEvent.Clicked);
                Check(clickedCount == 1, "订阅链路：后端 Clicked → OrielTray.Clicked");

                probe.RaiseSynthetic(TraySyntheticEvent.TrayEvent);
                Check(trayEventCount == 1, "订阅链路：后端 EventReceived → OrielTray.TrayEvent");

                probe.RaiseSynthetic(TraySyntheticEvent.MenuItemClicked, "selftest-item");
                Check(menuItemId == "selftest-item", "订阅链路：后端 MenuItemClicked → OrielTray.MenuItemClicked（id 原样到达）");
            }
            else
            {
                // 后端没实现探针就是失败：这条断言的价值全在"每个平台都真的能被验证"，
                // 缺一个平台等于那个平台的接线仍然无人看守。
                Check(false, "托盘后端未实现 ITrayEventProbe：订阅链路无从验证");
            }

            // 3) 图标对象与模板标记如实回读
            Check(ReferenceEquals(first.Icon, png), "托盘句柄持有传进去的图标对象");
            Check(!first.IconAsTemplate, "模板标记如实回读（false）");
            Check(second.IconAsTemplate, "模板标记如实回读（true）");

            // 4) 运行期换图（已解码对象 + 文件路径两条入口）
            first.SetIcon(ico);
            Check(ReferenceEquals(first.Icon, ico), "SetIcon(OrielImage) 生效");

            first.SetIcon("app.png");
            Check(first.Icon is not null && first.IconPath is not null, "SetIcon(string) 生效并记住路径");

            // 5) 菜单与显隐
            second.SetMenu(
            [
                OrielMenuItem.Item("do", "做点什么"),
                OrielMenuItem.Separator(),
                OrielMenuItem.RoleItem(OrielMenuRole.Quit),
            ]);
            Check(true, "SetMenu 未抛异常");

            first.Hide();
            Check(!first.IsVisible, "Hide 之后 IsVisible 为 false（或平台本就不显示）");
            first.Show();
            Check(true, "Show 未抛异常");

            // 6) 标题（Windows 上是空操作，Linux/macOS 显示）
            second.Title = "7";
            Check(second.Title == "7", "Title 如实回读");

            // 7) 移除：按 id 移除一个，剩下那个还在
            bool removed = app.RemoveTray("selftest-a");
            Check(removed, "RemoveTray(id) 返回 true");
            Check(app.GetTray("selftest-a") is null, "被移除的托盘已从表里消失");
            Check(app.GetTray("selftest-b") is not null, "另一个托盘不受影响");
            Check(!app.RemoveTray("selftest-a"), "重复移除同一个 id 返回 false");

            // 8) 重复 id 应当抛（否则 GetTray 的结果取决于创建顺序）
            bool threw = false;
            try
            {
                app.CreateTray(o => o.Id = "selftest-b");
            }
            catch (InvalidOperationException)
            {
                threw = true;
            }

            Check(threw, "重复的托盘 id 抛 InvalidOperationException");

            // 收尾：移除剩下那个（不留托盘，免得自检进程挂着不退出）
            app.RemoveTray("selftest-b");

            Report("TRAY-SELFTEST");
            window.PostToUiThread(() => app.Quit());
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _failures);
            Console.WriteLine($"[tray-selftest] 异常：{ex}");
            Report("TRAY-SELFTEST");
            window.PostToUiThread(() => app.Quit());
        }
    }

    /// <summary>
    /// 前端自检：让**页面**通过 <c>oriel.tray</c> 建托盘、设属性、订阅事件、移除。
    /// </summary>
    /// <remarks>
    /// 这一项证明的是"前后端一致"：页面调用的方法与 C# 侧同名同义，命令往返真的通。
    /// 结论由页面回显（宿主侧只负责发起与收尾），见 <c>ManualCommands</c> 之外的那条命令。
    /// </remarks>
    internal static void RunFrontend(OrielApp app, WebviewWindow window)
    {
        Console.WriteLine("[tray-js-selftest] 开始（页面侧 oriel.tray）");

        // 同 RunBackend：没有图形会话时状态项创建不起来，整套跳过。
        if (OperatingSystem.IsMacOS() && !MacDisplay.HasScreenSession)
        {
            Console.WriteLine(
                "[tray-js-selftest] SKIP: 本进程没有图形会话（macOS 停在登录窗口），"
                + "NSStatusBar 不可用——页面侧托盘 API 无从验证");
            Report("TRAY-JS-SELFTEST");
            window.PostToUiThread(() => app.Quit());
            return;
        }

        // 页面执行一段脚本：建托盘 → 取回 → 设属性 → 移除 → 把结果报回来。
        // 结果经 oriel.postMessage('tray-js-selftest', ...) 回传，宿主订阅 MessageReceived 收。
        const string script = """
            (async () => {
              const report = [];
              // 每完成一步立即回传：进程若在下一步崩溃，这条进度就是崩溃点。
              // 最终的 report 仍照旧一次性回传（结论判定不受影响）。
              const progress = (line) => oriel.postMessage('tray-js-progress', { line });
              const check = (ok, what) => {
                const line = (ok ? 'OK: ' : '失败: ') + what;
                report.push(line);
                progress(line);
              };
              progress('脚本开始执行');
              try {
                progress('即将调用 tray.new');
                const tray = await oriel.tray.new({ id: 'js-tray', tooltip: 'from page', iconAsTemplate: false });
                check(tray && tray.id === 'js-tray', 'oriel.tray.new 返回带 id 的句柄');

                const fetched = await oriel.tray.getById('js-tray');
                check(fetched !== null && fetched.id === 'js-tray', 'getById 取回同一个句柄');

                const all = await oriel.tray.getAll();
                check(all.some((t) => t.id === 'js-tray'), 'getAll 里能看到它');

                progress('即将调用 tray.setTooltip');
                await tray.setTooltip('changed');
                progress('即将调用 tray.setTitle');
                await tray.setTitle('99');
                progress('即将调用 tray.setIconAsTemplate');
                await tray.setIconAsTemplate(true);
                progress('即将调用 tray.setMenu');
                await tray.setMenu([{ id: 'x', label: 'X' }, { type: 'separator' }]);
                progress('即将调用 tray.setVisible');
                await tray.setVisible(false);
                await tray.setVisible(true);
                check(true, '句柄的全部 set* 方法都成功往返');

                const missing = await oriel.tray.getById('no-such');
                check(missing === null, '不存在的 id 返回 null');

                progress('即将调用 tray.close');
                const removed = await tray.close();
                check(removed === true, 'close() 移除成功');
                check((await oriel.tray.getById('js-tray')) === null, '移除后取不到了');

                const events = [];
                const off = oriel.tray.onEvent((e) => events.push(e));
                check(typeof off === 'function', 'onEvent 返回退订函数');
                off();
              } catch (err) {
                report.push('失败: 异常 ' + (err && err.message ? err.message : err));
              }
              oriel.postMessage('tray-js-selftest', { report });
            })();
            """;

        window.MessageReceived += args =>
        {
            // **即时进度**（诊断通道）：页面每完成一步就推一条，宿主立刻打印。
            //
            // 为什么需要它：`tray-js` 在 macOS 上曾出现 SIGSEGV（退出码 139），而原来的
            // report 是**脚本跑完后一次性回传**的——进程一崩，日志里只剩"开始"，
            // 连崩在 new / setMenu / close 哪一步都不知道。分段上报之后，最后一条进度
            // 就是崩溃点。这条通道不参与结论判定，只是取证。
            if (args.Name == "tray-js-progress")
            {
                try
                {
                    using var progress = System.Text.Json.JsonDocument.Parse(args.Json);
                    string line = progress.RootElement.GetProperty("line").GetString() ?? string.Empty;
                    Console.WriteLine($"[tray-js-selftest] 进度: {line}");
                    Console.Out.Flush();
                }
                catch
                {
                    // 诊断输出坏了不该影响自检结论
                }

                return;
            }

            if (args.Name != "tray-js-selftest")
            {
                return;
            }

            // 页面回传的 report 是一个字符串数组；逐条打印并统计失败
            foreach (System.Text.Json.JsonElement line in args.Json is { Length: > 0 }
                         ? System.Text.Json.JsonDocument.Parse(args.Json).RootElement
                             .GetProperty("report").EnumerateArray()
                         : [])
            {
                string text = line.GetString() ?? string.Empty;
                if (text.StartsWith("失败", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref _failures);
                }

                Console.WriteLine($"[tray-js-selftest] {text}");
            }

            Report("TRAY-JS-SELFTEST");
            window.PostToUiThread(() => app.Quit());
        };

        window.PostToUiThread(() => _ = window.EvaluateJs(script));
    }

    /// <summary>按可执行文件目录解析并解码一个图标文件；失败返回 null 并打印原因。</summary>
    private static OrielImage? DecodeRelative(string name)
    {
        string path = Path.Combine(AppContext.BaseDirectory, name);
        if (OrielImage.TryFromFile(path, out OrielImage? image, out string? error))
        {
            return image;
        }

        Console.WriteLine($"[tray-selftest] 解码 {name} 失败：{error}");
        return null;
    }
}
