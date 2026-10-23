using System.Text.Json;
using OrielWeb.Platform.Windows.Interop;

namespace OrielWeb.Platform.Windows;

/// <summary>
/// Win32 侧与宿主状态无关的换算与映射。
/// </summary>
/// <remarks>
/// <para>
/// 这个类的收录标准只有一条：**不碰宿主字段、不调 P/Invoke**。满足它的函数放进来，
/// 就能用普通单测覆盖；需要窗口句柄、消息循环或真实 COM 对象的，仍然留在
/// <see cref="Win32WindowHost"/> 里。
/// </para>
/// <para>
/// 抽出来的直接原因是覆盖面：平台层占全仓一半以上代码，却几乎没有托管测试——
/// 不是因为不值得测，而是逻辑与 P/Invoke 掺在一个类里，构造不出测试替身。
/// 把纯计算分离出来，等于把"能不能测"变成"要不要测"。
/// </para>
/// </remarks>
internal static class Win32Conversions
{
    /// <summary>ARGB → ABGR（强调色策略的 <c>GradientColor</c> 是 ABGR 序）。</summary>
    internal static uint ToAbgr(uint argb)
    {
        uint a = (argb >> 24) & 0xFF;
        uint r = (argb >> 16) & 0xFF;
        uint g = (argb >> 8) & 0xFF;
        uint b = argb & 0xFF;
        return (a << 24) | (b << 16) | (g << 8) | r;
    }

    internal static uint ComputeStyle(OrielWindowOptions options)
    {
        uint style;
        if (options.Frameless)
        {
            style = Win32Constants.WS_POPUP | Win32Constants.WS_SYSMENU;
            if (options.Resizable)
            {
                style |= Win32Constants.WS_THICKFRAME | Win32Constants.WS_MAXIMIZEBOX | Win32Constants.WS_MINIMIZEBOX;
            }
        }
        else
        {
            style = Win32Constants.WS_OVERLAPPEDWINDOW;
            if (!options.Resizable)
            {
                style &= ~(Win32Constants.WS_THICKFRAME | Win32Constants.WS_MAXIMIZEBOX);
            }
        }
        return style;
    }

    /// <summary>读字符串属性；缺失或类型不符时返回空串（页面数据不可信，一律按可缺席处理）。</summary>
    internal static string ReadStringProperty(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    /// <summary>默认提示：区分"系统未装 Evergreen 运行时"与"指定的固定版本目录无效"两种情形。</summary>
    internal static string RuntimeMissingMessage(string? browserFolder) =>
        browserFolder is null
            ? "未检测到 Microsoft Edge WebView2 运行时，界面无法显示。\r\n\r\n" +
              "请安装 WebView2 运行时后重新启动本应用：\r\n" +
              OrielWebView2RuntimeMissingEventArgs.DownloadUrl
            : "未在指定目录找到 WebView2 运行时，界面无法显示。\r\n\r\n" +
              $"目录：{browserFolder}\r\n\r\n" +
              "请确认环境变量 ORIEL_WEBVIEW2_FOLDER 指向有效的固定版本运行时目录；" +
              "如需改用系统 Evergreen 运行时，请清除该变量。";
}
