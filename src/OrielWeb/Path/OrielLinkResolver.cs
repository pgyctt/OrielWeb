using OrielWeb.Ipc;

namespace OrielWeb;

/// <summary>
/// 把符号链接解析到最终目标：fs 命令用它校验候选路径，scope 用它归一整条模式。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么两处都必须是"已解析"的形态</b>：scope 判定本质上是"两个路径是否落在同一棵子树里"的比较。
/// 若一边是真实路径（<c>/private/var/folders/...</c>）而另一边还是字面写法（<c>/var/folders/...</c>），
/// 比较必然失败——macOS 上 <c>/var</c> 正是 <c>/private/var</c> 的符号链接。
/// 只解析候选路径、不解析模式，结果是 <b>macOS 上所有 <c>fs.*</c> 命令都被拒</b>
/// （0.4.9 引入的回归，由同一版本新增的 macOS CI 抓到；本机 Windows/Linux 上不可能复现）。
/// </para>
/// <para>
/// <b>逐段解析，不是只看最后一段</b>：allowed 目录里放一个指向 <c>/etc</c> 的目录链接，
/// 再读 <c>allowed/link/passwd</c>——最后一段（<c>passwd</c>）自己不是链接，只看它等于没查。
/// </para>
/// <para>
/// <b>只解析已存在的段</b>：写命令的目标通常还不存在，<c>ResolveLinkTarget</c> 对不存在的路径
/// 没有意义；不存在的段原样续接，与旧行为一致。
/// </para>
/// </remarks>
internal static class OrielLinkResolver
{
    /// <summary>通配符字符：遇到它们之后的部分不是路径，不能拿去访问文件系统。</summary>
    private static readonly char[] WildcardChars = ['*', '?', '[', '{'];

    /// <summary>
    /// 把一个绝对路径逐段解析到最终目标。
    /// </summary>
    /// <param name="full">已归一的绝对路径（可以是尚不存在的写目标）。</param>
    /// <param name="throwOnFailure">
    /// 解析失败（成环、跨卷、权限不足）时是否抛出。
    /// <c>true</c> 用于候选路径：无法确定真实目标时不能放行。
    /// <c>false</c> 用于模式：模式只是字面量，解析不动的部分保持原样即可（判定会退化为字面匹配）。
    /// </param>
    /// <returns>解析后的绝对路径；没有链接时与入参相同。</returns>
    internal static string Resolve(string full, bool throwOnFailure = true)
    {
        string root = Path.GetPathRoot(full) ?? string.Empty;
        if (root.Length == 0)
        {
            return full;
        }

        string current = root;
        string[] segments = full[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        // 逐段解析：每段一次 Exists + LinkTarget，深路径多几次 syscall，
        // 相对于随后真实的文件 I/O 可以忽略。
        foreach (string segment in segments)
        {
            current = Path.Combine(current, segment);

            // junction 在 .NET 里也算目录（Directory.Exists 为 true）；不存在的段按文件处理，
            // 它的 LinkTarget 是 null，于是续接下去——写命令的目标通常还不存在。
            FileSystemInfo info = Directory.Exists(current)
                ? new DirectoryInfo(current)
                : new FileInfo(current);

            if (info.LinkTarget is null)
            {
                continue;
            }

            try
            {
                FileSystemInfo? target = info.ResolveLinkTarget(returnFinalTarget: true);
                if (target is not null)
                {
                    current = target.FullName;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (!throwOnFailure)
                {
                    return full;
                }

                throw new OrielIpcException(
                    $"路径上的符号链接无法解析（链接成环、跨卷或权限不足）：{ex.Message}", ex)
                {
                    Code = OrielIpcErrorCodes.PathNotAllowed,
                };
            }
        }

        return current;
    }

    /// <summary>
    /// 解析一条已归一模式里**通配符之前**的静态前缀，把模式搬到与候选路径同一个"真实路径"空间。
    /// </summary>
    /// <param name="normalizedPattern">已展开变量、已归一（分隔符为 <c>/</c>）的模式。</param>
    /// <returns>解析后的模式；没有可解析的前缀（或前缀本来就解析不动）时原样返回。</returns>
    /// <remarks>
    /// <para>
    /// 例：<c>/var/folders/x/oriel-demo-selftest/**</c> 的前缀是
    /// <c>/var/folders/x/oriel-demo-selftest</c>，解析后变成
    /// <c>/private/var/folders/x/oriel-demo-selftest/**</c>——与候选路径的形态一致，才能比。
    /// </para>
    /// <para>
    /// <b>为什么只解析前缀</b>：通配符之后的段不是具体路径，不能拿去做文件系统访问；
    /// 而 scope 模式的所有通配都出现在尾部（<c>$APPDATA/**</c>、<c>$TMP/logs/*.txt</c>），
    /// 前缀已经覆盖了会受符号链接影响的部分。
    /// </para>
    /// </remarks>
    internal static string ResolvePattern(string normalizedPattern)
    {
        int wildcard = normalizedPattern.IndexOfAny(WildcardChars);
        if (wildcard < 0)
        {
            // 没有通配：整条模式就是一个具体路径（deny 名单里常见这种写法）。
            return Resolve(normalizedPattern, throwOnFailure: false);
        }

        int slash = normalizedPattern.LastIndexOf('/', wildcard);
        if (slash <= 0)
        {
            // 通配出现在第一段之前（相对模式，或 /*/...）：没有可解析的静态前缀。
            return normalizedPattern;
        }

        string prefix = normalizedPattern[..slash];
        string suffix = normalizedPattern[slash..];
        string resolved = Normalize(Resolve(prefix, throwOnFailure: false));

        return string.Equals(resolved, prefix, StringComparison.Ordinal)
            ? normalizedPattern
            : resolved + suffix;
    }

    /// <summary>
    /// 把 <see cref="Resolve"/> 的输出归一成与模式一致的形式（分隔符统一为 <c>/</c>、无重复、无尾随）。
    /// </summary>
    /// <remarks>
    /// <see cref="Path.Combine(string, string)"/> 在 Windows 上产出 <c>\</c>，而模式一律用 <c>/</c> 写；
    /// 不归一的话解析出来的模式与候选路径会因分隔符不同而互不匹配。
    /// </remarks>
    private static string Normalize(string path)
    {
        string result = path.Replace('\\', '/');

        while (result.Contains("//", StringComparison.Ordinal))
        {
            result = result.Replace("//", "/", StringComparison.Ordinal);
        }

        return result.Length > 1 ? result.TrimEnd('/') : result;
    }
}
