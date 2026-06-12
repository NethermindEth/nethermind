// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Trie;

namespace Nethermind.State.Flat.Persistence;

/// <summary>
/// A decorator for IPersistence that caches readers to reduce the overhead of creating a full rocksdb snapshot.
/// The cache is periodically cleared to allow database compaction.
/// </summary>
public class CachedReaderPersistence : IPersistence, IAsyncDisposable
{
    private const int StorageSlotCacheCapacity = 262_144;

    private readonly IPersistence _inner; // Externally owned
    private readonly ILogger _logger;
    private readonly Lock _readerCacheLock = new();
    private readonly Lock _storageSlotCacheUpdateLock = new();
    private readonly AssociativeCache<StorageCell, SlotCacheEntry> _storageSlotCache = new(StorageSlotCacheCapacity);
    private readonly CancellationTokenSource _cancelTokenSource;
    private readonly Task _clearTimerTask;

    private RefCountingPersistenceReader? _cachedReader;
    private int _storageSlotCacheGeneration;
    private int _isDisposed;

    public CachedReaderPersistence(IPersistence inner,
        IProcessExitSource processExitSource,
        ILogManager logManager)
    {
        _inner = inner;
        _logger = logManager.GetClassLogger<CachedReaderPersistence>();
        _cancelTokenSource = CancellationTokenSource.CreateLinkedTokenSource(processExitSource.Token);

        // Start the background cache clearing task
        _clearTimerTask = Task.Run(async () =>
        {
            using PeriodicTimer timer = new(TimeSpan.FromSeconds(5));

            try
            {
                while (true)
                {
                    await timer.WaitForNextTickAsync(_cancelTokenSource.Token);
                    ClearReaderCache();
                }
            }
            catch (OperationCanceledException)
            {
            }
        });

        // Prime the reader cache
        using IPersistence.IPersistenceReader reader = CreateReader();
    }

    public IPersistence.IPersistenceReader CreateReader(ReaderFlags flags = ReaderFlags.None)
    {
        if ((flags & ReaderFlags.Sync) != 0)
            return _inner.CreateReader(flags);

        RefCountingPersistenceReader? cachedReader = _cachedReader;
        if (cachedReader is not null && cachedReader.TryAcquire())
        {
            return cachedReader;
        }

        using Lock.Scope _ = _readerCacheLock.EnterScope();
        return CreateReaderNoLock();
    }

    private IPersistence.IPersistenceReader CreateReaderNoLock()
    {
        while (true)
        {
            RefCountingPersistenceReader? cachedReader = _cachedReader;
            if (cachedReader is null)
            {
                _cachedReader = cachedReader = new RefCountingPersistenceReader(
                    new CachedPersistenceReader(_inner.CreateReader(), this, Volatile.Read(ref _storageSlotCacheGeneration)),
                    _logger
                );
            }

            if (cachedReader.TryAcquire())
            {
                return cachedReader;
            }

            // Was disposed but not cleared. Not yet at least.
            Interlocked.CompareExchange(ref _cachedReader, null, cachedReader);
        }
    }

    public IPersistence.IWriteBatch CreateWriteBatch(in StateId from, in StateId to, WriteFlags flags = WriteFlags.None) => new UpdateCacheOnWriteBatchComplete(_inner.CreateWriteBatch(from, to, flags), this);

    public void Flush() => _inner.Flush();

    public void Clear()
    {
        using Lock.Scope readerCacheScope = _readerCacheLock.EnterScope();
        using Lock.Scope storageSlotCacheUpdateScope = _storageSlotCacheUpdateLock.EnterScope();

        ClearReaderCacheNoLock();
        _storageSlotCache.Clear();
        Interlocked.Increment(ref _storageSlotCacheGeneration);
        _inner.Clear();
    }

    private void ClearReaderCache()
    {
        using Lock.Scope _ = _readerCacheLock.EnterScope();
        ClearReaderCacheNoLock();
    }

    private void ClearReaderCacheNoLock()
    {
        RefCountingPersistenceReader? cachedReader = _cachedReader;
        _cachedReader = null;
        cachedReader?.Dispose();
    }

    private void CompleteWriteBatch(
        IPersistence.IWriteBatch inner,
        List<StorageSlotUpdate>? storageSlotUpdates,
        bool clearStorageSlotCache)
    {
        using Lock.Scope readerCacheScope = _readerCacheLock.EnterScope();
        using Lock.Scope storageSlotCacheUpdateScope = _storageSlotCacheUpdateLock.EnterScope();

        ClearReaderCacheNoLock();
        Interlocked.Increment(ref _storageSlotCacheGeneration);

        try
        {
            inner.Dispose();
        }
        catch
        {
            _storageSlotCache.Clear();
            Interlocked.Increment(ref _storageSlotCacheGeneration);
            throw;
        }

        if (clearStorageSlotCache)
        {
            _storageSlotCache.Clear();
        }

        if (storageSlotUpdates is not null)
        {
            foreach (StorageSlotUpdate update in storageSlotUpdates)
            {
                StorageCell storageCell = update.StorageCell;
                _storageSlotCache.Set(in storageCell, update.Entry);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _isDisposed, 1, 0) == 1) return;

        await _cancelTokenSource.CancelAsync();
        await _clearTimerTask.ConfigureAwait(false);
        _cachedReader?.Dispose();
        _cancelTokenSource.Dispose();
    }

    private sealed class UpdateCacheOnWriteBatchComplete(IPersistence.IWriteBatch inner, CachedReaderPersistence parent)
        : IPersistence.IWriteBatch
    {
        private List<StorageSlotUpdate>? _storageSlotUpdates;
        private bool _clearStorageSlotCache;

        public void SelfDestruct(Address addr)
        {
            inner.SelfDestruct(addr);
            ClearStorageSlotCacheOnDispose();
        }

        public void SetAccount(Address addr, Account? account)
        {
            inner.SetAccount(addr, account);
            if (account is null)
            {
                ClearStorageSlotCacheOnDispose();
            }
        }

        public void SetStorage(Address addr, in UInt256 slot, in SlotValue? value)
        {
            inner.SetStorage(addr, slot, value);

            StorageCell storageCell = new(addr, in slot);
            _storageSlotUpdates ??= [];
            _storageSlotUpdates.Add(new StorageSlotUpdate(
                storageCell,
                new SlotCacheEntry(value.HasValue, value.GetValueOrDefault())));
        }

        public void SetStateTrieNode(in TreePath path, scoped ReadOnlySpan<byte> rlp) => inner.SetStateTrieNode(path, rlp);
        public void SetStorageTrieNode(Hash256 address, in TreePath path, scoped ReadOnlySpan<byte> rlp) => inner.SetStorageTrieNode(address, path, rlp);

        public void SetStorageRawEncoded(in ValueHash256 addrHash, in ValueHash256 slotHash, scoped ReadOnlySpan<byte> rlpValue)
        {
            inner.SetStorageRawEncoded(addrHash, slotHash, rlpValue);
            ClearStorageSlotCacheOnDispose();
        }

        public void SetAccountRaw(in ValueHash256 addrHash, Account account) => inner.SetAccountRaw(addrHash, account);

        public void DeleteAccountRange(in ValueHash256 fromPath, in ValueHash256 toPath)
        {
            inner.DeleteAccountRange(fromPath, toPath);
            ClearStorageSlotCacheOnDispose();
        }

        public void DeleteStorageRange(in ValueHash256 addressHash, in ValueHash256 fromPath, in ValueHash256 toPath)
        {
            inner.DeleteStorageRange(addressHash, fromPath, toPath);
            ClearStorageSlotCacheOnDispose();
        }

        public void DeleteStateTrieNodeRange(in TreePath fromPath, in TreePath toPath) => inner.DeleteStateTrieNodeRange(fromPath, toPath);
        public void DeleteStorageTrieNodeRange(in ValueHash256 addressHash, in TreePath fromPath, in TreePath toPath) => inner.DeleteStorageTrieNodeRange(addressHash, fromPath, toPath);

        public void Dispose() => parent.CompleteWriteBatch(inner, _storageSlotUpdates, _clearStorageSlotCache);

        private void ClearStorageSlotCacheOnDispose()
        {
            _clearStorageSlotCache = true;
            _storageSlotUpdates?.Clear();
        }
    }

    private sealed class CachedPersistenceReader(
        IPersistence.IPersistenceReader inner,
        CachedReaderPersistence parent,
        int storageSlotCacheGeneration)
        : IPersistence.IPersistenceReader
    {
        public StateId CurrentState => inner.CurrentState;

        public void Dispose() => inner.Dispose();

        public Account? GetAccount(Address address) => inner.GetAccount(address);

        public bool TryGetSlot(Address address, in UInt256 slot, ref SlotValue outValue)
        {
            StorageCell storageCell = new(address, in slot);
            if (IsCurrentStorageSlotCacheGeneration() &&
                parent._storageSlotCache.TryGet(in storageCell, out SlotCacheEntry? entry))
            {
                SlotCacheEntry cached = entry!;
                outValue = cached.Value;
                return cached.Exists;
            }

            SlotValue value = default;
            bool exists = inner.TryGetSlot(address, in slot, ref value);
            outValue = value;

            if (IsCurrentStorageSlotCacheGeneration())
            {
                using Lock.Scope _ = parent._storageSlotCacheUpdateLock.EnterScope();
                if (IsCurrentStorageSlotCacheGeneration())
                {
                    parent._storageSlotCache.Set(in storageCell, new SlotCacheEntry(exists, value));
                }
            }

            return exists;
        }

        public byte[]? TryLoadStateRlp(in TreePath path, ReadFlags flags) =>
            inner.TryLoadStateRlp(in path, flags);

        public byte[]? TryLoadStorageRlp(Hash256 address, in TreePath path, ReadFlags flags) =>
            inner.TryLoadStorageRlp(address, in path, flags);

        public byte[]? GetAccountRaw(in ValueHash256 addrHash) =>
            inner.GetAccountRaw(in addrHash);

        public bool TryGetStorageRaw(in ValueHash256 addrHash, in ValueHash256 slotHash, ref SlotValue value) =>
            inner.TryGetStorageRaw(in addrHash, in slotHash, ref value);

        public IPersistence.IFlatIterator CreateAccountIterator(in ValueHash256 startKey, in ValueHash256 endKey) =>
            inner.CreateAccountIterator(in startKey, in endKey);

        public IPersistence.IFlatIterator CreateStorageIterator(in ValueHash256 accountKey, in ValueHash256 startSlotKey, in ValueHash256 endSlotKey) =>
            inner.CreateStorageIterator(in accountKey, in startSlotKey, in endSlotKey);

        public bool IsPreimageMode => inner.IsPreimageMode;

        private bool IsCurrentStorageSlotCacheGeneration() =>
            storageSlotCacheGeneration == Volatile.Read(ref parent._storageSlotCacheGeneration);
    }

    private readonly struct StorageSlotUpdate(StorageCell storageCell, SlotCacheEntry entry)
    {
        public StorageCell StorageCell { get; } = storageCell;
        public SlotCacheEntry Entry { get; } = entry;
    }

    private sealed class SlotCacheEntry(bool exists, SlotValue value)
    {
        public bool Exists { get; } = exists;
        public SlotValue Value { get; } = value;
    }
}
