namespace VitalWatch.Tests;

/// <summary>
/// Elle ilerletilen saat. <c>Task.Delay(süre, saat, iptal)</c> zamanlayıcıyı buradan alır;
/// <see cref="Advance"/> vadesi gelenleri tetikler.
/// </summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = new(2026, 10, 23, 9, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);

        lock (_gate)
        {
            _timers.Add(timer);
        }

        timer.Change(dueTime, period);

        return timer;
    }

    /// <summary>Kurulu (vadesi gelmemiş) zamanlayıcı sayısı.</summary>
    public int Pending
    {
        get
        {
            lock (_gate)
            {
                return _timers.Count(t => t.Due is not null);
            }
        }
    }

    /// <summary>Döngü bir sonraki beklemeye girene kadar bekler.</summary>
    public async Task<bool> WaitForPendingAsync(int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

        while (DateTime.UtcNow < deadline)
        {
            if (Pending > 0)
            {
                return true;
            }

            await Task.Delay(10);
        }

        return Pending > 0;
    }

    public void Advance(TimeSpan delta)
    {
        List<ManualTimer> due;

        lock (_gate)
        {
            _now += delta;
            due = _timers.Where(t => t.Due is { } d && d <= _now).ToList();

            foreach (var timer in due)
            {
                timer.Due = timer.Period > TimeSpan.Zero && timer.Period != Timeout.InfiniteTimeSpan
                    ? _now + timer.Period
                    : null;
            }
        }

        foreach (var timer in due)
        {
            timer.Fire();
        }
    }

    private void Remove(ManualTimer timer)
    {
        lock (_gate)
        {
            _timers.Remove(timer);
        }
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset? Due { get; set; }

        public TimeSpan Period { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                Period = period;
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
            }

            return true;
        }

        public void Fire() => callback(state);

        public void Dispose() => owner.Remove(this);

        public ValueTask DisposeAsync()
        {
            Dispose();

            return ValueTask.CompletedTask;
        }
    }
}
