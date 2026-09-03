// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Collections.Pooled;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;

[assembly: InternalsVisibleTo("Nethermind.State.Test")]
namespace Nethermind.Evm.State;

public class PreBlockCaches
{
    private readonly Func<CacheType>[] _clearCaches;

    private readonly SeqlockCache<StorageCell, byte[]> _storageCache;
    private readonly SeqlockCache<AddressAsKey, Account> _stateCache;
    private readonly PrecompileCaches _precompileCaches;
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

    internal PreBlockCaches() : this(new PreBlockCachesConfig(), PrecompileCaches.Empty) { }

    internal PreBlockCaches(PreBlockCachesConfig config) : this(config, PrecompileCaches.Empty) { }

    public PreBlockCaches(PreBlockCachesConfig config, PrecompileCaches precompileCaches)
    {
        _storageCache = new SeqlockCache<StorageCell, byte[]>(config.StorageCacheSetsBits);
        _stateCache = new SeqlockCache<AddressAsKey, Account>(config.StateCacheSetsBits);
        _precompileCaches = precompileCaches;
        _clearCaches =
        [
            () => { _storageCache.Clear(); return CacheType.None; },
            () => { _stateCache.Clear(); return CacheType.None; },
            () => { _precompileCaches.ClearBlockCache(); return CacheType.None; }
        ];
        _writeBack = new WriteBackBatch(this);
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

    /// <summary>Opens a consumer scope; see <see cref="ConsumerScopeOpen"/>.</summary>
    /// <remarks>
    /// At most one may be open at a time, which is what leaves the write-back a single writer. Nothing here enforces
    /// it: it holds because only the main processing scope is decorated, leaving the RPC, tracing and simulate world
    /// states out. A second consumer inside that scope would clear the caches mid-block and race the write-back.
    /// </remarks>
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
    public void ClearPrecompileCache() => _precompileCaches.ClearBlockCache();

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
    /// <param name="stateRoot">The state root the next block starts from.</param>
    /// <param name="logger">Reports when caches for another state root have to be cleared.</param>
    /// <returns><see langword="true"/> when the caches were kept; <see langword="false"/> when they were cleared.</returns>
    public bool PrepareFor(Hash256? stateRoot, ILogger logger = default)
    {
        JoinPendingWriteBack();
        lock (_reconcileLock)
        {
            if (stateRoot is not null && _validFor == stateRoot) return true;

            Hash256? validFor = _validFor;
            ClearCachesCore();
            if (validFor is not null && logger.IsInfo) ReportCachesClearedForStateMismatch(logger, validFor, stateRoot);
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
    /// <param name="stateRoot">The state root the consumer is about to read.</param>
    /// <param name="logger">Reports when caches for another state root have to be cleared.</param>
    public void EnsureNotStaleFor(Hash256? stateRoot, ILogger logger = default)
    {
        JoinPendingWriteBack();
        lock (_reconcileLock)
        {
            if (stateRoot is null || _validFor != stateRoot)
            {
                Hash256? validFor = _validFor;
                ClearStateCachesCore();
                if (validFor is not null && logger.IsInfo) ReportCachesClearedForStateMismatch(logger, validFor, stateRoot);
            }
        }
    }

    /// <remarks>Out of line because state mismatches are rare during linear block processing.</remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ReportCachesClearedForStateMismatch(ILogger logger, Hash256 validFor, Hash256? requestedState) =>
        logger.Info($"Pre-block caches cleared because cached state root {validFor} does not match requested state root {requestedState}");

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
    /// the committing scope. A speculative session that starts while this one runs is covered by the first of them:
    /// its start calls <see cref="PrepareFor"/>, which joins, before the session's own thread exists.
    /// </para>
    /// <para>
    /// <see cref="IWorldStateScopeProvider.IStorageWriteBatch.Clear"/> drops the whole storage cache, because a
    /// contract's pre-block slots cannot be enumerated. The batch then stops accepting storage writes, so the snapshot
    /// ends there instead of refilling the cache with the one block it holds.
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
                        WriteBackCore(baseStateRoot, stateRoot, snapshot.WriteTo, logger);
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

    private void WriteBackCore(Hash256? baseStateRoot, Hash256 stateRoot, Action<IWorldStateScopeProvider.IWorldStateWriteBatch> writeChanges, ILogger logger)
    {
        lock (_reconcileLock)
        {
            if (baseStateRoot is null || _validFor != baseStateRoot) return;

            _writeBack.Begin(logger);
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
                if (logger.IsInfo) ReportCachesCleared(logger);
            }
            else
            {
                _validFor = stateRoot;
            }
        }
    }

    /// <remarks>Out of line because it must be rare.</remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ReportCachesCleared(ILogger logger) =>
        logger.Info("Pre-block caches cleared by a writer that overlapped the write-back");

    private sealed class WriteBackBatch(PreBlockCaches caches) : IWorldStateScopeProvider.IWorldStateWriteBatch
    {
        private readonly StorageWriteBackBatch _storage = new(caches._storageCache);
        private bool _contended;

        public bool Contended => _contended || _storage.Contended;

        public bool AcceptsStorageWrites => !_storage.Cleared && !Contended;

        public void Begin(ILogger logger)
        {
            _contended = false;
            _storage.Contended = false;
            _storage.Cleared = false;
            _storage.Logger = logger;
        }

        // Never raised: nothing on the way into a cache recomputes a storage root.
        public event EventHandler<IWorldStateScopeProvider.AccountUpdated>? OnAccountUpdated { add { } remove { } }

        public void Set(Address key, Account? account)
        {
            // A contended write-back ends with both caches cleared, so nothing after the first one is worth writing.
            if (Contended) return;

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
        public ILogger Logger { get; set; }
        public bool Cleared { get; set; }

        public void Set(in UInt256 index, byte[] value)
        {
            if (Contended) return;

            StorageCell cell = new(Address, in index);
            if (!storageCache.TrySetExclusive(in cell, value)) Contended = true;
        }

        public void Clear()
        {
            if (Cleared) return;

            storageCache.Clear();
            Cleared = true;
            if (Logger.IsInfo) ReportStorageCacheCleared(Logger, Address);
        }

        /// <remarks>Out of line because it must be rare.</remarks>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ReportStorageCacheCleared(ILogger logger, Address address) =>
            logger.Info($"Pre-block storage cache cleared by {address}");

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
}

public sealed record PreBlockCachesConfig
{
    /// <summary>
    /// Set-index bits of the account cache, giving 2^n sets of 2 ways. Default 16, so 131072 entries.
    /// </summary>
    /// <remarks>
    /// Sized well above one block's accounts because the caches carry across blocks: what a block leaves behind
    /// serves the blocks after it until evicted. An entry is 24 bytes of array and keeps its account alive on top
    /// of that, so a full cache costs roughly three times its array.
    /// </remarks>
    public int StateCacheSetsBits { get; init; } = 16;

    /// <summary>
    /// Set-index bits of the storage cache, giving 2^n sets of 2 ways. Default 18, so 524288 entries.
    /// </summary>
    /// <remarks>
    /// Above the ~140K-slot working set of a single 300M-gas block, with room for the blocks before it. An entry is
    /// 56 bytes of array, the slot index being most of it, and keeps its value alive on top of that.
    /// </remarks>
    public int StorageCacheSetsBits { get; init; } = 18;

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
