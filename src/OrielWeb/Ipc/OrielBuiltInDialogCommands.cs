using System.Text.Json;

namespace OrielWeb.Ipc;

/// <summary>
/// 内建对话框命令族（<c>dialog.*</c>）：对标 Tauri 2 的 <c>@tauri-apps/plugin-dialog</c>。
/// </summary>
/// <remarks>
/// <para>
/// <b>与 <c>win.pickFile</c> / <c>window.pickFile</c> 的关系（一个刻意的决定）</b>：
/// 那两条是既有的公开契约，**冻结不动**——它们只收 <c>title</c> / <c>filter</c>，
/// 返回单个路径。本族是新入口，收 Tauri 形状的 options（多选 / 过滤器 / 初始目录）
/// 并返回数组。
/// </para>
/// <para>
/// 两者**共用底下的原生对话框**（<c>IPlatformBackend.ShowOpenFileDialog</c> 本来就收
/// <see cref="OrielOpenFileDialogOptions"/>，多选与过滤器早就在里面）——
/// 把原生实现抄第二份才是真的错：两份必然在某个平台上不一致。所以"独立"指的是
/// <b>命令契约独立</b>（互不影响、可以各自演进），不是复制原生代码。
/// </para>
/// <para>
/// <b>门禁姿态</b>：本族需要声明。它们会弹模态对话框并让用户选路径，
/// 而"能弹对话框"在无人值守场景里是有意义的（挡住自动化流程）——
/// 不过 <c>win.pickFile</c> 已经在白名单里了，所以这里不是为了拦弹窗，
/// 而是为了让**新族**与既有的门禁姿态一致（新的都要声明）。
/// </para>
/// </remarks>
internal static class OrielBuiltInDialogCommands
{
    /// <summary>命令名前缀。</summary>
    internal const string Prefix = "dialog.";

    /// <summary>取某个 <c>dialog.*</c> 命令的处理函数；不是本族命令时返回 false。</summary>
    internal static bool TryGet(string name, out Func<IOrielWindowControl, string, JsonElement, object?> handler)
    {
        switch (name)
        {
            case "dialog.open":
                handler = static (window, command, args) => Open(window, args);
                return true;

            case "dialog.save":
                handler = static (window, command, args) => Save(window, args);
                return true;

            case "dialog.message":
                handler = static (window, command, args) => Message(window, args);
                return true;

            default:
                handler = null!;
                return false;
        }
    }

    /// <summary>
    /// 打开文件（或目录）选择对话框。
    /// </summary>
    /// <returns>选中的路径数组；取消时是空数组。</returns>
    /// <remarks>
    /// <para>
    /// <b>返回数组</b>（对标 Tauri 的 <c>open</c>：单选给 <c>string</c>、多选给 <c>string[]</c>、
    /// 取消给 <c>null</c>）。命令层统一给数组，页面侧的包装负责按"是不是单选"摊平——
    /// 那样页面拿到的形状与 Tauri 一致，而命令层的返回值形状稳定（不需要页面猜）。
    /// </para>
    /// <para>
    /// <c>directory: true</c> 走另一个原生对话框（<c>ShowFolderDialog</c>），
    /// 而且它**只支持单选**——Tauri 那边也是。多选 + directory 的组合会报错而不是静默只取第一个。
    /// </para>
    /// </remarks>
    private static object Open(IOrielWindowControl window, JsonElement args)
    {
        JsonElement options = GetOptions(args);

        RejectUnsupported(options, "canCreateDirectories", "recursive");

        bool directory = ReadBool(options, "directory", fallback: false);
        bool multiple = ReadBool(options, "multiple", fallback: false);
        string? title = ReadString(options, "title");
        string? defaultPath = ReadString(options, "defaultPath");

        if (directory)
        {
            if (multiple)
            {
                throw new OrielIpcException(
                    "dialog.open 不支持同时指定 directory 与 multiple：目录选择器只支持单选" +
                    "（与 Tauri 一致）。要选多个目录请多次调用。")
                {
                    Code = OrielIpcErrorCodes.InvalidArgument,
                };
            }

            string? folder = window.ShowFolderDialog(title, SplitDirectory(defaultPath));
            return folder is null ? [] : new[] { folder };
        }

        var dialogOptions = new OrielOpenFileDialogOptions
        {
            Title = title,
            Filters = ParseFilters(options),
            InitialDirectory = SplitDirectory(defaultPath),
            AllowMultiple = multiple,
        };

        return window.ShowOpenFileDialog(dialogOptions);
    }

    /// <summary>保存文件对话框。</summary>
    /// <returns>选中的路径；取消时是 <c>null</c>。</returns>
    /// <remarks>
    /// Tauri 的 <c>save</c> 里 <c>defaultPath</c> 既可能是目录也可能是完整文件名，
    /// 而本库的原生层把它拆成 <c>InitialDirectory</c> 与 <c>DefaultFileName</c> 两个字段。
    /// 这里的拆法是：以分隔符结尾、或者是已存在的目录 → 当目录；否则把最后一段当文件名。
    /// </remarks>
    private static object? Save(IOrielWindowControl window, JsonElement args)
    {
        JsonElement options = GetOptions(args);

        RejectUnsupported(options, "canCreateDirectories");

        string? defaultPath = ReadString(options, "defaultPath");
        (string? directory, string? fileName) = SplitDefaultPath(defaultPath);

        var dialogOptions = new OrielSaveFileDialogOptions
        {
            Title = ReadString(options, "title"),
            Filters = ParseFilters(options),
            InitialDirectory = directory,
            DefaultFileName = fileName,
        };

        return window.ShowSaveFileDialog(dialogOptions);
    }

    /// <summary>
    /// 弹一个消息对话框，返回**用户点了哪个语义按钮**。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 返回语义（<c>"ok"</c> / <c>"cancel"</c> / <c>"yes"</c> / <c>"no"</c> / <c>null</c>）
    /// 而不是按钮文案。理由见 <see cref="IWindowBackend.ShowMessageDialog"/> 的说明：
    /// 三平台能显示的自定义文案不同，若返回"实际显示的文案"，页面拿它与期望标签比对时
    /// 会在 Windows 上**静默得到错误答案**。文案由页面侧的包装自己管。
    /// </para>
    /// <para>
    /// 参数名是 <c>message</c> 而不是 <c>text</c>：Tauri 的命令字段就是 <c>message</c>。
    /// </para>
    /// </remarks>
    private static object? Message(IOrielWindowControl window, JsonElement args)
    {
        string message = OrielJson.GetRequiredArg<string>(args, "message", null);
        string? title = ReadString(args, "title");
        string kind = ReadString(args, "kind") ?? "info";

        (MessageButtons buttons, string okLabel, string cancelLabel) = ParseButtons(args);

        MessageResult? result = window.ShowMessageDialog(message, title, kind, buttons, okLabel, cancelLabel);

        return result switch
        {
            MessageResult.Ok => "ok",
            MessageResult.Cancel => "cancel",
            MessageResult.Yes => "yes",
            MessageResult.No => "no",
            _ => null,
        };
    }

    // ---------------------------------------------------------------- 参数

    /// <summary>取 Tauri 的 <c>options</c> 字段（缺省时空对象）。</summary>
    private static JsonElement GetOptions(JsonElement args)
        => args.ValueKind == JsonValueKind.Object && args.TryGetProperty("options", out JsonElement options)
           && options.ValueKind == JsonValueKind.Object
            ? options
            : default;

    /// <summary>拒绝本库没有对应的选项，而不是静默忽略。</summary>
    /// <remarks>
    /// 静默忽略一个布尔选项最危险的地方在于：调用方以为它生效了。
    /// 比如 <c>canCreateDirectories: false</c> 被忽略后用户仍能新建目录——
    /// 而调用方已经不再校验那条路径是否存在了。
    /// </remarks>
    private static void RejectUnsupported(JsonElement options, params string[] names)
    {
        foreach (string name in names)
        {
            if (options.ValueKind == JsonValueKind.Object
                && options.TryGetProperty(name, out JsonElement value)
                && value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                throw new OrielIpcException(
                    $"dialog.open/save 的选项 '{name}' 尚未实现：请省略它。" +
                    "本库不会静默忽略一个没生效的选项——那会让调用方以为它已经生效。")
                {
                    Code = OrielIpcErrorCodes.NotSupported,
                };
            }
        }
    }

    /// <summary>
    /// 把 Tauri 的 <c>filters</c> 转成原生层的过滤器。
    /// </summary>
    /// <remarks>
    /// Tauri 的形状是 <c>[{ name, extensions: ["png", "jpg"] }]</c>，
    /// 而原生层要的是 <c>OrielFileFilter</c>（<c>Patterns = ["*.png", "*.jpg"]</c>）。
    /// 扩展名带不带点都收（两种写法在真实配置里都常见）。
    /// </remarks>
    private static IReadOnlyList<OrielFileFilter>? ParseFilters(JsonElement options)
    {
        if (options.ValueKind != JsonValueKind.Object
            || !options.TryGetProperty("filters", out JsonElement filters)
            || filters.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var result = new List<OrielFileFilter>();

        foreach (JsonElement filter in filters.EnumerateArray())
        {
            if (filter.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string name = ReadString(filter, "name") ?? "文件";

            if (!filter.TryGetProperty("extensions", out JsonElement extensions)
                || extensions.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var patterns = new List<string>();

            foreach (JsonElement extension in extensions.EnumerateArray())
            {
                if (extension.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                string value = extension.GetString()!;

                // `*` 是"所有文件"的约定写法（Tauri 的文档里就这么用），原样保留；
                // 别的值补上 `*.` 前缀，而带点与不带点两种写法都收。
                patterns.Add(value switch
                {
                    "*" => "*",
                    _ when value.StartsWith('*') => value,
                    _ when value.StartsWith('.') => "*" + value,
                    _ => "*." + value,
                });
            }

            if (patterns.Count > 0)
            {
                result.Add(new OrielFileFilter { Name = name, Patterns = patterns });
            }
        }

        return result.Count > 0 ? result : null;
    }

    /// <summary>
    /// 从 <c>defaultPath</c> 里取初始目录。
    /// </summary>
    /// <remarks>
    /// Tauri 的 <c>defaultPath</c> 在 <c>open</c> 里通常是目录，但也可能给了完整路径。
    /// 判据是"以分隔符结尾、或者本身是个已存在的目录"——**不猜**"最后一段没有扩展名所以是目录"：
    /// 那种猜法会把 <c>Notes</c>（一个没有扩展名的文件名）当成目录。
    /// </remarks>
    private static string? SplitDirectory(string? defaultPath)
    {
        if (string.IsNullOrWhiteSpace(defaultPath))
        {
            return null;
        }

        try
        {
            if (Directory.Exists(defaultPath))
            {
                return defaultPath;
            }

            return defaultPath.EndsWith(Path.DirectorySeparatorChar)
                || defaultPath.EndsWith(Path.AltDirectorySeparatorChar)
                ? defaultPath
                : Path.GetDirectoryName(defaultPath);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>把 <c>defaultPath</c> 拆成"初始目录 + 预填文件名"。</summary>
    private static (string? Directory, string? FileName) SplitDefaultPath(string? defaultPath)
    {
        if (string.IsNullOrWhiteSpace(defaultPath))
        {
            return (null, null);
        }

        if (Directory.Exists(defaultPath))
        {
            return (defaultPath, null);
        }

        try
        {
            string? directory = Path.GetDirectoryName(defaultPath);
            string fileName = Path.GetFileName(defaultPath);
            return (directory, fileName.Length > 0 ? fileName : null);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException)
        {
            return (null, null);
        }
    }

    /// <summary>
    /// 解析按钮配置。
    /// </summary>
    /// <returns>按钮集合、确认按钮文案、取消按钮文案。</returns>
    /// <remarks>
    /// 形状照 Tauri 的 <c>buttonsToRust</c>：字符串（<c>Ok</c> / <c>OkCancel</c> /
    /// <c>YesNo</c> / <c>YesNoCancel</c>）或对象（<c>{ok}</c> / <c>{ok,cancel}</c> /
    /// <c>{yes,no,cancel}</c>）。缺省时给一个"确定"。
    /// </remarks>
    private static (MessageButtons Buttons, string OkLabel, string CancelLabel) ParseButtons(JsonElement args)
    {
        if (args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty("buttons", out JsonElement buttons))
        {
            if (buttons.ValueKind == JsonValueKind.String)
            {
                return buttons.GetString() switch
                {
                    "OkCancel" => (MessageButtons.OkCancel, "确定", "取消"),
                    "YesNo" => (MessageButtons.YesNo, "是", "否"),
                    "YesNoCancel" => (MessageButtons.YesNoCancel, "是", "取消"),
                    _ => (MessageButtons.Ok, "确定", "取消"),
                };
            }

            if (buttons.ValueKind == JsonValueKind.Object)
            {
                string? ok = ReadString(buttons, "ok");
                string? cancel = ReadString(buttons, "cancel");
                string? yes = ReadString(buttons, "yes");
                string? no = ReadString(buttons, "no");

                if (yes is not null && no is not null)
                {
                    // yes/no 与 yes/no/cancel 由有没有 cancel 区分
                    return cancel is not null
                        ? (MessageButtons.YesNoCancel, yes, cancel)
                        : (MessageButtons.YesNo, yes, no);
                }

                if (ok is not null)
                {
                    return cancel is not null
                        ? (MessageButtons.OkCancel, ok, cancel)
                        : (MessageButtons.Ok, ok, "取消");
                }
            }
        }

        return (MessageButtons.Ok, "确定", "取消");
    }

    private static string? ReadString(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(name, out JsonElement value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool ReadBool(JsonElement element, string name, bool fallback)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(name, out JsonElement value)
           && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : fallback;
}
