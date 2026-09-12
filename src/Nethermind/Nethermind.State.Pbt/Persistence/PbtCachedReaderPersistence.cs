// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Core.Crypto;
using Nethermind.Core.Utils;
using Nethermind.Db;
using Nethermind.Pbt;

namespace Nethermind.State.Pbt.Persistence;

/// <summary>Decorates <see cref="IPbtPersistence"/> with a shared reader snapshot.</summary>
public sealed class PbtCachedReaderPersistence : IPbtPersistence, IAsyncDisposable
{
    private static readonly TimeSpan ClearInterval = TimeSpan.FromSeconds(5);
    private readonly IPbtPersistence _inner;
    private readonly Lock _cacheLock = new();
    private readonly CancellationTokenSource _stopSource;
    private readonly Task _clearWorker;
    private SharedReader? _cachedReader;
    private int _pinDepth;
    private int _isDisposed;

    public PbtCachedReaderPersistence(IPbtPersistence inner, IProcessExitSource processExitSource)
    {
        _inner = inner;
        _stopSource = CancellationTokenSource.CreateLinkedTokenSource(processExitSource.Token);
        _clearWorker = Task.Run(RunClearWorker);
    }

    public IPbtPersistence.IReader CreateReader()
    {
        SharedReader? cached = Volatile.Read(ref _cachedReader);
        if (cached is not null && cached.TryLease()) return cached;
        using Lock.Scope _ = _cacheLock.EnterScope();
        _cachedReader ??= new SharedReader(_inner.CreateReader());
        _cachedReader.AcquireLease();
        return _cachedReader;
    }

    public IPbtPersistence.IWriteBatch CreateWriteBatch(in StateId from, in StateId to, in ValueHash256 treeRoot, WriteFlags flags)
    {
        PinReaderCache();
        try
        {
            return new CacheClearingWriteBatch(_inner.CreateWriteBatch(from, to, treeRoot, flags), this);
        }
        catch
        {
            ReleaseReaderCachePin();
            throw;
        }
    }

    public IPbtPersistence.IWriteBatch CreateStagingWriteBatch(WriteFlags flags) => _inner.CreateStagingWriteBatch(flags);

    public void Flush() => _inner.Flush();

    private void PinReaderCache()
    {
        using Lock.Scope _ = _cacheLock.EnterScope();
        _cachedReader ??= new SharedReader(_inner.CreateReader());
        _pinDepth++;
    }

    private void ReleaseReaderCachePin()
    {
        using Lock.Scope _ = _cacheLock.EnterScope();
        _pinDepth--;
    }

    private void PublishCommittedState()
    {
        SharedReader? stale;
        using (_cacheLock.EnterScope()) stale = Unpublish();
        stale?.Dispose();
    }

    private void ClearReaderCache()
    {
        SharedReader? cached = null;
        using (_cacheLock.EnterScope())
        {
            if (_pinDepth == 0) cached = Unpublish();
        }
        cached?.Dispose();
    }

    private SharedReader? Unpublish()
    {
        SharedReader? cached = _cachedReader;
        _cachedReader = null;
        return cached;
    }

    private async Task RunClearWorker()
    {
        using PeriodicTimer timer = new(ClearInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(_stopSource.Token)) ClearReaderCache();
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) == 1) return;
        await _stopSource.CancelAsync();
        await _clearWorker;
        SharedReader? cached;
        using (_cacheLock.EnterScope()) cached = Unpublish();
        cached?.Dispose();
        _stopSource.Dispose();
    }

    private sealed class SharedReader(IPbtPersistence.IReader inner) : RefCountingDisposable, IPbtPersistence.IReader
    {
        public StateId CurrentState => inner.CurrentState;
        public ValueHash256 CurrentRoot => inner.CurrentRoot;
        public Account? GetAccount(in ValueHash256 addressHash) => inner.GetAccount(addressHash);
        public EvmWord GetSlot(PbtStorageFullKey key) => inner.GetSlot(key);
        public CodeInfo? GetCode(in ValueHash256 codeHash) => inner.GetCode(codeHash);
        public IPbtIterator<KeyValuePair<ValueHash256, Account>> EnumerateAccounts() => inner.EnumerateAccounts();
        public IPbtIterator<KeyValuePair<PbtStorageFullKey, EvmWord>> EnumerateStorage(PbtStorageFullKey? prefix = null) => inner.EnumerateStorage(prefix);
        public RefCountingMemory? GetNodeGroup<TPath>(TPath groupKey) where TPath : struct, IPbtNodePath<TPath> => inner.GetNodeGroup(groupKey);
        public IPbtIterator<PbtStorageNodePath> EnumerateNodeGroupKeys() => inner.EnumerateNodeGroupKeys();
        public ulong GetCodeReference(in ValueHash256 codeHash) => inner.GetCodeReference(codeHash);
        public bool TryLease() => TryAcquireLease();
        protected override void CleanUp() => inner.Dispose();
    }

    private sealed class CacheClearingWriteBatch(IPbtPersistence.IWriteBatch inner, PbtCachedReaderPersistence parent) : IPbtPersistence.IWriteBatch
    {
        private readonly Lock _stateLock = new();
        private bool _commitAttempted;
        private bool _disposed;

        public void SetAccount(in ValueHash256 addressHash, Account? account) => inner.SetAccount(addressHash, account);
        public void SetSlot(PbtStorageFullKey key, in EvmWord value) => inner.SetSlot(key, value);
        public void SetCode(in ValueHash256 codeHash, CodeInfo code) => inner.SetCode(codeHash, code);
        public void ClearStorage(in ValueHash256 addressHash) => inner.ClearStorage(addressHash);
        public void SetNodeGroup<TPath>(TPath groupKey, RefCountingMemory? payload) where TPath : struct, IPbtNodePath<TPath> => inner.SetNodeGroup(groupKey, payload);
        public void SetCodeReference(in ValueHash256 codeHash, ulong? referenceCount) => inner.SetCodeReference(codeHash, referenceCount);

        public void Commit()
        {
            using Lock.Scope _ = _stateLock.EnterScope();
            if (_disposed || _commitAttempted) return;
            _commitAttempted = true;
            inner.Commit();
            parent.PublishCommittedState();
        }

        public void Dispose()
        {
            using Lock.Scope _ = _stateLock.EnterScope();
            if (_disposed) return;
            _disposed = true;
            try
            {
                inner.Dispose();
            }
            finally
            {
                parent.ReleaseReaderCachePin();
            }
        }
    }
}
