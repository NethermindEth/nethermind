// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Utils;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Pbt;

namespace Nethermind.State.Pbt.Persistence;

/// <summary>Decorates <see cref="IPbtPersistence"/> with a shared reader snapshot.</summary>
/// <remarks>
/// Creating a snapshot across all columns is expensive, but snapshots pin SST files and delay
/// compaction, so the cache is periodically cleared. A write batch pins the pre-batch snapshot until
/// disposal to prevent readers from observing a partially applied multi-column batch.
/// </remarks>
public sealed class PbtCachedReaderPersistence : IPbtPersistence, IAsyncDisposable
{
    private static readonly TimeSpan ClearInterval = TimeSpan.FromSeconds(5);

    private readonly IPbtPersistence _inner;
    private readonly Lock _cacheLock = new();
    private readonly CancellationTokenSource _stopSource;
    private readonly Task _clearWorker;

    private SharedReader? _cachedReader;

    // Accessed only under _cacheLock.
    private int _pinDepth;

    private int _isDisposed;

    public PbtCachedReaderPersistence(IPbtPersistence inner, IProcessExitSource processExitSource)
    {
        _inner = inner;
        _stopSource = CancellationTokenSource.CreateLinkedTokenSource(processExitSource.Token);
        _clearWorker = Task.Run(RunClearWorker);
    }

    /// <remarks>Disposing the returned reader releases its lease; a write batch keeps its pre-batch snapshot pinned.</remarks>
    public IPbtPersistence.IReader CreateReader()
    {
        SharedReader? cached = Volatile.Read(ref _cachedReader);
        if (cached is not null && cached.TryLease()) return cached;

        using Lock.Scope _ = _cacheLock.EnterScope();

        // The cache's lease keeps an entry published under this lock alive while it is leased.
        _cachedReader ??= new SharedReader(_inner.CreateReader());
        _cachedReader.AcquireLease();
        return _cachedReader;
    }

    /// <remarks>Pins the pre-batch snapshot until the batch is disposed.</remarks>
    public IPbtPersistence.IWriteBatch CreateWriteBatch(in StateId from, in StateId to, in ValueHash256 toTreeRoot, WriteFlags flags)
    {
        PinReaderCache();
        try
        {
            return new CacheClearingWriteBatch(_inner.CreateWriteBatch(from, to, toTreeRoot, flags), this);
        }
        catch
        {
            // An unreturned batch cannot release its pin.
            UnpinReaderCache();
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

    /// <remarks>The last pin is released after its batch commits, so the now-stale snapshot is dropped.</remarks>
    private void UnpinReaderCache()
    {
        SharedReader? cached = null;
        using (_cacheLock.EnterScope())
        {
            if (--_pinDepth == 0) cached = Unpublish();
        }

        cached?.Dispose();
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

    /// <remarks>On shutdown, this prevents a batch that outlives the decorator from retaining the snapshot.</remarks>
    private void DropReaderCache()
    {
        SharedReader? cached;
        using (_cacheLock.EnterScope()) cached = Unpublish();

        cached?.Dispose();
    }

    /// <remarks>Must be called under <see cref="_cacheLock"/>; dispose the returned cache lease outside the lock.</remarks>
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
            while (await timer.WaitForNextTickAsync(_stopSource.Token))
            {
                ClearReaderCache();
            }
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
        DropReaderCache();
        _stopSource.Dispose();
    }

    private sealed class SharedReader(IPbtPersistence.IReader inner) : RefCountingDisposable, IPbtPersistence.IReader
    {
        public StateId CurrentState => inner.CurrentState;

        public ValueHash256 CurrentTreeRoot => inner.CurrentTreeRoot;

        public RefCountingMemory? GetLeafBlob(in Stem stem) => inner.GetLeafBlob(in stem);

        public RefCountingMemory? GetTrieNode(in TrieNodeKey key) => inner.GetTrieNode(in key);

        public bool TryLease() => TryAcquireLease();

        protected override void CleanUp() => inner.Dispose();
    }

    /// <remarks>Releases the pin after applying the inner batch so readers cannot observe a partial batch.</remarks>
    private sealed class CacheClearingWriteBatch(IPbtPersistence.IWriteBatch inner, PbtCachedReaderPersistence parent) : IPbtPersistence.IWriteBatch
    {
        public void SetLeafBlob(in Stem stem, scoped ReadOnlySpan<byte> blob) => inner.SetLeafBlob(in stem, blob);

        public void SetTrieNode(in TrieNodeKey key, scoped ReadOnlySpan<byte> node) => inner.SetTrieNode(in key, node);

        public void Dispose()
        {
            inner.Dispose();
            parent.UnpinReaderCache();
        }
    }
}
