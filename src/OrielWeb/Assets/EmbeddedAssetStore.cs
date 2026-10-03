using System.Reflection;

namespace OrielWeb;

/// <summary>
/// 内嵌前端资源（<c>wwwroot</c>）的目录表与按需读取入口。
/// </summary>
/// <remarks>
/// <para>
/// 取代了旧的 <c>EmbeddedAssetExtractor</c>：那时资源先解压到
/// <c>%LocalAppData%\OrielWeb\&lt;程序集&gt;\www</c>，再按目录/文件交给引擎。现在三平台统一用自定义
/// scheme <c>oriel://&lt;host&gt;/…</c> 提供服务（Windows 走 <c>WebResourceRequested</c>、
/// Linux 走 <c>WebKitURISchemeRequest</c>、macOS 走 <c>WKURLSchemeHandler</c>），于是：
/// </para>
/// <list type="bullet">
///   <item>不写盘：只读介质、沙箱、容器里都能跑，也不在用户盘上留可被篡改的前端文件；</item>
///   <item>不必"先递归清空再重建"：那份目录一旦陈旧，症状是"代码里删了资源、运行时还能访问到"，
///     为此存在的 <c>ResetDirectory</c> 及其"必须先校验路径否则会删错数据"的防线一并消失；</item>
///   <item>目录穿越防护退化成一次字典查表——查不到就是没有，不需要再和文件系统语义周旋。</item>
/// </list>
/// <para>
/// 资源是编译进程序集的，所以<strong>单文件 / Native AOT 发布</strong>后依然取得到。
/// </para>
/// </remarks>
internal sealed class EmbeddedAssetStore
{
    /// <summary>相对路径 → 内嵌资源名。</summary>
    /// <remarks>
    /// 比较用 <see cref="StringComparer.OrdinalIgnoreCase"/>：旧的落盘实现在 Windows/macOS 上
    /// 天然大小写不敏感，只有 Linux 敏感，于是同一份前端资源在三个平台上的行为并不一致。
    /// 归一成"一律不敏感"后，<c>&lt;img src="Logo.PNG"&gt;</c> 这种写法不会再只在 Linux 上 404。
    /// 代价是两个只差大小写的资源名会撞车（先到先得）——这本来就是不该有的命名。
    /// </remarks>
    private readonly Dictionary<string, string> _resources;

    private readonly Assembly _assembly;

    private EmbeddedAssetStore(Assembly assembly, Dictionary<string, string> resources)
    {
        _assembly = assembly;
        _resources = resources;
    }

    /// <summary>已登记的相对路径（诊断用，顺序不保证）。</summary>
    internal IReadOnlyCollection<string> Paths => _resources.Keys;

    /// <summary>资源条数（用于构建日志与配置错误的提示）。</summary>
    internal int Count => _resources.Count;

    /// <summary>
    /// 扫描入口程序集的内嵌资源，建立"相对路径 → 资源名"的表。
    /// </summary>
    /// <param name="resourcePrefixOverride"><c>UseEmbeddedAssets</c> 的第二个参数；null 表示按程序集名推断。</param>
    /// <exception cref="InvalidOperationException">一个资源都没找到（这是配置错误，不是"空站点"）。</exception>
    public static EmbeddedAssetStore Create(string? resourcePrefixOverride)
    {
        var assembly = Assembly.GetEntryAssembly()
            ?? throw new InvalidOperationException("无法确定入口程序集。");

        string assemblyName = assembly.GetName().Name ?? "app";
        string[] names = assembly.GetManifestResourceNames();
        var convention = ResolveConvention(names, assemblyName, resourcePrefixOverride);

        var resources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in names)
        {
            if (!name.StartsWith(convention.Prefix, StringComparison.Ordinal))
            {
                continue;
            }

            string relative = MapResourceToPath(name[convention.Prefix.Length..], convention.ExplicitSeparators)
                .Replace('\\', '/');

            // 同一个相对路径出现两次（大小写不同、或两种约定混用）时保首个：后者是命名事故，
            // 与其静默覆盖，不如让先声明的那个稳定胜出。
            resources.TryAdd(relative, name);
        }

        if (resources.Count == 0)
        {
            throw new InvalidOperationException(
                $"未找到前缀为 '{convention.Prefix}' 的内嵌资源：请在 csproj 配置 " +
                "<EmbeddedResource Include=\"wwwroot\\**\\*\" />（见 README「快速开始」），" +
                "或为 UseEmbeddedAssets 显式指定 resourcePrefix。");
        }

        return new EmbeddedAssetStore(assembly, resources);
    }

    /// <summary>
    /// 按 URL 路径取一件资源。
    /// </summary>
    /// <param name="requestPath">
    /// URL 的 path 部分（未解码也可，内部会解码）。空、<c>/</c>、以 <c>/</c> 结尾的路径按
    /// <c>index.html</c> 处理，与旧的 <c>AssetUrlResolver</c> 语义一致。
    /// </param>
    /// <param name="asset">取到的资源；未命中时 <c>null</c>。</param>
    /// <returns>是否命中。</returns>
    internal bool TryGet(string? requestPath, out EmbeddedAsset asset)
    {
        asset = default;

        string? relative = NormalizeRequestPath(requestPath);
        if (relative is null || !_resources.TryGetValue(relative, out string? resourceName))
        {
            return false;
        }

        asset = new EmbeddedAsset(resourceName, MimeTypes.ForPath(relative));
        return true;
    }

    /// <summary>打开一件资源的内容流（每次调用都是新流，可并发读）。</summary>
    /// <exception cref="InvalidOperationException">资源名在表里却有取不出来（程序集被裁剪等）。</exception>
    internal Stream Open(in EmbeddedAsset asset)
        => _assembly.GetManifestResourceStream(asset.ResourceName)
           ?? throw new InvalidOperationException($"内嵌资源缺失：{asset.ResourceName}");

    /// <summary>
    /// URL 的 path → 资源表的键。返回 <c>null</c> 表示这个路径按"不存在"处理。
    /// </summary>
    /// <remarks>
    /// 目录穿越（<c>..</c>、<c>.</c> 段）在这里就被判掉。旧实现要在解析后校验
    /// "最终路径仍在资源目录之内"，因为它的输出是文件系统路径；现在输出是字典键，
    /// 穿越段本来就不可能是键，但仍然显式拒绝——这样"拒绝"是**决定**而不是**巧合**。
    /// </remarks>
    internal static string? NormalizeRequestPath(string? requestPath)
    {
        if (string.IsNullOrEmpty(requestPath))
        {
            return "index.html";
        }

        string path = Uri.UnescapeDataString(requestPath).Replace('\\', '/').TrimStart('/');
        if (path.Length == 0)
        {
            return "index.html";
        }

        if (path.EndsWith('/'))
        {
            path += "index.html";
        }

        foreach (string segment in path.Split('/'))
        {
            if (segment is ".." or ".")
            {
                return null;
            }
        }

        return path;
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

/// <summary>一件内嵌资源：它的资源名与按扩展名推断的内容类型。</summary>
/// <param name="ResourceName">程序集里的内嵌资源名（<see cref="EmbeddedAssetStore"/> 查表得来）。</param>
/// <param name="ContentType">应答时用的 Content-Type（含 charset，见 <see cref="MimeTypes"/>）。</param>
internal readonly record struct EmbeddedAsset(string ResourceName, string ContentType);
