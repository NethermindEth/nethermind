// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Autofac.Features.AttributeFilters;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Snap;

namespace Nethermind.Synchronization.SnapSync
{
    public class SnapProvider(ProgressTracker progressTracker, [KeyFilter(DbNames.Code)] IDb codeDb, ISnapTrieFactory trieFactory, ILogManager logManager, ISyncConfig? syncConfig = null) : ISnapProvider
    {
        private const int MinSingleStorageResponseBatchSlots = 1024;
        private const int MaxStorageRangeParallelism = 8;

        private readonly IDb _codeDb = codeDb;
        private readonly ILogger _logger = logManager.GetClassLogger<SnapProvider>();

        private readonly ProgressTracker _progressTracker = progressTracker;
        private readonly ISnapTrieFactory _trieFactory = trieFactory;
        private readonly SnapRangeProfiler? _rangeProfiler = SnapRangeProfiler.Create(logManager);
        private readonly int _storageRangeParallelism = Math.Clamp(syncConfig?.SnapSyncStorageRangeParallelism ?? 1, 1, MaxStorageRangeParallelism);

        // This is actually close to 97% effective.
        private readonly AssociativeKeyCache<ValueHash256> _codeExistKeyCache = new(1024 * 16);

        public bool CanSync() => _progressTracker.CanSync();

        public bool IsFinished(out SnapSyncBatch? nextBatch) => _progressTracker.IsFinished(out nextBatch);

        public AddRangeResult AddAccountRange(AccountRange request, AccountsAndProofs response)
        {
            AddRangeResult result;

            if (response.PathAndAccounts.Count == 0)
            {
                _logger.Trace($"SNAP - GetAccountRange - requested expired RootHash:{request.RootHash}");

                result = AddRangeResult.ExpiredRootHash;
            }
            else
            {
                result = AddAccountRange(
                    request.BlockNumber.Value,
                    request.RootHash,
                    request.StartingHash,
                    response.PathAndAccounts,
                    response.Proofs,
                    hashLimit: request.LimitHash);

                if (result == AddRangeResult.OK)
                {
                    Interlocked.Add(ref Metrics.SnapSyncedAccounts, response.PathAndAccounts.Count);
                }
            }

            _progressTracker.ReportAccountRangePartitionFinished(request.LimitHash.Value);
            response.Dispose();

            Metrics.SnapRangeResult.Increment(new SnapRangeResult(isStorage: false, result: result));
            return result;
        }

        public AddRangeResult AddAccountRange(
            long blockNumber,
            in ValueHash256 expectedRootHash,
            in ValueHash256 startingHash,
            IReadOnlyList<PathWithAccount> accounts,
            IByteArrayList proofs = null,
            in ValueHash256? hashLimit = null!)
        {
            if (accounts.Count == 0)
                throw new ArgumentException("Cannot be empty.", nameof(accounts));
            ValueHash256 effectiveHashLimit = hashLimit ?? ValueKeccak.MaxValue;

            (AddRangeResult result, bool moreChildrenToRight, List<PathWithAccount>? accountsWithStorage, List<ValueHash256>? codeHashes, Hash256 actualRootHash) =
                SnapProviderHelper.AddAccountRange(_trieFactory, blockNumber, expectedRootHash, startingHash, effectiveHashLimit, accounts, proofs, _rangeProfiler);

            if (result == AddRangeResult.OK)
            {
                if (accountsWithStorage is not null)
                {
                    foreach (PathWithAccount item in CollectionsMarshal.AsSpan(accountsWithStorage))
                    {
                        _progressTracker.EnqueueAccountStorage(item);
                    }
                }

                try
                {
                    if (codeHashes is not null)
                    {
                        using ArrayPoolListRef<ValueHash256> filteredCodeHashes = FilterMissingCodeHashes(codeHashes);

                        _progressTracker.EnqueueCodeHashes(filteredCodeHashes.AsSpan());
                    }
                }
                catch (AggregateException ae) when (ae.Flatten().InnerExceptions is { Count: > 0 } inners
                    && inners.All(e => e is ObjectDisposedException))
                {
                    ExceptionDispatchInfo.Capture(inners[0]).Throw();
                }

                ValueHash256 nextPath = accounts[^1].Path.IncrementPath();
                _progressTracker.UpdateAccountRangePartitionProgress(effectiveHashLimit, nextPath, moreChildrenToRight);
            }
            if (_logger.IsTrace)
            {
                string message = result switch
                {
                    AddRangeResult.MissingRootHashInProofs => $"SNAP - AddAccountRange failed, missing root hash {actualRootHash} in the proofs, startingHash:{startingHash}",
                    AddRangeResult.DifferentRootHash => $"SNAP - AddAccountRange failed, expected {blockNumber}:{expectedRootHash} but was {actualRootHash}, startingHash:{startingHash}",
                    AddRangeResult.InvalidOrder => $"SNAP - AddAccountRange failed, accounts are not in sorted order, startingHash:{startingHash}",
                    AddRangeResult.OutOfBounds => $"SNAP - AddAccountRange failed, accounts are out of bounds, startingHash:{startingHash}",
                    AddRangeResult.EmptyRange => $"SNAP - AddAccountRange failed, empty accounts, startingHash:{startingHash}",
                    _ => null
                };
                if (message is not null)
                {
                    _logger.Trace(message);
                }
            }

            return result;
        }

        private ArrayPoolListRef<ValueHash256> FilterMissingCodeHashes(List<ValueHash256> codeHashes)
        {
            if (_codeDb is not IKeyValueStoreWithSnapshot snapshotSource)
            {
                return codeHashes.AsParallel().Where((code) => ShouldRequestCode(code, _codeDb)).ToPooledListRef(codeHashes.Count);
            }

            ArrayPoolListRef<ValueHash256> uncachedCodeHashes = FilterUncachedCodeHashes(codeHashes);
            if (uncachedCodeHashes.Count == 0)
            {
                return uncachedCodeHashes;
            }

            IKeyValueStoreSnapshot snapshot;
            try
            {
                snapshot = snapshotSource.CreateSnapshot();
            }
            catch
            {
                uncachedCodeHashes.Dispose();
                throw;
            }

            using (snapshot)
            {
                return FilterMissingCodeHashes(uncachedCodeHashes, snapshot);
            }
        }

        private bool ShouldRequestCode(ValueHash256 code, IReadOnlyKeyValueStore codeStore)
        {
            if (_codeExistKeyCache.Get(code)) return false;

            bool exists = codeStore.KeyExists(code.Bytes);
            if (exists) _codeExistKeyCache.Set(code);
            return !exists;
        }

        private ArrayPoolListRef<ValueHash256> FilterUncachedCodeHashes(List<ValueHash256> codeHashes)
        {
            ArrayPoolListRef<ValueHash256> uncachedCodeHashes = new(codeHashes.Count);
            foreach (ValueHash256 code in CollectionsMarshal.AsSpan(codeHashes))
            {
                if (!_codeExistKeyCache.Get(code))
                {
                    uncachedCodeHashes.Add(code);
                }
            }

            return uncachedCodeHashes;
        }

        private ArrayPoolListRef<ValueHash256> FilterMissingCodeHashes(ArrayPoolListRef<ValueHash256> uncachedCodeHashes, IReadOnlyKeyValueStore codeStore)
        {
            ArrayPoolListRef<ValueHash256> missingCodeHashes = new(uncachedCodeHashes.Count);
            try
            {
                foreach (ValueHash256 code in uncachedCodeHashes.AsSpan())
                {
                    if (codeStore.KeyExists(code.Bytes))
                    {
                        _codeExistKeyCache.Set(code);
                    }
                    else
                    {
                        missingCodeHashes.Add(code);
                    }
                }

                return missingCodeHashes;
            }
            catch
            {
                missingCodeHashes.Dispose();
                throw;
            }
            finally
            {
                uncachedCodeHashes.Dispose();
            }
        }

        public AddRangeResult AddStorageRange(StorageRange request, SlotsAndProofs response)
        {
            try
            {
                AddRangeResult result = AddRangeResult.OK;

                IOwnedReadOnlyList<IOwnedReadOnlyList<PathWithStorageSlot>> responses = response.PathsAndSlots;
                int responseCount = responses.Count;
                if (responseCount == 0 && response.Proofs.Count == 0)
                {
                    _logger.Trace($"SNAP - GetStorageRange - expired BlockNumber:{request.BlockNumber}, RootHash:{request.RootHash}, (Accounts:{request.Accounts.Count}), {request.StartingHash}");

                    _progressTracker.RetryStorageRange(request.Copy());
                    Metrics.SnapRangeResult.Increment(new SnapRangeResult(isStorage: true, result: AddRangeResult.ExpiredRootHash));

                    return AddRangeResult.ExpiredRootHash;
                }
                else
                {
                    int slotCount = 0;

                    int requestLength = request.Accounts.Count;

                    using ISnapStorageBatch? storageBatch = ShouldUseStorageBatch(responses, responseCount) ? _trieFactory.StartStorageBatch() : null;
                    if (storageBatch is not null)
                    {
                        StorageRangeAccountResult[] results = AddStorageRangeInBatch(request, responses, response.Proofs, responseCount, storageBatch);
                        for (int i = 0; i < results.Length; i++)
                        {
                            StorageRangeAccountResult accountResult = results[i];
                            ApplyStorageRangeResult(request, i, accountResult);
                            Metrics.SnapRangeResult.Increment(new SnapRangeResult(isStorage: true, result: accountResult.Result));
                            slotCount += accountResult.SlotCount;
                        }

                        result = results[^1].Result;
                    }
                    else if (responseCount > 1 && _storageRangeParallelism > 1)
                    {
                        StorageRangeAccountResult[] results = AddStorageRangeInParallel(request, responses, response.Proofs, responseCount);
                        for (int i = 0; i < results.Length; i++)
                        {
                            StorageRangeAccountResult accountResult = results[i];
                            ApplyStorageRangeResult(request, i, accountResult);
                            Metrics.SnapRangeResult.Increment(new SnapRangeResult(isStorage: true, result: accountResult.Result));
                            slotCount += accountResult.SlotCount;
                        }

                        result = results[^1].Result;
                    }
                    else
                    {
                        for (int i = 0; i < responseCount; i++)
                        {
                            // only the last can have proofs
                            IByteArrayList? proofs = null;
                            if (i == responseCount - 1)
                            {
                                proofs = response.Proofs;
                            }

                            result = AddStorageRangeForAccount(request, i, responses[i], proofs);
                            Metrics.SnapRangeResult.Increment(new SnapRangeResult(isStorage: true, result: result));

                            slotCount += responses[i].Count;
                        }
                    }

                    if (requestLength > responseCount)
                    {
                        _progressTracker.ReportFullStorageRequestFinished(requestLength, request.Accounts.AsSpan()[responseCount..]);
                    }
                    else
                    {
                        _progressTracker.ReportFullStorageRequestFinished(requestLength);
                    }

                    if (result == AddRangeResult.OK && slotCount > 0)
                    {
                        Interlocked.Add(ref Metrics.SnapSyncedStorageSlots, slotCount);
                    }
                }

                return result;
            }
            finally
            {
                response.Dispose();
            }
        }

        public AddRangeResult AddStorageRangeForAccount(StorageRange request, int accountIndex, IReadOnlyList<PathWithStorageSlot> slots, IByteArrayList? proofs = null)
        {
            StorageRangeAccountResult accountResult = ProcessStorageRangeForAccount(request, accountIndex, slots, proofs);
            ApplyStorageRangeResult(request, accountIndex, accountResult);
            return accountResult.Result;
        }

        private StorageRangeAccountResult[] AddStorageRangeInParallel(
            StorageRange request,
            IOwnedReadOnlyList<IOwnedReadOnlyList<PathWithStorageSlot>> responses,
            IByteArrayList proofs,
            int responseCount)
        {
            StorageRangeAccountResult[] results = new StorageRangeAccountResult[responseCount];
            try
            {
                Parallel.For(
                    0,
                    responseCount,
                    new ParallelOptions { MaxDegreeOfParallelism = Math.Min(responseCount, _storageRangeParallelism) },
                    i =>
                    {
                        results[i] = ProcessStorageRangeForAccount(request, i, responses[i], i == responseCount - 1 ? proofs : null);
                    });
            }
            catch (AggregateException ae) when (ae.Flatten().InnerExceptions is { Count: > 0 } inners
                && inners.All(e => e is ObjectDisposedException))
            {
                ExceptionDispatchInfo.Capture(inners[0]).Throw();
            }

            return results;
        }

        private static bool ShouldUseStorageBatch(IOwnedReadOnlyList<IOwnedReadOnlyList<PathWithStorageSlot>> responses, int responseCount) =>
            responseCount > 1 || (responseCount == 1 && responses[0].Count >= MinSingleStorageResponseBatchSlots);

        private StorageRangeAccountResult[] AddStorageRangeInBatch(
            StorageRange request,
            IOwnedReadOnlyList<IOwnedReadOnlyList<PathWithStorageSlot>> responses,
            IByteArrayList proofs,
            int responseCount,
            ISnapStorageBatch storageBatch)
        {
            StorageRangeAccountResult[] results = new StorageRangeAccountResult[responseCount];
            if (storageBatch is IParallelSnapStorageBatch && _storageRangeParallelism > 1)
            {
                try
                {
                    Parallel.For(
                        0,
                        responseCount,
                        new ParallelOptions { MaxDegreeOfParallelism = Math.Min(responseCount, _storageRangeParallelism) },
                        i =>
                        {
                            results[i] = ProcessStorageRangeForAccount(request, i, responses[i], i == responseCount - 1 ? proofs : null, storageBatch);
                        });
                }
                catch (AggregateException ae) when (ae.Flatten().InnerExceptions is { Count: > 0 } inners
                    && inners.All(e => e is ObjectDisposedException))
                {
                    ExceptionDispatchInfo.Capture(inners[0]).Throw();
                }
            }
            else
            {
                for (int i = 0; i < responseCount; i++)
                {
                    results[i] = ProcessStorageRangeForAccount(request, i, responses[i], i == responseCount - 1 ? proofs : null, storageBatch);
                }
            }

            SnapRangeProfiler? rangeProfiler = _rangeProfiler;
            if (rangeProfiler is null)
            {
                storageBatch.Commit();
                return results;
            }

            int entryCount = 0;
            for (int i = 0; i < results.Length; i++)
            {
                entryCount += results[i].SlotCount;
            }

            long commitStart = Stopwatch.GetTimestamp();
            bool threw = false;
            try
            {
                storageBatch.Commit();
            }
            catch
            {
                threw = true;
                throw;
            }
            finally
            {
                rangeProfiler.ReportStorageBatchCommit(responseCount, entryCount, threw, Stopwatch.GetElapsedTime(commitStart).Ticks);
            }

            return results;
        }

        private StorageRangeAccountResult ProcessStorageRangeForAccount(StorageRange request, int accountIndex, IReadOnlyList<PathWithStorageSlot> slots, IByteArrayList? proofs, ISnapStorageBatch? storageBatch = null)
        {
            ReadOnlySpan<PathWithAccount> accounts = request.Accounts.AsSpan();
            PathWithAccount pathWithAccount = accounts[accountIndex];

            try
            {
                (AddRangeResult result, bool moreChildrenToRight, Hash256 actualRootHash, bool isRootPersisted) = SnapProviderHelper.AddStorageRange(_trieFactory, pathWithAccount, slots, request.StartingHash, request.LimitHash, proofs, _rangeProfiler, storageBatch);
                return new StorageRangeAccountResult(result, moreChildrenToRight, actualRootHash, isRootPersisted, slots.Count, slots.Count == 0 ? ValueKeccak.Zero : slots[^1].Path);
            }
            catch (Exception e)
            {
                if (_logger.IsWarn) _logger.Warn($"Error in storage {e}");
                throw;
            }
        }

        private void ApplyStorageRangeResult(StorageRange request, int accountIndex, in StorageRangeAccountResult accountResult)
        {
            ReadOnlySpan<PathWithAccount> accounts = request.Accounts.AsSpan();
            PathWithAccount pathWithAccount = accounts[accountIndex];

            if (accountResult.Result == AddRangeResult.OK)
            {
                if (accountResult.MoreChildrenToRight)
                {
                    _progressTracker.EnqueueNextSlot(request, accountIndex, accountResult.LastProcessedPath, accountResult.SlotCount);
                }
                else if (accountIndex == 0 && request.Accounts.Count == 1)
                {
                    _progressTracker.OnCompletedLargeStorage(pathWithAccount);
                }

                if (!accountResult.MoreChildrenToRight && (request.LimitHash == null || request.LimitHash == ValueKeccak.MaxValue) && !accountResult.IsRootPersisted)
                {
                    // Sometimes the stitching does not work. Likely because part of the storage is using different
                    // pivot, sometimes the proof is in a form that we cannot cleanly verify if it should persist or not,
                    // but also because of stitching bug. So we just force trigger healing and continue on with our lives.
                    _progressTracker.TrackAccountToHeal(accounts[accountIndex].Path);
                }

                return;
            }

            if (_logger.IsTrace)
            {
                string message = accountResult.Result switch
                {
                    AddRangeResult.MissingRootHashInProofs => $"SNAP - AddStorageRange failed, missing root hash {accountResult.ActualRootHash} in the proofs, startingHash:{request.StartingHash}",
                    AddRangeResult.DifferentRootHash => $"SNAP - AddStorageRange failed, expected storage root hash:{pathWithAccount.Account.StorageRoot} but was {accountResult.ActualRootHash}, startingHash:{request.StartingHash}",
                    AddRangeResult.InvalidOrder => $"SNAP - AddStorageRange failed, slots are not in sorted order, startingHash:{request.StartingHash}",
                    AddRangeResult.OutOfBounds => $"SNAP - AddStorageRange failed, slots are out of bounds, startingHash:{request.StartingHash}",
                    AddRangeResult.EmptyRange => $"SNAP - AddStorageRange failed, slots list is empty, startingHash:{request.StartingHash}",
                    _ => null
                };
                if (message is not null)
                {
                    _logger.Trace(message);
                }
            }

            _progressTracker.EnqueueAccountRefresh(pathWithAccount, request.StartingHash, request.LimitHash);
        }

        private readonly record struct StorageRangeAccountResult(
            AddRangeResult Result,
            bool MoreChildrenToRight,
            Hash256 ActualRootHash,
            bool IsRootPersisted,
            int SlotCount,
            ValueHash256 LastProcessedPath);

        public void RefreshAccounts(AccountsToRefreshRequest request, IByteArrayList response)
        {
            int respLength = response.Count;
            ReadOnlySpan<AccountWithStorageStartingHash> paths = request.Paths.AsSpan();
            for (int reqIndex = 0; reqIndex < paths.Length; reqIndex++)
            {
                AccountWithStorageStartingHash requestedPath = paths[reqIndex];

                if (reqIndex < respLength)
                {
                    ReadOnlySpan<byte> nodeData = response[reqIndex];

                    if (nodeData.Length == 0)
                    {
                        RetryAccountRefresh(requestedPath);
                        _logger.Trace($"SNAP - Empty Account Refresh: {requestedPath.PathAndAccount.Path}");
                        continue;
                    }

                    requestedPath.PathAndAccount.Account = requestedPath.PathAndAccount.Account.WithChangedStorageRoot(Keccak.Compute(nodeData));

                    if (requestedPath.StorageStartingHash > ValueKeccak.Zero)
                    {
                        StorageRange range = new()
                        {
                            Accounts = new ArrayPoolList<PathWithAccount>(1) { requestedPath.PathAndAccount },
                            StartingHash = requestedPath.StorageStartingHash,
                            LimitHash = requestedPath.StorageHashLimit
                        };

                        _progressTracker.EnqueueNextSlot(range);
                    }
                    else
                    {
                        _progressTracker.EnqueueAccountStorage(requestedPath.PathAndAccount);
                    }
                }
                else
                {
                    RetryAccountRefresh(requestedPath);
                }
            }

            _progressTracker.ReportAccountRefreshFinished();
        }

        private void RetryAccountRefresh(AccountWithStorageStartingHash requestedPath) => _progressTracker.EnqueueAccountRefresh(requestedPath.PathAndAccount, requestedPath.StorageStartingHash, requestedPath.StorageHashLimit);

        public void AddCodes(IReadOnlyList<ValueHash256> requestedHashes, IByteArrayList codes)
        {
            int codesCount = codes.Count;
            HashSet<ValueHash256> set = new(requestedHashes.Count);
            for (int i = 0; i < requestedHashes.Count; i++)
            {
                set.Add(requestedHashes[i]);
            }

            try
            {
                using IWriteBatch writeBatch = _codeDb.StartWriteBatch();
                for (int i = 0; i < codesCount; i++)
                {
                    ReadOnlySpan<byte> codeSpan = codes[i];
                    ValueHash256 codeHash = ValueKeccak.Compute(codeSpan);

                    if (set.Remove(codeHash))
                    {
                        Interlocked.Add(ref Metrics.SnapStateSynced, codeSpan.Length);
                        writeBatch.PutSpan(codeHash.Bytes, codeSpan);
                    }
                }
            }
            finally
            {
                codes.Dispose();
            }

            Interlocked.Add(ref Metrics.SnapSyncedCodes, codesCount);
            if (set.Count == 0)
            {
                _progressTracker.ReportCodeRequestFinished(ReadOnlySpan<ValueHash256>.Empty);
            }
            else
            {
                _progressTracker.ReportCodeRequestFinished(set.ToArray());
            }
        }

        public void RetryRequest(SnapSyncBatch batch)
        {
            if (batch.AccountRangeRequest is not null)
            {
                _progressTracker.ReportAccountRangePartitionFinished(batch.AccountRangeRequest.LimitHash.Value);
            }
            else if (batch.StorageRangeRequest is not null)
            {
                _progressTracker.RetryStorageRange(batch.StorageRangeRequest.Copy());
            }
            else if (batch.CodesRequest is not null)
            {
                _progressTracker.ReportCodeRequestFinished(batch.CodesRequest.AsSpan());
            }
            else if (batch.AccountsToRefreshRequest is not null)
            {
                _progressTracker.ReportAccountRefreshFinished(batch.AccountsToRefreshRequest);
            }
        }

        public bool IsSnapGetRangesFinished() => _progressTracker.IsSnapGetRangesFinished();

        public void UpdatePivot() => _progressTracker.UpdatePivot();

        public void Dispose() => _codeExistKeyCache.Clear();

    }
}
