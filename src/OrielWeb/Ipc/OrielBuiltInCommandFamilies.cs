using System.Text.Json;

namespace OrielWeb.Ipc;

/// <summary>
/// 一个内建命令族：名字匹配 + 处理方法。
/// </summary>
/// <remarks>
/// <para>
/// 存在的理由：分发器原先对 10 个族各写一段 20–30 行的同构代码（判空 → try/catch → 回执），
/// 共 228 行。第 11 个族要复制第 11 遍，而"谁先谁后"只能靠代码位置看出来。收成一张有序表之后，
/// 顺序变成数组顺序（可被测试直接断言），新增族只需加一项。
/// </para>
/// <para>
/// <b>处理方法统一为三参</b>（窗口、命令名、参数）：族原本的签名有三种
/// （<c>(window, args)</c> / <c>(window, name, args)</c> / <c>(args)</c>），这里都适配到同一种。
/// 需要命令名的族（fs / dialog / platform 要按名字分派 scope 与错误文案）直接用第三个参数，其余忽略。
/// </para>
/// <para>
/// <b><see cref="RequiresWindow"/> 为 false 的族必须忽略窗口参数</b>：它可能是 <c>null</c>。
/// 目前只有 <c>path.*</c> 属于这一类（只对页面给的字符串做运算，不读宿主状态、不碰文件系统）。
/// </para>
/// </remarks>
internal interface IBuiltInCommandFamily
{
    /// <summary>族名（"win" / "fs" …）。只用于诊断与错误文案，不参与匹配。</summary>
    string Name { get; }

    /// <summary>是否需要来源窗口；false 表示无窗口时也可调用。</summary>
    bool RequiresWindow { get; }

    /// <summary>
    /// 命中时给出处理方法并返回 true；未命中返回 false（此时 <paramref name="handler"/> 的值不可用）。
    /// </summary>
    bool TryGet(
        string commandName,
        IOrielWindowControl? window,
        out Func<IOrielWindowControl, string, JsonElement, object?> handler);

    /// <summary>
    /// 异步变体：命中且该命令**必须让出 UI 线程**时返回 true。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 默认返回 false，因为绝大多数内建命令是纯内存操作，同步执行既快又简单。
    /// 只有会阻塞 UI 线程的命令（<c>dialog.*</c> 的原生模态对话框）需要实现它——
    /// 同步版本会把<b>整条 IPC 通道</b>堵在 <c>GetSaveFileNameW</c> 的栈帧里：
    /// 对话框开多久，期间所有页面 invoke 就排队多久，关闭后再一次性涌入。
    /// </para>
    /// <para>
    /// 分发器**先试异步、再试同步**。两者都不命中才算未知命令。
    /// </para>
    /// </remarks>
    bool TryGetAsync(
        string commandName,
        IOrielWindowControl? window,
        out Func<IOrielWindowControl, string, JsonElement, ValueTask<object?>> handler)
    {
        handler = null!;
        return false;
    }
}

/// <summary>
/// 族表里的一项：匹配逻辑以委托注入，避免为 10 个族各写一个类。
/// </summary>
/// <param name="name">见 <see cref="IBuiltInCommandFamily.Name"/>。</param>
/// <param name="requiresWindow">见 <see cref="IBuiltInCommandFamily.RequiresWindow"/>。</param>
/// <param name="tryGet">各族的同步 <c>TryGet</c> 适配器；与 <paramref name="tryGetAsync"/> 至少给一个。</param>
/// <param name="tryGetAsync">各族的异步 <c>TryGetAsync</c> 适配器（会让出 UI 线程的族才给）。</param>
internal sealed class BuiltInCommandFamily(
    string name,
    bool requiresWindow,
    BuiltInCommandFamily.TryGetDelegate? tryGet = null,
    BuiltInCommandFamily.TryGetAsyncDelegate? tryGetAsync = null) : IBuiltInCommandFamily
{
    /// <summary>与 <see cref="IBuiltInCommandFamily.TryGet"/> 同形，单独命名以便字段持有。</summary>
    internal delegate bool TryGetDelegate(
        string commandName,
        IOrielWindowControl? window,
        out Func<IOrielWindowControl, string, JsonElement, object?> handler);

    /// <summary>与 <see cref="IBuiltInCommandFamily.TryGetAsync"/> 同形。</summary>
    internal delegate bool TryGetAsyncDelegate(
        string commandName,
        IOrielWindowControl? window,
        out Func<IOrielWindowControl, string, JsonElement, ValueTask<object?>> handler);

    private readonly TryGetDelegate? _tryGet = tryGet;
    private readonly TryGetAsyncDelegate? _tryGetAsync = tryGetAsync;

    public string Name { get; } = name;

    public bool RequiresWindow { get; } = requiresWindow;

    public bool TryGet(
        string commandName,
        IOrielWindowControl? window,
        out Func<IOrielWindowControl, string, JsonElement, object?> handler)
    {
        handler = null!;
        return _tryGet is not null && _tryGet(commandName, window, out handler);
    }

    public bool TryGetAsync(
        string commandName,
        IOrielWindowControl? window,
        out Func<IOrielWindowControl, string, JsonElement, ValueTask<object?>> handler)
    {
        handler = null!;
        return _tryGetAsync is not null && _tryGetAsync(commandName, window, out handler);
    }
}

/// <summary>
/// 全部内建命令族，**顺序即语义**。
/// </summary>
/// <remarks>
/// <para>
/// 顺序不是随意的：<c>win.*</c> 与 <c>tray.*</c> 是库保留前缀，必须在应用路由之前解析
/// （应用定义同名命令会在注册期就被拦下，见 <c>OrielCommandRegistry.AddRouter</c>）。
/// 族之间没有重名，所以其余相对顺序对行为无影响，只影响诊断文案里报的族名。
/// </para>
/// <para>
/// 新增一族的成本是这里加一项 + 一行测试断言（族名与 RequiresWindow），不再是往
/// <c>HandleInvokeAsync</c> 里复制一段 25 行的 if。
/// </para>
/// </remarks>
internal static class OrielBuiltInCommandFamilies
{
    /// <summary>按解析优先级排列的族表。</summary>
    internal static readonly IBuiltInCommandFamily[] All =
    [
        // win.*：自绘标题栏的最小化 / 最大化 / 关闭 / 拖动 / 选文件 / 上下文菜单。
        new BuiltInCommandFamily("win", requiresWindow: true,
            (string n, IOrielWindowControl? _, out Func<IOrielWindowControl, string, JsonElement, object?> h) =>
            {
                bool hit = OrielBuiltInWindowCommands.TryGet(n, out var inner);
                h = hit ? (w, _, a) => inner(w, a) : null!;
                return hit;
            }),

        // tray.*：托盘是应用级的，但命令由某个窗口的页面发起，经 window.App 拿到应用。
        new BuiltInCommandFamily("tray", requiresWindow: true,
            (string n, IOrielWindowControl? _, out Func<IOrielWindowControl, string, JsonElement, object?> h) =>
            {
                bool hit = OrielBuiltInTrayCommands.TryGet(n, out var inner);
                h = hit ? (w, _, a) => inner(w, a) : null!;
                return hit;
            }),

        // event.*：广播 / 定向发送。不在"始终放行"名单里——广播是真正的能力（能驱动另一个窗口）。
        new BuiltInCommandFamily("event", requiresWindow: true,
            (string n, IOrielWindowControl? _, out Func<IOrielWindowControl, string, JsonElement, object?> h) =>
            {
                bool hit = OrielBuiltInEventCommands.TryGet(n, out var inner);
                h = hit ? (w, _, a) => inner(w, a) : null!;
                return hit;
            }),

        // path.*：只做字符串运算，**不需要窗口**——这是它与其余族的结构性差别。
        new BuiltInCommandFamily("path", requiresWindow: false,
            (string n, IOrielWindowControl? window, out Func<IOrielWindowControl, string, JsonElement, object?> h) =>
            {
                bool hit = OrielBuiltInPathCommands.TryGet(n, window?.App.Info.Identifier, out var inner);
                h = hit ? (_, _, a) => inner(a) : null!;
                return hit;
            }),

        // fs.*：唯一"命令授权 + 路径 scope"两道关都必过的族。
        new BuiltInCommandFamily("fs", requiresWindow: true,
            (string n, IOrielWindowControl? _, out Func<IOrielWindowControl, string, JsonElement, object?> h) =>
            {
                bool hit = OrielBuiltInFsCommands.TryGet(n, out var inner);
                h = hit ? inner : null!;
                return hit;
            }),

        // dialog.*：与既有的 win.pickFile / window.pickFile 并存（那两条是冻结的公开契约）。
        //
        // **本族只走异步通道**：原生文件对话框是模态阻塞调用，同步执行会把整条 IPC 通道
        // 堵在 GetSaveFileNameW 的栈帧里（对话框开 29 秒 = 29 秒内零命令执行，关闭后积压
        // 命令一次性涌入）。异步通道让分发器在等待期间把 UI 线程交还给消息循环。
        new BuiltInCommandFamily("dialog", requiresWindow: true,
            tryGetAsync: (string n, IOrielWindowControl? _,
                out Func<IOrielWindowControl, string, JsonElement, ValueTask<object?>> h) =>
            {
                bool hit = OrielBuiltInDialogCommands.TryGet(n, out var inner);
                h = hit ? inner : null!;
                return hit;
            }),

        // opener.* / notification.* / autostart.*：都改系统状态或能拉起外部程序，不整族放行。
        new BuiltInCommandFamily("platform", requiresWindow: true,
            (string n, IOrielWindowControl? _, out Func<IOrielWindowControl, string, JsonElement, object?> h) =>
            {
                bool hit = OrielBuiltInPlatformCommands.TryGet(n, out var inner);
                h = hit ? inner : null!;
                return hit;
            }),

        // app.*：只读元信息，需要应用实例（window.App），所以要有来源窗口。
        new BuiltInCommandFamily("app", requiresWindow: true,
            (string n, IOrielWindowControl? _, out Func<IOrielWindowControl, string, JsonElement, object?> h) =>
            {
                bool hit = OrielBuiltInAppCommands.TryGet(n, out var inner);
                h = hit ? (w, _, a) => inner(w, a) : null!;
                return hit;
            }),

        // window.*：可按 label 寻址，只有标题栏必需的那组才绕过能力校验。
        new BuiltInCommandFamily("window", requiresWindow: true,
            (string n, IOrielWindowControl? _, out Func<IOrielWindowControl, string, JsonElement, object?> h) =>
            {
                bool hit = OrielBuiltInWindowApiCommands.TryGet(n, out var inner);
                h = hit ? (w, _, a) => inner(w, a) : null!;
                return hit;
            }),

        // menu.*：页面把整棵菜单树发过来，宿主弹一次或装成菜单栏；无资源表，无状态。
        new BuiltInCommandFamily("menu", requiresWindow: true,
            (string n, IOrielWindowControl? _, out Func<IOrielWindowControl, string, JsonElement, object?> h) =>
            {
                bool hit = OrielBuiltInMenuCommands.TryGet(n, out var inner);
                h = hit ? (w, _, a) => inner(w, a) : null!;
                return hit;
            }),
    ];
}
