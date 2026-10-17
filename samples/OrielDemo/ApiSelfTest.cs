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

                // ---- app（F4a）：只读元信息 ----
                const app = await import(vendor + '/app.js');

                const appName = await app.getName();
                check('app.getName() 给非空应用名',
                    typeof appName === 'string' && appName.length > 0, appName);

                const appVersion = await app.getVersion();
                check('app.getVersion() 形如 主.次.修订',
                    /^\d+\.\d+\.\d+$/.test(appVersion), appVersion);

                const appId = await app.getIdentifier();
                check('app.getIdentifier() 是目录安全的标识符',
                    /^[A-Za-z0-9._-]+$/.test(appId) && appId === appId.toLowerCase(), appId);

                // getTauriVersion 与 oriel.version **必须同源**：两处做能力探测
                // 得到矛盾答案的话，"该信哪个"就变成一个没有答案的问题。
                const orielVersion = await app.getTauriVersion();
                check('app.getTauriVersion() 与 window.oriel.version 同源',
                    orielVersion === window.oriel.version,
                    orielVersion + ' vs ' + window.oriel.version);

                check('app.supportsMultipleWindows() 为 true',
                    (await app.supportsMultipleWindows()) === true, 'ok');

                // demo 的能力名单里没有任何 app.* 条目——这几条能跑通就是"只读元信息放行"的证据
                check('app.* 元信息无需能力声明即可用', true, '见 Program.cs 的能力名单');

                // ---- path（F1）：同步常量 + 路径运算 ----
                const path = await import(vendor + '/path.js');

                // sep/delimiter 是**同步**的（读宿主注入的常量），所以这里刻意不 await
                check('path.sep() 与宿主平台一致（本机 Windows 上是反斜杠）',
                    path.sep() === '\\', JSON.stringify(path.sep()));
                check('path.delimiter() 与宿主平台一致（本机 Windows 上是分号）',
                    path.delimiter() === ';', JSON.stringify(path.delimiter()));

                const joined = await path.join('a', 'b', 'c');
                check('path.join 按平台分隔符拼接',
                    joined === ['a', 'b', 'c'].join(path.sep()), joined);

                // **不带前导点**——与 Node 的 path.extname 不同，与 Tauri/Rust 一致
                check('path.extname 不带前导点', (await path.extname('file.html')) === 'html',
                    await path.extname('file.html'));

                // 隐藏文件没有扩展名：这条 .NET 与 Rust 口径不同，是本库显式实现的那条规则
                check('path.extname 对 .gitignore 给空串（Rust/Node 口径，非 .NET 口径）',
                    (await path.extname('.gitignore')) === '', JSON.stringify(await path.extname('.gitignore')));

                check('path.basename 能剥离指定扩展名',
                    (await path.basename('dir/app.conf', '.conf')) === 'app',
                    await path.basename('dir/app.conf', '.conf'));

                const dirInput = ['a', 'b', 'c.txt'].join(path.sep());
                check('path.dirname 给出父目录',
                    (await path.dirname(dirInput)) === ['a', 'b'].join(path.sep()),
                    await path.dirname(dirInput));

                check('path.isAbsolute 判平台相关的绝对性',
                    (await path.isAbsolute(['C:', 'x'].join(path.sep()))) === true
                        && (await path.isAbsolute('a/b')) === false, 'ok');

                // normalize 是**词法**的：不存在的路径也要能归一（Tauri 用 canonicalize，会在这里报错）
                const ghost = ['nonexistent-oriel', 'sub', '..', 'file.txt'].join(path.sep());
                const normalized = await path.normalize(ghost);
                check('path.normalize 对不存在的路径也能工作（不做 I/O）',
                    normalized.endsWith(['nonexistent-oriel', 'file.txt'].join(path.sep())), normalized);

                // resolve 保证绝对；join 不保证——这是两者的实质差别
                const resolved = await path.resolve('sub', 'file.txt');
                check('path.resolve 给出绝对路径',
                    resolved !== joined && resolved.includes(path.sep() + 'sub'),
                    resolved);

                // path.* 整族放行：demo 的能力名单里**没有**任何 path.* 条目
                // （见 Program.cs），所以这几条能跑通本身就是"放行生效"的证据。
                check('path.* 无需能力声明即可用（整族放行）', true, '见 Program.cs 的能力名单');

                // ---- 结构化错误（F7）：码要真的到达页面 ----
                //
                // 用一条**确定会被拒**的命令取证：demo 刻意没有声明 window.setResizable。
                // 页面据此能分支（提示"没有权限"而不是"出错了"）——分不清这两者，
                // 是排障时最费时间的一件事。
                let deniedCode = null;
                try {
                    await core.invoke('window.setResizable', { value: false });
                } catch (e) {
                    deniedCode = e && e.code;
                }
                check('被拒的命令带着 code=not_allowed 到页面',
                    deniedCode === 'not_allowed', deniedCode);

                // 未知命令：要用一条**在能力名单里但没注册**的名字。
                //
                // 拿 `no.such.command` 试会得到 not_allowed —— 那是**正确**行为：
                // deny-by-default 下未声明的命令在授权层就被拒，压根走不到路由查找，
                // 于是它也不泄露"这个命令存不存在"。而 `todo.nonexistent` 命中了
                // Allow("todo.*")，能走到路由查找，才会给出 unknown_command。
                let unknownCode = 'unset';
                try {
                    await core.invoke('todo.nonexistent');
                } catch (e) {
                    unknownCode = e && e.code;
                }
                check('未知命令带着 code=unknown_command 到页面',
                    unknownCode === 'unknown_command', unknownCode);

                // 顺带钉住上面那条口径：名单外的名字（哪怕它也不存在）报的是 not_allowed
                let outsideCode = 'unset';
                try {
                    await core.invoke('no.such.command');
                } catch (e) {
                    outsideCode = e && e.code;
                }
                check('名单外的命令报 not_allowed（不泄露命令是否存在）',
                    outsideCode === 'not_allowed', outsideCode);

                // ---- per-call timeout（F7）：真宿主上要能用 ----
                //
                // 这一条与"传输层声明了 supportsPerCallTimeout"是两回事：
                // 声明对但桥接脚本没真读那个参数，页面就会拿到 30 秒默认超时——
                // 而那种失效在单测里看不出来（mock 不走真桥接）。
                let perCallOk = false;
                try {
                    await core.invoke('todo.list', null, { timeout: 5000 });
                    perCallOk = true;
                } catch (e) {
                    perCallOk = false;
                }
                check('per-call timeout 在真宿主上可用（不再被拒）', perCallOk, 'F7');

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
