// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;

namespace Nethermind.Blockchain.Tracing.GethStyle;

internal sealed class GethTraceDeadline : IDisposable
{
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaximumTimerDelay = TimeSpan.FromMilliseconds(uint.MaxValue - 1);
    private readonly object _gate = new();
    private readonly CancellationTokenSource _request = new();
    private readonly CancellationTokenSource _linked;
    private readonly CancellationToken _external;
    private readonly TimeProvider _clock;
    private ITimer? _timer;
    private long _started;
    private TimeSpan _duration;
    private bool _disposed;
    private bool _paused;

    internal GethTraceDeadline(CancellationToken external = default, TimeProvider? clock = null)
    {
        _external = external;
        _clock = clock ?? TimeProvider.System;
        try { _linked = CancellationTokenSource.CreateLinkedTokenSource(external, _request.Token); }
        catch { _request.Dispose(); throw; }
    }
    internal CancellationToken Token => _linked.Token;
    internal bool Expired => _request.IsCancellationRequested && !_external.IsCancellationRequested;

    internal void Start(TimeSpan duration)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _duration = duration;
            _started = _clock.GetTimestamp();
            // Geth's traceTx watcher delivers even an already-expired deadline asynchronously.
            _timer = _clock.CreateTimer(static state => ((GethTraceDeadline)state!).Tick(), this,
                Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _timer.Change(Delay(duration), Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Suspends the owned execution budget without suspending external cancellation.</summary>
    internal void Pause()
    {
        lock (_gate)
        {
            if (_disposed || _paused || _timer is null) return;
            _external.ThrowIfCancellationRequested();
            if (Expired) throw new GethTraceDeadlineException();
            _duration -= _clock.GetElapsedTime(_started);
            _paused = true;
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Resumes the remaining execution budget without resetting it.</summary>
    internal void Resume()
    {
        lock (_gate)
        {
            if (_disposed || !_paused || _external.IsCancellationRequested) return;
            _started = _clock.GetTimestamp();
            _paused = false;
            _timer!.Change(Delay(_duration), Timeout.InfiniteTimeSpan);
        }
    }

    // Go permits durations longer than a .NET timer's 32-bit millisecond range.
    private static TimeSpan Delay(TimeSpan remaining) => remaining <= TimeSpan.Zero ? TimeSpan.Zero
        : remaining > MaximumTimerDelay ? MaximumTimerDelay : remaining;
    private void Tick()
    {
        lock (_gate)
        {
            if (_disposed || _paused) return;
            TimeSpan remaining = _duration - _clock.GetElapsedTime(_started);
            if (remaining <= TimeSpan.Zero) _request.Cancel();
            else _timer!.Change(Delay(remaining), Timeout.InfiniteTimeSpan);
        }
    }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _timer?.Dispose();
            _linked.Dispose();
            _request.Dispose();
        }
    }
}
