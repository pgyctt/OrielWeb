namespace OrielWeb;

/// <summary>系统主题（浅色 / 深色）。由宿主的平台后端检测，见 <see cref="OrielApp.Theme"/>。</summary>
public enum OrielTheme
{
    /// <summary>浅色（也是检测不到时的默认值）。</summary>
    Light,
    /// <summary>深色。</summary>
    Dark,
}
