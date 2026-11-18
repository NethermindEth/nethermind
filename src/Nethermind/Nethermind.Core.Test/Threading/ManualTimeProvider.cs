// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Core.Test.Threading;

public sealed class ManualTimeProvider : TimeProvider
{
    private sealed class ManualTimer : ITimer
    {
        private readonly ManualTimeProvider _clock;
        private TimerCallback? _callback;
        private object? _state;
        internal long DueAt;
        private long _period;
        private long _generation;
        private bool _disposed;

        internal ManualTimer(ManualTimeProvider clock, TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _clock = clock;
            _callback = callback;
            _state = state;
            SetDeadline(dueTime, period);
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            Validate(dueTime, period);
            lock (_clock._timerLock)
            {
                if (_disposed) return false;
                _generation++;
                SetDeadline(dueTime, period);
                return true;
            }
        }

        private void SetDeadline(TimeSpan dueTime, TimeSpan period)
        {
            Validate(dueTime, period);
            long now = _clock.GetTimestamp();
            if (dueTime > TimeSpan.Zero && now >= long.MaxValue - dueTime.Ticks)
                throw new ArgumentOutOfRangeException(nameof(dueTime));
            DueAt = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : now + dueTime.Ticks;
            _period = period > TimeSpan.Zero ? period.Ticks : 0;
        }

        private static void Validate(TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime < TimeSpan.Zero && dueTime != Timeout.InfiniteTimeSpan) throw new ArgumentOutOfRangeException(nameof(dueTime));
            if (period < TimeSpan.Zero && period != Timeout.InfiniteTimeSpan) throw new ArgumentOutOfRangeException(nameof(period));
        }

        internal void Fire(bool force)
        {
            TimerCallback? callback;
            object? state;
            long generation;
            long dueAt;
            lock (_clock._timerLock)
            {
                if (_disposed || !force && DueAt > _clock.GetTimestamp()) return;
                callback = _callback;
                state = _state;
                generation = _generation;
                dueAt = DueAt;
                DueAt = long.MaxValue;
            }
            try
            {
                callback?.Invoke(state);
            }
            finally
            {
                lock (_clock._timerLock)
                {
                    if (!_disposed && _generation == generation && _period != 0)
                    {
                        long now = _clock.GetTimestamp();
                        long start = dueAt == long.MaxValue ? now : Math.Max(dueAt, now);
                        DueAt = start >= long.MaxValue - _period ? long.MaxValue : start + _period;
                    }
                }
            }
        }

        public void Dispose()
        {
            lock (_clock._timerLock)
            {
                if (_disposed) return;
                _disposed = true;
                _generation++;
                _callback = null;
                _state = null;
                _clock._timers.Remove(this);
                if (ReferenceEquals(_clock._timer, this)) _clock._timer = null;
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return default;
        }
    }

    private readonly Lock _timerLock = new();
    private readonly List<TimeSpan> _dueTimes = [];
    private readonly List<ManualTimer> _timers = [];
    private readonly TaskCompletionSource _timerCreated = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource _nextTimerCreated = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ManualTimer? _timer;
    private long _elapsedTicks;
    private long _utcTicks;

    public Task TimerCreated => _timerCreated.Task;

    /// <summary>Gets a snapshot of the initial due times supplied to created timers.</summary>
    public IReadOnlyList<TimeSpan> DueTimes
    {
        get
        {
            lock (_timerLock) return _dueTimes.ToArray();
        }
    }

    /// <summary>Completes when a timer is created after this property is read.</summary>
    public Task NextTimerCreated
    {
        get
        {
            lock (_timerLock) return _nextTimerCreated.Task;
        }
    }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Volatile.Read(ref _elapsedTicks);

    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(Volatile.Read(ref _utcTicks));

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        ManualTimer timer = new(this, callback, state, dueTime, period);
        lock (_timerLock)
        {
            _timer = timer;
            _timers.Add(timer);
            _dueTimes.Add(dueTime);
            _timerCreated.TrySetResult();
            _nextTimerCreated.TrySetResult();
            _nextTimerCreated = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        return timer;
    }

    public void Advance(TimeSpan elapsed)
    {
        Interlocked.Add(ref _elapsedTicks, elapsed.Ticks);
        Interlocked.Add(ref _utcTicks, elapsed.Ticks);
    }

    public void JumpUtc(TimeSpan delta) => Interlocked.Add(ref _utcTicks, delta.Ticks);

    public void AdvanceAndFireTimer(TimeSpan elapsed)
    {
        Advance(elapsed);
        ManualTimer? timer;
        lock (_timerLock) timer = _timer;
        timer?.Fire(force: true);
    }

    /// <summary>Advances time and fires timers in deadline order, including timers created by callbacks.</summary>
    public void AdvanceAndFireDueTimers(TimeSpan elapsed)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(elapsed.Ticks);
        long target = checked(GetTimestamp() + elapsed.Ticks);
        while (true)
        {
            ManualTimer? next = null;
            lock (_timerLock)
            {
                foreach (ManualTimer timer in _timers)
                {
                    if (timer.DueAt != long.MaxValue && timer.DueAt <= target && (next is null || timer.DueAt < next.DueAt)) next = timer;
                }
                if (next is null)
                {
                    if (GetTimestamp() < target) Advance(TimeSpan.FromTicks(target - GetTimestamp()));
                    return;
                }
                if (GetTimestamp() < next.DueAt) Advance(TimeSpan.FromTicks(next.DueAt - GetTimestamp()));
            }
            next.Fire(force: false);
        }
    }
}
