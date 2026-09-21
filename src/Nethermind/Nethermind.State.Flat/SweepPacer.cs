// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;

namespace Nethermind.State.Flat;

/// <summary>Serializes the passes of every sweep sharing it and makes each pass pay for itself: after a pass that took
/// <c>t</c>, the next one may not start for another <c>t</c>, so the sweeps together use at most half of the wall clock.
/// Turns are handed out in arrival order, so a sweep whose loop asks again the moment its pass ends cannot starve the
/// other one.</summary>
public sealed class SweepPacer
{
    private readonly object _lock = new();
    private readonly HashSet<long> _abandoned = [];
    private long _nextTicket;
    private long _serving;
    private long _resumeAt;

    public bool Run(Func<bool> pass, CancellationToken token)
    {
        TakeTurn(token);
        try
        {
            TimeSpan owed = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), _resumeAt);
            if (owed > TimeSpan.Zero && token.WaitHandle.WaitOne(owed)) throw new OperationCanceledException(token);

            long startedAt = Stopwatch.GetTimestamp();
            bool completed = pass();
            long finishedAt = Stopwatch.GetTimestamp();
            _resumeAt = finishedAt + (finishedAt - startedAt);
            return completed;
        }
        finally
        {
            lock (_lock)
            {
                _serving++;
                SkipAbandonedLocked();
                Monitor.PulseAll(_lock);
            }
        }
    }

    private void TakeTurn(CancellationToken token)
    {
        using CancellationTokenRegistration wake = token.Register(static state =>
        {
            lock (state!)
            {
                Monitor.PulseAll(state);
            }
        }, _lock);

        lock (_lock)
        {
            long ticket = _nextTicket++;
            while (_serving != ticket)
            {
                if (token.IsCancellationRequested)
                {
                    _abandoned.Add(ticket);
                    throw new OperationCanceledException(token);
                }

                Monitor.Wait(_lock);
            }
        }
    }

    private void SkipAbandonedLocked()
    {
        while (_abandoned.Remove(_serving)) _serving++;
    }
}
