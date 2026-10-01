using System.Globalization;
using Microsoft.Win32;

namespace OrielWeb.Cli.Doctor;

/// <summary>
/// 本机环境体检：这台机器能不能**跑**基于本库的应用、能不能**发布**（尤其 Native AOT）、
/// 能不能**打包**（<c>oriel bundle</c> 需要的外部工具）。
/// </summary>
/// <remarks>
/// <para>
/// 这些检查此前散在三处：<c>tools/verify-linux.sh</c>（dpkg 查库、clang、DISPLAY/WAYLAND_DISPLAY）、
/// <c>dotnet publish</c>（同一批 dpkg 检查）、<c>tools/verify-macos.sh</c>
/// （codesign/swiftc/hdiutil），加上库里的 WebView2 运行时探测。散着的代价是"我该跑哪个脚本"，
/// 而且新人改了库的依赖（例如换上 libwebkit2gtk-4.0）时没有一处会被提醒。
/// </para>
/// <para>
/// 三类结论刻意分开：**能跑**（引擎 + 显示环境）缺失是 FAIL，**能发布**（clang/SDK）缺失是
/// WARN（用户只是拿不到产物），**能打包**（wix/appimagetool）缺失也是 WARN
/// （只有 <c>oriel bundle</c> 用到）。混成一种等级会让"这台机器能不能跑我的应用"这个
/// 最要紧的问题被无关的工具链问题淹没。
/// </para>
/// </remarks>
internal static class SystemChecks
{
    internal static CheckReport Run()
    {
        var report = new CheckReport($"本机环境（{PlatformName()}）");

        Runtime(report);
        if (OperatingSystem.IsWindows())
        {
            Windows(report);
        }
        else if (OperatingSystem.IsMacOS())
        {
            MacOS(report);
        }
        else
        {
            Linux(report);
        }

        Display(report);
        PackagingTools(report);
        return report;
    }

    internal static string PlatformName()
        => OperatingSystem.IsWindows() ? "windows"
            : OperatingSystem.IsMacOS() ? "macos"
            : OperatingSystem.IsLinux() ? "linux"
            : "unknown";

    // ---- 运行时与 SDK ----

    private static void Runtime(CheckReport report)
    {
        report.Add(CheckItem.Pass("dotnet-runtime", ".NET 运行时", $"{Environment.Version}"));

        // SDK 与运行时是两回事：能跑已发布的应用不代表能发布。
        // 只看主版本 10——本库只提供 net10.0 目标，装 8.x SDK 也发布不出这个应用。
        (bool ok, string output) = ProcessRunner.TryRun("dotnet", "--list-sdks");
        bool hasSdk = ok && output
            .Split('\n')
            .Any(line => line.TrimStart().StartsWith("10.", StringComparison.Ordinal));

        report.Add(hasSdk
            ? CheckItem.Pass("dotnet-sdk", ".NET SDK（10.x）", "已安装（发布与 Native AOT 需要它）")
            : CheckItem.Warn("dotnet-sdk", ".NET SDK（10.x）",
                "dotnet --list-sdks 里没有 10.x。跑已发布的应用不需要 SDK，但**发布**需要。" +
                "安装：curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0"));
    }

    // ---- Windows ----

    private static void Windows(CheckReport report)
    {
        string? version = WebView2RuntimeVersion();
        report.Add(version is null
            ? CheckItem.Fail("webview2-runtime", "WebView2 运行时",
                "未检测到。应用的界面完全由网页渲染，缺少它无法显示。安装入口：" +
                "https://go.microsoft.com/fwlink/p/?LinkId=2124703" +
                "（应用侧可以用 OnWebView2RuntimeMissing 引导用户，见 README「WebView2 运行时与缺失引导」）")
            : CheckItem.Pass("webview2-runtime", "WebView2 运行时", version));
    }

    /// <summary>WebView2 Evergreen 运行时的产品标识（微软文档里固定的那个 GUID）。</summary>
    private const string WebView2ClientId = "{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";

    /// <summary>
    /// WebView2 运行时的版本；未安装时返回 null。
    /// </summary>
    /// <remarks>
    /// 库里有一份同样的探测（经 <c>WebView2Utilities</c>），但那是 internal——本工具刻意不引用库
    /// （见 csproj 注释），所以按微软文档记录的注册表位置各查一遍：64 位视图、32 位视图
    /// （WOW6432Node）、以及 per-user 安装。三处都要查：Evergreen 运行时可能被装成任意一种，
    /// 只查一处会在某些机器上给出"未安装"的误报。
    /// </remarks>
    private static string? WebView2RuntimeVersion()
    {
        string[] subkeys =
        [
            $@"SOFTWARE\Microsoft\EdgeUpdate\Clients\{WebView2ClientId}",
            $@"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{WebView2ClientId}",
        ];

        foreach (RegistryKey root in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            foreach (string subkey in subkeys)
            {
                using RegistryKey? key = root.OpenSubKey(subkey);
                if (key?.GetValue("pv") is string version && version.Length > 0)
                {
                    return version;
                }
            }
        }

        return null;
    }

    // ---- Linux ----

    private static void Linux(CheckReport report)
    {
        report.Add(LibraryCheck(
            "webkitgtk",
            "WebKitGTK 4.1（渲染引擎）",
            ["libwebkit2gtk-4.1-0", "libwebkit2gtk-4.1-dev"],
            "libwebkit2gtk-4.1",
            "sudo apt-get install -y libwebkit2gtk-4.1-dev"));

        report.Add(LibraryCheck(
            "gtk3",
            "GTK3",
            ["libgtk-3-0", "libgtk-3-dev"],
            "libgtk-3.so.0",
            "sudo apt-get install -y libgtk-3-dev"));

        report.Add(CjkFont());
        report.Add(Tool(
            "clang",
            "clang（Native AOT 的原生工具链）",
            ["--version"],
            CheckStatus.Warn,
            "Native AOT 发布需要它：sudo apt-get install -y clang zlib1g-dev"));
    }

    /// <summary>
    /// 查一个共享库是否装了：先问包管理（能说清"装了哪个包"），再回落到 <c>ldconfig</c> 的缓存
    /// （非 Debian 系、或包名与库名对不上时靠它）。
    /// </summary>
    private static CheckItem LibraryCheck(
        string id, string title, string[] packages, string soname, string installHint)
    {
        (bool hasDpkg, _) = ProcessRunner.TryRun("dpkg", "--version");
        if (hasDpkg)
        {
            foreach (string package in packages)
            {
                (bool installed, _) = ProcessRunner.TryRun("dpkg", "-s", package);
                if (installed)
                {
                    return CheckItem.Pass(id, title, $"已安装（dpkg 包 {package}）");
                }
            }
        }

        string? hit = FindInLdconfig(soname);
        if (hit is not null)
        {
            return CheckItem.Pass(id, title, $"已安装（ldconfig：{hit}）");
        }

        return CheckItem.Fail(id, title, $"未找到 {soname}。安装：{installHint}");
    }

    private static string? FindInLdconfig(string soname)
    {
        // ldconfig 通常在 /sbin 里，而普通用户的 PATH 往往不含它
        foreach (string executable in new[] { "ldconfig", "/sbin/ldconfig", "/usr/sbin/ldconfig" })
        {
            (bool ok, string output) = ProcessRunner.TryRun(executable, "-p");
            if (!ok)
            {
                continue;
            }

            string? line = output
                .Split('\n')
                .FirstOrDefault(candidate => candidate.Contains(soname, StringComparison.OrdinalIgnoreCase));
            if (line is not null)
            {
                return line.Trim();
            }
        }

        return null;
    }

    /// <summary>
    /// 中文字体：缺了它的症状是"界面中文全是方框"（Linux 首次真机验证时踩过，见 API.md）。
    /// </summary>
    /// <remarks>
    /// 判据是"<c>sans-serif:lang=zh</c> 会落到哪个字体族"，而不是"装了某个包"——
    /// 用户完全可能用别的方式装到能显示中文的字体。
    /// </remarks>
    private static CheckItem CjkFont()
    {
        (bool ok, string output) = ProcessRunner.TryRun("fc-match", "-f", "%{family}", "sans-serif:lang=zh");
        if (!ok)
        {
            return CheckItem.Warn("cjk-font", "中文字体",
                "没找到 fc-match（fontconfig），无法判定。若界面中文渲染成方框：" +
                "sudo apt-get install -y fonts-noto-cjk");
        }

        string matched = output.Trim();
        bool looksCjk = new[] { "CJK", "Noto", "WenQuanYi", "Droid Sans Fallback", "Source Han" }
            .Any(name => matched.Contains(name, StringComparison.OrdinalIgnoreCase));

        return looksCjk
            ? CheckItem.Pass("cjk-font", "中文字体", $"sans-serif:lang=zh → {matched}")
            : CheckItem.Warn("cjk-font", "中文字体",
                $"sans-serif:lang=zh 落到了「{matched}」，看起来不含中文字形——中文界面可能全是方框。" +
                "安装：sudo apt-get install -y fonts-noto-cjk");
    }

    // ---- macOS ----

    private static void MacOS(CheckReport report)
    {
        (bool hdiutil, _) = ProcessRunner.TryRun("/usr/bin/hdiutil", "help");
        report.Add(hdiutil
            ? CheckItem.Pass("hdiutil", "打包工具 hdiutil（.dmg）", "系统自带")
            : CheckItem.Fail("hdiutil", "打包工具 hdiutil（.dmg）", "未找到 /usr/bin/hdiutil——系统组件缺失，环境异常"));

        (bool codesign, string codesignOutput) = ProcessRunner.TryRun("/usr/bin/codesign", "--version");
        report.Add(codesign
            ? CheckItem.Pass("codesign", "签名工具 codesign", ProcessRunner.FirstLine(codesignOutput) ?? "可用")
            : CheckItem.Fail("codesign", "签名工具 codesign",
                "未找到 /usr/bin/codesign——.app 至少要 ad-hoc 签名才能被系统信任"));

        report.Add(Tool(
            "clang",
            "clang（Native AOT 的原生工具链）",
            ["--version"],
            CheckStatus.Warn,
            "需要 Xcode 命令行工具：xcode-select --install"));

        // WKWebView 的 isInspectable 是 13.3+ 的公开 API。更低版本上库会跳过设置
        // （先 respondsToSelector: 探测），DevTools 因此依赖 Safari 的默认行为——
        // 这不是"坏了"，而是"能力不由本库提供"，所以要如实报出来而不是当成通过。
        report.Add(OperatingSystem.IsMacOSVersionAtLeast(13, 3)
            ? CheckItem.Pass("macos-version", "macOS 版本", $"≥ 13.3（{Environment.OSVersion.VersionString}）")
            : CheckItem.Warn("macos-version", "macOS 版本",
                $"低于 13.3（{Environment.OSVersion.VersionString}）：本库会跳过 WKWebView 的 isInspectable 设置，" +
                "DevTools 开关依赖 Safari 的默认行为"));
    }

    // ---- 显示环境 ----

    private static void Display(CheckReport report)
    {
        if (OperatingSystem.IsLinux())
        {
            string? display = Environment.GetEnvironmentVariable("DISPLAY");
            string? wayland = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY");
            bool hasDisplay = display is { Length: > 0 } || wayland is { Length: > 0 };

            report.Add(hasDisplay
                ? CheckItem.Pass("display", "显示环境",
                    $"DISPLAY={display ?? "<空>"} WAYLAND_DISPLAY={wayland ?? "<空>"}")
                : CheckItem.Fail("display", "显示环境",
                    "DISPLAY 与 WAYLAND_DISPLAY 都为空，窗口无法创建。" +
                    "无头环境请用 xvfb-run 包裹，或进桌面会话（WSL 里确认 WSLg 可用：ls /mnt/wslg）"));
            return;
        }

        // Windows / macOS：没有可靠的方式判断"当前是不是交互式桌面会话"，
        // Environment.UserInteractive 是唯一便宜的近似（服务里跑就是 false）。
        report.Add(Environment.UserInteractive
            ? CheckItem.Pass("display", "交互式会话", "是（窗口能创建；托盘/通知是否真的显示只能人眼）")
            : CheckItem.Warn("display", "交互式会话",
                "Environment.UserInteractive = false（像是服务或计划任务里跑的）——窗口可能创建不出来"));
    }

    // ---- 打包工具（只有 oriel bundle 用到）----

    private static void PackagingTools(CheckReport report)
    {
        if (OperatingSystem.IsWindows())
        {
            (bool ok, string output) = ProcessRunner.TryRun("wix", "--version");
            if (!ok)
            {
                report.Add(CheckItem.Warn("wix", "打包工具 WiX（.msi）",
                    "未找到 wix，oriel bundle 打不出 .msi。安装：dotnet tool install --global wix --version 5.*"));
                return;
            }

            string version = ProcessRunner.FirstLine(output) ?? output;
            report.Add(MajorVersion(version) >= 6
                ? CheckItem.Warn("wix", "打包工具 WiX（.msi）",
                    $"wix {version}：WiX **v6 起要求接受 OSMF 的付费条款**（EULA），本仓库用最后一个 OSI 许可的 v5。" +
                    "降级：dotnet tool uninstall --global wix && dotnet tool install --global wix --version 5.*")
                : CheckItem.Pass("wix", "打包工具 WiX（.msi）", $"wix {version}"));
            return;
        }

        if (OperatingSystem.IsLinux())
        {
            (bool ok, string output) = ProcessRunner.TryRun("appimagetool", "--version");
            report.Add(ok
                ? CheckItem.Pass("appimagetool", "打包工具 appimagetool（.AppImage）",
                    ProcessRunner.FirstLine(output) ?? "可用")
                : CheckItem.Warn("appimagetool", "打包工具 appimagetool（.AppImage）",
                    "未找到 appimagetool，oriel bundle 打不出 .AppImage。" +
                    "下载 https://github.com/AppImage/appimagetool/releases（放进 PATH 即可）"));
        }
    }

    // ---- 通用 ----

    /// <summary>
    /// 从版本字符串里取主版本号（"5.0.2+aa65968c" → 5；认不出来返回 0）。
    /// </summary>
    /// <remarks>
    /// 纯函数，好让它被单测——WiX 的许可分界就在主版本上（见 <see cref="PackagingTools"/>），
    /// 解析错会让"该提醒的没提醒"或者反过来把 v5 也拦下。
    /// </remarks>
    internal static int MajorVersion(string version)
    {
        int end = version.IndexOfAny(['.', '+', '-', ' ', '\n', '\r']);
        string head = end < 0 ? version : version[..end];
        return int.TryParse(head, NumberStyles.Integer, CultureInfo.InvariantCulture, out int major) ? major : 0;
    }

    private static CheckItem Tool(
        string id, string title, string[] arguments, CheckStatus missingStatus, string installHint)
    {
        (bool ok, string output) = ProcessRunner.TryRun(id, arguments);
        return ok
            ? CheckItem.Pass(id, title, ProcessRunner.FirstLine(output) ?? "可用")
            : new CheckItem(id, title, missingStatus, $"未找到或不可用（{id}）。{installHint}");
    }
}
