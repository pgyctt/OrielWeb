namespace OrielWeb.Ipc;

/// <summary>
/// 全局命令路由注册表。生成器在引用程序集里生成 <c>[ModuleInitializer]</c>，
/// 加载时自动调用 <see cref="AddRouter"/>，应用代码无需手工登记。
/// </summary>
/// <remarks>
/// <para>
/// 注册时即检测两类命令名冲突，若静默放过，后果都是"注册成功但永远不可达且无任何提示"：
/// </para>
/// <list type="bullet">
///   <item><description>两个路由注册同名命令：生效方取决于 <c>[ModuleInitializer]</c> 执行序，不稳定。</description></item>
///   <item><description>应用命令与**内建命令精确名**重合（2026-10-23 评审发现的盲区）：内建在分发器里
///     先于应用路由解析，应用侧 <c>window.close</c> / <c>fs.readTextFile</c> 这类同名命令注册成功
///     但永不执行——症状是"我明明写了这个命令，页面说找不到"，而根因在库的内建表里。</description></item>
/// </list>
/// </remarks>
public static class OrielCommandRegistry
{
    private static readonly Dictionary<string, IOrielCommandRouter> s_index = new(StringComparer.Ordinal);
    private static readonly object s_gate = new();

    /// <summary>注册表的修订号：<see cref="AddRouter"/> 每加入新命令就 +1。</summary>
    /// <remarks>
    /// 给分发器的"快照过期检测"用：路由索引在分发器构造期一次性快照（热路径无锁），
    /// 运行期 <c>Assembly.Load</c> 进来的插件程序集会让全局索引领先于快照——
    /// 分发器在查不到命令时比对修订号，发现领先就重建一次（见 OrielCommandDispatcher）。
    /// </remarks>
    public static long Version
    {
        get
        {
            lock (s_gate)
            {
                return s_version;
            }
        }
    }

    private static long s_version;

    /// <summary>注册一个路由。命令名与既有注册或内建命令冲突时抛 <see cref="InvalidOperationException"/>。</summary>
    public static void AddRouter(IOrielCommandRouter router)
    {
        ArgumentNullException.ThrowIfNull(router);
        lock (s_gate)
        {
            foreach (var name in router.CommandNames)
            {
                if (IsBuiltInCommandName(name))
                {
                    throw new InvalidOperationException(
                        $"命令名冲突：'{name}' 与库内建命令重合。内建命令先于应用路由解析，" +
                        "应用同名命令注册成功但永不可达——请换一个命令名。");
                }

                if (s_index.TryGetValue(name, out var existing) && !ReferenceEquals(existing, router))
                {
                    throw new InvalidOperationException(
                        $"命令名冲突：'{name}' 同时注册于 {existing.GetType().Name} 与 {router.GetType().Name}。" +
                        "请修改其中一处 [OrielCommand] 的名称。");
                }
                s_index[name] = router;
            }

            s_version++;
        }
    }

    /// <summary>
    /// 构建「命令名 → (路由, 索引)」快照，供分发器在热路径做 O(1) 无锁无分配查找。
    /// 注册全部发生在 <c>[ModuleInitializer]</c>（程序集加载时），因此构造期快照是安全的。
    /// </summary>
    internal static Dictionary<string, (IOrielCommandRouter Router, int Index)> BuildIndex()
    {
        lock (s_gate)
        {
            var index = new Dictionary<string, (IOrielCommandRouter, int)>(s_index.Count, StringComparer.Ordinal);
            foreach (var (name, router) in s_index)
            {
                index[name] = (router, router.Route(name));
            }
            return index;
        }
    }

    /// <summary>
    /// 列出"全局索引里有实例命令、但给定集合里没有"的类型（诊断用）。
    /// </summary>
    /// <param name="registered">本应用已 <c>AddCommands</c> 的类型。</param>
    /// <remarks>
    /// <para>
    /// 注册是**进程全局**的：生成器在每个程序集打 <c>[ModuleInitializer]</c>，程序集一被加载，
    /// 里面所有 <c>[OrielCommand]</c> 就进了索引，与"哪个应用要用"无关；而"要不要用"是
    /// per-app 的（<c>AddCommands</c>）。两者的差集就是"索引里有、但本应用没注册"的类型——
    /// 调用它们会在解析目标时失败。
    /// </para>
    /// <para>
    /// <b>为什么要报出来</b>：不报的话，症状是运行期一条命令失败，而开发者的心智模型是
    /// "我没 AddCommands，它就不该存在"。这条诊断把"能看见但调不通"变成一行可见的提示。
    /// 它**不改变行为**，只写诊断通道。
    /// </para>
    /// <para>
    /// 静态命令（<c>RequiresTarget == false</c>）不在结果里：它们不需要工厂，
    /// 也不受 <c>AddCommands</c> 约束——这一点写在 API.md 的「命令的注册语义」里。
    /// </para>
    /// </remarks>
    internal static Type[] UnregisteredTargetTypes(IReadOnlyCollection<Type> registered)
    {
        lock (s_gate)
        {
            return [.. s_index.Values
                .Where(router => router.RequiresTarget
                    && router.TargetType is { } type
                    && !registered.Contains(type))
                .Select(router => router.TargetType!)
                .Distinct()];
        }
    }

    /// <summary>
    /// name 是否由任何一个内建命令族实现（各族 <c>TryGet</c> 的精确名 switch）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 分发器把内建命令放在应用路由**之前**逐族解析（见 <c>OrielCommandDispatcher</c>），
    /// 于是内建精确名会静默遮蔽应用同名命令。这里在注册期（程序集加载时）把重合的名字
    /// 拦下——与"路由之间重名"同一时刻、同一姿态（启动即崩，不静默）。
    /// </para>
    /// <para>
    /// <b>win. / tray. 前缀刻意不在这里拦</b>：它们由编译期 <c>ORIELWEB106</c>（Error 级）
    /// 负责，运行期放行保留一条"#pragma 压制 106 做内建优先行为验证"的知情路径
    /// （见测试 ShadowWindowCommands）。两层的分工互补：**编译期管前缀，运行期管精确名**
    /// ——后者编译期看不见（window. / fs. 等前缀本身合法），也不像前缀那样有可镜像的常量。
    /// 正因如此，下面的链里没有 <c>win.</c> / <c>tray.</c> 两族：前缀短路在先，
    /// 它们的 TryGet 永远不会被调到。
    /// </para>
    /// <para>
    /// 各族 <c>TryGet</c> 是纯名字匹配（switch，无副作用），在这里复用**零漂移**——
    /// 内建表加一个名字，这条检查自动跟着生效，不存在"手抄第二份清单忘更新"的缝。
    /// </para>
    /// </remarks>
    private static bool IsBuiltInCommandName(string name)
    {
        if (OrielCapabilityRules.IsBuiltInCommand(name))
        {
            return false;
        }

        return OrielBuiltInWindowApiCommands.TryGet(name, out _)
            || OrielBuiltInFsCommands.TryGet(name, out _)
            || OrielBuiltInDialogCommands.TryGet(name, out _)
            || OrielBuiltInPlatformCommands.TryGet(name, out _)
            || OrielBuiltInAppCommands.TryGet(name, out _)
            || OrielBuiltInMenuCommands.TryGet(name, out _)
            || OrielBuiltInEventCommands.TryGet(name, out _)
            || OrielBuiltInPathCommands.TryGet(name, out _);
    }
}
