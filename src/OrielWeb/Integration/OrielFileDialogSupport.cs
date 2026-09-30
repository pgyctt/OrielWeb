namespace OrielWeb;

/// <summary>
/// 文件对话框里与平台无关的数据处理。放在这里而不是散进各平台后端，是为了让它们**可被单测**——
/// 对话框本身弹出后无法在无头环境断言，但"原生返回的字节怎样变成路径数组"完全可以。
/// </summary>
/// <remarks>
/// 刻意**不调用 <c>System.IO.Path</c>**：这些函数要能在任意平台上被测试（尤其是 Windows 的多选解析
/// 必须在 Linux 的 CI 上也能跑），而 Path 的行为随平台变——<c>Path.Combine("C:\\dir", "a.txt")</c>
/// 在 Linux 上会拼成 <c>C:\dir/a.txt</c>，测试就废了。字符串处理在这里反而是对的。
/// </remarks>
internal static class OrielFileDialogSupport
{
    /// <summary>
    /// 解析 Win32 <c>OPENFILENAMEW</c> 在 <c>OFN_ALLOWMULTISELECT</c> 下写回的缓冲区。
    /// </summary>
    /// <remarks>
    /// 原生对话框的返回形状有**两种**，靠段数区分，这是最容易写错的一处：
    /// <list type="bullet">
    /// <item>选中**一个**文件：整块就是完整路径 <c>C:\dir\file.txt</c>（单段，没有分隔符）。</item>
    /// <item>选中**多个**文件：第一段是目录、之后每段是一个文件名
    /// （<c>C:\dir\0a.txt\0b.txt\0\0</c>），需要自己拼回完整路径。</item>
    /// </list>
    /// 若把单段结果也当"目录 + 文件名"处理，就会得到一个空的文件名；
    /// 若把多段结果当完整路径，得到的是那个目录本身。
    /// </remarks>
    internal static string[] ParseWin32MultiSelect(string? buffer)
    {
        if (string.IsNullOrEmpty(buffer))
        {
            return [];
        }

        string[] parts = buffer.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return [];
        }

        if (parts.Length == 1)
        {
            return [parts[0]];
        }

        // 目录段与文件名段的拼接：沿用输入里的分隔符风格，不经过 Path.Combine（见类型注释）
        string directory = parts[0].TrimEnd('/', '\\');
        char separator = parts[0].Contains('\\') ? '\\' : '/';

        var paths = new string[parts.Length - 1];
        for (int i = 1; i < parts.Length; i++)
        {
            paths[i - 1] = $"{directory}{separator}{parts[i]}";
        }

        return paths;
    }

    /// <summary>
    /// 用户没写扩展名时补上（保存对话框用）。
    /// </summary>
    /// <remarks>
    /// 需要它是因为三平台**不会**替我们做同一件事：Windows 的原生对话框有 <c>lpstrDefExt</c> 会自动补，
    /// GTK 与 Cocoa 不会——不补的话用户在 Linux/macOS 上存「报告」得到的是一个无扩展名文件，
    /// 双击它系统不知道该用什么程序打开。这个差异由调用方决定要不要走这里，而不是各后端各写一份。
    /// 扩展名前后带点（<c>".txt"</c>）也接受。
    /// </remarks>
    internal static string EnsureExtension(string path, string? extension)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrWhiteSpace(extension))
        {
            return path;
        }

        // 已经带了扩展名就不动它（用户手写 .md 时要尊重，而不是变成 .md.txt）
        int lastSeparator = path.LastIndexOfAny(['/', '\\']);
        string fileName = lastSeparator >= 0 ? path[(lastSeparator + 1)..] : path;
        if (fileName.Contains('.'))
        {
            return path;
        }

        return $"{path}.{extension.TrimStart('.')}";
    }
}
