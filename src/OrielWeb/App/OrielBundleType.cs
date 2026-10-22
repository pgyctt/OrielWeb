using System.Reflection;

namespace OrielWeb;

/// <summary>
/// 推断"这个应用是被什么形态打包/安装的"（对标 Tauri 的 <c>App::get_bundle_type()</c>）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么需要推断而不是读配置</b>：Tauri 的打包器在编译期把 <c>TAURI_BUNDLE_TYPE</c>
/// 塞进二进制，所以它天生知道答案。本库**不是打包器**（打包交给 Velopack，见 API.md §12），
/// 编译期没有"产物类型"这个概念——同一个 <c>dotnet publish</c> 的输出既可能被塞进 Setup.exe，
/// 也可能被 zip 起来手工解压。能观察到的只有**运行期事实**：自己是不是在一个 <c>.app</c> 里、
/// 旁边有没有 Velopack 的 <c>Update.exe</c>、环境里有没有 <c>APPIMAGE</c>。
/// </para>
/// <para>
/// <b>不编值</b>：观察不出来的形态一律返回 <c>null</c>（对应 Tauri 的"不是打包产物"），
/// 而不是按平台猜一个。迁移代码会依据这个值分支（"是不是 msi 安装的"），
/// 一个猜出来的答案会让它走错分支——这比 <c>null</c> 难查得多。
/// </para>
/// <para>
/// <b>想精确控制就显式声明</b>：应用可以在 csproj 里写
/// <c>&lt;AssemblyMetadata Include="OrielBundleType" Value="nsis" /&gt;</c>，
/// 本类优先读它（见 <see cref="MetadataKey"/>）。这是给"我知道自己怎么打包"的应用准备的，
/// 也是本库唯一能支持 <c>msi</c> / <c>deb</c> / <c>rpm</c> 这几格的途径——
/// 那几种安装形态在运行期彼此无法区分（装完之后留在磁盘上的东西是一样的）。
/// </para>
/// </remarks>
internal static class OrielBundleType
{
    /// <summary>程序集元数据里"显式声明产物类型"的键名。</summary>
    internal const string MetadataKey = "OrielBundleType";

    /// <summary>与本库 JS 侧 <c>BundleType</c> 同值的全部取值（也是 Tauri 的全部取值）。</summary>
    internal static readonly string[] Known = ["deb", "rpm", "appimage", "nsis", "msi", "app"];

    /// <summary>
    /// 推断当前应用的产物类型；观察不出来时返回 <c>null</c>。
    /// </summary>
    /// <remarks>
    /// 只做一次（结果缓存在 <see cref="OrielApp.BundleType"/>）：这些事实在整个进程生命期内
    /// 都不会变，而它可能被页面反复查询。
    /// </remarks>
    internal static string? Detect()
    {
        // 元数据读的是**入口程序集**（应用自己的），不是本库的：声明写在应用的 csproj 里。
        Assembly assembly = Assembly.GetEntryAssembly() ?? typeof(OrielBundleType).Assembly;

        string? declared = null;
        foreach (AssemblyMetadataAttribute attribute in
                 assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
        {
            if (string.Equals(attribute.Key, MetadataKey, StringComparison.OrdinalIgnoreCase))
            {
                declared = attribute.Value;
                break;
            }
        }

        return Detect(
            declared,
            AppContext.BaseDirectory,
            OperatingSystem.IsMacOS(),
            OperatingSystem.IsWindows(),
            Environment.GetEnvironmentVariable("APPIMAGE"));
    }

    /// <summary>
    /// 纯函数形态的推断（可单测）。
    /// </summary>
    /// <param name="declared">程序集元数据里的显式声明（可为 <c>null</c>）。</param>
    /// <param name="baseDirectory">可执行文件所在目录（<see cref="AppContext.BaseDirectory"/>）。</param>
    /// <param name="isMacOS">是否 macOS。</param>
    /// <param name="isWindows">是否 Windows。</param>
    /// <param name="appImagePath">AppImage 运行时的 <c>APPIMAGE</c> 环境变量（可为 null）。</param>
    /// <remarks>
    /// 判定顺序：**显式声明 &gt; 运行期观察**。显式声明优先是因为它由应用自己写，
    /// 比任何推断都准；而"观察"里 macOS 的 <c>.app</c> 最确定（路径就是证据），
    /// 其次才是 Velopack 的布局与 AppImage 的环境变量。
    /// </remarks>
    internal static string? Detect(
        string? declared,
        string baseDirectory,
        bool isMacOS,
        bool isWindows,
        string? appImagePath)
    {
        if (Normalize(declared) is { } explicitType)
        {
            return explicitType;
        }

        if (isMacOS && LooksLikeMacBundle(baseDirectory))
        {
            // 在 .app 里跑 = 这就是一个 app bundle。至于是 dmg 装的还是 zip 解开的，
            // 运行期看不出来（也不影响任何行为），所以不猜 dmg。
            return "app";
        }

        if (isWindows && LooksLikeVelopackLayout(baseDirectory))
        {
            // Velopack 在 Windows 上的安装器是 Setup.exe（不是 msi）。Tauri 的枚举里
            // 没有 "setup"，最接近的是 nsis——两者都是"exe 安装器"这一档。
            // 用 msi 装的 Velopack 应用在运行期与 Setup.exe 装的**完全一样**，
            // 所以那种情况报不出 msi（要报请用程序集元数据显式声明）。
            return "nsis";
        }

        if (!string.IsNullOrWhiteSpace(appImagePath))
        {
            return "appimage";
        }

        // 开发期 / 手工解压 / 单文件发布：没有可观察的打包形态。Tauri 在 `tauri dev`
        // 与未打包的二进制上也返回 null，这里与它一致。
        return null;
    }

    /// <summary>把声明值归一成 <see cref="Known"/> 里的取值；不认识时返回 <c>null</c>。</summary>
    internal static string? Normalize(string? declared)
    {
        if (string.IsNullOrWhiteSpace(declared))
        {
            return null;
        }

        string value = declared.Trim().ToLowerInvariant();
        foreach (string known in Known)
        {
            if (string.Equals(known, value, StringComparison.Ordinal))
            {
                return known;
            }
        }

        OrielDiagnostics.Report(
            $"[OrielWeb] 程序集元数据 {MetadataKey}=\"{declared}\" 不是已知的产物类型"
            + $"（{string.Join(" / ", Known)}），已忽略并改用运行期推断。");
        return null;
    }

    /// <summary>可执行文件是不是在一个 macOS <c>.app</c> 包里（<c>…/X.app/Contents/MacOS/</c>）。</summary>
    internal static bool LooksLikeMacBundle(string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            return false;
        }

        // 归一化分隔符后做子串判断：Windows 上构造出来的路径可能用 '\'，
        // 而 .app 包在 macOS 上永远是 '/'。
        string normalized = baseDirectory.Replace('\\', '/');
        return normalized.Contains(".app/Contents/MacOS", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 目录像不像 Velopack 装出来的布局（安装根目录里有 <c>Update.exe</c>）。
    /// </summary>
    /// <remarks>
    /// Velopack 的 Windows 布局是 <c>…\&lt;AppId&gt;\Update.exe</c> +
    /// <c>…\&lt;AppId&gt;\current\&lt;App&gt;.exe</c>，所以可执行文件所在目录**和它的上一级**
    /// 都要看：开发时直接跑 <c>bin\</c> 里的 exe 不会命中，安装后才命中。
    /// </remarks>
    internal static bool LooksLikeVelopackLayout(string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            return false;
        }

        string directory;
        try
        {
            directory = Path.GetFullPath(baseDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        if (HasUpdateExe(directory))
        {
            return true;
        }

        string? parent = Path.GetDirectoryName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return parent is not null && HasUpdateExe(parent);
    }

    private static bool HasUpdateExe(string directory)
    {
        try
        {
            return File.Exists(Path.Combine(directory, "Update.exe"));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
