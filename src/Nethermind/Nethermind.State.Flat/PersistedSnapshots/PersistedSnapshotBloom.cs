// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Utils;
using Nethermind.State.Flat.Persistence.BloomFilter;

namespace Nethermind.State.Flat.PersistedSnapshots;

/// <summary>
/// Refcounted wrapper holding the single bloom that covers a state range
/// (<see cref="From"/>, <see cref="To"/>]. The bloom carries every key type
/// (address / slot / self-destruct / state-trie path / storage-trie path)
/// in one filter — query call sites compute the type-specific hash and probe
/// this one <see cref="Bloom"/>. Owned by
/// <see cref="PersistedSnapshotBloomFilterManager"/>; the manager and any
/// read-side lessees each hold one lease, so the underlying
/// <see cref="BloomFilter"/> is only released when every slot and every reader
/// has disposed its lease.
///
/// On construction/cleanup the wrapper updates
/// <see cref="Metrics.PersistedSnapshotBloomMemory"/> incrementally, so the
/// gauge always reflects the live bloom set without a polling pass.
/// </summary>
public sealed class PersistedSnapshotBloom : RefCountingDisposable
{
    public BloomFilter Bloom { get; }
    public StateId From { get; }
    public StateId To { get; }

    public PersistedSnapshotBloom(StateId from, StateId to, BloomFilter bloom)
    {
        From = from;
        To = to;
        Bloom = bloom;
        Interlocked.Add(ref Metrics._persistedSnapshotBloomMemory, bloom.DataBytes);
    }

    /// <remarks>
    /// When <paramref name="immortal"/> is true, the lease counter is initialised to a
    /// value high enough that no realistic Acquire/Release sequence can reach zero, so
    /// <see cref="CleanUp"/> will never run. Used for the <see cref="AlwaysTrue"/>
    /// sentinel; not exposed publicly.
    /// </remarks>
    private PersistedSnapshotBloom(StateId from, StateId to, BloomFilter bloom, bool immortal)
        : this(from, to, bloom)
    {
        if (immortal)
        {
            // Direct field write is safe here: this constructor is invoked only from the
            // static initialiser for s_alwaysTrue, before any thread has access to the instance.
            _leases.Value = long.MaxValue / 2;
        }
    }

    /// <summary>Lease for an additional concurrent user. Returns false if already disposed.</summary>
    public bool TryAcquire() => TryAcquireLease();

    public long BloomCount => Bloom.Count;

    protected override void CleanUp()
    {
        Interlocked.Add(ref Metrics._persistedSnapshotBloomMemory, -Bloom.DataBytes);
        Bloom.Dispose();
    }

    private static readonly PersistedSnapshotBloom s_alwaysTrue = CreateAlwaysTrue();

    /// <summary>
    /// Sentinel whose <see cref="BloomFilter.MightContain"/> returns true for every
    /// query. Used when the manager has no entry for a snapshot's <c>To</c> (race
    /// against compaction/prune, or never-registered). The instance is initialised
    /// with a lease count high enough that <see cref="RefCountingDisposable.CleanUp"/>
    /// can never run, so its underlying <see cref="BloomFilter"/> lives forever.
    /// </summary>
    public static PersistedSnapshotBloom AlwaysTrue => s_alwaysTrue;

    private static PersistedSnapshotBloom CreateAlwaysTrue() =>
        new(StateId.PreGenesis, StateId.PreGenesis, BloomFilter.AlwaysTrue(), immortal: true);
}
