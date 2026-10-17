using System.Globalization;
using System.Text;
using System.Text.Json;

namespace OrielWeb.Ipc;

/// <summary>
/// 内建文件命令族（<c>fs.*</c>）：对标 Tauri 2 的 <c>@tauri-apps/plugin-fs</c>。
/// </summary>
/// <remarks>
/// <para>
/// <b>本族每一个命令都要过两道关</b>：命令授权（<c>Allow("fs.readTextFile")</c>）与
/// **路径 scope**（<c>Allow("fs.readTextFile", s =&gt; s.AllowPath("$APPDATA/**"))</c>）。
/// 少了后者，一条 XSS 就能把 <c>~/.ssh/id_rsa</c> 读走——所以本族**不整族放行**，
/// 也不像 <c>path.*</c> 那样"只做运算所以安全"。
/// </para>
/// <para>
/// <b>本族不含句柄式 API</b>（Tauri 的 <c>open</c> / <c>create</c> / <c>FileHandle</c> /
/// <c>seek</c> / <c>read</c> / <c>write</c> / <c>readTextFileLines</c> / <c>watch</c>）。
/// 那些靠一张 <c>rid</c> 句柄表，而本库**没有句柄表**（见 packages/api/README 的说明）——
/// 加它是一整套新机制（分配/回收/跨调用生命周期），不该混在"文件操作"这一批里做。
/// 整文件读写覆盖了 webview 界面里绝大多数场景。
/// </para>
/// <para>
/// <b>二进制走 JSON 数字数组</b>：<c>readFile</c> 返回 <c>byte[]</c>（页面侧
/// <c>new Uint8Array(arr)</c>），<c>writeFile</c> 收同样的形状。Tauri 用 <c>invoke</c> 的
/// 二进制 body 传，本库的 IPC 是纯 JSON，所以没有那条路。后果大文件会明显变慢
/// （一个字节变成两三个字符），几十 MB 级别的文件不适用——已在文档里写明。
/// </para>
/// </remarks>
internal static class OrielBuiltInFsCommands
{
    /// <summary>
    /// 写文本文件用的 UTF-8 编码：**不带 BOM**。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Encoding.UTF8"/> 这个静态属性在写文件时**会写 BOM**（三个字节
    /// <c>EF BB BF</c>），因为它的 <c>encoderShouldEmitUTF8Identifier</c> 是 true。
    /// 后果很具体：写一个 5 个汉字的文件，<c>fs.size</c> 给的是 18 而不是 15——
    /// 而调用方算出来的是字符数乘 3。
    /// </para>
    /// <para>
    /// 更麻烦的是下游：BOM 会让不少解析器（JSON、CSV、shell 脚本、grep）把文件首字节
    /// 当成内容的一部分。Tauri 的 <c>writeTextFile</c> 走 <c>String::into_bytes()</c>，
    /// **不写 BOM**，所以这里也对齐它。
    /// </para>
    /// <para>
    /// 读的时候不必操心：<see cref="File.ReadAllText(string, Encoding)"/> 会自己探测 BOM
    /// 并去掉，所以别人写的带 BOM 文件也能正确读出来。
    /// </para>
    /// </remarks>
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>命令名前缀。</summary>
    internal const string Prefix = "fs.";

    /// <summary>取某个 <c>fs.*</c> 命令的处理函数；不是本族命令时返回 false。</summary>
    /// <remarks>
    /// 处理函数收 <see cref="IOrielWindowControl"/>：既为拿应用标识符（展开 scope 里的变量），
    /// 也为过门禁（<c>sender.App.Guard</c>）。
    /// </remarks>
    internal static bool TryGet(string name, out Func<IOrielWindowControl, string, JsonElement, object?> handler)
    {
        switch (name)
        {
            // ---- 读 ----
            case "fs.readTextFile":
                handler = static (window, command, args) => File.ReadAllText(AuthorizePath(window, command, args, out _), Utf8NoBom);
                return true;

            case "fs.readFile":
                handler = static (window, command, args) => File.ReadAllBytes(AuthorizePath(window, command, args, out _));
                return true;

            case "fs.exists":
                // exists 也要过 scope：不过的话它就成了一个"这台机器上有哪些文件"的探测器，
                // 而那种信息在攻防里很有用（先枚举、再决定读什么）。
                handler = static (window, command, args) => File.Exists(AuthorizePath(window, command, args, out _))
                    || Directory.Exists(AuthorizePath(window, command, args, out _));
                return true;

            case "fs.readDir":
                handler = static (window, command, args) => ReadDir(AuthorizePath(window, command, args, out _));
                return true;

            case "fs.size":
                handler = static (window, command, args) => new FileInfo(AuthorizePath(window, command, args, out _)).Length;
                return true;

            case "fs.stat":
                handler = static (window, command, args) => Stat(AuthorizePath(window, command, args, out _), followLinks: true);
                return true;

            case "fs.lstat":
                handler = static (window, command, args) => Stat(AuthorizePath(window, command, args, out _), followLinks: false);
                return true;

            // ---- 写 ----
            case "fs.writeTextFile":
                handler = static (window, command, args) =>
                {
                    string target = AuthorizePath(window, command, args, out JsonElement data);
                    EnsureParentDirectory(target);
                    File.WriteAllText(target, ReadString(data), Utf8NoBom);
                    return null;
                };
                return true;

            case "fs.writeFile":
                handler = static (window, command, args) =>
                {
                    string target = AuthorizePath(window, command, args, out JsonElement data);
                    EnsureParentDirectory(target);
                    File.WriteAllBytes(target, ReadBytes(data));
                    return null;
                };
                return true;

            case "fs.mkdir":
                handler = static (window, command, args) =>
                {
                    string target = AuthorizePath(window, command, args, out _);
                    bool recursive = ReadBool(args, "recursive", fallback: false);

                    if (Directory.Exists(target))
                    {
                        // 非递归且已存在 → 报错（与 Tauri/Node 一致）。递归时已存在是正常的，
                        // 因为递归的语义就是"确保存在"。
                        if (!recursive)
                        {
                            throw new IOException($"目录已存在：{target}");
                        }

                        return null;
                    }

                    Directory.CreateDirectory(target);
                    return null;
                };
                return true;

            case "fs.remove":
                handler = static (window, command, args) =>
                {
                    string target = AuthorizePath(window, command, args, out _);
                    bool recursive = ReadBool(args, "recursive", fallback: false);

                    if (Directory.Exists(target))
                    {
                        Directory.Delete(target, recursive);
                    }
                    else if (File.Exists(target))
                    {
                        File.Delete(target);
                    }

                    return null;
                };
                return true;

            case "fs.rename":
                handler = static (window, command, args) =>
                {
                    // **两个路径都要过 scope**：只查源不查目标的话，页面能把
                    // $APPDATA 里的文件"搬"到 scope 之外（那是一次写）。
                    string from = AuthorizePath(window, command, args, out _, argName: "oldPath");
                    string to = AuthorizePath(window, command, args, out _, argName: "newPath");

                    EnsureParentDirectory(to);

                    if (Directory.Exists(from))
                    {
                        Directory.Move(from, to);
                    }
                    else
                    {
                        File.Move(from, to);
                    }

                    return null;
                };
                return true;

            case "fs.copyFile":
                handler = static (window, command, args) =>
                {
                    // 同 rename：目标是写，也要过 scope
                    string from = AuthorizePath(window, command, args, out _, argName: "oldPath");
                    string to = AuthorizePath(window, command, args, out _, argName: "newPath");

                    EnsureParentDirectory(to);
                    File.Copy(from, to, overwrite: true);
                    return null;
                };
                return true;

            case "fs.truncate":
                handler = static (window, command, args) =>
                {
                    string target = AuthorizePath(window, command, args, out _);
                    long length = OrielJson.GetRequiredArg<long>(args, "len", null);

                    if (length < 0)
                    {
                        throw new OrielIpcException($"截断长度不能为负：{length}")
                        {
                            Code = OrielIpcErrorCodes.InvalidArgument,
                        };
                    }

                    using var stream = new FileStream(target, FileMode.Open, FileAccess.Write, FileShare.None);
                    stream.SetLength(length);
                    return null;
                };
                return true;

            default:
                handler = null!;
                return false;
        }
    }

    // ---------------------------------------------------------------- 路径授权

    /// <summary>
    /// 读参数里的路径、**归一**、过 scope，返回可用的绝对路径。
    /// </summary>
    /// <param name="window">来源窗口（拿应用标识符与门禁）。</param>
    /// <param name="command">命令名（用于匹配 scope 与报错）。</param>
    /// <param name="args">命令参数。</param>
    /// <param name="data">顺带取出 <c>data</c> 字段（写命令用）；不需要时忽略。</param>
    /// <param name="argName">路径参数名（默认 <c>path</c>；<c>rename</c>/<c>copyFile</c> 用别的）。</param>
    /// <remarks>
    /// <para>
    /// <b>顺序很关键：先归一，再判 scope。</b> 反过来的话
    /// <c>$APPDATA/../../etc/passwd</c> 会先被 scope 判成"在 $APPDATA 之内"（字面量确实在内），
    /// 然后归一后指到别处——这就是经典的 scope 绕过。归一是 <see cref="Path.GetFullPath(string)"/>。
    /// </para>
    /// <para>
    /// <b>已知限制：不解析符号链接。</b> 若 allowed 目录里有个符号链接指向外部，
    /// 文本层面的 scope 判定会通过。解析真实路径（<c>ResolveLinkTarget</c>）要做多次
    /// 文件系统访问且对不存在的路径无效（写文件时目标通常还不存在），
    /// 所以这里不做——已记入文档的"已知限制"，而不是假装它不存在。
    /// </para>
    /// </remarks>
    private static string AuthorizePath(
        IOrielWindowControl window,
        string command,
        JsonElement args,
        out JsonElement data,
        string argName = "path")
    {
        string raw = OrielJson.GetRequiredArg<string>(args, argName, null);

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
        if (!window.App.Guard.TryAuthorize(command, out string? commandReason))
        {
            throw new OrielIpcException(commandReason ?? $"命令 '{command}' 未被授权。")
            {
                Code = OrielIpcErrorCodes.NotAllowed,
            };
        }

        if (!window.App.Guard.TryAuthorizePath(command, full, window.App.Info.Identifier, out string? reason))
        {
            throw new OrielIpcException(reason ?? $"路径不在 '{command}' 的 scope 内。")
            {
                Code = OrielIpcErrorCodes.PathNotAllowed,
            };
        }

        // data 与 path 一起取出来：写命令两者都要，分两次取会让"取 path 时顺手验了 scope"
        // 这个顺序保证被拆散
        data = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("data", out JsonElement value)
            ? value
            : default;

        return full;
    }

    /// <summary>读字符串参数；类型不符时给 invalid_argument。</summary>
    private static string ReadString(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.String)
        {
            throw new OrielIpcException($"命令参数 'data' 的类型不符：期望 String，实际 {data.ValueKind}。")
            {
                Code = OrielIpcErrorCodes.InvalidArgument,
            };
        }

        return data.GetString()!;
    }

    /// <summary>
    /// 读字节数组参数（JSON 数字数组，0..255）。
    /// </summary>
    /// <remarks>
    /// 逐个检查范围而不是直接 <c>(byte)</c> 强转：强转会把 300 变成 44，静默写坏数据。
    /// 而"写进去的文件内容不对"是最难归因的一类故障（看起来像编码问题）。
    /// </remarks>
    private static byte[] ReadBytes(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Array)
        {
            throw new OrielIpcException($"命令参数 'data' 的类型不符：期望 Array，实际 {data.ValueKind}。")
            {
                Code = OrielIpcErrorCodes.InvalidArgument,
            };
        }

        var bytes = new List<byte>(data.GetArrayLength());
        int index = 0;

        foreach (JsonElement item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt32(out int value) || value is < 0 or > 255)
            {
                throw new OrielIpcException(
                    $"命令参数 'data' 的第 {index} 个元素不是 0..255 的整数（实际 {item.GetRawText()}）。")
                {
                    Code = OrielIpcErrorCodes.InvalidArgument,
                };
            }

            bytes.Add((byte)value);
            index++;
        }

        return [.. bytes];
    }

    /// <summary>读可选布尔参数。</summary>
    private static bool ReadBool(JsonElement args, string name, bool fallback)
        => args.ValueKind == JsonValueKind.Object
           && args.TryGetProperty(name, out JsonElement value)
           && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : fallback;

    /// <summary>
    /// 确保目标的父目录存在。
    /// </summary>
    /// <remarks>
    /// 写文件时自动建父目录：不建的话页面每次写都要先 <c>mkdir</c>，
    /// 而漏建的症状是 <c>DirectoryNotFoundException</c>——那个报错说的是"目录不存在"，
    /// 而调用方以为自己在写文件。
    /// <b>只建到 scope 允许的层</b>：父目录本身不需要单独过 scope，
    /// 因为它是由"允许写的那个路径"决定的，不是调用方另指定的。
    /// </remarks>
    private static void EnsureParentDirectory(string target)
    {
        string? parent = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
        {
            Directory.CreateDirectory(parent);
        }
    }

    /// <summary>读目录内容（对标 Tauri 的 <c>readDir</c>）。</summary>
    /// <remarks>
    /// 手写 JSON 而不是返回 record：内建命令的返回值走 <see cref="OrielJson.WriteResult"/>，
    /// 而它只认基元与 <see cref="JsonElement"/>——record 要求**应用**注册那个类型的
    /// STJ 上下文（应用凭什么知道库内部有个 <c>DirEntry</c>？），漏注册的症状是运行期报错。
    /// 而且 STJ 默认按 PascalCase 写属性名，页面侧就得写 <c>entry.Name</c>——
    /// 与 Tauri 的 <c>entry.name</c> 不一致。
    /// </remarks>
    private static JsonElement ReadDir(string path)
    {
        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException($"目录不存在：{path}");
        }

        var json = new StringBuilder("[");
        bool first = true;

        foreach (string entry in Directory.EnumerateFileSystemEntries(path))
        {
            FileAttributes attributes = File.GetAttributes(entry);
            bool isDirectory = attributes.HasFlag(FileAttributes.Directory);
            bool isSymlink = attributes.HasFlag(FileAttributes.ReparsePoint);

            if (!first)
            {
                json.Append(',');
            }

            first = false;

            json.Append("{\"name\":").Append(JsonText.EncodeString(Path.GetFileName(entry)))
                .Append(",\"isFile\":").Append(JsonBool(!isDirectory))
                .Append(",\"isDirectory\":").Append(JsonBool(isDirectory))
                .Append(",\"isSymlink\":").Append(JsonBool(isSymlink))
                .Append('}');
        }

        return Parse(json.Append(']').ToString());
    }

    /// <summary>取文件/目录的元信息（对标 Tauri 的 <c>stat</c> / <c>lstat</c>）。</summary>
    /// <remarks>
    /// <para>
    /// 返回 <see cref="JsonElement"/> 的理由同 <see cref="ReadDir"/>。
    /// </para>
    /// <para>
    /// <c>followLinks</c> 的差别只体现在"链接自身算不算文件"上：.NET 的
    /// <see cref="FileSystemInfo"/> 对符号链接**跟随**（<c>Directory.Exists</c> 判的是目标），
    /// 所以这里如实报告"它是个链接"，而不是假装 <c>lstat</c> 与 <c>stat</c> 有两套元数据——
    /// 编出链接自身的长度/时间在 .NET 上要额外做系统调用，而本库目前不需要那个精度。
    /// </para>
    /// </remarks>
    private static JsonElement Stat(string path, bool followLinks)
    {
        FileSystemInfo info = Directory.Exists(path)
            ? new DirectoryInfo(path)
            : new FileInfo(path);

        if (!info.Exists)
        {
            throw new FileNotFoundException($"路径不存在：{path}");
        }

        bool isSymlink = info.LinkTarget is not null;
        bool isDirectory = Directory.Exists(path);
        long size = info is FileInfo file ? file.Length : 0;

        _ = followLinks;

        return Parse(
            "{\"isFile\":" + JsonBool(!isDirectory) +
            ",\"isDirectory\":" + JsonBool(isDirectory) +
            ",\"isSymlink\":" + JsonBool(isSymlink) +
            ",\"size\":" + size.ToString(CultureInfo.InvariantCulture) +
            // 毫秒时间戳（Unix epoch）。**取 UTC**：本地时区参与进来会让同一份数据
            // 在不同机器上不一致，而"文件时间差 8 小时"这类现象很难归因。
            ",\"mtime\":" + new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) +
            ",\"atime\":" + new DateTimeOffset(info.LastAccessTimeUtc).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) +
            ",\"birthtime\":" + new DateTimeOffset(info.CreationTimeUtc).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) +
            "}");
    }

    private static string JsonBool(bool value) => value ? "true" : "false";

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
