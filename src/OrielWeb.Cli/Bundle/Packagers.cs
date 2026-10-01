using System.Text;

namespace OrielWeb.Cli.Bundle;

/// <summary>
/// 三平台产物的**文本**生成（纯函数）。
/// </summary>
/// <remarks>
/// 抽成纯函数是为了能被单测：这些文本的错都很难在开发机上发现——MSI 的 UpgradeCode 变了要等
/// 用户升级时才暴露、Info.plist 少一个键会让 WKWebView 直接 __builtin_trap（见
/// oriel bundle 头部那段记录）、.desktop 的 Exec 写错只是"点不开"。
/// 单测至少能钉住"关键字段在不在、值对不对"。
/// </remarks>
internal static class Packagers
{
    /// <summary>
    /// WiX v5 的 .wxs：per-user 安装到 <c>%LocalAppData%\Programs\&lt;名字&gt;</c>，带开始菜单快捷方式。
    /// </summary>
    /// <remarks>
    /// per-user 而不是 Program Files：与库现有的"AOT 单文件、解压即用"分发方式一致，
    /// 也不需要 UAC；而装到 Program Files 之后"应用自己要更新"就必须提权（updater 还没做，
    /// 现在选 per-machine 等于把那个问题提前埋下）。见 ROADMAP 阶段 D 第 4 项的取舍。
    /// </remarks>
    internal static string WixSource(BundleOptions options, string executableName)
    {
        var text = new StringBuilder();
        text.AppendLine("""<?xml version="1.0" encoding="UTF-8"?>""");
        text.AppendLine("""<!-- 由 oriel bundle 生成（per-user 安装，不需要管理员权限）。不要手工编辑。 -->""");
        text.AppendLine("""<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">""");
        text.AppendLine(
            $"  <Package Name=\"{Escape(options.Name)}\" " +
            $"Manufacturer=\"{Escape(options.Publisher ?? options.Name)}\" " +
            $"Version=\"{Escape(options.Version)}\" " +
            $"UpgradeCode=\"{options.UpgradeCode:B}\" Scope=\"perUser\" Compressed=\"yes\">");
        // DowngradeErrorMessage 不是装饰：没有 MajorUpgrade 的话，装着新版本时再装旧版会变成并存
        text.AppendLine("""    <MajorUpgrade DowngradeErrorMessage="已安装了更新的版本，请先在「应用和功能」里卸载它。" />""");
        text.AppendLine("""    <MediaTemplate EmbedCab="yes" />""");
        text.AppendLine();
        text.AppendLine("""    <StandardDirectory Id="LocalAppDataFolder">""");
        text.AppendLine("""      <Directory Id="ProgramsFolder" Name="Programs">""");
        text.AppendLine($"""        <Directory Id="INSTALLFOLDER" Name="{Escape(options.Name)}" />""");
        text.AppendLine("""      </Directory>""");
        text.AppendLine("""    </StandardDirectory>""");
        text.AppendLine("""    <StandardDirectory Id="ProgramMenuFolder">""");
        text.AppendLine($"""      <Directory Id="AppMenuFolder" Name="{Escape(options.Name)}" />""");
        text.AppendLine("""    </StandardDirectory>""");
        text.AppendLine();
        text.AppendLine("""    <!-- 发布目录里的所有文件（WiX v4+ 的批量收集；AOT 单文件通常只有一个 exe 加旁文件） -->""");
        text.AppendLine("""    <ComponentGroup Id="AppFiles" Directory="INSTALLFOLDER">""");
        text.AppendLine($"""      <Files Include="{Escape(options.PublishDirectory)}\**" />""");
        text.AppendLine("""    </ComponentGroup>""");
        text.AppendLine();
        text.AppendLine("""    <!-- 开始菜单快捷方式。Guid="*" 让 WiX 按组件路径生成稳定 GUID（每次打包都一样） -->""");
        text.AppendLine("""    <Component Id="ApplicationShortcut" Directory="AppMenuFolder" Guid="*">""");
        text.AppendLine(
            $"      <Shortcut Id=\"StartMenuShortcut\" Name=\"{Escape(options.Name)}\" " +
            $"Target=\"[INSTALLFOLDER]{Escape(executableName)}\" WorkingDirectory=\"INSTALLFOLDER\" />");
        text.AppendLine("""      <RemoveFolder Id="RemoveAppMenuFolder" Directory="AppMenuFolder" On="uninstall" />""");
        text.AppendLine(
            $"      <RegistryValue Root=\"HKCU\" Key=\"Software\\{Escape(options.Id)}\" " +
            "Name=\"Installed\" Type=\"integer\" Value=\"1\" KeyPath=\"yes\" />");
        text.AppendLine("""    </Component>""");
        text.AppendLine();
        text.AppendLine($"    <Feature Id=\"MainFeature\" Title=\"{Escape(options.Name)}\" Level=\"1\">");
        text.AppendLine("""      <ComponentGroupRef Id="AppFiles" />""");
        text.AppendLine("""      <ComponentRef Id="ApplicationShortcut" />""");
        text.AppendLine("""    </Feature>""");
        text.AppendLine("""  </Package>""");
        text.AppendLine("""</Wix>""");
        return text.ToString();
    }

    /// <summary>
    /// macOS 的 <c>Info.plist</c>。
    /// </summary>
    /// <remarks>
    /// <b>CFBundleIdentifier 不是可选项</b>：WKWebView 在 macOS 上是多进程架构，宿主进程要凭
    /// main bundle 的身份才能与 WebContent / Networking 这些 XPC 服务通信；没有 identifier 时
    /// WebKit 在内部断言处 __builtin_trap()，现象是"进程不崩消息循环、直接死掉"（退出码 133）。
    /// 见 oriel bundle 头部的真机记录。
    /// </remarks>
    internal static string InfoPlist(BundleOptions options, string executableName, bool hasIcon)
    {
        var text = new StringBuilder();
        text.AppendLine("""<?xml version="1.0" encoding="UTF-8"?>""");
        text.AppendLine("""<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">""");
        text.AppendLine("""<plist version="1.0">""");
        text.AppendLine("""<dict>""");
        Append(text, "CFBundleExecutable", executableName);
        Append(text, "CFBundleIdentifier", options.Id);
        Append(text, "CFBundleName", options.Name);
        Append(text, "CFBundleDisplayName", options.Name);
        Append(text, "CFBundlePackageType", "APPL");
        Append(text, "CFBundleShortVersionString", options.Version);
        Append(text, "CFBundleVersion", options.Version);
        if (hasIcon)
        {
            // 与 BundleCommand 放进 Contents/Resources 的文件名一致（不带扩展名）
            Append(text, "CFBundleIconFile", AppIconName);
        }

        text.AppendLine("""    <key>NSHighResolutionCapable</key>""");
        text.AppendLine("""    <true/>""");
        Append(text, "LSMinimumSystemVersion", "11.0");
        text.AppendLine("""</dict>""");
        text.AppendLine("""</plist>""");
        return text.ToString();
    }

    /// <summary>Contents/Resources 里图标的名字（不含扩展名）。</summary>
    internal const string AppIconName = "AppIcon";

    /// <summary>AppImage 的 <c>.desktop</c>。</summary>
    internal static string DesktopEntry(BundleOptions options, string executableName)
    {
        var text = new StringBuilder();
        text.AppendLine("[Desktop Entry]");
        text.AppendLine("Type=Application");
        text.AppendLine($"Name={options.Name}");
        text.AppendLine($"Comment={options.Name}");
        text.AppendLine($"Exec={executableName}");
        text.AppendLine($"Icon={IconName(options)}");
        text.AppendLine("Terminal=false");
        text.AppendLine("Categories=Utility;");
        text.AppendLine($"X-AppImage-Version={options.Version}");
        return text.ToString();
    }

    /// <summary>AppImage 的入口脚本（<c>AppRun</c>）。</summary>
    /// <remarks>
    /// 用 <c>readlink -f</c> 解析自身路径而不是 <c>$0</c>：AppImage 运行时会把内容挂到
    /// <c>/tmp/.mount_xxx</c> 下，相对调用时 <c>$0</c> 不一定是绝对路径。
    /// </remarks>
    internal static string AppRun(string executableName)
        => string.Join('\n',
            "#!/bin/sh",
            "# 由 oriel bundle 生成：AppImage 的入口脚本。",
            "HERE=\"$(dirname \"$(readlink -f \"$0\")\")\"",
            $"exec \"$HERE/usr/bin/{executableName}\" \"$@\"",
            string.Empty);

    /// <summary>.desktop 与图标文件共用的图标名（小写、无空格）。</summary>
    internal static string IconName(BundleOptions options)
        => BundleOptions.Sanitize(options.Name).ToLowerInvariant().Replace('.', '-');

    private static void Append(StringBuilder text, string key, string value)
    {
        text.AppendLine($"    <key>{key}</key>");
        text.AppendLine($"    <string>{Escape(value)}</string>");
    }

    private static string Escape(string value)
        => value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal);
}
