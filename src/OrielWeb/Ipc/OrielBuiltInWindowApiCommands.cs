using System.Text.Json;

namespace OrielWeb.Ipc;

/// <summary>
/// 内建窗口 API 命令族（<c>window.*</c>）：对标 Tauri 2 的 <c>@tauri-apps/api/window</c>。
/// </summary>
/// <remarks>
/// <para>
/// 与 <c>win.*</c> 的分工：<c>win.*</c> 是早期的库内建命令（只作用于发起者、整族始终放行），
/// <c>window.*</c> 是为对齐 Tauri 2 新建的族——**可以按 label 寻址**，且**只有标题栏必需的那组
/// 才放行**（见 <see cref="OrielCapabilityRules.IsAlwaysAllowedWindowCommand"/>）。
/// </para>
/// <para>
/// <b>为什么会有两族并存</b>：<c>win.*</c> 是既有的公开契约（demo 与文档都在用），
/// 直接改它的语义是破坏性变更；而新族要能寻址就得带 <c>label</c> 参数，那是另一个形状。
/// 两族并存、旧的冻结、新的扩展，是唯一不破坏既有应用的路径。
/// </para>
/// <para>
/// <b>实现方式</b>：命令经 <c>label</c> 解析出目标 <see cref="WebviewWindow"/>（缺省 = 发起者），
/// 然后调它的公开方法。所以这里不需要把 <see cref="WebviewWindow"/> 的六十多个成员
/// 都搬到 <see cref="IOrielWindowControl"/> 上——那个窄接口只是给 <c>win.*</c> 用的。
/// </para>
/// </remarks>
internal static class OrielBuiltInWindowApiCommands
{
    /// <summary>命令名前缀。注意它**不**在 <see cref="OrielCapabilityRules.IsBuiltInCommand"/> 的放行名单里。</summary>
    internal const string Prefix = "window.";

    /// <summary>取某个 <c>window.*</c> 命令的处理函数；不是本族命令时返回 false。</summary>
    internal static bool TryGet(string name, out Func<IOrielWindowControl, JsonElement, object?> handler)
    {
        switch (name)
        {
            // ---- 查询 ----
            case "window.label":
                handler = static (sender, args) => Resolve(sender, args).Label;
                return true;

            case "window.isVisible":
                handler = static (sender, args) => Resolve(sender, args).IsVisible;
                return true;

            case "window.isMaximized":
                handler = static (sender, args) => Resolve(sender, args).IsMaximized;
                return true;

            // ---- 只读状态查询（对标 Tauri 的 Window.isMinimized / isFocused / …）----
            //
            // 这四条与 isVisible / isMaximized 一样**进了标题栏白名单**：
            // 自绘标题栏要用它们渲染图标与激活态（最小化/最大化/全屏按钮的当前状态，
            // 以及窗口是否激活时的标题栏配色），而它们只报告自己窗口的状态、
            // 不泄露跨窗口信息。逐条理由见 OrielCapabilityRules.AlwaysAllowedWindowCommands。
            case "window.isMinimized":
                handler = static (sender, args) => Resolve(sender, args).IsMinimized;
                return true;

            case "window.isFocused":
                handler = static (sender, args) => Resolve(sender, args).IsFocused;
                return true;

            case "window.isFullscreen":
                handler = static (sender, args) => Resolve(sender, args).IsFullscreen;
                return true;

            case "window.isAlwaysOnTop":
                handler = static (sender, args) => Resolve(sender, args).IsAlwaysOnTop;
                return true;

            case "window.isDecorated":
                handler = static (sender, args) => Resolve(sender, args).IsDecorated;
                return true;

            case "window.isResizable":
                handler = static (sender, args) => Resolve(sender, args).IsResizable;
                return true;

            case "window.isMaximizable":
                handler = static (sender, args) => Resolve(sender, args).IsMaximizable;
                return true;

            case "window.isMinimizable":
                handler = static (sender, args) => Resolve(sender, args).IsMinimizable;
                return true;

            case "window.isClosable":
                handler = static (sender, args) => Resolve(sender, args).IsClosable;
                return true;

            case "window.isEnabled":
                handler = static (sender, args) => Resolve(sender, args).IsEnabled;
                return true;

            case "window.setEnabled":
                handler = static (sender, args) =>
                {
                    Resolve(sender, args).SetEnabled(OrielJson.GetRequiredArg<bool>(args, "value", null));
                    return null;
                };
                return true;

            // ---- 三个标题栏按钮的可用性（M5-6 第三组）----
            //
            // closable 的落点与其他两个不同：Windows 上要灰化系统菜单项（见
            // Win32WindowHost.SetClosable），macOS 切 styleMask 位，Linux 是空操作。
            case "window.setClosable":
                handler = static (sender, args) =>
                {
                    Resolve(sender, args).SetClosable(OrielJson.GetRequiredArg<bool>(args, "value", null));
                    return null;
                };
                return true;

            case "window.setMaximizable":
                handler = static (sender, args) =>
                {
                    Resolve(sender, args).SetMaximizable(OrielJson.GetRequiredArg<bool>(args, "value", null));
                    return null;
                };
                return true;

            case "window.setMinimizable":
                handler = static (sender, args) =>
                {
                    Resolve(sender, args).SetMinimizable(OrielJson.GetRequiredArg<bool>(args, "value", null));
                    return null;
                };
                return true;

            // 图标：收路径字符串。Tauri 的 setIcon 还收字节数组与 Image handle，
            // 这两种本库暂不支持（见 docs 的记录），所以这里只解析 path 一种参数。
            case "window.setIcon":
                handler = static (sender, args) =>
                {
                    Resolve(sender, args).SetIcon(OrielJson.GetRequiredArg<string>(args, "path", null));
                    return null;
                };
                return true;

            // 底色：页面侧已经把 hex / 数组 / 对象统一解析成 ARGB 整数，这里只收整数。
            // 解析放在页面侧而不是这里，是因为 Tauri 的 Color 有四种形状，
            // 而那套归一化逻辑在 JS 里能写单测、在宿主这边只能靠真机试。
            case "window.setBackgroundColor":
                handler = static (sender, args) =>
                {
                    Resolve(sender, args).SetBackgroundColor(
                        OrielJson.GetRequiredArg<uint>(args, "argb", null));
                    return null;
                };
                return true;

            // ---- 尺寸约束（M5-6）----
            //
            // 只给 setMaxSize；min 早就有 window.setMinSize 了。页面侧的
            // setSizeConstraints 会按需分派到这两条——那样宿主表面不增加"组合命令"，
            // 而组合语义只在页面上存在。
            case "window.setMaxSize":
                handler = static (sender, args) =>
                {
                    JsonElement value = OrielJson.RequireArgElement(args, "value");
                    Resolve(sender, args).SetMaxSize(ReadOptionalInt(value, "width"), ReadOptionalInt(value, "height"));
                    return null;
                };
                return true;

            // ---- 请求用户注意（M5-6）----
            //
            // type 为 null / 缺省 → 取消已发出的请求（与 Tauri 一致）。
            case "window.requestUserAttention":
                handler = static (sender, args) =>
                {
                    string? type = OrielJson.TryGetArgElement(args, "type", out JsonElement typeElement)
                        && typeElement.ValueKind == JsonValueKind.String
                            ? typeElement.GetString()
                            : null;

                    Resolve(sender, args).RequestUserAttention(type switch
                    {
                        "critical" => OrielUserAttention.Critical,
                        "informational" => OrielUserAttention.Informational,
                        null => null,
                        _ => throw new OrielIpcException(
                            $"window.requestUserAttention: 未知的 type「{type}」" +
                            "（只认 critical / informational；不传表示取消）。")
                        {
                            Code = OrielIpcErrorCodes.InvalidArgument,
                        },
                    });

                    return null;
                };
                return true;

            case "window.theme":
                // 对标 Tauri 的 Window.theme()：返回 'light' / 'dark' / null。
                //
                // 本库目前**只有"跟随系统"这一种来源**（OrielApp.Theme），没有"按窗口覆写主题"
                // 的能力，所以永远给得出值、不会返回 null。Tauri 的 null 表示"无法确定"，
                // 而"系统主题是什么"我们总是知道（三平台都能读）。
                handler = static (sender, _) => sender.App.Theme == OrielTheme.Dark ? "dark" : "light";
                return true;

            case "window.scaleFactor":
                // 页面用它把 PhysicalSize 换算成逻辑尺寸（Tauri 的 setSize 两种都收）。
                // 取窗口所在显示器的值——窗口拖到另一块不同缩放的显示器后它会变。
                handler = static (sender, args) => Resolve(sender, args).ScaleFactor;
                return true;

            case "window.title":
                // 对标 Tauri 的 Window.title()。只读查询自己的窗口标题。
                handler = static (sender, args) => Resolve(sender, args).Title;
                return true;

            // ---- 几何查询（对标 Tauri 的 innerSize / outerSize / innerPosition / outerPosition）----
            //
            // 这四条**刻意不在**标题栏白名单里：自绘标题栏不需要它们，而它们还能按 label
            // 读到**别的**窗口的几何。所以要用必须显式声明——与 isVisible / title 那几条不同。
            //
            // 返回形状与 dpi 模块的 LogicalSize / LogicalPosition 同形（那是 Tauri 侧的形状），
            // 页面拿到即可直接喂给 setSize / setPosition。
            case "window.innerSize":
                handler = static (sender, args) => SizeOf(Resolve(sender, args).InnerSize);
                return true;

            case "window.outerSize":
                handler = static (sender, args) => SizeOf(Resolve(sender, args).OuterSize);
                return true;

            case "window.innerPosition":
                handler = static (sender, args) => PositionOf(Resolve(sender, args).InnerPosition);
                return true;

            case "window.outerPosition":
                handler = static (sender, args) => PositionOf(Resolve(sender, args).OuterPosition);
                return true;

            case "window.getAll":
                // 返回 label 列表而不是窗口对象：内建命令的返回值要能被 AOT 安全的基元序列化
                // 直接写出，而自定义 DTO 会要求应用注册 STJ 上下文——内建命令不该要求那个。
                handler = static (sender, _) => GetAllLabels(sender);
                return true;

            // ---- 显隐与状态 ----
            case "window.show":
                handler = static (sender, args) => Mutate(Resolve(sender, args).Show);
                return true;

            case "window.hide":
                handler = static (sender, args) => Mutate(Resolve(sender, args).Hide);
                return true;

            case "window.focus":
                handler = static (sender, args) => Mutate(Resolve(sender, args).Focus);
                return true;

            case "window.close":
                handler = static (sender, args) => Mutate(Resolve(sender, args).Close);
                return true;

            case "window.destroy":
                handler = static (sender, args) => Mutate(Resolve(sender, args).Destroy);
                return true;

            case "window.minimize":
                handler = static (sender, args) => Mutate(Resolve(sender, args).Minimize);
                return true;

            case "window.maximize":
                handler = static (sender, args) => Mutate(Resolve(sender, args).Maximize);
                return true;

            case "window.restore":
                handler = static (sender, args) => Mutate(Resolve(sender, args).Restore);
                return true;

            case "window.toggleMaximize":
                handler = static (sender, args) => Resolve(sender, args).ToggleMaximize();
                return true;

            case "window.toggleFullscreen":
                handler = static (sender, args) => Resolve(sender, args).ToggleFullscreen();
                return true;

            case "window.toggleOnTop":
                handler = static (sender, args) => Resolve(sender, args).ToggleOnTop();
                return true;

            case "window.setFullscreen":
                handler = static (sender, args) =>
                {
                    const string Command = "window.setFullscreen";
                    WebviewWindow target = Resolve(sender, args);
                    target.SetFullscreen(RequiredBool(args, "value", Command));
                    return null;
                };
                return true;

            case "window.setOnTop":
                handler = static (sender, args) =>
                {
                    const string Command = "window.setOnTop";
                    WebviewWindow target = Resolve(sender, args);
                    target.SetOnTop(RequiredBool(args, "value", Command));
                    return null;
                };
                return true;

            // ---- 外观 ----
            case "window.setTitle":
                handler = static (sender, args) =>
                {
                    const string Command = "window.setTitle";
                    WebviewWindow target = Resolve(sender, args);
                    string title = RequiredText(args, "title", Command);
                    target.SetTitle(title);
                    return null;
                };
                return true;

            case "window.setResizable":
                handler = static (sender, args) =>
                {
                    const string Command = "window.setResizable";
                    WebviewWindow target = Resolve(sender, args);
                    target.SetResizable(RequiredBool(args, "value", Command));
                    return null;
                };
                return true;

            // ---- 几何 ----
            case "window.setMinSize":
                handler = static (sender, args) =>
                {
                    const string Command = "window.setMinSize";
                    WebviewWindow target = Resolve(sender, args);
                    target.SetMinSize(
                        RequiredInt(args, "width", Command),
                        RequiredInt(args, "height", Command));
                    return null;
                };
                return true;

            case "window.moveTo":
                handler = static (sender, args) =>
                {
                    const string Command = "window.moveTo";
                    WebviewWindow target = Resolve(sender, args);
                    target.MoveTo(RequiredInt(args, "x", Command), RequiredInt(args, "y", Command));
                    return null;
                };
                return true;

            case "window.resize":
                handler = static (sender, args) =>
                {
                    const string Command = "window.resize";
                    WebviewWindow target = Resolve(sender, args);
                    // 语义是**逻辑像素的客户区**（与 WebviewWindow.Resize 一致，见 API.md 的尺寸说明）。
                    target.Resize(RequiredInt(args, "width", Command), RequiredInt(args, "height", Command));
                    return null;
                };
                return true;

            case "window.center":
                handler = static (sender, args) => Mutate(Resolve(sender, args).Center);
                return true;

            // ---- 拖动（与 win.* 同源，供迁移后用新族名）----
            case "window.drag":
                handler = static (sender, args) => Mutate(Resolve(sender, args).BeginDrag);
                return true;

            case "window.dragStart":
                handler = static (sender, args) =>
                {
                    const string Command = "window.dragStart";
                    WebviewWindow target = Resolve(sender, args);
                    target.BeginDragStreaming(
                        RequiredDouble(args, "px", Command),
                        RequiredDouble(args, "py", Command),
                        RequiredDouble(args, "wx", Command),
                        RequiredDouble(args, "wy", Command),
                        RequiredDouble(args, "ww", Command),
                        RequiredDouble(args, "wh", Command),
                        RequiredDouble(args, "sh", Command));
                    return null;
                };
                return true;

            case "window.dragTo":
                handler = static (sender, args) =>
                {
                    const string Command = "window.dragTo";
                    WebviewWindow target = Resolve(sender, args);
                    target.DragTo(RequiredDouble(args, "dx", Command), RequiredDouble(args, "dy", Command));
                    return null;
                };
                return true;

            case "window.dragEnd":
                handler = static (sender, args) => Mutate(Resolve(sender, args).EndDrag);
                return true;

            // ---- 关闭接管 ----
            case "window.setCloseIntercept":
                handler = static (sender, args) =>
                {
                    WebviewWindow target = Resolve(sender, args);
                    // 缺省按"打开"算：调用它的页面几乎总是为了"我要接管"，
                    // 而漏写字段时打开接管是可排查的（窗口不关 → 去查页面），
                    // 静默关掉接管则是"确认框没弹就直接关了"。
                    bool enabled = OptionalBool(args, "enabled") ?? true;
                    target.CloseInterceptEnabled = enabled;
                    return enabled;
                };
                return true;

            // ---- 与 win.* 同源的两个交互入口 ----
            case "window.pickFile":
                handler = static (sender, args) =>
                {
                    WebviewWindow target = Resolve(sender, args);
                    return target.ShowOpenFileDialog(
                        OptionalText(args, "title"), OptionalText(args, "filter"), null);
                };
                return true;

            case "window.contextMenu":
                handler = static (sender, args) =>
                {
                    WebviewWindow target = Resolve(sender, args);
                    target.ShowContextMenu(OrielStandardContextMenu.Items);
                    return null;
                };
                return true;

            // ---- 事件转发开关 ----
            case "window.setEventForwarding":
                handler = static (sender, args) => SetEventForwarding(sender, args);
                return true;

            default:
                handler = null!;
                return false;
        }
    }

    /// <summary>
    /// 尺寸 → 页面形状 <c>{ type, width, height }</c>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 带 <c>type: 'Logical'</c> 是为了让页面拿到的东西**直接就是** <c>LogicalSize</c> 的形状——
    /// 可以原样喂给 <c>setSize()</c>，做"读出来再写回去"这类运算时不必手工补 <c>type</c>。
    /// 与 Tauri 的差别是我们给逻辑像素（那边给物理），理由见 <see cref="WebviewWindow.InnerSize"/>。
    /// </para>
    /// <para>
    /// <b>返回 <see cref="JsonElement"/> 而不是匿名对象</b>：内建命令的返回值走
    /// <see cref="OrielJson.WriteResult"/>，而它只认基元与 <see cref="JsonElement"/> ——
    /// 匿名对象是普通类型，走 STJ 需要类型信息，于是要求应用注册它的 STJ 上下文。
    /// 那对内建命令是很不合理的负担（应用凭什么知道库内部有个匿名类型？），
    /// 漏注册的症状还是运行期报错。手拼 JSON 再解析成 <see cref="JsonElement"/> 是这条路上
    /// 唯一不需要应用配合的做法（代价是每次调用一次解析，而这些是低频查询）。
    /// </para>
    /// </remarks>
    private static JsonElement SizeOf(OrielSize size)
        => JsonDocument.Parse(
            $"{{\"type\":\"Logical\",\"width\":{OrielEventPayload.Number(size.Width)}," +
            $"\"height\":{OrielEventPayload.Number(size.Height)}}}").RootElement.Clone();

    /// <summary>位置 → 页面形状 <c>{ type, x, y }</c>；理由同 <see cref="SizeOf"/>。</summary>
    private static JsonElement PositionOf(OrielPosition position)
        => JsonDocument.Parse(
            $"{{\"type\":\"Logical\",\"x\":{OrielEventPayload.Number(position.X)}," +
            $"\"y\":{OrielEventPayload.Number(position.Y)}}}").RootElement.Clone();

    /// <summary>
    /// 解析命令的目标窗口：<c>label</c> 缺省时是**发起者**。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 两种情况都走 <c>App.GetWindow(label)</c> 查表，**包括缺省时的发起者自己**。
    /// 这不只是图省事——查表顺带保证了目标是个**已注册的存活窗口**：
    /// 窗口在关闭流程中（已从表里摘掉但对象还在）时，命令会明确报错而不是作用到半个
    /// 已销毁的窗口上。这类"作用到死对象"的故障在原生层表现为崩溃或无响应，很难归因。
    /// </para>
    /// <para>
    /// 找不到目标时抛 <see cref="OrielIpcException"/>（而不是静默作用到发起者）：
    /// 那会让"发错窗口"变成一个看得见的现象——页面以为在操作 settings，
    /// 结果把 main 关了，而没有任何报错。
    /// </para>
    /// </remarks>
    private static WebviewWindow Resolve(IOrielWindowControl sender, JsonElement args)
    {
        string? label = OptionalText(args, "label");
        OrielApp app = sender.App;

        if (label is null)
        {
            string? ownLabel = sender.Label;
            if (ownLabel is null)
            {
                throw new OrielIpcException(
                    "无法确定发起命令的窗口：窗口后端尚未就位。请在页面加载完成后再调用。")
                {
                    Code = OrielIpcErrorCodes.NoSourceWindow,
                };
            }

            label = ownLabel;
        }

        return app.GetWindow(label) ?? throw new OrielIpcException(
            $"找不到 label 为「{label}」的窗口：它可能已被关闭，或 label 拼错了。")
        {
            Code = OrielIpcErrorCodes.TargetNotFound,
        };
    }

    /// <summary>列出所有存活窗口的 label。</summary>
    private static object GetAllLabels(IOrielWindowControl sender)
    {
        var labels = new List<string>();
        foreach (WebviewWindow window in sender.App.Windows)
        {
            if (window.Label is { } label)
            {
                labels.Add(label);
            }
        }

        return labels;
    }

    /// <summary>
    /// 打开/关闭某几类宿主事件向页面的转发。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么需要这个开关</b>：尺寸与位置事件在拖动时每个像素来一次
    /// （已按 0.5 逻辑像素去重，但一次跨屏拖动仍有上千条）。而把事件推给页面
    /// 在 macOS/Linux 上是**一次 JS 求值**——上千次求值会明显拖慢拖动。
    /// 浏览器里没人订阅的事件等于白推，所以默认**全关**，由 JS 包在
    /// <c>listen</c> 到对应事件名时按需打开。
    /// </para>
    /// <para>
    /// 参数形如 <c>{ resize: true, move: true, focus: true }</c>；缺省字段保持原值不变
    /// （页面的两次 listen 各自只打开自己要的那一项，不需要知道别的项当前是什么状态）。
    /// </para>
    /// </remarks>
    private static object SetEventForwarding(IOrielWindowControl sender, JsonElement args)
    {
        // 与 win.setCloseIntercept 一样只作用于**发起者**：转发是"我这个页面的订阅行为"，
        // 按 label 改别的窗口的转发开关既没有用例，也会让"谁订阅了什么"变得难以推理。
        if (sender is not WebviewWindow window)
        {
            throw new OrielIpcException(
                "window.setEventForwarding 只能由窗口自身发起（它描述的是本页面的订阅行为）。");
        }

        OrielWindowEventKind kinds = window.EventForwarding;

        if (OptionalBool(args, "resize") is { } resize)
        {
            kinds = resize ? kinds | OrielWindowEventKind.Resize : kinds & ~OrielWindowEventKind.Resize;
        }

        if (OptionalBool(args, "move") is { } move)
        {
            kinds = move ? kinds | OrielWindowEventKind.Move : kinds & ~OrielWindowEventKind.Move;
        }

        if (OptionalBool(args, "focus") is { } focus)
        {
            kinds = focus ? kinds | OrielWindowEventKind.Focus : kinds & ~OrielWindowEventKind.Focus;
        }

        window.EventForwarding = kinds;

        // 返回当前生效的项，便于页面断言"我开的确实开了"
        return new
        {
            resize = (kinds & OrielWindowEventKind.Resize) != 0,
            move = (kinds & OrielWindowEventKind.Move) != 0,
            focus = (kinds & OrielWindowEventKind.Focus) != 0,
        };
    }

    private static object? Mutate(Action action)
    {
        action();
        return null;
    }

    private static string RequiredText(JsonElement args, string property, string commandName)
    {
        if (args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty(property, out JsonElement element)
            && element.ValueKind == JsonValueKind.String
            && element.GetString() is { } value)
        {
            return value;
        }

        throw new OrielIpcException($"命令 '{commandName}' 缺少字符串参数 '{property}'。");
    }

    private static string? OptionalText(JsonElement args, string property)
        => args.ValueKind == JsonValueKind.Object
           && args.TryGetProperty(property, out JsonElement element)
           && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static bool RequiredBool(JsonElement args, string property, string commandName)
        => OptionalBool(args, property)
           ?? throw new OrielIpcException($"命令 '{commandName}' 缺少布尔参数 '{property}'。");

    private static bool? OptionalBool(JsonElement args, string property)
        => args.ValueKind == JsonValueKind.Object
           && args.TryGetProperty(property, out JsonElement element)
           && element.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? element.GetBoolean()
            : null;

    private static int RequiredInt(JsonElement args, string property, string commandName)
        => OptionalInt(args, property)
           ?? throw new OrielIpcException($"命令 '{commandName}' 缺少整数参数 '{property}'。");

    private static int? OptionalInt(JsonElement args, string property)
        => args.ValueKind == JsonValueKind.Object
           && args.TryGetProperty(property, out JsonElement element)
           && element.ValueKind == JsonValueKind.Number
           && element.TryGetInt32(out int value)
            ? value
            : null;

    private static double RequiredDouble(JsonElement args, string property, string commandName)
        => args.ValueKind == JsonValueKind.Object
           && args.TryGetProperty(property, out JsonElement element)
           && element.ValueKind == JsonValueKind.Number
            ? element.GetDouble()
            : throw new OrielIpcException($"命令 '{commandName}' 缺少数字参数 '{property}'。");

    /// <summary>
    /// 读一个可选的整数属性；缺省或 <c>null</c> 都当 0。
    /// </summary>
    /// <remarks>
    /// 0 在本库的尺寸约束里表示**不限制**（与 Tauri 的 <c>null</c> 语义对应），
    /// 所以"没给"与"给了 0"可以合并成同一件事——页面侧传 <c>null</c> 也落到这里。
    /// 给了非数字会**明确报错**而不是当 0，那会让调用方以为限制生效了。
    /// </remarks>
    private static int ReadOptionalInt(JsonElement value, string property)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(property, out JsonElement element))
        {
            return 0;
        }

        if (element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return 0;
        }

        if (element.ValueKind != JsonValueKind.Number)
        {
            throw new OrielIpcException(
                $"window.setMaxSize 的 '{property}' 必须是数字或 null（实际是 {element.ValueKind}）。")
            {
                Code = OrielIpcErrorCodes.InvalidArgument,
            };
        }

        return element.GetInt32();
    }
}
