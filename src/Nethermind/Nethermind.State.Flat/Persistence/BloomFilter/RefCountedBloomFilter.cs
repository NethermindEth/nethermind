// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Utils;

namespace Nethermind.State.Flat.Persistence.BloomFilter;

/// <summary>
/// Ref-counted owner of a single <see cref="BloomFilter"/>. The wrapped native filter is disposed — and
/// its contribution to <see cref="Metrics.PersistedSnapshotBloomMemory"/> reversed — only once every lease
/// has been released, so one filter can back several <see cref="PersistedSnapshots.PersistedSnapshot"/>s.
/// </summary>
/// <remarks>
/// A large compaction adopts its merged bloom as the (superset) pre-filter of every snapshot it contains:
/// each contained snapshot is re-registered as a twin holding a lease on this wrapper, and the filter
/// survives until the big snapshot and all twins (and their in-flight readers) drain. Keeping the lease
/// count out of <see cref="BloomFilter"/> leaves that type a pure data structure.
/// </remarks>
public sealed class RefCountedBloomFilter : SmallRefCountingDisposable
{
    private readonly BloomFilter _filter;

    public RefCountedBloomFilter(BloomFilter filter)
    {
        _filter = filter;
        Interlocked.Add(ref Metrics._persistedSnapshotBloomMemory, filter.DataBytes);
    }

    /// <summary>A freshly-owned <see cref="BloomFilter.AlwaysTrue"/> sentinel — correct (no false
    /// negatives) but unfiltered — for snapshots whose real bloom is built later (the placeholder
    /// snapshot is then re-registered carrying that bloom).</summary>
    public static RefCountedBloomFilter AlwaysTrue() => new(BloomFilter.AlwaysTrue());

    /// <summary>The wrapped filter. Valid for as long as the caller holds a lease on this wrapper.</summary>
    public BloomFilter Filter => _filter;

    protected override void CleanUp()
    {
        Interlocked.Add(ref Metrics._persistedSnapshotBloomMemory, -_filter.DataBytes);
        _filter.Dispose();
    }
}
