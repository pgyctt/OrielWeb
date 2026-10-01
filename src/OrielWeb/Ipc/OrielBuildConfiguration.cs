using System.Diagnostics;
using System.Reflection;

namespace OrielWeb.Ipc;

/// <summary>
/// 判定"这次运行是 Debug 构建还是 Release 构建"。
/// </summary>
/// <remarks>
/// <para>
/// 为什么不能只在库内部写 <c>#if DEBUG</c>：本库是**以 Release 发布的**，
/// 库自己的 <c>DEBUG</c> 常量永远是 false。而"Debug 还是 Release"这个问题的答案属于
/// **消费方**的构建配置——安全模型要用它决定"未声明能力时是全放行还是全拒绝"。
/// </para>
/// <para>
/// 两条取值路径，按精度排序：
/// <list type="number">
///   <item><description><c>AssemblyMetadata("OrielBuildConfiguration")</c>：由包内的
///     <c>buildTransitive/OrielWeb.targets</c> 注入到**消费方**程序集上，值是
///     <c>$(Configuration)</c>。最准。</description></item>
///   <item><description>入口程序集的 <see cref="DebuggableAttribute.IsJITOptimizerDisabled"/>：
///     不依赖任何包内 targets，因此未导入 targets 的场景（本仓库内的 ProjectReference）
///     也判定得到。Debug 构建会关掉 JIT 优化，这个位就是那个开关。</description></item>
/// </list>
/// </para>
/// <para>
/// 两条都取不到时按 **Release** 算：判不出来就当"生产构建"，与"配置缺失时 fail-closed"
/// 的方向一致。宁可让开发者先去声明能力，也不要让 Release 悄悄全放行。
/// </para>
/// </remarks>
internal static class OrielBuildConfiguration
{
    /// <summary>包内 targets 写入的元数据键名（改这里必须同步改 buildTransitive/OrielWeb.targets）。</summary>
    internal const string MetadataKey = "OrielBuildConfiguration";

    internal const string DebugValue = "Debug";
    internal const string ReleaseValue = "Release";

    /// <summary>把元数据值解析成 bool；认不出来（空、拼错、别的配置名）时返回 null。</summary>
    internal static bool? ParseMetadataValue(string? value)
        => value switch
        {
            null or "" => null,
            _ when string.Equals(value, DebugValue, StringComparison.OrdinalIgnoreCase) => true,
            _ when string.Equals(value, ReleaseValue, StringComparison.OrdinalIgnoreCase) => false,
            _ => null,
        };

    /// <summary>
    /// 合并两条取值路径。元数据优先，认不出来才回退到 <paramref name="jitOptimizerDisabled"/>。
    /// </summary>
    internal static bool Resolve(bool? metadataSaysDebug, bool jitOptimizerDisabled)
        => metadataSaysDebug ?? jitOptimizerDisabled;

    /// <summary>读取入口程序集上的构建配置元数据（没有时返回 false）。</summary>
    internal static bool TryReadMetadata(Assembly? assembly, out string? value)
    {
        value = null;
        if (assembly is null)
        {
            return false;
        }

        foreach (AssemblyMetadataAttribute attribute in assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
        {
            if (string.Equals(attribute.Key, MetadataKey, StringComparison.Ordinal))
            {
                value = attribute.Value;
                return true;
            }
        }

        return false;
    }

    /// <summary>入口程序集是否为 Debug 构建。</summary>
    internal static bool IsDebugBuild(Assembly? entryAssembly)
    {
        bool jitOptimizerDisabled =
            entryAssembly?.GetCustomAttribute<DebuggableAttribute>()?.IsJITOptimizerDisabled ?? false;

        bool? metadataSaysDebug = TryReadMetadata(entryAssembly, out string? raw)
            ? ParseMetadataValue(raw)
            : null;

        bool isDebug = Resolve(metadataSaysDebug, jitOptimizerDisabled);

        if (metadataSaysDebug is null && raw is not null)
        {
            // 元数据在、但值不认识（有人把 Configuration 改成了 "Shipping" 之类）。
            // 静默回退会让"为什么 Release 下全被拒了"无从查起，这里报一笔。
            Debug.WriteLine(
                $"[OrielWeb] 入口程序集上的 {MetadataKey}=\"{raw}\" 不是 Debug/Release，" +
                "已回退到 DebuggableAttribute 判定。");
        }

        return isDebug;
    }
}
