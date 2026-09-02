// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.History.Walk;

internal sealed class MismatchSink
{
    public const int MaxRecorded = 100_000;

    private readonly List<HistoryWalkMismatch> _mismatches = [];

    public void Add(in HistoryWalkMismatch mismatch)
    {
        lock (_mismatches)
        {
            if (_mismatches.Count < MaxRecorded) _mismatches.Add(mismatch);
        }
    }

    public void AddRange(List<HistoryWalkMismatch> mismatches)
    {
        lock (_mismatches)
        {
            int room = MaxRecorded - _mismatches.Count;
            if (room <= 0) return;

            _mismatches.AddRange(mismatches.Count <= room ? mismatches : mismatches.GetRange(0, room));
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
