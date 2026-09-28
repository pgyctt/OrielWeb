namespace OrielWeb.Ipc;

/// <summary>
/// 全局命令路由注册表。生成器在引用程序集里生成 <c>[ModuleInitializer]</c>，
/// 加载时自动调用 <see cref="AddRouter"/>，应用代码无需手工登记。
/// </summary>
/// <remarks>
/// 注册时即检测命令名冲突：两个类注册同名命令时，若静默放过，后者将永远不可达
/// 且无任何提示（生效方取决于 <c>[ModuleInitializer]</c> 执行序，不稳定）。
/// </remarks>
public static class OrielCommandRegistry
{
    private static readonly Dictionary<string, IOrielCommandRouter> s_index = new(StringComparer.Ordinal);
    private static readonly object s_gate = new();

    /// <summary>注册一个路由。命令名与既有注册冲突时抛 <see cref="InvalidOperationException"/>。</summary>
    public static void AddRouter(IOrielCommandRouter router)
    {
        ArgumentNullException.ThrowIfNull(router);
        lock (s_gate)
        {
            foreach (var name in router.CommandNames)
            {
                if (s_index.TryGetValue(name, out var existing) && !ReferenceEquals(existing, router))
                {
                    throw new InvalidOperationException(
                        $"命令名冲突：'{name}' 同时注册于 {existing.GetType().Name} 与 {router.GetType().Name}。" +
                        "请修改其中一处 [OrielCommand] 的名称。");
                }
                s_index[name] = router;
            }
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
}
