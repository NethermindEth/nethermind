// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Core;
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
    private readonly IPersistence _inner; // Externally owned
    private readonly ILogger _logger;
    private readonly Lock _readerCacheLock = new();
    private readonly CancellationTokenSource _cancelTokenSource;
    private readonly Task _clearTimerTask;

    private RefCountingPersistenceReader? _cachedReader;
    private int _isDisposed;

    public CachedReaderPersistence(IPersistence inner,
        IProcessExitSource processExitSource,
        ILogManager logManager)
    {
        _inner = inner;
        _logger = logManager.GetClassLogger();
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

    public IPersistence.IPersistenceReader CreateReader()
    {
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
                    _inner.CreateReader(),
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

    public IPersistence.IWriteBatch CreateWriteBatch(in StateId from, in StateId to, WriteFlags flags = WriteFlags.None)
    {
        return new ClearCacheOnWriteBatchComplete(_inner.CreateWriteBatch(from, to, flags), this);
    }

    public void Flush() => _inner.Flush();

    private void ClearReaderCache()
    {
        using Lock.Scope _ = _readerCacheLock.EnterScope();
        RefCountingPersistenceReader? cachedReader = _cachedReader;
        _cachedReader = null;
        cachedReader?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _isDisposed, 1, 0) == 1) return;

        await _cancelTokenSource.CancelAsync();
        await _clearTimerTask.ConfigureAwait(false);
        _cachedReader?.Dispose();
        _cancelTokenSource.Dispose();
    }

    private class ClearCacheOnWriteBatchComplete(IPersistence.IWriteBatch inner, CachedReaderPersistence parent)
        : IPersistence.IWriteBatch
    {
        public void Clear() => inner.Clear();
        public void SelfDestruct(Address addr) => inner.SelfDestruct(addr);
        public void SetAccount(Address addr, Account? account) => inner.SetAccount(addr, account);
        public void SetStorage(Address addr, in UInt256 slot, in SlotValue? value) => inner.SetStorage(addr, slot, value);
        public void SetStateTrieNode(in TreePath path, TrieNode tnValue) => inner.SetStateTrieNode(path, tnValue);
        public void SetStorageTrieNode(Hash256 address, in TreePath path, TrieNode tnValue) => inner.SetStorageTrieNode(address, path, tnValue);
        public void SetStorageRaw(Hash256 addrHash, Hash256 slotHash, in SlotValue? value) => inner.SetStorageRaw(addrHash, slotHash, value);
        public void SetAccountRaw(Hash256 addrHash, Account account) => inner.SetAccountRaw(addrHash, account);
        public void DeleteAccountRange(in ValueHash256 fromPath, in ValueHash256 toPath) => inner.DeleteAccountRange(fromPath, toPath);
        public void DeleteStorageRange(in ValueHash256 addressHash, in ValueHash256 fromPath, in ValueHash256 toPath) => inner.DeleteStorageRange(addressHash, fromPath, toPath);
        public void DeleteStateTrieNodeRange(in TreePath fromPath, in TreePath toPath) => inner.DeleteStateTrieNodeRange(fromPath, toPath);
        public void DeleteStorageTrieNodeRange(in ValueHash256 addressHash, in TreePath fromPath, in TreePath toPath) => inner.DeleteStorageTrieNodeRange(addressHash, fromPath, toPath);

        public void Dispose()
        {
            inner.Dispose();

            // not in lock as it has its own lock
            parent.ClearReaderCache();
        }
    }
}
