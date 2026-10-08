namespace OrielWeb;

/// <summary>
/// 内嵌资源的内容类型表（扩展名 → Content-Type）。
/// </summary>
/// <remarks>
/// <para>
/// 以前这件事由引擎做：资源以 <c>file://</c> 或虚拟主机形式交给它，扩展名到 MIME 的推断是引擎内置的，
/// 所以库可以宣称"不做映射表"。改用自定义 scheme 之后没有这层推断——应答的每一个字节都由我们给出，
/// 类型写错的表现是**样式不生效、模块被拒**（<c>text/html</c> 之外的脚本类型不对会被浏览器按
/// 严格 MIME 检查拦掉），而且不会报"缺 MIME"这种直接的错。所以这张表从"可选"变成"必需"。
/// </para>
/// <para>
/// 认不出来的一律 <c>application/octet-stream</c>（下载语义），不猜 <c>text/html</c>：
/// 猜错的代价是浏览器把二进制当页面渲染，比"当成未知文件"糟糕得多。
/// </para>
/// </remarks>
internal static class MimeTypes
{
    private const string Fallback = "application/octet-stream";

    // 文本类一律显式带 charset：自定义 scheme 的应答没有 HTTP 头可继承，
    // 少了 charset 时中文会被按 latin-1 解出乱码——而页面的 <meta charset> 只能救 HTML，救不了 .js/.css。
    private const string TextUtf8 = "; charset=utf-8";

    private static readonly Dictionary<string, string> Table = new(StringComparer.OrdinalIgnoreCase)
    {
        // 页面与脚本
        [".html"] = "text/html" + TextUtf8,
        [".htm"] = "text/html" + TextUtf8,
        [".js"] = "text/javascript" + TextUtf8,
        [".mjs"] = "text/javascript" + TextUtf8,
        [".css"] = "text/css" + TextUtf8,
        [".json"] = "application/json" + TextUtf8,
        [".map"] = "application/json" + TextUtf8,
        [".wasm"] = "application/wasm",
        [".txt"] = "text/plain" + TextUtf8,
        [".csv"] = "text/csv" + TextUtf8,
        [".xml"] = "application/xml" + TextUtf8,
        [".svg"] = "image/svg+xml" + TextUtf8,

        // 图片
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".avif"] = "image/avif",
        [".ico"] = "image/x-icon",
        [".bmp"] = "image/bmp",

        // 字体
        [".woff"] = "font/woff",
        [".woff2"] = "font/woff2",
        [".ttf"] = "font/ttf",
        [".otf"] = "font/otf",
        [".eot"] = "application/vnd.ms-fontobject",

        // 其它常见静态资源
        [".pdf"] = "application/pdf",
        [".mp3"] = "audio/mpeg",
        [".wav"] = "audio/wav",
        [".ogg"] = "audio/ogg",
        [".mp4"] = "video/mp4",
        [".webm"] = "video/webm",
        [".webmanifest"] = "application/manifest+json" + TextUtf8,

        // 苹果/流媒体常见格式（评审 P3：此前落 octet-stream，<audio>/<video> 会因类型不符拒绝播放）。
        // .avifs（AVIF 序列）没有注册的 MIME，按 image/avif 给——它至少是可渲染的图片类型，
        // 而不是"未识别 → 交给下载"。
        [".m4a"] = "audio/mp4",
        [".m4v"] = "video/x-m4v",
        [".aac"] = "audio/aac",
        [".flac"] = "audio/flac",
        [".heic"] = "image/heic",
        [".avifs"] = "image/avif",
    };

    /// <summary>按路径的扩展名给出 Content-Type。</summary>
    internal static string ForPath(string path)
    {
        string extension = Path.GetExtension(path);
        return extension.Length > 0 && Table.TryGetValue(extension, out string? contentType)
            ? contentType
            : Fallback;
    }
}
