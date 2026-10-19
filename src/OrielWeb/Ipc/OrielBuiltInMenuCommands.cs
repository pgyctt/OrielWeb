using System.Text.Json;

namespace OrielWeb.Ipc;

/// <summary>
/// 内建菜单命令族（<c>menu.*</c>）：对标 Tauri 2 的 <c>@tauri-apps/api/menu</c> 里**弹出**那一半。
/// </summary>
/// <remarks>
/// <para>
/// <b>本族是一条"无状态"通道</b>：页面每次把整棵菜单树发过来，宿主按它构建原生菜单并弹出。
/// Tauri 的做法相反——菜单在 Rust 侧是资源（rid），页面只持有句柄，<c>append</c>/<c>insert</c>
/// 都是对那个资源的增量修改。本库没有资源表（README 里"没有句柄表"那条结构性缺口），
/// 所以只能整棵发。**代价**是每次 popup 的载荷随菜单大小增长；**收益**是没有句柄泄漏、
/// 没有"句柄对应的原生对象在别的窗口上"这类跨窗口错用。
/// </para>
/// <para>
/// 由此派生的两处调用形状差异（写进 API.md，不留给使用者猜）：
/// </para>
/// <list type="bullet">
///   <item><description>菜单对象在页面侧是**纯数据**：<c>new Menu()</c>、<c>append</c>、
///     <c>items()</c> 都在本地完成，不发 IPC；
///     只有 <c>popup()</c> 会到宿主。</description></item>
///   <item><description>菜单项的 <c>action</c> 回调通过 <c>menu.itemclicked</c> 事件回到页面，
///     而不是 Tauri 那种"回调挂在资源上"。见 <see cref="OrielApp.MenuItemClickedEventName"/>。</description></item>
/// </list>
/// </remarks>
internal static class OrielBuiltInMenuCommands
{
    /// <summary>命令名前缀。</summary>
    internal const string Prefix = "menu.";

    /// <summary>菜单树的最大深度（含顶层）。超过即报错。</summary>
    /// <remarks>
    /// 防御性上限，不是功能限制：正常菜单两层就够（顶层 + 子菜单）。没有上限时一棵被构造出来的
    /// 超深树会让 <see cref="ParseItems"/> 递归到栈溢出——那是进程级的死法，
    /// 而这条通道的输入来自页面。
    /// </remarks>
    private const int MaxDepth = 8;

    /// <summary>菜单树的最大项数（所有层级合计）。</summary>
    private const int MaxItems = 500;

    /// <summary>取某个 <c>menu.*</c> 命令的处理函数；不是本族命令时返回 false。</summary>
    internal static bool TryGet(string name, out Func<IOrielWindowControl, JsonElement, object?> handler)
    {
        if (string.Equals(name, "menu.popup", StringComparison.Ordinal))
        {
            handler = static (sender, args) => Popup(sender, args);
            return true;
        }

        if (string.Equals(name, "menu.close", StringComparison.Ordinal))
        {
            handler = static (sender, _) =>
            {
                sender.CloseContextMenu();
                return null;
            };
            return true;
        }

        handler = null!;
        return false;
    }

    /// <summary>弹出一个页面定义的菜单。</summary>
    /// <remarks>
    /// <b>Windows 上这个命令是阻塞的</b>（原生菜单自带模态循环），直到用户选择或取消才回执。
    /// <b>macOS 也是阻塞的</b>（AppKit 的 <c>popUpMenuPositioningItem:</c> 跟踪到用户选择），
    /// 只有 Linux 立即返回（GTK 的菜单是异步的）。点击通过 <c>menu.itemclicked</c> 事件到达页面。
    /// 这一点无法在三平台上统一，只能如实写进文档。
    /// </remarks>
    private static object? Popup(IOrielWindowControl sender, JsonElement args)
    {
        // 先校验参数、再解析目标窗口：输入不合法是页面自己的错，没必要先去查一次窗口表。
        // 顺序也影响可测性——参数校验不必依赖"有一个已注册的真实窗口"。
        //
        // 项数用一个局部计数跨层级累计：上限约束的是整棵树，不是每层。
        int count = 0;
        IReadOnlyList<OrielMenuItem> items = ParseItems(RequiredArray(args, "items"), depth: 1, ref count);

        // 位置也要先校验（它可能格式不对），但**换算**要等拿到目标窗口才知道缩放比。
        // 拆成"校验 + 归一"和"换算"两步，是为了让非法位置在查窗口表之前就报错。
        RequestedPosition? position = ParsePosition(args);
        if (items.Count == 0)
        {
            return null;
        }

        IOrielWindowControl target = OrielCommandTarget.ResolveControl(sender, args);
        target.ShowContextMenu(items, position?.ToLogical(target), AutoDismissDelay);
        return null;
    }

    /// <summary>
    /// 无交互环境下的自动关闭延时（从环境变量读，默认关闭）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么不走页面 API</b>：这是给自检与自动化测试用的，不是应用功能。
    /// 加进 <c>menu.popup</c> 的参数会让它成为公开契约的一部分，而它解决的是一个
    /// 纯测试问题（CI 上没有人点菜单，Windows 的阻塞弹出会一直挂着）。
    /// </para>
    /// <para>
    /// 环境变量名带 <c>ORIELWEB_</c> 前缀，与其他内部开关同族。
    /// 值是毫秒；解析失败或非正数一律当作"不自动关闭"——配置写错不该让菜单行为变怪。
    /// </para>
    /// </remarks>
    private static TimeSpan? AutoDismissDelay
    {
        get
        {
            string? raw = Environment.GetEnvironmentVariable("ORIELWEB_MENU_AUTODISMISS_MS");
            return int.TryParse(raw, out int ms) && ms > 0 ? TimeSpan.FromMilliseconds(ms) : null;
        }
    }

    /// <summary>
    /// 页面请求的弹出位置：数值 + 单位，尚未换算。
    /// </summary>
    /// <remarks>
    /// 用一个小类型而不是两个 out 参数或一个静态字段：静态字段会在并发命令下串值
    /// （两个窗口同时弹菜单，其中一个读到另一个的单位），而这条通道本来就是多窗口共用的。
    /// </remarks>
    private readonly record struct RequestedPosition(double X, double Y, bool IsPhysical)
    {
        /// <summary>按目标窗口的缩放比换成逻辑像素。</summary>
        /// <param name="window">目标窗口（读缩放比）。</param>
        /// <returns>逻辑像素位置。</returns>
        /// <remarks>
        /// 缩放比取不到（窗口未就绪，返回 0 或负数）时按 1:1 处理：
        /// 菜单位置差一点，好过菜单弹不出来。
        /// </remarks>
        internal OrielPosition ToLogical(IOrielWindowControl window)
        {
            if (!IsPhysical)
            {
                return new OrielPosition(X, Y);
            }

            double scale = window.ScaleFactor;
            return scale > 0
                ? new OrielPosition(X / scale, Y / scale)
                : new OrielPosition(X, Y);
        }
    }

    /// <summary>解析可选的弹出位置（只做校验与归一，不换算）。</summary>
    private static RequestedPosition? ParsePosition(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object ||
            !args.TryGetProperty("position", out JsonElement position) ||
            position.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (position.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("position", "必须是 { x, y } 对象或 null");
        }

        double x = Number(position, "x");
        double y = Number(position, "y");

        string type = position.TryGetProperty("type", out JsonElement typeElement) &&
                      typeElement.ValueKind == JsonValueKind.String
            ? typeElement.GetString() ?? "Logical"
            : "Logical";

        return new RequestedPosition(x, y, string.Equals(type, "Physical", StringComparison.Ordinal));
    }

    /// <summary>解析菜单树。</summary>
    private static IReadOnlyList<OrielMenuItem> ParseItems(JsonElement array, int depth, ref int count)
    {
        if (depth > MaxDepth)
        {
            throw Invalid("items", $"菜单嵌套超过 {MaxDepth} 层");
        }

        var items = new List<OrielMenuItem>(array.GetArrayLength());

        foreach (JsonElement element in array.EnumerateArray())
        {
            if (++count > MaxItems)
            {
                throw Invalid("items", $"菜单项总数超过 {MaxItems}");
            }

            OrielMenuItem? item = ParseItem(element, depth, ref count);
            if (item is not null)
            {
                items.Add(item);
            }
        }

        return items;
    }

    /// <summary>解析一个菜单项；返回 null 表示"这一项在桌面端没有对应物，跳过"。</summary>
    private static OrielMenuItem? ParseItem(JsonElement element, int depth, ref int count)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("items", "每个菜单项必须是对象");
        }

        string kind = Text(element, "kind") ?? "item";

        if (kind == "predefined")
        {
            string? predefined = PredefinedName(element);

            if (string.Equals(predefined, "Separator", StringComparison.Ordinal))
            {
                return OrielMenuItem.Separator();
            }

            string? role = RoleOf(predefined);
            if (role is null)
            {
                // 例如 Services（macOS 的"服务"子菜单，需要 AppKit 的服务注册表）。
                // 菜单是尽力而为的 UI：少一项比整棵菜单报错好，但要留一条痕迹。
                OrielDiagnostics.Report($"[OrielWeb] 预定义菜单项「{predefined}」在本库没有对应实现，已跳过。");
                return null;
            }

            return new OrielMenuItem
            {
                Role = role,
                Label = Text(element, "text"),
                Enabled = Bool(element, "enabled") ?? true,
            };
        }

        var item = new OrielMenuItem
        {
            Id = Text(element, "id"),
            Label = Text(element, "text"),
            Accelerator = Text(element, "accelerator"),
            Enabled = Bool(element, "enabled") ?? true,
            Checked = string.Equals(kind, "check", StringComparison.Ordinal) && Bool(element, "checked") is true,
        };

        if (string.Equals(kind, "submenu", StringComparison.Ordinal) &&
            element.TryGetProperty("items", out JsonElement children) &&
            children.ValueKind == JsonValueKind.Array)
        {
            item.Items = ParseItems(children, depth + 1, ref count);
        }

        return item;
    }

    /// <summary>读预定义项的名字（兼容 <c>"Quit"</c> 与 <c>{ "About": {...} }</c> 两种形态）。</summary>
    /// <remarks>
    /// Tauri 的 <c>PredefinedMenuItemOptions.item</c> 是联合类型：字符串枚举，或
    /// <c>{ About: AboutMetadata | null }</c>。后者是唯一带数据的一项，这里只取名字——
    /// 元数据（应用名/版本/作者）本库的 about role 不需要，它读的是应用自己的信息。
    /// </remarks>
    private static string? PredefinedName(JsonElement element)
    {
        if (!element.TryGetProperty("item", out JsonElement value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        if (value.ValueKind == JsonValueKind.Object &&
            value.TryGetProperty("About", out _))
        {
            return "About";
        }

        return null;
    }

    /// <summary>Tauri 的预定义项名 → 本库的 role 名。</summary>
    /// <remarks>
    /// 名字对不上的只有两处，都是刻意的：
    /// <list type="bullet">
    ///   <item><description><c>Maximize</c> → <c>zoom</c>：macOS 的绿按钮语义是 zoom 而不是
    ///     填满屏幕，本库的 role 名跟的是 macOS 的叫法。</description></item>
    ///   <item><description><c>CloseWindow</c> → <c>close</c>：本库的 close 就是关窗。</description></item>
    /// </list>
    /// 没有映射的（<c>Services</c> / <c>About</c> 元数据形态）返回 null，由调用方跳过。
    /// </remarks>
    private static string? RoleOf(string? predefined) => predefined switch
    {
        "Copy" => OrielMenuRole.Copy,
        "Cut" => OrielMenuRole.Cut,
        "Paste" => OrielMenuRole.Paste,
        "SelectAll" => OrielMenuRole.SelectAll,
        "Undo" => OrielMenuRole.Undo,
        "Redo" => OrielMenuRole.Redo,
        "Minimize" => OrielMenuRole.Minimize,
        "Maximize" => OrielMenuRole.Zoom,
        "Fullscreen" => OrielMenuRole.ToggleFullScreen,
        "Hide" => OrielMenuRole.Hide,
        "HideOthers" => OrielMenuRole.HideOthers,
        "ShowAll" => OrielMenuRole.ShowAll,
        "CloseWindow" => OrielMenuRole.Close,
        "Quit" => OrielMenuRole.Quit,
        "About" => OrielMenuRole.About,
        "BringAllToFront" => OrielMenuRole.Front,
        _ => null,
    };

    private static JsonElement RequiredArray(JsonElement args, string property)
    {
        if (args.ValueKind == JsonValueKind.Object &&
            args.TryGetProperty(property, out JsonElement value) &&
            value.ValueKind == JsonValueKind.Array)
        {
            return value;
        }

        throw Invalid(property, "必须是数组");
    }

    private static string? Text(JsonElement element, string property)
        => element.TryGetProperty(property, out JsonElement value) &&
           value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool? Bool(JsonElement element, string property)
        => element.TryGetProperty(property, out JsonElement value) &&
           value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static double Number(JsonElement element, string property)
        => element.TryGetProperty(property, out JsonElement value) &&
           value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : throw Invalid(property, "必须是数字");

    private static OrielIpcException Invalid(string property, string expectation)
        => new($"菜单参数 '{property}' 不合法：{expectation}。")
        {
            Code = OrielIpcErrorCodes.InvalidArgument,
        };
}
