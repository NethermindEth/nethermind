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

    public override string ToString() => $"{Average.TotalMicroseconds:F1}μs ({_count})";

    public void Combine(ExecTimeStats stats)
    {
        _totalMicroseconds += stats._totalMicroseconds;
        _count += stats._count;
    }
}
