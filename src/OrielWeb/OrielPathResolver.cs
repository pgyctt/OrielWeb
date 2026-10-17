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
}
