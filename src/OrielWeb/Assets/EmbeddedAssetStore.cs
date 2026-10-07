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
    /// <summary>相对路径 → 持有资源的程序集 + 内嵌资源名。</summary>
    /// <remarks>
    /// <para>
    /// 比较用 <see cref="StringComparer.OrdinalIgnoreCase"/>：旧的落盘实现在 Windows/macOS 上
    /// 天然大小写不敏感，只有 Linux 敏感，于是同一份前端资源在三个平台上的行为并不一致。
    /// 归一成"一律不敏感"后，<c>&lt;img src="Logo.PNG"&gt;</c> 这种写法不会再只在 Linux 上 404。
    /// 代价是两个只差大小写的资源名会撞车（先到先得）——这本来就是不该有的命名。
    /// </para>
    /// <para>
    /// 值里带**来源程序集**：类库的 wwwroot 会与入口的合并进同一张表（见 <see cref="Create"/>），
    /// 取流时必须回自己的程序集拿，而不是无脑找入口程序集。
    /// </para>
    /// </remarks>
    private readonly Dictionary<string, (Assembly Source, string ResourceName)> _resources;

    private EmbeddedAssetStore(Dictionary<string, (Assembly Source, string ResourceName)> resources)
    {
        _resources = resources;
    }

    /// <summary>已登记的相对路径（诊断用，顺序不保证）。</summary>
    internal IReadOnlyCollection<string> Paths => _resources.Keys;

    /// <summary>资源条数（用于构建日志与配置错误的提示）。</summary>
    internal int Count => _resources.Count;

    /// <summary>
    /// 扫描入口程序集与**已加载的引用程序集**（类库）的内嵌资源，建立"相对路径 → 资源"的表。
    /// </summary>
    /// <param name="resourcePrefixOverride"><c>UseEmbeddedAssets</c> 的第二个参数；null 表示按程序集名推断。只作用于入口程序集。</param>
    /// <remarks>
    /// <para>
    /// 类库也能带 wwwroot：包内 targets 对**每个**消费项目同样生效，类库的资源名前缀是
    /// **类库自己的程序集名**。运行期把入口与各类库的 wwwroot 合并成**一棵站点**，
    /// 同一相对路径由入口程序集优先（<c>TryAdd</c> 先到先得）——类库不能劫持应用的首页。
    /// </para>
    /// <para>
    /// 引用程序集走 <c>AppDomain.CurrentDomain.GetAssemblies()</c>：只枚举**已加载**的，
    /// 不按名强制加载（AOT/裁剪下按名加载不可靠）。应用只要用过类库里的任何类型
    /// （命令类、选项类……）它就已在场；仅在 csproj 里 PackageReference 却从不触及其类型的
    /// 极端用法不在覆盖范围。资源清单是元数据，<see cref="Assembly.GetManifestResourceNames"/>
    /// 在 AOT 下可用。
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">入口与所有已加载程序集加起来一个资源都没找到（这是配置错误，不是"空站点"）。</exception>
    public static EmbeddedAssetStore Create(string? resourcePrefixOverride)
    {
        var entry = Assembly.GetEntryAssembly()
            ?? throw new InvalidOperationException("无法确定入口程序集。");

        var resources = new Dictionary<string, (Assembly Source, string ResourceName)>(StringComparer.OrdinalIgnoreCase);

        // 入口程序集先行：只有它可以走自定义 resourcePrefix，且同路径冲突时它优先。
        string entryName = entry.GetName().Name ?? "app";
        string[] entryNames = entry.GetManifestResourceNames();
        string entryPrefix = ResolveConvention(entryNames, entryName, resourcePrefixOverride);
        CollectResources(entry, entryNames, entryPrefix, resources);

        // 已加载的引用程序集（类库）：固定按 <程序集名>.wwwroot/ 约定收集。
        // 按名排序让合并顺序确定（跨次运行一致）；动态程序集与名字取不到的跳过。
        var others = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !ReferenceEquals(a, entry) && !a.IsDynamic)
            .Select(a => (Assembly: a, Name: a.GetName().Name))
            .Where(t => !string.IsNullOrEmpty(t.Name))
            .OrderBy(t => t.Name, StringComparer.Ordinal);
        foreach (var (assembly, name) in others)
        {
            CollectResources(assembly, assembly.GetManifestResourceNames(), $"{name}.wwwroot/", resources);
        }

        if (resources.Count == 0)
        {
            throw new InvalidOperationException(
                $"入口程序集与已加载的引用程序集里都没有找到 wwwroot 内嵌资源（入口约定前缀 '{entryPrefix}'）。" +
                "正确做法是**什么都不写**——包内的 buildTransitive/OrielWeb.targets 会把 wwwroot 自动内嵌（见 README「快速开始」）；" +
                "自己声明时则必须带 LogicalName=\"<程序集名>.wwwroot/%(RecursiveDir)%(Filename)%(Extension)\"，" +
                "或为 UseEmbeddedAssets 显式指定以 '/' 结尾的 resourcePrefix。");
        }

        return new EmbeddedAssetStore(resources);
    }

    /// <summary>把一个程序集里命中前缀的资源收进表：同路径已在表里时**保留先来的**（先到先得，见 <see cref="Create"/>）。</summary>
    internal static void CollectResources(
        Assembly source, string[] resourceNames, string prefix,
        Dictionary<string, (Assembly Source, string ResourceName)> resources)
    {
        foreach (string name in resourceNames)
        {
            if (!name.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            string relative = MapResourceToPath(name[prefix.Length..]).Replace('\\', '/');

            // 同一个相对路径出现两次（大小写不同、两种约定混用、或入口与类库都提供同一文件）时
            // 保首个：后者是命名事故，与其静默覆盖，不如让先声明的那个稳定胜出。
            resources.TryAdd(relative, (source, name));
        }
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
        if (relative is null || !_resources.TryGetValue(relative, out (Assembly Source, string ResourceName) hit))
        {
            return false;
        }

        asset = new EmbeddedAsset(hit.Source, hit.ResourceName, MimeTypes.ForPath(relative));
        return true;
    }

    /// <summary>打开一件资源的内容流（每次调用都是新流，可并发读）。</summary>
    /// <exception cref="InvalidOperationException">资源名在表里却有取不出来（程序集被裁剪等）。</exception>
    internal Stream Open(in EmbeddedAsset asset)
        => asset.Source.GetManifestResourceStream(asset.ResourceName)
           ?? throw new InvalidOperationException(
               $"内嵌资源缺失：{asset.Source.GetName().Name}/{asset.ResourceName}");

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
    /// 判定资源名前缀：<c>&lt;程序集名&gt;.wwwroot/</c>（显式分隔符）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 只有这一种约定。以前还认"不带 <c>LogicalName</c>"的旧写法（资源名形如
    /// <c>MyApp.wwwroot.app.min.js</c>），靠"最后一个 <c>.</c> 是扩展名"反推目录——那个反推在
    /// 文件名主干含 <c>.</c> 时**必然出错**（<c>app.min.js</c> 被推成 <c>app/min.js</c>，
    /// 页面 404 白屏而宿主侧没有任何异常），而且从资源名上无法与"嵌套目录"区分开。
    /// 2026-10-03 起不再兼容：撞见旧形式直接抛异常并指路，而不是继续猜。
    /// </para>
    /// <para>
    /// <b>前缀自己就说明用的是哪一种</b>，所以这里不需要（也不能）靠资源名去猜：
    /// <c>wwwroot/app.min.js</c> 与 <c>wwwroot/app/min.js</c> 在旧约定下生成的资源名一模一样，
    /// 只有前缀（<c>/</c> 还是 <c>.</c>）能区分它们。
    /// </para>
    /// </remarks>
    internal static string ResolveConvention(
        string[] resourceNames, string assemblyName, string? resourcePrefixOverride)
    {
        if (resourcePrefixOverride is { Length: > 0 } custom)
        {
            if (!IsExplicitSeparatorPrefix(custom))
            {
                throw new InvalidOperationException(
                    $"UseEmbeddedAssets 的 resourcePrefix「{custom}」不是显式分隔符形式：" +
                    "它必须以 '/' 结尾（如 \"MyApp.wwwroot/\"）。不带分隔符的旧形式已不再支持——" +
                    "那种形式下 app.min.js 与 app/min.js 的资源名完全一样，只能靠猜，而猜错的表现是页面白屏。");
            }

            return custom;
        }

        string prefix = $"{assemblyName}.wwwroot/";
        if (Array.Exists(resourceNames, name => name.StartsWith(prefix, StringComparison.Ordinal)))
        {
            return prefix;
        }

        // 走到这里通常是旧写法：报"你用的是已删除的形式"，而不是笼统的"没找到资源"——
        // 后者会让人去翻资源名，而这里的问题在 csproj 的写法上。
        string legacyPrefix = $"{assemblyName}.wwwroot.";
        if (Array.Exists(resourceNames, name => name.StartsWith(legacyPrefix, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                $"检测到旧写法内嵌资源（前缀 '{legacyPrefix}'）：不带 LogicalName 的 " +
                "<EmbeddedResource Include=\"wwwroot\\**\\*\" /> 已不再支持。请删掉那一行，" +
                "让包内的 buildTransitive/OrielWeb.targets 自动内嵌（零配置）；" +
                "要自己声明就必须带 LogicalName=\"" + $"{assemblyName}.wwwroot/" + "%(RecursiveDir)%(Filename)%(Extension)\"。");
        }

        return prefix;
    }

    /// <summary>前缀自身是否声明了"显式分隔符"（以 <c>/</c> 或 <c>\</c> 结尾）。</summary>
    private static bool IsExplicitSeparatorPrefix(string prefix)
        => prefix[^1] == '/' || prefix[^1] == '\\';

    /// <summary>
    /// 资源名后缀 → 相对路径。
    /// </summary>
    /// <remarks>
    /// 只做分隔符翻译：资源名里的 <c>.</c> 一律是文件名的一部分
    /// （<c>app.min.js</c> 就是 <c>app.min.js</c>，不是 <c>app/min.js</c>）——这正是
    /// <c>LogicalName</c> 带显式 <c>/</c> 的意义所在。旧写法那套"反推目录"已删除，见
    /// <see cref="ResolveConvention"/>。
    /// </remarks>
    /// <param name="resourceSuffix">去掉前缀之后的资源名。</param>
    internal static string MapResourceToPath(string resourceSuffix)
        => resourceSuffix
            .Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);
}

/// <summary>一件内嵌资源：持有它的程序集、资源名与按扩展名推断的内容类型。</summary>
/// <param name="Source">持有这份资源的程序集（入口程序集，或某个带 wwwroot 的引用类库）。</param>
/// <param name="ResourceName">该程序集里的内嵌资源名（<see cref="EmbeddedAssetStore"/> 查表得来）。</param>
/// <param name="ContentType">应答时用的 Content-Type（含 charset，见 <see cref="MimeTypes"/>）。</param>
internal readonly record struct EmbeddedAsset(Assembly Source, string ResourceName, string ContentType);
