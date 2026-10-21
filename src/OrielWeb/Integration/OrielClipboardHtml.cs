using System.Globalization;
using System.Text;

namespace OrielWeb;

/// <summary>
/// 构造 Windows 的 <c>CF_HTML</c> 剪贴板数据（带字节偏移头的 HTML 格式）。
/// </summary>
/// <remarks>
/// <para>
/// CF_HTML 不是"把 HTML 字符串放进剪贴板"那么简单：它是一段**自描述**的数据——
/// 头部用 <c>StartHTML</c> / <c>EndHTML</c> / <c>StartFragment</c> / <c>EndFragment</c>
/// 四个偏移量指明正文在哪里，而**偏移是相对整段数据开头的 UTF-8 字节数**。
/// 头部自身占多少字节又取决于这些偏移的位数，这就是那个经典的"先有鸡还是先有蛋"。
/// 标准解法是把偏移写成**固定 10 位、前导补零**，于是头部长度与数值无关，可以先量后填。
/// </para>
/// <para>
/// <b>偏移写错的后果是静默的</b>：粘贴目标（Word、Outlook、编辑器）会拿到一段错位的 HTML——
/// 表现是"粘出来是乱码/被截断/只有一半"，而写入方这边一切正常。所以这段逻辑
/// **不能靠肉眼检查**：测试会把产物按头部的偏移切回去，逐段与原文比对。
/// </para>
/// <para>
/// 纯函数、不碰剪贴板：Windows 后端拿到字节后连同 <c>CF_UNICODETEXT</c> 一起写进剪贴板
/// （纯文本是给不支持 HTML 的粘贴目标用的回退）。
/// </para>
/// </remarks>
internal static class OrielClipboardHtml
{
    /// <summary>片段起止标记（MS 规范要求片段的边界带注释标记）。</summary>
    internal const string StartFragmentMark = "<!--StartFragment-->";

    /// <summary>片段结束标记。</summary>
    internal const string EndFragmentMark = "<!--EndFragment-->";

    /// <summary>头部四行的固定宽度（10 位十进制，前导补零）。</summary>
    private const string OffsetFormat = "D10";

    /// <summary>
    /// 把 HTML 片段包装成 CF_HTML 的 UTF-8 字节。
    /// </summary>
    /// <param name="html">片段 HTML（不需要 <c>&lt;html&gt;</c> 外壳，会自动补）。</param>
    /// <exception cref="ArgumentNullException"><paramref name="html"/> 为 null。</exception>
    internal static byte[] Build(string html)
    {
        ArgumentNullException.ThrowIfNull(html);

        // 外壳与片段标记都是 ASCII；只有 html 本身可能是多字节，所以偏移必须按 UTF-8 字节算，
        // 不能按 char 数量算（中文/emoji 每个字符 3~4 字节，按 char 算会整体错位）。
        const string Prefix = "<html><body>\r\n";
        string suffix = EndFragmentMark + "\r\n</body></html>";

        // 先用全 0 量出头部长度——固定宽度保证它与真实数值等长
        int headerBytes = Encoding.ASCII.GetByteCount(Header(0, 0, 0, 0));

        int startHtml = headerBytes;
        int startFragment = startHtml + Encoding.ASCII.GetByteCount(Prefix + StartFragmentMark);
        int endFragment = startFragment + Encoding.UTF8.GetByteCount(html);
        int endHtml = endFragment + Encoding.ASCII.GetByteCount(suffix);

        string header = Header(startHtml, endHtml, startFragment, endFragment);

        // 头部长度必须与测量值一致：不一致说明格式串里混进了随数值变长的东西，
        // 那样后面所有偏移都会错——宁可在写入前炸掉，也不要产出一段错位的剪贴板数据。
        if (Encoding.ASCII.GetByteCount(header) != headerBytes)
        {
            throw new InvalidOperationException("CF_HTML 头部长度与测量值不一致（偏移字段必须固定宽度）。");
        }

        return Encoding.UTF8.GetBytes(string.Concat(header, Prefix, StartFragmentMark, html, suffix));
    }

    /// <summary>生成四行头部（Version 行 + 四个偏移，CRLF 结尾）。</summary>
    private static string Header(int startHtml, int endHtml, int startFragment, int endFragment)
        => "Version:0.9\r\n"
           + "StartHTML:" + startHtml.ToString(OffsetFormat, CultureInfo.InvariantCulture) + "\r\n"
           + "EndHTML:" + endHtml.ToString(OffsetFormat, CultureInfo.InvariantCulture) + "\r\n"
           + "StartFragment:" + startFragment.ToString(OffsetFormat, CultureInfo.InvariantCulture) + "\r\n"
           + "EndFragment:" + endFragment.ToString(OffsetFormat, CultureInfo.InvariantCulture) + "\r\n";
}
