using Xunit;

namespace OrielWeb.Tests;

/// <summary>
/// 通知排期的**纯计算**：日历匹配（<see cref="OrielScheduleInterval.NextAfter"/>）与配置校验。
/// </summary>
/// <remarks>
/// 单独测这两块是因为它们是整批里唯一"算错了不会报错、只会晚响或永远不响"的部分：
/// 定时器与投递都有测试盯着，而"下次是哪一刻"错了，表现只是通知在错误的时间出现。
/// 时间一律用本地时间构造（<c>DateTimeKind.Local</c>），这套 API 的日历字段本来就按本地时间解释。
/// </remarks>
public sealed class NotificationScheduleTests
{
    private static DateTimeOffset Local(int year, int month, int day, int hour = 0, int minute = 0, int second = 0)
        => new(new DateTime(year, month, day, hour, minute, second, DateTimeKind.Local));

    private static DateTime Wall(DateTimeOffset value) => value.LocalDateTime;

    [Fact]
    public void Interval_FindsLaterTimeOnTheSameDay()
    {
        var interval = new OrielScheduleInterval { Hour = 9, Minute = 0 };

        DateTimeOffset? next = interval.NextAfter(Local(2026, 10, 21, 8, 0));

        Assert.NotNull(next);
        Assert.Equal(new DateTime(2026, 10, 21, 9, 0, 0), Wall(next!.Value));
    }

    [Fact]
    public void Interval_RollsToTheNextDayWhenTodaysTimeHasPassed()
    {
        var interval = new OrielScheduleInterval { Hour = 9, Minute = 0 };

        DateTimeOffset? next = interval.NextAfter(Local(2026, 10, 21, 9, 30));

        Assert.NotNull(next);
        Assert.Equal(new DateTime(2026, 10, 22, 9, 0, 0), Wall(next!.Value));
    }

    [Fact]
    public void Interval_IsStrictlyAfterTheGivenMoment()
    {
        // "正好 9:00:00" 这一刻求下一次，必须给**明天**的 9:00——否则会在同一秒里反复触发
        var interval = new OrielScheduleInterval { Hour = 9, Minute = 0 };

        DateTimeOffset? next = interval.NextAfter(Local(2026, 10, 21, 9, 0));

        Assert.NotNull(next);
        Assert.Equal(new DateTime(2026, 10, 22, 9, 0, 0), Wall(next!.Value));
    }

    [Fact]
    public void Interval_HonoursSeconds()
    {
        var interval = new OrielScheduleInterval { Second = 30 };
        var after = Local(2026, 10, 21, 10, 0, 0).AddMilliseconds(500);

        DateTimeOffset? next = interval.NextAfter(after);

        Assert.NotNull(next);
        Assert.Equal(new DateTime(2026, 10, 21, 10, 0, 30), Wall(next!.Value));
    }

    [Fact]
    public void Interval_WeekdayUsesTauriNumbering()
    {
        // 2026-10-21 是周三；Tauri 的编号 1 = 周日 … 7 = 周六，所以 2 = 周一
        var interval = new OrielScheduleInterval { Weekday = 2 };

        DateTimeOffset? next = interval.NextAfter(Local(2026, 10, 21, 10, 0));

        Assert.NotNull(next);
        Assert.Equal(new DateTime(2026, 10, 26, 0, 0, 0), Wall(next!.Value));
        Assert.Equal(DayOfWeek.Monday, next!.Value.DayOfWeek);
    }

    [Fact]
    public void Interval_FindsLeapDayAcrossYears()
    {
        var interval = new OrielScheduleInterval { Month = 2, Day = 29 };

        DateTimeOffset? next = interval.NextAfter(Local(2026, 3, 1));

        Assert.NotNull(next);
        Assert.Equal(new DateTime(2028, 2, 29, 0, 0, 0), Wall(next!.Value));
    }

    [Fact]
    public void Interval_JumpsStraightToTheRequestedYear()
    {
        var interval = new OrielScheduleInterval { Year = 2030 };

        DateTimeOffset? next = interval.NextAfter(Local(2026, 10, 21, 10, 0));

        Assert.NotNull(next);
        Assert.Equal(new DateTime(2030, 1, 1, 0, 0, 0), Wall(next!.Value));
    }

    [Theory]
    [InlineData(2, 30)]   // 永远不存在的日期
    [InlineData(4, 31)]
    public void Interval_ReturnsNullForImpossibleDates(int month, int day)
    {
        var interval = new OrielScheduleInterval { Month = month, Day = day };

        Assert.Null(interval.NextAfter(Local(2026, 1, 1)));
    }

    [Fact]
    public void Interval_ReturnsNullWhenTheYearIsInThePast()
    {
        var interval = new OrielScheduleInterval { Year = 2020 };

        Assert.Null(interval.NextAfter(Local(2026, 10, 21)));
    }

    // ---- 校验 ----

    [Fact]
    public void Validate_RejectsScheduleWithNoTrigger()
    {
        var schedule = new OrielNotificationSchedule();

        Assert.Contains("三者之一", schedule.Validate(Local(2026, 10, 21)));
    }

    [Fact]
    public void Validate_RejectsTwoTriggersAtOnce()
    {
        var schedule = new OrielNotificationSchedule
        {
            AtDate = Local(2026, 10, 22),
            Every = OrielScheduleEvery.Day,
        };

        Assert.Contains("三者之一", schedule.Validate(Local(2026, 10, 21)));
    }

    [Fact]
    public void Validate_RejectsPastAtDate()
    {
        var schedule = new OrielNotificationSchedule { AtDate = Local(2026, 10, 20) };

        Assert.Contains("将来", schedule.Validate(Local(2026, 10, 21)));
    }

    [Fact]
    public void Validate_AcceptsFutureAtDate()
    {
        var schedule = new OrielNotificationSchedule { AtDate = Local(2026, 10, 22) };

        Assert.Null(schedule.Validate(Local(2026, 10, 21)));
    }

    [Theory]
    [InlineData("fortnight")]
    [InlineData("Hour")]   // 大小写敏感：Tauri 的枚举是小写字符串
    [InlineData("")]
    public void Validate_RejectsUnknownEveryUnit(string unit)
    {
        var schedule = new OrielNotificationSchedule { Every = unit, EveryCount = 1 };

        Assert.Contains("未知", schedule.Validate(Local(2026, 10, 21)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_RejectsNonPositiveEveryCount(int count)
    {
        var schedule = new OrielNotificationSchedule { Every = OrielScheduleEvery.Hour, EveryCount = count };

        Assert.Contains("count", schedule.Validate(Local(2026, 10, 21)));
    }

    [Fact]
    public void Validate_RejectsEmptyInterval()
    {
        var schedule = new OrielNotificationSchedule { Interval = new OrielScheduleInterval() };

        Assert.Contains("至少要设定一个字段", schedule.Validate(Local(2026, 10, 21)));
    }

    [Theory]
    [InlineData(13, null, null)]   // 月越界
    [InlineData(null, 24, null)]   // 时越界
    [InlineData(null, null, 8)]    // 星期越界
    public void Validate_RejectsOutOfRangeIntervalFields(int? month, int? hour, int? weekday)
    {
        var schedule = new OrielNotificationSchedule
        {
            Interval = new OrielScheduleInterval { Month = month, Hour = hour, Weekday = weekday },
        };

        Assert.Contains("超出范围", schedule.Validate(Local(2026, 10, 21)));
    }

    [Fact]
    public void Validate_RejectsIntervalThatNeverMatches()
    {
        var schedule = new OrielNotificationSchedule
        {
            Interval = new OrielScheduleInterval { Month = 2, Day = 30 },
        };

        Assert.Contains("永远不会成立", schedule.Validate(Local(2026, 10, 21)));
    }

    [Fact]
    public void Validate_AcceptsCalendarInterval()
    {
        var schedule = new OrielNotificationSchedule
        {
            Interval = new OrielScheduleInterval { Hour = 9, Minute = 0 },
        };

        Assert.Null(schedule.Validate(Local(2026, 10, 21)));
    }

    [Fact]
    public void EveryPeriod_IsUnitTimesCount()
    {
        var schedule = new OrielNotificationSchedule { Every = OrielScheduleEvery.Hour, EveryCount = 3 };

        Assert.Equal(TimeSpan.FromHours(3), schedule.EveryPeriod);
    }

    [Fact]
    public void EveryPeriod_ApproximatesYearAndMonthFixedDays()
    {
        // 年 = 365 天、月 = 30 天：调度器在进程里，"隔 N 天"而不是"每年某日"
        Assert.Equal(TimeSpan.FromDays(365), new OrielNotificationSchedule { Every = OrielScheduleEvery.Year }.EveryPeriod);
        Assert.Equal(TimeSpan.FromDays(30), new OrielNotificationSchedule { Every = OrielScheduleEvery.Month }.EveryPeriod);
    }
}
