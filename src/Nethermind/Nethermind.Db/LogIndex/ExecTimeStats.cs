// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;

namespace Nethermind.Db;

// TODO: get rid of after testing
public class ExecTimeStats
{
    private long _totalMicroseconds;
    private int _count;

    public void Include(TimeSpan elapsed)
    {
        Interlocked.Add(ref _totalMicroseconds, (long)elapsed.TotalMicroseconds);
        Interlocked.Increment(ref _count);
    }

    public TimeSpan Average => _count == 0 ? TimeSpan.Zero : TimeSpan.FromMicroseconds((double)_totalMicroseconds / _count);

    public override string ToString()
    {
        var timeStr = Average switch
        {
            { TotalDays: >= 1 } x => $"{x.TotalDays:F2}d",
            { TotalHours: >= 1 } x => $"{x.TotalHours:F2}h",
            { TotalMinutes: >= 1 } x => $"{x.TotalMinutes:F2}m",
            { TotalSeconds: >= 1 } x => $"{x.TotalSeconds:F2}s",
            { TotalMilliseconds: >= 1 } x => $"{x.TotalMilliseconds:F1}ms",
            { TotalMicroseconds: >= 1 } x => $"{x.TotalMicroseconds:F1}μs",
            var x => $"{x.TotalNanoseconds:F1}ns"
        };

        return $"{timeStr} ({_count:N0})";
    }

    public void Combine(ExecTimeStats stats)
    {
        _totalMicroseconds += stats._totalMicroseconds;
        _count += stats._count;
    }
}
