namespace OrielWeb.Cli;

/// <summary>
/// 工具版本：程序集版本的前三段。
/// </summary>
/// <remarks>
/// 与库注入给页面的 <c>oriel.version</c> 同一套口径（都取 <c>Major.Minor.Build</c>）：
/// 版本号只有一个来源（根 <c>Directory.Build.props</c> 的 <c>&lt;Version&gt;</c>），
/// 工具与库同版本发布，<c>oriel version</c> 打出来的应当与包版本一致。
/// </remarks>
internal static class CliVersion
{
    internal static string Value { get; } = Build();

    private static string Build()
    {
        Version? version = typeof(CliVersion).Assembly.GetName().Version;
        return version is null
            ? "0.0.0"
            : $"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}";
    }
}
