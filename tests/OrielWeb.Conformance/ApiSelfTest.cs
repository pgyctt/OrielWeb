using System.Text.Json;
using OrielWeb;

namespace OrielConformance;

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

            string probeScript = BuildProbeScript();

            // 诊断开关：设 ORIEL_PROBE_DUMP=1 时把生成的脚本落盘。
            //
            // 为什么值得留一个开关：**探针脚本解析失败时一条输出都没有**，
            // 表现是"自检超时"而不是"某条断言失败"——而超时这个现象完全指不到
            // "有个重复的 const"这种根因。落盘之后一条 `node --check` 就能定位。
            // （这条经验不是假设：调试这一批时就撞上了重复的 `let`。）
            if (Environment.GetEnvironmentVariable("ORIEL_PROBE_DUMP") is { Length: > 0 })
            {
                string dumpPath = Path.Combine(Path.GetTempPath(), "oriel-api-probe.js");
                File.WriteAllText(dumpPath, probeScript);
                Console.WriteLine($"[api-selftest] 探针脚本已落盘：{dumpPath}");
            }

            await main.EvaluateJs(probeScript);

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

            // 宿主平台：**所有平台相关的期望值都从这里取**，不写死某一平台。
            // 自检在三个平台的 CI 上都跑（smoke-windows / smoke-linux / smoke-macos），
            // 任何一处写死 Windows 的断言都会让另两个平台必然红——而那不是库的缺陷。
            // 用 `window.oriel.platform`（宿主注入）而不是 navigator：与库内部的平台判断同源。
            const hostPlatform = (window.oriel && window.oriel.platform) || '';
            const isWindowsHost = hostPlatform === 'windows';
            const isMacOsHost = hostPlatform === 'macos';

            // GDK 后端是不是 Wayland（宿主侧探的，见 LinuxDisplay）。Wayland 下有几件事
            // 由合成器/焦点状态决定（剪贴板读写要键盘焦点），无人自检的窗口没有焦点——
            // 那些断言验的是环境而不是库，按"条件成立才断言"处理，其余平台恒 false。
            const isWaylandSession = {{(LinuxDisplay.IsWayland ? "true" : "false")}};

            // macOS 没有图形会话时（机器停在登录窗口、走 SSH 调用）NSPasteboard 不可用：
            // 写入返回 false、读取返回 null。那验的是环境而不是库。
            const hasNoScreenSession = {{(OperatingSystem.IsMacOS() && !MacDisplay.HasScreenSession ? "true" : "false")}};
            const clipboardUsable = !isWaylandSession && !hasNoScreenSession;

            // path 模块在**前面几段**就要用（fs 段的逃逸路径要按平台拼分隔符），
            // 所以提前导入；后面 path 那一节直接复用这个绑定。
            // 放到段内声明会让 fs 段踩到 TDZ（`Cannot access 'path' before initialization`），
            // 而那看起来像"fs 命令没按预期拒绝"——排查方向会完全错。
            const path = await import(vendor + '/path.js');

            try {
                // ---- core ----
                const core = await import(vendor + '/core.js');
                check('core 导出三个函数',
                    typeof core.invoke === 'function' && typeof core.isTauri === 'function'
                        && typeof core.convertFileSrc === 'function',
                    Object.keys(core).join(','));

                check('core.isTauri() 在宿主里为 true', core.isTauri() === true, core.isTauri());

                // 真的发一条命令并拿到返回值——这是"包能驱动宿主"的最小证据。
                // 用自检自己的 conf.list，而不是示例应用的业务命令：契约测试不该因为
                // "Todo 示例改了"而红，那种失败的报错信息指向的是 Todo，不是契约。
                const listed = await core.invoke('conf.list');
                check('core.invoke 能往返', Array.isArray(listed), Array.isArray(listed) ? listed.length : typeof listed);

                // convertFileSrc 尚未实现（P1-2）：必须**明确抛错**，不能返回一个看似可用的 URL
                // convertFileSrc 现在**能用**了（F2），所以这里验契约而不是"抛错"。
                // 早先它显式抛错（那时还没有 asset 协议），断言写的是"必须抛"——
                // 实现变了之后那条断言就成了"验证缺陷存在"，必须一起改。
                let convertUrl = '';
                try {
                    convertUrl = core.convertFileSrc('/tmp/a.png');
                } catch (_) { /* 下面断言 */ }
                check('core.convertFileSrc 返回 oriel://file.oriel/ 形态的 URL',
                    convertUrl.startsWith('oriel://file.oriel/'), convertUrl);

                let emptyThrew = false;
                try { core.convertFileSrc(''); } catch (_) { emptyThrew = true; }
                check('core.convertFileSrc 对空路径抛错', emptyThrew, 'ok');

                let protocolThrew = false;
                try { core.convertFileSrc('/tmp/a.png', 'myproto'); } catch (_) { protocolThrew = true; }
                check('core.convertFileSrc 对自定义协议名明确抛错（不静默忽略）', protocolThrew, 'ok');

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
                check('app.* 元信息无需能力声明即可用', true, '见 ConformanceRun.AllowCapabilities');

                // ---- fs（F4b-2b）：两道关（命令授权 + 路径 scope）----
                const fs = await import(vendor + '/fs.js');

                // 能力声明把 fs 的 scope 钉在临时目录下（见 ConformanceRun.AllowApiCommands），
                // 所以这里能真读写；两边用的是同一个 ConformanceCommands.FsRootPath。
                const fsRoot = await core.invoke('conf.fsRoot');

                // **先把上次运行留下的清掉**：自检必须幂等，否则第二次跑会因为
                // "目录已存在"（非递归 mkdir 会报错）或"文件已存在"而失败。
                // 这种"第一次跑过、第二次不过"的失败最难查——它看起来像随机失败。
                await fs.remove(fsRoot, { recursive: true });

                const fsFile = fsRoot + '/api-selftest.txt';

                await fs.writeTextFile(fsFile, '你好，世界');
                check('fs.writeTextFile + readTextFile 往返',
                    (await fs.readTextFile(fsFile)) === '你好，世界', await fs.readTextFile(fsFile));

                check('fs.exists 对存在的文件为 true', (await fs.exists(fsFile)) === true, 'ok');
                check('fs.size 给出字节数（UTF-8 下 5 个汉字 = 15 字节）',
                    (await fs.size(fsFile)) === 15, await fs.size(fsFile));

                // 二进制：真通道上验 Uint8Array 的转换（JSON 不认它，必须转数组）
                const binFile = fsRoot + '/api-selftest.bin';
                await fs.writeFile(binFile, new Uint8Array([0, 1, 127, 128, 255]));
                const readBack = await fs.readFile(binFile);
                check('fs.writeFile + readFile 二进制往返（含 0 与 255 两个边界）',
                    readBack instanceof Uint8Array
                        && readBack.length === 5
                        && readBack[0] === 0
                        && readBack[4] === 255,
                    String(readBack));

                const stat = await fs.stat(fsFile);
                check('fs.stat 给 camelCase 字段与毫秒时间戳',
                    stat.isFile === true && typeof stat.mtime === 'number' && stat.mtime > 0,
                    JSON.stringify(stat));

                const dir = fsRoot + '/api-selftest-dir';
                await fs.mkdir(dir);
                check('fs.mkdir 建出目录', (await fs.exists(dir)) === true, dir);
                const entries = await fs.readDir(fsRoot);
                check('fs.readDir 列出的项是 camelCase 形状',
                    Array.isArray(entries) && entries.some(function (e) { return e.name === 'api-selftest.txt'; }),
                    JSON.stringify(entries));

                await fs.rename(binFile, fsRoot + '/api-selftest-renamed.bin');
                check('fs.rename 生效',
                    (await fs.exists(fsRoot + '/api-selftest-renamed.bin')) === true
                        && (await fs.exists(binFile)) === false, 'ok');

                await fs.copyFile(fsFile, fsRoot + '/api-selftest-copy.txt');
                check('fs.copyFile 生效', (await fs.exists(fsRoot + '/api-selftest-copy.txt')) === true, 'ok');

                await fs.truncate(fsFile, 3);
                check('fs.truncate 截断到指定长度', (await fs.size(fsFile)) === 3, await fs.size(fsFile));

                await fs.remove(fsRoot + '/api-selftest-copy.txt');
                check('fs.remove 删掉文件', (await fs.exists(fsRoot + '/api-selftest-copy.txt')) === false, 'ok');

                // **scope 逃逸必须被挡**：demo 只声明了临时目录，所以这里必然被拒。
                // 用平台相关的绝对路径（Windows 盘符 / POSIX 根），否则在 Linux 上
                // `C:\Windows\win.ini` 只是个"不存在的相对文件名"，虽然同样会被拒，
                // 但那时验的已经不是"绝对路径越界"这件事了。
                let fsEscapeCode = 'unset';
                try {
                    await fs.readTextFile(isWindowsHost ? 'C:\\Windows\\win.ini' : '/etc/hostname');
                } catch (e) {
                    fsEscapeCode = e && e.code;
                }
                check('fs 读 scope 之外的绝对路径 → path_not_allowed',
                    fsEscapeCode === 'path_not_allowed', fsEscapeCode);

                let dotdotCode = 'unset';
                let dotdotDetail = '';
                try {
                    // **用 path.sep() 拼，不能写死反斜杠**：Linux/macOS 上 `\` 是普通文件名字符，
                    // 拼出来的路径**仍在 scope 内**（只是文件不存在），于是断言会退化
                    // 成"读一个不存在的文件"（错误码不是 path_not_allowed）而在 CI 上必红。
                    const dotdot = ['..', '..', 'escape.txt'].reduce(
                        (left, right) => left + path.sep() + right, fsRoot);
                    dotdotDetail = dotdot;
                    await fs.readTextFile(dotdot);
                } catch (e) {
                    dotdotCode = e && e.code;
                    // 把 message 也带上：只报"码不对"看不出到底是 scope 放行、还是路径不合法
                    dotdotDetail = String(e && e.code) + ' / ' + String(e && e.message);
                }
                check('fs 用 .. 逃逸出 scope → path_not_allowed（归一再判定）',
                    dotdotCode === 'path_not_allowed', dotdotDetail);

                // ---- 平台集成（F5）：opener / notification / autostart ----
                //
                // 自检**只验只读那几条与参数校验**：真去 openUrl 会弹浏览器、
                // 真去 autostart.enable 会往系统里写一个自启项——自检不该改机器的状态。
                const opener = await import(vendor + '/opener.js');
                const notification = await import(vendor + '/notification.js');
                const autostart = await import(vendor + '/autostart.js');

                let openWithCode = 'unset';
                try {
                    await opener.openUrl('https://example.com', 'firefox');
                } catch (e) {
                    openWithCode = e && e.code;
                }
                check('opener.openUrl 对 openWith 明确拒绝（not_supported）',
                    openWithCode === 'not_supported', openWithCode);

                let badUrlCode = 'unset';
                try {
                    await opener.openUrl('example.com');
                } catch (e) {
                    badUrlCode = e && e.code;
                }
                check('opener.openUrl 拒绝非绝对 URL（invalid_argument）',
                    badUrlCode === 'invalid_argument', badUrlCode);

                let revealThrew = false;
                try {
                    opener.revealItemInDir('');
                } catch (_) {
                    revealThrew = true;
                }
                check('opener.revealItemInDir 对空字符串在页面侧就抛错', revealThrew, 'ok');

                check('notification.isPermissionGranted 返回布尔',
                    typeof (await notification.isPermissionGranted()) === 'boolean', 'ok');

                const permission = await notification.requestPermission();
                check('notification.requestPermission 返回 granted/denied 字符串',
                    permission === 'granted' || permission === 'denied', permission);

                check('autostart.isEnabled 返回布尔（只读，不改系统状态）',
                    typeof (await autostart.isEnabled()) === 'boolean', 'ok');

                // 三族的命令都**不是**始终放行。这条能跑通，说明自检的能力名单里
                // 确实显式声明了它们——断言钉的是那份声明的存在。
                check('平台集成命令需要显式声明', true, '见 ConformanceRun.AllowApiCommands');

                // ---- clipboard（F6）：真读写一次 ----
                //
                // 与 opener / autostart 不同，剪贴板**可以**在自检里真读写：
                // 它不改系统状态、不弹窗口，而"写进去再读回来"是唯一能证明三个平台的
                // 原生实现真的接对了的方式（单测里没有后端，测的只是"没后端时不抛"）。
                //
                // 代价是会把用户当前的剪贴板内容覆盖掉。这一点在 --selftest 的说明里写明——
                // 一个"改了用户剪贴板"的无人自检必须让人事先知道。
                const clipboard = await import(vendor + '/clipboard.js');

                const marker = 'oriel-selftest-' + Date.now();
                check('clipboard.writeText 报告成功',
                    clipboardUsable ? (await clipboard.writeText(marker)) === true : true,
                    clipboardUsable ? marker : '无图形会话/无键盘焦点，跳过内容判定');

                const clipboardBack = await clipboard.readText();
                // Wayland：剪贴板读写要**键盘焦点**，而无人自检的窗口没有焦点——
                // 用最小 GTK 程序实测过（gtk_clipboard_set_text 之后 wait_for_text 返回 NULL，
                // 同一段代码在 X11 下正常）。这里不断言内容，但把实测值打出来便于排查。
                check('clipboard.readText 读回刚写进去的内容（三平台原生实现接对了）',
                    !clipboardUsable || clipboardBack === marker,
                    clipboardUsable ? String(clipboardBack)
                                    : `跳过（读到 ${String(clipboardBack)}）`);

                let clipLabelCode = 'unset';
                try {
                    await clipboard.writeText('x', { label: 'PRIMARY' });
                } catch (e) {
                    clipLabelCode = e && e.code;
                }
                check('clipboard.writeText 对 label 明确拒绝（not_supported）',
                    clipLabelCode === 'not_supported', clipLabelCode);

                check('clipboard.clear 报告成功',
                    clipboardUsable ? (await clipboard.clear()) === true : true,
                    clipboardUsable ? 'ok' : '无图形会话/无键盘焦点，跳过');

                const afterClear = await clipboard.readText();
                check('清空之后读不到文本（null 或空串）',
                    afterClear === null || afterClear === '', String(afterClear));

                // ---- dialog（F3）：**只验参数校验，不真弹** ----
                //
                // 模态对话框会把自检挂住（它在等一个不会有人点的窗口），所以这里只验
                // 那些"在弹之前就该被拒"的路径。真弹的验证靠人工（demo 的按钮）——
                // 无人自检里弹模态框本来就是自相矛盾的要求。
                const dialog = await import(vendor + '/dialog.js');

                let unsupportedCode = 'unset';
                try {
                    await dialog.open({ canCreateDirectories: false });
                } catch (e) {
                    unsupportedCode = e && e.code;
                }
                check('dialog.open 对未实现的选项明确拒绝（not_supported）',
                    unsupportedCode === 'not_supported', unsupportedCode);

                let comboCode = 'unset';
                try {
                    await dialog.open({ directory: true, multiple: true });
                } catch (e) {
                    comboCode = e && e.code;
                }
                check('dialog.open 拒绝 directory + multiple 组合（invalid_argument）',
                    comboCode === 'invalid_argument', comboCode);

                let dialogMissingCode = 'unset';
                try {
                    await core.invoke('dialog.message', { title: '没有正文' });
                } catch (e) {
                    dialogMissingCode = e && e.code;
                }
                check('dialog.message 缺 message 参数报 missing_argument',
                    dialogMissingCode === 'missing_argument', dialogMissingCode);

                check('dialog 模块导出 open / save / message / ask / confirm',
                    ['open', 'save', 'message', 'ask', 'confirm'].every(function (k) { return typeof dialog[k] === 'function'; }),
                    'ok');

                //
                // 这一条必须在**真页面**里验：`convertFileSrc` 是同步拼字符串，
                // 真正的强制点在 webview 去请求那个 URL 时。而"请求能不能成功"
                // 只有真发起一次请求才知道——尤其要确认 Chromium 没有把路径里的
                // %2F 归一掉（那会让整段编码的方案失效）。
                const imgFile = fsRoot + '/api-selftest.png';
                // 一个最小的合法 PNG（1x1 透明）
                const pngBytes = [
                    137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, 73, 72, 68, 82,
                    0, 0, 0, 1, 0, 0, 0, 1, 8, 6, 0, 0, 0, 31, 21, 196, 137,
                    0, 0, 0, 10, 73, 68, 65, 84, 120, 156, 99, 0, 1, 0, 0, 5, 0, 1,
                    13, 10, 45, 180, 0, 0, 0, 0, 73, 69, 78, 68, 174, 66, 96, 130,
                ];
                await fs.writeFile(imgFile, new Uint8Array(pngBytes));

                const imgUrl = core.convertFileSrc(imgFile);
                check('convertFileSrc 给出 oriel://file.oriel/ 形态的 URL',
                    imgUrl.startsWith('oriel://file.oriel/'), imgUrl);

                // 真请求一次：这是"整段编码没被引擎归一掉"的唯一取证方式。
                //
                // **asset 协议目前只在 Windows 上真的提供内容**：Linux / macOS 的 scheme
                // 处理器还没接授权器，`Serve` 一律 403（fail-closed，见 F2 的说明与
                // CHANGELOG 的"已知限制"）。所以"能加载出来"这条只在 Windows 上成立；
                // 另两个平台改为断言"**确实被拒**"——那也证明了 URL 形态是通的。
                const imgOk = await new Promise(function (resolve) {
                    const probe = new Image();
                    probe.onload = function () { resolve(probe.naturalWidth === 1); };
                    probe.onerror = function () { resolve(false); };
                    probe.src = imgUrl;
                });
                if (isWindowsHost) {
                    check('页面能真的加载 convertFileSrc 出来的 URL（%2F 没被引擎归一）', imgOk, imgUrl);
                } else {
                    check('非 Windows 上 asset 协议 fail-closed：convertFileSrc 的 URL 被拒（403）',
                        imgOk === false, imgUrl);
                }

                // **scope 之外的必须加载失败**：这是 convertFileSrc 的关键安全性质——
                // 否则它就是一个绕过 fs scope 的读取通道
                const outsideUrl = core.convertFileSrc('C:\\Windows\\win.ini');
                const outsideOk = await new Promise(function (resolve) {
                    const probe = new Image();
                    probe.onload = function () { resolve(true); };
                    probe.onerror = function () { resolve(false); };
                    probe.src = outsideUrl;
                });
                check('scope 之外的文件 URL 加载失败（403）', outsideOk === false, outsideUrl);

                // 目录也不能通过这条通道列出来
                const dirUrl = core.convertFileSrc(fsRoot);
                const dirOk = await new Promise(function (resolve) {
                    const probe = new Image();
                    probe.onload = function () { resolve(true); };
                    probe.onerror = function () { resolve(false); };
                    probe.src = dirUrl;
                });
                check('目录不能通过文件通道取到', dirOk === false, dirUrl);

                // ---- path（F1）：同步常量 + 路径运算 ----
                // `path` 已在脚本开头导入（fs 段的逃逸路径也要用它的 sep）。

                // sep/delimiter 是**同步**的（读宿主注入的常量），所以这里刻意不 await。
                //
                // **期望值必须按宿主平台取**，不能写死反斜杠/分号：自检在三个平台的 CI 上都跑，
                // 写死 Windows 的值会让 Linux/macOS 的 smoke **必然**红，而那不是库的缺陷。
                check('path.sep() 与宿主平台一致',
                    path.sep() === (isWindowsHost ? '\\' : '/'),
                    `${hostPlatform} → ${JSON.stringify(path.sep())}`);
                check('path.delimiter() 与宿主平台一致',
                    path.delimiter() === (isWindowsHost ? ';' : ':'),
                    `${hostPlatform} → ${JSON.stringify(path.delimiter())}`);

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

                // isAbsolute 的判据按平台取：Windows 用 `C:\x`（盘符），POSIX 用 `/x`。
                // 写死 `C:` 拼平台分隔符在 Linux 上会得到 `C:/x`——那**也**是绝对路径，
                // 所以这条在 Windows 上永远看不出写错了，只在别处露馅。
                const absoluteInput = isWindowsHost ? 'C:\\x' : '/x';
                check('path.isAbsolute 判平台相关的绝对性',
                    (await path.isAbsolute(absoluteInput)) === true
                        && (await path.isAbsolute('a/b')) === false,
                    `${absoluteInput} / a/b`);

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

                // path.* 整族放行：能力名单里**没有**任何 path.* 条目，
                // 所以这几条能跑通本身就是"整族放行生效"的证据。
                check('path.* 无需能力声明即可用（整族放行）', true, '整族放行');

                // ---- 目录解析（F4b-1）：真宿主上逐格取值 ----
                const home = await path.homeDir();
                check('path.homeDir() 给绝对路径', home.startsWith('C:\\') || home.startsWith('/'), home);

                const appData = await path.appDataDir();
                check('path.appDataDir() 落在 dataDir 之下且含应用标识符',
                    appData.endsWith(appId) && appData.startsWith(await path.dataDir()),
                    appData);

                // 四格目录的平台差异（**按平台给期望值**，不能写死 Windows 那一套）：
                //   Windows：Data=LocalData=Config=Roaming、Cache=Local
                //   Linux：  Data=LocalData=~/.local/share、Cache=~/.cache、Config=~/.config
                //   macOS：  Data=LocalData=Config=~/Library/Application Support、Cache=~/Library/Caches
                //
                // 于是两条关系的真值表是：
                //   config == data      → Windows ✅、macOS ✅、Linux ❌
                //   localData == cache  → Windows ✅、macOS ❌、Linux ❌
                // **macOS 的 config 与 data 同为 Application Support**（`dirs` 的 macOS 映射就
                // 是这样，Tauri 与本库一致）——早先这里把 macOS 与 Linux 归成一类，断言必红。
                const config = await path.configDir();
                const data = await path.dataDir();
                const localData = await path.localDataDir();
                const cache = await path.cacheDir();
                check('path.configDir() 与 path.dataDir() 的关系与平台一致',
                    (isWindowsHost || isMacOsHost) ? config === data : config !== data,
                    `${config} vs ${data}（${hostPlatform}）`);
                check('path.localDataDir() 与 path.cacheDir() 的关系与平台一致',
                    isWindowsHost ? localData === cache : localData !== cache,
                    `${localData} vs ${cache}（${hostPlatform}）`);
                // Windows 上 Roaming 与 Local 必须是不同目录（混成一条实现会让一对双双错掉）；
                // POSIX 上没有 Roaming/Local 之分，这一条不适用。
                check('Windows 上 Roaming 与 Local 是不同目录',
                    isWindowsHost ? data !== localData : true,
                    `${data} vs ${localData}（${hostPlatform}）`);

                // 下载目录**不能**靠 .NET 的 SpecialFolder 得到（没有那个成员），
                // 而 publicDir 也不是 CommonDocuments（那个多一层 \Documents）。
                const downloads = await path.downloadDir();
                const publicDir = await path.publicDir();

                // **不能断言字面量 "Downloads"**：Linux 上这个目录名来自 XDG 用户目录，
                // 会随系统语言本地化——中文 Ubuntu 22.04 上是 `/home/<user>/下载`（真机实测）。
                // macOS / Windows 同样如此（Windows 上 Downloads 也是可重定向的 Known Folder）。
                // 这条断言真正要保证的是"给了一个**具体的**目录，而不是把家目录原样返回"，
                // 所以判据换成：绝对路径 + 不是家目录本身 + 与 publicDir 不同。
                check('path.downloadDir() 是绝对路径且不是家目录本身',
                    (downloads.startsWith('/') || /^[A-Za-z]:[\\/]/.test(downloads))
                    && downloads !== home,
                    `${downloads}（家目录 ${home}）`);
                // 同理不断言"Public"这个字面量（本地化/重定向都会变），
                // 只要它不落到 ...\\Documents 那一层（.NET 的 CommonDocuments 就是那么错的）。
                check('path.publicDir() 不是 .../Documents 那一层',
                    !/[\\/]Documents$/.test(publicDir), publicDir);

                // 平台不支持的那几格要以 not_supported 明确拒绝（与 Tauri 一致），
                // 而不是给一个编出来的值。
                // **fontDir 只在 Windows 上"不支持"**：Linux 给 ~/.local/share/fonts、
                // macOS 给 ~/Library/Fonts，所以另两个平台上这条应当**成功**。
                let fontCode = 'unset';
                let fontValue = null;
                try {
                    fontValue = await path.fontDir();
                } catch (e) {
                    fontCode = e && e.code;
                }
                check(isWindowsHost
                        ? 'path.fontDir() 在 Windows 上以 not_supported 被拒（与 Tauri 一致）'
                        : 'path.fontDir() 在非 Windows 上给得出路径（Tauri 同样支持）',
                    isWindowsHost
                        ? fontCode === 'not_supported'
                        : typeof fontValue === 'string' && fontValue.length > 0,
                    isWindowsHost ? fontCode : String(fontValue));

                // resolveResource 的越界校验
                const resource = await path.resolveResource('assets/logo.svg');
                check('path.resolveResource() 落在资源目录之下',
                    resource.startsWith(await path.resourceDir()), resource);

                let escapeCode = 'unset';
                try {
                    await path.resolveResource('../outside.txt');
                } catch (e) {
                    escapeCode = e && e.code;
                }
                check('path.resolveResource() 拒绝跑出资源目录', escapeCode === 'invalid_argument', escapeCode);

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
                // 于是它也不泄露"这个命令存不存在"。而 `conf.nonexistent` 命中了
                // Allow("conf.*")，能走到路由查找，才会给出 unknown_command。
                let unknownCode = 'unset';
                try {
                    await core.invoke('conf.nonexistent');
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
                    await core.invoke('conf.list', null, { timeout: 5000 });
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

                // getFocusedWindow：**已实现**（遍历存活窗口取第一个 IsFocused 为真的）。
                // 断言"形状与自洽性"，不断言"焦点一定在谁身上"——那取决于桌面把前台给了谁，
                // 不归本库控制（无窗口管理器的 CI 环境里尤其不可靠）。
                const focused = await win.getFocusedWindow();
                const allLabels = all.map(function (w) { return w.label; });
                check('window.getFocusedWindow() 要么给 null、要么给一个已知 label',
                    focused === null || (focused && allLabels.indexOf(focused.label) >= 0),
                    focused === null ? 'null' : focused.label);
                // 与 isFocused 自洽：返回的窗口自己必须报告有焦点；返回 null 时不该有窗口声称有焦点
                check('window.getFocusedWindow() 与 isFocused() 自洽',
                    focused === null
                        ? (await Promise.all(all.map(function (w) { return w.isFocused(); })))
                            .every(function (v) { return v === false; })
                        : (await focused.isFocused()) === true,
                    focused === null ? 'null' : focused.label);

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
                // 超时的**两种成因**要分开说，否则这个现象完全指不到根因：
                //
                //   * 脚本**解析失败**（比如一个重复的 const）——那时一条输出都不会有，
                //     而"超时"与"有个重复的变量名"看不出任何关系。
                //     这个是**踩过两次**的：探针是一个扁平作用域，所有 const 都在同一层。
                //   * 脚本跑起来了但某条命令没回执——那时前面会有若干 OK 行。
                //
                // 提示里给出定位手段（ORIEL_PROBE_DUMP + node --check），而不是只说"超时了"。
                Failures.Add(
                    $"页面探针在 {elapsedSeconds:F0} 秒内没有回显。" +
                    "若此前**一条 OK 都没有**，多半是脚本解析失败（例如重复的 const——" +
                    "探针是扁平作用域）；设 ORIEL_PROBE_DUMP=1 落盘后用 node --check 定位。");
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
