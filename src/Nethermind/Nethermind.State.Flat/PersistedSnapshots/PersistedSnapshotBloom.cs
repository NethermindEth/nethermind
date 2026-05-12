// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Utils;
using Nethermind.State.Flat.Persistence.BloomFilter;

namespace Nethermind.State.Flat.PersistedSnapshots;

/// <summary>
/// Refcounted wrapper holding the key + trie blooms that cover a single state range
/// (<see cref="From"/>, <see cref="To"/>]. Owned by
/// <see cref="PersistedSnapshotBloomFilterManager"/>; the manager and any read-side
/// lessees each hold one lease, so the underlying <see cref="BloomFilter"/>s are
/// only released when every slot and every reader has disposed its lease.
///
/// On construction/cleanup the wrapper updates
/// <see cref="Metrics.PersistedSnapshotKeyBloomMemory"/> and
/// <see cref="Metrics.PersistedSnapshotTrieBloomMemory"/> incrementally, so the
/// gauges always reflect the live bloom set without a polling pass.
/// </summary>
public sealed class PersistedSnapshotBloom : RefCountingDisposable
{
    public BloomFilter KeyBloom { get; }
    public BloomFilter TrieBloom { get; }
    public StateId From { get; }
    public StateId To { get; }

    public PersistedSnapshotBloom(StateId from, StateId to, BloomFilter keyBloom, BloomFilter trieBloom)
    {
        From = from;
        To = to;
        KeyBloom = keyBloom;
        TrieBloom = trieBloom;
        Interlocked.Add(ref Metrics._persistedSnapshotKeyBloomMemory, keyBloom.DataBytes);
        Interlocked.Add(ref Metrics._persistedSnapshotTrieBloomMemory, trieBloom.DataBytes);
    }

    /// <summary>Lease for an additional concurrent user. Returns false if already disposed.</summary>
    public bool TryAcquire() => TryAcquireLease();

    public long KeyBloomCount => KeyBloom.Count;

    protected override void CleanUp()
    {
        Interlocked.Add(ref Metrics._persistedSnapshotKeyBloomMemory, -KeyBloom.DataBytes);
        Interlocked.Add(ref Metrics._persistedSnapshotTrieBloomMemory, -TrieBloom.DataBytes);
        KeyBloom.Dispose();
        TrieBloom.Dispose();
    }

    private static readonly PersistedSnapshotBloom s_alwaysTrue = CreateAlwaysTrue();

    /// <summary>
    /// Sentinel whose <see cref="BloomFilter.MightContain"/> returns true for every
    /// query. Used when the manager has no entry for a snapshot's <c>To</c> (race
    /// against compaction/prune, or never-registered). The instance is initialised
    /// with a lease count high enough that <see cref="RefCountingDisposable.CleanUp"/>
    /// can never run, so its underlying <see cref="BloomFilter"/>s live forever.
    /// </summary>
    public static PersistedSnapshotBloom AlwaysTrue => s_alwaysTrue;

    private static PersistedSnapshotBloom CreateAlwaysTrue()
    {
        // Saturate two minimum-size (1-block, 64B) bloom filters so every probe hits.
        BloomFilter keyBloom = new(capacity: 1, bitsPerKey: 1.0);
        BloomFilter trieBloom = new(capacity: 1, bitsPerKey: 1.0);
        SaturateAllBits(keyBloom);
        SaturateAllBits(trieBloom);
        PersistedSnapshotBloom sentinel = new(StateId.PreGenesis, StateId.PreGenesis, keyBloom, trieBloom);
        // Set leases very high so all decrement paths never reach zero.
        // Direct field write is safe here: this is called inside the static
        // initialiser before any thread has access to the instance.
        sentinel._leases.Value = long.MaxValue / 2;
        return sentinel;
    }

    private static unsafe void SaturateAllBits(BloomFilter bloom)
    {
        byte* data = bloom.DangerousGetDataPointer();
        for (long i = 0; i < bloom.DataBytes; i++) data[i] = 0xFF;
    }
}
