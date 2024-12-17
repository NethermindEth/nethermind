// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Db;

// TODO: get rid of after testing
public class ExecTimeStats
{
    private double _totalMs;
    private int _count;

    public void Include(TimeSpan elapsed)
    {
        _totalMs += elapsed.TotalMilliseconds;
        _count++;
    }

    public TimeSpan Average => _count == 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(_totalMs / _count);

    public override string ToString() => $"{Average.TotalMicroseconds:F1}ms ({_count})";

    public void Add(ExecTimeStats stats)
    {
        _totalMs += stats._totalMs;
        _count += stats._count;
    }
}
