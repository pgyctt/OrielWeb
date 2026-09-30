using System.Diagnostics;

namespace OrielWeb;

/// <summary>Shell 动作：打开链接、在文件管理器里显示、用默认程序打开文件。</summary>
internal enum OrielShellAction
{
    OpenUrl,
    RevealPath,
    OpenPath,
}

/// <summary>
/// "交给系统默认程序"之前的校验。
/// </summary>
/// <remarks>
/// 这类 API 的危险不在自己执行了什么，而在于**它决定了别的程序去打开什么**：
/// 一个未经校验的 <c>file:</c> 或 <c>javascript:</c> 会被系统默认处理器以它自己的权限打开。
/// 所以白名单是默认拒绝式的，并且这条校验是纯函数——能被逐例单测，而不是"看起来没问题"。
/// </remarks>
internal static class OrielShellPolicy
{
    /// <summary>
    /// 校验一个"打开外部链接"的目标。三类拒绝理由：
    /// ① 不是绝对 URI（裸路径、相对路径都会被系统当成"文件"打开）；
    /// ② scheme 不在白名单里；
    /// ③ 含控制字符（换行/制表符会污染下游对命令行的解析）。
    /// </summary>
    internal static bool IsAllowedUrl(string? url, IReadOnlyList<string> allowedSchemes)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        foreach (char c in url)
        {
            if (char.IsControl(c))
            {
                return false;
            }
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme.Length == 0)
        {
            return false;
        }

        foreach (string scheme in allowedSchemes)
        {
            if (string.Equals(scheme, uri.Scheme, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 校验"在文件管理器里显示 / 用默认程序打开"的路径：必须是绝对路径，且**确实存在**——
    /// 不存在的路径交给 explorer 会弹出一个指向别处（或什么都不选）的窗口，不如直接不调用。
    /// </summary>
    internal static bool IsUsablePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
        {
            return false;
        }

        return File.Exists(path) || Directory.Exists(path);
    }
}

/// <summary>
/// 把动作翻译成"可执行文件 + 参数"。纯函数（平台由参数注入）以便逐平台单测——
/// 这三条系统命令各有各的怪癖，靠肉眼 review 很容易漏掉一个逗号或一个空格。
/// </summary>
internal static class OrielShellCommand
{
    internal static (string Executable, IReadOnlyList<string> Arguments) Build(
        OrielShellAction action,
        string target,
        bool isWindows,
        bool isMacOS)
    {
        if (isWindows)
        {
            // explorer 的 /select, 后面**不能有空格**（写成 "/select, path" 会被当成两个参数而静默失败）；
            // 打开 URL/文件则没有独立可执行文件，交给 shell 执行（UseShellExecute），此时参数为空
            return action == OrielShellAction.RevealPath
                ? ("explorer.exe", ["/select," + target])
                : (target, []);
        }

        if (isMacOS)
        {
            // open 是 macOS 的通用"交给默认程序"入口；-R 表示在 Finder 里显示而不是打开
            return action == OrielShellAction.RevealPath
                ? ("/usr/bin/open", ["-R", target])
                : ("/usr/bin/open", [target]);
        }

        // Linux 的 xdg-open 只能"打开"：显示文件就退一步打开它所在的目录
        // （freedesktop 没有等价的"选中"入口；要走精确选中得用 FileManager1 的 D-Bus 接口）
        return action == OrielShellAction.RevealPath
            ? ("xdg-open", [ParentDirectory(target)])
            : ("xdg-open", [target]);
    }

    /// <summary>
    /// 取父目录。刻意用字符串处理而不用 <see cref="Path.GetDirectoryName(string?)"/>：
    /// "Linux 分支收到 POSIX 路径"这个行为要在 **Windows 上也能被测试断言**，
    /// 而 Path API 在 Windows 上会把不带盘符的 POSIX 路径按相对路径解析（<c>/home/x</c> 变成
    /// 当前盘符下的 <c>\home\x</c>）——纯函数一旦调了平台相关 API，就不再纯。
    /// </summary>
    private static string ParentDirectory(string path)
    {
        int cut = path.LastIndexOfAny(['/', '\\']);
        return cut > 0 ? path[..cut] : path;
    }
}

/// <summary>
/// 按平台把动作交给系统默认程序。薄到只剩"启动进程 + 失败如实返回"。
/// </summary>
internal static class OrielShellLauncher
{
    internal static bool Launch(OrielShellAction action, string target)
    {
        (string executable, IReadOnlyList<string> arguments) = OrielShellCommand.Build(
            action, target, OperatingSystem.IsWindows(), OperatingSystem.IsMacOS());

        try
        {
            // Windows 的"打开 URL/文件"没有独立可执行文件——用 shell 执行才是标准做法
            if (arguments.Count == 0)
            {
                _ = Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true });
                return true;
            }

            var startInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            // 逐项传参、不经 shell：目标里带空格/引号/分号都不会变成第二个命令
            foreach (string argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using Process? process = Process.Start(startInfo);
            return process is not null;
        }
        catch (Exception ex)
        {
            // 环境里没有 xdg-open、或没有默认处理器：如实返回失败（调用方可以退回应用内提示）
            Debug.WriteLine($"[OrielWeb] 调用系统默认程序失败（{executable}）：{ex.Message}");
            return false;
        }
    }
}
