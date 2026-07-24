// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Threading;
using CkzgLib;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Threading;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.TxPool.Collections;

public class PersistentBlobTxDistinctSortedPool : BlobTxDistinctSortedPool, IDisposable
{
    private const int MaxBlobCellCandidates = BlobCellMask.CellCount;
    private const int MaxFullBlobCandidatesPerHash = 4;
    private readonly ITxStorage _blobTxStorage;
    private readonly LruCache<ValueHash256, Transaction> _blobTxCache;
    private readonly ILogger _logger;
    private readonly Dictionary<ValueHash256, PendingBlobUpdate> _pendingBlobUpdates = [];
    private readonly Dictionary<ValueHash256, Transaction> _unpersistableBlobUpdates = [];
    private readonly int _maxPendingBlobUpdates;
    private const int MaxBlobUpdateWriteAttempts = 2;
    private const int MaxBlobUpdateRetryExponent = 5;
    private static readonly TimeSpan InitialBlobUpdateRetryDelay = TimeSpan.FromSeconds(1);
    private readonly object _blobUpdateRetryTimerLock = new();
    private Timer? _blobUpdateRetryTimer;
    private DateTimeOffset? _nextBlobUpdateRetryAt;
    private long _nextBlobUpdateToken;
    private int _disposed;

    public PersistentBlobTxDistinctSortedPool(ITxStorage blobTxStorage, ITxPoolConfig txPoolConfig, IComparer<Transaction> comparer, ILogManager logManager)
        : base(txPoolConfig.PersistentBlobStorageSize, comparer, logManager)
    {
        _blobTxStorage = blobTxStorage ?? throw new ArgumentNullException(nameof(blobTxStorage));
        _blobTxCache = new(txPoolConfig.BlobCacheSize, txPoolConfig.BlobCacheSize, "blob txs cache");
        _maxPendingBlobUpdates = Math.Max(1, txPoolConfig.BlobCacheSize);
        _logger = logManager?.GetClassLogger<PersistentBlobTxDistinctSortedPool>() ?? throw new ArgumentNullException(nameof(logManager));

        RecreateLightTxCollectionAndCache(blobTxStorage);
    }

    private void RecreateLightTxCollectionAndCache(ITxStorage blobTxStorage)
    {
        if (_logger.IsTrace) _logger.Trace("Recreating light collection of blob transactions and cache");
        int numberOfTxsInDb = 0;
        int numberOfBlobsInDb = 0;
        long startTime = Stopwatch.GetTimestamp();
        foreach (LightTransaction lightBlobTx in blobTxStorage.GetAll())
        {
            if (lightBlobTx.SenderAddress is not null
                && base.InsertCore(lightBlobTx.Hash, lightBlobTx, lightBlobTx.SenderAddress))
            {
                numberOfTxsInDb++;
                numberOfBlobsInDb += lightBlobTx.GetBlobCount();
            }
        }

        if (_logger.IsInfo && numberOfTxsInDb != 0)
        {
            long loadingTime = (long)Stopwatch.GetElapsedTime(startTime).TotalMilliseconds;
            _logger.Info($"Loaded {numberOfTxsInDb} blob txs from persistent db, containing {numberOfBlobsInDb} blobs, in {loadingTime:N0}ms");
            _logger.Info($"There are {BlobIndex.Count} unique blobs indexed");
        }
    }

    protected override bool InsertCore(ValueHash256 hash, Transaction fullBlobTx, AddressAsKey groupKey)
    {
        if (base.InsertCore(hash, new LightTransaction(fullBlobTx), groupKey))
        {
            _blobTxCache.Set(fullBlobTx.Hash, fullBlobTx);
            _blobTxStorage.Add(fullBlobTx);
            if (_pendingBlobUpdates.TryGetValue(hash, out PendingBlobUpdate? pendingUpdate)
                && pendingUpdate.WriterActive)
            {
                TrackBlobUpdateNonLocked(fullBlobTx);
            }
            return true;
        }

        return false;
    }

    protected override bool TryGetValueNonLocked(ValueHash256 hash, [NotNullWhen(true)] out Transaction? fullBlobTx)
    {
        // Firstly check if tx is present in in-memory collection of light blob txs (without actual blobs).
        // If not, just return false
        if (base.TryGetValueNonLocked(hash, out Transaction? lightTx))
        {
            if (_pendingBlobUpdates.TryGetValue(hash, out PendingBlobUpdate? pendingUpdate)
                && pendingUpdate.Transaction is not null)
            {
                fullBlobTx = pendingUpdate.Transaction;
                return true;
            }

            // tx is present in light collection. Try to get full blob tx from cache
            if (_blobTxCache.TryGet(hash, out fullBlobTx))
            {
                return true;
            }

            // tx is present, but not cached, at this point we need to load it from db...
            if (_blobTxStorage.TryGet(hash, lightTx.SenderAddress!, lightTx.Timestamp, out fullBlobTx))
            {
                // ...and we are saving recently used blob tx to cache
                _blobTxCache.Set(hash, fullBlobTx);
                return true;
            }
        }

        fullBlobTx = default;
        return false;
    }

    protected override bool TryGetValueForCellMerge(ValueHash256 hash, [NotNullWhen(true)] out Transaction? blobTx)
        => TryGetFullBlobTransactionOutsideLock(hash, out blobTx);

    protected override bool TryGetValueForCellMergeNonLocked(ValueHash256 hash, [NotNullWhen(true)] out Transaction? blobTx)
    {
        if (!base.TryGetValueNonLocked(hash, out _))
        {
            blobTx = default;
            return false;
        }

        if (_pendingBlobUpdates.TryGetValue(hash, out PendingBlobUpdate? pendingUpdate)
            && pendingUpdate.Transaction is not null)
        {
            blobTx = pendingUpdate.Transaction;
            return true;
        }

        return _blobTxCache.TryGet(hash, out blobTx);
    }

    public override int TryGetBlobsAndProofsV1(
         byte[][] requestedBlobVersionedHashes,
         Span<byte[]?> blobs,
         Span<ReadOnlyMemory<byte[]>> proofs)
    {
        int found = 0;
        using ArrayPoolList<TxLookupKey> dbKeys = new(requestedBlobVersionedHashes.Length);
        using ArrayPoolList<Transaction> dbLightTransactions = new(requestedBlobVersionedHashes.Length);
        using ArrayPoolList<int> missOutputIndex = new(requestedBlobVersionedHashes.Length);
        using ArrayPoolList<int> missBlobIndex = new(requestedBlobVersionedHashes.Length);

        // Phase 1: Under lock — in-memory lookups only
        using (McsLock.Disposable lockRelease = Lock.Acquire())
        {
            for (int i = 0; i < requestedBlobVersionedHashes.Length; i++)
            {
                byte[] requestedBlobVersionedHash = requestedBlobVersionedHashes[i];
                if (!BlobIndex.TryGetValue(requestedBlobVersionedHash, out List<Hash256>? txHashes))
                    continue;

                foreach (Hash256 hash in CollectionsMarshal.AsSpan(txHashes))
                {
                    if (!TryGetFullBlobCandidateNonLocked(hash, requestedBlobVersionedHash, out _, out int indexOfBlob))
                        continue;

                    // Try cache first — on hit, populate and stop searching for this blob hash
                    Transaction? cachedTx = _pendingBlobUpdates.TryGetValue(hash, out PendingBlobUpdate? pendingUpdate)
                        ? pendingUpdate.Transaction
                        : null;
                    if ((cachedTx is not null || _blobTxCache.TryGet(hash, out cachedTx))
                        && cachedTx.NetworkWrapper is ShardBlobNetworkWrapper { Version: ProofVersion.V1 } cachedWrapper
                        && cachedWrapper.HasFullBlobs())
                    {
                        blobs[i] = cachedWrapper.Blobs[indexOfBlob];
                        proofs[i] = new ReadOnlyMemory<byte[]>(
                            cachedWrapper.Proofs,
                            Ckzg.CellsPerExtBlob * indexOfBlob,
                            Ckzg.CellsPerExtBlob);
                        found++;
                        break;
                    }
                }

                if (blobs[i] is not null)
                {
                    continue;
                }

                int dbCandidateCount = 0;
                foreach (Hash256 hash in CollectionsMarshal.AsSpan(txHashes))
                {
                    if (TryGetFullBlobCandidateNonLocked(
                        hash,
                        requestedBlobVersionedHash,
                        out Transaction? lightTx,
                        out int indexOfBlob))
                    {
                        dbKeys.Add(new TxLookupKey(hash, lightTx.SenderAddress!, lightTx.Timestamp));
                        dbLightTransactions.Add(lightTx);
                        missOutputIndex.Add(i);
                        missBlobIndex.Add(indexOfBlob);
                        if (++dbCandidateCount == MaxFullBlobCandidatesPerHash)
                        {
                            break;
                        }
                    }
                }
            }
        }

        // Phase 2: Outside lock — single RocksDB MultiGet for all misses
        int missCount = dbKeys.Count;
        if (missCount > 0)
        {
            Transaction?[] dbResults = ArrayPool<Transaction?>.Shared.Rent(missCount);
            try
            {
                Array.Clear(dbResults, 0, missCount);
                _blobTxStorage.TryGetMany(dbKeys.UnsafeGetInternalArray(), missCount, dbResults);

                using McsLock.Disposable lockRelease = Lock.Acquire();
                for (int m = 0; m < missCount; m++)
                {
                    int outIdx = missOutputIndex[m];
                    if (blobs[outIdx] is not null)
                    {
                        continue;
                    }

                    TxLookupKey dbKey = dbKeys[m];
                    if (!base.TryGetValueNonLocked(dbKey.Hash, out Transaction? currentLightTx)
                        || !ReferenceEquals(currentLightTx, dbLightTransactions[m])
                        || currentLightTx.SenderAddress != dbKey.Sender
                        || currentLightTx.Timestamp != dbKey.Timestamp
                        || currentLightTx is not LightTransaction currentLightBlobTx
                        || currentLightBlobTx.ProofVersion != ProofVersion.V1
                        || !currentLightBlobTx.BlobCellMask.IsFull)
                    {
                        continue;
                    }

                    Transaction? fullTx;
                    bool cacheStorageResult = false;
                    if (_pendingBlobUpdates.TryGetValue(dbKey.Hash, out PendingBlobUpdate? pendingUpdate)
                        && pendingUpdate.Transaction is not null)
                    {
                        fullTx = pendingUpdate.Transaction;
                    }
                    else if (!_blobTxCache.TryGet(dbKey.Hash, out fullTx))
                    {
                        fullTx = dbResults[m];
                        cacheStorageResult = true;
                    }

                    int blobIdx = missBlobIndex[m];
                    if (fullTx?.NetworkWrapper is not ShardBlobNetworkWrapper { Version: ProofVersion.V1 } wrapper
                        || !wrapper.HasFullBlobs()
                        || (uint)blobIdx >= (uint)wrapper.Blobs.Length
                        || Ckzg.CellsPerExtBlob * (blobIdx + 1) > wrapper.Proofs.Length)
                    {
                        continue;
                    }

                    blobs[outIdx] = wrapper.Blobs[blobIdx];
                    proofs[outIdx] = new ReadOnlyMemory<byte[]>(
                        wrapper.Proofs,
                        Ckzg.CellsPerExtBlob * blobIdx,
                        Ckzg.CellsPerExtBlob);
                    found++;

                    if (cacheStorageResult)
                    {
                        _blobTxCache.Set(dbKey.Hash, fullTx);
                    }
                }
            }
            finally
            {
                ArrayPool<Transaction?>.Shared.Return(dbResults, clearArray: true);
            }
        }

        return found;
    }

    private bool TryGetFullBlobCandidateNonLocked(
        Hash256 hash,
        byte[] requestedBlobVersionedHash,
        [NotNullWhen(true)] out Transaction? lightTx,
        out int blobIndex)
    {
        if (!base.TryGetValueNonLocked(hash, out lightTx)
            || lightTx is not LightTransaction { ProofVersion: ProofVersion.V1, BlobCellMask.IsFull: true }
            || lightTx.BlobVersionedHashes is not { Length: > 0 } blobVersionedHashes)
        {
            blobIndex = -1;
            return false;
        }

        for (int i = 0; i < blobVersionedHashes.Length; i++)
        {
            if (Bytes.AreEqual(blobVersionedHashes[i], requestedBlobVersionedHash))
            {
                blobIndex = i;
                return true;
            }
        }

        lightTx = default;
        blobIndex = -1;
        return false;
    }

    public override bool TryGetCells(ValueHash256 hash, BlobCellMask requestedMask, out BlobCellMask availableMask, out byte[][]? cells)
    {
        if (TryGetFullBlobTransactionOutsideLock(hash, out Transaction? blobTx)
            && blobTx.NetworkWrapper is ShardBlobNetworkWrapper wrapper
            && BlobCellsHelper.TryGetFlattenedCells(wrapper, requestedMask, out byte[][] flattenedCells))
        {
            availableMask = wrapper.GetAvailableCellMask() & requestedMask;
            cells = flattenedCells;
            return true;
        }

        availableMask = default;
        cells = default;
        return false;
    }

    public override bool TryGetBlobCellsAndProofsV1(
        byte[] requestedBlobVersionedHash,
        BlobCellMask requestedMask,
        out BlobCellMask availableMask,
        out byte[][]? cells,
        out byte[][]? proofs)
    {
        List<PersistentBlobCellsCandidate>? candidates = null;
        bool hasV1Candidate = false;
        using (McsLock.Disposable lockRelease = Lock.Acquire())
        {
            if (!BlobIndex.TryGetValue(requestedBlobVersionedHash, out List<Hash256>? txHashes))
            {
                availableMask = default;
                cells = default;
                proofs = default;
                return false;
            }

            candidates = new(Math.Min(txHashes.Count, MaxBlobCellCandidates));
            BlobCellMask capturedMask = BlobCellMask.Empty;
            foreach (Hash256 hash in CollectionsMarshal.AsSpan(txHashes))
            {
                if (!base.TryGetValueNonLocked(hash, out Transaction? lightTx)
                    || lightTx is not LightTransaction { ProofVersion: ProofVersion.V1 } lightBlobTx
                    || !TryFindBlobIndex(lightTx, requestedBlobVersionedHash, out int blobIndex))
                {
                    continue;
                }

                hasV1Candidate = true;
                BlobCellMask candidateMask = lightBlobTx.BlobCellMask & requestedMask;
                if (!candidateMask.Except(capturedMask).IsEmpty)
                {
                    candidates.Add(new PersistentBlobCellsCandidate(hash.ValueHash256, blobIndex, candidateMask));
                    capturedMask |= candidateMask;
                    if (capturedMask == requestedMask || candidates.Count == MaxBlobCellCandidates)
                    {
                        break;
                    }
                }
            }
        }

        if (candidates.Count == 0)
        {
            availableMask = default;
            cells = hasV1Candidate ? [] : default;
            proofs = hasV1Candidate ? [] : default;
            return hasV1Candidate;
        }

        candidates.Sort(static (x, y) => y.AvailableMask.Count.CompareTo(x.AvailableMask.Count));
        List<BlobCellsCandidate> loadedCandidates = new(Math.Min(candidates.Count, requestedMask.Count));
        BlobCellMask loadedMask = BlobCellMask.Empty;
        int candidateLoads = 0;
        for (int i = 0;
             i < candidates.Count && loadedMask != requestedMask && candidateLoads < BlobCellMask.CellCount;
             i++)
        {
            PersistentBlobCellsCandidate candidate = candidates[i];
            if ((candidate.AvailableMask.Except(loadedMask)).IsEmpty)
            {
                continue;
            }

            candidateLoads++;
            if (!TryGetFullBlobTransactionOutsideLock(candidate.Hash, out Transaction? blobTx)
                || blobTx.NetworkWrapper is not ShardBlobNetworkWrapper { Version: ProofVersion.V1 } wrapper)
            {
                continue;
            }

            BlobCellMask actualMask = wrapper.GetAvailableCellMask() & requestedMask;
            if ((actualMask.Except(loadedMask)).IsEmpty)
            {
                continue;
            }

            loadedCandidates.Add(new BlobCellsCandidate(wrapper, candidate.BlobIndex));
            loadedMask |= actualMask;
        }

        return TryBuildBlobCellsAndProofsResponse(loadedCandidates, requestedMask, out availableMask, out cells, out proofs);
    }

    private bool TryGetFullBlobTransactionOutsideLock(ValueHash256 hash, [NotNullWhen(true)] out Transaction? fullBlobTx)
    {
        Transaction? lightTx;
        BlobCellMask lightCellMask;
        using (McsLock.Disposable lockRelease = Lock.Acquire())
        {
            if (!base.TryGetValueNonLocked(hash, out lightTx))
            {
                fullBlobTx = default;
                return false;
            }

            if (_pendingBlobUpdates.TryGetValue(hash, out PendingBlobUpdate? pendingUpdate)
                && pendingUpdate.Transaction is not null)
            {
                fullBlobTx = pendingUpdate.Transaction;
                return true;
            }

            if (_blobTxCache.TryGet(hash, out fullBlobTx))
            {
                return true;
            }

            lightCellMask = lightTx is LightTransaction lightBlobTx
                ? lightBlobTx.BlobCellMask
                : BlobCellMask.Empty;
        }

        if (lightTx.SenderAddress is null
            || !_blobTxStorage.TryGet(hash, lightTx.SenderAddress, lightTx.Timestamp, out fullBlobTx))
        {
            fullBlobTx = default;
            return false;
        }

        using (McsLock.Disposable lockRelease = Lock.Acquire())
        {
            if (!base.TryGetValueNonLocked(hash, out Transaction? currentLightTx))
            {
                fullBlobTx = default;
                return false;
            }

            if (_pendingBlobUpdates.TryGetValue(hash, out PendingBlobUpdate? pendingUpdate)
                && pendingUpdate.Transaction is not null)
            {
                fullBlobTx = pendingUpdate.Transaction;
                _blobTxCache.Set(hash, fullBlobTx);
                return true;
            }

            if (_blobTxCache.TryGet(hash, out Transaction? currentCachedTx))
            {
                fullBlobTx = currentCachedTx;
                return true;
            }

            if (!ReferenceEquals(currentLightTx, lightTx)
                || currentLightTx.Timestamp != lightTx.Timestamp
                || currentLightTx is LightTransaction currentLightBlobTx
                    && currentLightBlobTx.BlobCellMask != lightCellMask)
            {
                fullBlobTx = default;
                return false;
            }

            _blobTxCache.Set(hash, fullBlobTx);
            return true;
        }
    }

    protected override bool Remove(ValueHash256 hash, out Transaction? tx)
    {
        if (base.Remove(hash, out tx))
        {
            _unpersistableBlobUpdates.Remove(hash);
            PendingBlobUpdate? activeUpdate = null;
            if (_pendingBlobUpdates.TryGetValue(hash, out PendingBlobUpdate? pendingUpdate))
            {
                pendingUpdate.Token = ++_nextBlobUpdateToken;
                pendingUpdate.Transaction = null;
                if (pendingUpdate.WriterActive)
                {
                    activeUpdate = pendingUpdate;
                }
                else
                {
                    _pendingBlobUpdates.Remove(hash);
                }
            }

            if (tx is not null)
            {
                if (activeUpdate is null)
                {
                    _blobTxStorage.Delete(hash, tx.Timestamp);
                }
                else
                {
                    lock (activeUpdate.StorageLock)
                    {
                        _blobTxStorage.Delete(hash, tx.Timestamp);
                    }
                }
            }

            _blobTxCache.Delete(hash);

            return true;
        }

        return false;
    }

    protected override void OnBlobTransactionUpdatedNonLocked(Transaction blobTx)
    {
        // Keep the in-memory light entry's mask in sync so mask-only queries and
        // announcements reflect the merged cells without loading the full transaction.
        TryGetBlobTxSortingEquivalent(blobTx.Hash!, out Transaction? lightTx);
        if (lightTx is LightTransaction light)
        {
            light.BlobCellMask = (blobTx.NetworkWrapper as ShardBlobNetworkWrapper)?.GetAvailableCellMask() ?? default;
        }

        _blobTxCache.Set(blobTx.Hash, blobTx);
        if (!TrackBlobUpdateNonLocked(blobTx) && lightTx is not null)
        {
            _unpersistableBlobUpdates[blobTx.Hash!.ValueHash256] = lightTx;
        }
    }

    protected override void OnBlobTransactionUpdated(ValueHash256 hash, in UInt256 updateTimestamp)
    {
        PendingBlobUpdate state;
        bool removeUnpersistableTransaction;
        using (McsLock.Disposable lockRelease = Lock.Acquire())
        {
            removeUnpersistableTransaction = _unpersistableBlobUpdates.Remove(hash, out Transaction? failedGeneration)
                && base.TryGetValueNonLocked(hash, out Transaction? currentLightTx)
                && ReferenceEquals(currentLightTx, failedGeneration);
            if (removeUnpersistableTransaction)
            {
                state = null!;
            }
            else if (!_pendingBlobUpdates.TryGetValue(hash, out PendingBlobUpdate? pendingState)
                || pendingState.WriterActive)
            {
                return;
            }
            else
            {
                state = pendingState;
                state.WriterActive = true;
                state.NextRetryAt = null;
            }
        }

        if (removeUnpersistableTransaction)
        {
            try
            {
                TryRemove(hash);
            }
            catch (Exception ex)
            {
                if (_logger.IsError) _logger.Error($"Failed to evict blob transaction {hash} after persistence retry capacity was exhausted.", ex);
            }

            if (_logger.IsError) _logger.Error($"Evicted blob transaction {hash} because persistence retry capacity was exhausted.");
            return;
        }

        int failedWriteAttempts = 0;
        while (true)
        {
            long token;
            Transaction? transaction;
            UInt256 persistenceTimestamp;
            using (McsLock.Disposable lockRelease = Lock.Acquire())
            {
                if (!_pendingBlobUpdates.TryGetValue(hash, out PendingBlobUpdate? current)
                    || !ReferenceEquals(current, state))
                {
                    return;
                }

                token = current.Token;
                transaction = current.Transaction;
                persistenceTimestamp = current.Timestamp;
            }

            try
            {
                lock (state.StorageLock)
                {
                    if (transaction is null)
                    {
                        _blobTxStorage.Delete(hash, persistenceTimestamp);
                    }
                    else
                    {
                        _blobTxStorage.Add(transaction);
                    }
                }

                failedWriteAttempts = 0;
                using McsLock.Disposable lockRelease = Lock.Acquire();
                if (!_pendingBlobUpdates.TryGetValue(hash, out PendingBlobUpdate? current)
                    || !ReferenceEquals(current, state))
                {
                    return;
                }

                if (current.Token == token)
                {
                    _pendingBlobUpdates.Remove(hash);
                    return;
                }
            }
            catch (Exception ex)
            {
                bool retry;
                DateTimeOffset? retryAt = null;
                using (McsLock.Disposable lockRelease = Lock.Acquire())
                {
                    if (!_pendingBlobUpdates.TryGetValue(hash, out PendingBlobUpdate? current)
                        || !ReferenceEquals(current, state))
                    {
                        throw;
                    }

                    retry = current.Token != token || ++failedWriteAttempts < MaxBlobUpdateWriteAttempts;
                    if (!retry)
                    {
                        current.WriterActive = false;
                        int retryExponent = Math.Min(current.RetryCount++, MaxBlobUpdateRetryExponent);
                        retryAt = DateTimeOffset.UtcNow + InitialBlobUpdateRetryDelay * (1 << retryExponent);
                        current.NextRetryAt = retryAt;
                    }
                }

                if (!retry)
                {
                    if (_logger.IsError) _logger.Error($"Failed to persist blob transaction update for {hash}; retry scheduled.", ex);
                    ScheduleBlobUpdateRetry(retryAt!.Value);
                    return;
                }
            }
        }
    }

    private void ScheduleBlobUpdateRetry(DateTimeOffset retryAt)
    {
        lock (_blobUpdateRetryTimerLock)
        {
            if (Volatile.Read(ref _disposed) != 0
                || _nextBlobUpdateRetryAt is { } scheduledAt && scheduledAt <= retryAt)
            {
                return;
            }

            _nextBlobUpdateRetryAt = retryAt;
            _blobUpdateRetryTimer ??= new Timer(
                static state => ((PersistentBlobTxDistinctSortedPool)state!).RetryPendingBlobUpdatesSafely(),
                this,
                Timeout.InfiniteTimeSpan,
                Timeout.InfiniteTimeSpan);
            TimeSpan dueTime = retryAt - DateTimeOffset.UtcNow;
            _blobUpdateRetryTimer.Change(dueTime > TimeSpan.Zero ? dueTime : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        }
    }

    private void RetryPendingBlobUpdatesSafely()
    {
        try
        {
            RetryPendingBlobUpdates();
        }
        catch (Exception ex)
        {
            if (_logger.IsError) _logger.Error("Failed to run blob transaction persistence retries; retry rescheduled.", ex);
            ScheduleBlobUpdateRetry(DateTimeOffset.UtcNow + InitialBlobUpdateRetryDelay);
        }
    }

    private void RetryPendingBlobUpdates()
    {
        lock (_blobUpdateRetryTimerLock)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            _nextBlobUpdateRetryAt = null;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        List<(ValueHash256 Hash, UInt256 Timestamp)>? retries = null;
        DateTimeOffset? nextRetryAt = null;
        using (McsLock.Disposable lockRelease = Lock.Acquire())
        {
            foreach (KeyValuePair<ValueHash256, PendingBlobUpdate> entry in _pendingBlobUpdates)
            {
                PendingBlobUpdate state = entry.Value;
                if (state.WriterActive)
                {
                    continue;
                }

                if (state.NextRetryAt is { } retryAt && retryAt > now)
                {
                    nextRetryAt = nextRetryAt is null || retryAt < nextRetryAt ? retryAt : nextRetryAt;
                    continue;
                }

                (retries ??= []).Add((entry.Key, state.Timestamp));
            }
        }

        if (nextRetryAt is { } scheduledRetryAt)
        {
            ScheduleBlobUpdateRetry(scheduledRetryAt);
        }

        if (retries is null)
        {
            return;
        }

        for (int i = 0; i < retries.Count; i++)
        {
            (ValueHash256 hash, UInt256 timestamp) = retries[i];
            OnBlobTransactionUpdated(hash, timestamp);
        }
    }

    private bool TrackBlobUpdateNonLocked(Transaction blobTx)
    {
        if (!_pendingBlobUpdates.ContainsKey(blobTx.Hash!)
            && _pendingBlobUpdates.Count >= _maxPendingBlobUpdates)
        {
            return false;
        }

        Transaction snapshot = new();
        blobTx.CopyTo(snapshot);
        long token = ++_nextBlobUpdateToken;
        if (_pendingBlobUpdates.TryGetValue(blobTx.Hash!, out PendingBlobUpdate? pendingUpdate))
        {
            pendingUpdate.Token = token;
            pendingUpdate.Transaction = snapshot;
            pendingUpdate.Timestamp = blobTx.Timestamp;
            pendingUpdate.RetryCount = 0;
            pendingUpdate.NextRetryAt = null;
        }
        else
        {
            _pendingBlobUpdates[blobTx.Hash!] = new PendingBlobUpdate(token, snapshot, blobTx.Timestamp);
        }

        return true;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Timer? retryTimer;
        lock (_blobUpdateRetryTimerLock)
        {
            retryTimer = _blobUpdateRetryTimer;
            _blobUpdateRetryTimer = null;
            _nextBlobUpdateRetryAt = null;
        }

        if (retryTimer is not null)
        {
            using ManualResetEvent disposed = new(initialState: false);
            retryTimer.Dispose(disposed);
            disposed.WaitOne();
        }
    }

    private readonly record struct PersistentBlobCellsCandidate(
        ValueHash256 Hash,
        int BlobIndex,
        BlobCellMask AvailableMask);

    private sealed class PendingBlobUpdate(long token, Transaction? transaction, UInt256 timestamp)
    {
        public long Token { get; set; } = token;
        public Transaction? Transaction { get; set; } = transaction;
        public UInt256 Timestamp { get; set; } = timestamp;
        public bool WriterActive { get; set; }
        public object StorageLock { get; } = new();
        public int RetryCount { get; set; }
        public DateTimeOffset? NextRetryAt { get; set; }
    }
}
