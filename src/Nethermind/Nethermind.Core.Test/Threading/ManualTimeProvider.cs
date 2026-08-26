// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Core.Test.Threading;

public sealed class ManualTimeProvider : TimeProvider
{
    private sealed class NoopTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;
        public void Dispose() { }
        public ValueTask DisposeAsync() => default;
    }

    private static readonly NoopTimer SharedTimer = new();
    private readonly TaskCompletionSource _timerCreated = new(TaskCreationOptions.RunContinuationsAsynchronously);
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
        _timerCreated.TrySetResult();
        return SharedTimer;
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
        _timerCallback?.Invoke(_timerState);
    }
}
