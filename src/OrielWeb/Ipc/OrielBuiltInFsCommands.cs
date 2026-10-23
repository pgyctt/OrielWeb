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
/// <b>句柄族自 B17 起提供</b>（Tauri 的 <c>open</c> / <c>create</c> / <c>FileHandle</c> /
/// <c>seek</c> / <c>read</c> / <c>write</c> / <c>readTextFileLines</c>）：宿主侧有一张
/// <see cref="OrielFileHandleRegistry"/> 的 id 句柄表（应用级，对标 Tauri 的资源表）。
/// <b>scope 只在 <c>open</c>/<c>create</c> 时判</b>——拿到 id 之后的读写不再看路径，
/// 与 Tauri 相同（理由见句柄表的说明）。
/// </para>
/// <para>
/// <b>监视自 B20 起提供</b>（<c>fs.watch</c> / <c>fs.unwatch</c>）：宿主侧是应用级的
/// <see cref="OrielFsWatcherRegistry"/>，每次监视一个编号，事件经
/// <c>oriel://fs-watch/&lt;id&gt;</c> 只回给发起它的那个窗口。
/// <b><c>paths</c> 的每一项都单独过 scope</b>（不是只看第一项）——只放行第一项的话，
/// 后面的路径就成了"搭便车"。
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

            // ---- 句柄式（B17，对标 Tauri 的 open / create / FileHandle / SeekMode）----
            //
            // **scope 只在 open / create 时判**：拿到 id 之后的读写不再看路径——句柄代表
            // "已经过授权的那个打开的文件"，反复按路径重判既无意义（路径可能已变），
            // 也会把"文件改名后就读不了"变成常态。见 OrielFileHandleRegistry 的说明。

            case "fs.open":
                handler = static (window, command, args) =>
                {
                    string target = AuthorizePath(window, command, args, out _);
                    FileStream stream = OpenHandleStream(
                        target,
                        read: ReadBool(args, "read", fallback: false),
                        write: ReadBool(args, "write", fallback: false),
                        append: ReadBool(args, "append", fallback: false),
                        truncate: ReadBool(args, "truncate", fallback: false),
                        create: ReadBool(args, "create", fallback: false),
                        createNew: ReadBool(args, "createNew", fallback: false));

                    return window.App.FileHandles.Add(stream, target);
                };
                return true;

            case "fs.create":
                handler = static (window, command, args) =>
                {
                    // create = 读 + 写 + 建（不存在时）+ 截断（存在时），与 Tauri 的
                    // CreateOptions 语义一致。父目录**自动创建**——与本库 writeTextFile
                    // 的既有约定对齐：同一个库里两种"新建文件"对父目录的姿态不该不同。
                    string target = AuthorizePath(window, command, args, out _);
                    EnsureParentDirectory(target);
                    FileStream stream = OpenHandleStream(
                        target,
                        read: true, write: true, append: false,
                        truncate: true, create: true, createNew: false);

                    return window.App.FileHandles.Add(stream, target);
                };
                return true;

            case "fs.read":
                handler = static (window, command, args) =>
                {
                    long id = OrielJson.GetRequiredArg<long>(args, "id", null);
                    int length = OrielJson.GetRequiredArg<int>(args, "length", null);

                    if (length <= 0 || length > MaxReadChunk)
                    {
                        throw new OrielIpcException(
                            $"fs.read: length 必须在 1..{MaxReadChunk} 之间（实际 {length}）。" +
                            "块大小由调用方决定，库只挡「一次读一个 G」这类意外。")
                        {
                            Code = OrielIpcErrorCodes.InvalidArgument,
                        };
                    }

                    FileStream stream = window.App.FileHandles.Get(id, command).Stream;
                    byte[] buffer = new byte[length];
                    int read = stream.Read(buffer, 0, length);

                    // EOF 返回 null（Tauri 的 read 同样是 number | null）：
                    // 返回空数组会让"读到 0 字节"与"读完了"无法区分。
                    if (read == 0)
                    {
                        return null;
                    }

                    return read == length ? buffer : buffer[..read];
                };
                return true;

            case "fs.write":
                handler = static (window, command, args) =>
                {
                    long id = OrielJson.GetRequiredArg<long>(args, "id", null);
                    JsonElement data = OrielJson.RequireArgElement(args, "data");
                    byte[] bytes = ReadBytes(data);

                    FileStream stream = window.App.FileHandles.Get(id, command).Stream;
                    stream.Write(bytes, 0, bytes.Length);

                    // **不逐次 Flush**：Tauri 的 write_all 也不 flush（缓冲由操作系统管），
                    // 每次 flush 会让"逐块写大文件"慢一个数量级。同一个流里的
                    // write → seek → read 是缓冲一致的；跨句柄的可见性靠 close 时的落盘。
                    return bytes.Length;
                };
                return true;

            case "fs.seek":
                handler = static (window, command, args) =>
                {
                    long id = OrielJson.GetRequiredArg<long>(args, "id", null);
                    long offset = OrielJson.GetRequiredArg<long>(args, "offset", null);

                    // 缺省 = 0（Start）。GetOptionalArg<int> 在"没给"时直接回落 0，
                    // 正好是我们要的默认值。
                    int whence = OrielJson.GetOptionalArg<int>(args, "whence", null);

                    SeekOrigin origin = whence switch
                    {
                        0 => SeekOrigin.Begin,
                        1 => SeekOrigin.Current,
                        2 => SeekOrigin.End,
                        _ => throw new OrielIpcException(
                            $"fs.seek: whence 只认 0（Start）/ 1（Current）/ 2（End），实际 {whence}。")
                        {
                            Code = OrielIpcErrorCodes.InvalidArgument,
                        },
                    };

                    return window.App.FileHandles.Get(id, command).Stream.Seek(offset, origin);
                };
                return true;

            case "fs.fstat":
                handler = static (window, command, args) =>
                {
                    long id = OrielJson.GetRequiredArg<long>(args, "id", null);
                    return StatOfHandle(window.App.FileHandles.Get(id, command));
                };
                return true;

            case "fs.ftruncate":
                handler = static (window, command, args) =>
                {
                    long id = OrielJson.GetRequiredArg<long>(args, "id", null);
                    FileStream stream = window.App.FileHandles.Get(id, command).Stream;

                    // len 缺省 = 截到**当前读写位置**（与 Tauri 的 FileHandle.truncate 一致）。
                    // 这里不能用 GetOptionalArg<long>：它在"没给"时给 0，而 0 是一个
                    // 有意义的截断长度（清空文件），两者必须分开。
                    long? explicitLength = args.ValueKind == JsonValueKind.Object
                        && args.TryGetProperty("len", out JsonElement lenElement)
                        && lenElement.ValueKind == JsonValueKind.Number
                        && lenElement.TryGetInt64(out long parsedLength)
                            ? parsedLength
                            : null;
                    long length = explicitLength ?? stream.Position;

                    if (length < 0)
                    {
                        throw new OrielIpcException($"fs.ftruncate: 截断长度不能为负（{length}）。")
                        {
                            Code = OrielIpcErrorCodes.InvalidArgument,
                        };
                    }

                    stream.SetLength(length);
                    return null;
                };
                return true;

            case "fs.close":
                handler = static (window, command, args) =>
                {
                    long id = OrielJson.GetRequiredArg<long>(args, "id", null);
                    window.App.FileHandles.Close(id, command);
                    return null;
                };
                return true;

            case "fs.readTextFileLines":
                handler = static (window, command, args) =>
                {
                    // **整文件读入再切行**，不是流式（返回的是 string[]，不是 Channel）。
                    // 理由与取舍：本库 IPC 是纯 JSON，真正的流式要占用一条 Channel
                    // 并按行回传——那是 B7 的机制、可以后加，但会让"读个日志文件"
                    // 变成需要理解通道语义的事。代价写进文档：内存 = 文件大小。
                    string target = AuthorizePath(window, command, args, out _);
                    return SplitLines(File.ReadAllText(target, Utf8NoBom));
                };
                return true;

            // ---- 监视（B20）----
            case "fs.watch":
                handler = static (window, command, args) => Watch(window, command, args);
                return true;

            case "fs.unwatch":
                handler = static (window, _, args) =>
                {
                    // 不需要 scope：编号是宿主发出去的，它代表"已经过校验的那批路径"。
                    // 再按路径判一遍反而会把"监视期间目录被改名"变成关不掉。
                    long id = OrielJson.GetRequiredArg<long>(args, "id", null);
                    return window.App.UnwatchPaths(id);
                };
                return true;

            default:
                handler = null!;
                return false;
        }
    }

    /// <summary>单次 <c>fs.read</c> 的最大字节数（防止页面一句 invoke 分配掉进程内存）。</summary>
    private const int MaxReadChunk = 8 * 1024 * 1024;

    /// <summary>
    /// 按 <c>FileStream</c> 打开一个文件；校验选项组合并映射到 <see cref="FileMode"/>/<see cref="FileAccess"/>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 选项语义照 Tauri 的 <c>OpenOptions</c>（实为 Rust <c>std::fs::OpenOptions</c>）：
    /// 三个访问位（read/write/append）至少要有一个；<c>truncate</c> 需要写权限；
    /// <c>createNew</c> 与 <c>create</c> 互斥。冲突组合**当场报错**而不是猜一个意图——
    /// 猜错的后果是数据被截断或文件被意外创建。
    /// </para>
    /// <para>
    /// <c>FileShare.ReadWrite | FileShare.Delete</c>：与 Rust 默认的共享模式一致
    /// （Windows 上 Rust 默认允许别人读写删）。用 <c>FileShare.None</c> 会让
    /// "一边写日志一边被查看"直接失败，那不是调用方想要的。
    /// </para>
    /// </remarks>
    private static FileStream OpenHandleStream(
        string target, bool read, bool write, bool append, bool truncate, bool create, bool createNew)
    {
        // **先判更具体的那条**：`{truncate: true}` 同时命中"没有访问位"与"truncate 需要写权限"，
        // 而调用方明确表达了截断意图——告诉它"缺写权限"比"三者全 false"有用得多。
        bool writable = write || append;
        if (truncate && !writable)
        {
            throw new OrielIpcException(
                "fs.open: truncate 需要写权限（write 或 append 为 true）——" +
                "只读地截断一个文件不是有意义的操作。")
            {
                Code = OrielIpcErrorCodes.InvalidArgument,
            };
        }

        if (!read && !write && !append)
        {
            throw new OrielIpcException(
                "fs.open: read / write / append 至少要有一个为 true——三者全 false 时" +
                "打开文件没有意义（Rust 的 OpenOptions 在这组参数上同样报错）。")
            {
                Code = OrielIpcErrorCodes.InvalidArgument,
            };
        }

        if (createNew && create)
        {
            throw new OrielIpcException(
                "fs.open: createNew 与 create 不能同时为 true——前者要求「必须不存在」、" +
                "后者允许「已存在就打开」，两者同时给出是矛盾的。")
            {
                Code = OrielIpcErrorCodes.InvalidArgument,
            };
        }

        FileAccess access = read && writable ? FileAccess.ReadWrite : writable ? FileAccess.Write : FileAccess.Read;

        // 顺序有意义：createNew 要求文件不存在；append 要求只能追加（写入位置固定在末尾）；
        // truncate 要清空已有内容；create 允许新建或打开已有。都不给时按"必须已存在"打开。
        FileMode mode = createNew ? FileMode.CreateNew
            : append ? FileMode.Append
            : truncate ? FileMode.Create
            : create ? FileMode.OpenOrCreate
            : FileMode.Open;

        // Append 模式只接受 Write 访问（FileStream 的构造约束）。
        if (mode == FileMode.Append)
        {
            access = FileAccess.Write;
        }

        return new FileStream(target, mode, access, FileShare.ReadWrite | FileShare.Delete, bufferSize: 4096);
    }

    /// <summary>
    /// 从**打开的句柄**读元信息（<c>fs.fstat</c>），形状与 <c>fs.stat</c> 一致。
    /// </summary>
    /// <remarks>
    /// 尺寸取 <see cref="FileStream.Length"/>（句柄自己知道）；时间戳走
    /// <c>File.GetLastWriteTimeUtc(SafeFileHandle)</c> 一族——它们作用于
    /// **文件描述符**而不是路径，所以文件被改名/挪走后仍然给得出正确的值
    /// （路径版的 <c>FileInfo</c> 在那个场景下会抛"找不到文件"）。
    /// <c>isSymlink</c> 恒为 <c>false</c>：句柄已经跟随过链接，回答"打开的这个东西
    /// 是不是链接"无从谈起——与 Rust 的 <c>File::metadata()</c> 一致。
    /// </remarks>
    private static JsonElement StatOfHandle(OrielFileHandle handle)
    {
        FileStream stream = handle.Stream;
        return Parse(
            "{\"isFile\":true,\"isDirectory\":false,\"isSymlink\":false,\"size\":" +
            stream.Length.ToString(CultureInfo.InvariantCulture) +
            ",\"mtime\":" + new DateTimeOffset(File.GetLastWriteTimeUtc(stream.SafeFileHandle))
                .ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) +
            ",\"atime\":" + new DateTimeOffset(File.GetLastAccessTimeUtc(stream.SafeFileHandle))
                .ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) +
            ",\"birthtime\":" + new DateTimeOffset(File.GetCreationTimeUtc(stream.SafeFileHandle))
                .ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) +
            "}");
    }

    /// <summary>
    /// 把整段文本切成行（<c>fs.readTextFileLines</c> 的切分规则）。
    /// </summary>
    /// <remarks>
    /// 按 <c>\n</c> 切、去掉紧随的 <c>\r</c>（CRLF 与 LF 都支持，单独出现的 <c>\r</c>
    /// 不切——那是老 Mac 换行，与 Tauri 的 TextLineStream 行为一致）。
    /// 末尾换行不产生一个空行；文件中间的空行照常保留（那是内容）。
    /// </remarks>
    private static string[] SplitLines(string text)
    {
        var lines = new List<string>();
        int start = 0;

        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n')
            {
                continue;
            }

            int end = i > start && text[i - 1] == '\r' ? i - 1 : i;
            lines.Add(text[start..end]);
            start = i + 1;
        }

        // 末尾没有换行时，最后一个字符之后还剩一行；以换行结尾时没有"尾随空行"。
        if (start < text.Length)
        {
            lines.Add(text[start..]);
        }

        return [.. lines];
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
    /// <para>
    /// <b>符号链接在 scope 判定之前解析</b>（0.4.9 起）：allowed 目录里放一个指向外部的链接，
    /// 曾经能让文本层面的判定通过——scope 于是形同虚设。现在由 <see cref="OrielLinkResolver.Resolve"/>
    /// **逐段**解析到最终目标，再用它判 scope。目标不存在（写命令的常见情形）时跳过解析：
    /// <c>ResolveLinkTarget</c> 对不存在的路径没有意义。
    /// </para>
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
        string full = AuthorizeRawPath(window, command, raw, argName);

        // data 与 path 一起取出来：写命令两者都要，分两次取会让"取 path 时顺手验了 scope"
        // 这个顺序保证被拆散
        data = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("data", out JsonElement value)
            ? value
            : default;

        return full;
    }

    /// <summary>
    /// 归一 + 过 scope 的**共用部分**（路径来自参数，或来自 <c>paths</c> 数组的某一项）。
    /// </summary>
    /// <param name="window">来源窗口（拿应用标识符与门禁）。</param>
    /// <param name="command">命令名（用于匹配 scope 与报错）。</param>
    /// <param name="raw">待校验的原始路径（可能相对、可能含 <c>..</c>）。</param>
    /// <param name="argName">报错时用的参数名（数组场景下是 <c>paths[i]</c>）。</param>
    /// <remarks>
    /// 把这段单独抽出来是因为 <c>fs.watch</c> 收的是**数组**：每一项都要走完全相同的
    /// "归一 → 命令授权 → 路径 scope"，抄一份的话总有一天两处的顺序会不一样——
    /// 而顺序错了就是 scope 绕过（见 <see cref="AuthorizePath"/> 的说明）。
    /// </remarks>
    private static string AuthorizeRawPath(
        IOrielWindowControl window,
        string command,
        string raw,
        string argName)
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
        if (!window.App.Guard.TryAuthorize(command, out string? commandReason))
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

    /// <summary>
    /// <c>fs.watch</c>：校验并开始监视（<c>fs.watchImmediate</c> 与它同一条命令，只差 <c>delayMs</c>）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>每个路径都要过 scope</b>，而且是逐项校验、全过才建 watcher——放行一项再报错
    /// 会让调用方拿到一个"部分监视"的编号，之后收不齐事件却看不出原因。
    /// </para>
    /// <para>
    /// <c>delayMs</c> 缺省 = immediate；给了 0 也按 immediate 处理（<c>Timer</c> 的 0 延时
    /// 与"下一条事件立刻发"在语义上没有差别，而 0 更容易被误读成"关闭 debounce"）。
    /// </para>
    /// </remarks>
    private static long Watch(IOrielWindowControl window, string command, JsonElement args)
    {
        JsonElement pathsElement = OrielJson.RequireArgElement(args, "paths");
        if (pathsElement.ValueKind != JsonValueKind.Array)
        {
            throw new OrielIpcException(
                $"fs.watch: paths 必须是字符串数组，实际 {pathsElement.ValueKind}。")
            {
                Code = OrielIpcErrorCodes.InvalidArgument,
            };
        }

        var paths = new List<string>();
        int index = 0;
        foreach (JsonElement item in pathsElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw new OrielIpcException(
                    $"fs.watch: paths[{index}] 不是字符串（实际 {item.ValueKind}）。")
                {
                    Code = OrielIpcErrorCodes.InvalidArgument,
                };
            }

            paths.Add(AuthorizeRawPath(window, command, item.GetString()!, $"paths[{index}]"));
            index++;
        }

        if (paths.Count == 0)
        {
            throw new OrielIpcException("fs.watch: paths 至少要有一个路径。")
            {
                Code = OrielIpcErrorCodes.InvalidArgument,
            };
        }

        bool recursive = args.ValueKind == JsonValueKind.Object
                         && args.TryGetProperty("recursive", out JsonElement recursiveElement)
                         && recursiveElement.ValueKind == JsonValueKind.True;

        int? delayMs = null;
        if (args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty("delayMs", out JsonElement delayElement)
            && delayElement.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            if (delayElement.ValueKind != JsonValueKind.Number
                || !delayElement.TryGetInt32(out int parsed)
                || parsed < 0)
            {
                throw new OrielIpcException("fs.watch: delayMs 必须是非负整数（毫秒）。")
                {
                    Code = OrielIpcErrorCodes.InvalidArgument,
                };
            }

            delayMs = parsed;
        }

        return window.App.WatchPaths(window.Label, paths, recursive, delayMs);
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
