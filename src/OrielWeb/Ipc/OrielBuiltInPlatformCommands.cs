using System.Text.Json;

namespace OrielWeb.Ipc;

/// <summary>
/// 内建平台集成命令族：<c>opener.*</c> / <c>notification.*</c> / <c>autostart.*</c>。
/// </summary>
/// <remarks>
/// <para>
/// 对标 Tauri 2 的 <c>plugin-opener</c> / <c>plugin-notification</c> / <c>plugin-autostart</c>。
/// 这三族**没有新平台代码**——宿主侧的能力早就在 <see cref="OrielApp"/> 上
/// （<c>OpenExternal</c> / <c>ShowNotification</c> / <c>EnableAutoStart</c> …），
/// 这里做的是"给它们配 Tauri 形状的命令名与参数"。
/// </para>
/// <para>
/// 三个前缀放在一个类里而不是三个类：它们的形状完全一样（要来源窗口、读参数、
/// 调 <c>App</c> 上的方法），拆成三个类只会多两份转发样板与三条分发器路由。
/// </para>
/// <para>
/// <b>门禁姿态：三条都不是"始终放行"</b>，要用必须显式声明。
/// 理由各不相同但都成立：<c>opener.openPath</c> 相当于"用默认程序打开任意文件"
/// （双击一个 .exe 就等于执行它）、<c>opener.openUrl</c> 能拉起任意协议处理器、
/// <c>autostart.*</c> 改的是系统状态。它们都不是"只操作自己那个窗口"那一类。
/// </para>
/// </remarks>
internal static class OrielBuiltInPlatformCommands
{
    /// <summary>命令名前缀们。</summary>
    internal static readonly string[] Prefixes = ["opener.", "notification.", "autostart.", "clipboard."];

    /// <summary>取某个本族命令的处理函数；不是本族命令时返回 false。</summary>
    internal static bool TryGet(string name, out Func<IOrielWindowControl, string, JsonElement, object?> handler)
    {
        switch (name)
        {
            // ---- opener（对标 plugin-opener）----

            case "opener.openUrl":
                handler = static (window, command, args) =>
                {
                    RejectOpenWith(args);

                    string url = OrielJson.GetRequiredArg<string>(args, "url", null);

                    // **必须要求原文里显式带 scheme**，不能只靠 `Uri.TryCreate(Absolute)`：
                    // 在 POSIX 上 `new Uri("/tmp/a.txt", UriKind.Absolute)` 是**成功**的
                    // （它被当成隐式的 `file:` URI），于是同一条断言在 Windows 上拒绝、
                    // 在 Linux 上放行——而放行之后又会被 OpenExternal 的白名单挡掉，
                    // 页面拿到的是 `false` 而不是 invalid_argument。
                    // 判据因此加半句："原文以 '<scheme>:' 开头"。
                    bool hasExplicitScheme =
                        Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed)
                        && parsed.Scheme.Length > 0
                        && url.StartsWith(parsed.Scheme + ":", StringComparison.OrdinalIgnoreCase);

                    if (!hasExplicitScheme)
                    {
                        throw new OrielIpcException(
                            $"openUrl 的 url 必须是绝对 URL（含协议，如 https://…），实际是「{url}」。" +
                            "裸路径请用 opener.openPath。")
                        {
                            Code = OrielIpcErrorCodes.InvalidArgument,
                        };
                    }

                    return window.App.OpenExternal(url);
                };
                return true;

            case "opener.openPath":
                handler = static (window, command, args) =>
                {
                    RejectOpenWith(args);

                    string path = OrielJson.GetRequiredArg<string>(args, "path", null);
                    return window.App.OpenWithDefaultApp(path);
                };
                return true;

            case "opener.revealItemInDir":
                handler = static (window, command, args) => RevealInDir(window, args);
                return true;

            // ---- notification（对标 plugin-notification 的桌面子集）----

            case "notification.isPermissionGranted":
                // 桌面上没有"通知权限"这个概念：能显示就能显示。移动端的那个概念
                // 在这里没有对应物，所以如实回"宿主是否支持通知"。
                handler = static (window, _, _) => window.App.NotificationsSupported;
                return true;

            case "notification.requestPermission":
                // 同上：桌面端没有可弹的权限对话框，所以不弹、直接给结果。
                // 返回字符串而不是布尔，是为了与 Tauri（以及 Web Notification 规范）一致。
                handler = static (window, _, _) => window.App.NotificationsSupported ? "granted" : "denied";
                return true;

            case "notification.sendNotification":
                handler = static (window, command, args) => SendNotification(window, args);
                return true;

            // ---- autostart（对标 plugin-autostart）----

            case "autostart.isEnabled":
                handler = static (window, _, _) => Running(window) && window.App.IsAutoStartEnabled;
                return true;

            case "autostart.enable":
                handler = static (window, _, _) => Running(window) && window.App.EnableAutoStart();
                return true;

            case "autostart.disable":
                handler = static (window, _, _) => Running(window) && window.App.DisableAutoStart();
                return true;

            // ---- clipboard（对标 plugin-clipboard-manager 的纯文本部分）----

            case "clipboard.readText":
                // 没后端时返回 null（"没有文本"）而不是抛：对调用方来说，
                // 那与"剪贴板里没有文本"是同一个结论
                handler = static (window, _, _) => window.App.ReadClipboardText();
                return true;

            case "clipboard.writeText":
                handler = static (window, command, args) =>
                {
                    RejectClipboardLabel(args);

                    string text = OrielJson.GetRequiredArg<string>(args, "text", null);
                    return window.App.WriteClipboardText(text);
                };
                return true;

            case "clipboard.clear":
                handler = static (window, _, _) => window.App.ClearClipboard();
                return true;

            default:
                handler = null!;
                return false;
        }
    }

    /// <summary>
    /// 拒绝 Tauri 的 <c>label</c> 参数（"操作哪一个剪贴板"）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 那个参数在 X11 上有意义（<c>CLIPBOARD</c> / <c>PRIMARY</c> / <c>SECONDARY</c> 是三个
    /// 独立的剪贴板），而 Windows 与 macOS 各只有一个。本库目前只操作默认那个
    /// （X11 的 <c>CLIPBOARD</c>，也就是 Ctrl+C/V 用的那个）。
    /// </para>
    /// <para>
    /// <b>明确拒绝而不是静默忽略</b>：忽略会让调用方以为"我写进的是 PRIMARY"
    /// （X11 上中键粘贴用的那个），实际写进了 CLIPBOARD——一次静默的目标错位。
    /// 要支持 PRIMARY 应当连同"三平台语义不一致"这件事一起设计
    /// （Windows/macOS 上它根本不存在），不该靠一个被忽略的参数蒙混过去。
    /// </para>
    /// </remarks>
    private static void RejectClipboardLabel(JsonElement args)
    {
        if (args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty("label", out JsonElement label)
            && label.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            throw new OrielIpcException(
                "本库不支持 clipboard 的 label（指定哪个剪贴板）：目前只操作默认剪贴板" +
                "（X11 的 CLIPBOARD），而 Windows/macOS 上也没有第二个剪贴板。请省略该参数。")
            {
                Code = OrielIpcErrorCodes.NotSupported,
            };
        }
    }

    /// <summary>
    /// 拒绝 Tauri 的 <c>with</c> 参数（"用指定程序打开"）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么不静默忽略</b>：那个参数是"指定一个可执行文件去打开这个东西"，
    /// 也就是**任意程序执行**。忽略它会让调用方以为"我指定了用某个程序打开"，
    /// 而实际是按系统默认打开——一次静默的行为降级，而调用方不会发现。
    /// </para>
    /// <para>
    /// 本库的宿主侧只有"用默认程序打开"（<c>OpenWithDefaultApp</c>），
    /// 没有实现"指定程序"——因为那需要额外的授权模型（允许哪些程序），
    /// 而目前没有真实需求。要加的话应当连同那个模型一起加。
    /// </para>
    /// </remarks>
    /// <summary>宿主还没跑起来时返回 false，而不是让它抛一个未分类的内部异常。</summary>
    private static bool Running(IOrielWindowControl window) => window.App.IsRunning;

    private static void RejectOpenWith(JsonElement args)
    {
        if (args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty("with", out JsonElement with)
            && with.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            throw new OrielIpcException(
                "本库不支持 openWith（用指定程序打开）：那等于任意程序执行，需要一套" +
                "\"允许哪些程序\"的授权模型，目前没有实现。请省略该参数。")
            {
                Code = OrielIpcErrorCodes.NotSupported,
            };
        }
    }

    /// <summary>在文件管理器里定位若干文件（对标 <c>revealItemInDir</c>）。</summary>
    /// <remarks>
    /// Tauri 收 <c>paths</c>（数组）。本库宿主的 <c>RevealInFileManager</c> 一次一个，
    /// 所以循环调用——**全部成功才算成功**：若其中一个失败就返回 false，
    /// 否则调用方会以为"三个都定位到了"。
    /// </remarks>
    private static bool RevealInDir(IOrielWindowControl window, JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("paths", out JsonElement paths))
        {
            throw new OrielIpcException("命令缺少必需参数 'paths'。")
            {
                Code = OrielIpcErrorCodes.MissingArgument,
            };
        }

        if (paths.ValueKind != JsonValueKind.Array)
        {
            throw new OrielIpcException($"命令参数 'paths' 的类型不符：期望 Array，实际 {paths.ValueKind}。")
            {
                Code = OrielIpcErrorCodes.InvalidArgument,
            };
        }

        bool allOk = true;
        int count = 0;

        foreach (JsonElement item in paths.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw new OrielIpcException($"命令参数 'paths' 的元素必须是字符串，实际 {item.ValueKind}。")
                {
                    Code = OrielIpcErrorCodes.InvalidArgument,
                };
            }

            count++;

            // 用 `&=` 而不是提前返回：把每一条都试一遍，最后看是否全成功。
            // 提前返回会让"第一个失败后面就没试"变成一个不可预期的行为。
            allOk &= window.App.RevealInFileManager(item.GetString()!);
        }

        if (count == 0)
        {
            throw new OrielIpcException("命令参数 'paths' 不能为空数组。")
            {
                Code = OrielIpcErrorCodes.InvalidArgument,
            };
        }

        return allOk;
    }

    /// <summary>
    /// 发一条系统通知（对标 <c>sendNotification</c>）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 参数形状照 Tauri：字符串，或 <c>{ title, body }</c>。
    /// </para>
    /// <para>
    /// <b>与 Tauri 的差别：这个是异步的。</b> Tauri 的 <c>sendNotification</c> 在桌面端
    /// 直接 <c>new window.Notification(...)</c>（走浏览器的通知 API），所以是同步的；
    /// 而本库走宿主命令，因为 WebView 的浏览器通知 API 三平台并不可靠
    /// （WKWebView 上基本没有），而 <c>OrielApp.ShowNotification</c> 三平台都是原生的。
    /// 后果：忽略返回值的调用方不受影响，但<b>不能</b>依赖"调用完通知就已经发出去了"。
    /// </para>
    /// <para>
    /// 被通知系统拒绝（macOS 上用户关了通知）时返回 <c>false</c> 而不是抛错：
    /// 那是一个正常状态，不是调用方的错误。
    /// </para>
    /// </remarks>
    private static bool SendNotification(IOrielWindowControl window, JsonElement args)
    {
        // Tauri 允许 sendNotification('纯字符串')，这里在命令层只收对象——
        // 页面侧的包装负责把字符串转成 { title }，因为命令的参数字段必须稳定。
        string title = OrielJson.GetRequiredArg<string>(args, "title", null);

        string? body = args.TryGetProperty("body", out JsonElement bodyElement)
                       && bodyElement.ValueKind == JsonValueKind.String
            ? bodyElement.GetString()
            : null;

        return Running(window) && window.App.ShowNotification(title, body);
    }
}
