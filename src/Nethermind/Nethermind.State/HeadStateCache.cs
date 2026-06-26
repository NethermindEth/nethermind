// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;

namespace Nethermind.State;

/// <summary>
/// A cross-block, coherent cache of state at the canonical head (and the last
/// <see cref="Depth"/> ancestors), used to accelerate read-only RPC calls (eth_call, debug_trace…)
/// on top of the head. Account and storage values are direct O(1) lookups, skipping trie traversal.
/// </summary>
/// <remarks>
/// <para>
/// The cache content is kept <b>coherent at the current head</b>: <see cref="HeadStateCacheUpdater"/>
/// advances it by refreshing the keys a new block changed (and flushes on reorg/gap). A small ring of
/// per-block <b>changed-key</b> sets lets a reader pinned to head-k tell whether a key is safe to serve
/// from the head-coherent cache (unchanged across the window ⇒ head value == head-k value) or must be
/// read from the trie at the head-k root.
/// </para>
/// <para>
/// Concurrency uses a generation seqlock: <see cref="Generation"/> is even when stable and odd while the
/// updater mutates the rings/content. A reader snapshots the generation at scope start
/// (<see cref="TrySnapshot"/>); each access re-checks it and falls back to the trie if it changed. This
/// keeps every individual read consistent with the header the reader pinned, even across head advances.
/// </para>
/// </remarks>
public sealed class HeadStateCache
{
    private readonly int _depth;

    private readonly SeqlockCache<AddressAsKey, Account> _accounts;
    private readonly SeqlockCache<StorageCell, byte[]> _storage;

    // Rings, mutated only under an odd generation. Index 0 == current head, index d == head-d.
    private readonly Hash256?[] _headHashes;                 // length depth + 1
    private readonly FrozenSet<AddressAsKey>?[] _changedAccounts; // [d] = accounts changed by block at depth d
    private readonly FrozenSet<StorageCell>?[] _changedSlots;     // [d] = slots changed by block at depth d

    private long _generation;

    // Serializes cache writes (backfill, Advance, Flush) so a backfill that races an Advance is
    // rejected by the generation check instead of publishing a stale value. Reads stay lock-free.
    private readonly Lock _writeLock = new();

    public HeadStateCache(int depth, int accountSetsBits, int storageSetsBits)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(depth, 0);
        _depth = depth;
        _accounts = new SeqlockCache<AddressAsKey, Account>(accountSetsBits);
        _storage = new SeqlockCache<StorageCell, byte[]>(storageSetsBits);
        _headHashes = new Hash256?[depth + 1];
        _changedAccounts = new FrozenSet<AddressAsKey>?[depth];
        _changedSlots = new FrozenSet<StorageCell>?[depth];
    }

    /// <summary>The maximum number of blocks behind the head that can be served (the X in head-X).</summary>
    public int Depth => _depth;

    /// <summary>Even when stable, odd while the updater is applying a block or flushing.</summary>
    public long Generation => Volatile.Read(ref _generation);

    public SeqlockCache<AddressAsKey, Account> Accounts => _accounts;
    public SeqlockCache<StorageCell, byte[]> Storage => _storage;

    /// <summary>The current head block hash, or null before the first head is set.</summary>
    public Hash256? HeadHash => Volatile.Read(ref _headHashes[0]);

    /// <summary>
    /// Attempts to take a read snapshot for a reader pinned to <paramref name="headerHash"/>. Returns
    /// false (the reader must pass through to the trie for the whole scope) when the cache is mid-update,
    /// the header is not the head or one of the tracked ancestors, or the header is null.
    /// </summary>
    public bool TrySnapshot(Hash256? headerHash, out HeadStateSnapshot snapshot)
    {
        snapshot = default;
        if (headerHash is null) return false;

        long gen = Volatile.Read(ref _generation);
        if ((gen & 1) != 0) return false; // mid-update

        int depth = -1;
        for (int d = 0; d <= _depth; d++)
        {
            if (headerHash.Equals(Volatile.Read(ref _headHashes[d])))
            {
                depth = d;
                break;
            }
        }
        if (depth < 0) return false;

        // Snapshot the changed-key sets covering blocks newer than the pinned header (depths 0..depth-1).
        FrozenSet<AddressAsKey>?[]? windowAccounts = null;
        FrozenSet<StorageCell>?[]? windowSlots = null;
        if (depth > 0)
        {
            windowAccounts = new FrozenSet<AddressAsKey>?[depth];
            windowSlots = new FrozenSet<StorageCell>?[depth];
            for (int d = 0; d < depth; d++)
            {
                windowAccounts[d] = Volatile.Read(ref _changedAccounts[d]);
                windowSlots[d] = Volatile.Read(ref _changedSlots[d]);
            }
        }

        // The rings are only swapped under an odd generation; if it is unchanged and still even, the
        // depth and snapshotted sets are a consistent view.
        if (Volatile.Read(ref _generation) != gen) return false;

        snapshot = new HeadStateSnapshot(this, gen, depth, windowAccounts, windowSlots);
        return true;
    }

    /// <summary>Advances the head to a child block, refreshing the keys it changed. Caller must ensure
    /// <paramref name="newHeadHash"/>'s parent is the current head.</summary>
    /// <param name="newHeadHash">The new head block hash.</param>
    /// <param name="changedAccounts">All accounts the block mutated.</param>
    /// <param name="changedSlots">All storage cells the block mutated.</param>
    /// <param name="refresh">Callback that supplies the post-block value for a currently-cached changed key.</param>
    public void Advance(
        Hash256 newHeadHash,
        IReadOnlyCollection<AddressAsKey> changedAccounts,
        IReadOnlyCollection<StorageCell> changedSlots,
        IHeadStateRefresher refresh)
    {
        FrozenSet<AddressAsKey> accountSet = changedAccounts as FrozenSet<AddressAsKey> ?? changedAccounts.ToFrozenSet();
        FrozenSet<StorageCell> slotSet = changedSlots as FrozenSet<StorageCell> ?? changedSlots.ToFrozenSet();

        lock (_writeLock)
        {
            // Phase 1 (may throw — trie reads): gather refreshed values for currently-cached changed
            // keys *before* touching the generation, so a failure leaves the cache untouched and even.
            // The lock blocks concurrent backfills, so the cached set can't grow under us here.
            List<(AddressAsKey Key, Account? Value)> accountUpdates = [];
            foreach (AddressAsKey address in accountSet)
            {
                if (_accounts.TryGetValue(in address, out _))
                {
                    accountUpdates.Add((address, refresh.GetAccount(address.Value)));
                }
            }
            List<(StorageCell Key, byte[] Value)> slotUpdates = [];
            foreach (StorageCell cell in slotSet)
            {
                StorageCell key = cell;
                if (_storage.TryGetValue(in key, out _))
                {
                    slotUpdates.Add((key, refresh.GetStorage(in key)));
                }
            }

            // Phase 2 (no throw — in-memory publish): readers see odd generation and pass through.
            Interlocked.Increment(ref _generation);
            try
            {
                foreach ((AddressAsKey key, Account? value) in accountUpdates) _accounts.Set(in key, value);
                foreach ((StorageCell key, byte[] value) in slotUpdates) _storage.Set(in key, value);
                ShiftRings(newHeadHash, accountSet, slotSet);
            }
            finally
            {
                Interlocked.Increment(ref _generation); // always return to even, even on the unexpected
            }
        }
    }

    /// <summary>Drops all cached content and the ancestor window, re-anchoring at <paramref name="newHeadHash"/>.
    /// Used on the first head, a reorg, or a non-sequential head jump.</summary>
    public void Flush(Hash256 newHeadHash)
    {
        lock (_writeLock)
        {
            Interlocked.Increment(ref _generation); // -> odd
            try
            {
                _accounts.Clear();
                _storage.Clear();
                for (int d = 0; d < _depth; d++)
                {
                    Volatile.Write(ref _changedAccounts[d], null);
                    Volatile.Write(ref _changedSlots[d], null);
                }
                Volatile.Write(ref _headHashes[0], newHeadHash);
                for (int d = 1; d <= _depth; d++)
                {
                    Volatile.Write(ref _headHashes[d], null);
                }
            }
            finally
            {
                Interlocked.Increment(ref _generation); // -> even
            }
        }
    }

    /// <summary>
    /// Backfills a value read at scope start, but only if no <see cref="Advance"/>/<see cref="Flush"/>
    /// has happened since (generation unchanged). Run under the write lock so the check-and-set is atomic
    /// against head changes — this prevents publishing a stale value for a key the new head changed.
    /// </summary>
    public void TryBackfillAccount(in AddressAsKey key, Account? value, long expectedGeneration)
    {
        // Non-blocking: never stall an RPC read behind an in-progress Advance/Flush. Skipping just means
        // the value isn't cached this time; the next read re-attempts.
        if (!_writeLock.TryEnter()) return;
        try
        {
            if (Volatile.Read(ref _generation) == expectedGeneration) _accounts.Set(in key, value);
        }
        finally
        {
            _writeLock.Exit();
        }
    }

    /// <inheritdoc cref="TryBackfillAccount"/>
    public void TryBackfillStorage(in StorageCell key, byte[] value, long expectedGeneration)
    {
        if (!_writeLock.TryEnter()) return;
        try
        {
            if (Volatile.Read(ref _generation) == expectedGeneration) _storage.Set(in key, value);
        }
        finally
        {
            _writeLock.Exit();
        }
    }

    private void ShiftRings(Hash256 newHeadHash, FrozenSet<AddressAsKey> accountSet, FrozenSet<StorageCell> slotSet)
    {
        for (int d = _depth; d >= 1; d--)
        {
            Volatile.Write(ref _headHashes[d], Volatile.Read(ref _headHashes[d - 1]));
        }
        Volatile.Write(ref _headHashes[0], newHeadHash);

        for (int d = _depth - 1; d >= 1; d--)
        {
            Volatile.Write(ref _changedAccounts[d], Volatile.Read(ref _changedAccounts[d - 1]));
            Volatile.Write(ref _changedSlots[d], Volatile.Read(ref _changedSlots[d - 1]));
        }
        if (_depth > 0)
        {
            Volatile.Write(ref _changedAccounts[0], accountSet);
            Volatile.Write(ref _changedSlots[0], slotSet);
        }
    }
}

/// <summary>Supplies post-block values for keys the <see cref="HeadStateCache"/> needs to refresh.</summary>
public interface IHeadStateRefresher
{
    Account? GetAccount(Address address);
    byte[] GetStorage(in StorageCell cell);
}

/// <summary>
/// A consistent read view captured by <see cref="HeadStateCache.TrySnapshot"/>. Tells the scope
/// provider, for a given key, whether the head-coherent cache may answer the read.
/// </summary>
public readonly struct HeadStateSnapshot
{
    private readonly HeadStateCache _cache;
    private readonly FrozenSet<AddressAsKey>?[]? _windowAccounts;
    private readonly FrozenSet<StorageCell>?[]? _windowSlots;

    internal HeadStateSnapshot(
        HeadStateCache cache,
        long generation,
        int depth,
        FrozenSet<AddressAsKey>?[]? windowAccounts,
        FrozenSet<StorageCell>?[]? windowSlots)
    {
        _cache = cache;
        Generation = generation;
        Depth = depth;
        _windowAccounts = windowAccounts;
        _windowSlots = windowSlots;
    }

    public long Generation { get; }
    public int Depth { get; }

    /// <summary>True if the cache generation still matches the one captured at scope start.</summary>
    public bool IsCurrent => _cache.Generation == Generation;

    /// <summary>True if the account may have changed between the pinned header and the head, so the
    /// head-coherent cache must not be consulted/populated for it.</summary>
    public bool ChangedInWindow(in AddressAsKey address)
    {
        FrozenSet<AddressAsKey>?[]? window = _windowAccounts;
        if (window is null) return false;
        for (int d = 0; d < window.Length; d++)
        {
            if (window[d]?.Contains(address) == true) return true;
        }
        return false;
    }

    /// <inheritdoc cref="ChangedInWindow(in AddressAsKey)"/>
    public bool ChangedInWindow(in StorageCell cell)
    {
        FrozenSet<StorageCell>?[]? window = _windowSlots;
        if (window is null) return false;
        for (int d = 0; d < window.Length; d++)
        {
            if (window[d]?.Contains(cell) == true) return true;
        }
        return false;
    }
}
