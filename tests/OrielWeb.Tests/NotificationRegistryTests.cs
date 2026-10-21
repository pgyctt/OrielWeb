using Xunit;

namespace OrielWeb.Tests;

/// <summary>
/// 通知登记表（B18）的语义：待发/活动两张视图、取消、移除、编号与投递失败的处理。
/// </summary>
/// <remarks>
/// <para>
/// 用**假时钟 + 手动定时器**而不是真等待：这套逻辑的正确性全在"下次是哪一刻、这次该不该投递"
/// 上，靠 <c>Thread.Sleep</c> 去碰这些边界只会得到一堆偶发失败。
/// </para>
/// <para>
/// 这里刻意**不测真实投递**（那会真的弹系统通知），投递行为由 <c>deliver</c> 委托模拟。
/// </para>
/// </remarks>
public sealed class NotificationRegistryTests
{
    private static DateTimeOffset Local(int year, int month, int day, int hour = 0, int minute = 0, int second = 0)
        => new(new DateTime(year, month, day, hour, minute, second, DateTimeKind.Local));

    /// <summary>假时钟 + 手动定时器 + 记录投递。</summary>
    private sealed class Harness
    {
        public DateTimeOffset Now { get; set; } = Local(2026, 10, 21, 8, 0);

        public List<(TimeSpan Delay, Action Callback)> Timers { get; } = [];

        public List<OrielNotificationRecord> Delivered { get; } = [];

        /// <summary>下一次投递是否成功（模拟"没有通知守护"）。</summary>
        public bool DeliverResult { get; set; } = true;

        public OrielNotificationRegistry Registry { get; }

        public Harness()
        {
            Registry = new OrielNotificationRegistry(
                () => Now,
                (delay, callback) => Timers.Add((delay, callback)),
                record =>
                {
                    Delivered.Add(record);
                    return DeliverResult;
                });
        }

        /// <summary>触发最近排下的那个定时器（重排会追加新的，所以"最近"就是当前有效的那个）。</summary>
        public void FireLatest() => Timers[^1].Callback();

        public TimeSpan LatestDelay => Timers[^1].Delay;
    }

    private static OrielNotificationOptions Options(string title = "标题") => new() { Title = title, Body = "正文" };

    // ---- 排期与投递 ----

    [Fact]
    public void ScheduledNoticeIsPendingUntilItFires()
    {
        var harness = new Harness();

        long id = harness.Registry.Schedule(Options(), new OrielNotificationSchedule { AtDate = harness.Now.AddMinutes(10) });

        OrielNotificationRecord pending = Assert.Single(harness.Registry.Pending());
        Assert.Equal(id, pending.Id);
        Assert.Equal("标题", pending.Title);
        Assert.NotNull(pending.Schedule);
        Assert.Empty(harness.Registry.Active());
        Assert.Equal(TimeSpan.FromMinutes(10), harness.LatestDelay);
        Assert.Empty(harness.Delivered);
    }

    [Fact]
    public void OneShotNoticeMovesToActiveAfterDelivery()
    {
        var harness = new Harness();
        harness.Registry.Schedule(Options(), new OrielNotificationSchedule { AtDate = harness.Now.AddMinutes(10) });

        harness.Now = harness.Now.AddMinutes(10);
        harness.FireLatest();

        Assert.Single(harness.Delivered);
        Assert.Empty(harness.Registry.Pending());
        OrielNotificationRecord active = Assert.Single(harness.Registry.Active());
        Assert.Equal("标题", active.Title);
        // 排期来的那条保留原排期（Tauri 的 ActiveNotification.schedule 同义）
        Assert.NotNull(active.Schedule);
    }

    [Fact]
    public void RepeatingNoticeStaysPendingWithTheNextDueTime()
    {
        var harness = new Harness();
        long id = harness.Registry.Schedule(
            Options(),
            new OrielNotificationSchedule { Every = OrielScheduleEvery.Hour, EveryCount = 2 });

        Assert.Equal(TimeSpan.FromHours(2), harness.LatestDelay);

        harness.Now = harness.Now.AddHours(2);
        harness.FireLatest();

        Assert.Single(harness.Delivered);
        // 周期通知投递之后**仍然待发**（对 Tauri 的 at/repeating 与 every 都是这个语义）
        Assert.Equal(id, Assert.Single(harness.Registry.Pending()).Id);
        Assert.Equal(TimeSpan.FromHours(2), harness.LatestDelay);
        Assert.Empty(harness.Registry.Active());
    }

    [Fact]
    public void MissedOccurrencesAreCollapsedIntoOneDelivery()
    {
        // 机器睡眠醒来之后：错过的多次只补一次，且下一次必须落在将来
        var harness = new Harness();
        harness.Registry.Schedule(Options(), new OrielNotificationSchedule { Every = OrielScheduleEvery.Hour });

        harness.Now = harness.Now.AddHours(5);
        harness.FireLatest();

        Assert.Single(harness.Delivered);
        // 下一次仍是一小时后，而不是立刻再响
        Assert.Equal(TimeSpan.FromHours(1), harness.LatestDelay);
    }

    [Fact]
    public void CalendarIntervalReschedulesToTheNextMatch()
    {
        var harness = new Harness();
        harness.Registry.Schedule(
            Options(),
            new OrielNotificationSchedule { Interval = new OrielScheduleInterval { Hour = 9, Minute = 0 } });

        // 08:00 排期 → 09:00 触发
        Assert.Equal(TimeSpan.FromHours(1), harness.LatestDelay);

        harness.Now = Local(2026, 10, 21, 9, 0);
        harness.FireLatest();

        Assert.Single(harness.Delivered);
        Assert.Single(harness.Registry.Pending());
        // 下一次是明天 09:00
        Assert.Equal(TimeSpan.FromHours(24), harness.LatestDelay);
    }

    [Fact]
    public void DeliveryFailureKeepsOneShotOutOfActive()
    {
        var harness = new Harness { DeliverResult = false };
        harness.Registry.Schedule(Options(), new OrielNotificationSchedule { AtDate = harness.Now.AddMinutes(1) });

        harness.Now = harness.Now.AddMinutes(1);
        harness.FireLatest();

        Assert.Single(harness.Delivered);
        Assert.Empty(harness.Registry.Active());
        Assert.Empty(harness.Registry.Pending());
    }

    [Fact]
    public void LongDelayIsClampedAndReArmedUntilDue()
    {
        var harness = new Harness();
        harness.Registry.Schedule(Options(), new OrielNotificationSchedule { AtDate = harness.Now.AddDays(365) });

        // Timer 的 dueTime 是 int 毫秒，装不下一年；钳到 20 天再算
        Assert.Equal(TimeSpan.FromDays(20), harness.LatestDelay);

        harness.Now = harness.Now.AddDays(20);
        harness.FireLatest();

        // 还没到期，不该提前投递；继续排下一次钳过的定时器
        Assert.Empty(harness.Delivered);
        Assert.Equal(TimeSpan.FromDays(20), harness.LatestDelay);

        harness.Now = Local(2026, 10, 21, 8, 0).AddDays(365);
        harness.FireLatest();

        Assert.Single(harness.Delivered);
    }

    // ---- 取消与移除 ----

    [Fact]
    public void CancelRemovesPendingAndStopsTheTimerCallback()
    {
        var harness = new Harness();
        long id = harness.Registry.Schedule(Options(), new OrielNotificationSchedule { AtDate = harness.Now.AddMinutes(1) });

        Assert.Equal(1, harness.Registry.Cancel([id]));
        Assert.Empty(harness.Registry.Pending());

        // 已经排进定时器队列的回调仍会到达——令牌必须已经取消（Dispose 不取消，见 Disarm 的说明）
        harness.Now = harness.Now.AddMinutes(1);
        harness.FireLatest();
        Assert.Empty(harness.Delivered);
    }

    [Fact]
    public void CancelDoesNotTouchActiveNotifications()
    {
        var harness = new Harness();
        OrielNotificationRecord record = harness.Registry.TrackDelivered(Options());

        // cancel 的语义只针对待发（与 Tauri 一致）
        Assert.Equal(0, harness.Registry.Cancel([record.Id]));
        Assert.Single(harness.Registry.Active());
    }

    [Fact]
    public void RemoveActiveRemovesOnlyDeliveredOnes()
    {
        var harness = new Harness();
        OrielNotificationRecord active = harness.Registry.TrackDelivered(Options());
        long pending = harness.Registry.Schedule(Options(), new OrielNotificationSchedule { AtDate = harness.Now.AddMinutes(1) });

        Assert.Equal(1, harness.Registry.RemoveActive([active.Id, pending]));
        Assert.Empty(harness.Registry.Active());
        // 待发的不在 removeActive 的语义里
        Assert.Single(harness.Registry.Pending());
    }

    [Fact]
    public void CancelAllAndRemoveAllActiveReportCounts()
    {
        var harness = new Harness();
        harness.Registry.TrackDelivered(Options());
        harness.Registry.TrackDelivered(Options());
        harness.Registry.Schedule(Options(), new OrielNotificationSchedule { AtDate = harness.Now.AddMinutes(1) });
        harness.Registry.Schedule(Options(), new OrielNotificationSchedule { AtDate = harness.Now.AddMinutes(2) });

        Assert.Equal(2, harness.Registry.RemoveAllActive());
        Assert.Equal(2, harness.Registry.CancelAll());
        Assert.Empty(harness.Registry.Active());
        Assert.Empty(harness.Registry.Pending());
    }

    [Fact]
    public void CancelOfUnknownIdIsNotAnError()
    {
        var harness = new Harness();

        Assert.Equal(0, harness.Registry.Cancel([12345]));
    }

    [Fact]
    public void IdsAreMonotonicAndNeverReused()
    {
        var harness = new Harness();

        OrielNotificationRecord first = harness.Registry.TrackDelivered(Options("一"));
        long second = harness.Registry.Schedule(Options("二"), new OrielNotificationSchedule { AtDate = harness.Now.AddMinutes(1) });
        harness.Registry.Cancel([second]);
        OrielNotificationRecord third = harness.Registry.TrackDelivered(Options("三"));

        Assert.Equal(first.Id + 1, second);
        // 取消掉的编号不复用
        Assert.Equal(second + 1, third.Id);
    }

    [Fact]
    public void ActiveViewIsCappedAndDropsTheOldest()
    {
        var harness = new Harness();

        OrielNotificationRecord first = harness.Registry.TrackDelivered(Options("最早"));
        for (int i = 0; i < 300; i++)
        {
            harness.Registry.TrackDelivered(Options("后续"));
        }

        IReadOnlyList<OrielNotificationRecord> active = harness.Registry.Active();
        Assert.Equal(256, active.Count);
        Assert.DoesNotContain(active, record => record.Id == first.Id);
    }

    [Fact]
    public void DisposeStopsScheduledDeliveries()
    {
        var harness = new Harness();
        harness.Registry.Schedule(Options(), new OrielNotificationSchedule { AtDate = harness.Now.AddMinutes(1) });

        harness.Registry.Dispose();

        harness.Now = harness.Now.AddMinutes(1);
        harness.FireLatest();

        Assert.Empty(harness.Delivered);
        Assert.Empty(harness.Registry.Pending());
    }
}
