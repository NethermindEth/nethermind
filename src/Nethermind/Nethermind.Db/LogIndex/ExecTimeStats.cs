// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;

namespace Nethermind.Db.LogIndex;

/// <summary>
/// Aggregates average and total execution time of multiple executions of the same operation.
/// </summary>
public class ExecTimeStats
{
    private long _totalTicks;
    private int _count;

    public void Include(TimeSpan elapsed)
    {
        Interlocked.Add(ref _totalTicks, elapsed.Ticks);
        Interlocked.Increment(ref _count);
    }

    public TimeSpan Total => TimeSpan.FromTicks(_totalTicks);
    public TimeSpan Average => _count == 0 ? TimeSpan.Zero : TimeSpan.FromTicks((long)((double)_totalTicks / _count));

    private string Format(TimeSpan value) => value switch
    {
        { TotalDays: >= 1 } x => $"{x.TotalDays:F2}d",
        { TotalHours: >= 1 } x => $"{x.TotalHours:F2}h",
        { TotalMinutes: >= 1 } x => $"{x.TotalMinutes:F2}m",
        { TotalSeconds: >= 1 } x => $"{x.TotalSeconds:F2}s",
        { TotalMilliseconds: >= 1 } x => $"{x.TotalMilliseconds:F1}ms",
        { TotalMicroseconds: >= 1 } x => $"{x.TotalMicroseconds:F1}μs",
        var x => $"{x.TotalNanoseconds:F1}ns"
    };

    public override string ToString() => $"{Format(Average)} ({_count:N0}) [{Format(Total)}]";

    public void Combine(ExecTimeStats stats)
    {
        _totalTicks += stats._totalTicks;
        _count += stats._count;
    }
}
