using System.Text;

namespace OrielWeb;

/// <summary>系统通知的内容与标识。</summary>
public sealed class OrielNotificationOptions
{
    /// <summary>标题（多数平台会加粗显示；不可为空）。</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>正文（可省略）。</summary>
    public string? Body { get; set; }

    /// <summary>
    /// 附加图标文件路径（部分平台忽略）。<b>相对路径按可执行文件所在目录
    /// （<see cref="AppContext.BaseDirectory"/>）解析</b>，不是当前工作目录
    /// （理由见 <see cref="OrielWindowOptions.Icon"/>）。
    /// </summary>
    public string? IconPath
    {
        get => _iconPath;
        set => _iconPath = value is null ? null : OrielPathResolver.Resolve(value);
    }

    private string? _iconPath;

    /// <summary>
    /// 通知标识：作为"这条通知的身份"传给系统（Windows 上是 toast 的 <c>Tag</c>）。
    /// </summary>
    /// <remarks>
    /// 它**不再**用于点击回传——通知点击上报功能已整体移除（三个平台都拿不到点击，
    /// 见 <see cref="OrielApp"/> 里同处的说明）。
    /// </remarks>
    public string? Id { get; set; }
}

/// <summary>
/// 通知重复的周期单位（对标 Tauri 的 <c>ScheduleEvery</c>）。
/// </summary>
/// <remarks>
/// 取值与 Tauri 的 TS 枚举**逐字一致**（<c>'year'</c> / <c>'twoWeeks'</c> …），
/// 所以页面侧的字符串可以直接穿过 IPC 到这里，不需要一张翻译表。
/// </remarks>
public static class OrielScheduleEvery
{
    /// <summary>每 year 一次（按 365 天计）。</summary>
    public const string Year = "year";

    /// <summary>每 month 一次（按 30 天计——与 Tauri 在 Android 上的近似一致）。</summary>
    public const string Month = "month";

    /// <summary>每 twoWeeks 一次（14 天）。</summary>
    public const string TwoWeeks = "twoWeeks";

    /// <summary>每 week 一次（7 天）。</summary>
    public const string Week = "week";

    /// <summary>每 day 一次（24 小时）。</summary>
    public const string Day = "day";

    /// <summary>每 hour 一次。</summary>
    public const string Hour = "hour";

    /// <summary>每 minute 一次。</summary>
    public const string Minute = "minute";

    /// <summary>每 second 一次。</summary>
    public const string Second = "second";

    /// <summary>是不是已知的单位。</summary>
    internal static bool IsKnown(string? value)
        => value is Year or Month or TwoWeeks or Week or Day or Hour or Minute or Second;

    /// <summary>
    /// 把"单位 × count"折算成固定的时长。
    /// </summary>
    /// <remarks>
    /// 年/月按固定天数近似（365 / 30），而不是走日历——**本库的调度器在应用进程里**，
    /// 不是系统级调度器：它不是"每年 1 月 1 日响"，而是"隔 365 天响"。
    /// 要按日历字段匹配请用 <see cref="OrielNotificationSchedule.Interval"/>。
    /// </remarks>
    internal static TimeSpan Period(string value, int count) => value switch
    {
        Year => TimeSpan.FromDays(365 * count),
        Month => TimeSpan.FromDays(30 * count),
        TwoWeeks => TimeSpan.FromDays(14 * count),
        Week => TimeSpan.FromDays(7 * count),
        Day => TimeSpan.FromDays(count),
        Hour => TimeSpan.FromHours(count),
        Minute => TimeSpan.FromMinutes(count),
        Second => TimeSpan.FromSeconds(count),
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "未知的周期单位。"),
    };
}

/// <summary>
/// 按日历字段匹配的触发规则（对标 Tauri 的 <c>ScheduleInterval</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 语义是"当前时刻的每个**已设定**字段都要相等"，**更细的未设定字段按 0 处理**：
/// <c>Hour = 9</c> 是每天 09:00:00（不是 09:00–09:59 每秒一次），
/// <c>Hour = 9, Minute = 0</c> 同样是每天 09:00:00，而 <c>Second = 30</c> 是每分钟的第 30 秒
/// （时/分没设，保持"任意"）。这条规则来自 Tauri 的文档语义（"fires every day at 9:00"）——
/// 不这么定义的话，同一份配置在两边含义不同。
/// </para>
/// <para>
/// 每个字段都按**本地时间**解释（日历字段没有时区概念）。与 Tauri 的差别在实现层：
/// 它是移动端把匹配交给系统闹钟，本库在宿主进程里算下一次匹配时刻（见 <see cref="NextAfter"/>）。
/// </para>
/// </remarks>
public sealed class OrielScheduleInterval
{
    /// <summary>年（不设 = 任意年）。</summary>
    public int? Year { get; set; }

    /// <summary>月（1–12；不设 = 任意月）。</summary>
    public int? Month { get; set; }

    /// <summary>日（1–31；不设 = 任意日）。</summary>
    public int? Day { get; set; }

    /// <summary>星期（1 = 周日 … 7 = 周六，与 Tauri 一致；不设 = 任意星期）。</summary>
    public int? Weekday { get; set; }

    /// <summary>小时（0–23；不设 = 任意小时）。</summary>
    public int? Hour { get; set; }

    /// <summary>分（0–59；不设 = 任意分）。</summary>
    public int? Minute { get; set; }

    /// <summary>秒（0–59；不设 = 任意秒）。</summary>
    public int? Second { get; set; }

    /// <summary>是否有任何字段被设定。</summary>
    internal bool HasAnyField
        => Year is not null || Month is not null || Day is not null || Weekday is not null
           || Hour is not null || Minute is not null || Second is not null;

    /// <summary>字段范围校验；返回 null 表示没问题。</summary>
    internal string? Validate()
    {
        if (!HasAnyField)
        {
            return "schedule.interval.interval 至少要设定一个字段——一个字段都不设等于每秒触发一次，" +
                   "那不是调用方的意图。";
        }

        if (Year is int year && year < 1970)
        {
            return $"schedule.interval.interval.year 不合理：{year}（应 ≥ 1970）。";
        }

        if (Month is int month && month is < 1 or > 12)
        {
            return $"schedule.interval.interval.month 超出范围：{month}（应为 1–12）。";
        }

        if (Day is int day && day is < 1 or > 31)
        {
            return $"schedule.interval.interval.day 超出范围：{day}（应为 1–31）。";
        }

        if (Weekday is int weekday && weekday is < 1 or > 7)
        {
            return $"schedule.interval.interval.weekday 超出范围：{weekday}（应为 1–7，1 = 周日）。";
        }

        if (Hour is int hour && hour is < 0 or > 23)
        {
            return $"schedule.interval.interval.hour 超出范围：{hour}（应为 0–23）。";
        }

        if (Minute is int minute && minute is < 0 or > 59)
        {
            return $"schedule.interval.interval.minute 超出范围：{minute}（应为 0–59）。";
        }

        if (Second is int second && second is < 0 or > 59)
        {
            return $"schedule.interval.interval.second 超出范围：{second}（应为 0–59）。";
        }

        return null;
    }

    /// <summary>
    /// 求严格晚于 <paramref name="after"/> 的下一个匹配时刻；找不到时返回 <c>null</c>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// **逐日扫描 + 当日取最早时刻**，而不是逐秒试：逐秒在"每年一次"这种配置下要跑一年。
    /// 日这一层最多扫 8 年（覆盖闰年 2 月 29 日这种"四年才一次"的组合），
    /// 日内最多 86400 次构造（无设定字段时第一次就命中，实际远小于此）。
    /// </para>
    /// <para>
    /// 指定了 <see cref="Year"/> 时直接从那一年的 1 月 1 日开始扫（不从头扫到未来），
    /// 那一年扫完还没有就返回 <c>null</c>——比"8 年内找不到"更准确的结论。
    /// </para>
    /// </remarks>
    internal DateTimeOffset? NextAfter(DateTimeOffset after)
    {
        DateTime local = after.LocalDateTime;
        DateTime start = new(
            local.Year, local.Month, local.Day, local.Hour, local.Minute, local.Second,
            DateTimeKind.Unspecified);
        start = start.AddSeconds(1);

        DateTime cursor;
        int maxDays;
        if (Year is int year)
        {
            if (year < start.Year)
            {
                return null;
            }

            cursor = year == start.Year ? start.Date : new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
            maxDays = 366;
        }
        else
        {
            cursor = start.Date;
            maxDays = 366 * 8;
        }

        for (int offset = 0; offset <= maxDays; offset++)
        {
            DateTime date = cursor.AddDays(offset);

            if (Month is int month && date.Month != month)
            {
                continue;
            }

            if (Day is int day && date.Day != day)
            {
                continue;
            }

            if (Weekday is int weekday && (int)date.DayOfWeek + 1 != weekday)
            {
                continue;
            }

            DateTime? candidate = FirstMatchingTime(date, offset == 0 ? start : date);
            if (candidate is DateTime match)
            {
                return new DateTimeOffset(DateTime.SpecifyKind(match, DateTimeKind.Local));
            }
        }

        return null;
    }

    /// <summary>某一天里不早于 <paramref name="lowerBound"/> 的最早匹配时刻。</summary>
    /// <remarks>
    /// 更细的未设定字段按 0 处理（见类型注释里的规则）：
    /// <c>{ hour: 9 }</c> 是每天 09:00:00，而 <c>{ second: 30 }</c> 才是每分钟的第 30 秒。
    /// 不这么做的话，<c>{ hour: 9, minute: 0 }</c> 会在 09:00 那一分钟里每秒都匹配一次。
    /// </remarks>
    private DateTime? FirstMatchingTime(DateTime date, DateTime lowerBound)
    {
        int finest = FinestSpecifiedIndex();
        int? hour = Hour, minute = Minute, second = Second;

        if (finest < 4)
        {
            hour ??= 0;
            minute ??= 0;
            second ??= 0;
        }
        else if (finest == 4)
        {
            minute ??= 0;
            second ??= 0;
        }
        else if (finest == 5)
        {
            second ??= 0;
        }

        int hourFrom = hour ?? 0, hourTo = hour ?? 23;
        int minuteFrom = minute ?? 0, minuteTo = minute ?? 59;
        int secondFrom = second ?? 0, secondTo = second ?? 59;

        for (int h = hourFrom; h <= hourTo; h++)
        {
            for (int m = minuteFrom; m <= minuteTo; m++)
            {
                for (int s = secondFrom; s <= secondTo; s++)
                {
                    var candidate = new DateTime(date.Year, date.Month, date.Day, h, m, s, DateTimeKind.Unspecified);
                    if (candidate >= lowerBound)
                    {
                        return candidate;
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 最细的已设定字段的下标（<c>year</c> = 0 … <c>second</c> = 6）；一个都没设时返回 -1。
    /// </summary>
    private int FinestSpecifiedIndex()
    {
        if (Second is not null) return 6;
        if (Minute is not null) return 5;
        if (Hour is not null) return 4;
        if (Weekday is not null) return 3;
        if (Day is not null) return 2;
        if (Month is not null) return 1;
        if (Year is not null) return 0;
        return -1;
    }
}

/// <summary>
/// 一条通知的排期（对标 Tauri 的 <c>Schedule</c>）。
/// </summary>
/// <remarks>
/// <para>
/// <b>三者只能选一个</b>：<see cref="AtDate"/>（某个时刻）、<see cref="Interval"/>（按日历字段匹配）、
/// <see cref="Every"/>（固定周期）。同时给两个是配置错误，会当场报错而不是猜一个优先级。
/// </para>
/// <para>
/// <b>与 Tauri 的行为差异（如实登记）</b>：Tauri 的桌面端**忽略** <c>schedule</c>、立即显示
/// （"Scheduling is only supported on mobile"）。本库在桌面端真的排期——因为调度器在宿主进程里，
/// 这是超集而不是缺陷；代价是<b>应用退出后就不再响</b>（没有系统级调度器），要跨进程可靠请用
/// 系统的计划任务 / systemd timer / launchd，而不是本 API。这条写进了 docs/API.md 的差异表。
/// </para>
/// <para>
/// <see cref="AllowWhileIdle"/> 是 Android 的 Doze 概念，桌面上**必然为 false 的效果**（没有
/// Doze）——接受并忽略，不报错：迁移代码带着它不该在桌面上失败。
/// </para>
/// </remarks>
public sealed class OrielNotificationSchedule
{
    /// <summary>在某个时刻触发（<c>at.date</c>）。</summary>
    public DateTimeOffset? AtDate { get; set; }

    /// <summary>是否按 <see cref="AtDate"/> 与"排期时刻"的间隔重复（<c>at.repeating</c>）。</summary>
    public bool AtRepeating { get; set; }

    /// <summary>按日历字段匹配（<c>interval</c>）。</summary>
    public OrielScheduleInterval? Interval { get; set; }

    /// <summary>固定周期单位（<c>every.interval</c>），取值见 <see cref="OrielScheduleEvery"/>。</summary>
    public string? Every { get; set; }

    /// <summary>周期倍数（<c>every.count</c>）：<c>every(Hour, 2)</c> = 每两小时一次。</summary>
    public int EveryCount { get; set; } = 1;

    /// <summary>允许在低电量待机时触发（Android 专有；桌面上接受并忽略）。</summary>
    public bool AllowWhileIdle { get; set; }

    /// <summary>把"单位 × 倍数"折算成时长；不是 <see cref="Every"/> 时返回 null。</summary>
    internal TimeSpan? EveryPeriod
        => Every is null || EveryCount < 1 ? null : OrielScheduleEvery.Period(Every, EveryCount);

    /// <summary>
    /// 校验这份排期在 <paramref name="now"/> 这个时刻是否可用；返回 null 表示可用。
    /// </summary>
    /// <remarks>
    /// 校验放在"登记之前"：排期一旦登记就要占用定时器与登记项，
    /// 等到触发时才发现配置错了，症状是"通知永远不来"而不是一条清晰的错误。
    /// </remarks>
    internal string? Validate(DateTimeOffset now)
    {
        int kinds = (AtDate is null ? 0 : 1) + (Interval is null ? 0 : 1) + (Every is null ? 0 : 1);
        if (kinds != 1)
        {
            return "schedule 必须且只能给 at / interval / every 三者之一（实际给了 " +
                   kinds.ToString(System.Globalization.CultureInfo.InvariantCulture) + " 个）。";
        }

        if (AtDate is DateTimeOffset at)
        {
            if (at <= now)
            {
                return $"schedule.at.date 必须是将来的时刻（实际是 {at:O}，现在是 {now:O}）。" +
                       "想立刻显示请去掉 schedule。";
            }

            return null;
        }

        if (Every is not null)
        {
            if (!OrielScheduleEvery.IsKnown(Every))
            {
                return $"schedule.every.interval 未知：'{Every}'（应为 year / month / twoWeeks / week / " +
                       "day / hour / minute / second）。";
            }

            if (EveryCount < 1)
            {
                return $"schedule.every.count 必须是 ≥ 1 的整数（实际是 {EveryCount}）。";
            }

            return null;
        }

        string? intervalProblem = Interval!.Validate();
        if (intervalProblem is not null)
        {
            return intervalProblem;
        }

        return Interval.NextAfter(now) is null
            ? "schedule.interval.interval 这个字段组合永远不会成立（例如 2 月 30 日、或指定的年份已过去）。"
            : null;
    }
}

/// <summary>
/// 一条通知在本库登记表里的样子——<c>active()</c> / <c>pending()</c> 的返回元素。
/// </summary>
/// <remarks>
/// 这是**本库自己的记账**，不是"系统通知中心里的全部"：系统里别的应用发的、用户手动清掉的，
/// 本库都看不到（三平台都没有"列出通知中心"的 API）。语义在 docs/API.md 写明。
/// </remarks>
public sealed class OrielNotificationRecord
{
    /// <summary>本库分配的编号（单调递增、永不复用）。</summary>
    public long Id { get; init; }

    /// <summary>标题。</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>正文。</summary>
    public string? Body { get; init; }

    /// <summary>调用方给的标识（Windows 上是 toast 的 <c>Tag</c>）。</summary>
    public string? Tag { get; init; }

    /// <summary>图标路径（已按可执行文件目录解析过）。</summary>
    public string? IconPath { get; init; }

    /// <summary>排期（仅待发通知有；已投递的为 <c>null</c>）。</summary>
    public OrielNotificationSchedule? Schedule { get; init; }
}

/// <summary>
/// 系统通知的"应用标识"规范化（Windows 上即 AUMID，Linux 上是 <c>notify-send --app-name</c>）。
/// </summary>
/// <remarks>
/// 抽成纯函数是为了能单测：这段规范化的输出会直接出现在系统的"通知"设置里，
/// 写错了不会报错，只会让用户在设置里看到一个奇怪的名字（甚至几条通知归不到一起）。
/// <para>
/// 约束来自 Windows 对 AUMID 的规定：**不超过 128 个字符、不能含空格**。
/// 其余非法字符一并换成 <c>_</c>，避免把路径分隔符、引号之类带进注册表与命令行。
/// </para>
/// </remarks>
internal static class OrielNotificationAppId
{
    /// <summary>取不到任何可用名字时的兜底（也是本库在 0.1.x 上的历史值）。</summary>
    internal const string Fallback = "OrielWeb";

    /// <summary>AUMID 的长度上限（Windows 规定）。</summary>
    internal const int MaxLength = 128;

    /// <summary>
    /// 把任意字符串规范化成可用的应用标识；空白或全部非法时返回 <see cref="Fallback"/>。
    /// </summary>
    internal static string Sanitize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Fallback;
        }

        var builder = new StringBuilder(Math.Min(raw.Length, MaxLength));
        foreach (char c in raw)
        {
            if (builder.Length == MaxLength)
            {
                break;
            }

            // 只留"标识符安全"的字符：字母数字与 . - _ +（含非 ASCII 字母，程序集名可能是中文）
            builder.Append(char.IsLetterOrDigit(c) || c is '.' or '-' or '_' or '+' ? c : '_');
        }

        return builder.Length == 0 ? Fallback : builder.ToString();
    }
}
