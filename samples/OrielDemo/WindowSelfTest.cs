using System.Text.Json;
using OrielWeb;

namespace OrielDemo;

/// <summary>
/// 窗口 API 命令族自检（<c>--selftest window</c>）：按 label 寻址 + 能力门禁。
/// </summary>
/// <remarks>
/// <para>
/// 取证 D1/D2 的两件事：
/// </para>
/// <list type="number">
///   <item><b>按 label 寻址</b>：页面从**主窗口**发命令，要求作用到**另一个窗口**上。
///     这是 <c>window.*</c> 与 <c>win.*</c> 的本质区别——后者只能作用于发起者。</item>
///   <item><b>能力门禁</b>：同一次运行里对照两条命令——白名单里的（<c>window.isVisible</c>）
///     必须放行，没声明的（<c>window.setResizable</c>）必须被拒。
///     只验一侧会漏掉"放行分支写坏了"或"门禁根本没生效"。</item>
/// </list>
/// <para>
/// 页面侧用裸 <c>window.oriel</c> 而不是 <c>@orielweb/api/window</c>：本自检取的是
/// **宿主侧契约**（命令名、label 路由、门禁），而 JS 包的映射由 <c>tests/window.test.mjs</c>
/// 覆盖（那里能造任意时序与失败）。两者分工，不重复。
/// </para>
/// <para>
/// 结论行：<c>WINDOW-SELFTEST: PASS</c> / <c>FAIL（…）</c>。
/// </para>
/// </remarks>
internal static class WindowSelfTest
{
    private const string AssetHost = "app.oriel";
    private const string ProbeLabel = "win-probe";

    /// <summary>带系统装饰的对照窗口（见 isDecorated 那一段）。</summary>
    private const string DecoratedProbeLabel = "decorated-probe";
    private const string ProbeUrl = "https://" + AssetHost + "/win-probe.html";

    private static readonly object Gate = new();
    private static readonly List<string> Failures = [];

    private static OrielApp? _app;
    private static WebviewWindow? _main;
    private static Timer? _watchdog;

    /// <summary>页面回执：id → (ok, 值或错误)。</summary>
    private static readonly Dictionary<int, (bool Ok, string? Error, JsonElement Value)> Replies = [];

    private static int _nextRequestId;

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

    /// <summary>开始跑（在 <c>Loaded</c> 里调用，此时 App 可用）。</summary>
    internal static void Begin(OrielApp app) => _ = RunAsync(app);

    private static async Task RunAsync(OrielApp app)
    {
        _app = app;
        WebviewWindow main = _main!;

        try
        {
            _watchdog = new Timer(_ => OnTimeout(), null, TimeSpan.FromSeconds(90), Timeout.InfiniteTimeSpan);

            // ---- 建第二个窗口（探针页面，只是为了让"另一个窗口"真实存在）----
            WebviewWindow probe = app.CreateWindow(
                w => w.WithLabel(ProbeLabel)
                      .WithTitle("窗口探针")
                      .WithSize(360, 260)
                      .WithFrameless()
                      .WithUrl(ProbeUrl));

            // 等它真的进了窗口表——CreateWindow 返回时页面还没加载完，
            // 但 label 已经在表里了，所以这一刻就能查
            await Task.Delay(400);

            // ---- 1. 枚举：从主窗口页面能看到两个窗口 ----
            JsonElement labels = await AskMainAsync("window.getAll", "null");
            Check(labels.ValueKind == JsonValueKind.Array, $"window.getAll 应返回数组（实际 {labels.ValueKind}）");

            if (labels.ValueKind == JsonValueKind.Array)
            {
                var seen = new List<string>();
                foreach (JsonElement item in labels.EnumerateArray())
                {
                    seen.Add(item.GetString() ?? string.Empty);
                }

                Check(seen.Contains("main"), $"window.getAll 应包含 main（实际 {string.Join(",", seen)}）");
                Check(seen.Contains(ProbeLabel), $"window.getAll 应包含 {ProbeLabel}（实际 {string.Join(",", seen)}）");
            }

            // ---- 2. 按 label 寻址：主窗口发命令，作用到另一个窗口 ----
            // 这是 window.* 与 win.* 的本质区别。若路由写错成"总是作用到发起者"，
            // 下面这条断言会拿到 main 的状态而暴露。
            bool probeVisible = (await AskWithLabelAsync("window.isVisible", ProbeLabel)).GetBoolean();
            Check(probeVisible, "window.isVisible 按 label 应当读到**另一个**窗口的状态");

            // hide 另一个窗口，再从主窗口查它——两边的可观测状态都要对上
            await AskWithLabelAsync("window.hide", ProbeLabel);
            await WaitForAsync(() => !probe.IsVisible, 5000);
            Check(!probe.IsVisible, "window.hide 应当真的把**另一个**窗口藏起来（宿主侧可观测）");

            await AskWithLabelAsync("window.show", ProbeLabel);
            await WaitForAsync(() => probe.IsVisible, 5000);
            Check(probe.IsVisible, "window.show 应当把另一个窗口显示回来");

            // ---- 3. 改标题 + 读回：用一条**非可见性**的命令再证一次路由 ----
            //
            // 为什么读回而不是等 TitleChanged：那个事件只在**页面文档标题**变化时触发
            // （标题栏跟随页面），程序调 SetTitle 不会触发它——第一次写这条断言时就栽在这里
            // （"没有收到 TitleChanged"看起来像路由坏了，其实是观测点选错了）。
            await AskMainAsync(
                "window.setTitle",
                SelfTestJson.Object(
                    ("label", SelfTestJson.String(ProbeLabel)),
                    ("title", SelfTestJson.String("被主窗口改过"))));

            string titleOnProbe = (await AskWithLabelAsync("window.title", ProbeLabel)).GetString() ?? string.Empty;
            Check(
                titleOnProbe == "被主窗口改过",
                $"window.setTitle 应当改到目标窗口上，读回应为「被主窗口改过」（实际「{titleOnProbe}」）");

            // 反向确认：发起者自己的标题**没有**被改（若路由写错成"总是作用到发起者"，
            // 上一条会失败而这一条会通过——两条一起才说明作用对象是对的）
            string titleOnMain = (await AskWithLabelAsync("window.title", "main")).GetString() ?? string.Empty;
            Check(
                titleOnMain != "被主窗口改过",
                $"window.setTitle 不该改到发起者上（main 的标题变成了「{titleOnMain}」）");

            // ---- 4. 能力门禁：未声明的必须被拒 ----
            // 白名单命令（只读查询）在第 2 步已经验过放行。这里验反面：
            // demo 刻意**没有**声明 window.setResizable。
            JsonElement? denied = await TryAskMainAsync(
                "window.setResizable",
                SelfTestJson.Object(
                    ("label", SelfTestJson.String(ProbeLabel)),
                    ("value", SelfTestJson.Bool(false))));

            Check(denied is null, "window.setResizable 没被声明，应当被拒（若成功了说明门禁没生效）");

            if (denied is null)
            {
                // 从另一个入口确认它确实是被**权限**拒的，而不是因为别的错
                string error = LastError ?? string.Empty;
                Check(
                    error.Contains("Allow 名单") || error.Contains("Deny 名单"),
                    $"拒绝原因应当是权限（实际：{error}）");
            }

            // ---- 5. 只读状态查询（M5-1）：真窗口上的取值与流转 ----
            //
            // isFocused 断言的是"**页面查询与宿主侧一致**"，而不是"哪个窗口应当有焦点"。
            // 理由：焦点取决于桌面把前台给了谁，而那在无人自检里不受控
            // （刚创建的窗口通常会拿到前台；`--selftest windowevents` 就吃过这个环境的亏）。
            // "两者一致"既验了查询真的读了状态，又不依赖那个环境事实。
            bool probeFocused = (await AskWithLabelAsync("window.isFocused", ProbeLabel)).GetBoolean();
            bool mainFocused = (await AskWithLabelAsync("window.isFocused", "main")).GetBoolean();

            Check(probeFocused == probe.IsFocused,
                $"window.isFocused 应当与宿主侧一致（页面 {probeFocused} vs 宿主 {probe.IsFocused}）");

            Check(mainFocused == main.IsFocused,
                $"window.isFocused 应当与宿主侧一致（main：页面 {mainFocused} vs 宿主 {main.IsFocused}）");

            // 不变量：两个窗口不可能同时是前台窗口。
            // 这条挡住"永远返回 true"，而上面那两条挡住"永远返回 false"。
            Check(!(probeFocused && mainFocused),
                "两个窗口不可能同时拥有焦点（若都 true 说明查询没按窗口判）");

            Check(!(await AskWithLabelAsync("window.isMinimized", ProbeLabel)).GetBoolean(),
                "window.isMinimized 对刚建出来的窗口应当是 false");

            Check(!(await AskWithLabelAsync("window.isFullscreen", ProbeLabel)).GetBoolean(),
                "window.isFullscreen 对刚建出来的窗口应当是 false");

            Check(!(await AskWithLabelAsync("window.isAlwaysOnTop", ProbeLabel)).GetBoolean(),
                "window.isAlwaysOnTop 对刚建出来的窗口应当是 false");

            // **状态流转**：只验"初始值为假"会被"永远返回 false"骗过，
            // 所以让宿主真去切一次状态，再看查询跟不跟得上。
            probe.Minimize();
            await WaitForAsync(() => probe.IsMinimized, 5000);
            Check(probe.IsMinimized, "host 侧 Minimize() 之后 IsMinimized 应当为 true");
            Check((await AskWithLabelAsync("window.isMinimized", ProbeLabel)).GetBoolean(),
                "window.isMinimized 应当跟上宿主状态（true）");

            // 还原
            probe.Restore();
            await WaitForAsync(() => !probe.IsMinimized, 5000);
            Check(!probe.IsMinimized, "host 侧 Restore() 之后 IsMinimized 应当回到 false");
            Check(!(await AskWithLabelAsync("window.isMinimized", ProbeLabel)).GetBoolean(),
                "window.isMinimized 应当跟上宿主的还原（false）");

            // 置顶：切换之后查询要跟着变（这条验的是"记住的状态"而不是原生查询）
            probe.ToggleOnTop();
            await Task.Delay(200);
            bool onTopAfterToggle = (await AskWithLabelAsync("window.isAlwaysOnTop", ProbeLabel)).GetBoolean();
            Check(onTopAfterToggle == probe.IsAlwaysOnTop,
                $"window.isAlwaysOnTop 应当与宿主侧一致（页面 {onTopAfterToggle} vs 宿主 {probe.IsAlwaysOnTop}）");
            Check(onTopAfterToggle, "ToggleOnTop() 之后 window.isAlwaysOnTop 应当是 true");

            // ---- 6. 外观与能力的只读查询（M5-2）----
            //
            // 这一组断言"页面查询与宿主侧一致"（不依赖环境事实），
            // 因为假窗口验不了、而真窗口的取值又依赖平台与选项——
            // "两边一致"是唯一既能证明查询通了、又不假设取值的断言。
            (string Command, bool HostValue)[] appearanceQueries =
            [
                ("window.isResizable", probe.IsResizable),
                ("window.isMaximizable", probe.IsMaximizable),
                ("window.isMinimizable", probe.IsMinimizable),
                ("window.isClosable", probe.IsClosable),
            ];

            foreach ((string command, bool hostValue) in appearanceQueries)
            {
                bool pageValue = (await AskWithLabelAsync(command, ProbeLabel)).GetBoolean();

                Check(
                    pageValue == hostValue,
                    $"{command} 应当与宿主侧一致（页面 {pageValue} vs 宿主 {hostValue}）");
            }

            // 无边框探针窗口 → isDecorated 必须是 false。
            Check(!(await AskWithLabelAsync("window.isDecorated", ProbeLabel)).GetBoolean(),
                "探针窗口是无边框的，isDecorated 应当是 false");

            // **正反对照**：demo 的主窗口也是无边框的（自绘标题栏），
            // 所以"两个都 false"证明不了查询不是写死的——必须另建一个**带装饰**的窗口。
            // （第一版我拿主窗口当对照，结果那条失败：主窗口本来就是 frameless。）
            WebviewWindow? decorated = app.CreateWindow(
                w => w.WithLabel(DecoratedProbeLabel)
                      .WithTitle("装饰探针")
                      .WithSize(320, 240)
                      .WithUrl(ProbeUrl));

            try
            {
                await Task.Delay(400);

                Check((await AskWithLabelAsync("window.isDecorated", DecoratedProbeLabel)).GetBoolean(),
                    "带装饰的窗口 isDecorated 应当是 true");

                // 同一个宿主、同一时刻，两个窗口给出不同的答案——
                // 这才说明查询真的读了每个窗口自己的状态
                Check(!(await AskWithLabelAsync("window.isDecorated", ProbeLabel)).GetBoolean(),
                    "同一时刻无边框窗口仍应当是 false（对照）");
            }
            finally
            {
                decorated?.Destroy();
                await Task.Delay(200);
            }

            // theme：三平台总能读到系统主题，所以给得出 'light' / 'dark'。
            string theme = (await AskWithLabelAsync("window.theme", ProbeLabel)).GetString() ?? "";
            Check(theme is "light" or "dark", $"window.theme 应当给 light/dark（实际「{theme}」）");

            // ---- 7. 定向销毁：关掉的是目标窗口而不是发起者 ----
            await AskWithLabelAsync("window.destroy", ProbeLabel);
            await WaitForAsync(() => !app.Windows.Contains(probe), 5000);

            Check(!app.Windows.Contains(probe), "window.destroy 应当关掉目标窗口");
            Check(app.Windows.Contains(main), "window.destroy 不该把发起者一起关掉");
        }
        catch (Exception ex)
        {
            Check(false, $"自检过程抛出异常：{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _watchdog?.Dispose();
            Report();
            main.PostToUiThread(_app!.Quit);
        }
    }

    /// <summary>主窗口最后一次 <c>window.*</c> 调用的失败原因（成功时为 null）。</summary>
    private static string? LastError { get; set; }

    /// <summary>让主窗口的页面发一条命令，返回它的值；失败时抛出。</summary>
    private static async Task<JsonElement> AskMainAsync(string command, string argsJson)
    {
        JsonElement? result = await TryAskMainAsync(command, argsJson);
        if (result is null)
        {
            throw new InvalidOperationException(
                $"页面执行 '{command}' 失败：{LastError ?? "（无错误信息）"}");
        }

        return result.Value;
    }

    /// <summary>让主窗口的页面发一条命令，只带 label 参数。</summary>
    private static Task<JsonElement> AskWithLabelAsync(string command, string label)
        => AskMainAsync(command, LabelArgs(label));

    /// <summary>让主窗口的页面发一条命令；失败时返回 null（错误在 <see cref="LastError"/>）。</summary>
    /// <remarks>
    /// <para>
    /// <b>结果必须经回显拿，不能直接读 EvaluateJs 的返回值</b>：<c>oriel.invoke</c> 返回 Promise，
    /// 而 <c>ExecuteScriptAsync</c> 不会替我们 await 它——直接读会得到 <c>{}</c>。
    /// C3 的 event 自检踩过同一个坑。
    /// </para>
    /// <para>
    /// 参数对象由宿主序列化成 JSON 文本再嵌进脚本：它是我们自己的匿名对象，
    /// 但走 JSON 而不是手拼引号，免得将来加了字符串参数就变成注入。
    /// </para>
    /// </remarks>
    /// <summary>只带 label 的参数（最常用的一形）。</summary>
    private static string LabelArgs(string label)
        => SelfTestJson.Object(("label", SelfTestJson.String(label)));

    private static async Task<JsonElement?> TryAskMainAsync(string command, string argsJson)
    {
        WebviewWindow main = _main!;
        int id = Interlocked.Increment(ref _nextRequestId);

        string script =
            "window.oriel.invoke(" + SelfTestJson.String(command) + ", " + argsJson + ")" +
            ".then(function (v) { window.oriel.postMessage('win.probe', { id: " + id + ", ok: true, v: v }); })" +
            ".catch(function (e) { window.oriel.postMessage('win.probe', { id: " + id + ", ok: false, e: String(e) }); });";

        await main.EvaluateJs(script);

        // 轮询等回执：原生 IPC 是异步的，固定延时要么不够要么白等
        int waited = 0;
        while (waited < 8000)
        {
            lock (Gate)
            {
                if (Replies.TryGetValue(id, out var reply))
                {
                    Replies.Remove(id);

                    if (reply.Ok)
                    {
                        LastError = null;
                        return reply.Value;
                    }

                    LastError = reply.Error;
                    return null;
                }
            }

            await Task.Delay(50);
            waited += 50;
        }

        LastError = $"'{command}' 在 8 秒内没有回执";
        return null;
    }

    private static void OnMessage(OrielMessageReceivedEventArgs args)
    {
        if (args.Name != "win.probe")
        {
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(args.Json);
            JsonElement root = doc.RootElement;

            if (!root.TryGetProperty("id", out JsonElement idElement) || !idElement.TryGetInt32(out int id))
            {
                return;
            }

            bool ok = root.TryGetProperty("ok", out JsonElement okElement) && okElement.ValueKind == JsonValueKind.True;
            string? error = ok ? null : root.TryGetProperty("e", out JsonElement e) ? e.GetString() : null;

            // 值要 Clone：doc 出了 using 就失效，而这个元素会存进字典跨 await 使用
            JsonElement value = ok && root.TryGetProperty("v", out JsonElement v) ? v.Clone() : default;

            lock (Gate)
            {
                Replies[id] = (ok, error, value);
            }
        }
        catch (JsonException ex)
        {
            Check(false, $"页面回执不是合法 JSON：{args.Json}（{ex.Message}）");
        }
    }

    private static void OnTimeout()
        => Check(false, "窗口 API 自检超时：90 秒内没走完");

    /// <summary>轮询等待同步条件成立。</summary>
    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs)
    {
        int waited = 0;
        while (waited < timeoutMs)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(50);
            waited += 50;
        }
    }

    /// <summary>等一个 Task 完成；超时返回 null。</summary>
    private static async Task<T?> WaitForAsync<T>(Func<Task<T>> factory, int timeoutMs)
    {
        Task<T> task = factory();
        Task finished = await Task.WhenAny(task, Task.Delay(timeoutMs));
        return finished == task ? await task : default;
    }

    private static void Check(bool condition, string description)
    {
        lock (Gate)
        {
            if (condition)
            {
                Console.WriteLine($"[window-selftest] OK: {description}");
                return;
            }

            Failures.Add(description);
            Console.WriteLine($"[window-selftest] 失败: {description}");
        }
    }

    private static void Report()
    {
        lock (Gate)
        {
            if (Failures.Count > 0)
            {
                Console.WriteLine($"WINDOW-SELFTEST: FAIL（{Failures.Count} 项）");
            }
            else
            {
                Console.WriteLine("WINDOW-SELFTEST: PASS");
            }
        }

        Console.Out.Flush();
    }
}
