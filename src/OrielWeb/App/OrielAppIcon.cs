namespace OrielWeb;

/// <summary>
/// 应用默认图标：可执行文件旁边那份 <c>app.png</c>（Windows 上还可以是 <c>app.ico</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 对标 Tauri 的 <c>App::default_window_icon()</c>。Tauri 的图标来自
/// <c>tauri.conf.json &gt; bundle.icon</c> 与打包器生成的资源表；本库没有资源表
/// （README 里"没有句柄/资源模型"那条），所以约定成**可执行文件旁边的固定文件名**——
/// 打包产物把图标放到 exe 旁边是 Velopack 与本库都做得到的事，而"资源表"需要一整套
/// 编译器与运行时支持。
/// </para>
/// <para>
/// <b>只认 <c>app.png</c> 与 <c>app.ico</c></b>，且必须是真的图片（按魔数判断）。
/// 一个叫 <c>app.png</c> 的文本文件不做 magic 检查就会原样交给页面，
/// 而页面侧的 <c>Image.fromBytes</c> 会在解析尺寸时抛一个离原因很远的错。
/// </para>
/// <para>
/// <b>结果缓存到进程结束</b>：图标在运行时不会变（Tauri 的也是编译期资源），
/// 而页面可能把它当"应用信息"反复取。缓存 <c>null</c> 也一样——启动后才放进来的
/// 文件需要重启才能看到，这与打包好的应用行为一致。
/// </para>
/// </remarks>
internal static class OrielAppIcon
{
    /// <summary>按优先级尝试的文件名（PNG 优先：三平台都认，ICO 只有 Windows 用得上）。</summary>
    internal static readonly string[] FileNames = ["app.png", "app.ico"];

    private static readonly object Gate = new();
    private static bool _resolved;
    private static byte[]? _cached;

    /// <summary>取默认图标字节；没有时返回 <c>null</c>。结果缓存。</summary>
    internal static byte[]? Find()
    {
        lock (Gate)
        {
            if (_resolved)
            {
                return _cached;
            }

            _resolved = true;
            _cached = FindIn(AppContext.BaseDirectory);
            return _cached;
        }
    }

    /// <summary>在指定目录里找（可单测；不做缓存）。</summary>
    internal static byte[]? FindIn(string directory)
    {
        foreach (string name in FileNames)
        {
            string path = Path.Combine(directory, name);
            byte[] bytes;
            try
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                bytes = File.ReadAllBytes(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                OrielDiagnostics.Report($"[OrielWeb] 读取默认图标失败（{path}）：{ex.Message}");
                continue;
            }

            if (!LooksLikeImage(bytes))
            {
                OrielDiagnostics.Report(
                    $"[OrielWeb] {name} 不是 PNG/ICO 图片（魔数不匹配），已忽略：{path}");
                continue;
            }

            return bytes;
        }

        return null;
    }

    /// <summary>按魔数判断是不是 PNG 或 ICO 字节。</summary>
    /// <remarks>
    /// ICO 的头是 <c>00 00 01 00</c> 加一个非零的图像数（第 4–5 字节）。
    /// 只查前四个字节会把 <c>00 00 01 00</c> 开头的空文件当成图标，
    /// 所以连计数一起查——一个 0 图的 ICO 本来也解析不出尺寸。
    /// </remarks>
    internal static bool LooksLikeImage(ReadOnlySpan<byte> bytes)
    {
        ReadOnlySpan<byte> png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (bytes.Length >= png.Length && bytes[..png.Length].SequenceEqual(png))
        {
            return true;
        }

        return bytes.Length >= 6
            && bytes[0] == 0x00
            && bytes[1] == 0x00
            && bytes[2] == 0x01
            && bytes[3] == 0x00
            && (bytes[4] != 0 || bytes[5] != 0);
    }
}
