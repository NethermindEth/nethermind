// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Config;
using Nethermind.Core;
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
    private bool _refreshPending;
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
            ReleaseReaderCachePin(refresh: false);
            throw;
        }
    }

    public void Flush() => _inner.Flush();

    private void PinReaderCache()
    {
        using Lock.Scope _ = _cacheLock.EnterScope();
        _cachedReader ??= new SharedReader(_inner.CreateReader());
        _pinDepth++;
    }

    private void ReleaseReaderCachePin(bool refresh)
    {
        SharedReader? stale = null;
        try
        {
            using Lock.Scope _ = _cacheLock.EnterScope();
            _refreshPending |= refresh;
            if (--_pinDepth == 0)
            {
                if (_refreshPending && Volatile.Read(ref _isDisposed) == 0)
                {
                    stale = Unpublish();
                    _cachedReader = new SharedReader(_inner.CreateReader());
                }
                _refreshPending = false;
            }
        }
        finally
        {
            stale?.Dispose();
        }
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
        public ValueHash256? GetLeaf(PbtFullKey key) => inner.GetLeaf(key);
        public IEnumerable<KeyValuePair<PbtFullKey, ValueHash256>> EnumerateLeaves() => inner.EnumerateLeaves();
        public IEnumerable<KeyValuePair<PbtFullKey, ValueHash256>> EnumerateLeaves(PbtFullKey prefix) => inner.EnumerateLeaves(prefix);
        public byte[]? GetNode(PbtFullKey locator) => inner.GetNode(locator);
        public IEnumerable<KeyValuePair<PbtFullKey, byte[]>> EnumerateNodes() => inner.EnumerateNodes();
        public ulong GetCodeReference(in ValueHash256 codeHash) => inner.GetCodeReference(codeHash);
        public bool TryLease() => TryAcquireLease();
        protected override void CleanUp() => inner.Dispose();
    }

    private sealed class CacheClearingWriteBatch(IPbtPersistence.IWriteBatch inner, PbtCachedReaderPersistence parent) : IPbtPersistence.IWriteBatch
    {
        private int _disposed;
        public void SetLeaf(PbtFullKey key, ValueHash256? value) => inner.SetLeaf(key, value);
        public void SetNode(PbtFullKey locator, ReadOnlySpan<byte> encoding) => inner.SetNode(locator, encoding);
        public void SetCodeReference(in ValueHash256 codeHash, ulong? referenceCount) => inner.SetCodeReference(codeHash, referenceCount);
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            try
            {
                inner.Dispose();
            }
            finally
            {
                parent.ReleaseReaderCachePin(refresh: true);
            }
        }
    }
}
