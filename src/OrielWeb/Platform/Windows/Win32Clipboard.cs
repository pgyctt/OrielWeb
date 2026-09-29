using System.Runtime.InteropServices;
using System.Text;
using OrielWeb.Platform.Windows.Interop;

namespace OrielWeb.Platform.Windows;

/// <summary>
/// 剪贴板读写（Win32 全局剪贴板）。文本用 <c>CF_UNICODETEXT</c>；HTML 用 "HTML Format" 自定义格式
/// （即 CF_HTML）——它是一段带偏移头的 UTF-8 文本，偏移按**字节**计，所以构造与解析都必须按字节做。
/// </summary>
/// <remarks>
/// 拆成独立的分部文件（与 <c>Win32WebView2Ipc.cs</c> 同样的理由）：剪贴板与窗口生命周期无关，
/// 混进主文件只会让它更难读。
/// </remarks>
internal partial class Win32WindowHost
{
    /// <summary>进程内缓存的 "HTML Format" 格式 ID（0 表示尚未注册）。</summary>
    private static int s_htmlClipboardFormat;

    /// <summary>"HTML Format" 自定义剪贴板格式 ID；首次使用时注册并缓存。</summary>
    private static uint HtmlClipboardFormat
    {
        get
        {
            if (Volatile.Read(ref s_htmlClipboardFormat) == 0)
            {
                Volatile.Write(ref s_htmlClipboardFormat, (int)Win32.RegisterClipboardFormatW("HTML Format"));
            }

            return (uint)Volatile.Read(ref s_htmlClipboardFormat);
        }
    }

    // ------------------------------------------------------------------
    // 文本
    // ------------------------------------------------------------------

    public string? GetClipboardText()
    {
        if (!Win32.OpenClipboard(_hwnd))
        {
            // 剪贴板被别的进程占用时 OpenClipboard 会失败（无重试）；返回 null 而不是抛异常：
            // 读剪贴板是"尽力而为"的操作，调用方多半只想要一个可空结果。
            return null;
        }

        try
        {
            nint handle = Win32.GetClipboardData(Win32Constants.CF_UNICODETEXT);
            if (handle == 0)
            {
                return null;
            }

            nint ptr = Win32.GlobalLock(handle);
            if (ptr == 0)
            {
                return null;
            }

            try
            {
                return Marshal.PtrToStringUni(ptr);
            }
            finally
            {
                _ = Win32.GlobalUnlock(handle);
            }
        }
        finally
        {
            _ = Win32.CloseClipboard();
        }
    }

    public void SetClipboardText(string text)
    {
        if (!Win32.OpenClipboard(_hwnd))
        {
            return;
        }

        try
        {
            _ = Win32.EmptyClipboard();
            SetOwnedGlobal(Win32Constants.CF_UNICODETEXT, AllocGlobalUtf16(text));
        }
        finally
        {
            _ = Win32.CloseClipboard();
        }
    }

    // ------------------------------------------------------------------
    // HTML（CF_HTML）
    // ------------------------------------------------------------------

    public string? GetClipboardHtml()
    {
        uint format = HtmlClipboardFormat;
        if (format == 0)
        {
            return null;
        }

        string? raw = ReadClipboardUtf8(format);
        return raw is null ? null : ExtractHtmlFragment(raw);
    }

    public void SetClipboardHtml(string html, string? plainTextFallback)
    {
        uint format = HtmlClipboardFormat;
        if (format == 0)
        {
            // 注册失败（极罕见）：至少把纯文本放进去，别让调用方以为"写了 HTML"而其实什么都没写
            SetClipboardText(plainTextFallback ?? html);
            return;
        }

        if (!Win32.OpenClipboard(_hwnd))
        {
            return;
        }

        try
        {
            _ = Win32.EmptyClipboard();

            // 同时放一份纯文本：不认 HTML 的应用（记事本等）也能粘到内容。
            SetOwnedGlobal(
                Win32Constants.CF_UNICODETEXT,
                AllocGlobalUtf16(plainTextFallback ?? html));

            SetOwnedGlobal(format, AllocGlobalUtf8(BuildCfHtml(html)));
        }
        finally
        {
            _ = Win32.CloseClipboard();
        }
    }

    /// <summary>
    /// 把全局内存块交给剪贴板；系统拒绝接管（返回 0）时自己释放，避免泄漏。
    /// 内存为 0（分配失败）时什么都不做。
    /// </summary>
    private static void SetOwnedGlobal(uint format, nint memory)
    {
        if (memory == 0)
        {
            return;
        }

        if (Win32.SetClipboardData(format, memory) == 0)
        {
            _ = Win32.GlobalFree(memory);
        }
    }

    /// <summary>读 8 位剪贴板格式并按 UTF-8 解码（CF_HTML 是 UTF-8；Win32 的 PtrToStringAnsi 用 ANSI 代码页，中文会乱码）。</summary>
    private string? ReadClipboardUtf8(uint format)
    {
        if (!Win32.OpenClipboard(_hwnd))
        {
            return null;
        }

        try
        {
            nint handle = Win32.GetClipboardData(format);
            if (handle == 0)
            {
                return null;
            }

            nint ptr = Win32.GlobalLock(handle);
            if (ptr == 0)
            {
                return null;
            }

            try
            {
                return Marshal.PtrToStringUTF8(ptr);
            }
            finally
            {
                _ = Win32.GlobalUnlock(handle);
            }
        }
        finally
        {
            _ = Win32.CloseClipboard();
        }
    }

    /// <summary>
    /// 构造 CF_HTML：定长头部 + HTML 文档；头部里的四个偏移是**从整段开头的字节偏移**。
    /// 用两遍法——先按占位值算出头部长度（偏移字段定长 10 位，所以头长与内容无关），再回填真实偏移。
    /// </summary>
    internal static string BuildCfHtml(string fragment)
    {
        const string headerTemplate =
            "Version:0.9\r\n" +
            "StartHTML:{0:D10}\r\n" +
            "EndHTML:{1:D10}\r\n" +
            "StartFragment:{2:D10}\r\n" +
            "EndFragment:{3:D10}\r\n";
        const string documentPrefix = "<html><body><!--StartFragment-->";
        const string documentSuffix = "<!--EndFragment--></body></html>";

        int headerLength = Encoding.UTF8.GetByteCount(string.Format(headerTemplate, 0, 0, 0, 0));
        int startFragment = headerLength + Encoding.UTF8.GetByteCount(documentPrefix);
        int endFragment = startFragment + Encoding.UTF8.GetByteCount(fragment);
        int endHtml = endFragment + Encoding.UTF8.GetByteCount(documentSuffix);

        string header = string.Format(
            headerTemplate, headerLength, endHtml, startFragment, endFragment);
        return header + documentPrefix + fragment + documentSuffix;
    }

    /// <summary>
    /// 从 CF_HTML 里取出 <c>StartFragment</c>/<c>EndFragment</c> 之间的片段。
    /// 头缺失或偏移越界时退化为整段原文——比直接返回 null 更有用（调用方至少拿到内容）。
    /// </summary>
    internal static string ExtractHtmlFragment(string raw)
    {
        int start = ReadHeaderOffset(raw, "StartFragment:");
        int end = ReadHeaderOffset(raw, "EndFragment:");
        if (start < 0 || end <= start)
        {
            return raw;
        }

        byte[] bytes = Encoding.UTF8.GetBytes(raw);
        if (end > bytes.Length)
        {
            return raw;
        }

        return Encoding.UTF8.GetString(bytes, start, end - start);
    }

    /// <summary>读 CF_HTML 头里的一个十进制偏移值；缺失或非法返回 -1。</summary>
    private static int ReadHeaderOffset(string raw, string name)
    {
        int index = raw.IndexOf(name, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return -1;
        }

        index += name.Length;
        int end = index;
        while (end < raw.Length && char.IsAsciiDigit(raw[end]))
        {
            end++;
        }

        return end > index
            && int.TryParse(raw.AsSpan(index, end - index), out int value)
                ? value
                : -1;
    }

    // ------------------------------------------------------------------
    // 全局内存分配
    // ------------------------------------------------------------------

    /// <summary>分配可移动全局内存并写入 UTF-16 文本（含结尾 NUL）。失败返回 0。</summary>
    private static nint AllocGlobalUtf16(string text)
    {
        int bytes = (text.Length + 1) * 2;
        return AllocGlobal(bytes, ptr =>
        {
            Marshal.Copy(text.ToCharArray(), 0, ptr, text.Length);
            Marshal.WriteInt16(ptr, text.Length * 2, 0);
        });
    }

    /// <summary>分配可移动全局内存并写入 UTF-8 文本（含结尾 NUL）。失败返回 0。</summary>
    private static nint AllocGlobalUtf8(string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        return AllocGlobal(bytes.Length + 1, ptr =>
        {
            Marshal.Copy(bytes, 0, ptr, bytes.Length);
            Marshal.WriteByte(ptr, bytes.Length, 0);
        });
    }

    private static nint AllocGlobal(int bytes, Action<nint> fill)
    {
        nint memory = Win32.GlobalAlloc(Win32Constants.GMEM_MOVEABLE, (nuint)bytes);
        if (memory == 0)
        {
            return 0;
        }

        nint ptr = Win32.GlobalLock(memory);
        if (ptr == 0)
        {
            _ = Win32.GlobalFree(memory);
            return 0;
        }

        try
        {
            fill(ptr);
        }
        finally
        {
            _ = Win32.GlobalUnlock(memory);
        }

        return memory;
    }
}
