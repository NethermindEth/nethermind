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
/// </remarks>
public sealed class PbtCachedReaderPersistence : IPbtPersistence, IAsyncDisposable
{
    private static readonly TimeSpan ClearInterval = TimeSpan.FromSeconds(5);

    private readonly IPbtPersistence _inner;
    private readonly Lock _cacheLock = new();
    private readonly CancellationTokenSource _stopSource;
    private readonly Task _clearWorker;

    private SharedReader? _cachedReader;
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

    public IPbtPersistence.IWriteBatch CreateWriteBatch(in StateId from, in StateId to, in ValueHash256 toTreeRoot, WriteFlags flags) =>
        new CacheClearingWriteBatch(_inner.CreateWriteBatch(from, to, toTreeRoot, flags), this);

    public void Flush() => _inner.Flush();

    /// <remarks>
    /// Unpublishes before releasing, so a concurrent <see cref="CreateReader"/> either leases the entry
    /// while it is still published, or misses it and takes a fresh snapshot. Readers already handed out
    /// keep their snapshot alive on their own leases.
    /// </remarks>
    private void ClearReaderCache()
    {
        SharedReader? cached;
        using (_cacheLock.EnterScope())
        {
            cached = _cachedReader;
            _cachedReader = null;
        }

        cached?.Dispose();
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
        ClearReaderCache();
        _stopSource.Dispose();
    }

    /// <summary>A reader over a snapshot shared by everyone holding a lease on it.</summary>
    /// <remarks>Constructed with the one lease the cache itself holds; <see cref="ClearReaderCache"/> releases it.</remarks>
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
    /// The commit is what makes the cached snapshot stale, so the cache is dropped on disposal rather
    /// than on creation: a reader taken while the batch is still open sees the state the batch started
    /// from, which is exactly what the writer itself reads back.
    /// </remarks>
    private sealed class CacheClearingWriteBatch(IPbtPersistence.IWriteBatch inner, PbtCachedReaderPersistence parent) : IPbtPersistence.IWriteBatch
    {
        public void SetTrieNode(in TrieNodeKey key, byte[]? node) => inner.SetTrieNode(in key, node);

        public void SetLeafBlob(in Stem stem, scoped ReadOnlySpan<byte> blob) => inner.SetLeafBlob(in stem, blob);

        public void SetTrieNode(in TrieNodeKey key, scoped ReadOnlySpan<byte> node) => inner.SetTrieNode(in key, node);

        public void Dispose()
        {
            inner.Dispose();
            parent.ClearReaderCache();
        }
    }
}
