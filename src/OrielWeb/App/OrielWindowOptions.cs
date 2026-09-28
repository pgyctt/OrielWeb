namespace OrielWeb;

/// <summary>窗口初始配置（对齐 pywebview 基本面）。用 With* 方法链式配置。</summary>
public sealed class OrielWindowOptions
{
    public string Title { get; set; } = "Oriel";
    public int Width { get; set; } = 1000;
    public int Height { get; set; } = 700;
    public int? MinWidth { get; set; }
    public int? MinHeight { get; set; }
    /// <summary>初始位置；未设置且 <see cref="Center"/> 为 true 时居中。</summary>
    public int? X { get; set; }
    public int? Y { get; set; }
    public bool Center { get; set; } = true;
    public bool Resizable { get; set; } = true;
    public bool Fullscreen { get; set; }
    public bool OnTop { get; set; }
    public bool Frameless { get; set; }
    public bool Hidden { get; set; }
    public bool Maximized { get; set; }
    /// <summary>直接加载的外部 URL（如 Vite dev server）。未设置时使用内嵌资产首页。</summary>
    public string? Url { get; set; }
    /// <summary>是否启用开发者工具（DevTools）。</summary>
    public bool Debug { get; set; }

    public OrielWindowOptions WithTitle(string title) { Title = title; return this; }
    public OrielWindowOptions WithSize(int width, int height) { Width = width; Height = height; return this; }
    public OrielWindowOptions WithMinSize(int width, int height) { MinWidth = width; MinHeight = height; return this; }
    public OrielWindowOptions At(int x, int y) { X = x; Y = y; Center = false; return this; }
    public OrielWindowOptions Centered(bool center = true) { Center = center; return this; }
    public OrielWindowOptions WithResizable(bool resizable = true) { Resizable = resizable; return this; }
    public OrielWindowOptions WithFullscreen(bool fullscreen = true) { Fullscreen = fullscreen; return this; }
    public OrielWindowOptions WithOnTop(bool onTop = true) { OnTop = onTop; return this; }
    public OrielWindowOptions WithFrameless(bool frameless = true) { Frameless = frameless; return this; }
    public OrielWindowOptions WithHidden(bool hidden = true) { Hidden = hidden; return this; }
    public OrielWindowOptions WithMaximized(bool maximized = true) { Maximized = maximized; return this; }
    public OrielWindowOptions WithUrl(string url) { Url = url; return this; }
    public OrielWindowOptions WithDebug(bool debug = true) { Debug = debug; return this; }
}
