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

                // ---- fs（F4b-2b）：两道关（命令授权 + 路径 scope）----
                const fs = await import(vendor + '/fs.js');

                // demo 把 fs 的 scope 声明在临时目录下（见 Program.cs），所以这里能真读写
                const fsRoot = await core.invoke('demo.fsRoot');

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

                // **scope 逃逸必须被挡**：demo 只声明了临时目录，所以这里必然被拒
                let fsEscapeCode = 'unset';
                try {
                    await fs.readTextFile('C:\\Windows\\win.ini');
                } catch (e) {
                    fsEscapeCode = e && e.code;
                }
                check('fs 读 scope 之外的绝对路径 → path_not_allowed',
                    fsEscapeCode === 'path_not_allowed', fsEscapeCode);

                let dotdotCode = 'unset';
                try {
                    await fs.readTextFile(fsRoot + '\\..\\..\\escape.txt');
                } catch (e) {
                    dotdotCode = e && e.code;
                }
                check('fs 用 .. 逃逸出 scope → path_not_allowed（归一再判定）',
                    dotdotCode === 'path_not_allowed', dotdotCode);

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

                // ---- 目录解析（F4b-1）：真宿主上逐格取值 ----
                const home = await path.homeDir();
                check('path.homeDir() 给绝对路径', home.startsWith('C:\\') || home.startsWith('/'), home);

                const appData = await path.appDataDir();
                check('path.appDataDir() 落在 dataDir 之下且含应用标识符',
                    appData.endsWith(appId) && appData.startsWith(await path.dataDir()),
                    appData);

                // Windows 上这四格分成两对（Roaming / Local）——把 Data 与 LocalData
                // 混成一条实现会让其中一对双双错掉，而那种错在 Linux/macOS 上看不出来
                const config = await path.configDir();
                const data = await path.dataDir();
                const localData = await path.localDataDir();
                const cache = await path.cacheDir();
                check('path.configDir() 与 path.dataDir() 同（Windows 上都是 Roaming）',
                    config === data, config + ' vs ' + data);
                check('path.localDataDir() 与 path.cacheDir() 同（Windows 上都是 Local）',
                    localData === cache, localData + ' vs ' + cache);
                check('Windows 上 Roaming 与 Local 是不同目录',
                    data !== localData, data + ' vs ' + localData);

                // 下载目录**不能**靠 .NET 的 SpecialFolder 得到（没有那个成员），
                // 而 publicDir 也不是 CommonDocuments（那个多一层 \Documents）。
                const downloads = await path.downloadDir();
                const publicDir = await path.publicDir();
                check('path.downloadDir() 取到 Downloads',
                    /[\\/]Downloads$/.test(downloads), downloads);
                check('path.publicDir() 是 Public 而不是 Public\\Documents',
                    !/[\\/]Documents$/.test(publicDir), publicDir);

                // 平台不支持的那几格要以 not_supported 明确拒绝（与 Tauri 一致），
                // 而不是给一个编出来的值
                let fontCode = 'unset';
                try {
                    await path.fontDir();
                } catch (e) {
                    fontCode = e && e.code;
                }
                check('path.fontDir() 在 Windows 上以 not_supported 被拒（与 Tauri 一致）',
                    fontCode === 'not_supported', fontCode);

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
