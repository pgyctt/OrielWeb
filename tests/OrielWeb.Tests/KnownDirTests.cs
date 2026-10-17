using System.Runtime.InteropServices;
using OrielWeb.Ipc;
using Xunit;

namespace OrielWeb.Tests;

/// <summary>
/// 目录解析（F4b-1）：逐格对照 Tauri 的 <c>BaseDirectory</c> 映射。
/// </summary>
/// <remarks>
/// <para>
/// 这组测试的思路是**不信任间接层**：断言时直接用 <c>SHGetKnownFolderPath</c> 独立算一遍
/// 期望值，而不是断言我们在 <c>OrielKnownDirs</c> 里写的那个 GUID 或那个枚举。
/// 否则"实现和测试抄同一份错的映射"会双双通过——而那正是这类代码最容易出的错。
/// </para>
/// <para>
/// 尤其是 <c>downloadDir</c> 与 <c>publicDir</c> 这两格：它们之所以要自己调
/// <c>SHGetKnownFolderPath</c>，就是因为 .NET 的 <see cref="Environment.SpecialFolder"/>
/// 给不出正确值（前者没有成员，后者多一层 <c>\Documents</c>）。
/// 所以测试里也**不能**用 <see cref="Environment.SpecialFolder"/> 当期望值。
/// </para>
/// <para>
/// 平台相关的断言用 <c>OperatingSystem.IsWindows()</c> 守卫：Linux/macOS 的格子按 Tauri
/// 的表实现了但没在真机验过，这里只断言"形状"（绝对路径、非空、与标识符拼接正确）。
/// </para>
/// </remarks>
public sealed partial class KnownDirTests
{
    private const string AppId = "com.example.app";

    // ---- Windows：逐格对照 FOLDERID ----

    [Fact]
    public void Windows_HomeIsFolderIdProfile()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Equal(KnownFolderPath(ProfileId), Resolve("Home"));
    }

    [Theory]
    [InlineData("Desktop", "B4BFCC3A-DB2C-424C-B029-7FE99A87C641")]
    [InlineData("Document", "FDD39AD0-238F-46AF-ADB4-6C85480369C7")]
    [InlineData("Download", "374DE290-123F-4565-9164-39C4925E467B")]
    [InlineData("Picture", "33E28130-4E1E-4676-835A-98395C3BC3BB")]
    [InlineData("Video", "18989B1D-99B5-455B-841C-AB7C74E4DDFC")]
    [InlineData("Audio", "4BD8D571-6D19-48D3-BE97-422220080E43")]
    [InlineData("Public", "DFDF76A2-C82A-4D63-906A-5644AC457385")]
    [InlineData("Template", "A63293E8-664E-48DB-A079-DF759E0509F7")]
    public void Windows_UserDirsMatchTheirFolderId(string directory, string guid)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Equal(KnownFolderPath(new Guid(guid)), Resolve(directory));
    }

    [Fact]
    public void Windows_PublicIsNotCommonDocuments()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // 这一格是**必须自己调 SHGetKnownFolderPath 的理由**：
        // .NET 的 CommonDocuments 给 "...\Public\Documents"，比 FOLDERID_Public 多一层。
        // 若哪天有人图省事把实现换成 Environment.SpecialFolder.CommonDocuments，
        // 这条会立刻变红。
        string publicDir = Resolve("Public");
        string commonDocuments = Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments);

        Assert.Equal(KnownFolderPath(new Guid("DFDF76A2-C82A-4D63-906A-5644AC457385")), publicDir);
        Assert.NotEqual(commonDocuments, publicDir);
    }

    [Fact]
    public void Windows_DownloadsIsNotGuessedFromUserProfile()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // 另一格必须自己调的理由：.NET 没有 Downloads 成员。
        // 常见替代是拼 "$USERPROFILE\Downloads"——本机恰好相等，所以这条**不能**断言不等，
        // 只能断言"等于 FOLDERID 的值"。用户在属性里改过位置时它才暴露差别。
        Assert.Equal(
            KnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B")),
            Resolve("Download"));
    }

    [Fact]
    public void Windows_ConfigAndDataAreRoaming_LocalDataAndCacheAreLocal()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string roaming = KnownFolderPath(new Guid("3EB685DB-65F9-4CF6-A03A-E3EF65729F3D"));
        string local = KnownFolderPath(new Guid("F1B32785-6FBA-4FCF-9D55-7B8E7F157091"));

        // Windows 上这四格分成两对，**不是**同一个目录——把 Data 与 LocalData
        // 走同一条实现会让其中一对双双错掉，而那种错在 Linux/macOS 上完全看不出来。
        Assert.Equal(roaming, Resolve("Config"));
        Assert.Equal(roaming, Resolve("Data"));
        Assert.Equal(local, Resolve("LocalData"));
        Assert.Equal(local, Resolve("Cache"));
    }

    [Fact]
    public void Windows_TempIsTheProcessTempPath()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Equal(
            Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar),
            Resolve("Temp"));
    }

    [Theory]
    [InlineData("Font")]
    [InlineData("Runtime")]
    [InlineData("Executable")]
    public void Windows_UnsupportedDirsReportNotSupported(string directory)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // 照 Tauri 拒绝：它在这三格上明确"不支持"。多给一个值会让"能跑"与"能迁"分叉，
        // 而那是迁移完成之后才会发现的差异。
        Assert.False(OrielKnownDirs.IsSupported(directory, AppId));

        OrielIpcException ex = Assert.Throws<OrielIpcException>(() => Resolve(directory));
        Assert.Equal(OrielIpcErrorCodes.NotSupported, ex.Code);
    }

    // ---- App* 目录：一律是"根 / 标识符" ----

    [Theory]
    [InlineData("AppConfig", "Config")]
    [InlineData("AppData", "Data")]
    [InlineData("AppLocalData", "LocalData")]
    [InlineData("AppCache", "Cache")]
    public void AppDirsAreRootSlashIdentifier(string appDir, string baseDir)
    {
        Assert.Equal(
            Path.Combine(Resolve(baseDir), AppId),
            Resolve(appDir));
    }

    [Fact]
    public void AppLogDirIsPlatformSpecific()
    {
        // 三平台里唯一一个规则各不相同的：macOS 落在 ~/Library/Logs（**不在**
        // 应用数据目录下），另两个平台落在"本地数据 / 标识符 / logs"。
        string expected = OperatingSystem.IsMacOS()
            ? Path.Combine(Resolve("Home"), "Library", "Logs", AppId)
            : Path.Combine(Resolve("LocalData"), AppId, "logs");

        Assert.Equal(expected, Resolve("AppLog"));
    }

    // ---- 跨平台形状 ----

    [Theory]
    [InlineData("AppCache")]
    [InlineData("AppConfig")]
    [InlineData("AppData")]
    [InlineData("AppLocalData")]
    [InlineData("AppLog")]
    [InlineData("Cache")]
    [InlineData("Config")]
    [InlineData("Data")]
    [InlineData("Home")]
    [InlineData("LocalData")]
    [InlineData("Resource")]
    [InlineData("Temp")]
    public void SupportedDirsAreAbsoluteAndNonEmpty(string directory)
    {
        string value = Resolve(directory);

        Assert.False(string.IsNullOrWhiteSpace(value));
        Assert.True(Path.IsPathFullyQualified(value), $"{directory} 应当是绝对路径，实际 {value}");
    }

    [Fact]
    public void AllDeclaredDirectoriesAreResolvableOrExplicitlyUnsupported()
    {
        // 清单里的每一项都必须能被"解析"或"明确报不支持"——不能有第三种情况。
        // 第三种是 `default:` 分支（未知目录名），那意味着清单与实现不同步，
        // 而症状是某个 path.xxxDir 命令报"未知的目录名"（看起来像拼错了）。
        foreach (string directory in OrielKnownDirs.AllDirectories)
        {
            string description = OrielKnownDirs.Describe(directory, AppId);

            Assert.DoesNotContain("未知的目录名", description, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EveryDirectoryHasACorrespondingCommand()
    {
        // 命令注册与能力放行都从 AllDirectories 派生（见各自的 remarks），
        // 所以这里钉的是"派生真的通了"：每个目录名都对应一条可注册、可放行的命令。
        foreach (string directory in OrielKnownDirs.AllDirectories)
        {
            string command = $"path.{char.ToLowerInvariant(directory[0])}{directory[1..]}Dir";

            Assert.True(
                OrielBuiltInPathCommands.TryGet(command, out _),
                $"目录 {directory} 没有对应的命令 {command}");

            Assert.True(
                OrielCapabilityRules.IsAlwaysAllowedPathCommand(command),
                $"命令 {command} 不在始终放行的名单里");
        }
    }

    [Fact]
    public void UnknownDirectoryNameIsInvalidArgumentNotNotSupported()
    {
        // "拼错了"与"这个平台没有"是两回事，错误码要分开：
        // 前者是调用方的 bug，后者是调用方该按平台分支的正常情况。
        OrielIpcException ex = Assert.Throws<OrielIpcException>(() => Resolve("NotARealDir"));

        Assert.Equal(OrielIpcErrorCodes.InvalidArgument, ex.Code);
    }

    // ---- resolveResource ----

    [Fact]
    public void ResolveResourceJoinsUnderTheResourceDir()
    {
        string result = Invoke<string>("path.resolveResource", """{ "path": "assets/logo.svg" }""");

        Assert.Equal(
            Path.Combine(Resolve("Resource"), "assets", "logo.svg"),
            result);
    }

    [Fact]
    public void ResolveResourceRejectsAbsolutePaths()
    {
        // 绝对路径等于"读文件系统任意位置"，而这个函数的名字是 resolve**Resource**。
        // Tauri 的文档也明说参数必须遵循 resources 清单的语法（相对路径）。
        string absolute = OperatingSystem.IsWindows() ? @"C:\Windows\notepad.exe" : "/etc/passwd";

        OrielIpcException ex = Assert.Throws<OrielIpcException>(
            () => Invoke<string>("path.resolveResource", $$"""{ "path": "{{absolute.Replace("\\", "\\\\")}}" }"""));

        Assert.Equal(OrielIpcErrorCodes.InvalidArgument, ex.Code);
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("a/../../outside.txt")]
    [InlineData("assets/../../outside.txt")]
    public void ResolveResourceRejectsEscapingTheResourceDir(string relative)
    {
        // 判据是"拼完在不在根之内"，**不是**"字符串里有没有 .."——
        // `assets/../logo.svg` 是合法的，那种写法很常见。
        OrielIpcException ex = Assert.Throws<OrielIpcException>(
            () => Invoke<string>("path.resolveResource", $$"""{ "path": "{{relative}}" }"""));

        Assert.Equal(OrielIpcErrorCodes.InvalidArgument, ex.Code);
    }

    [Fact]
    public void ResolveResourceAllowsInteriorDotDot()
    {
        // 内部 .../.. 是合法的，不能被上面那条误伤
        string result = Invoke<string>("path.resolveResource", """{ "path": "assets/../logo.svg" }""");

        Assert.Equal(Path.Combine(Resolve("Resource"), "logo.svg"), result);
    }

    [Fact]
    public void ResolveResourceMissingPathGivesMissingArgument()
    {
        OrielIpcException ex = Assert.Throws<OrielIpcException>(
            () => Invoke<string>("path.resolveResource", "{ }"));

        Assert.Equal(OrielIpcErrorCodes.MissingArgument, ex.Code);
    }

    // ---- 夹具 ----

    private static readonly Guid ProfileId = new("5E6C858F-0E22-4760-9AFE-EA3317B67173");

    private static string Resolve(string directory) => OrielKnownDirs.Resolve(directory, AppId);

    private static T Invoke<T>(string name, string argsJson)
    {
        Assert.True(OrielBuiltInPathCommands.TryGet(name, AppId, out var handler), $"未注册：{name}");

        using var doc = System.Text.Json.JsonDocument.Parse(argsJson);
        return (T)handler(doc.RootElement)!;
    }

    [LibraryImport("shell32.dll")]
    private static partial int SHGetKnownFolderPath(in Guid rfid, uint dwFlags, nint hToken, out nint path);

    [LibraryImport("ole32.dll")]
    private static partial void CoTaskMemFree(nint pv);

    /// <summary>
    /// 独立算一遍 <c>FOLDERID_*</c> 的值——**故意不复用实现里的那个方法**。
    /// </summary>
    /// <remarks>
    /// 复用就等于"实现和测试抄同一份错的映射"会双双通过，而那正是这类代码最容易出的错。
    /// 这里直接调 Win32，期望值来自测试自己手上的 GUID 字面量。
    /// </remarks>
    private static string KnownFolderPath(Guid id)
    {
        nint buffer = 0;

        try
        {
            int hr = SHGetKnownFolderPath(in id, 0, 0, out buffer);
            Assert.True(hr >= 0 && buffer != 0, $"SHGetKnownFolderPath({id}) 失败：0x{hr:X8}");
            return Marshal.PtrToStringUni(buffer)!;
        }
        finally
        {
            if (buffer != 0)
            {
                CoTaskMemFree(buffer);
            }
        }
    }
}
