// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Pbt;
using IResettable = Nethermind.Core.Resettables.IResettable;

namespace Nethermind.State.Pbt;

/// <summary>
/// The leaf blobs a bundle has read out of the shared view below it, kept for the rest of the block so
/// that the flat reads and the root fold of one stem share a single fetch.
/// </summary>
/// <remarks>
/// This is a cache, not a tier: it holds only what <see cref="PbtReadOnlySnapshotBundle"/> answered, which
/// is immutable for the bundle's lifetime, and it is consulted below the tiers that shadow it — so an entry
/// can never be stale, only superseded. A present <see langword="null"/> is a cached absence, which is as
/// final as any other answer of that view.
/// <para>
/// Concurrent because a block reads from as many threads as it processes storage on. Pooled per
/// <see cref="PbtResourcePool.Usage"/>, so an instance backs exactly one bundle at a time and must not be
/// touched once returned. Each non-null entry is owned through one lease of its own, released when the
/// entry is removed or the cache is reset.
/// </para>
/// </remarks>
public sealed class PbtLeafBlobCache : IDisposable, IResettable
{
    private readonly ConcurrentDictionary<Stem, RefCountingMemory?> _leafBlobs = new();

    internal IReadOnlyDictionary<Stem, RefCountingMemory?> LeafBlobs => _leafBlobs;

    /// <summary>Returns whether the stem is cached and acquires a lease on a non-null blob.</summary>
    public bool TryGet(in Stem stem, out RefCountingMemory? blob)
    {
        while (_leafBlobs.TryGetValue(stem, out blob))
        {
            if (blob is null) return true;

            try
            {
                blob.AcquireLease();
                if (_leafBlobs.TryGetValue(stem, out RefCountingMemory? current) && ReferenceEquals(blob, current)) return true;
                Release(blob);
            }
            catch (InvalidOperationException)
            {
                // A concurrent removal released the cache's lease before this reader could acquire its
                // own. Retry against what the cache holds now rather than touching released bytes.
            }
        }

        blob = null;
        return false;
    }

    /// <summary>Caches <paramref name="blob"/> under its own lease; the caller keeps theirs.</summary>
    /// <remarks>A concurrent add of the same stem wins or loses harmlessly: whichever lease is not stored is released.</remarks>
    public void Add(in Stem stem, RefCountingMemory? blob)
    {
        blob?.AcquireLease();
        if (!_leafBlobs.TryAdd(stem, blob)) Release(blob);
    }

    /// <summary>Drops the stem from the cache, releasing the lease it was held under.</summary>
    public void Remove(in Stem stem)
    {
        if (_leafBlobs.TryRemove(stem, out RefCountingMemory? blob)) Release(blob);
    }

    /// <remarks>
    /// The lock-free clear is sound only where the block's reading threads have been joined: at the commit
    /// that seals the block, and at a pool-return boundary.
    /// </remarks>
    public void Reset()
    {
        foreach ((_, RefCountingMemory? blob) in _leafBlobs) Release(blob);

        _leafBlobs.NoLockClear();
    }

    private static void Release(RefCountingMemory? memory) => ((IDisposable?)memory)?.Dispose();

    /// <remarks>Releases all retained leases when the pool has no room to retain this cache.</remarks>
    public void Dispose() => Reset();
}
