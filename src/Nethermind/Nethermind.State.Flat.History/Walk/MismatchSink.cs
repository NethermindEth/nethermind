// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.History.Walk;

internal sealed class MismatchSink
{
    private readonly List<HistoryWalkMismatch> _mismatches = [];

    public void Add(in HistoryWalkMismatch mismatch)
    {
        lock (_mismatches)
        {
            _mismatches.Add(mismatch);
        }
    }

    public void AddRange(List<HistoryWalkMismatch> mismatches)
    {
        lock (_mismatches)
        {
            _mismatches.AddRange(mismatches);
        }
    }

    public List<HistoryWalkMismatch> Drain()
    {
        lock (_mismatches)
        {
            List<HistoryWalkMismatch> sorted = [.. _mismatches];
            sorted.Sort(static (a, b) => a.Block.CompareTo(b.Block));
            return sorted;
        }
    }
}
