using System.Text;

namespace OrielWeb.Cli.Bundle;

/// <summary><c>oriel bundle</c> 的结果（映射到进程退出码）。</summary>
internal enum BundleOutcome
{
    /// <summary>产物已生成。</summary>
    Ok,

    /// <summary>打包失败（缺工具、外部命令报错）。</summary>
    Failed,

    /// <summary>用法错误（必填项没给、参数指向的东西不存在）。</summary>
    UsageError,
}

/// <summary>
/// <c>oriel bundle</c>：把一个已发布的目录打成该平台的安装产物。
/// </summary>
/// <remarks>
/// <para>
/// 每个平台都要一份"平台上才有的工具"：Windows 的 WiX、macOS 的 hdiutil/codesign、Linux 的
/// appimagetool。所以**在哪台机器上打哪个平台的包**是硬约束，跨平台打包不做
/// （它需要的那套东西不是"写点代码"能补上的）——命令会直接说清楚，而不是失败在一个看不懂的地方。
/// </para>
/// <para>
/// Linux 的 .AppImage 需要 <c>appimagetool</c>，它是外部二进制而非系统包；缺它时**只组装 AppDir**
/// 并明确报错，而不是让调用方拿到一个半成品。AppDir 本身是完整可运行的目录，调试时也用得上。
/// </para>
/// </remarks>
internal static class BundleCommand
{
    internal static BundleOutcome Run(ParsedCommandLine parsed)
    {
        (BundleOptions? options, string? error) = BundleOptionsReader.Resolve(parsed);
        if (options is null)
        {
            Console.Error.WriteLine($"oriel bundle：{error}");
            return BundleOutcome.UsageError;
        }

        string? executable = FindExecutable(options);
        if (executable is null)
        {
            Console.Error.WriteLine(
                $"oriel bundle：在 {options.PublishDirectory} 里找不到主可执行文件。" +
                "（--dir 要给 `dotnet publish` 的**输出目录**，不是项目目录）");
            return BundleOutcome.Failed;
        }

        Directory.CreateDirectory(options.OutputDirectory);
        Console.WriteLine($"打包 {options.Name} {options.Version}（{options.Rid}）");
        Console.WriteLine($"  发布目录：{options.PublishDirectory}");
        Console.WriteLine($"  产物目录：{options.OutputDirectory}");
        WarnAboutDebugSymbols(options.PublishDirectory);

        return options.Platform switch
        {
            BundlePlatform.Windows => BundleWindows(options, executable),
            BundlePlatform.MacOS => BundleMacOS(options, executable),
            BundlePlatform.Linux => BundleLinux(options, executable),
            _ => BundleOutcome.Failed,
        };
    }

    /// <summary>
    /// 发布目录里带了调试符号时提醒一句。
    /// </summary>
    /// <remarks>
    /// 不阻止打包（可能是刻意的），但产物体积会大一圈——AOT 的 <c>.dbg</c> 经常比可执行文件本身还大。
    /// 发布时的正确写法是 <c>-p:DebugType=none</c>（release.yml 与 README 都是这么做的）。
    /// </remarks>
    private static void WarnAboutDebugSymbols(string publishDirectory)
    {
        string[] symbols = Directory
            .EnumerateFiles(publishDirectory)
            .Where(path => Path.GetExtension(path) is ".pdb" or ".dbg")
            .ToArray();

        if (symbols.Length > 0)
        {
            Console.WriteLine(
                $"  [WARN] 发布目录里有 {symbols.Length} 个调试符号文件" +
                $"（{string.Join('、', symbols.Select(Path.GetFileName))}），会被一起打进产物。" +
                "发布时加 -p:DebugType=none 可去掉。");
        }
    }

    /// <summary>发布目录里的主可执行文件：Windows 看 <c>*.exe</c>，其它平台看没有扩展名的可执行文件。</summary>
    private static string? FindExecutable(BundleOptions options)
    {
        IEnumerable<string> candidates = options.Platform == BundlePlatform.Windows
            ? Directory.EnumerateFiles(options.PublishDirectory, "*.exe")
            : Directory.EnumerateFiles(options.PublishDirectory)
                .Where(path => Path.GetExtension(path).Length == 0);

        return candidates
            .OrderBy(path => path, StringComparer.Ordinal)
            .FirstOrDefault(File.Exists);
    }

    // ---- Windows：WiX v5 → .msi ----

    private static BundleOutcome BundleWindows(BundleOptions options, string executable)
    {
        (bool hasWix, _) = ProcessRunner.TryRun("wix", "--version");
        if (!hasWix)
        {
            Console.Error.WriteLine(
                "oriel bundle：找不到 wix（WiX v5）。安装：dotnet tool install --global wix");
            return BundleOutcome.Failed;
        }

        string wxsPath = Path.Combine(Path.GetTempPath(), $"oriel-{Guid.NewGuid():N}.wxs");
        string msiPath = Path.Combine(options.OutputDirectory, options.ArtifactBaseName + ".msi");

        try
        {
            // UTF-8 不带 BOM：WiX 读带 BOM 的 XML 没问题，但不带更省事（少一个可能的坑）
            File.WriteAllText(wxsPath, Packagers.WixSource(options, Path.GetFileName(executable)), new UTF8Encoding(false));

            (bool ok, string output) = ProcessRunner.TryRun(
                "wix", "build", "-arch", ArchitectureFor(options.Rid), "-o", msiPath, wxsPath);

            if (!ok)
            {
                Console.Error.WriteLine("oriel bundle：wix build 失败。");
                Console.Error.WriteLine(output);
                return BundleOutcome.Failed;
            }

            Console.WriteLine($"  [OK] {msiPath}");
            return BundleOutcome.Ok;
        }
        finally
        {
            File.Delete(wxsPath);
        }
    }

    private static string ArchitectureFor(string rid)
        => rid.Contains("arm64", StringComparison.OrdinalIgnoreCase) ? "arm64"
            : rid.Contains("x86", StringComparison.OrdinalIgnoreCase) ? "x86"
            : "x64";

    // ---- macOS：.app（ad-hoc 签名）+ .dmg ----

    private static BundleOutcome BundleMacOS(BundleOptions options, string executable)
    {
        if (!OperatingSystem.IsMacOS())
        {
            Console.Error.WriteLine("oriel bundle：.app / .dmg 只能在 macOS 上打（需要 codesign 与 hdiutil）。");
            return BundleOutcome.Failed;
        }

        string appDirectory = Path.Combine(options.OutputDirectory, options.Name + ".app");
        string contents = Path.Combine(appDirectory, "Contents");
        string macOsDirectory = Path.Combine(contents, "MacOS");
        string resources = Path.Combine(contents, "Resources");
        string executableName = Path.GetFileName(executable);

        // 重建：同一目录里反复打包时，残留的旧文件会一起进包（而且可能已经被签过名）
        if (Directory.Exists(appDirectory))
        {
            Directory.Delete(appDirectory, recursive: true);
        }

        Directory.CreateDirectory(macOsDirectory);
        Directory.CreateDirectory(resources);
        File.Copy(executable, Path.Combine(macOsDirectory, executableName), overwrite: true);

        bool hasIcon = options.Icon is { Length: > 0 } icon && TryInstallIcon(icon, resources);
        File.WriteAllText(
            Path.Combine(contents, "Info.plist"),
            Packagers.InfoPlist(options, executableName, hasIcon),
            new UTF8Encoding(false));

        // 两个层次都要签：先可执行文件（Apple Silicon 的运行前提），再 bundle。
        // 顺序反了会被内层那份未签名的代码破坏（见 oriel bundle 的说明）。
        if (!RunTool("/usr/bin/codesign", "--force", "--sign", "-", Path.Combine(macOsDirectory, executableName))
            || !RunTool("/usr/bin/codesign", "--force", "--deep", "--sign", "-", appDirectory))
        {
            Console.Error.WriteLine("oriel bundle：codesign 失败（至少要 ad-hoc 签名，否则 .app 无法运行）。");
            return BundleOutcome.Failed;
        }

        Console.WriteLine($"  [OK] {appDirectory}");

        string dmgPath = Path.Combine(options.OutputDirectory, options.ArtifactBaseName + ".dmg");
        if (!RunTool("/usr/bin/hdiutil", "create", "-volname", options.Name, "-srcfolder", appDirectory,
                "-ov", "-format", "UDZO", dmgPath))
        {
            Console.Error.WriteLine("oriel bundle：hdiutil 打 .dmg 失败（.app 已经生成，可直接分发它）。");
            return BundleOutcome.Failed;
        }

        Console.WriteLine($"  [OK] {dmgPath}");
        return BundleOutcome.Ok;
    }

    /// <summary>
    /// 把图标放进 <c>Contents/Resources</c>：<c>.icns</c> 直接拷，图片则用 sips + iconutil 转。
    /// </summary>
    /// <remarks>
    /// 转换失败**不阻断**打包（只是图标缺失），但会打印出来——静默少一个图标会让人以为是别处的问题。
    /// </remarks>
    private static bool TryInstallIcon(string iconPath, string resourcesDirectory)
    {
        string target = Path.Combine(resourcesDirectory, Packagers.AppIconName + ".icns");

        if (Path.GetExtension(iconPath).Equals(".icns", StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(iconPath, target, overwrite: true);
            return true;
        }

        string iconset = Path.Combine(Path.GetTempPath(), $"oriel-{Guid.NewGuid():N}.iconset");
        Directory.CreateDirectory(iconset);
        try
        {
            int[] sizes = [16, 32, 64, 128, 256, 512];
            foreach (int size in sizes)
            {
                if (!RunTool("/usr/bin/sips", "-z", size.ToString(), size.ToString(), iconPath,
                        "--out", Path.Combine(iconset, $"icon_{size}x{size}.png")))
                {
                    Console.WriteLine($"  [WARN] 图标转换失败（sips {size}px），.app 将不带图标");
                    return false;
                }
            }

            if (!RunTool("/usr/bin/iconutil", "-c", "icns", iconset, "-o", target))
            {
                Console.WriteLine("  [WARN] iconutil 失败，.app 将不带图标");
                return false;
            }

            return true;
        }
        finally
        {
            Directory.Delete(iconset, recursive: true);
        }
    }

    // ---- Linux：AppDir → .AppImage ----

    private static BundleOutcome BundleLinux(BundleOptions options, string executable)
    {
        if (!OperatingSystem.IsLinux())
        {
            Console.Error.WriteLine("oriel bundle：.AppImage 只能在 Linux 上打（需要一个 Linux 文件系统与 appimagetool）。");
            return BundleOutcome.Failed;
        }

        string executableName = Path.GetFileName(executable);
        string appDirectory = Path.Combine(options.OutputDirectory, options.Name + ".AppDir");
        string binDirectory = Path.Combine(appDirectory, "usr", "bin");

        if (Directory.Exists(appDirectory))
        {
            Directory.Delete(appDirectory, recursive: true);
        }

        Directory.CreateDirectory(binDirectory);

        // 发布目录里的所有文件都进 usr/bin（AOT 产物通常只有一个可执行文件加旁文件）
        foreach (string file in Directory.EnumerateFiles(options.PublishDirectory))
        {
            File.Copy(file, Path.Combine(binDirectory, Path.GetFileName(file)), overwrite: true);
        }

        string appRunPath = Path.Combine(appDirectory, "AppRun");
        File.WriteAllText(appRunPath, Packagers.AppRun(executableName), new UTF8Encoding(false));
        MakeExecutable(appRunPath, executableName, binDirectory);

        File.WriteAllText(
            Path.Combine(appDirectory, options.Name + ".desktop"),
            Packagers.DesktopEntry(options, executableName),
            new UTF8Encoding(false));

        if (options.Icon is { Length: > 0 } icon)
        {
            string iconName = Packagers.IconName(options) + Path.GetExtension(icon);
            File.Copy(icon, Path.Combine(appDirectory, iconName), overwrite: true);
            string hicolor = Path.Combine(appDirectory, "usr", "share", "icons", "hicolor", "256x256", "apps");
            Directory.CreateDirectory(hicolor);
            File.Copy(icon, Path.Combine(hicolor, Packagers.IconName(options) + Path.GetExtension(icon)), overwrite: true);
        }

        Console.WriteLine($"  [OK] {appDirectory}（AppDir，可直接运行其中的 AppRun）");

        string appImagePath = Path.Combine(options.OutputDirectory, options.ArtifactBaseName + ".AppImage");
        (bool hasTool, _) = ProcessRunner.TryRun("appimagetool", "--version");
        if (!hasTool)
        {
            Console.Error.WriteLine(
                "oriel bundle：找不到 appimagetool，只生成了 AppDir。" +
                "下载 https://github.com/AppImage/appimagetool/releases 放进 PATH 后再跑一次即可。");
            return BundleOutcome.Failed;
        }

        if (!RunTool("appimagetool", appDirectory, appImagePath))
        {
            Console.Error.WriteLine("oriel bundle：appimagetool 失败（AppDir 已经生成）。");
            return BundleOutcome.Failed;
        }

        Console.WriteLine($"  [OK] {appImagePath}");
        return BundleOutcome.Ok;
    }

    /// <summary>
    /// 给 AppRun 与 <c>usr/bin</c> 里的可执行文件补上执行位。
    /// </summary>
    /// <remarks>
    /// .NET 没有跨平台的 chmod API（<c>File.SetUnixFileMode</c> 只在 Unix 上有效），
    /// 而 AppImage 少一个执行位就完全跑不起来——所以这里显式设，并且只设"看起来像可执行文件"的那些：
    /// 发布目录里可能还有 .json/.so 之类不该带的文件。
    /// </remarks>
    private static void MakeExecutable(string appRunPath, string executableName, string binDirectory)
    {
        var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                   | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                   | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

        File.SetUnixFileMode(appRunPath, mode);

        string main = Path.Combine(binDirectory, executableName);
        if (File.Exists(main))
        {
            File.SetUnixFileMode(main, mode);
        }
    }

    private static bool RunTool(string fileName, params string[] arguments)
    {
        (bool ok, string output) = ProcessRunner.TryRun(fileName, arguments);
        if (!ok && output.Length > 0)
        {
            Console.Error.WriteLine(output);
        }

        return ok;
    }
}
