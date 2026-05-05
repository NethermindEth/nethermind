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
/// </summary>
public sealed class PersistedSnapshotBloomFilterManager : IDisposable
{
    private readonly ConcurrentDictionary<StateId, PersistedSnapshotBloom> _blooms = new();
    private readonly Lock _writeLock = new();

    /// <summary>
    /// Register a bloom covering (<paramref name="bloom"/>.From, <paramref name="bloom"/>.To].
    /// Every existing slot whose key falls in that range is replaced with
    /// <paramref name="bloom"/>; one lease is acquired on <paramref name="bloom"/> per slot,
    /// and one lease is released on each evicted entry.
    /// </summary>
    /// <remarks>
    /// The caller's "creation" lease is consumed by this method — i.e. the bloom must
    /// be passed in with refcount = 1 (the count from its constructor). If no slot is
    /// claimed, the bloom is disposed.
    /// </remarks>
    public void Register(PersistedSnapshotBloom bloom)
    {
        long fromBlock = bloom.From.BlockNumber;
        long toBlock = bloom.To.BlockNumber;

        lock (_writeLock)
        {
            bool selfSlotAssigned = false;

            // Snapshot keys first so we can mutate during iteration.
            using ArrayPoolList<StateId> existing = new(_blooms.Count);
            foreach (KeyValuePair<StateId, PersistedSnapshotBloom> kv in _blooms) existing.Add(kv.Key);

            foreach (StateId key in existing)
            {
                long k = key.BlockNumber;
                if (k <= fromBlock || k > toBlock) continue;
                if (!_blooms.TryGetValue(key, out PersistedSnapshotBloom? prev)) continue;
                bloom.TryAcquire();
                _blooms[key] = bloom;
                prev.Dispose();
                if (key == bloom.To) selfSlotAssigned = true;
            }

            if (!selfSlotAssigned)
            {
                bloom.TryAcquire();
                if (_blooms.TryGetValue(bloom.To, out PersistedSnapshotBloom? prev))
                {
                    _blooms[bloom.To] = bloom;
                    prev.Dispose();
                }
                else
                {
                    _blooms[bloom.To] = bloom;
                }
            }

            // Release the caller's creation lease. Slot leases acquired above keep the
            // bloom alive.
            bloom.Dispose();
        }
    }

    /// <summary>
    /// Lease the bloom keyed by <paramref name="to"/>. Acquires an additional lease for
    /// the caller. Returns <see cref="PersistedSnapshotBloom.AlwaysTrue"/> on miss.
    /// </summary>
    public PersistedSnapshotBloom LeaseOrSentinel(StateId to)
    {
        if (_blooms.TryGetValue(to, out PersistedSnapshotBloom? bloom) && bloom.TryAcquire())
            return bloom;
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
            foreach (KeyValuePair<StateId, PersistedSnapshotBloom> kv in _blooms)
            {
                if (kv.Key.BlockNumber < stateId.BlockNumber) toRemove.Add(kv.Key);
            }
            foreach (StateId key in toRemove)
            {
                if (_blooms.TryRemove(key, out PersistedSnapshotBloom? bloom))
                {
                    bloom.Dispose();
                    pruned++;
                }
            }
            return pruned;
        }
    }

    public long TotalKeyBloomBytes
    {
        get
        {
            // Distinct instances only — the same bloom may live in many slots.
            HashSet<PersistedSnapshotBloom> seen = new(ReferenceEqualityComparer.Instance);
            long total = 0;
            foreach (KeyValuePair<StateId, PersistedSnapshotBloom> kv in _blooms)
            {
                if (seen.Add(kv.Value)) total += kv.Value.KeyBloomBytes;
            }
            return total;
        }
    }

    public long TotalTrieBloomBytes
    {
        get
        {
            HashSet<PersistedSnapshotBloom> seen = new(ReferenceEqualityComparer.Instance);
            long total = 0;
            foreach (KeyValuePair<StateId, PersistedSnapshotBloom> kv in _blooms)
            {
                if (seen.Add(kv.Value)) total += kv.Value.TrieBloomBytes;
            }
            return total;
        }
    }

    public void Dispose()
    {
        lock (_writeLock)
        {
            foreach (KeyValuePair<StateId, PersistedSnapshotBloom> kv in _blooms)
                kv.Value.Dispose();
            _blooms.Clear();
        }
    }
}
