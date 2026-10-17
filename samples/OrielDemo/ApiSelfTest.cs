using System.Text.Json;
using OrielWeb;

namespace OrielDemo;

/// <summary>
/// 前端包自检（<c>--selftest api</c>）：<c>@orielweb/api</c> 在**真实页面**里能不能用。
/// </summary>
/// <remarks>
/// <para>
/// 为什么要单独有这一项：包的所有单测都跑在 Node 里（<c>packages/api/tests/*.test.mjs</c>），
/// 而那一侧没有浏览器环境——ESM 加载、相对 import 解析、桥接脚本的存在性、
/// 宿主真的有那几条命令…… 一件都没验过。这一项补的正是那一段：
/// <b>它是唯一一处"包在真页面里被 import 并调用"的取证</b>。
/// </para>
/// <para>
/// 页面用**动态 import</b> 加载各个模块（<c>EvaluateJs</c> 在全局作用域求值，不能写静态 import），
/// 逐项跑一遍再把结论回显给宿主。这与用户实际用它的方式一致：
/// 静态 import 与动态 import 走的是同一个模块图。
/// </para>
/// <para>
/// 结论行：<c>API-SELFTEST: PASS</c> / <c>FAIL（…）</c>。
/// </para>
/// </remarks>
internal static class ApiSelfTest
{
    private const string ReportName = "api.probe";
    private const string VendorPath = "./vendor/orielweb-api";

    private static readonly object Gate = new();
    private static readonly List<string> Failures = [];

    private static WebviewWindow? _main;
    private static Timer? _watchdog;
    private static bool _finished;

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

    /// <summary>订阅页面回执（在 <c>onCreated</c> 里调用，早于导航）。</summary>
    internal static void AttachEvents(WebviewWindow main)
    {
        _main = main;
        main.MessageReceived += OnMessage;
    }

    /// <summary>开始跑（在 <c>Loaded</c> 里调用）。</summary>
    internal static void Begin() => _ = RunAsync();

    private static async Task RunAsync()
    {
        WebviewWindow main = _main!;

        try
        {
            _watchdog = new Timer(
                _ => Report(TimeSpan.FromSeconds(60).TotalSeconds, timedOut: true),
                null,
                TimeSpan.FromSeconds(60),
                Timeout.InfiniteTimeSpan);

            await main.EvaluateJs(BuildProbeScript());

            // 结论由页面回显带回来（见 OnMessage）；这里只负责别让 CI 白等
            int waited = 0;
            while (!_finished && waited < 60000)
            {
                await Task.Delay(100);
                waited += 100;
            }
        }
        catch (Exception ex)
        {
            Check(false, $"自检过程抛出异常：{ex.GetType().Name}: {ex.Message}");
            Report(0, timedOut: false);
        }
        finally
        {
            _watchdog?.Dispose();
            main.PostToUiThread(main.App.Quit);
        }
    }

    /// <summary>
    /// 页面侧的探针脚本。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 用 C# 的原始字符串字面量写，而不是一堆字符串拼接：这段 JS 有一百多行，
    /// 拼出来的版本读不了、也改不动（插入一个引号就变成"自检莫名超时"，指不到根因）。
    /// </para>
    /// <para>
    /// 每一项都**返回结构化结论**而不是抛异常就完：自检要能回答"哪一项不行"，
    /// 而不是"整个脚本炸了"。所以每项各自 try/catch，最后一次性回显。
    /// </para>
    /// </remarks>
    private static string BuildProbeScript() => $$"""
        (async function () {
            const vendor = '{{VendorPath}}';
            const checks = [];
            const check = (name, ok, detail) => checks.push({ name, ok: ok === true, detail: String(detail ?? '') });

            try {
                // ---- core ----
                const core = await import(vendor + '/core.js');
                check('core 导出三个函数',
                    typeof core.invoke === 'function' && typeof core.isTauri === 'function'
                        && typeof core.convertFileSrc === 'function',
                    Object.keys(core).join(','));

                check('core.isTauri() 在宿主里为 true', core.isTauri() === true, core.isTauri());

                // 真的发一条命令并拿到返回值——这是"包能驱动宿主"的最小证据
                const todos = await core.invoke('todo.list');
                check('core.invoke 能往返', Array.isArray(todos), Array.isArray(todos) ? todos.length : typeof todos);

                // convertFileSrc 尚未实现（P1-2）：必须**明确抛错**，不能返回一个看似可用的 URL
                let convertThrew = false;
                try { core.convertFileSrc('/tmp/a.png'); } catch (_) { convertThrew = true; }
                check('core.convertFileSrc 未实现时明确抛错', convertThrew, 'P1-2');

                // ---- dpi ----
                const dpi = await import(vendor + '/dpi.js');
                const size = new dpi.LogicalSize(800, 600);
                check('dpi.LogicalSize 带 type 字段',
                    size.type === 'Logical' && size.width === 800 && size.height === 600,
                    size.type);

                check('dpi.normalizeSize 接受类实例',
                    dpi.normalizeSize(size).type === 'Logical', 'ok');

                // 宿主侧的舍入是 .NET 的 ToEven（Math.Round(4.5) = 4），
                // 包必须复刻它——用 Math.round 会得到 5，差 1 像素且只在 .5 处出现
                check('dpi 舍入复刻 .NET 的 ToEven（3 * 1.5 = 4.5 → 4）',
                    dpi.logicalToPhysical(3, 1.5) === 4, dpi.logicalToPhysical(3, 1.5));

                // ---- event ----
                const event = await import(vendor + '/event.js');
                check('event.TauriEvent 与 Tauri 同名同值',
                    event.TauriEvent.WINDOW_RESIZED === 'tauri://resize'
                        && event.TauriEvent.WINDOW_CLOSE_REQUESTED === 'tauri://close-requested',
                    event.TauriEvent.WINDOW_RESIZED);

                const off = event.listen('no-such-event', function () {});
                check('event.listen 返回同步退订函数', typeof off === 'function', typeof off);
                off();

                check('event.once / emit / emitTo 都在',
                    typeof event.once === 'function' && typeof event.emit === 'function'
                        && typeof event.emitTo === 'function', 'ok');

                // ---- window ----
                const win = await import(vendor + '/window.js');
                const current = win.getCurrentWindow();
                check('window.getCurrentWindow().label 是宿主注入的值',
                    current.label === 'main', current.label);

                check('window.Window 类的 label 校验',
                    (function () { try { new win.Window(''); return false; } catch (_) { return true; } })(),
                    'ok');

                // 只读查询（在标题栏白名单里，不需要能力声明）
                check('window.Window.title() 能读回标题',
                    typeof (await current.title()) === 'string', await current.title());

                check('window.Window.isVisible() 返回布尔',
                    (await current.isVisible()) === true, 'ok');

                check('window.Window.scaleFactor() 是正数',
                    (await current.scaleFactor()) > 0, await current.scaleFactor());

                // 枚举窗口需要能力声明（demo 声明了 window.getAll）
                const all = await win.getAllWindows();
                check('window.getAllWindows 含本窗口',
                    Array.isArray(all) && all.some(function (w) { return w.label === 'main'; }),
                    Array.isArray(all) ? all.map(function (w) { return w.label; }).join(',') : typeof all);

                // getFocusedWindow 恒 null（三平台都还没有"哪个窗口有焦点"的查询能力）
                check('window.getFocusedWindow() 恒为 null（如实标注）',
                    (await win.getFocusedWindow()) === null, 'M3');

                // ---- 几何查询（E2）：断言形状与单位，不断言具体数值 ----
                // 具体数值依赖平台与主题（边框 16、标题栏 39 在别的系统上会变），
                // 而"是不是 LogicalSize 实例、type 对不对"不依赖——后者才是包该保证的。
                const innerSize = await current.innerSize();
                check('window.innerSize() 返回 LogicalSize 实例（type=Logical）',
                    innerSize instanceof dpi.LogicalSize && innerSize.type === 'Logical',
                    innerSize && (innerSize.type + ' ' + innerSize.width + 'x' + innerSize.height));

                const outerSize = await current.outerSize();
                check('window.outerSize() 返回 LogicalSize 实例',
                    outerSize instanceof dpi.LogicalSize && outerSize.type === 'Logical',
                    outerSize && outerSize.type);

                const outerPos = await current.outerPosition();
                check('window.outerPosition() 返回 LogicalPosition 实例',
                    outerPos instanceof dpi.LogicalPosition && outerPos.type === 'Logical',
                    outerPos && outerPos.type);

                const innerPos = await current.innerPosition();
                check('window.innerPosition() 返回 LogicalPosition 实例',
                    innerPos instanceof dpi.LogicalPosition && innerPos.type === 'Logical',
                    innerPos && innerPos.type);

                // InnerSize 必须与 setSize 同单位——否则"读出来写回去"会逐次放大/缩小。
                // 这条比上面三条都关键：它钉的是**单位一致性**，而那正是最容易被改错的地方。
                const before = await current.innerSize();
                await current.setSize(before);
                const after = await current.innerSize();
                check('setSize(innerSize()) 是不动点（单位一致）',
                    Math.abs(after.width - before.width) <= 2 && Math.abs(after.height - before.height) <= 2,
                    before.width + 'x' + before.height + ' -> ' + after.width + 'x' + after.height);

                // ---- mocks ----
                const mocks = await import(vendor + '/mocks.js');
                check('mocks.isMocked() 在真实宿主里为 false',
                    mocks.isMocked() === false, mocks.isMocked());
            } catch (error) {
                check('探针脚本本身没有抛出', false, String(error && error.stack ? error.stack : error));
            }

            window.oriel.postMessage('{{ReportName}}', { checks: checks });
        })();
        """;

    private static void OnMessage(OrielMessageReceivedEventArgs args)
    {
        if (args.Name != ReportName)
        {
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(args.Json);

            if (!doc.RootElement.TryGetProperty("checks", out JsonElement checks)
                || checks.ValueKind != JsonValueKind.Array)
            {
                Check(false, $"页面回执里没有 checks 数组：{args.Json}");
                Report(0, timedOut: false);
                return;
            }

            int total = 0;
            foreach (JsonElement item in checks.EnumerateArray())
            {
                total++;
                string name = item.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? "?" : "?";
                bool ok = item.TryGetProperty("ok", out JsonElement o) && o.ValueKind == JsonValueKind.True;
                string detail = item.TryGetProperty("detail", out JsonElement d) ? d.GetString() ?? "" : "";

                Check(ok, ok ? name : $"{name}（实际：{detail}）");
            }

            if (total == 0)
            {
                Check(false, "页面没有报告任何检查项（探针脚本可能提前退出了）");
            }
        }
        catch (JsonException ex)
        {
            Check(false, $"页面回执不是合法 JSON：{args.Json}（{ex.Message}）");
        }

        Report(0, timedOut: false);
    }

    private static void Check(bool condition, string description)
    {
        lock (Gate)
        {
            if (condition)
            {
                Console.WriteLine($"[api-selftest] OK: {description}");
                return;
            }

            Failures.Add(description);
            Console.WriteLine($"[api-selftest] 失败: {description}");
        }
    }

    private static void Report(double elapsedSeconds, bool timedOut)
    {
        lock (Gate)
        {
            if (_finished)
            {
                return;
            }

            _finished = true;

            if (timedOut)
            {
                Failures.Add($"页面探针在 {elapsedSeconds:F0} 秒内没有回显");
            }

            if (Failures.Count > 0)
            {
                Console.WriteLine($"API-SELFTEST: FAIL（{Failures.Count} 项）");
            }
            else
            {
                Console.WriteLine("API-SELFTEST: PASS");
            }
        }

        Console.Out.Flush();
    }
}
