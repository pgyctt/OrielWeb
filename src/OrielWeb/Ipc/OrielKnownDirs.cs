using System.Runtime.InteropServices;

namespace OrielWeb.Ipc;

/// <summary>
/// 目录解析：把 Tauri 的 <c>BaseDirectory</c> 每个取值解成宿主上的绝对路径。
/// </summary>
/// <remarks>
/// <para>
/// <b>规则逐格对齐 Tauri 的 <c>tauri::path::PathResolver</c></b>（底层是 Rust 的
/// <c>dirs</c> crate：Windows 走 <c>SHGetKnownFolderPath</c>、Linux 走 XDG、
/// macOS 走 <c>~/Library/*</c>）。逐格对齐而不是"用 .NET 的 <c>SpecialFolder</c> 差不多就行"，
/// 因为**实测有几格不一样**：
/// </para>
/// <list type="bullet">
///   <item><description><c>downloadDir</c>：.NET 没有对应的 <c>SpecialFolder</c> 成员；
///     Tauri 给 <c>{FOLDERID_Downloads}</c>。</description></item>
///   <item><description><c>publicDir</c>：.NET 的 <c>CommonDocuments</c> 给
///     <c>C:\Users\Public\Documents</c>，而 Tauri 的 <c>{FOLDERID_Public}</c> 是
///     <c>C:\Users\Public</c>——**差一层目录**。</description></item>
///   <item><description><c>fontDir</c> / <c>runtimeDir</c> / <c>executableDir</c>：Tauri 在
///     Windows 上明确不支持（调用报错），而 .NET 能给出 <c>fontDir</c> 的答案。
///     这里**照 Tauri 拒绝**：多给一个值会让"能跑"与"能迁"分叉，
///     而分叉在迁移完成之后才发现是最糟的时机。</description></item>
/// </list>
/// <para>
/// <b>哪些格子实测过</b>：Windows 全部（<c>KnownDirTests</c> 对着 <c>FOLDERID_*</c> 的期望断言）。
/// Linux 与 macOS 按上表实现但**未在真机验证**，已记入 CHANGELOG 的"已知限制"。
/// </para>
/// </remarks>
internal static partial class OrielKnownDirs
{
    /// <summary>解析一个目录；不支持或取不到时抛 <see cref="OrielIpcException"/>。</summary>
    /// <param name="directory">目录名，与 Tauri 的 <c>BaseDirectory</c> 取值同名。</param>
    /// <param name="appIdentifier">应用标识符，用于 <c>App*</c> 那几个目录。</param>
    internal static string Resolve(string directory, string appIdentifier)
    {
        string? value = TryResolve(directory, appIdentifier, out string? reason);

        return value ?? throw new OrielIpcException(
            reason ?? $"无法解析目录「{directory}」。")
        {
            // 区分"这个平台没这个概念"（not_supported，调用方该按平台分支）
            // 与"参数不认识"（invalid_argument，那是拼错了）。
            Code = reason is null || reason.StartsWith("未知的目录名", StringComparison.Ordinal)
                ? OrielIpcErrorCodes.InvalidArgument
                : OrielIpcErrorCodes.NotSupported,
        };
    }

    /// <summary>解析一个目录；取不到时返回 <c>null</c> 并由 <paramref name="reason"/> 说明原因。</summary>
    private static string? TryResolve(string directory, string appIdentifier, out string? reason)
    {
        reason = null;

        switch (directory)
        {
            case "Home":
                return Home();

            case "Temp":
                return Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);

            case "Config":
                return ConfigDir();

            // Data 与 LocalData 在 Linux/macOS 上是同一个目录，在 Windows 上**不是**
            // （Roaming 与 Local）。所以不能共用一条实现——共用会让 Windows 上的
            // dataDir 与 localDataDir 双双错一个，而那种错在别的平台上完全看不出来。
            case "Data":
                return DataDir();

            case "LocalData":
                return LocalDataDir();

            case "Cache":
                return CacheDir();

            case "Desktop":
                return UserDir("Desktop", "XDG_DESKTOP_DIR", KnownFolder.Desktop);
            case "Document":
                return UserDir("Documents", "XDG_DOCUMENTS_DIR", KnownFolder.Documents);
            case "Download":
                return UserDir("Downloads", "XDG_DOWNLOAD_DIR", KnownFolder.Downloads);
            case "Picture":
                return UserDir("Pictures", "XDG_PICTURES_DIR", KnownFolder.Pictures);
            case "Video":
                return UserDir("Movies", "XDG_VIDEOS_DIR", KnownFolder.Videos);
            case "Audio":
                return UserDir("Music", "XDG_MUSIC_DIR", KnownFolder.Music);
            case "Public":
                return UserDir("Public", "XDG_PUBLICSHARE_DIR", KnownFolder.Public);
            case "Template":
                return UserDir("Templates", "XDG_TEMPLATES_DIR", KnownFolder.Templates);

            case "Font":
                return FontDir(out reason);
            case "Runtime":
                return RuntimeDir(out reason);
            case "Executable":
                return ExecutableDir(out reason);

            case "Resource":
                return ResourceDir();

            case "AppConfig":
                return UnderAppDir(ConfigDir(), appIdentifier, out reason);
            case "AppData":
                return UnderAppDir(DataDir(), appIdentifier, out reason);
            case "AppLocalData":
                return UnderAppDir(LocalDataDir(), appIdentifier, out reason);
            case "AppCache":
                return UnderAppDir(CacheDir(), appIdentifier, out reason);

            // appLogDir 是唯一一个三平台规则都不同的：macOS 落在 ~/Library/Logs（**不在**
            // 应用数据目录下），另两个平台落在"本地数据 / 标识符 / logs"。Tauri 就是这么分的。
            case "AppLog":
                return OperatingSystem.IsMacOS()
                    ? UnderAppDir(Path.Combine(Home(), "Library", "Logs"), appIdentifier, out reason)
                    : UnderAppDir(Path.Combine(UnderAppDir(LocalDataDir(), appIdentifier, out reason) ?? "", "logs"),
                                  appIdentifier: null, out reason);

            default:
                reason = $"未知的目录名「{directory}」。";
                return null;
        }
    }

    /// <summary>把应用标识符接到某个根目录下；<paramref name="appIdentifier"/> 为 null 表示已经接过了。</summary>
    private static string? UnderAppDir(string? root, string? appIdentifier, out string? reason)
    {
        reason = null;

        if (root is null)
        {
            reason = "上层目录无法解析。";
            return null;
        }

        return appIdentifier is null ? root : Path.Combine(root, appIdentifier);
    }

    // ---------------------------------------------------------------- 平台目录

    /// <summary>家目录。<c>{FOLDERID_Profile}</c> / <c>$HOME</c>，三平台同名同义。</summary>
    private static string Home()
    {
        string? home = Environment.GetEnvironmentVariable("HOME");

        return FirstNonEmpty(
            OperatingSystem.IsWindows() ? WindowsKnownFolder(KnownFolder.Profile) : home,
            home,
            Environment.GetEnvironmentVariable("USERPROFILE"),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Directory.GetCurrentDirectory())!;
    }

    /// <summary>配置目录。<c>{FOLDERID_RoamingAppData}</c> / <c>$XDG_CONFIG_HOME</c> / <c>~/Library/Application Support</c>。</summary>
    private static string? ConfigDir() => OperatingSystem.IsWindows()
        ? WindowsKnownFolder(KnownFolder.RoamingAppData)
        : OperatingSystem.IsMacOS()
            ? Path.Combine(Home(), "Library", "Application Support")
            : Xdg("XDG_CONFIG_HOME", ".config");

    /// <summary>数据目录。<c>{FOLDERID_RoamingAppData}</c> / <c>$XDG_DATA_HOME</c> / <c>~/Library/Application Support</c>。</summary>
    private static string? DataDir() => OperatingSystem.IsWindows()
        ? WindowsKnownFolder(KnownFolder.RoamingAppData)
        : OperatingSystem.IsMacOS()
            ? Path.Combine(Home(), "Library", "Application Support")
            : Xdg("XDG_DATA_HOME", ".local/share");

    /// <summary>本地数据目录。<c>{FOLDERID_LocalAppData}</c>；另两个平台与 <see cref="DataDir"/> 相同。</summary>
    private static string? LocalDataDir() => OperatingSystem.IsWindows()
        ? WindowsKnownFolder(KnownFolder.LocalAppData)
        : DataDir();

    /// <summary>缓存目录。<c>{FOLDERID_LocalAppData}</c> / <c>$XDG_CACHE_HOME</c> / <c>~/Library/Caches</c>。</summary>
    private static string? CacheDir() => OperatingSystem.IsWindows()
        ? WindowsKnownFolder(KnownFolder.LocalAppData)
        : OperatingSystem.IsMacOS()
            ? Path.Combine(Home(), "Library", "Caches")
            : Xdg("XDG_CACHE_HOME", ".cache");

    /// <summary>XDG Base Directory：先读环境变量，没有就拼在家目录下。</summary>
    private static string Xdg(string variable, string relativeFallback)
    {
        string? value = Environment.GetEnvironmentVariable(variable);

        return !string.IsNullOrWhiteSpace(value) && Path.IsPathFullyQualified(value)
            ? value
            : Path.Combine(Home(), relativeFallback.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>
    /// 用户目录（桌面/文档/下载/…），三平台来源完全不同。
    /// </summary>
    /// <param name="macName">macOS 上的固定目录名（macOS 没有 xdg-user-dirs 的等价物，Tauri 也写死）。</param>
    /// <param name="xdgVariable">Linux 上 <c>user-dirs.dirs</c> 里的变量名。</param>
    /// <param name="windowsFolder">Windows 上对应的 <c>FOLDERID_*</c>。</param>
    /// <remarks>
    /// <para>
    /// <b>第三个参数用枚举而不是字符串</b>：这里曾经用 <c>Enum.Parse&lt;KnownFolder&gt;(name)</c>，
    /// 而 <c>Video</c> 那格把 <c>macName</c> 与 <c>windowsFolder</c> 写反了
    /// （<c>"Videos", "XDG_VIDEOS_DIR", "Movies"</c>）——运行到那一格才炸，
    /// 而炸出来的是 <c>ArgumentException: Requested value 'Movies' was not found</c>，
    /// 与"目录解析"这个话题看不出关系。用枚举之后同样的笔误是编译错误。
    /// </para>
    /// <para>
    /// xdg-user-dirs 把"桌面在哪"做成了用户可配置项（中文系统上常改成 <c>~/桌面</c>、<c>~/下载</c>），
    /// 所以**不能**假定 <c>~/Desktop</c>。文件里的值形如
    /// <c>XDG_DESKTOP_DIR="$HOME/桌面"</c>——含 <c>$HOME</c>，要展开。
    /// </para>
    /// <para>
    /// Windows 的已知文件夹同样可以被重定向到别的盘，所以也不能拼字符串。
    /// </para>
    /// </remarks>
    private static string? UserDir(string macName, string xdgVariable, KnownFolder windowsFolder)
    {
        if (OperatingSystem.IsWindows())
        {
            return WindowsKnownFolder(windowsFolder);
        }

        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(Home(), macName);
        }

        return ReadXdgUserDir(xdgVariable) ?? Path.Combine(Home(), macName);
    }

    /// <summary>从 <c>~/.config/user-dirs.dirs</c> 读一个 <c>XDG_*_DIR</c> 值。</summary>
    /// <remarks>
    /// 解析失败（文件不存在、格式不认识、值是相对路径）时返回 <c>null</c>，由调用方退回默认值。
    /// **不抛异常**：这个文件是用户可编辑的，一份写坏的配置不该让"取桌面目录"整个失败。
    /// </remarks>
    private static string? ReadXdgUserDir(string variable)
    {
        try
        {
            string path = Path.Combine(Home(), ".config", "user-dirs.dirs");
            if (!File.Exists(path))
            {
                return null;
            }

            foreach (string line in File.ReadLines(path))
            {
                string trimmed = line.Trim();
                if (!trimmed.StartsWith(variable + "=", StringComparison.Ordinal))
                {
                    continue;
                }

                string raw = trimmed[(variable.Length + 1)..].Trim().Trim('"');
                if (raw.Length == 0)
                {
                    return null;
                }

                string expanded = raw
                    .Replace("${HOME}", Home(), StringComparison.Ordinal)
                    .Replace("$HOME", Home(), StringComparison.Ordinal);

                return Path.IsPathFullyQualified(expanded) ? expanded : null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 读不了就当没有配置——不因为这个文件让整个调用失败
        }

        return null;
    }

    /// <summary>字体目录：Linux <c>$XDG_DATA_HOME/fonts</c>、macOS <c>~/Library/Fonts</c>、Windows **不支持**。</summary>
    private static string? FontDir(out string? reason)
    {
        reason = null;

        if (OperatingSystem.IsWindows())
        {
            reason = "fontDir 在 Windows 上不存在（与 Tauri 一致，请按平台分支处理）。";
            return null;
        }

        return OperatingSystem.IsMacOS()
            ? Path.Combine(Home(), "Library", "Fonts")
            : Path.Combine(Xdg("XDG_DATA_HOME", ".local/share"), "fonts");
    }

    /// <summary>运行时目录：只有 Linux 有（<c>$XDG_RUNTIME_DIR</c>）。</summary>
    private static string? RuntimeDir(out string? reason)
    {
        reason = null;

        if (!OperatingSystem.IsLinux())
        {
            reason = "runtimeDir 只在 Linux 上存在（$XDG_RUNTIME_DIR），与 Tauri 一致。";
            return null;
        }

        string? value = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (!string.IsNullOrWhiteSpace(value) && Path.IsPathFullyQualified(value))
        {
            return value;
        }

        // 变量缺失时**不猜**（比如退到 /tmp）：/tmp 是全局共享的，不是"本用户的运行时目录"
        // 那个语义。给一个错的值比给一个明确的失败糟得多——调用方会把 socket 放进 /tmp。
        reason = "环境变量 XDG_RUNTIME_DIR 未设置，无法确定运行时目录。";
        return null;
    }

    /// <summary>可执行文件目录：只有 Linux 有（<c>$XDG_BIN_HOME</c> 或 <c>~/.local/bin</c>）。</summary>
    private static string? ExecutableDir(out string? reason)
    {
        reason = null;

        if (!OperatingSystem.IsLinux())
        {
            reason = "executableDir 只在 Linux 上存在（$XDG_BIN_HOME 或 ~/.local/bin），与 Tauri 一致。";
            return null;
        }

        string? bin = Environment.GetEnvironmentVariable("XDG_BIN_HOME");
        if (!string.IsNullOrWhiteSpace(bin) && Path.IsPathFullyQualified(bin))
        {
            // Tauri 的规则是 $XDG_BIN_HOME/../bin
            return Path.GetFullPath(Path.Combine(bin, "..", "bin"));
        }

        DirectoryInfo? parent = Directory.GetParent(Xdg("XDG_DATA_HOME", ".local/share"));
        return parent is null
            ? Path.Combine(Home(), ".local", "bin")
            : Path.Combine(parent.FullName, "bin");
    }

    /// <summary>
    /// 资源目录（随应用分发的额外文件，如数据库模板、脚本）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 三平台规则不同（照 Tauri）：Windows 是主可执行文件所在目录；macOS 是
    /// <c>${exe_dir}/../Resources</c>（.app 包内布局）；Linux 是
    /// <c>$APPDIR/usr/lib/{exe}</c>（AppImage）或 <c>/usr/lib/{exe}</c>。
    /// </para>
    /// <para>
    /// Linux 下**先看可执行文件旁边有没有 <c>../lib/{exe}</c>**：那是开发期
    /// （<c>target/{debug,release}/</c>）与自包含发布的布局，判它比判"是不是装在系统里"可靠。
    /// 内嵌前端资源不走这里（它们经 <c>oriel://</c> 提供，本来就不落盘）。
    /// </para>
    /// </remarks>
    private static string ResourceDir()
    {
        string exeDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        string exeName = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "app");

        if (OperatingSystem.IsMacOS())
        {
            return Path.GetFullPath(Path.Combine(exeDir, "..", "Resources"));
        }

        if (!OperatingSystem.IsWindows())
        {
            string? appDir = Environment.GetEnvironmentVariable("APPDIR");
            if (!string.IsNullOrWhiteSpace(appDir))
            {
                return Path.Combine(appDir, "usr", "lib", exeName);
            }

            string sibling = Path.GetFullPath(Path.Combine(exeDir, "..", "lib", exeName));
            return Directory.Exists(sibling) ? sibling : Path.Combine("/usr", "lib", exeName);
        }

        return exeDir;
    }

    // ---------------------------------------------------------------- Windows 已知文件夹

    /// <summary>本库用到的 Windows 已知文件夹。</summary>
    /// <remarks>
    /// 只列出真的需要的那些。特别地 <see cref="Downloads"/> 与 <see cref="Public"/> 必须自己调
    /// <c>SHGetKnownFolderPath</c>：.NET 的 <see cref="Environment.SpecialFolder"/> 里
    /// 没有可靠的 <c>Downloads</c> 成员，而 <c>CommonDocuments</c> 给的是
    /// <c>...\Public\Documents</c>（比 <c>FOLDERID_Public</c> 多一层）。
    /// </remarks>
    private enum KnownFolder
    {
        Profile,
        Desktop,
        Documents,
        Downloads,
        Pictures,
        Videos,
        Music,
        Public,
        Templates,
        RoamingAppData,
        LocalAppData,
    }

    /// <summary>
    /// 枚举 → <c>KNOWNFOLDERID</c> 的 GUID。
    /// </summary>
    /// <remarks>
    /// 这些是 Windows SDK 里的常量值，必须逐字节正确——写错一个字符拿到的是
    /// <c>E_INVALIDARG</c>，而症状只是"某个目录解析失败"，看不出是 GUID 写错了。
    /// 有一条测试逐项把它们与 <c>SHGetKnownFolderPath</c> 的结果对齐（见 KnownDirTests）。
    /// </remarks>
    private static Guid FolderGuid(KnownFolder folder) => folder switch
    {
        KnownFolder.Profile => new Guid("5E6C858F-0E22-4760-9AFE-EA3317B67173"),
        KnownFolder.Desktop => new Guid("B4BFCC3A-DB2C-424C-B029-7FE99A87C641"),
        KnownFolder.Documents => new Guid("FDD39AD0-238F-46AF-ADB4-6C85480369C7"),
        KnownFolder.Downloads => new Guid("374DE290-123F-4565-9164-39C4925E467B"),
        KnownFolder.Pictures => new Guid("33E28130-4E1E-4676-835A-98395C3BC3BB"),
        KnownFolder.Videos => new Guid("18989B1D-99B5-455B-841C-AB7C74E4DDFC"),
        KnownFolder.Music => new Guid("4BD8D571-6D19-48D3-BE97-422220080E43"),
        KnownFolder.Public => new Guid("DFDF76A2-C82A-4D63-906A-5644AC457385"),
        KnownFolder.Templates => new Guid("A63293E8-664E-48DB-A079-DF759E0509F7"),
        KnownFolder.RoamingAppData => new Guid("3EB685DB-65F9-4CF6-A03A-E3EF65729F3D"),
        KnownFolder.LocalAppData => new Guid("F1B32785-6FBA-4FCF-9D55-7B8E7F157091"),
        _ => throw new ArgumentOutOfRangeException(nameof(folder), folder, "未映射的已知文件夹。"),
    };

    [LibraryImport("shell32.dll")]
    private static partial int SHGetKnownFolderPath(in Guid rfid, uint dwFlags, nint hToken, out nint path);

    [LibraryImport("ole32.dll")]
    private static partial void CoTaskMemFree(nint pv);

    /// <summary>取一个 Windows 已知文件夹；失败时返回 <c>null</c>（调用方据此给 not_supported）。</summary>
    /// <remarks>
    /// <c>dwFlags = 0</c> 要求目录存在；实测即使 Downloads 被删掉，它也会返回**路径**
    /// （不要求目录真的在），所以这里不需要 <c>KF_FLAG_DONT_VERIFY</c>。
    /// 失败时返回 null 而不是抛：上层据此统一报 not_supported，错误码只有一处来源。
    /// </remarks>
    private static string? WindowsKnownFolder(KnownFolder folder)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        Guid id = FolderGuid(folder);
        nint buffer = 0;

        try
        {
            return SHGetKnownFolderPath(in id, 0, 0, out buffer) < 0 || buffer == 0
                ? null
                : Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            if (buffer != 0)
            {
                CoTaskMemFree(buffer);
            }
        }
    }

    // ---------------------------------------------------------------- 小工具

    private static string? FirstNonEmpty(params string?[] candidates)
    {
        foreach (string? candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>给测试用：当前平台能否解析这个目录（不抛异常）。</summary>
    internal static bool IsSupported(string directory, string appIdentifier)
        => TryResolve(directory, appIdentifier, out _) is not null;

    /// <summary>给测试用：解析结果或"不支持"的原因（不抛异常）。</summary>
    internal static string Describe(string directory, string appIdentifier)
    {
        string? value = TryResolve(directory, appIdentifier, out string? reason);
        return value ?? $"（不支持：{reason}）";
    }

    /// <summary>全部可解析的目录名（与 Tauri 的 <c>BaseDirectory</c> 取值一一对应）。</summary>
    /// <remarks>
    /// 这份清单同时是<b>命令注册表的来源</b>（见 <see cref="OrielBuiltInPathCommands"/>），
    /// 所以"有哪些目录"只有一个地方定义——漏一个的症状会是"某个 <c>path.xxxDir</c> 命令
    /// 静默地不存在"，而那看起来像命令没授权。
    /// </remarks>
    internal static readonly string[] AllDirectories =
    [
        "AppCache", "AppConfig", "AppData", "AppLocalData", "AppLog",
        "Audio", "Cache", "Config", "Data", "Desktop", "Document", "Download",
        "Executable", "Font", "Home", "LocalData", "Picture", "Public",
        "Resource", "Runtime", "Temp", "Template", "Video",
    ];
}
