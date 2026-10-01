using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OrielWeb.Cli.Bundle;

/// <summary>产物面向的平台（由 RID 推断）。</summary>
internal enum BundlePlatform
{
    Windows,
    MacOS,
    Linux,
}

/// <summary>
/// <c>oriel bundle</c> 的输入：一个已发布的目录 + 元数据。
/// </summary>
/// <remarks>
/// 打包器只**消费**发布产物，不替调用方决定怎么发布：`dotnet publish -r &lt;rid&gt;`（含 AOT 与那堆
/// 属性）各项目差别很大，库没有立场替它决定。这也是"输入是一个目录"而不是"输入是一个 csproj"的原因。
/// </remarks>
internal sealed record BundleOptions(
    string PublishDirectory,
    string Rid,
    BundlePlatform Platform,
    string Name,
    string Id,
    string Version,
    string? Icon,
    string? Publisher,
    string OutputDirectory)
{
    /// <summary>
    /// MSI 的 UpgradeCode：从应用标识派生，**必须稳定**。
    /// </summary>
    /// <remarks>
    /// 它决定 Windows 把新版本认成"同一个应用的升级"还是"另一个应用"。每次打包随机生成的话，
    /// 升级会变成并列安装、旧版本卸不掉——而这类错误在开发者本机往往看不出来（他只装一次）。
    /// </remarks>
    internal Guid UpgradeCode => StableGuid.From("orielweb.msi.upgrade:" + Id);

    /// <summary>产物文件名的公共前缀（平台后缀由各打包器加）。</summary>
    internal string ArtifactBaseName => $"{Sanitize(Name)}-{Version}-{Rid}";

    /// <summary>
    /// 把应用名变成"文件名安全"的形式：**只保留 ASCII** 字母数字与 <c>- _ .</c>，其余一律换成 <c>-</c>。
    /// </summary>
    /// <remarks>
    /// 刻意不用 <c>char.IsLetterOrDigit</c>：它对中文返回 true，于是 "My App 中文" 会原样进入产物文件名，
    /// 而 .AppImage / .desktop 那条链路上（图标名、desktop 文件名）非 ASCII 会遇到一堆真实的兼容问题。
    /// 名字本身不受影响——只有文件名用它，应用的显示名还是原名。
    /// </remarks>
    internal static string Sanitize(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (char character in name)
        {
            bool keep = character is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9')
                        or '-' or '_' or '.';
            builder.Append(keep ? character : '-');
        }

        return builder.ToString().Trim('-');
    }
}

/// <summary>从字符串派生**稳定**的 GUID（同样的输入永远得到同样的值）。</summary>
/// <remarks>
/// 用 SHA-256 的前 16 字节：这里不需要抗碰撞，只需要确定性；选 SHA-256 而不是 MD5 是为了不触发
/// "用了已知破损的散列算法"那类分析器告警——这个 GUID 最终会写进 MSI，别让它看起来像安全相关的东西。
/// </remarks>
internal static class StableGuid
{
    internal static Guid From(string seed)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        return new Guid(hash.AsSpan(0, 16));
    }
}

/// <summary>
/// 把命令行参数与可选的 manifest 文件合成一份 <see cref="BundleOptions"/>，并**在本地就把错拦住**。
/// </summary>
/// <remarks>
/// 两处来源共用同一套校验：CI 里用参数（一目了然），应用的长期配置放 manifest（不重复写）。
/// 参数覆盖 manifest 里的同名项——这样 CI 想临时改产物目录时不必去动仓库里的文件。
/// </remarks>
internal static class BundleOptionsReader
{
    private const string ManifestKeyDirectory = "dir";
    private const string ManifestKeyRid = "rid";
    private const string ManifestKeyName = "name";
    private const string ManifestKeyId = "id";
    private const string ManifestKeyVersion = "version";
    private const string ManifestKeyIcon = "icon";
    private const string ManifestKeyPublisher = "publisher";
    private const string ManifestKeyOutput = "out";

    internal static (BundleOptions? Options, string? Error) Resolve(ParsedCommandLine parsed)
    {
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        string? baseDirectory = null;

        if (parsed.Value("--manifest") is { Length: > 0 } manifestPath)
        {
            if (!File.Exists(manifestPath))
            {
                return (null, $"找不到 manifest 文件：{manifestPath}");
            }

            (Dictionary<string, string> fromManifest, string? manifestError) = ParseManifest(File.ReadAllText(manifestPath));
            if (manifestError is not null)
            {
                return (null, $"{manifestPath}：{manifestError}");
            }

            foreach ((string key, string value) in fromManifest)
            {
                values[key] = value;
            }

            // manifest 里的相对路径以 manifest 文件所在目录为准——不这样的话，
            // "在仓库根跑"与"在子目录跑"会得到不同的结果，而这是最难查的一类问题。
            baseDirectory = Path.GetDirectoryName(Path.GetFullPath(manifestPath));
        }

        void Override(string option, string key)
        {
            if (parsed.Value(option) is { } value)
            {
                values[key] = value;
            }
        }

        Override("--dir", ManifestKeyDirectory);
        Override("--rid", ManifestKeyRid);
        Override("--name", ManifestKeyName);
        Override("--id", ManifestKeyId);
        Override("--version", ManifestKeyVersion);
        Override("--icon", ManifestKeyIcon);
        Override("--publisher", ManifestKeyPublisher);
        Override("--out", ManifestKeyOutput);

        return Build(values, baseDirectory, parsed.Value("--out"));
    }

    /// <summary>纯函数：manifest 文本 → 键值对。</summary>
    internal static (Dictionary<string, string> Values, string? Error) ParseManifest(string json)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            return (values, $"不是合法 JSON：{ex.Message}");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return (values, "顶层必须是一个对象。");
            }

            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String)
                {
                    return (values, $"字段 {property.Name} 必须是字符串。");
                }

                values[property.Name] = property.Value.GetString() ?? string.Empty;
            }
        }

        return (values, null);
    }

    private static (BundleOptions? Options, string? Error) Build(
        Dictionary<string, string> values, string? manifestBaseDirectory, string? outputOverride)
    {
        string Get(string key) => values.TryGetValue(key, out string? value) ? value : string.Empty;

        string directory = Get(ManifestKeyDirectory);
        string rid = Get(ManifestKeyRid);
        string name = Get(ManifestKeyName);
        string id = Get(ManifestKeyId);

        if (directory.Length == 0 || rid.Length == 0 || name.Length == 0 || id.Length == 0)
        {
            return (null, "缺少必填项：--dir、--rid、--name、--id（或 manifest 里的同名项）。");
        }

        string publishDirectory = manifestBaseDirectory is null
            ? Path.GetFullPath(directory)
            : Path.GetFullPath(directory, manifestBaseDirectory);

        if (!Directory.Exists(publishDirectory))
        {
            return (null, $"--dir 指向的目录不存在：{publishDirectory}（先跑 dotnet publish）");
        }

        if (ResolvePlatform(rid) is not { } platform)
        {
            return (null, $"--rid 认不出来：{rid}（期望 win-* / osx-* / linux-*）");
        }

        if (!LooksLikeReverseDomainId(id))
        {
            return (null, $"--id 不像反向域名标识：{id}（如 com.example.myapp；" +
                          "它同时是 macOS 的 bundle identifier 与 MSI 的 UpgradeCode 来源）");
        }

        string? version = Get(ManifestKeyVersion) is { Length: > 0 } explicitVersion
            ? explicitVersion
            : TryReadVersionFromPublishOutput(publishDirectory, platform);

        if (version is null)
        {
            return (null, "没给 --version，也没能从发布目录里的可执行文件读出文件版本。请显式给 --version。");
        }

        string? icon = Get(ManifestKeyIcon) is { Length: > 0 } iconValue
            ? (manifestBaseDirectory is null ? Path.GetFullPath(iconValue) : Path.GetFullPath(iconValue, manifestBaseDirectory))
            : null;

        if (icon is not null && !File.Exists(icon))
        {
            return (null, $"--icon 指向的文件不存在：{icon}");
        }

        string outputDirectory = outputOverride is { Length: > 0 }
            ? Path.GetFullPath(outputOverride)
            : Path.Combine(publishDirectory, "..", "bundle");

        return (new BundleOptions(
            publishDirectory,
            rid,
            platform,
            name,
            id,
            version,
            icon,
            Get(ManifestKeyPublisher) is { Length: > 0 } publisher ? publisher : null,
            Path.GetFullPath(outputDirectory)), null);
    }

    internal static BundlePlatform? ResolvePlatform(string rid)
        => rid.StartsWith("win-", StringComparison.OrdinalIgnoreCase) ? BundlePlatform.Windows
            : rid.StartsWith("osx-", StringComparison.OrdinalIgnoreCase) ? BundlePlatform.MacOS
            : rid.StartsWith("linux-", StringComparison.OrdinalIgnoreCase) ? BundlePlatform.Linux
            : null;

    /// <summary>
    /// 反向域名标识的宽松校验：至少一个点、只含字母数字与 <c>. - _</c>、不以点开头。
    /// </summary>
    /// <remarks>
    /// 刻意不写成严格的正则：这个值同时用于 bundle identifier 与注册表键，判错会把明明能用的标识拒掉。
    /// </remarks>
    internal static bool LooksLikeReverseDomainId(string id)
        => id.Contains(".", StringComparison.Ordinal)
           && !id.StartsWith(".", StringComparison.Ordinal)
           && !id.EndsWith(".", StringComparison.Ordinal)
           && id.All(character => char.IsLetterOrDigit(character) || character is '.' or '-' or '_');

    /// <summary>
    /// 从发布目录里的主可执行文件读文件版本（<c>--version</c> 没给时的兜底）。
    /// </summary>
    /// <remarks>
    /// 读的是**文件版本**而不是程序集版本：AOT 产物的版本就是发布时写进去的那个，
    /// 而"忘了传 --version"是这条命令最常见的用法错误——能从产物读出来就不该让它失败。
    /// <para>
    /// <b>只在 Windows 上真的有效</b>：Linux/macOS 的 AOT 产物是 ELF / Mach-O，没有 PE 的版本资源，
    /// 实测读出来是空的（于是会走到"请显式给 --version"那条错误）。CI 与 release 里都显式传了版本，
    /// 这里保留兜底是为了本机打包时少一个必填项。
    /// </para>
    /// </remarks>
    private static string? TryReadVersionFromPublishOutput(string publishDirectory, BundlePlatform platform)
    {
        string[] candidates = platform == BundlePlatform.Windows
            ? Directory.EnumerateFiles(publishDirectory, "*.exe").OrderBy(p => p, StringComparer.Ordinal).ToArray()
            : Directory.EnumerateFiles(publishDirectory)
                .Where(path => (File.GetAttributes(path) & FileAttributes.Directory) == 0)
                .Where(path => Path.GetExtension(path).Length == 0)
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToArray();

        foreach (string candidate in candidates)
        {
            FileVersionInfo info = FileVersionInfo.GetVersionInfo(candidate);
            string? version = info.FileVersion ?? info.ProductVersion;
            if (!string.IsNullOrWhiteSpace(version))
            {
                return version.Trim();
            }
        }

        return null;
    }
}
