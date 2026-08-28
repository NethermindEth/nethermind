// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Core.Test.Threading;

public sealed class ManualTimeProvider : TimeProvider
{
    private sealed class RecordedTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        public TimerCallback Callback => callback;
        public object? State => state;
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;
        public void Dispose() => owner.Remove(this);
        public ValueTask DisposeAsync()
        {
            Dispose();
            return default;
        }
    }

    private readonly TaskCompletionSource _timerCreated = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<RecordedTimer> _timers = [];
    private TimerCallback? _timerCallback;
    private object? _timerState;
    private long _elapsedTicks;
    private long _utcTicks;

    public Task TimerCreated => _timerCreated.Task;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Volatile.Read(ref _elapsedTicks);

    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(Volatile.Read(ref _utcTicks));

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        _timerCallback = callback;
        _timerState = state;
        RecordedTimer timer = new(this, callback, state);
        lock (_timers)
        {
            _timers.Add(timer);
        }

        _timerCreated.TrySetResult();
        return timer;
    }

    public void Advance(TimeSpan elapsed)
    {
        Interlocked.Add(ref _elapsedTicks, elapsed.Ticks);
        Interlocked.Add(ref _utcTicks, elapsed.Ticks);
    }

    public void JumpUtc(TimeSpan delta) => Interlocked.Add(ref _utcTicks, delta.Ticks);

    /// <summary>Advances the clock and fires the most recently created timer, whether or not it is still armed.</summary>
    public void AdvanceAndFireTimer(TimeSpan elapsed)
    {
        Advance(elapsed);
        _timerCallback?.Invoke(_timerState);
    }

    /// <summary>Advances the clock and fires every timer that is still armed, oldest first.</summary>
    public void AdvanceAndFireTimers(TimeSpan elapsed)
    {
        Advance(elapsed);
        RecordedTimer[] timers;
        lock (_timers)
        {
            timers = [.. _timers];
        }

        foreach (RecordedTimer timer in timers)
        {
            timer.Callback(timer.State);
        }
    }

    private void Remove(RecordedTimer timer)
    {
        lock (_timers)
        {
            _timers.Remove(timer);
        }
    }
}
