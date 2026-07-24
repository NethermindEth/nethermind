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

/// <summary>
/// Decorates <see cref="IPbtPersistence"/> so that readers asking for the same state share one
/// database snapshot rather than each creating its own.
/// </summary>
/// <remarks>
/// Gathering a bundle takes a reader on every miss, and taking a rocksdb snapshot across every column
/// is far from free, so the reader is shared for as long as it stays current. It stops being current
/// when a write batch commits, hence the invalidation there - before <see cref="PbtSnapshotRepository"/>
/// prunes the layers a stale floor would still be walked to. A snapshot also pins the SST files it was
/// taken over, holding off compaction, so the cache is dropped periodically even when nothing is
/// written.
/// <para>
/// The entry is also what keeps a reader from ever observing a half-applied batch: it is taken before
/// the batch starts and pinned until after the batch is disposed, so nothing takes a snapshot while
/// the columns are being written. Not every backend applies a batch atomically across the columns -
/// the in-memory column store commits one column at a time, and rocksdb splits a
/// <see cref="WriteFlags.DisableWAL"/> batch into chunks - and a torn snapshot is worse than a stale
/// one: a metadata column ahead of the leaf columns yields a reader whose <c>CurrentState</c> is a
/// floor the layers holding the missing writes sit below, so they are walked past rather than read.
/// </para>
/// </remarks>
public sealed class PbtCachedReaderPersistence : IPbtPersistence, IAsyncDisposable
{
    private static readonly TimeSpan ClearInterval = TimeSpan.FromSeconds(5);

    private readonly IPbtPersistence _inner;
    private readonly Lock _cacheLock = new();
    private readonly CancellationTokenSource _stopSource;
    private readonly Task _clearWorker;

    private SharedReader? _cachedReader;

    /// <summary>How many write batches are open, and so must keep <see cref="_cachedReader"/> as it is.</summary>
    /// <remarks>Read and written under <see cref="_cacheLock"/> only.</remarks>
    private int _pinDepth;

    private int _isDisposed;

    public PbtCachedReaderPersistence(IPbtPersistence inner, IProcessExitSource processExitSource)
    {
        _inner = inner;
        _stopSource = CancellationTokenSource.CreateLinkedTokenSource(processExitSource.Token);
        _clearWorker = Task.Run(RunClearWorker);
    }

    /// <remarks>
    /// The returned reader may be shared with other callers; disposing it releases only this caller's
    /// lease, and the snapshot underneath lives until the cache drops it and every lease is gone.
    /// While a write batch is open the entry is pinned, so this hands back the snapshot taken before
    /// that batch rather than taking one of its own.
    /// </remarks>
    public IPbtPersistence.IReader CreateReader()
    {
        SharedReader? cached = Volatile.Read(ref _cachedReader);
        if (cached is not null && cached.TryLease()) return cached;

        using Lock.Scope _ = _cacheLock.EnterScope();

        // the field is only ever written under this lock, and the cache holds a lease of its own for
        // as long as it publishes an entry, so leasing what is published here cannot fail
        _cachedReader ??= new SharedReader(_inner.CreateReader());
        _cachedReader.AcquireLease();
        return _cachedReader;
    }

    /// <remarks>
    /// Takes the shared snapshot before handing the batch out, so every reader for the batch's
    /// lifetime is served the state it started from and none is taken while it is being applied.
    /// </remarks>
    public IPbtPersistence.IWriteBatch CreateWriteBatch(in StateId from, in StateId to, in ValueHash256 toTreeRoot, WriteFlags flags)
    {
        PinReaderCache();
        try
        {
            return new CacheClearingWriteBatch(_inner.CreateWriteBatch(from, to, toTreeRoot, flags), this);
        }
        catch
        {
            // a batch that was never handed out will never be disposed, and a pin left behind would
            // keep the cache from ever dropping a snapshot again
            UnpinReaderCache();
            throw;
        }
    }

    public void Flush() => _inner.Flush();

    /// <summary>Publishes a snapshot if there is none, and holds whatever is published against being dropped.</summary>
    private void PinReaderCache()
    {
        using Lock.Scope _ = _cacheLock.EnterScope();

        // the cache holds a lease of its own for as long as it publishes an entry, so the pin alone
        // is enough to keep this reader alive and current for every caller until it is released
        _cachedReader ??= new SharedReader(_inner.CreateReader());
        _pinDepth++;
    }

    /// <summary>Releases one pin, dropping the snapshot once it was the last one.</summary>
    /// <remarks>
    /// The batch that took the pin has committed by the time this runs, which is what makes the
    /// snapshot stale, so the last release drops it rather than leaving it published.
    /// </remarks>
    private void UnpinReaderCache()
    {
        SharedReader? cached = null;
        using (_cacheLock.EnterScope())
        {
            if (--_pinDepth == 0) cached = Unpublish();
        }

        cached?.Dispose();
    }

    /// <summary>Drops the published snapshot unless a write batch is holding it.</summary>
    private void ClearReaderCache()
    {
        SharedReader? cached = null;
        using (_cacheLock.EnterScope())
        {
            if (_pinDepth == 0) cached = Unpublish();
        }

        cached?.Dispose();
    }

    /// <summary>Drops the published snapshot whether or not a write batch is holding it.</summary>
    /// <remarks>
    /// For shutdown, where a batch that outlives this decorator would otherwise leak the snapshot;
    /// its <see cref="UnpinReaderCache"/> then finds nothing published and is a no-op.
    /// </remarks>
    private void DropReaderCache()
    {
        SharedReader? cached;
        using (_cacheLock.EnterScope()) cached = Unpublish();

        cached?.Dispose();
    }

    /// <summary>Takes the entry out of the cache, passing the cache's own lease on to the caller.</summary>
    /// <remarks>
    /// Must be called under <see cref="_cacheLock"/>, and the entry disposed outside it. Unpublishing
    /// before releasing means a concurrent <see cref="CreateReader"/> either leases the entry while it
    /// is still published, or misses it and takes a fresh snapshot. Readers already handed out keep
    /// their snapshot alive on their own leases.
    /// </remarks>
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

    /// <summary>A reader over a snapshot shared by everyone holding a lease on it.</summary>
    /// <remarks>Constructed with the one lease the cache itself holds; whoever <see cref="Unpublish"/> hands it to releases it.</remarks>
    private sealed class SharedReader(IPbtPersistence.IReader inner) : RefCountingDisposable, IPbtPersistence.IReader
    {
        public StateId CurrentState => inner.CurrentState;

        public ValueHash256 CurrentTreeRoot => inner.CurrentTreeRoot;

        public RefCountingMemory? GetLeafBlob(in Stem stem) => inner.GetLeafBlob(in stem);

        public RefCountingMemory? GetTrieNode(in TrieNodeKey key) => inner.GetTrieNode(in key);

        public bool TryLease() => TryAcquireLease();

        protected override void CleanUp() => inner.Dispose();
    }

    /// <remarks>
    /// The commit is what makes the pinned snapshot stale, so the pin is released after the inner
    /// batch is disposed and not before: until then every reader sees the state the batch started
    /// from, which is exactly what the writer itself reads back, and no reader can catch the columns
    /// mid-apply.
    /// </remarks>
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
