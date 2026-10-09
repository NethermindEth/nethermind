// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;

namespace Nethermind.State.Flat.History.Walk;

internal sealed class StorageGroupFrontier(MismatchSink found, int checkpointGroups, Action<uint> checkpoint)
{
    private readonly object _lock = new();
    private readonly ConcurrentDictionary<long, (uint Prefix, MismatchSink Found)> _pending = [];
    private readonly HashSet<long> _completed = [];
    private long _issued = -1;
    private long _frontier = -1;
    private int _sinceCheckpoint;

    public long Issue(uint prefix, MismatchSink groupFound)
    {
        long sequence = Interlocked.Increment(ref _issued);
        _pending[sequence] = (prefix, groupFound);
        return sequence;
    }

    public void Complete(long sequence)
    {
        lock (_lock)
        {
            _completed.Add(sequence);
            while (_completed.Remove(_frontier + 1))
            {
                _frontier++;
                if (!_pending.TryRemove(_frontier, out (uint Prefix, MismatchSink Found) group)) throw new InvalidOperationException($"Storage group {_frontier} completed without having been issued.");

                found.AddRange(group.Found);
                if (++_sinceCheckpoint < checkpointGroups) continue;

                _sinceCheckpoint = 0;
                checkpoint(group.Prefix);
            }
        }
    }
}
