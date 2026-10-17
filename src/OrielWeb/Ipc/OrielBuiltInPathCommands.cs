using System.Text.Json;

namespace OrielWeb.Ipc;

/// <summary>
/// 内建路径命令族（<c>path.*</c>）：对标 Tauri 2 的 <c>@tauri-apps/api/path</c> 里的路径运算部分。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么这些"纯字符串运算"放在宿主而不是 JS 里做</b>：这是本批唯一一个反直觉的决定，
/// 所以写清楚。Tauri 2 自己就是全部走 IPC 的（只有 <c>sep</c> / <c>delimiter</c> 是同步常量），
/// 我一开始以为可以在 JS 里实现，读源码后发现不是。
/// </para>
/// <para>
/// 真正让它在宿主做的理由不是"照抄 Tauri"，而是**平台路径规则的正确性**：
/// Windows 的盘符、UNC（<c>\\server\share</c>）、扩展路径前缀（<c>\\?\</c>）、
/// 大小写不敏感，以及 POSIX 的重复斜杠、尾随斜杠、<c>~</c> 不展开等等，
/// 手写一份 JS 实现就是**又造一份平台语义**——而两份实现必然在某个边角上不一致，
/// 症状是"只在某个平台错"的那类缺陷。宿主已经有 <see cref="System.IO.Path"/>，
/// 那是这些规则在 .NET 里的唯一权威实现。
/// </para>
/// <para>
/// <b>本族不做文件系统 I/O</b>：<c>normalize</c> 与 <c>resolve</c> 走的是
/// <see cref="Path.GetFullPath(string)"/>（词法归一 + 拼接当前工作目录），
/// **不解析符号链接**。见 <see cref="Normalize"/> 的 remarks——那里说明了与 Tauri 的差别。
/// </para>
/// <para>
/// <b>门禁姿态</b>：本族**始终放行**（见 <see cref="OrielCapabilityRules.AlwaysAllowedPathCommands"/>）。
/// 理由见那里的说明。
/// </para>
/// </remarks>
internal static class OrielBuiltInPathCommands
{
    /// <summary>命令名前缀。</summary>
    internal const string Prefix = "path.";

    /// <summary>取某个 <c>path.*</c> 命令的处理函数；不是本族命令时返回 false。</summary>
    /// <remarks>
    /// 处理函数只收 <c>args</c>——本族命令**不需要来源窗口**，这是它与 <c>win.*</c> /
    /// <c>window.*</c> 的结构性差别（那些在缺少来源窗口时要报错）。
    /// </remarks>
    internal static bool TryGet(string name, out Func<JsonElement, object?> handler)
    {
        switch (name)
        {
            case "path.join":
                handler = static args => Path.Join(GetPaths(args));
                return true;

            case "path.dirname":
                handler = static args => Path.GetDirectoryName(
                    TrimTrailingSeparators(OrielJson.GetRequiredArg<string>(args, "path", null))) ?? string.Empty;
                return true;

            case "path.extname":
                handler = static args => ExtName(OrielJson.GetRequiredArg<string>(args, "path", null));
                return true;

            case "path.basename":
                handler = static args => BaseName(args);
                return true;

            case "path.isAbsolute":
                handler = static args => Path.IsPathFullyQualified(OrielJson.GetRequiredArg<string>(args, "path", null));
                return true;

            case "path.normalize":
                handler = static args => Normalize(OrielJson.GetRequiredArg<string>(args, "path", null));
                return true;

            case "path.resolve":
                handler = static args => Normalize(Path.Join(GetPaths(args)));
                return true;

            default:
                handler = null!;
                return false;
        }
    }

    /// <summary>读必需参数 <c>paths</c>（字符串数组）。</summary>
    private static string[] GetPaths(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("paths", out JsonElement element))
        {
            throw new OrielIpcException("命令缺少必需参数 'paths'。")
            {
                Code = OrielIpcErrorCodes.MissingArgument,
            };
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new OrielIpcException($"命令参数 'paths' 的类型不符：期望 Array，实际 {element.ValueKind}。")
            {
                Code = OrielIpcErrorCodes.InvalidArgument,
            };
        }

        var paths = new List<string>();
        foreach (JsonElement item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw new OrielIpcException($"命令参数 'paths' 的元素必须是字符串，实际 {item.ValueKind}。")
                {
                    Code = OrielIpcErrorCodes.InvalidArgument,
                };
            }

            paths.Add(item.GetString()!);
        }

        return [.. paths];
    }

    /// <summary>
    /// 扩展名，**不带点**（对标 Tauri 与 Rust 的 <c>Path::extension</c>）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>这里不能直接用 <see cref="Path.GetExtension(string)"/></b>：.NET 与 Rust 对
    /// <b>以点开头的隐藏文件</b>看法不同。.NET 认为 <c>.gitignore</c> 的扩展名是
    /// <c>gitignore</c>，而 Rust 的 <c>Path::extension()</c>（以及 Node 的
    /// <c>path.extname</c>）都认为它**没有扩展名**——整个名字是文件名。
    /// </para>
    /// <para>
    /// 从 Tauri 迁过来的代码走的是 Rust 口径，所以这里显式实现那条规则：
    /// 扩展名是**文件名里最后一个点之后的部分**，且那个点不能在第 0 位、也不能在末尾。
    /// </para>
    /// <para>
    /// 顺带两处与 Node 的差别（都与 Tauri 一致）：返回值不含前导点；没有扩展名时给空串而非抛错。
    /// </para>
    /// </remarks>
    private static string ExtName(string path)
    {
        string name = Path.GetFileName(TrimTrailingSeparators(path));
        int dot = name.LastIndexOf('.');

        // dot > 0：前导点不算扩展名分隔符（.gitignore 没有扩展名）
        // dot < name.Length - 1：尾随点之后是空的，同样没有扩展名（archive.tar.）
        return dot > 0 && dot < name.Length - 1 ? name[(dot + 1)..] : string.Empty;
    }

    /// <summary>
    /// 末段文件名；给了 <c>ext</c> 时把它从结果里去掉。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 先去尾随分隔符再取名字：<see cref="Path.GetFileName(string)"/> 对 <c>"/a/b/"</c>
    /// 给空串，而 Rust 的 <c>Path::file_name()</c>（Tauri 的 <c>basename</c>）给 <c>b</c>
    /// ——它把尾随分隔符忽略掉。Tauri 的文档里就写着"trailing directory separators are ignored"。
    /// </para>
    /// <para>
    /// <c>ext</c> 的比较**区分大小写**（与 Tauri 一致）：不同平台的文件名大小写规则不同，
    /// 而"传了什么就按什么比"是最不容易猜错的口径。要去掉 <c>.PNG</c> 就传 <c>PNG</c>。
    /// </para>
    /// </remarks>
    private static string BaseName(JsonElement args)
    {
        string path = OrielJson.GetRequiredArg<string>(args, "path", null);
        string name = Path.GetFileName(TrimTrailingSeparators(path));

        if (args.TryGetProperty("ext", out JsonElement ext)
            && ext.ValueKind == JsonValueKind.String)
        {
            string suffix = ext.GetString()!;
            if (suffix.Length > 0 && name.EndsWith(suffix, StringComparison.Ordinal))
            {
                name = name[..^suffix.Length];
            }
        }

        return name;
    }

    /// <summary>
    /// 去掉尾随的目录分隔符，但**不动根**。
    /// </summary>
    /// <remarks>
    /// 与根的长度比较，而不是"一直剪到空"：<c>"C:\"</c> 剪成 <c>"C:"</c> 之后
    /// <see cref="Path.GetFileName(string)"/> 会给出 <c>"C:"</c>——那是个看起来像文件名、
    /// 实际什么都不是的东西。根必须原样保留。
    /// </remarks>
    private static string TrimTrailingSeparators(string path)
    {
        string root = Path.GetPathRoot(path) ?? string.Empty;
        int end = path.Length;

        while (end > root.Length && (path[end - 1] == '/' || path[end - 1] == '\\'))
        {
            end--;
        }

        return path[..end];
    }

    /// <summary>
    /// 词法归一：展开 <c>.</c> / <c>..</c>、统一分隔符、拼成绝对路径。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>与 Tauri 的差别（刻意，且是改进）</b>：Tauri 的 <c>normalize</c> 用
    /// <c>std::fs::canonicalize</c>，它**会解析符号链接**，而且**路径不存在时直接报错**。
    /// 后者是个很容易踩的坑："把用户输入的路径归一一下"在路径还不存在时就会失败。
    /// </para>
    /// <para>
    /// 本库用 <see cref="Path.GetFullPath(string)"/>：纯词法，**不做任何文件系统访问**，
    /// 所以对不存在的路径也能正确工作，也不会因为路径里有一段符号链接就给出意外的结果。
    /// 需要"真实路径"（解析链接）应当是 <c>fs</c> 模块的一个显式操作，
    /// 而不是一个叫 <c>normalize</c> 的函数偷偷做的事。
    /// </para>
    /// <para>
    /// 路径里含 NUL 等非法字符时 <see cref="Path.GetFullPath(string)"/> 会抛
    /// <see cref="ArgumentException"/>——那会被转成 <c>invalid_argument</c> 到页面。
    /// </para>
    /// </remarks>
    private static string Normalize(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new OrielIpcException($"路径不合法，无法归一：{ex.Message}", ex)
            {
                Code = OrielIpcErrorCodes.InvalidArgument,
            };
        }
    }
}
