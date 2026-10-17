namespace OrielWeb;

/// <summary>
/// 一张已解码的位图：行优先（从上到下）的 32bpp RGBA 像素 + 宽高。
/// </summary>
/// <remarks>
/// <para>
/// 这是三平台图标加载的**统一输入**（对标 Tauri 2 的 <c>tauri::image::Image</c>）：
/// Windows 走 <c>CreateIconIndirect</c>、macOS 走 <c>CGImageCreate</c> + <c>NSImage</c>、
/// Linux 走 <c>gdk_pixbuf_new_from_data</c> + <c>gtk_status_icon_set_from_pixbuf</c>，
/// 三者都能直接吃 RGBA——于是"某个平台的加载器认不认这个格式"这件事从运行期的静默失败，
/// 变成了**解码期的一次明确失败**（见 <see cref="OrielImageCodec"/>）。
/// </para>
/// <para>
/// 与 Tauri 一样，本类型**不持有任何原生资源**：它是纯托管数据，可跨平台、可复用、
/// 可在没有窗口/UI 的情况下构造（测试因此不需要真机）。
/// </para>
/// <para>
/// <see cref="Rgba"/> 是**只读视图**，不要改写它——同一个实例可能被多个托盘/窗口共享。
/// </para>
/// </remarks>
public sealed class OrielImage
{
    /// <summary>按 RGBA 数据构造（<paramref name="rgba"/> 长度必须是 <c>width * height * 4</c>）。</summary>
    /// <exception cref="ArgumentException">长度与宽高不匹配，或宽高不是正数。</exception>
    public OrielImage(ReadOnlyMemory<byte> rgba, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentException($"图像宽高必须是正数（收到 {width}x{height}）。");
        }

        long expected = (long)width * height * 4;
        if (rgba.Length != expected)
        {
            throw new ArgumentException(
                $"RGBA 数据长度与宽高不匹配：期望 {expected} 字节（{width}x{height}x4），实到 {rgba.Length} 字节。");
        }

        Rgba = rgba;
        Width = width;
        Height = height;
    }

    /// <summary>行优先（从上到下）、每像素 4 字节（R,G,B,A）的像素数据。</summary>
    public ReadOnlyMemory<byte> Rgba { get; }

    /// <summary>宽度（像素）。</summary>
    public int Width { get; }

    /// <summary>高度（像素）。</summary>
    public int Height { get; }

    /// <summary>从 PNG 或 ICO 字节解码（格式由文件头自动识别）。</summary>
    /// <exception cref="OrielImageFormatException">不是可识别的 PNG/ICO，或数据损坏。</exception>
    public static OrielImage FromBytes(ReadOnlySpan<byte> bytes) => OrielImageCodec.Decode(bytes);

    /// <summary>
    /// 从 PNG 或 ICO 文件解码。相对路径按可执行文件所在目录解析
    /// （见 <see cref="OrielPathResolver"/>）。
    /// </summary>
    /// <exception cref="FileNotFoundException">文件不存在。</exception>
    /// <exception cref="OrielImageFormatException">不是可识别的 PNG/ICO，或数据损坏。</exception>
    public static OrielImage FromFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string resolved = OrielPathResolver.Resolve(path);
        if (!File.Exists(resolved))
        {
            throw new FileNotFoundException($"图像文件不存在：{resolved}", resolved);
        }

        return OrielImageCodec.Decode(File.ReadAllBytes(resolved));
    }

    /// <summary>尝试从文件解码；失败时返回 false 并给出原因（不抛异常）。</summary>
    public static bool TryFromFile(string path, out OrielImage? image, out string? error)
    {
        try
        {
            image = FromFile(path);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or OrielImageFormatException
                                       or ArgumentException or NotSupportedException
                                       or UnauthorizedAccessException or System.Security.SecurityException)
        {
            image = null;
            error = ex.Message;
            return false;
        }
    }

    /// <inheritdoc />
    public override string ToString() => $"OrielImage({Width}x{Height}, {Rgba.Length} 字节 RGBA)";
}

/// <summary>
/// 图像数据无法解码（不是 PNG/ICO、或结构损坏）。
/// </summary>
/// <remarks>
/// 刻意与 <see cref="ArgumentException"/> 分开：调用方常需要区分"参数写错了"（编程错误，该抛）
/// 与"这个文件不是有效图像"（运行期数据问题，该降级/上报）。
/// </remarks>
public sealed class OrielImageFormatException : Exception
{
    public OrielImageFormatException(string message) : base(message)
    {
    }

    public OrielImageFormatException(string message, Exception inner) : base(message, inner)
    {
    }
}
