using System.Reflection;

namespace OrielWeb;

/// <summary>
/// 关于本库自身的信息。
/// </summary>
public static class OrielLibrary
{
    /// <summary>
    /// 库的版本号（<c>主.次.修订</c>，形如 <c>0.4.0</c>）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 从程序集版本读，**不写字面量**：写死的话它与 csproj 的 <c>&lt;Version&gt;</c> 会各自漂移，
    /// 而页面基于版本做的兼容判断会静默地得到错答案。
    /// </para>
    /// <para>
    /// 同一个值注入到页面侧的 <c>oriel.version</c>，也是 <c>app.getTauriVersion()</c> 的返回值
    /// （对标 Tauri 的 <c>getTauriVersion()</c>——那边给的是框架版本，这边给库版本）。
    /// </para>
    /// </remarks>
    public static string Version
    {
        get
        {
            Version? version = typeof(OrielLibrary).Assembly.GetName().Version;
            return version is null
                ? "0.0.0"
                : $"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}";
        }
    }
}

/// <summary>
/// 应用自身的元信息（对标 Tauri 的 <c>tauri.conf.json</c> 里那几个字段）。
/// </summary>
/// <remarks>
/// <para>
/// 三项都可以显式给（<see cref="OrielAppBuilder.WithAppInfo"/>），不给时从**入口程序集**推：
/// 名称取 <see cref="AssemblyProductAttribute"/>（退化到 <see cref="AssemblyTitleAttribute"/>、
/// 再退化到程序集简单名），版本取 <see cref="AssemblyInformationalVersionAttribute"/>（退化到
/// 程序集版本），标识符退化为程序集简单名的小写形式。
/// </para>
/// <para>
/// <b>自动推断的值只适合"能跑起来"，正式应用应当显式给</b>——尤其
/// <see cref="Identifier"/>：它将成为数据目录的组成部分（对标 Tauri 的
/// <c>bundle.identifier</c>），而一个从程序集名推出来的标识符很容易与服务名/产品名不一致，
/// 等数据已经写进去之后再改就要做迁移。
/// </para>
/// <para>
/// 为什么从程序集推而不是要求必填：让最小示例不必为了打印个版本号就配一堆东西。
/// 显式配置的代价很低（一个方法调用），而"必须先配 3 个字段才能启动"会挡住第一次上手的人。
/// </para>
/// </remarks>
public sealed class OrielAppInfo
{
    /// <summary>应用显示名（对标 Tauri 的 <c>productName</c>）。</summary>
    public required string Name { get; init; }

    /// <summary>应用版本（对标 Tauri 的 <c>version</c>）。</summary>
    public required string Version { get; init; }

    /// <summary>
    /// 应用标识符（对标 Tauri 的 <c>identifier</c>），形如 <c>com.example.app</c>。
    /// </summary>
    /// <remarks>
    /// 它是**数据目录的组成部分**，所以字符集受限于"能安全当目录名"：
    /// 字母、数字、<c>-</c>、<c>_</c>、<c>.</c>。校验在 <see cref="Validate"/> 里做。
    /// </remarks>
    public required string Identifier { get; init; }

    /// <summary>
    /// 从入口程序集推断一份默认值。
    /// </summary>
    /// <remarks>
    /// 各项独立退化：产品名缺失不影响版本仍能从程序集读到。
    /// </remarks>
    internal static OrielAppInfo Detect()
    {
        Assembly assembly = Assembly.GetEntryAssembly() ?? typeof(OrielAppInfo).Assembly;
        AssemblyName assemblyName = assembly.GetName();
        string simpleName = assemblyName.Name ?? "oriel-app";

        string? product = assembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product;
        string? title = assembly.GetCustomAttribute<AssemblyTitleAttribute>()?.Title;
        string name = FirstNonEmpty(product, title, simpleName);

        return new OrielAppInfo
        {
            Name = name,
            Version = DetectVersion(assembly, assemblyName),
            // 标识符归一成小写：数据目录在 Windows/macOS 上大小写不敏感、Linux 上敏感，
            // 而"同一个应用在两台机器上写出两个目录"（MyApp / myapp）是很难查的一类问题。
            Identifier = NormalizeIdentifier(simpleName),
        };
    }

    /// <summary>
    /// 校验三项都可用，不合法时抛 <see cref="ArgumentException"/>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 在 <c>Build()</c> 时调（不是用时才调）：标识符要拼进数据目录，而"启动后第一次写配置"
    /// 才发现标识符非法，故障点离原因很远。宁可启动就失败并指出是哪个字符。
    /// </para>
    /// <para>
    /// 名称与版本只查非空：它们是给人看的，任何字符都可能合理出现。
    /// </para>
    /// </remarks>
    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new ArgumentException("应用名不能为空（WithAppInfo 的 name）。", nameof(Name));
        }

        if (string.IsNullOrWhiteSpace(Version))
        {
            throw new ArgumentException("应用版本不能为空（WithAppInfo 的 version）。", nameof(Version));
        }

        if (string.IsNullOrWhiteSpace(Identifier))
        {
            throw new ArgumentException("应用标识符不能为空（WithAppInfo 的 identifier）。", nameof(Identifier));
        }

        // 标识符要当目录名，所以字符集卡死：允许字母、数字、'-'、'_'、'.'。
        // 特别地**不允许路径分隔符**——'/' 或 '\' 会让"应用数据目录"跳到别的地方去，
        // 而那是从一个配置文件里读出来的值，不该有这种能力。
        foreach (char c in Identifier)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.'))
            {
                throw new ArgumentException(
                    $"应用标识符「{Identifier}」含非法字符「{c}」：它要拼进应用数据目录，只能用字母、数字、'-'、'_'、'.'。",
                    nameof(Identifier));
            }
        }

        // 全是点/横线的标识符（"."、".."、"..."）能通过上面的字符集检查，
        // 但作为目录名分别指"当前目录""上级目录"——那是最糟糕的一类输入。
        if (Identifier.Trim('.', '-', '_').Length == 0)
        {
            throw new ArgumentException(
                $"应用标识符「{Identifier}」只有点或横线：它作为目录名会指到当前/上级目录。",
                nameof(Identifier));
        }
    }

    /// <summary>
    /// 版本：优先 <see cref="AssemblyInformationalVersionAttribute"/>（那是语义化版本），
    /// 退化到程序集版本。
    /// </summary>
    /// <remarks>
    /// 把 <c>+</c> 之后的构建元数据去掉：.NET SDK 会在那里塞 git 提交号
    /// （<c>1.2.3+abc1234</c>），而它进到"给用户看的版本"或"参与比较的版本"里都是噪音——
    /// 比较 <c>1.2.3+abc</c> 与 <c>1.2.3</c> 会得到"不相等"，而它们是同一个发布版本。
    /// </remarks>
    private static string DetectVersion(Assembly assembly, AssemblyName assemblyName)
    {
        string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            int plus = informational.IndexOf('+', StringComparison.Ordinal);
            return plus > 0 ? informational[..plus] : informational;
        }

        Version? version = assemblyName.Version;
        return version is null
            ? "0.0.0"
            : $"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}";
    }

    /// <summary>把程序集名归一成可当目录名的标识符（小写；非法字符换成 '-'）。</summary>
    private static string NormalizeIdentifier(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);

        foreach (char c in value)
        {
            builder.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'
                ? char.ToLowerInvariant(c)
                : '-');
        }

        string result = builder.ToString().Trim('-', '.');
        return result.Length == 0 ? "oriel-app" : result;
    }

    private static string FirstNonEmpty(params string?[] candidates)
    {
        foreach (string? candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate;
            }
        }

        return "oriel-app";
    }
}
