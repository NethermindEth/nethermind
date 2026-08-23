// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Threading;
using Collections.Pooled;
using Nethermind.Core;
using Nethermind.Core.Collections;

namespace Nethermind.Evm.State;

public class PreBlockCaches
{
    private readonly Func<CacheType>[] _clearCaches;

    private readonly SeqlockCache<StorageCell, byte[]> _storageCache;
    private readonly SeqlockCache<AddressAsKey, Account> _stateCache = new();
    private readonly PrecompileCaches _precompileCaches;
    private volatile IWorldStateScopeProvider.IScope? _mainScope;

    [ThreadStatic]
    private static StorageReadCapture? _currentStorageReadCapture;

    public PreBlockCaches() : this(new PreBlockCachesConfig(), PrecompileCaches.Empty) { }

    public PreBlockCaches(PreBlockCachesConfig config, PrecompileCaches precompileCaches)
    {
        _storageCache = new SeqlockCache<StorageCell, byte[]>(config.StorageCacheSetsBits);
        _precompileCaches = precompileCaches;
        _clearCaches =
        [
            () => { _storageCache.Clear(); return CacheType.None; },
            () => { _stateCache.Clear(); return CacheType.None; },
            () => { _precompileCaches.ClearBlockCache(); return CacheType.None; }
        ];
    }

    public SeqlockCache<StorageCell, byte[]> StorageCache => _storageCache;
    public SeqlockCache<AddressAsKey, Account> StateCache => _stateCache;

    /// <summary>
    /// The main processing scope, registered for its lifetime as the target of trie warm-up hints
    /// (<see cref="IWorldStateScopeProvider.IScope.HintWarmAccount"/>); may disappear at any time.
    /// </summary>
    public IWorldStateScopeProvider.IScope? MainScope
    {
        get => _mainScope;
        set => _mainScope = value;
    }

    /// <summary>
    /// Starts a thread-local capture of backing-store storage misses made through this block cache.
    /// </summary>
    /// <remarks>
    /// While a capture is active, callers record each missed storage cell and use a speculative placeholder
    /// instead of reading the backing store. The speculative execution result must not be consumed.
    /// </remarks>
    /// <param name="remainingCells">
    /// Cell budget consumed by every distinct recorded cell; recording stops once it is exhausted, while reads
    /// past the cap still get the placeholder. Share one box across concurrent captures to bound their aggregate.
    /// </param>
    /// <exception cref="InvalidOperationException">A capture is already active on this thread.</exception>
    public StorageReadCapture BeginStorageReadCapture(StrongBox<int> remainingCells)
    {
        if (_currentStorageReadCapture is not null)
        {
            throw new InvalidOperationException("Storage-read captures must not nest; the previous capture would be orphaned.");
        }

        return _currentStorageReadCapture = new StorageReadCapture(this, remainingCells);
    }

    /// <summary>The capture active on the current thread for this cache, if any.</summary>
    public StorageReadCapture? CurrentStorageReadCapture
    {
        get
        {
            StorageReadCapture? capture = _currentStorageReadCapture;
            return capture is not null && ReferenceEquals(capture.Owner, this) ? capture : null;
        }
    }

    public CacheType ClearCaches()
    {
        CacheType isDirty = CacheType.None;
        foreach (Func<CacheType> clearCache in _clearCaches)
        {
            isDirty |= clearCache();
        }

        return isDirty;
    }

    /// <summary>
    /// A synchronous, thread-local storage-read capture. Not thread-safe: record and dispose only on the
    /// thread where it was created.
    /// </summary>
    public sealed class StorageReadCapture : IDisposable
    {
        // Covers the modal candidate; genuinely heavy captures grow in a few doublings.
        private const int InitialCellCapacity = 256;

        private readonly PooledSet<StorageCell> _cells;
        private readonly int _ownerThreadId = Environment.CurrentManagedThreadId;
        private readonly StrongBox<int> _remainingCells;
        private bool _disposed;

        internal StorageReadCapture(PreBlockCaches owner, StrongBox<int> remainingCells)
        {
            Owner = owner;
            _remainingCells = remainingCells;
            _cells = new PooledSet<StorageCell>(Math.Clamp(remainingCells.Value, 0, InitialCellCapacity));
        }

        internal PreBlockCaches Owner { get; }

        /// <summary>Distinct cells encountered while backing reads were skipped.</summary>
        /// <remarks>Exposed as the concrete pooled set so consumers enumerate without boxing; treat as read-only.</remarks>
        public PooledSet<StorageCell> Cells => _cells;

        /// <summary>Records a missed storage cell while the shared cell budget lasts.</summary>
        /// <remarks>The budget gate is approximate under concurrent captures (a small transient overshoot is possible); callers needing a hard bound must clamp when consuming <see cref="Cells"/>.</remarks>
        /// <exception cref="InvalidOperationException">Called from a thread other than the one that created the capture.</exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Record(in StorageCell storageCell)
        {
            if (Environment.CurrentManagedThreadId != _ownerThreadId)
            {
                throw new InvalidOperationException("A capture must only record on the thread that created it.");
            }

            if (Volatile.Read(ref _remainingCells.Value) > 0 && _cells.Add(storageCell))
            {
                Interlocked.Decrement(ref _remainingCells.Value);
            }
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (ReferenceEquals(_currentStorageReadCapture, this))
            {
                _currentStorageReadCapture = null;
            }

            _cells.Dispose();
        }
    }
}

public sealed record PreBlockCachesConfig
{
    // 2^17 × 2 ways = 262144 entries, above the ~140K-slot working set at 300M gas.
    public int StorageCacheSetsBits { get; init; } = 17;

    public int SurvivingPrecompileCacheMaxEntries { get; init; } = 16384;

    /// <summary>
    /// Weighted budget for the per-block precompile tier: payload bytes plus <see cref="PrecompileCaches.EntryOverheadBytes"/> charged per entry.
    /// </summary>
    /// <remarks>
    /// A caller buys at most ~2.8 bytes of cache per gas spent (the sha256 asymptote, matched by blake2f with
    /// zero rounds once EVM call overhead is counted), while honest traffic runs two orders of magnitude below that.
    /// 32 MiB therefore leaves real blocks ample room at a 60M gas limit while refusing an adversarial filler well inside one block.
    /// It does not track the live gas limit, so it needs revisiting if the limit grows by an order of magnitude.
    /// </remarks>
    // TODO: calculate based on gas limit and precompiles stats?
    public long PrecompileCacheMaxBytes { get; init; } = 32 * 1024 * 1024;
}

[Flags]
public enum CacheType
{
    None = 0,
    Storage = 0b1,
    State = 0b10,
    /// <summary>RLP node-storage caching was enabled when cleared; this does not indicate whether it contained entries.</summary>
    Rlp = 0b100,
    Precompile = 0b1000
}
