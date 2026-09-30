namespace OrielWeb;

/// <summary>
/// 拖放载荷里与平台无关的字符串处理：把 <c>file://</c> URI 变成本地路径。
/// </summary>
/// <remarks>
/// 放在这里而不是 Linux 后端内部，是为了能**在任意平台上被单测**——拖放本身要真人操作才验证得了，
/// 但"URI 怎么变成路径"是纯字符串处理，而且它有一堆容易写错的边界
/// （百分号编码、Windows 的盘符、非 file 协议、主机名段），值得逐条钉住。
/// <para>
/// 刻意不用 <c>System.Uri.LocalPath</c>：它是**平台相关**的——同一个
/// <c>file:///C:/x.txt</c> 在 Windows 上给 <c>C:\x.txt</c>、在 Linux 上给 <c>/C:/x.txt</c>，
/// 于是为 Linux 写的解析反而在 Linux 的 CI 上测不出 Windows 的形状。手写解析虽然啰嗦，但结果稳定。
/// </para>
/// </remarks>
internal static class OrielFileDropSupport
{
    /// <summary>
    /// 把一条拖放载荷里的 URI 转成本地路径；不是本地文件（http、data、非 file 协议）时返回 null。
    /// </summary>
    internal static string? UriToPath(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
        {
            return null;
        }

        string text = uri.Trim();
        if (!text.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string rest = text[5..];

        // "file://host/path"：主机名段要么是空的、要么是 localhost，其它主机意味着网络路径——
        // 那不是本地文件，如实返回 null 而不是拼出一个看起来像路径的字符串。
        if (rest.StartsWith("//", StringComparison.Ordinal))
        {
            rest = rest[2..];
            int slash = rest.IndexOf('/');
            if (slash < 0)
            {
                return null; // 只有主机名，没有路径
            }

            string host = rest[..slash];
            if (host.Length > 0 && !host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            rest = rest[slash..];
        }

        // 百分号解码（文件名里的空格、非 ASCII 都会编码）。UnescapeDataString 是纯字符串操作，
        // 不碰文件系统也不看平台，跨平台结果一致。
        string path = Uri.UnescapeDataString(rest);

        // Windows：file:///C:/x.txt → /C:/x.txt，要把路径前导的那个 '/' 去掉才是盘符路径
        if (path.Length >= 3 && path[0] == '/' && char.IsAsciiLetter(path[1]) && path[2] == ':')
        {
            path = path[1..];
        }

        return path.Length == 0 ? null : path;
    }

    /// <summary>批量转换，丢掉不是本地文件的项（顺序保持）。</summary>
    internal static IReadOnlyList<string> UrisToPaths(IEnumerable<string?> uris)
    {
        var paths = new List<string>();
        foreach (string? uri in uris)
        {
            if (UriToPath(uri) is { } path)
            {
                paths.Add(path);
            }
        }

        return paths;
    }
}
