// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Collections.Pooled;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Logging;

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
    private readonly WriteBackBatch _writeBack;
    // The write-back of the block just committed, still running; joined before anything else writes the caches.
    // Guarded by _joinLock, which is never held by the write-back itself, so a joiner cannot deadlock against it.
    private readonly Lock _joinLock = new();
    private Task? _pendingWriteBack;
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
        _writeBack = new WriteBackBatch(this);
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
        JoinPendingWriteBack();
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

        _validFor = null;
        return isDirty;
    }

    // Epoch bumps only: safe while populators for another head are still writing, unlike the precompile dictionary's clear.
    private void ClearStateCachesCore()
    {
        _storageCache.Clear();
        _stateCache.Clear();
        _validFor = null;
    }

    /// <summary>Drops the per-block precompile results once a block has finished; the account and storage caches carry over.</summary>
    public void ClearPrecompileCache() => _precompileCache.NoLockClear();

    /// <summary>The state root the account and storage caches reflect, or <see langword="null"/> when unknown.</summary>
    public Hash256? ValidFor => _validFor;

    /// <summary>
    /// Makes the account and storage caches reflect the state at <paramref name="stateRoot"/>: keeps them when they
    /// already do and clears them (together with the per-block precompile cache) otherwise.
    /// </summary>
    /// <remarks>
    /// For the driver, once every populator is joined: afterwards the caches are known to reflect
    /// <paramref name="stateRoot"/>, so populators may fill them from that state.
    /// </remarks>
    /// <returns><see langword="true"/> when the caches were kept; <see langword="false"/> when they were cleared.</returns>
    public bool PrepareFor(Hash256? stateRoot)
    {
        JoinPendingWriteBack();
        lock (_reconcileLock)
        {
            if (stateRoot is not null && _validFor == stateRoot) return true;

            ClearCachesCore();
            _validFor = stateRoot;
            return false;
        }
    }

    /// <summary>
    /// Guards a consumer scope about to read state at <paramref name="stateRoot"/>: account and storage caches that
    /// do not reflect it are cleared.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="PrepareFor"/>, a clear here adopts no identity: only the driver vouches for the caches, once
    /// it has prepared them for the block. <see cref="BeginConsumerScope"/> beforehand joins any speculative session,
    /// so no populator writes while this runs or while the scope reads.
    /// </remarks>
    public void EnsureNotStaleFor(Hash256? stateRoot)
    {
        JoinPendingWriteBack();
        lock (_reconcileLock)
        {
            if (stateRoot is null || _validFor != stateRoot) ClearStateCachesCore();
        }
    }

    /// <summary>
    /// Brings the account and storage caches from the state at <paramref name="baseStateRoot"/> to the committed
    /// state at <paramref name="stateRoot"/>, on a background thread, after which the caches count as reflecting
    /// <paramref name="stateRoot"/>.
    /// </summary>
    /// <remarks>
    /// Does nothing when the caches do not reflect <paramref name="baseStateRoot"/>: they then have nothing to bring
    /// forward and stay as they are until the driver prepares them.
    /// <para>
    /// Running off the block-processing thread keeps the write-back off the response path. The batch is still a
    /// single-writer upsert: the block's pre-warming has been joined, the commit's write batch has drained the
    /// scope's background readers, and every other writer reaches the caches through <see cref="PrepareFor"/>,
    /// <see cref="EnsureNotStaleFor"/> or <see cref="ClearCaches"/>, each of which joins this write-back first.
    /// Detecting a writer that gets in regardless is best effort:
    /// <see cref="SeqlockCache{TKey,TValue}.TrySetExclusive"/> reports one that overlaps a write, and that, like an
    /// exception in the writer, leaves the caches cleared rather than half-updated. A fault clears the caches and is
    /// logged instead of failing the block that produced it; only a failure of the logging itself reaches a joiner,
    /// and then just once.
    /// </para>
    /// <para>
    /// The invariant this rests on is not enforced here: it is that <see cref="PrepareFor"/>,
    /// <see cref="EnsureNotStaleFor"/> and <see cref="ClearCaches"/> are the only ways to the caches for anything but
    /// the committing scope. In particular a speculative session cannot start while a consumer scope is open, so none
    /// can begin between this write-back being queued and its join.
    /// </para>
    /// <para>
    /// <see cref="IWorldStateScopeProvider.IStorageWriteBatch.Clear"/> drops the whole storage cache, because a
    /// contract's pre-block slots cannot be enumerated, so the snapshot must issue every clear before its first
    /// slot write.
    /// </para>
    /// </remarks>
    /// <param name="takeSnapshot">
    /// Called on the calling thread, and only once the caches look like they want the write-back, so a block whose
    /// changes would be dropped rarely pays to snapshot them. That check is advisory, taken outside the lock that
    /// guards the identity; the write itself checks again and may still drop what it was given. The snapshot is
    /// disposed once written, or as soon as it is clear that it will not be.
    /// </param>
    public void WriteBackInBackground(
        Hash256? baseStateRoot,
        Hash256 stateRoot,
        Func<IWorldStateScopeProvider.IBlockChangeSnapshot> takeSnapshot,
        ILogger logger)
    {
        lock (_joinLock)
        {
            // The previous block's write-back is what moves the caches to this one's base state, so join before asking.
            JoinPendingWriteBackCore();
            if (baseStateRoot is null || _validFor != baseStateRoot) return;

            IWorldStateScopeProvider.IBlockChangeSnapshot snapshot = takeSnapshot();
            try
            {
                // Nothing may escape: a faulted task would be rethrown into whichever block joins it next.
                _pendingWriteBack = Task.Run(() =>
                {
                    try
                    {
                        WriteBackCore(baseStateRoot, stateRoot, snapshot.WriteTo);
                    }
                    catch (Exception e)
                    {
                        if (logger.IsError) logger.Error($"Pre-block cache write-back for state root {stateRoot} failed; the caches were dropped.", e);
                    }
                    finally
                    {
                        Release(snapshot, logger);
                    }
                });
            }
            catch (Exception e)
            {
                // No task will run, and the snapshot holds buffers the world state wants back.
                Release(snapshot, logger);
                if (logger.IsError) logger.Error($"Pre-block cache write-back for state root {stateRoot} could not be started.", e);
            }
        }
    }

    /// <summary>Gives a block's collections back. Never throws: a release failure must not reach a block either.</summary>
    private static void Release(IWorldStateScopeProvider.IBlockChangeSnapshot snapshot, ILogger logger)
    {
        try
        {
            snapshot.Dispose();
        }
        catch (Exception e)
        {
            if (logger.IsError) logger.Error("Releasing the block collections of a pre-block cache write-back failed.", e);
        }
    }

    /// <summary>Waits for the write-back of the previous block, if one is still running.</summary>
    private void JoinPendingWriteBack()
    {
        lock (_joinLock)
        {
            JoinPendingWriteBackCore();
        }
    }

    private void JoinPendingWriteBackCore()
    {
        Task? pending = _pendingWriteBack;
        if (pending is null) return;

        try
        {
            pending.GetAwaiter().GetResult();
        }
        finally
        {
            // Dropped even if the wait threw, so one bad write-back cannot fail every block after it.
            _pendingWriteBack = null;
        }
    }

    private void WriteBackCore(Hash256? baseStateRoot, Hash256 stateRoot, Action<IWorldStateScopeProvider.IWorldStateWriteBatch> writeChanges)
    {
        lock (_reconcileLock)
        {
            if (baseStateRoot is null || _validFor != baseStateRoot) return;

            _writeBack.Begin();
            try
            {
                writeChanges(_writeBack);
            }
            catch
            {
                ClearStateCachesCore();
                throw;
            }

            if (_writeBack.Contended)
            {
                // Another writer got in, so the caches describe neither the base nor the committed state.
                ClearStateCachesCore();
            }
            else
            {
                _validFor = stateRoot;
            }
        }
    }

    private sealed class WriteBackBatch(PreBlockCaches caches) : IWorldStateScopeProvider.IWorldStateWriteBatch
    {
        private readonly StorageWriteBackBatch _storage = new(caches._storageCache);
        private bool _contended;

        public bool Contended => _contended || _storage.Contended;

        public void Begin()
        {
            _contended = false;
            _storage.Contended = false;
        }

        // Never raised: nothing on the way into a cache recomputes a storage root.
        public event EventHandler<IWorldStateScopeProvider.AccountUpdated>? OnAccountUpdated { add { } remove { } }

        public void Set(Address key, Account? account)
        {
            AddressAsKey addressAsKey = key;
            if (!caches._stateCache.TrySetExclusive(in addressAsKey, account)) _contended = true;
        }

        // One contract at a time: the writer disposes each storage batch before creating the next.
        public IWorldStateScopeProvider.IStorageWriteBatch CreateStorageWriteBatch(Address key, int estimatedEntries)
        {
            _storage.Address = key;
            return _storage;
        }

        public void Dispose() { }
    }

    private sealed class StorageWriteBackBatch(SeqlockCache<StorageCell, byte[]> storageCache) : IWorldStateScopeProvider.IStorageWriteBatch
    {
        public Address Address { get; set; } = null!;
        public bool Contended { get; set; }

        public void Set(in UInt256 index, byte[] value)
        {
            StorageCell cell = new(Address, in index);
            if (!storageCache.TrySetExclusive(in cell, value)) Contended = true;
        }

        public void Clear() => storageCache.Clear();

        public void Dispose() { }
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
