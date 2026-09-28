namespace OrielWeb.Ipc;

/// <summary>
/// 全局命令路由注册表。生成器在引用程序集里生成 <c>[ModuleInitializer]</c>，
/// 加载时自动调用 <see cref="AddRouter"/>，应用代码无需手工登记。
/// </summary>
public static class OrielCommandRegistry
{
    private static readonly List<IOrielCommandRouter> s_routers = [];
    private static readonly object s_gate = new();

    public static void AddRouter(IOrielCommandRouter router)
    {
        ArgumentNullException.ThrowIfNull(router);
        lock (s_gate)
        {
            s_routers.Add(router);
        }
    }

    internal static IOrielCommandRouter[] Snapshot()
    {
        lock (s_gate)
        {
            return [.. s_routers];
        }
    }
}
