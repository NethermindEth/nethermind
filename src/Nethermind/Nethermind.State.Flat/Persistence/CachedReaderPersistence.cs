// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
                    ReportExperimentStats();
                }
            }
            catch (OperationCanceledException)
            {
            }
        });

        // Prime the reader cache
        using IPersistence.IPersistenceReader reader = CreateReader();
    }

    private static readonly bool ReportStats = Nethermind.Core.ExperimentSwitches.Bool("NM_XP_STATS");

    private void ReportExperimentStats()
    {
        if (!ReportStats || !_logger.IsInfo) return;

        long w = Metrics.CarryForwardProbesWarmer, c = Metrics.CarryForwardProbesCommit, o = Metrics.CarryForwardProbesOther;
        long total = w + c + o;
        _logger.Info(
            $"XPSTATS cf_acct_hits={Metrics.CarryForwardAccountHits} cf_acct_misses={Metrics.CarryForwardAccountMisses} " +
            $"cf_slot_hits={Metrics.CarryForwardSlotHits} cf_slot_misses={Metrics.CarryForwardSlotMisses} " +
            $"cf_wipes={Metrics.CarryForwardWipes} cf_acct_count={Metrics.CarryForwardAccountCount} cf_slot_count={Metrics.CarryForwardSlotCount} " +
            $"probes_warmer={w} probes_commit={c} probes_other={o} " +
            $"pct_warmer={Pct(w, total):F1} pct_commit={Pct(c, total):F1} pct_other={Pct(o, total):F1} " +
            $"storage_cleared={Db.Metrics.StorageCleared} " +
            $"preblock_slot_hits={Db.Metrics.PreBlockCacheStorageHits} preblock_slot_misses={Db.Metrics.PreBlockCacheStorageMisses} " +
            $"preblock_acct_hits={Db.Metrics.PreBlockCacheAccountHits} preblock_acct_misses={Db.Metrics.PreBlockCacheAccountMisses}");

        static double Pct(long part, long whole) => whole == 0 ? 0d : part * 100d / whole;
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

    public IPersistence.IWriteBatch CreateWriteBatch(in StateId from, in StateId to, WriteFlags flags = WriteFlags.None) => new ClearCacheOnWriteBatchComplete(_inner.CreateWriteBatch(from, to, flags), this);

    public void Flush() => _inner.Flush();

    public void Clear()
    {
        ClearReaderCache();
        _inner.Clear();
    }

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
        public void SelfDestruct(Address addr) => inner.SelfDestruct(addr);
        public void SetAccount(Address addr, Account? account) => inner.SetAccount(addr, account);
        public void SetStorage(Address addr, in UInt256 slot, in SlotValue? value) => inner.SetStorage(addr, slot, value);
        public void SetStateTrieNode(in TreePath path, scoped ReadOnlySpan<byte> rlp) => inner.SetStateTrieNode(path, rlp);
        public void SetStorageTrieNode(Hash256 address, in TreePath path, scoped ReadOnlySpan<byte> rlp) => inner.SetStorageTrieNode(address, path, rlp);
        public void SetStorageRawEncoded(in ValueHash256 addrHash, in ValueHash256 slotHash, scoped ReadOnlySpan<byte> rlpValue) => inner.SetStorageRawEncoded(addrHash, slotHash, rlpValue);
        public void SetAccountRaw(in ValueHash256 addrHash, Account account) => inner.SetAccountRaw(addrHash, account);
        public void DeleteAccountRange(in ValueHash256 fromPath, in ValueHash256 toPath) => inner.DeleteAccountRange(fromPath, toPath);
        public void DeleteStorageRange(in ValueHash256 addressHash, in ValueHash256 fromPath, in ValueHash256 toPath) => inner.DeleteStorageRange(addressHash, fromPath, toPath);
        public void DeleteStateTrieNodeRange(in ValueHash256 from, in ValueHash256 to) => inner.DeleteStateTrieNodeRange(from, to);
        public void DeleteStorageTrieNodeRange(in ValueHash256 addressHash, in ValueHash256 from, in ValueHash256 to) => inner.DeleteStorageTrieNodeRange(addressHash, from, to);

        public void Dispose()
        {
            inner.Dispose();

            // not in lock as it has its own lock
            parent.ClearReaderCache();
        }
    }
}
