// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;

namespace Nethermind.Db.LogIndex;

public class AverageStats
{
    private long _total;
    private int _count;

    public void Include(long value)
    {
        Interlocked.Add(ref _total, value);
        Interlocked.Increment(ref _count);
    }

    public double Average => _count == 0 ? 0 : (double)_total / _count;

    public override string ToString() => $"{Average:F2} ({_count:N0})";

    public void Combine(AverageStats stats)
    {
        _total += stats._total;
        _count += stats._count;
    }
}
