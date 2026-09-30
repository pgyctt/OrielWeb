namespace OrielWeb;

/// <summary>
/// 文件对话框的过滤器：一个显示名 + 一组通配模式（如 <c>"文本文件"</c> + <c>["*.txt", "*.md"]</c>）。
/// </summary>
/// <remarks>
/// 保留字符串形式（<c>"文本文件|*.txt;*.md"</c>）的同时引入结构化类型，理由是**平台差异的收敛点**：
/// 同一条过滤器在三平台上要变成三种形状——Win32 的 <c>\0</c> 分隔串、GTK 的逐条 pattern、
/// Cocoa 的扩展名数组。把这些转换集中在这里（<see cref="RenderForWin32"/> / <see cref="GtkPatterns"/> /
/// <see cref="CocoaExtensions"/>），平台后端就只剩"把数据搬进原生调用"，而转换规则本身
/// 是纯字符串处理、可以逐条单测——这是本批能拿到的最实在的机器可判定部分。
/// 字符串形式仍被 <see cref="Parse"/> 接受，存量调用方不用改。
/// </remarks>
public sealed class OrielFileFilter
{
    /// <summary>显示名（如 <c>"文本文件"</c>）。平台把它显示在过滤器的下拉里。</summary>
    public string Name { get; set; } = "";

    /// <summary>通配模式，如 <c>["*.txt", "*.md"]</c>。不要带 <c>;</c>（那是字符串形式的写法）。</summary>
    public IReadOnlyList<string> Patterns { get; set; } = [];

    /// <summary>构造一条过滤器。</summary>
    public static OrielFileFilter Of(string name, params string[] patterns)
        => new() { Name = name, Patterns = patterns };

    /// <summary>「所有文件」过滤器（各平台通用写法）。</summary>
    public static OrielFileFilter AllFiles { get; } = Of("所有文件", "*.*");

    /// <summary>
    /// 解析 pywebview 风格的过滤器串：<c>"文本文件|*.txt;*.md|所有文件|*.*"</c>。
    /// </summary>
    /// <remarks>
    /// 宽容规则（与既有 <c>ShowOpenFileDialog(string filter)</c> 的行为一致，避免破坏存量调用）：
    /// 空串或 null 返回空列表；末尾多余的 <c>|</c> 被忽略；只有名字没有模式（奇数个片段）时丢弃该片段——
    /// 一个没有模式的过滤器在界面上是不可选的，留着只会让人困惑。
    /// </remarks>
    public static IReadOnlyList<OrielFileFilter> Parse(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return [];
        }

        string[] parts = filter.TrimEnd('|').Split('|');
        var result = new List<OrielFileFilter>((parts.Length + 1) / 2);
        for (int i = 0; i + 1 < parts.Length; i += 2)
        {
            string name = parts[i].Trim();
            string[] patterns = parts[i + 1].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (name.Length == 0 || patterns.Length == 0)
            {
                continue;
            }

            result.Add(Of(name, patterns));
        }

        return result;
    }

    /// <summary>
    /// 把一组过滤器渲染成 Win32 <c>OPENFILENAMEW.lpstrFilter</c> 的形状：
    /// <c>名称\0模式1;模式2\0…\0</c>（**双 null 结尾**，这是 Win32 的要求，少一个就会读越界）。
    /// </summary>
    /// <remarks>
    /// 空列表退回「所有文件」而不是返回空串：没有过滤器的打开对话框在各平台上的表现不一致
    /// （Windows 会显示不出类型下拉），统一给一个即可。
    /// </remarks>
    internal static string RenderForWin32(IReadOnlyList<OrielFileFilter> filters)
    {
        if (filters.Count == 0)
        {
            OrielFileFilter all = AllFiles;
            return $"{all.Name}\0{string.Join(';', all.Patterns)}\0\0";
        }

        var builder = new System.Text.StringBuilder();
        foreach (OrielFileFilter filter in filters)
        {
            if (filter.Patterns.Count == 0)
            {
                continue;
            }

            builder.Append(filter.Name).Append('\0').Append(string.Join(';', filter.Patterns)).Append('\0');
        }

        // 一个有效条目都没有时仍然要给 Win32 一份可用数据
        if (builder.Length == 0)
        {
            OrielFileFilter all = AllFiles;
            return $"{all.Name}\0{string.Join(';', all.Patterns)}\0\0";
        }

        return builder.Append('\0').ToString();
    }

    /// <summary>GTK 侧逐条 <c>gtk_file_filter_add_pattern</c> 所需的模式（各条过滤器展开后合并）。</summary>
    /// <remarks>
    /// <c>*.*</c> 被归一成 <c>*</c>：GTK 的 pattern 走 fnmatch 语义，<c>*.*</c> 要求文件名里**必须有点**，
    /// 于是「所有文件」会漏掉 <c>README</c>、<c>Makefile</c> 这类无扩展名文件——
    /// 用户看到的会是"明明选了所有文件却看不到这个文件"。
    /// </remarks>
    internal static IReadOnlyList<string> GtkPatterns(IReadOnlyList<OrielFileFilter> filters)
        => [.. filters.SelectMany(f => f.Patterns)
            .Select(p => p.Trim() is "*.*" or "*" ? "*" : p.Trim())];

    /// <summary>
    /// Cocoa <c>NSOpenPanel.allowedFileTypes</c> 所需的扩展名（<c>"*.tar.gz"</c> → <c>"tar.gz"</c>）。
    /// </summary>
    /// <remarks>
    /// 「任意文件」的写法（<c>*</c> / <c>*.*</c>）被**剔除**：Cocoa 表示"不限类型"的方式是
    /// 根本不设 <c>allowedFileTypes</c>，塞一个 <c>*</c> 进去反而会把面板锁成只能选无扩展名文件。
    /// 因此结果为空时调用方应当跳过设置，而不是把空数组设进去。
    /// </remarks>
    internal static IReadOnlyList<string> CocoaExtensions(IReadOnlyList<OrielFileFilter> filters)
    {
        var result = new List<string>();
        foreach (string pattern in filters.SelectMany(f => f.Patterns))
        {
            string trimmed = pattern.Trim();
            if (trimmed is "*" or "*.*")
            {
                continue;
            }

            string extension = trimmed.StartsWith("*.", StringComparison.Ordinal) ? trimmed[2..] : trimmed;
            if (extension.Length > 0 && !result.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(extension);
            }
        }

        return result;
    }
}
