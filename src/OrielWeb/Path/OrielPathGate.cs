using System.Text.Json;
using OrielWeb.Ipc;

namespace OrielWeb;

/// <summary>
/// 路径类命令的统一闸门：归一 → 命令授权 → 逐段链接解析 → 路径 scope。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么这三步必须绑在一起、且是这个顺序</b>：<c>fs.*</c> 与 <c>opener.*</c> 都是"命令 + 路径"
/// 双关——命令授权只说"这个页面能调这个动作"，没说"能做在哪个路径上"。少了 scope，
/// 一条 XSS 就能读走 <c>~/.ssh/id_rsa</c>；少了链接解析，scope 目录里放一个指向它的链接同样能读走；
/// 顺序反了（先判 scope 再解析）等于用"链接所在位置"的合法性去担保"链接指向的目标"。
/// 这三件事各写一遍的话，总有一天其中一处会少一步——而少一步的表现是静默放行。
/// </para>
/// <para>
/// <b>2026-11-11 评审 H1 的教训</b>：<c>opener.openPath</c> 当时只判"绝对路径 + 存在"，
/// 于是授权一次就等于"拉起机器上任意已存在的 <c>.exe/.bat/.hta</c>"（配合 fs 往 scope 内写一个
/// <c>.bat</c> 再打开，构成完整 RCE 链）。逐段链接解析与 scope 当时只长在 fs 那一侧。
/// </para>
/// </remarks>
internal static class OrielPathGate
{
    /// <summary>
    /// 校验一个来自命令参数的路径，返回**可安全交给下游 I/O** 的绝对路径。
    /// </summary>
    /// <param name="window">来源窗口（拿门禁与应用标识符）。</param>
    /// <param name="command">命令名（用于匹配 scope 与报错）。</param>
    /// <param name="args">命令参数（门禁读 <c>label</c> 判目标窗口用）。</param>
    /// <param name="raw">待校验的原始路径（可能相对、可能含 <c>..</c>）。</param>
    /// <param name="argName">报错时用的参数名（数组场景下是 <c>paths[i]</c>）。</param>
    /// <returns>已归一、已解析链接、已过 scope 的绝对路径。</returns>
    /// <exception cref="OrielIpcException">
    /// 路径不合法（<c>invalid_argument</c>）、命令未授权（<c>not_allowed</c>）、
    /// 链接解析失败或路径不在 scope 内（<c>path_not_allowed</c>）。
    /// </exception>
    internal static string Authorize(
        IOrielWindowControl window, string command, JsonElement args, string raw, string argName)
        => Authorize(window, command, args, raw, argName, out _);

    /// <summary>
    /// 同上，额外给出"入参本身是不是符号链接"。
    /// </summary>
    /// <param name="window">来源窗口（拿门禁与应用标识符）。</param>
    /// <param name="command">命令名（用于匹配 scope 与报错）。</param>
    /// <param name="args">命令参数（门禁读 <c>label</c> 判目标窗口用）。</param>
    /// <param name="raw">待校验的原始路径（可能相对、可能含 <c>..</c>）。</param>
    /// <param name="argName">报错时用的参数名（数组场景下是 <c>paths[i]</c>）。</param>
    /// <param name="isSymlink">
    /// 入参路径本身是不是符号链接（**不跟随**：断链也答 true）。
    /// 必须在解析链接之前取——解析之后手上只剩目标，"入参是链接"这件事就永久丢失了
    /// （<c>fs.stat</c> / <c>fs.lstat</c> 的 <c>isSymlink</c> 字段曾因此恒为 false）。
    /// </param>
    /// <returns>已归一、已解析链接、已过 scope 的绝对路径。</returns>
    /// <exception cref="OrielIpcException">
    /// 路径不合法（<c>invalid_argument</c>）、命令未授权（<c>not_allowed</c>）、
    /// 链接解析失败或路径不在 scope 内（<c>path_not_allowed</c>）。
    /// </exception>
    internal static string Authorize(
        IOrielWindowControl window,
        string command,
        JsonElement args,
        string raw,
        string argName,
        out bool isSymlink)
    {
        if (raw.Length == 0)
        {
            throw new OrielIpcException($"命令参数 '{argName}' 不能为空。")
            {
                Code = OrielIpcErrorCodes.InvalidArgument,
            };
        }

        string full;

        try
        {
            full = Path.GetFullPath(raw);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new OrielIpcException($"路径不合法：{ex.Message}", ex)
            {
                Code = OrielIpcErrorCodes.InvalidArgument,
            };
        }

        // 相对路径按进程当前目录解析（写进错误文案，免得"相对谁"变成一个猜测）
        //
        // **命令授权在这里再查一遍**：分发器已经查过了，所以这不影响正常运行；
        // 但它让"没声明这个命令"与"声明了命令但路径不对"在**任何入口**下都能分开报。
        // 少了这一遍的话，直接调处理函数（测试、将来可能的其它调用方）会把
        // "命令没授权"报成 path_not_allowed，而那个错因描述会把人引向错的方向。
        if (!window.App.Guard.TryAuthorize(command, args, window, out string? commandReason))
        {
            throw new OrielIpcException(commandReason ?? $"命令 '{command}' 未被授权。")
            {
                Code = OrielIpcErrorCodes.NotAllowed,
            };
        }

        // 归一之后、判 scope 之前，把符号链接解析到最终目标。
        // **顺序不能反**：先判 scope 再解析，等于用"链接所在位置"的合法性去担保"链接指向的目标"——
        // allowed 目录里放一个指向 ~/.ssh/id_rsa 的链接就能读走它，而 scope 要防的正是这一层。
        // 注意模式那一侧也要解析（在 OrielPathScope 里做），否则 macOS 的 /var → /private/var
        // 会让两边永远对不上。
        isSymlink = IsSymlink(full);
        full = OrielLinkResolver.Resolve(full);

        if (!window.App.Guard.TryAuthorizePath(command, full, window.App.Info.Identifier, out string? reason))
        {
            throw new OrielIpcException(reason ?? $"路径不在 '{command}' 的 scope 内。")
            {
                Code = OrielIpcErrorCodes.PathNotAllowed,
            };
        }

        return full;
    }

    /// <summary>入参路径本身是不是符号链接（**不跟随**：断链也答 true）。</summary>
    /// <param name="full">已归一、尚未解析链接的绝对路径。</param>
    /// <remarks>
    /// <para>
    /// 判据是 <see cref="FileSystemInfo.LinkTarget"/> 而不是
    /// <see cref="FileAttributes.ReparsePoint"/>：后者对 junction、OneDrive 占位符同样为真，
    /// 会把"不是链接"的东西报成链接。
    /// </para>
    /// <para>
    /// <b>目录判定仍然要 <see cref="Directory.Exists(string)"/> 先试一次</b>：链接自身的
    /// <c>LinkTarget</c> 在 <see cref="FileInfo"/> 与 <see cref="DirectoryInfo"/> 上都能读到，
    /// 但选错类型会连带影响后续对该实例的其它读取；按"指向目录"与"其余"分两支，与
    /// <see cref="OrielLinkResolver.Resolve"/> 的取法保持一致。
    /// </para>
    /// </remarks>
    internal static bool IsSymlink(string full)
    {
        FileSystemInfo info = Directory.Exists(full)
            ? new DirectoryInfo(full)
            : new FileInfo(full);

        return info.LinkTarget is not null;
    }
}
