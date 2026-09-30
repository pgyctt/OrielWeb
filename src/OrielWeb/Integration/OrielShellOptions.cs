namespace OrielWeb;

/// <summary>
/// Shell 集成的选项（打开外部链接时的 scheme 白名单）。
/// </summary>
public sealed class OrielShellOptions
{
    /// <summary>
    /// 允许 <see cref="OrielApp.OpenExternal"/> 打开的 URL scheme（小写、不带冒号）。
    /// 默认只放 <c>http</c>/<c>https</c>/<c>mailto</c>——<c>file:</c> 与 <c>javascript:</c>
    /// 是这类 API 最常被滥用的两个面，默认拒绝。
    /// </summary>
    public IReadOnlyList<string> AllowedSchemes { get; set; } = DefaultSchemes;

    /// <summary>默认白名单：<c>http</c>、<c>https</c>、<c>mailto</c>。</summary>
    public static IReadOnlyList<string> DefaultSchemes { get; } = ["http", "https", "mailto"];

    /// <summary>追加一个 scheme（大小写不敏感；去掉可能带的冒号）。</summary>
    public OrielShellOptions WithScheme(string scheme)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scheme);
        string normalized = scheme.Trim().TrimEnd(':').ToLowerInvariant();

        var schemes = new List<string>(AllowedSchemes);
        if (!schemes.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            schemes.Add(normalized);
            AllowedSchemes = schemes;
        }

        return this;
    }
}
