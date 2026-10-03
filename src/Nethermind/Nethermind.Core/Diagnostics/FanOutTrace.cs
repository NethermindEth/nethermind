// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Nethermind.Core.Diagnostics;

/// <summary>
/// Timing of one parallel fan-out forked from the block-processing thread, noted on the newPayload trace as
/// label:n{jobs}:w{threads}:q{pool queue at the fork}:s{latest job start}:e{last job end}:b{summed job time}, in µs, then the
/// scheduler's load at the fork (r reserved runners, u not yet started, p pending callbacks) and a, the prewarm runs in progress.
/// </summary>
public sealed class FanOutTrace
{
    private readonly long _fork = Stopwatch.GetTimestamp();
    private readonly long[] _start;
    private readonly long[] _end;
    private readonly int[] _thread;
    private readonly long _queued = ThreadPool.PendingWorkItemCount;
    private readonly (int Reserved, int Unstarted, int Pending) _load = Threading.ParallelUnbalancedWork.CurrentLoad();
    private readonly int _warming = PrewarmActivity.Active;

    private FanOutTrace(int jobs)
    {
        _start = new long[jobs];
        _end = new long[jobs];
        _thread = new int[jobs];
    }

    /// <summary>A trace for a fan-out of <paramref name="jobs"/> forked on the block-processing thread, otherwise null.</summary>
    public static FanOutTrace? Begin(int jobs) =>
        NewPayloadTrace.Enabled && jobs > 0 && Threading.ProcessingThread.IsBlockProcessingThread ? new FanOutTrace(jobs) : null;

    public void JobStart(int job)
    {
        _start[job] = Stopwatch.GetTimestamp();
        _thread[job] = Environment.CurrentManagedThreadId;
    }

    public void JobEnd(int job) => _end[job] = Stopwatch.GetTimestamp();

    public void Note(string label)
    {
        long latestStart = 0, lastEnd = 0, busy = 0;
        HashSet<int> threads = [];
        for (int i = 0; i < _start.Length; i++)
        {
            if (_start[i] == 0 || _end[i] == 0) continue;
            latestStart = Math.Max(latestStart, _start[i] - _fork);
            lastEnd = Math.Max(lastEnd, _end[i] - _fork);
            busy += _end[i] - _start[i];
            threads.Add(_thread[i]);
        }

        static long us(long ticks) => ticks * 1_000_000 / Stopwatch.Frequency;
        NewPayloadTrace.Note($"{label}:n{_start.Length}:w{threads.Count}:q{_queued}:s{us(latestStart)}:e{us(lastEnd)}:b{us(busy)}:r{_load.Reserved}:u{_load.Unstarted}:p{_load.Pending}:a{_warming}");
    }
}
