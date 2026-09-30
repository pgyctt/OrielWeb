namespace OrielWeb;

/// <summary>系统通知的内容与标识。</summary>
public sealed class OrielNotificationOptions
{
    /// <summary>标题（多数平台会加粗显示；不可为空）。</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>正文（可省略）。</summary>
    public string? Body { get; set; }

    /// <summary>附加图标文件路径（部分平台忽略）。</summary>
    public string? IconPath { get; set; }

    /// <summary>
    /// 通知标识：用户点击该通知时，<see cref="OrielApp.NotificationClicked"/> 回传的就是它。
    /// 平台间差异较大（见 README 平台矩阵）：Linux 通过 libnotify 的 action 可靠上报；
    /// Windows 只在气泡由托盘承载时可上报；macOS 未打包运行时不支持点击上报。
    /// </summary>
    public string? Id { get; set; }
}
