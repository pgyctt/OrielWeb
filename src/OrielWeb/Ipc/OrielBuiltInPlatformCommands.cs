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

            // ---- notification 的登记表（B18，对标 plugin-notification 的句柄函数）----
            //
            // 这五条都不在标题栏白名单里（与 sendNotification 同姿态）：它们能看见"本应用发过
            // 什么通知"、能取消别处排好的通知——那是应用级的状态，不是"只操作自己那个窗口"。

            case "notification.active":
                handler = static (window, _, _) => NotificationRecords(window.App.ActiveNotifications, pending: false);
                return true;

            case "notification.pending":
                handler = static (window, _, _) => NotificationRecords(window.App.PendingNotifications, pending: true);
                return true;

            case "notification.cancel":
                handler = static (window, command, args) =>
                    window.App.CancelScheduledNotifications(NotificationIds(args, command));
                return true;

            case "notification.cancelAll":
                handler = static (window, _, _) => window.App.CancelAllScheduledNotifications();
                return true;

            case "notification.removeActive":
                handler = static (window, command, args) =>
                    window.App.RemoveActiveNotifications(NotificationIds(args, command));
                return true;

            case "notification.removeAllActive":
                handler = static (window, _, _) => window.App.RemoveAllActiveNotifications();
                return true;

            case "notification.actionSupport":
                // 用户点通知（或通知上的按钮）能不能回传到页面。
                // **三平台都是 false**：未打包的 toast 激活要 AUMID + COM 激活器，macOS 的
                // osascript 与 Linux 的 notify-send 都没有回调入口（见 OrielApp 里同处的说明）。
                // 页面侧 onAction 拿这个值决定"抛 not_supported"还是"真订阅"。
                handler = static (_, _, _) => false;
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
    /// <para>
    /// <b>带 <c>schedule</c> 时是排期而不是立即投递</b>（Tauri 也是同一个入口）：
    /// 参数形状照抄它的 <c>Schedule</c> 类序列化结果。返回 <c>true</c> 表示"已排期"，
    /// 编号经 <c>pending()</c> 取。排期配置非法时抛 <c>invalid_argument</c>——
    /// 在登记之前就报错，而不是让通知永远不来。
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

        // schedule 存在时是**排期**而不是立即投递（Tauri 的 sendNotification 也是同一个入口）。
        if (args.TryGetProperty("schedule", out JsonElement scheduleElement)
            && scheduleElement.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            OrielNotificationSchedule schedule = ParseSchedule(scheduleElement, "notification.sendNotification");

            // 校验放在命令层（而不是只靠 OrielApp 的 ArgumentException）：
            // 参数的错要回 `invalid_argument`，而 ArgumentException 到了页面只剩"命令执行失败"。
            if (schedule.Validate(DateTimeOffset.Now) is string problem)
            {
                throw new OrielIpcException(problem)
                {
                    Code = OrielIpcErrorCodes.InvalidArgument,
                };
            }

            if (!Running(window))
            {
                return false;
            }

            window.App.ScheduleNotification(
                new OrielNotificationOptions { Title = title, Body = body },
                schedule);
            return true;
        }

        return Running(window) && window.App.ShowNotification(title, body);
    }

    /// <summary>
    /// 读 <c>notifications</c> 数组（数字或 <c>{ id }</c>）。
    /// </summary>
    /// <remarks>
    /// 两种元素形状都收：Tauri 的 <c>cancel</c> 收 <c>number[]</c>、<c>removeActive</c> 收
    /// <c>{ id }[]</c>，把两者搞混是迁移里最常见的错误之一——而在"编号"这件事上
    /// 宽容地接受两种写法不会让任何合法调用变歧义。
    /// </remarks>
    private static List<long> NotificationIds(JsonElement args, string command)
    {
        JsonElement element = OrielJson.RequireArgElement(args, "notifications");
        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new OrielIpcException($"{command}: notifications 必须是数组。")
            {
                Code = OrielIpcErrorCodes.InvalidArgument,
            };
        }

        var ids = new List<long>(element.GetArrayLength());
        foreach (JsonElement item in element.EnumerateArray())
        {
            JsonElement idElement = item;
            if (item.ValueKind == JsonValueKind.Object)
            {
                if (!item.TryGetProperty("id", out idElement))
                {
                    throw new OrielIpcException($"{command}: 数组元素缺少 id 字段。")
                    {
                        Code = OrielIpcErrorCodes.InvalidArgument,
                    };
                }
            }

            if (idElement.ValueKind != JsonValueKind.Number || !idElement.TryGetInt64(out long id))
            {
                throw new OrielIpcException(
                    $"{command}: 通知编号必须是整数（实际是 {idElement.ValueKind}）。" +
                    "编号来自 pending() / active() 的返回值。")
                {
                    Code = OrielIpcErrorCodes.InvalidArgument,
                };
            }

            ids.Add(id);
        }

        return ids;
    }

    /// <summary>
    /// 把登记记录写成 Tauri 形状的 JSON 数组。
    /// </summary>
    /// <remarks>
    /// 手写 <see cref="Utf8JsonWriter"/> 而不是返回 DTO：内建命令的返回值要能被 AOT 安全的
    /// 基元序列化直接写出（与 <c>tray.getAll</c> 同一条理由，见那里的说明）。
    /// <para>
    /// <c>active</c> 的 <c>groupSummary</c> / <c>data</c> / <c>extra</c> / <c>attachments</c>
    /// 在 Tauri 的 <c>ActiveNotification</c> 里是**必填字段**，而它们分别是 Android / iOS 的概念
    /// （分组摘要、平台附加数据、通知携带的载荷、附件）。桌面上没有对应物，
    /// 这里给空值而不是省略——省略会让迁移代码里的 <c>notification.data.foo</c> 变成 TypeError。
    /// </para>
    /// </remarks>
    private static JsonElement NotificationRecords(IReadOnlyList<OrielNotificationRecord> records, bool pending)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (OrielNotificationRecord record in records)
            {
                writer.WriteStartObject();
                writer.WriteNumber("id", record.Id);

                if (record.Title.Length > 0)
                {
                    writer.WriteString("title", record.Title);
                }

                if (record.Body is not null)
                {
                    writer.WriteString("body", record.Body);
                }

                if (pending)
                {
                    writer.WritePropertyName("schedule");
                    WriteSchedule(writer, record.Schedule);
                }
                else
                {
                    // Tauri 的 ActiveNotification 也有可选的 schedule（"delivered with, if it was
                    // scheduled"）——排期到点的那条要把原排期带上，否则调用方看不出它是定时来的。
                    if (record.Schedule is not null)
                    {
                        writer.WritePropertyName("schedule");
                        WriteSchedule(writer, record.Schedule);
                    }

                    if (record.Tag is not null)
                    {
                        writer.WriteString("tag", record.Tag);
                    }

                    writer.WriteBoolean("groupSummary", false);
                    writer.WriteStartObject("data");
                    writer.WriteEndObject();
                    writer.WriteStartObject("extra");
                    writer.WriteEndObject();
                    writer.WriteStartArray("attachments");
                    writer.WriteEndArray();
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        return JsonDocument.Parse(buffer.WrittenMemory).RootElement.Clone();
    }

    /// <summary>把排期写回 Tauri 的线格式（<c>{ at: … }</c> / <c>{ interval: { interval: … } }</c> / <c>{ every: … }</c>）。</summary>
    private static void WriteSchedule(Utf8JsonWriter writer, OrielNotificationSchedule? schedule)
    {
        if (schedule is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();

        if (schedule.AtDate is DateTimeOffset at)
        {
            writer.WriteStartObject("at");
            writer.WriteString("date", at);
            writer.WriteBoolean("repeating", schedule.AtRepeating);
            writer.WriteBoolean("allowWhileIdle", schedule.AllowWhileIdle);
            writer.WriteEndObject();
        }
        else if (schedule.Interval is OrielScheduleInterval interval)
        {
            writer.WriteStartObject("interval");
            writer.WriteStartObject("interval");
            WriteField(writer, "year", interval.Year);
            WriteField(writer, "month", interval.Month);
            WriteField(writer, "day", interval.Day);
            WriteField(writer, "weekday", interval.Weekday);
            WriteField(writer, "hour", interval.Hour);
            WriteField(writer, "minute", interval.Minute);
            WriteField(writer, "second", interval.Second);
            writer.WriteEndObject();
            writer.WriteBoolean("allowWhileIdle", schedule.AllowWhileIdle);
            writer.WriteEndObject();
        }
        else if (schedule.Every is string every)
        {
            writer.WriteStartObject("every");
            writer.WriteString("interval", every);
            writer.WriteNumber("count", schedule.EveryCount);
            writer.WriteBoolean("allowWhileIdle", schedule.AllowWhileIdle);
            writer.WriteEndObject();
        }

        writer.WriteEndObject();

        static void WriteField(Utf8JsonWriter writer, string name, int? value)
        {
            if (value is int number)
            {
                writer.WriteNumber(name, number);
            }
        }
    }

    /// <summary>
    /// 把页面传来的 <c>schedule</c> 解析成宿主模型。
    /// </summary>
    /// <remarks>
    /// 形状是 Tauri 的 <c>Schedule</c> 类序列化之后的样子（三个互斥字段，嵌套层次照抄）：
    /// <c>{ at: { date, repeating, allowWhileIdle } }</c>、
    /// <c>{ interval: { interval: { hour, minute, … }, allowWhileIdle } }</c>、
    /// <c>{ every: { interval: 'hour', count, allowWhileIdle } }</c>。
    /// 缺字段一律按"没设"处理（<c>null</c>），由 <see cref="OrielNotificationSchedule.Validate"/>
    /// 统一判定"三者必须恰好一个"——在这里逐条猜意图只会让错误信息散落各处。
    /// </remarks>
    private static OrielNotificationSchedule ParseSchedule(JsonElement element, string command)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new OrielIpcException($"{command}: schedule 必须是对象。")
            {
                Code = OrielIpcErrorCodes.InvalidArgument,
            };
        }

        var schedule = new OrielNotificationSchedule();

        if (element.TryGetProperty("at", out JsonElement at) && at.ValueKind == JsonValueKind.Object)
        {
            schedule.AtDate = ReadScheduleDate(at, command);
            schedule.AtRepeating = at.TryGetProperty("repeating", out JsonElement repeating)
                                   && repeating.ValueKind == JsonValueKind.True;
            schedule.AllowWhileIdle |= at.TryGetProperty("allowWhileIdle", out JsonElement idle)
                                       && idle.ValueKind == JsonValueKind.True;
        }

        if (element.TryGetProperty("interval", out JsonElement interval) && interval.ValueKind == JsonValueKind.Object)
        {
            if (interval.TryGetProperty("interval", out JsonElement fields) && fields.ValueKind == JsonValueKind.Object)
            {
                var model = new OrielScheduleInterval
                {
                    Year = ReadInt(fields, "year"),
                    Month = ReadInt(fields, "month"),
                    Day = ReadInt(fields, "day"),
                    Weekday = ReadInt(fields, "weekday"),
                    Hour = ReadInt(fields, "hour"),
                    Minute = ReadInt(fields, "minute"),
                    Second = ReadInt(fields, "second"),
                };

                // 一个字段都没给 = 没设 interval（而不是"每秒一次"）——语义判定交给 Validate
                if (model.HasAnyField)
                {
                    schedule.Interval = model;
                }
            }

            schedule.AllowWhileIdle |= interval.TryGetProperty("allowWhileIdle", out JsonElement idle)
                                       && idle.ValueKind == JsonValueKind.True;
        }

        if (element.TryGetProperty("every", out JsonElement every) && every.ValueKind == JsonValueKind.Object)
        {
            schedule.Every = every.TryGetProperty("interval", out JsonElement unit)
                             && unit.ValueKind == JsonValueKind.String
                ? unit.GetString()
                : null;

            schedule.EveryCount = ReadInt(every, "count") ?? 1;
            schedule.AllowWhileIdle |= every.TryGetProperty("allowWhileIdle", out JsonElement idle)
                                       && idle.ValueKind == JsonValueKind.True;
        }

        return schedule;
    }

    /// <summary>读 <c>at.date</c>：ISO 8601 字符串（<c>Date</c> 经 JSON.stringify 的结果）或 Unix 毫秒。</summary>
    private static DateTimeOffset? ReadScheduleDate(JsonElement at, string command)
    {
        if (!at.TryGetProperty("date", out JsonElement date))
        {
            return null;
        }

        if (date.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(
                date.GetString(),
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeLocal,
                out DateTimeOffset parsed))
        {
            return parsed;
        }

        if (date.ValueKind == JsonValueKind.Number && date.TryGetInt64(out long milliseconds))
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
        }

        throw new OrielIpcException(
            $"{command}: schedule.at.date 必须是 ISO 8601 字符串或 Unix 毫秒数（实际是 {date.ValueKind}）。")
        {
            Code = OrielIpcErrorCodes.InvalidArgument,
        };
    }

    /// <summary>读一个可选的整数字段；缺失或不是数字时返回 <c>null</c>。</summary>
    private static int? ReadInt(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetInt32(out int number)
            ? number
            : null;
}
