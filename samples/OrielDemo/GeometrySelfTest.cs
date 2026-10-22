using System.Text.Json;
using OrielWeb;

namespace OrielDemo;

/// <summary>
/// 窗口尺寸语义自检（<c>--selftest geometry</c>）。
/// </summary>
/// <remarks>
/// <para>
/// <b>它取证的是一个现存缺陷，不是新功能。</b>库对 <c>WithSize(w, h)</c> 的承诺是
/// "逻辑像素、三平台一致"，但三平台用的原生 API 语义并不相同：
/// </para>
/// <list type="bullet">
///   <item><description>Windows <c>SetWindowPos</c> / <c>CreateWindowExW</c> 设的是**整窗**（含边框与标题栏）；</description></item>
///   <item><description>Linux <c>gtk_window_resize</c> / <c>gtk_window_set_default_size</c> 设的是**客户区**；</description></item>
///   <item><description>macOS <c>setContentSize:</c> / <c>initWithContentRect:</c> 设的是**客户区**。</description></item>
/// </list>
/// <para>
/// 于是同一份 <c>WithSize(800, 600)</c> 在 Windows 的装饰窗口上得到的内容区比另两个平台
/// 小一圈（差的正是边框与标题栏）。<c>samples/OrielDemo</c> 用 <c>WithFrameless()</c>，
/// <c>WS_POPUP</c> 下两者几乎相等，所以自检与真机都没暴露过。
/// </para>
/// <para>
/// <b>为什么用页面读而不是新增查询 API</b>：<c>documentElement.clientWidth</c> 就是客户区
/// （CSS 逻辑像素），<see cref="WebviewWindow.EvaluateJs"/> 今天就能取到。这样这条断言
/// 现在就能跑，不必等 <c>InnerSize</c> 落地——测试先行，让缺陷立刻变成红灯。
/// 等 E2 加上 <c>InnerSize</c> 之后，再加一条"API 读到的值与页面读到的一致"的断言。
/// </para>
/// <para>
/// 结论行：<c>GEOMETRY-SELFTEST: PASS</c> / <c>FAIL（…）</c>。
/// </para>
/// </remarks>
internal static class GeometrySelfTest
{
    /// <summary>测试用的窗口内容区尺寸（逻辑像素）。取非整数倍率友好的值，让偏差明显。</summary>
    private const int RequestedWidth = 800;
    private const int RequestedHeight = 600;

    /// <summary>
    /// 允许的偏差（逻辑像素）。
    /// </summary>
    /// <remarks>
    /// 不取 0：WebKitGTK 与 WKWebView 在某些主题下会因滚动条/边框取整而差 1 像素，
    /// 那不是我们要抓的缺陷。要抓的是"整圈边框与标题栏"，量级在 20–40 像素。
    /// </remarks>
    private const int Tolerance = 2;

    /// <summary>
    /// 当前环境是否有窗口管理器（决定"位置"那几条断言适不适用）。
    /// </summary>
    /// <remarks>
    /// 判据由验证脚本设置（<c>ORIEL_HAS_WM=1</c>，它在 X11 下查 <c>_NET_SUPPORTING_WM_CHECK</c>）；
    /// 未设置时只有非 Linux 才认为有 WM。没有 WM 时 <c>gtk_window_move</c> 是空操作，
    /// 位置断言验的是环境而不是库。
    /// </remarks>
    private static readonly bool HasWindowManager =
        !OperatingSystem.IsLinux()
        || Environment.GetEnvironmentVariable("ORIEL_HAS_WM") == "1";

    private static int _failures;

    /// <summary>本项是否失败——demo 的 Main 据此设置退出码。</summary>
    internal static bool Failed => Volatile.Read(ref _failures) > 0;

    /// <summary>
    /// 本自检要求**装饰窗口**（不能用 <c>WithFrameless</c>）。
    /// </summary>
    /// <remarks>
    /// 无边框窗口下整窗与客户区几乎相等，这个缺陷就测不出来——而那正是它至今没被发现的原因。
    /// 所以 demo 在 <c>--selftest geometry</c> 时**必须不**调 <c>WithFrameless()</c>。
    /// <para>
    /// <b>frameless 的回归由 <c>--selftest geometry-frameless</c> 覆盖</b>：修 framed 的换算时
    /// 极易把 frameless 也套进去，而症状是"无边框窗口比请求的大一圈"——那恰恰是 demo 的默认形态。
    /// 两条分开跑而不是合成一条，是因为它们要的窗口样式互斥（同一进程只能建一种）。
    /// </para>
    /// </remarks>
    internal const int Width = RequestedWidth;

    internal const int Height = RequestedHeight;

    /// <summary>frameless 模式的探针（<c>--selftest geometry-frameless</c>）。</summary>
    internal static void AttachFrameless(OrielApp app, WebviewWindow window)
        => _ = ProbeAndQuitAsync(app, window, expectFrameless: true);

    /// <summary>在窗口 <c>Loaded</c>（UI 线程、页面已就绪）时挂上本自检。</summary>
    /// <remarks>
    /// <b>不把整段逻辑丢进 <c>Task.Run</c></b>：<see cref="WebviewWindow.EvaluateJs"/> 必须在 UI 线程调用
    /// （WebView2 的 ExecuteScriptAsync 与 WebKit 的 evaluateJavaScript 都是线程相关的），
    /// 从线程池调会得到 <c>0x802A000C</c>（"只能从创建它的线程调用"）。自检本身是异步等待，
    /// 不需要额外线程——用 <c>async void</c> 的形态挂在 UI 线程上，靠 await 让出。
    /// </remarks>
    internal static void Attach(OrielApp app, WebviewWindow window)
    {
        // Loaded 在 UI 线程上触发。这里用 fire-and-forget 的 async 方法而不是 Task.Run：
        // 前者在 UI 线程上执行、await 期间让出消息循环，正好满足 EvaluateJs 的线程要求。
        _ = ProbeAndQuitAsync(app, window, expectFrameless: false);
    }

    /// <summary>探针主体。<paramref name="expectFrameless"/> 只影响失败时的提示文案。</summary>
    private static async Task ProbeAndQuitAsync(OrielApp app, WebviewWindow window, bool expectFrameless)
    {
        try
        {
            // macOS 无图形会话时，窗口既不能改尺寸也不能摆位置（没有窗口服务器），
            // 尺寸语义无从验证——整段跳过，其余平台照常严格断言。
            if (OperatingSystem.IsMacOS() && !MacDisplay.HasScreenSession)
            {
                Console.WriteLine(
                    "[geometry-selftest] SKIP: 本进程没有图形会话（macOS 停在登录窗口），"
                    + "窗口尺寸/位置不由客户端决定——几何语义无从验证");
                return;
            }

            // 页面已 Loaded，DOM 可用。读客户区（CSS 逻辑像素）。
            // 仍在 UI 线程上（await 之前的同步段），EvaluateJs 因此可调。
            string raw = await window.EvaluateJs(
                "JSON.stringify({ w: document.documentElement.clientWidth, " +
                "h: document.documentElement.clientHeight, " +
                "dpr: window.devicePixelRatio })");

            (int width, int height, double dpr) = ParseProbe(raw);

            string mode = expectFrameless ? "frameless" : "framed";
            Console.WriteLine(
                $"[geometry-selftest] ({mode}) 请求 WithSize({RequestedWidth}, {RequestedHeight})，" +
                $"页面客户区 {width}x{height}，devicePixelRatio={dpr}");

            Check(
                Math.Abs(width - RequestedWidth) <= Tolerance,
                $"({mode}) 客户区宽度应为 {RequestedWidth}（±{Tolerance}），实际 {width}" +
                DescribeDeviation(width, RequestedWidth, horizontal: true, expectFrameless));

            Check(
                Math.Abs(height - RequestedHeight) <= Tolerance,
                $"({mode}) 客户区高度应为 {RequestedHeight}（±{Tolerance}），实际 {height}" +
                DescribeDeviation(height, RequestedHeight, horizontal: false, expectFrameless));

            // 运行期改尺寸是**另一条代码路径**（WebviewWindow.Resize，不是建窗）。
            // 它同样要"逻辑像素的客户区"语义——只测建窗会漏掉这一半。
            await VerifyRuntimeResizeAsync(window, mode);

            // 几何查询（E2）：四个查询与"设进去的值"必须对得上。
            // 只测"设进去 → 页面看到"会漏掉另一半：读了回来对不对。
            await VerifyQueriesAsync(window, mode);
        }
        catch (Exception ex)
        {
            // 取不到值本身就是失败：静默跳过会让这个自检"永远绿"，比没有更糟
            Check(false, $"读取客户区失败：{ex.Message}");
        }
        finally
        {
            Report();

            // 退出必须在 UI 线程上（Quit 是线程相关的）。await 之后的续体已回到 UI 线程
            // （三平台都装了基于主线程队列的 SynchronizationContext，见 API.md §1），
            // 但仍用 PostToUiThread 兜一层：万一将来 SynchronizationContext 的语义变了，
            // 这里不会变成"自检跑完了但进程不退"。
            window.PostToUiThread(app.Quit);
        }
    }

    /// <summary>运行期 <c>Resize</c> 之后，客户区也应当等于请求值。</summary>
    /// <remarks>
    /// 取一个与建窗值不同的尺寸（且刻意选非整数倍率友好的值），
    /// 这样"换算写反了"不会被"恰好相等"掩盖。
    /// </remarks>
    private static async Task VerifyRuntimeResizeAsync(WebviewWindow window, string mode)
    {
        const int TargetWidth = 640;
        const int TargetHeight = 480;

        window.Resize(TargetWidth, TargetHeight);

        // 尺寸变更要等系统与引擎都跟上：WM_SIZE → WebView2 的 Bounds 更新 → 页面重排。
        // 不用固定 sleep，轮询到稳定为止（最多约 2 秒）。
        int width = 0;
        int height = 0;
        for (int attempt = 0; attempt < 40; attempt++)
        {
            string raw = await window.EvaluateJs(
                "JSON.stringify({ w: document.documentElement.clientWidth, h: document.documentElement.clientHeight })");
            (width, height, _) = ParseProbe(raw);

            if (width == TargetWidth && height == TargetHeight)
            {
                break;
            }

            await Task.Delay(50);
        }

        Console.WriteLine($"[geometry-selftest] ({mode}) Resize({TargetWidth}, {TargetHeight}) 之后客户区 {width}x{height}");

        // Wayland：尺寸由合成器决定，程序化 Resize 可能一点效果都没有（实测 gnome-shell）。
        // 那验的是环境——跳过，但把请求值与实测值都打出来（上面那行已经打了）。
        if (!(width == TargetWidth && height == TargetHeight) && LinuxDisplay.IsWayland)
        {
            Console.WriteLine(
                $"[geometry-selftest] SKIP: ({mode}) 本会话是 Wayland，窗口尺寸由合成器决定" +
                $"（请求 {TargetWidth}x{TargetHeight}，实际 {width}x{height}）——尺寸语义无从验证");
            return;
        }

        Check(
            width == TargetWidth && height == TargetHeight,
            $"({mode}) Resize({TargetWidth}, {TargetHeight}) 之后客户区应为 {TargetWidth}x{TargetHeight}，实际 {width}x{height}" +
            DescribeDeviation(width, TargetWidth, horizontal: true, frameless: mode == "frameless") +
            DescribeDeviation(height, TargetHeight, horizontal: false, frameless: mode == "frameless"));
    }

    /// <summary>
    /// 几何查询（E2）：<c>InnerSize</c> / <c>OuterSize</c> / <c>InnerPosition</c> / <c>OuterPosition</c>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 断言的是**关系**而不是具体数值，因为这些关系的成立不依赖平台与主题，
    /// 而具体数值依赖它们（边框 16、标题栏 39 在别的主题/系统上会变）：
    /// </para>
    /// <list type="bullet">
    ///   <item><description><c>InnerSize</c> 必须等于刚 <c>Resize</c> 进去的值——那是库自己的契约；</description></item>
    ///   <item><description>framed 时 <c>OuterSize</c> ≥ <c>InnerSize</c>（整窗含边框）；
    ///     frameless 时两者**相等**（客户区铺满整窗）；</description></item>
    ///   <item><description><c>OuterPosition</c> 必须等于刚 <c>MoveTo</c> 进去的值；</description></item>
    ///   <item><description><c>InnerPosition</c> 的两个分量都 ≥ <c>OuterPosition</c>（客户区在整窗内）
    ///     ——frameless 下相等。</description></item>
    /// </list>
    /// </remarks>
    private static async Task VerifyQueriesAsync(WebviewWindow window, string mode)
    {
        bool frameless = mode == "frameless";

        // 先把窗口摆到一个尺寸与位置上，好让查询有确定的期望值
        const int ProbeWidth = 640;
        const int ProbeHeight = 480;
        const int ProbeX = 140;
        const int ProbeY = 110;

        window.Resize(ProbeWidth, ProbeHeight);
        window.MoveTo(ProbeX, ProbeY);
        await Task.Delay(300);

        OrielSize inner = window.InnerSize;
        OrielSize outer = window.OuterSize;
        OrielPosition outerPos = window.OuterPosition;
        OrielPosition innerPos = window.InnerPosition;

        // Wayland 下窗口尺寸由**合成器**决定：实测（Ubuntu 22.04 / gnome-shell）
        // gtk_window_resize、gtk_window_set_default_size、子控件 size_request、
        // 甚至连 gdk_window_resize 都不会改变 GTK 的分配尺寸——连建窗时请求的
        // 800x600 都会被换成 852x689。这不是"尺寸语义算错了"，而是这个会话里
        // **没有可测的尺寸语义**，所以整段跳过（X11 上照常严格断言）。
        bool sizeHonored = Math.Abs(inner.Width - ProbeWidth) <= Tolerance
            && Math.Abs(inner.Height - ProbeHeight) <= Tolerance;

        if (!sizeHonored && LinuxDisplay.IsWayland)
        {
            Console.WriteLine(
                $"[geometry-selftest] SKIP: ({mode}) 本会话是 Wayland，窗口尺寸由合成器决定" +
                $"（请求 {ProbeWidth}x{ProbeHeight}，实际 {inner.Width:0.#}x{inner.Height:0.#}）——" +
                "尺寸语义无从验证");
            return;
        }

        Console.WriteLine(
            $"[geometry-selftest] ({mode}) InnerSize={inner.Width:0.#}x{inner.Height:0.#} " +
            $"OuterSize={outer.Width:0.#}x{outer.Height:0.#} " +
            $"OuterPos={outerPos.X:0.#},{outerPos.Y:0.#} InnerPos={innerPos.X:0.#},{innerPos.Y:0.#}");

        // ---- InnerSize：库自己的契约，必须精确 ----
        Check(
            Math.Abs(inner.Width - ProbeWidth) <= Tolerance && Math.Abs(inner.Height - ProbeHeight) <= Tolerance,
            $"({mode}) InnerSize 应等于刚 Resize 进去的 {ProbeWidth}x{ProbeHeight}（实际 {inner.Width:0.#}x{inner.Height:0.#}）");

        // ---- InnerSize 是 Resize 的逆运算：读出来写回去应当不变 ----
        window.Resize((int)Math.Round(inner.Width), (int)Math.Round(inner.Height));
        await Task.Delay(200);
        OrielSize afterRoundTrip = window.InnerSize;
        Check(
            Math.Abs(afterRoundTrip.Width - inner.Width) <= Tolerance
                && Math.Abs(afterRoundTrip.Height - inner.Height) <= Tolerance,
            $"({mode}) Resize(InnerSize) 应当是不动点（{inner.Width:0.#}x{inner.Height:0.#} → " +
            $"{afterRoundTrip.Width:0.#}x{afterRoundTrip.Height:0.#}）");

        // ---- OuterSize 与 InnerSize 的关系 ----
        if (frameless)
        {
            // 无边框窗口的客户区铺满整窗，两者必须相等——不等说明 WM_NCCALCSIZE 那条路变了
            Check(
                Math.Abs(outer.Width - inner.Width) <= Tolerance && Math.Abs(outer.Height - inner.Height) <= Tolerance,
                $"({mode}) frameless 下 OuterSize 应等于 InnerSize（实际 {outer.Width:0.#}x{outer.Height:0.#} " +
                $"vs {inner.Width:0.#}x{inner.Height:0.#}）");
        }
        else
        {
            Check(
                outer.Width >= inner.Width - Tolerance && outer.Height >= inner.Height - Tolerance,
                $"({mode}) framed 下 OuterSize 应 ≥ InnerSize（整窗含边框；实际 {outer.Width:0.#}x{outer.Height:0.#} " +
                $"vs {inner.Width:0.#}x{inner.Height:0.#}）");

            // 至少有一边应当**严格**大于——两边都相等说明装饰尺寸没读到（Wayland 上的正常现象，
            // 但 Windows/macOS 上就是实现有问题）。仅对非 Linux 断言。
            if (!OperatingSystem.IsLinux())
            {
                Check(
                    outer.Width > inner.Width + 1 || outer.Height > inner.Height + 1,
                    $"({mode}) Windows/macOS 的 framed 窗口应当读到装饰开销" +
                    $"（OuterSize {outer.Width:0.#}x{outer.Height:0.#} 与 InnerSize {inner.Width:0.#}x{inner.Height:0.#} 一样大，" +
                    "像是没读到装饰）");
            }
        }

        // ---- OuterPosition 应当等于刚 MoveTo 进去的值 ----
        // 容差放宽到 40：窗口管理器会吸附/约束（Wayland 下甚至不给位置）。
        // 位置那部分的严格断言在 --selftest windowevents 里已有一份，这里只防"读到别的窗口"。
        //
        // **无窗口管理器时跳过**（CI 的 xvfb）：`gtk_window_move` 是空操作，
        // 位置会停在合成器/默认给的地方（实测 26,23 或 0,0）。那验的是环境，
        // 而这条自检真正要吃的是**尺寸语义**，那部分照常断言。
        if (HasWindowManager)
        {
            Check(
                Math.Abs(outerPos.X - ProbeX) <= 40 && Math.Abs(outerPos.Y - ProbeY) <= 40,
                $"({mode}) OuterPosition 应接近刚 MoveTo 的 ({ProbeX},{ProbeY})（实际 {outerPos.X:0.#},{outerPos.Y:0.#}）");
        }
        else
        {
            Console.WriteLine(
                $"[geometry-selftest] SKIP: ({mode}) OuterPosition 应接近刚 MoveTo 的 ({ProbeX},{ProbeY})" +
                "（当前环境没有窗口管理器，窗口位置不由本进程决定）");
        }

        // ---- InnerPosition 在整窗之内 ----
        Check(
            innerPos.X >= outerPos.X - Tolerance && innerPos.Y >= outerPos.Y - Tolerance,
            $"({mode}) InnerPosition 不应在 OuterPosition 之外" +
            $"（inner {innerPos.X:0.#},{innerPos.Y:0.#} vs outer {outerPos.X:0.#},{outerPos.Y:0.#}）");

        if (frameless)
        {
            Check(
                Math.Abs(innerPos.X - outerPos.X) <= Tolerance && Math.Abs(innerPos.Y - outerPos.Y) <= Tolerance,
                $"({mode}) frameless 下 InnerPosition 应等于 OuterPosition" +
                $"（实际 {innerPos.X:0.#},{innerPos.Y:0.#} vs {outerPos.X:0.#},{outerPos.Y:0.#}）");
        }
    }

    /// <summary>
    /// 把偏差翻译成"大概是哪一侧的语义错了"。
    /// </summary>
    /// <remarks>
    /// 只说"实际 784"会让人去翻窗口代码；指出"小了 16，像是按整窗设的"能直接定位。
    /// Windows 的边框 + 标题栏在 100% 缩放下典型值是 16x39（左右各 8、标题栏 31 + 下边框 8）。
    /// </remarks>
    private static string DescribeDeviation(int actual, int requested, bool horizontal, bool frameless)
    {
        int delta = actual - requested;
        if (delta == 0)
        {
            return string.Empty;
        }

        // 两种窗口形态的"嫌疑方向"相反，提示也要相反——否则排障的人会往反方向查。
        string hint = frameless
            ? "无边框窗口的客户区应等于整窗；偏差说明**多算了一次边框**（把 framed 的换算套到 frameless 上了）"
            : delta < 0
                ? "客户区偏小——像是按**整窗**尺寸设置的（Windows 的 SetWindowPos 语义）"
                : "客户区偏大——像是重复折算了一次缩放比或边框";

        // 给出量级参照，便于对照是边框还是标题栏
        string typical = horizontal
            ? "（100% 缩放下左右边框合计通常 ≈16px）"
            : "（100% 缩放下标题栏 + 上下边框通常 ≈39px）";

        return $"；偏差 {delta:+#;-#;0}px，{hint}{typical}";
    }

    /// <summary>解析页面回传的 JSON（WebView2 风格：结果是 JSON 编码的字符串）。</summary>
    private static (int Width, int Height, double Dpr) ParseProbe(string raw)
    {
        // EvaluateJs 在 Windows 上返回的是"JSON 编码的结果串"：页面返回 string 时，
        // 外层再编码一次（形如 "\"{...}\""）。三平台统一按"可能有两层"处理。
        string json = raw;
        using (JsonDocument outer = JsonDocument.Parse(raw))
        {
            if (outer.RootElement.ValueKind == JsonValueKind.String)
            {
                json = outer.RootElement.GetString() ?? "null";
            }
        }

        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException($"页面探针返回值不是对象：{json}");
        }

        return (
            root.GetProperty("w").GetInt32(),
            root.GetProperty("h").GetInt32(),
            root.TryGetProperty("dpr", out JsonElement dpr) ? dpr.GetDouble() : 1);
    }

    private static void Check(bool condition, string description)
    {
        if (condition)
        {
            Console.WriteLine($"[geometry-selftest] OK: {description}");
            return;
        }

        Interlocked.Increment(ref _failures);
        Console.WriteLine($"[geometry-selftest] 失败: {description}");
    }

    private static void Report()
    {
        if (Failed)
        {
            Console.WriteLine(
                $"GEOMETRY-SELFTEST: FAIL（{Volatile.Read(ref _failures)} 项）——" +
                "三平台的尺寸语义不一致，见 docs/TAURI2-PLAN.md 的 E-1");
        }
        else
        {
            Console.WriteLine("GEOMETRY-SELFTEST: PASS");
        }

        Console.Out.Flush();
    }
}
