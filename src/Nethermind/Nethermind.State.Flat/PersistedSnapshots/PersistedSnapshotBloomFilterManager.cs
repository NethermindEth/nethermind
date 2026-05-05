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
    /// <see cref="BloomEntry.ParentState"/>; each slot whose existing bloom covers a
    /// strictly wider range is skipped (the existing entry already supersedes the
    /// incoming bloom). If the chain is not populated for a key, registration stops
    /// — base-snapshot inserts are the only writers that may add a new slot, so
    /// inserting here would break future chain walks. The caller's creation lease
    /// is released by this method.
    /// </summary>
    public void Register(PersistedSnapshotBloom bloom)
    {
        long fromBlock = bloom.From.BlockNumber;
        long newRange = bloom.To.BlockNumber - fromBlock;
        bool isBase = newRange == 1;
        StateId cur = bloom.To;

        while (cur.BlockNumber > fromBlock)
        {
            if (_blooms.TryGetValue(cur, out BloomEntry existing))
            {
                long existingRange = existing.Bloom.To.BlockNumber - existing.Bloom.From.BlockNumber;
                if (existingRange > newRange)
                {
                    // Existing entry already covers a wider range — leave it in place.
                    cur = existing.ParentState;
                    continue;
                }
                // TryAcquire — not AcquireLease: a concurrent prune/dispose may have
                // released the bloom we are trying to register before we finished
                // walking. On failure, abandon the rest of the registration (the
                // bloom is dead — there is nothing useful to insert).
                if (!bloom.TryAcquire()) return;
                if (!_blooms.TryUpdate(cur, new BloomEntry(bloom, existing.ParentState), existing))
                {
                    bloom.Dispose(); // lost CAS, undo the lease and retry the same key
                    continue;
                }
                existing.Bloom.Dispose();
                cur = existing.ParentState;
            }
            else
            {
                if (!isBase)
                {
                    // Compacted register on an unpopulated key: stop without inserting.
                    // Inserting here would break the parent-state chain that future
                    // compactions rely on.
                    break;
                }
                if (!bloom.TryAcquire()) return;
                if (_blooms.TryAdd(cur, new BloomEntry(bloom, bloom.From)))
                    break;
                bloom.Dispose(); // raced with a concurrent insert; retry via the update path
            }
        }

        bloom.Dispose(); // creation lease
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
        foreach (KeyValuePair<StateId, BloomEntry> kv in _blooms)
            kv.Value.Bloom.Dispose();
        _blooms.Clear();
    }
}
