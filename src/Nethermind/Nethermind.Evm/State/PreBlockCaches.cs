// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using Collections.Pooled;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;

using CollectionExtensions = Nethermind.Core.Collections.CollectionExtensions;

namespace Nethermind.Evm.State;

public class PreBlockCaches
{
    private const int InitialCapacity = 4096 * 8;

    private static int LockPartitions => CollectionExtensions.LockPartitions;

    private readonly Func<CacheType>[] _clearCaches;

    private readonly SeqlockCache<StorageCell, byte[]> _storageCache;
    private readonly SeqlockCache<AddressAsKey, Account> _stateCache = new();
    private readonly ConcurrentDictionary<PrecompileCacheKey, Result<byte[]>> _precompileCache = new(LockPartitions, InitialCapacity);
    private readonly ClockCache<PrecompileCacheKey, Result<byte[]>> _survivingPrecompileCache;
    private volatile IWorldStateScopeProvider.IScope? _mainScope;
    private int _consumerScopes;

    private readonly Lock _reconcileLock = new();
    private readonly BlockWriteSet _pendingWrites = new();
    // Once sealed, the pending writes take the caches from _pendingWritesBase to _pendingWritesRoot; a null root
    // means a block is still recording into them.
    private Hash256? _pendingWritesBase;
    private Hash256? _pendingWritesRoot;
    // State root the account and storage caches reflect; null until PrepareFor establishes one.
    private Hash256? _validFor;

    [ThreadStatic]
    private static StorageReadCapture? _currentStorageReadCapture;

    public PreBlockCaches() : this(new PreBlockCachesConfig()) { }

    public PreBlockCaches(PreBlockCachesConfig config)
    {
        _storageCache = new SeqlockCache<StorageCell, byte[]>(config.StorageCacheSetsBits);
        _survivingPrecompileCache = new ClockCache<PrecompileCacheKey, Result<byte[]>>(
            config.SurvivingPrecompileCacheMaxEntries, comparer: EqualityComparer<PrecompileCacheKey>.Default);
        _clearCaches =
        [
            () => { _storageCache.Clear(); return CacheType.None; },
            () => { _stateCache.Clear(); return CacheType.None; },
            () => { _precompileCache.NoLockClear(); return CacheType.None; }
        ];
    }

    public SeqlockCache<StorageCell, byte[]> StorageCache => _storageCache;
    public SeqlockCache<AddressAsKey, Account> StateCache => _stateCache;
    public ConcurrentDictionary<PrecompileCacheKey, Result<byte[]>> PrecompileCache => _precompileCache;
    public ClockCache<PrecompileCacheKey, Result<byte[]>> SurvivingPrecompileCache => _survivingPrecompileCache;

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
    /// Whether a consumer scope is open: from <see cref="BeginConsumerScope"/> until <see cref="EndConsumerScope"/>,
    /// which the consumer calls only once its underlying scope has been torn down and its background readers drained.
    /// No speculative session may run in that time.
    /// </summary>
    public bool ConsumerScopeOpen => Volatile.Read(ref _consumerScopes) > 0;

    /// <summary>
    /// Raised by <see cref="BeginConsumerScope"/> before the consumer reads. The driver joins any speculative session
    /// here, so a consumer scope and a session never coexist and nothing but the consumer writes while it is open.
    /// </summary>
    public event Action? ConsumerScopeOpened;

    public void BeginConsumerScope()
    {
        Interlocked.Increment(ref _consumerScopes);
        ConsumerScopeOpened?.Invoke();
    }

    /// <returns>The number of consumer scopes still open.</returns>
    public int EndConsumerScope() => Interlocked.Decrement(ref _consumerScopes);

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
        lock (_reconcileLock)
        {
            return ClearCachesCore();
        }
    }

    private CacheType ClearCachesCore()
    {
        CacheType isDirty = CacheType.None;
        foreach (Func<CacheType> clearCache in _clearCaches)
        {
            isDirty |= clearCache();
        }

        ForgetIdentity();
        return isDirty;
    }

    // Epoch bumps only: safe while populators for another head are still writing, unlike the precompile dictionary's clear.
    private void ClearStateCachesCore()
    {
        _storageCache.Clear();
        _stateCache.Clear();
        ForgetIdentity();
    }

    private void ForgetIdentity()
    {
        _validFor = null;
        // A sealed write set describes a transition the emptied caches no longer need; an unsealed one belongs to
        // the block still recording into it.
        if (_pendingWritesRoot is not null) ClearPendingWrites();
    }

    /// <summary>Drops the per-block precompile results once a block has finished; the account and storage caches carry over.</summary>
    public void ClearPrecompileCache() => _precompileCache.NoLockClear();

    /// <summary>The write set the main processing scope records the current block's committed values into.</summary>
    public BlockWriteSet PendingWrites => _pendingWrites;

    /// <summary>The state root the account and storage caches reflect, or <see langword="null"/> when unknown.</summary>
    public Hash256? ValidFor => _validFor;

    /// <summary>
    /// Marks the recorded writes as the persisted transition from <paramref name="baseStateRoot"/> to
    /// <paramref name="stateRoot"/>, ready for <see cref="PrepareFor"/> to replay.
    /// </summary>
    public void SealPendingWrites(Hash256? baseStateRoot, Hash256 stateRoot)
    {
        lock (_reconcileLock)
        {
            if (_pendingWritesRoot is not null)
            {
                // The previous block's writes were never replayed, so the caches have fallen behind the persisted state.
                ClearPendingWrites();
                _validFor = null;
                return;
            }

            _pendingWritesBase = baseStateRoot;
            _pendingWritesRoot = stateRoot;
        }
    }

    /// <summary>Drops writes recorded by a scope that never committed them, such as a discarded or failed block.</summary>
    public void DiscardUnsealedWrites()
    {
        lock (_reconcileLock)
        {
            if (_pendingWritesRoot is null) _pendingWrites.Clear();
        }
    }

    /// <summary>
    /// Makes the account and storage caches reflect the state at <paramref name="stateRoot"/>: keeps them when they
    /// already do, replays the sealed block write set when that is what separates them from it, and clears them
    /// (together with the per-block precompile cache) otherwise.
    /// </summary>
    /// <remarks>
    /// For the driver, once every populator is joined: afterwards the caches are known to reflect
    /// <paramref name="stateRoot"/>, so populators may fill them from that state.
    /// </remarks>
    /// <returns><see langword="true"/> when the caches were kept; <see langword="false"/> when they were cleared.</returns>
    public bool PrepareFor(Hash256? stateRoot)
    {
        lock (_reconcileLock)
        {
            if (TryMakeValidFor(stateRoot)) return true;

            ClearCachesCore();
            _validFor = stateRoot;
            return false;
        }
    }

    /// <summary>
    /// Guards a consumer scope about to read state at <paramref name="stateRoot"/>: account and storage caches that
    /// cannot be made to reflect it are cleared.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="PrepareFor"/>, a clear here adopts no identity: only the driver vouches for the caches, once
    /// it has prepared them for the block. <see cref="BeginConsumerScope"/> beforehand joins any speculative session,
    /// so no populator writes while this runs or while the scope reads.
    /// </remarks>
    public void EnsureNotStaleFor(Hash256? stateRoot)
    {
        lock (_reconcileLock)
        {
            if (!TryMakeValidFor(stateRoot)) ClearStateCachesCore();
        }
    }

    private bool TryMakeValidFor(Hash256? stateRoot)
    {
        if (stateRoot is null || _validFor is null) return false;

        Hash256? pendingRoot = _pendingWritesRoot;
        if (pendingRoot is null) return _validFor == stateRoot;
        if (_pendingWritesBase != _validFor) return false;

        // Another block on the same parent: the caches still hold that parent, only the sealed writes are moot.
        if (_validFor == stateRoot)
        {
            ClearPendingWrites();
            return true;
        }

        if (pendingRoot != stateRoot || !_pendingWrites.TryApplyTo(_stateCache, _storageCache)) return false;

        ClearPendingWrites();
        _validFor = stateRoot;
        return true;
    }

    private void ClearPendingWrites()
    {
        _pendingWrites.Clear();
        _pendingWritesBase = null;
        _pendingWritesRoot = null;
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

    public readonly struct PrecompileCacheKey(Address address, ReadOnlyMemory<byte> data, IReleaseSpec spec) : IEquatable<PrecompileCacheKey>
    {
        private Address Address { get; } = address;
        private ReadOnlyMemory<byte> Data { get; } = data;
        // Reference-compared; results may differ across forks, so entries never cross a fork boundary.
        private IReleaseSpec Spec { get; } = spec;

        public bool Equals(PrecompileCacheKey other) =>
            ReferenceEquals(Spec, other.Spec) && Address == other.Address && Data.Span.SequenceEqual(other.Data.Span);
        public override bool Equals(object? obj) => obj is PrecompileCacheKey other && Equals(other);
        public override int GetHashCode() => Data.Span.FastHash() ^ Address.GetHashCode() ^ RuntimeHelpers.GetHashCode(Spec);
    }
}

public sealed record PreBlockCachesConfig
{
    // 2^17 × 2 ways = 262144 entries, above the ~140K-slot working set at 300M gas.
    public int StorageCacheSetsBits { get; init; } = 17;

    public int SurvivingPrecompileCacheMaxEntries { get; init; } = 16384;
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
