// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Utils;
using Nethermind.Core.Collections;

namespace Nethermind.State.Flat.Persistence;

/// <summary>
/// Ref-counted owner of a single <see cref="Nethermind.Core.Collections.BloomFilter"/>. The wrapped native filter is disposed — and
/// its contribution to <see cref="Metrics.PersistedSnapshotBloomMemory"/> reversed — only once every lease
/// has been released, so one filter can back several <see cref="PersistedSnapshots.PersistedSnapshot"/>s.
/// </summary>
/// <remarks>
/// A large compaction adopts its merged bloom as the (superset) pre-filter of every snapshot it contains:
/// each contained snapshot is re-registered as a twin holding a lease on this wrapper, and the filter
/// survives until the big snapshot and all twins (and their in-flight readers) drain. Keeping the lease
/// count out of <see cref="Nethermind.Core.Collections.BloomFilter"/> leaves that type a pure data structure.
/// </remarks>
public sealed class RefCountedBloomFilter : SmallRefCountingDisposable
{
    private readonly Nethermind.Core.Collections.BloomFilter _filter;

    public RefCountedBloomFilter(Nethermind.Core.Collections.BloomFilter filter)
    {
        _filter = filter;
        Interlocked.Add(ref Metrics._persistedSnapshotBloomMemory, filter.DataBytes);
        Interlocked.Increment(ref Metrics._persistedSnapshotBloomCount);
    }

    /// <summary>A freshly-owned <see cref="Nethermind.Core.Collections.BloomFilter.AlwaysTrue"/> sentinel — correct (no false
    /// negatives) but unfiltered — for snapshots whose real bloom is built later (the placeholder
    /// snapshot is then re-registered carrying that bloom).</summary>
    public static RefCountedBloomFilter AlwaysTrue() => new(Nethermind.Core.Collections.BloomFilter.AlwaysTrue());

    /// <summary>The wrapped filter. Valid for as long as the caller holds a lease on this wrapper.</summary>
    public Nethermind.Core.Collections.BloomFilter Filter => _filter;

    protected override void CleanUp()
    {
        Interlocked.Add(ref Metrics._persistedSnapshotBloomMemory, -_filter.DataBytes);
        Interlocked.Decrement(ref Metrics._persistedSnapshotBloomCount);
        _filter.Dispose();
    }
}
