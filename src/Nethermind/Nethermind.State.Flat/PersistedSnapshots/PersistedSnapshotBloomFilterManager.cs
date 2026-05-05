// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Nethermind.Core.Collections;

namespace Nethermind.State.Flat.PersistedSnapshots;

/// <summary>
/// Stores the bloom filters for persisted snapshots, keyed by <see cref="StateId"/>.
/// Each registered <see cref="PersistedSnapshotBloom"/> may be pointed to by many
/// dictionary slots — every slot owns one independent lease, so eviction or read-side
/// release of one slot does not tear the bloom down while other slots still reference
/// it.
///
/// Each entry carries a <see cref="BloomEntry.ParentState"/> link to its immediate
/// predecessor so a compacted-bloom registration can walk the chain from <c>To</c>
/// back to <c>From</c> one slot at a time, instead of scanning every key.
/// </summary>
public sealed class PersistedSnapshotBloomFilterManager : IDisposable
{
    private readonly ConcurrentDictionary<StateId, BloomEntry> _blooms = new();
    private readonly Lock _writeLock = new();

    /// <summary>
    /// One slot in the registry: the bloom plus the predecessor <see cref="StateId"/>.
    /// For a base-snapshot slot at block N+1, <see cref="ParentState"/> is the
    /// <c>From</c> state at block N — i.e. the parent in the per-slot chain. The
    /// chain is preserved across compactions so a future register can walk it.
    /// </summary>
    private readonly struct BloomEntry(PersistedSnapshotBloom bloom, StateId parentState)
    {
        public PersistedSnapshotBloom Bloom { get; } = bloom;
        public StateId ParentState { get; } = parentState;
    }

    /// <summary>
    /// Register a bloom covering (<paramref name="bloom"/>.From, <paramref name="bloom"/>.To].
    /// For a base snapshot (range size 1) only the <c>To</c> slot is set, with
    /// <see cref="BloomEntry.ParentState"/> = <paramref name="bloom"/>.From. For a
    /// compacted snapshot the chain is walked from <c>To</c> backwards via
    /// <see cref="BloomEntry.ParentState"/>, replacing each slot until block-number
    /// crosses <c>From</c>; each replaced slot keeps its original predecessor link.
    /// One lease is acquired per slot; the caller's creation lease is released here.
    /// </summary>
    public void Register(PersistedSnapshotBloom bloom)
    {
        long fromBlock = bloom.From.BlockNumber;
        long toBlock = bloom.To.BlockNumber;
        long rangeSize = toBlock - fromBlock;

        lock (_writeLock)
        {
            if (rangeSize == 1)
            {
                AssignSlot(bloom.To, bloom, parentState: bloom.From);
            }
            else
            {
                StateId cur = bloom.To;
                while (cur.BlockNumber > fromBlock)
                {
                    if (!_blooms.TryGetValue(cur, out BloomEntry prev))
                    {
                        // Chain not yet populated for this key (e.g. registered out of
                        // order). Insert with ParentState = bloom.From and stop —
                        // caller will repopulate intermediate slots when those base
                        // snapshots register.
                        AssignSlot(cur, bloom, parentState: bloom.From);
                        break;
                    }
                    AssignSlot(cur, bloom, parentState: prev.ParentState);
                    cur = prev.ParentState;
                }
            }

            // Release the caller's creation lease. Slot leases acquired in
            // AssignSlot keep the bloom alive.
            bloom.Dispose();
        }
    }

    /// <summary>
    /// Replace <c>_blooms[key]</c> with <paramref name="bloom"/>, acquiring one new
    /// lease and disposing any previous slot's bloom lease.
    /// </summary>
    private void AssignSlot(StateId key, PersistedSnapshotBloom bloom, StateId parentState)
    {
        bloom.TryAcquire();
        if (_blooms.TryGetValue(key, out BloomEntry prev))
        {
            _blooms[key] = new BloomEntry(bloom, parentState);
            prev.Bloom.Dispose();
        }
        else
        {
            _blooms[key] = new BloomEntry(bloom, parentState);
        }
    }

    /// <summary>
    /// Lease the bloom keyed by <paramref name="to"/>. Acquires an additional lease for
    /// the caller. Returns <see cref="PersistedSnapshotBloom.AlwaysTrue"/> on miss.
    /// </summary>
    public PersistedSnapshotBloom LeaseOrSentinel(StateId to)
    {
        if (_blooms.TryGetValue(to, out BloomEntry entry) && entry.Bloom.TryAcquire())
            return entry.Bloom;
        return PersistedSnapshotBloom.AlwaysTrue;
    }

    /// <summary>
    /// Drop every slot whose <c>To.BlockNumber</c> is strictly less than
    /// <paramref name="stateId"/>'s, releasing one lease per slot. Mirrors
    /// <see cref="PersistedSnapshotRepository.PruneBefore"/>.
    /// </summary>
    public int PruneBefore(StateId stateId)
    {
        lock (_writeLock)
        {
            int pruned = 0;
            using ArrayPoolList<StateId> toRemove = new(0);
            foreach (KeyValuePair<StateId, BloomEntry> kv in _blooms)
            {
                if (kv.Key.BlockNumber < stateId.BlockNumber) toRemove.Add(kv.Key);
            }
            foreach (StateId key in toRemove)
            {
                if (_blooms.TryRemove(key, out BloomEntry entry))
                {
                    entry.Bloom.Dispose();
                    pruned++;
                }
            }
            return pruned;
        }
    }

    public long TotalKeyBloomBytes => SumDistinctBytes(static b => b.KeyBloomBytes);
    public long TotalTrieBloomBytes => SumDistinctBytes(static b => b.TrieBloomBytes);

    private long SumDistinctBytes(Func<PersistedSnapshotBloom, long> selector)
    {
        // Distinct instances only — the same bloom may live in many slots.
        HashSet<PersistedSnapshotBloom> seen = new(ReferenceEqualityComparer.Instance);
        long total = 0;
        foreach (KeyValuePair<StateId, BloomEntry> kv in _blooms)
        {
            if (seen.Add(kv.Value.Bloom)) total += selector(kv.Value.Bloom);
        }
        return total;
    }

    public void Dispose()
    {
        lock (_writeLock)
        {
            foreach (KeyValuePair<StateId, BloomEntry> kv in _blooms)
                kv.Value.Bloom.Dispose();
            _blooms.Clear();
        }
    }
}
