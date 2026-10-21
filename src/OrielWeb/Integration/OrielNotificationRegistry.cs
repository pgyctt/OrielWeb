namespace OrielWeb;

/// <summary>
/// 本应用发过的通知与排期中的通知（对标 Tauri 的"通知句柄表"）。
/// </summary>
/// <remarks>
/// <para>
/// <b>它记的是"本库发出过的通知"，不是系统通知中心</b>。三平台都没有"列出通知中心里的通知"
/// 的 API（Windows 的 <c>ToastNotificationManager.History</c> 只列本应用、且要求打包；
/// macOS 没有公开 API；Linux 的通知守护各有各的协议），所以 <see cref="Active"/> 的语义是
/// "本应用发出、且没有被 <see cref="RemoveActive"/> 撤下的那些"。
/// </para>
/// <para>
/// <b>撤下（remove）只撤登记项，不撤系统里那条</b>：投递走的是短命子进程
/// （Windows PowerShell / macOS osascript / Linux notify-send），投递完就拿不到系统侧的句柄了
/// ——Windows 要活的 <c>ToastNotification</c> 对象才<em>能</em> <c>Hide()</c>，
/// macOS 的 osascript 没有对应命令，Linux 要另外用 D-Bus 的 <c>CloseNotification</c>。
/// 这条差异写在 docs/API.md，而不是假装"移除等于撤下"。
/// </para>
/// <para>
/// <b>线程模型</b>：定时器回调在线程池上跑，与命令（UI 线程）并发。所有状态在 <see cref="_gate"/>
/// 下改动；投递（子进程）在锁外做——它可能阻塞几百毫秒，不该把 <c>active()</c> 一起卡住。
/// </para>
/// <para>
/// <b>编号单调递增、永不复用</b>（与 fs 句柄表同一条理由）：复用会让"取消一条早就不存在的通知"
/// 作用到另一条通知上，症状是"偶尔取消错了"。
/// </para>
/// </remarks>
internal sealed class OrielNotificationRegistry : IDisposable
{
    /// <summary>活动通知的记账上限（超过则淘汰编号最小的那些）。</summary>
    /// <remarks>
    /// 活动通知只增不减的话，一个每分钟发一条通知的长跑应用一年就是五十万条——
    /// 而 <c>active()</c> 本来也只是一个"最近发了什么"的视图。淘汰只影响这个列表，
    /// <b>系统通知中心里的那些仍在</b>（见类型注释）。
    /// </remarks>
    private const int MaxActiveEntries = 256;

    /// <summary>
    /// 单次定时器的最长延时。
    /// </summary>
    /// <remarks>
    /// <see cref="Timer"/> 的 <c>dueTime</c> 是 <see cref="int"/> 毫秒（上限约 24.8 天），
    /// 而"每年一次"的排期远超它。做法是**钳到上限、到点再算**：触发时发现还没到期就重新排。
    /// 这样既不用自己维护日历队列，也不会因为溢出变成一个立刻触发的定时器。
    /// </remarks>
    private static readonly TimeSpan MaxTimerDelay = TimeSpan.FromDays(20);

    private readonly object _gate = new();
    private readonly Dictionary<long, Entry> _entries = [];
    private readonly Func<DateTimeOffset> _now;
    private readonly Action<TimeSpan, Action> _arm;
    private readonly Func<OrielNotificationRecord, bool> _deliver;
    private long _nextId;
    private bool _disposed;

    /// <param name="now">取当前时刻（测试注入假时钟）。</param>
    /// <param name="arm">排一个一次性定时器：(延时, 回调)。测试注入手动触发器。</param>
    /// <param name="deliver">真正投递一条通知，返回系统是否接受（同时负责向页面广播事件）。</param>
    internal OrielNotificationRegistry(
        Func<DateTimeOffset> now,
        Action<TimeSpan, Action> arm,
        Func<OrielNotificationRecord, bool> deliver)
    {
        _now = now;
        _arm = arm;
        _deliver = deliver;
    }

    /// <summary>活动通知（按编号升序）。</summary>
    internal IReadOnlyList<OrielNotificationRecord> Active()
    {
        lock (_gate)
        {
            return _entries.Values
                .Where(e => e.IsActive)
                .OrderBy(e => e.Id)
                .Select(e => e.ToRecord())
                .ToArray();
        }
    }

    /// <summary>待发通知（按编号升序）。</summary>
    internal IReadOnlyList<OrielNotificationRecord> Pending()
    {
        lock (_gate)
        {
            return _entries.Values
                .Where(e => !e.IsActive)
                .OrderBy(e => e.Id)
                .Select(e => e.ToRecord())
                .ToArray();
        }
    }

    /// <summary>
    /// 记一条**已经投递成功**的通知，返回它的登记记录（含新编号）。
    /// </summary>
    /// <remarks>
    /// 只在投递成功之后调用：投递失败（比如 Linux 上没有通知守护）时它不在系统里，
    /// 也就不该出现在 <c>active()</c> 里。
    /// </remarks>
    internal OrielNotificationRecord TrackDelivered(OrielNotificationOptions options)
    {
        lock (_gate)
        {
            var entry = new Entry
            {
                Id = ++_nextId,
                Title = options.Title,
                Body = options.Body,
                Tag = options.Id,
                IconPath = options.IconPath,
                IsActive = true,
            };

            _entries[entry.Id] = entry;
            TrimActiveLocked();
            return entry.ToRecord();
        }
    }

    /// <summary>
    /// 登记一条排期通知，返回它的编号。
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// 排期在 <see cref="OrielNotificationSchedule.Validate"/> 之后仍然算不出下次触发时刻
    /// （正常走不到；走到说明校验与计算不一致，宁可抛也不要静默丢一条通知）。
    /// </exception>
    internal long Schedule(OrielNotificationOptions options, OrielNotificationSchedule schedule)
    {
        DateTimeOffset now = _now();
        DateTimeOffset due;
        TimeSpan? period = null;

        if (schedule.AtDate is DateTimeOffset at)
        {
            due = at;
            if (schedule.AtRepeating)
            {
                // 周期 = "排期时刻 → 目标时刻"的距离（Tauri 的 at(date, repeating: true) 同义）
                period = at - now;
            }
        }
        else if (schedule.EveryPeriod is TimeSpan everyPeriod)
        {
            due = now + everyPeriod;
            period = everyPeriod;
        }
        else if (schedule.Interval is not null)
        {
            due = schedule.Interval.NextAfter(now)
                  ?? throw new InvalidOperationException("排期算不出下次触发时刻（校验应当已经拦下）。");
        }
        else
        {
            throw new InvalidOperationException("排期没有任何触发条件（校验应当已经拦下）。");
        }

        lock (_gate)
        {
            var entry = new Entry
            {
                Id = ++_nextId,
                Title = options.Title,
                Body = options.Body,
                Tag = options.Id,
                IconPath = options.IconPath,
                Schedule = schedule,
                NextDue = due,
                Period = period,
                Interval = schedule.Interval,
            };

            _entries[entry.Id] = entry;
            Arm(entry);
            return entry.Id;
        }
    }

    /// <summary>取消待发通知；返回真正取消掉的条数（活动通知不在 cancel 的语义里）。</summary>
    internal int Cancel(IEnumerable<long> ids)
    {
        lock (_gate)
        {
            int count = 0;
            foreach (long id in ids)
            {
                if (_entries.TryGetValue(id, out Entry? entry) && !entry.IsActive)
                {
                    Disarm(entry);
                    _entries.Remove(id);
                    count++;
                }
            }

            return count;
        }
    }

    /// <summary>取消全部待发通知；返回条数。</summary>
    internal int CancelAll()
    {
        lock (_gate)
        {
            long[] pending = _entries.Values.Where(e => !e.IsActive).Select(e => e.Id).ToArray();
            foreach (long id in pending)
            {
                Disarm(_entries[id]);
                _entries.Remove(id);
            }

            return pending.Length;
        }
    }

    /// <summary>从活动列表里移除通知（**不撤系统里那条**，见类型注释）；返回条数。</summary>
    internal int RemoveActive(IEnumerable<long> ids)
    {
        lock (_gate)
        {
            int count = 0;
            foreach (long id in ids)
            {
                if (_entries.TryGetValue(id, out Entry? entry) && entry.IsActive)
                {
                    _entries.Remove(id);
                    count++;
                }
            }

            return count;
        }
    }

    /// <summary>清空活动列表；返回条数。</summary>
    internal int RemoveAllActive()
    {
        lock (_gate)
        {
            long[] active = _entries.Values.Where(e => e.IsActive).Select(e => e.Id).ToArray();
            foreach (long id in active)
            {
                _entries.Remove(id);
            }

            return active.Length;
        }
    }

    /// <summary>
    /// 让这一项当前的定时器失效（取消 + 释放）。
    /// </summary>
    /// <remarks>
    /// **必须 <c>Cancel()</c> 而不只是 <c>Dispose()</c>**：<see cref="CancellationTokenSource.Dispose()"/>
    /// 只释放资源、**不会**把令牌置为已取消——只 Dispose 的话，已经排进定时器队列的那次回调
    /// 仍然会跑，症状是"取消了的通知照样弹出来"。这一点没有编译期保护，只能靠这条注释与测试守着
    /// （<c>CancelRemovesPendingAndStopsTheTimerCallback</c>）。
    /// </remarks>
    private static void Disarm(Entry entry)
    {
        if (entry.Cts is not CancellationTokenSource cts)
        {
            return;
        }

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已经释放过：令牌早就不可用了，没有要取消的东西
        }

        cts.Dispose();
        entry.Cts = null;
    }

    /// <summary>为这一项排一个到 <see cref="Entry.NextDue"/> 的定时器（超长延时由 <see cref="MaxTimerDelay"/> 钳住）。</summary>
    private void Arm(Entry entry)
    {
        if (_disposed || entry.NextDue is not DateTimeOffset due)
        {
            return;
        }

        Disarm(entry);
        var cts = new CancellationTokenSource();
        entry.Cts = cts;

        DateTimeOffset now = _now();
        TimeSpan delay = due - now;
        if (delay < TimeSpan.Zero)
        {
            delay = TimeSpan.Zero;
        }
        else if (delay > MaxTimerDelay)
        {
            delay = MaxTimerDelay;
        }

        long id = entry.Id;
        CancellationToken token = cts.Token;
        _arm(delay, () =>
        {
            // 取消（或重新排期）之后，已经排进定时器队列的那次回调仍会到达 —— 令牌是唯一的判据
            if (token.IsCancellationRequested)
            {
                return;
            }

            Fire(id);
        });
    }

    /// <summary>定时器到点：算出下一次，投递这一次。</summary>
    private void Fire(long id)
    {
        OrielNotificationRecord record;
        bool repeating;

        lock (_gate)
        {
            if (_disposed || !_entries.TryGetValue(id, out Entry? entry) || entry.IsActive)
            {
                return;
            }

            DateTimeOffset now = _now();

            // 定时器被 MaxTimerDelay 钳过：还没到期就重新排，别提前响
            if (entry.NextDue is DateTimeOffset due && now < due)
            {
                Arm(entry);
                return;
            }

            record = entry.ToRecord();
            repeating = entry.Period is not null || entry.Interval is not null;

            if (entry.Period is TimeSpan period)
            {
                // 错过的多次只补发一次（应用被挂起、机器睡眠之后醒来是常态）：
                // 连补十条同样的通知只会扰民，真正要"一条不漏"的场景需要的是队列而不是通知。
                DateTimeOffset next = entry.NextDue!.Value;
                int guard = 0;
                do
                {
                    next += period;
                    guard++;
                }
                while (next <= now && guard < 100_000);

                entry.NextDue = next;
                Arm(entry);
            }
            else if (entry.Interval is not null)
            {
                DateTimeOffset? next = entry.Interval.NextAfter(now);
                if (next is null)
                {
                    Disarm(entry);
                    _entries.Remove(id);
                }
                else
                {
                    entry.NextDue = next.Value;
                    Arm(entry);
                }
            }
            else
            {
                // 一次性：留在表里，变成"活动"
                entry.IsActive = true;
                entry.NextDue = null;
                Disarm(entry);
                TrimActiveLocked();
            }
        }

        // 投递在锁外：它会起子进程，可能阻塞几百毫秒
        bool delivered = _deliver(record);

        if (!delivered && !repeating)
        {
            // 没发出去就别留在 active 里（否则 active() 会显示一条从未出现过的通知）
            lock (_gate)
            {
                _entries.Remove(id);
            }
        }
    }

    /// <summary>活动条数超上限时淘汰编号最小的那些（调用方持锁）。</summary>
    private void TrimActiveLocked()
    {
        if (_entries.Count <= MaxActiveEntries)
        {
            return;
        }

        int excess = _entries.Values.Count(e => e.IsActive) - MaxActiveEntries;
        if (excess <= 0)
        {
            return;
        }

        foreach (long id in _entries.Values
                     .Where(e => e.IsActive)
                     .OrderBy(e => e.Id)
                     .Take(excess)
                     .Select(e => e.Id)
                     .ToArray())
        {
            _entries.Remove(id);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (Entry entry in _entries.Values)
            {
                Disarm(entry);
            }

            _entries.Clear();
        }
    }

    /// <summary>登记表里的一项。</summary>
    private sealed class Entry
    {
        public long Id { get; init; }
        public string Title { get; init; } = string.Empty;
        public string? Body { get; init; }
        public string? Tag { get; init; }
        public string? IconPath { get; init; }
        public OrielNotificationSchedule? Schedule { get; init; }
        public DateTimeOffset? NextDue { get; set; }
        public TimeSpan? Period { get; set; }
        public OrielScheduleInterval? Interval { get; set; }
        public CancellationTokenSource? Cts { get; set; }
        public bool IsActive { get; set; }

        public OrielNotificationRecord ToRecord() => new()
        {
            Id = Id,
            Title = Title,
            Body = Body,
            Tag = Tag,
            IconPath = IconPath,
            Schedule = Schedule,
        };
    }
}
