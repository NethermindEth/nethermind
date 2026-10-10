// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.JsonRpc.Test;

/// <summary>A clock that moves only when told to, firing the timers that fall due.</summary>
/// <remarks>
/// Its timestamps count nanoseconds, as the Linux system clock does, not <see cref="TimeSpan"/> ticks, so code that takes
/// one unit for the other fails on it.
/// </remarks>
internal sealed class ManualClock : TimeProvider
{
    private const long Frequency = 1_000_000_000;

    private readonly List<ManualTimer> _timers = [];
    private long _timestamp;

    /// <summary>When set, <see cref="CreateTimer"/> throws it.</summary>
    public Exception? TimerFailure { get; init; }

    /// <summary>How long before its due time a timer fires, as the system timer may by up to about a millisecond.</summary>
    public TimeSpan TimerEarliness { get; init; }

    public override long TimestampFrequency => Frequency;

    public override long GetTimestamp() => Volatile.Read(ref _timestamp);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        if (TimerFailure is not null) throw TimerFailure;
        ManualTimer timer = new(this, callback, state);
        timer.Change(dueTime, period);
        lock (_timers) _timers.Add(timer);
        return timer;
    }

    /// <param name="elapsed">How far to move the clock.</param>
    /// <param name="fireTimers">Whether due timers fire, or stay late as on a starved timer thread.</param>
    public void Advance(TimeSpan elapsed, bool fireTimers = true)
    {
        long now = Interlocked.Add(ref _timestamp, ToTimestamp(elapsed));
        if (!fireTimers) return;

        long firesUpTo = now + ToTimestamp(TimerEarliness);
        ManualTimer[] due;
        lock (_timers)
        {
            due = [.. _timers.Where(t => t.DueTimestamp <= firesUpTo)];
            _timers.RemoveAll(due.Contains);
        }

        foreach (ManualTimer timer in due)
        {
            timer.Fire();
        }
    }

    private static long ToTimestamp(TimeSpan span) => span.Ticks * (Frequency / TimeSpan.TicksPerSecond);

    private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
    {
        public long DueTimestamp { get; private set; }

        public void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            DueTimestamp = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : clock.GetTimestamp() + ToTimestamp(dueTime);
            return true;
        }

        public void Dispose()
        {
            lock (clock._timers) clock._timers.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return default;
        }
    }
}
