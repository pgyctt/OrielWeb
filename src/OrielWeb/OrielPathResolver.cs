using OrielWeb.Ipc;

namespace OrielWeb;

/// <summary>
/// 把用户给的相对路径统一按 <see cref="AppContext.BaseDirectory"/>（可执行文件所在目录）解析。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么不是当前工作目录</b>：工控机现场常见的启动方式是快捷方式、计划任务、开机自启与
/// macOS 的 <c>.app</c>——这些情况下 cwd 常常不是程序目录（macOS 从 <c>.app</c> 启动时 cwd 是
/// <c>/</c>），于是"图标/资源按相对路径写"在开发机上好好的、一到现场就失效。
/// 可执行文件所在目录才是使用者心里的"程序目录"（下游反馈 2026-10-14 的 P1-3）。
/// </para>
/// <para>
/// 绝对路径原样返回：调用方仍可显式指定任意位置。
/// </para>
/// </remarks>
internal static class OrielPathResolver
{
    /// <summary>相对路径 → 基于 <see cref="AppContext.BaseDirectory"/> 的绝对路径；绝对路径原样返回。</summary>
    internal static string Resolve(string path)
    {
        if (Path.IsPathRooted(path))
        {
            return path;
        }

        try
        {
            return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // 非法字符 / 路径过长：解析不出来就原样返回，让调用方后续的存在性检查判定"不存在"，
            // 而不是在这里抛一个与调用方语义无关的异常。
            OrielDiagnostics.Report($"[OrielWeb] 路径无法解析为绝对路径，按原样处理：{path}（{ex.Message}）");
            return path;
        }
    }

    /// <summary>
    /// 把**页面给的应用资源路径**（托盘/窗口图标等）限定在可执行文件目录内，返回解析后的绝对路径。
    /// </summary>
    /// <param name="path">页面给的原始路径（相对路径按 <see cref="AppContext.BaseDirectory"/> 解析）。</param>
    /// <param name="full">通过校验的绝对路径（已逐段解析符号链接）。</param>
    /// <param name="error">不通过时的中文原因；**刻意不含宿主绝对路径**，见备注。</param>
    /// <returns>在应用目录内时 true。</returns>
    /// <remarks>
    /// <para>
    /// <b>为什么要收口</b>（2026-11-11 评审 M4）：<c>tray.*</c> 是库保留前缀、**零授权即可调用**，
    /// 而 <c>iconPath</c> 当时页面任给。于是"不触及文件系统"的放行理由不成立——页面能读任意
    /// PNG/ICO，还能靠 <c>FileNotFoundException</c> 在两种失败之间区分出"这个文件存不存在"，
    /// 而那是一个可用的路径探测器。应用图标本来就在可执行文件目录里（相对路径的解析基准也是它），
    /// 限定在这里不会挡掉任何正常用法。
    /// </para>
    /// <para>
    /// <b>错误文案里不能出现绝对路径</b>：内建命令的异常原样透传给页面（那是排障信息的取舍），
    /// 所以这里若把 <c>Path.GetFullPath</c> 的结果写进文案，等于把 M4 想堵的那条探测通道
    /// 又开在错误消息里。原因只说"不在应用目录内"，具体路径由调用方自己知道。
    /// </para>
    /// <para>
    /// <b>符号链接同样要解析</b>：应用目录里放一个指向外部的链接与直接写绝对路径是同一件事，
    /// 解析之后越界的照样拒绝（与 <see cref="OrielLinkResolver"/> 在 fs/opener 上的用法一致）。
    /// </para>
    /// </remarks>
    internal static bool TryResolveAppResource(string path, out string? full, out string? error)
    {
        full = null;
        error = null;

        if (string.IsNullOrWhiteSpace(path))
        {
            error = "路径为空。";
            return false;
        }

        string absolute;
        try
        {
            absolute = Path.GetFullPath(Resolve(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = "路径不合法（含非法字符或过长）。";
            return false;
        }

        string target;
        try
        {
            target = OrielLinkResolver.Resolve(absolute);
        }
        catch (OrielIpcException)
        {
            error = "路径上的符号链接无法解析（成环、跨卷或权限不足）。";
            return false;
        }

        if (!IsWithinDirectory(AppContext.BaseDirectory, target))
        {
            error = "路径不在应用目录（可执行文件所在目录）内。";
            return false;
        }

        full = target;
        return true;
    }

    /// <summary>候选路径是否落在某个目录之下（含目录自身）；大小写规则与 scope 判定一致。</summary>
    private static bool IsWithinDirectory(string directory, string candidate)
    {
        // 与 OrielPathScope 同一套大小写规则：Linux 敏感、Windows/macOS 的默认文件系统不敏感。
        // 这里用相反的一侧会**误放行**（不敏感当敏感），所以与 scope 保持一致比"更严格"更重要。
        StringComparison comparison = OperatingSystem.IsLinux()
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        string other = Path.TrimEndingDirectorySeparator(candidate);

        if (string.Equals(root, other, comparison))
        {
            return true;
        }

        return other.StartsWith(root + Path.DirectorySeparatorChar, comparison)
            || other.StartsWith(root + Path.AltDirectorySeparatorChar, comparison);
    }
}
