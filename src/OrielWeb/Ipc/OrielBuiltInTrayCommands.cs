using System.Text.Json;

namespace OrielWeb.Ipc;

/// <summary>
/// 内建托盘命令（<c>tray.*</c>）：页面侧经 <c>oriel.tray</c> 命名空间操作托盘，
/// 不必由应用注册任何命令（对标 Tauri 2 的 <c>plugin:tray</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 这一组与 <c>win.*</c> 同属"库保留前缀"（见 <see cref="OrielCapabilityRules.BuiltInCommandPrefix"/>），
/// 因此**始终放行、不受能力配置约束**。理由与窗口命令一致：它们只操作本应用自己的托盘，
/// 不触及文件系统、网络或外部程序。代价是 <c>tray.</c> 也成了保留前缀。
/// </para>
/// <para>
/// 托盘是**应用级**资源，命令从某个窗口发起时经 <see cref="IOrielWindowControl.App"/> 拿到应用。
/// 页面拿到的句柄只是一个 id 字符串——真正的对象始终留在宿主侧，页面从不直接持有原生资源。
/// </para>
/// </remarks>
internal static class OrielBuiltInTrayCommands
{
    /// <summary>取某个 <c>tray.*</c> 命令的处理函数；不是内建命令时返回 false。</summary>
    /// <param name="name">命令名（区分大小写，与命令授权用的是同一套 Ordinal 比较）。</param>
    /// <param name="handler">处理函数：窗口 + args → 回执里的 value。</param>
    internal static bool TryGet(string name, out Func<IOrielWindowControl, JsonElement, object?> handler)
    {
        switch (name)
        {
            case "tray.new":
                handler = static (window, args) =>
                {
                    var options = new OrielTrayOptions
                    {
                        Id = Text(args, "id"),
                        Tooltip = Text(args, "tooltip") ?? "OrielWeb",
                        Title = Text(args, "title"),
                        IconAsTemplate = Bool(args, "iconAsTemplate") ?? true,
                        MenuOnClick = Bool(args, "menuOnClick") ?? false,
                    };

                    // 图标：页面给的是路径（PNG/ICO，相对路径按 exe 目录解析）。
                    // 解码失败**不抛**——页面写错一个路径不该让命令失败，回落平台默认并报诊断即可。
                    string? iconPath = Text(args, "iconPath");
                    if (iconPath is not null)
                    {
                        options.IconPath = iconPath;
                    }

                    OrielTray tray = window.App.CreateTray(o => CopyInto(options, o));

                    // 菜单与 tray.setMenu 同一套线格式（kind/text，见 ParseMenuItems）。
                    // 创建时就带菜单是 Tauri 的 TrayIconOptions.menu 的等价物——
                    // 不解析的话页面传了 menu 会被静默忽略（"图标设了菜单没出来"）。
                    if (args.ValueKind == JsonValueKind.Object
                        && args.TryGetProperty("items", out JsonElement items)
                        && items.ValueKind == JsonValueKind.Array)
                    {
                        tray.SetMenu(OrielBuiltInMenuCommands.ParseMenuItems(items));
                    }

                    return tray.Id;
                };
                return true;
            case "tray.getById":
                handler = static (window, args) =>
                {
                    string id = Required(args, "id", "tray.getById");
                    // 返回 id 字符串（而不是一个 DTO）：内建命令的返回值要能被 AOT 安全的
                    // 基元序列化直接写出，而自定义 DTO 需要应用注册 STJ 上下文——
                    // 内建命令不该要求应用为它注册类型。页面侧拿到 id 即构成句柄（见 oriel.tray）。
                    return window.App.GetTray(id)?.Id;
                };
                return true;

            case "tray.getAll":
                // 手写 JSON 数组（而不是 string[] / 匿名对象）：内建命令的返回值不该依赖应用的
                // STJ 上下文——数组与自定义类型都要 [JsonSerializable] 注册，那等于要求应用
                // 为库的内部类型准备序列化信息；而反射式序列化在 AOT 下直接被分析器拦下（IL2026/IL3050）。
                // JsonElement 由 WriteResult 直接写出，零上下文、零反射。
                handler = static (window, _) =>
                {
                    var buffer = new System.Buffers.ArrayBufferWriter<byte>();
                    using (var writer = new Utf8JsonWriter(buffer))
                    {
                        writer.WriteStartArray();
                        foreach (OrielTray tray in window.App.Trays)
                        {
                            writer.WriteStringValue(tray.Id);
                        }

                        writer.WriteEndArray();
                    }

                    return JsonDocument.Parse(buffer.WrittenMemory).RootElement.Clone();
                };
                return true;

            case "tray.describe":
                // 供诊断：把托盘的当前可读属性拼成一段文本（页面/宿主排障时看"现在到底是什么状态"）
                handler = static (window, args) =>
                {
                    string id = Required(args, "id", "tray.describe");
                    OrielTray? tray = window.App.GetTray(id);
                    return tray is null ? null : Describe(tray);
                };
                return true;
            case "tray.removeById":
                handler = static (window, args) =>
                    window.App.RemoveTray(Required(args, "id", "tray.removeById"));
                return true;

            case "tray.setIcon":
                // 三形态与 window.setIcon 同一套（B12）：iconPath（路径，null=清除）/
                // bytes（PNG·ICO 字节）/ rgba+width+height（裸像素）。
                // 页面侧 @orielweb/api/tray 的 setIcon 把 JsImage 编成这三种之一。
                //
                // **参数校验在 Find 之前**：调用方写错了参数要能拿到"参数错"而不是
                // "托盘不存在"——后者会把人带去查 id，而真正的问题在 base64。
                handler = static (window, args) =>
                {
                    OrielImage? image = null;
                    string? iconPath = null;
                    bool hasPath = false;

                    // iconPath 属性存在（哪怕值是 null）就走路径分支：null 表示"清除图标"
                    if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("iconPath", out _))
                    {
                        hasPath = true;
                        iconPath = Text(args, "iconPath");
                    }
                    else if (OptionalString(args, "bytes") is { } base64)
                    {
                        byte[] decoded;
                        try
                        {
                            decoded = Convert.FromBase64String(base64);
                        }
                        catch (FormatException ex)
                        {
                            // 说清是**编码**错而不是图像错：这两种错误的修法完全不同
                            throw new OrielIpcException($"tray.setIcon 的 bytes 不是合法的 base64：{ex.Message}");
                        }

                        image = OrielImage.FromBytes(decoded);
                    }
                    else if (OptionalString(args, "rgba") is { } rgbaBase64)
                    {
                        byte[] rgba;
                        try
                        {
                            rgba = Convert.FromBase64String(rgbaBase64);
                        }
                        catch (FormatException ex)
                        {
                            throw new OrielIpcException($"tray.setIcon 的 rgba 不是合法的 base64：{ex.Message}");
                        }

                        int width = RequiredInt(args, "width", "tray.setIcon");
                        int height = RequiredInt(args, "height", "tray.setIcon");

                        if (width <= 0 || height <= 0)
                        {
                            throw new OrielIpcException(
                                $"tray.setIcon 的尺寸必须是正整数（收到 {width}x{height}）。");
                        }

                        if (rgba.Length != width * height * 4)
                        {
                            throw new OrielIpcException(
                                $"tray.setIcon 的 rgba 长度不符：{width}x{height} 需要 " +
                                $"{width * height * 4} 字节，收到 {rgba.Length} 字节。");
                        }

                        image = new OrielImage(rgba, width, height);
                    }
                    else
                    {
                        throw new OrielIpcException("tray.setIcon 需要 iconPath、bytes 或 rgba+width+height 其中之一。");
                    }

                    OrielTray tray = Find(window, args, "tray.setIcon");
                    if (hasPath)
                    {
                        tray.SetIcon(iconPath);
                    }
                    else
                    {
                        tray.SetIcon(image);
                    }

                    return null;
                };
                return true;

            case "tray.setIconAsTemplate":
                handler = static (window, args) =>
                {
                    OrielTray tray = Find(window, args, "tray.setIconAsTemplate");
                    tray.IconAsTemplate = Bool(args, "asTemplate") ?? true;
                    return null;
                };
                return true;

            case "tray.setMenuOnLeftClick":
                // 对标 Tauri 的 setMenuOnLeftClick / setShowMenuOnLeftClick（两个方法一个开关）。
                // **只在 Windows 生效**：Linux 与 macOS 上"左键弹菜单"是平台惯例（Tauri 对
                // Linux 同标注 Unsupported），本库后端没接通开关——如实抛 not_supported，
                // 不让"关掉了左键菜单"变成一句空话（B12 护栏：不支持 ≠ 静默忽略）。
                handler = static (window, args) =>
                {
                    // 平台判断在 Find 之前："这平台不支持"与"托盘不存在"是两回事，
                    // 后者会把人带去查 id，而真正的问题是平台。
                    if (!OperatingSystem.IsWindows())
                    {
                        throw new OrielIpcException(
                            "tray.setMenuOnLeftClick 仅 Windows 支持：Linux 与 macOS 的左键行为由平台惯例决定"
                            + "（左键就弹菜单，Tauri 对 Linux 同标注 Unsupported）。")
                        {
                            Code = OrielIpcErrorCodes.NotSupported,
                        };
                    }

                    OrielTray tray = Find(window, args, "tray.setMenuOnLeftClick");
                    tray.MenuOnClick = Bool(args, "onLeft") ?? true;
                    return null;
                };
                return true;

            case "tray.setMenu":
                handler = static (window, args) =>
                {
                    OrielTray tray = Find(window, args, "tray.setMenu");
                    tray.SetMenu(ReadMenu(args));
                    return null;
                };
                return true;

            case "tray.setTooltip":
                handler = static (window, args) =>
                {
                    OrielTray tray = Find(window, args, "tray.setTooltip");
                    tray.Tooltip = Text(args, "tooltip") ?? string.Empty;
                    return null;
                };
                return true;

            case "tray.setTitle":
                handler = static (window, args) =>
                {
                    OrielTray tray = Find(window, args, "tray.setTitle");
                    tray.Title = Text(args, "title");
                    return null;
                };
                return true;

            case "tray.setVisible":
                handler = static (window, args) =>
                {
                    OrielTray tray = Find(window, args, "tray.setVisible");
                    if (Bool(args, "visible") ?? true)
                    {
                        tray.Show();
                    }
                    else
                    {
                        tray.Hide();
                    }

                    return null;
                };
                return true;

            default:
                handler = null!;
                return false;
        }
    }

    /// <summary>把解析好的选项复制进库自己造的那个实例（<c>CreateTray</c> 的 configure 回调）。</summary>
    private static void CopyInto(OrielTrayOptions source, OrielTrayOptions target)
    {
        target.Id = source.Id;
        target.Tooltip = source.Tooltip;
        target.Title = source.Title;
        target.IconAsTemplate = source.IconAsTemplate;
        target.MenuOnClick = source.MenuOnClick;
        if (source.IconPath is { } path)
        {
            target.IconPath = path;
        }
    }

    /// <summary>页面可见的托盘描述（诊断用文本）。</summary>
    private static string Describe(OrielTray tray)
        => $"id={tray.Id} tooltip={tray.Tooltip} title={tray.Title ?? "(无)"} " +
           $"icon={tray.IconPath ?? "(默认)"} template={tray.IconAsTemplate} visible={tray.IsVisible}";

    private static OrielTray Find(IOrielWindowControl window, JsonElement args, string commandName)
    {
        string id = Required(args, "id", commandName);
        return window.App.GetTray(id)
            ?? throw new OrielIpcException($"托盘「{id}」不存在（{commandName}）。");
    }

    /// <summary>
    /// 解析页面传来的菜单项数组——**与 <c>menu.*</c> 同一套线格式**（B12 统一，
    /// 见 <see cref="OrielBuiltInMenuCommands.ParseMenuItems"/>）。
    /// </summary>
    private static IReadOnlyList<OrielMenuItem> ReadMenu(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object
            || !args.TryGetProperty("items", out JsonElement items)
            || items.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return OrielBuiltInMenuCommands.ParseMenuItems(items);
    }

    /// <summary>读一个必给的字符串参数；缺了就报错（静默用空串会让命令作用到错误的对象上）。</summary>
    private static string Required(JsonElement args, string property, string commandName)
    {
        string? value = Text(args, property);
        return string.IsNullOrEmpty(value)
            ? throw new OrielIpcException($"命令 '{commandName}' 缺少字符串参数 '{property}'。")
            : value;
    }

    /// <summary>读一个可选字符串参数。</summary>
    private static string? Text(JsonElement args, string property)
        => args.ValueKind == JsonValueKind.Object
           && args.TryGetProperty(property, out JsonElement element)
           && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    /// <summary>读一个可选布尔参数。</summary>
    private static bool? Bool(JsonElement args, string property)
        => args.ValueKind == JsonValueKind.Object
           && args.TryGetProperty(property, out JsonElement element)
           && element.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? element.GetBoolean()
            : null;

    /// <summary>读一个可选字符串参数（与 <c>window.*</c> 命令同名同义，tray 的图标三形态用）。</summary>
    private static string? OptionalString(JsonElement args, string property)
        => Text(args, property);

    /// <summary>读一个必给的整数参数；缺了或非整数就报错。</summary>
    private static int RequiredInt(JsonElement args, string property, string commandName)
        => args.ValueKind == JsonValueKind.Object
           && args.TryGetProperty(property, out JsonElement element)
           && element.ValueKind == JsonValueKind.Number
           && element.TryGetInt32(out int value)
            ? value
            : throw new OrielIpcException($"命令 '{commandName}' 缺少整数参数 '{property}'。");
}
