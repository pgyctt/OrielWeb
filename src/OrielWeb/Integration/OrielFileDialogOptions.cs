namespace OrielWeb;

/// <summary>打开文件对话框的参数。取消时返回空数组。</summary>
/// <remarks>
/// 与保存分成两个类型而不是共用一个大选项类：两者能用的参数本来就不同
/// （打开可多选但没有 <see cref="OrielSaveFileDialogOptions.DefaultExtension"/>，保存反之）。
/// 合成一个类就得靠运行时忽略无关字段，调用方看不出"这个字段在这里没用"。
/// </remarks>
public sealed class OrielOpenFileDialogOptions
{
    /// <summary>对话框标题；为 null 时用平台默认文案。</summary>
    public string? Title { get; set; }

    /// <summary>
    /// 过滤器列表。用 <see cref="OrielFileFilter.Of"/> 构造，或把字符串形式交给
    /// <see cref="OrielFileFilter.Parse"/>。为空时按「所有文件」处理。
    /// </summary>
    public IReadOnlyList<OrielFileFilter>? Filters { get; set; }

    /// <summary>初始目录；为 null 时由平台决定（Windows 上会落到文档目录而非系统目录）。</summary>
    public string? InitialDirectory { get; set; }

    /// <summary>
    /// 允许多选。Windows 上多选的返回值是**目录 + 多个文件名**（<c>\0</c> 分隔），
    /// 拼接由 <c>Win32OpenFileDialog</c> 负责——这一步在 Windows 上必须带 <c>OFN_EXPLORER</c>，
    /// 否则原生对话框会用老旧格式返回，单个文件也会带出目录。
    /// </summary>
    public bool AllowMultiple { get; set; }
}

/// <summary>保存文件对话框的参数。取消时返回 null。</summary>
public sealed class OrielSaveFileDialogOptions
{
    /// <summary>对话框标题；为 null 时用平台默认文案。</summary>
    public string? Title { get; set; }

    /// <summary>过滤器列表（含义同 <see cref="OrielOpenFileDialogOptions.Filters"/>）。</summary>
    public IReadOnlyList<OrielFileFilter>? Filters { get; set; }

    /// <summary>初始目录；为 null 时由平台决定。</summary>
    public string? InitialDirectory { get; set; }

    /// <summary>
    /// 用户没写扩展名时补上的扩展名（不含点，如 <c>"txt"</c>）。各平台补的时机不同：
    /// Windows 交给原生对话框（<c>lpstrDefExt</c>）、GTK 预填一个「未命名.txt」的默认名、
    /// Cocoa 由 <c>allowedFileTypes</c> 与系统行为共同决定。
    /// </summary>
    public string? DefaultExtension { get; set; }

    /// <summary>预填的文件名（不含路径）。为 null 时不预填。</summary>
    public string? DefaultFileName { get; set; }
}
