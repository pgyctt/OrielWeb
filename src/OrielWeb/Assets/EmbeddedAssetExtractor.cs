using System.Diagnostics;
using System.Reflection;

namespace OrielWeb;

/// <summary>
/// 把 csproj 中 EmbeddedResource 的 wwwroot 资源解压到用户目录，
/// 供平台后端以虚拟主机（如 https://app.oriel/）提供。
/// </summary>
internal static class EmbeddedAssetExtractor
{
    public static string Extract(string? resourcePrefixOverride)
    {
        var assembly = Assembly.GetEntryAssembly()
            ?? throw new InvalidOperationException("无法确定入口程序集。");

        string assemblyName = assembly.GetName().Name ?? "app";

        // LocalApplicationData 取不到时（精简容器、没设 HOME 的环境）GetFolderPath 返回空串，
        // 而 Path.Combine("", "OrielWeb", …) 拼出的是**相对**路径——资源会被解到当前工作目录里。
        // 2026-10-02 就这么在仓库根上造出了一个 OrielWeb/OrielDemo/www 并被误提交。
        // 这种情况退回临时目录：宁可落在奇怪的地方，也不要在别人的工作目录里拉屎。
        string dataRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(dataRoot))
        {
            dataRoot = Path.GetTempPath();
        }

        string root = Path.Combine(dataRoot, "OrielWeb", assemblyName, "www");

        var names = assembly.GetManifestResourceNames();
        (string Prefix, bool ExplicitSeparators) convention = ResolveConvention(names, assemblyName, resourcePrefixOverride);

        // 先算出完整清单再动磁盘：这样"没有资源"这种配置错误不会先把目录清空再抛异常。
        var planned = new List<(string Resource, string Relative)>();
        foreach (var name in names)
        {
            if (name.StartsWith(convention.Prefix, StringComparison.Ordinal))
            {
                planned.Add((name, MapResourceToPath(
                    name[convention.Prefix.Length..], convention.ExplicitSeparators)));
            }
        }

        if (planned.Count == 0)
        {
            throw new InvalidOperationException(
                $"未找到前缀为 '{convention.Prefix}' 的内嵌资源：请在 csproj 配置 " +
                "<EmbeddedResource Include=\"wwwroot\\**\\*\" />（见 README「快速开始」），" +
                "或为 UseEmbeddedAssets 显式指定 resourcePrefix。");
        }

        // 解压前先清空：否则**上一版里有、这一版已删掉**的前端文件会留在目录里继续被虚拟主机服务，
        // 表现为"代码里删了资源，运行时还能访问到"。顺带也让每次启动的结果与资源集严格一致。
        ResetDirectory(root);

        foreach (var (resource, relative) in planned)
        {
            var target = Path.Combine(root, relative);
            var targetDir = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }

            using var input = assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"内嵌资源缺失：{resource}");
            using var output = File.Create(target);
            input.CopyTo(output);
        }

        return root;
    }

    /// <summary>
    /// 清空并重建解压目录。
    /// </summary>
    /// <remarks>
    /// 这里做的是**递归删除**，因此必须先确认目标确实是本库自己建的那个目录——
    /// 路径拼接一旦写错，代价是删掉用户别处的数据。校验按"必须在
    /// <c>%LocalAppData%\OrielWeb\</c> 之下"进行，不满足就拒绝执行而不是将错就错。
    /// </remarks>
    private static void ResetDirectory(string root)
    {
        string expectedBase = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OrielWeb"));
        string fullRoot = Path.GetFullPath(root);

        if (!fullRoot.StartsWith(expectedBase + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"拒绝清理非本库目录：{fullRoot}（期望位于 {expectedBase} 之下）。");
        }

        if (Directory.Exists(fullRoot))
        {
            Directory.Delete(fullRoot, recursive: true);
        }

        Directory.CreateDirectory(fullRoot);
    }

    /// <summary>
    /// 判定资源名用的是哪种约定：前缀是 <c>&lt;程序集名&gt;.wwwroot/</c>（显式分隔符，推荐）
    /// 还是 <c>&lt;程序集名&gt;.wwwroot.</c>（兼容写法）。
    /// </summary>
    /// <remarks>
    /// <b>前缀自己就说明用的是哪一种</b>，所以这里不需要（也不能）靠资源名去猜。
    /// 这一点很关键：<c>wwwroot/app.min.js</c> 与 <c>wwwroot/app/min.js</c> 在两种约定下
    /// 生成的资源名一模一样，只有前缀（<c>/</c> 还是 <c>.</c>）能区分它们。
    /// 早先只按"资源名里有没有分隔符"判断，导致**平铺目录下的含点文件名**
    /// （<c>app.min.js</c>、<c>vendor.bundle.js</c>）仍然被反推成 <c>app/min.js</c>。
    /// </remarks>
    internal static (string Prefix, bool ExplicitSeparators) ResolveConvention(
        string[] resourceNames, string assemblyName, string? resourcePrefixOverride)
    {
        if (resourcePrefixOverride is { Length: > 0 } custom)
        {
            return (custom, IsExplicitSeparatorPrefix(custom));
        }

        string explicitPrefix = $"{assemblyName}.wwwroot/";
        if (Array.Exists(resourceNames, name => name.StartsWith(explicitPrefix, StringComparison.Ordinal)))
        {
            return (explicitPrefix, true);
        }

        return ($"{assemblyName}.wwwroot.", false);
    }

    /// <summary>前缀自身是否声明了"显式分隔符"约定（以 <c>/</c> 或 <c>\</c> 结尾）。</summary>
    private static bool IsExplicitSeparatorPrefix(string prefix)
        => prefix[^1] == '/' || prefix[^1] == '\\';

    /// <summary>
    /// 资源名后缀 → 相对路径。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>推荐写法（显式分隔符）</b>：用 <c>LogicalName</c> 让资源名自带路径分隔符，
    /// 此时 <c>.</c> 就只是文件名的一部分，不存在任何歧义：
    /// </para>
    /// <code>
    /// &lt;EmbeddedResource Include="wwwroot\**\*"
    ///                   LogicalName="$(AssemblyName).wwwroot/%(RecursiveDir)%(Filename)%(Extension)" /&gt;
    /// </code>
    /// <para>
    /// <b>兼容写法</b>：<c>Include="wwwroot\**\*"</c> 不加 <c>LogicalName</c> 时，MSBuild 会把
    /// <c>%(RecursiveDir)</c> 里的分隔符压成 <c>.</c>，于是只能靠"最后一个 <c>.</c> 是扩展名"反推目录。
    /// 这个反推在**文件名主干或目录名含 <c>.</c>** 时必然出错，而且从资源名上无法与"嵌套目录"区分开：
    /// <list type="bullet">
    ///   <item><c>assets/img/logo.svg</c> → <c>assets.img.logo.svg</c> → <c>assets/img/logo.svg</c> ✅</item>
    ///   <item><c>app.min.js</c> → <c>app.min.js</c> → <c>app/min.js</c> ❌（会静默 404 白屏）</item>
    /// </list>
    /// 含点文件名的工程请改用上面的 <c>LogicalName</c> 写法。
    /// </para>
    /// </remarks>
    /// <param name="resourceSuffix">去掉前缀之后的资源名。</param>
    /// <param name="explicitSeparators">
    /// 该资源集是否使用显式分隔符约定（由 <see cref="ResolveConvention"/> 判定）。
    /// </param>
    internal static string MapResourceToPath(string resourceSuffix, bool explicitSeparators)
    {
        // 显式约定，或资源名里本来就带分隔符 → '.' 一律是文件名的一部分，只需翻译分隔符
        if (explicitSeparators
            || resourceSuffix.Contains('/')
            || resourceSuffix.Contains('\\'))
        {
            return resourceSuffix
                .Replace('\\', Path.DirectorySeparatorChar)
                .Replace('/', Path.DirectorySeparatorChar);
        }

        // 兼容写法：只能靠"最后一个 '.' 是扩展名"反推
        var extensionIndex = resourceSuffix.LastIndexOf('.');
        if (extensionIndex < 0)
        {
            return resourceSuffix.Replace('.', Path.DirectorySeparatorChar);
        }

        var stem = resourceSuffix[..extensionIndex].Replace('.', Path.DirectorySeparatorChar);
        return stem + resourceSuffix[extensionIndex..];
    }
}
