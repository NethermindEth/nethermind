// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Autofac.Features.AttributeFilters;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Snap;
using Nethermind.Trie.Pruning;

namespace Nethermind.Synchronization.SnapSync
{
    public class SnapProvider : ISnapProvider
    {
        private readonly IDb _codeDb;
        private readonly ILogger _logger;

        private readonly ProgressTracker _progressTracker;
        private readonly ISnapTrieFactory _trieFactory;

        // This is actually close to 97% effective.
        private readonly ClockKeyCache<ValueHash256> _codeExistKeyCache = new(1024 * 16);

        public SnapProvider(ProgressTracker progressTracker, [KeyFilter(DbNames.Code)] IDb codeDb, ISnapTrieFactory trieFactory, ILogManager logManager)
        {
            _codeDb = codeDb;
            _progressTracker = progressTracker;
            _trieFactory = trieFactory;
            _logger = logManager.GetClassLogger<SnapProvider>();
        }

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
            IReadOnlyList<byte[]> proofs = null,
            in ValueHash256? hashLimit = null!)
        {
            if (accounts.Count == 0)
                throw new ArgumentException("Cannot be empty.", nameof(accounts));
            ISnapStateTree tree = _trieFactory.CreateStateTree();

            ValueHash256 effectiveHashLimit = hashLimit ?? ValueKeccak.MaxValue;

            (AddRangeResult result, bool moreChildrenToRight, List<PathWithAccount> accountsWithStorage, List<ValueHash256> codeHashes, Hash256 actualRootHash) =
                SnapProviderHelper.AddAccountRange(tree, blockNumber, expectedRootHash, startingHash, effectiveHashLimit, accounts, proofs);

            if (result == AddRangeResult.OK)
            {
                foreach (PathWithAccount item in CollectionsMarshal.AsSpan(accountsWithStorage))
                {
                    _progressTracker.EnqueueAccountStorage(item);
                }

                using ArrayPoolList<ValueHash256> filteredCodeHashes = codeHashes.AsParallel().Where((code) =>
                {
                    if (_codeExistKeyCache.Get(code)) return false;

                    bool exist = _codeDb.KeyExists(code.Bytes);
                    if (exist) _codeExistKeyCache.Set(code);
                    return !exist;
                }).ToPooledList(codeHashes.Count);

                _progressTracker.EnqueueCodeHashes(filteredCodeHashes.AsSpan());

                UInt256 nextPath = accounts[^1].Path.ToUInt256();
                nextPath += UInt256.One;
                _progressTracker.UpdateAccountRangePartitionProgress(effectiveHashLimit, nextPath.ToValueHash(), moreChildrenToRight);
            }
            else if (result == AddRangeResult.MissingRootHashInProofs)
            {
                _logger.Trace($"SNAP - AddAccountRange failed, missing root hash {actualRootHash} in the proofs, startingHash:{startingHash}");
            }
            else if (result == AddRangeResult.DifferentRootHash)
            {
                _logger.Trace($"SNAP - AddAccountRange failed, expected {blockNumber}:{expectedRootHash} but was {actualRootHash}, startingHash:{startingHash}");
            }
            else if (result == AddRangeResult.InvalidOrder)
            {
                if (_logger.IsTrace) _logger.Trace($"SNAP - AddAccountRange failed, accounts are not in sorted order, startingHash:{startingHash}");
            }
            else if (result == AddRangeResult.OutOfBounds)
            {
                if (_logger.IsTrace) _logger.Trace($"SNAP - AddAccountRange failed, accounts are out of bounds, startingHash:{startingHash}");
            }

            return result;
        }

        public AddRangeResult AddStorageRange(StorageRange request, SlotsAndProofs response)
        {
            AddRangeResult result = AddRangeResult.OK;

            IReadOnlyList<IOwnedReadOnlyList<PathWithStorageSlot>> responses = response.PathsAndSlots;
            if (responses.Count == 0 && response.Proofs.Count == 0)
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

                for (int i = 0; i < responses.Count; i++)
                {
                    // only the last can have proofs
                    IReadOnlyList<byte[]> proofs = null;
                    if (i == responses.Count - 1)
                    {
                        proofs = response.Proofs;
                    }

                    result = AddStorageRangeForAccount(request, i, responses[i], proofs);
                    Metrics.SnapRangeResult.Increment(new SnapRangeResult(isStorage: true, result: result));

                    slotCount += responses[i].Count;
                }

                if (requestLength > responses.Count)
                {
                    _progressTracker.ReportFullStorageRequestFinished(requestLength, request.Accounts.Skip(responses.Count));
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

            response.Dispose();
            return result;
        }

        public AddRangeResult AddStorageRangeForAccount(StorageRange request, int accountIndex, IReadOnlyList<PathWithStorageSlot> slots, IReadOnlyList<byte[]>? proofs = null)
        {
            PathWithAccount pathWithAccount = request.Accounts[accountIndex];
            ISnapStorageTree tree = _trieFactory.CreateStorageTree(pathWithAccount.Path);

            (AddRangeResult result, bool moreChildrenToRight, Hash256 actualRootHash) = SnapProviderHelper.AddStorageRange(tree, pathWithAccount, slots, request.StartingHash, request.LimitHash, proofs);

            if (result == AddRangeResult.OK)
            {
                if (moreChildrenToRight)
                {
                    _progressTracker.EnqueueNextSlot(request, accountIndex, slots[^1].Path);
                }
                else if (accountIndex == 0 && request.Accounts.Count == 1)
                {
                    _progressTracker.OnCompletedLargeStorage(pathWithAccount);
                }

                return result;
            }

            if (result == AddRangeResult.MissingRootHashInProofs)
            {
                _logger.Trace(
                    $"SNAP - AddStorageRange failed, missing root hash {actualRootHash} in the proofs, startingHash:{request.StartingHash}");
            }
            else if (result == AddRangeResult.DifferentRootHash)
            {
                _logger.Trace(
                    $"SNAP - AddStorageRange failed, expected storage root hash:{pathWithAccount.Account.StorageRoot} but was {actualRootHash}, startingHash:{request.StartingHash}");
            }
            else if (result == AddRangeResult.InvalidOrder)
            {
                if (_logger.IsTrace)
                    _logger.Trace(
                        $"SNAP - AddStorageRange failed, slots are not in sorted order, startingHash:{request.StartingHash}");
            }
            else if (result == AddRangeResult.OutOfBounds)
            {
                if (_logger.IsTrace)
                    _logger.Trace(
                        $"SNAP - AddStorageRange failed, slots are out of bounds, startingHash:{request.StartingHash}");
            }
            else if (result == AddRangeResult.EmptySlots)
            {
                if (_logger.IsTrace)
                    _logger.Trace(
                        $"SNAP - AddStorageRange failed, slots list is empty, startingHash:{request.StartingHash}");
            }

            _progressTracker.EnqueueAccountRefresh(pathWithAccount, request.StartingHash, request.LimitHash);
            return result;
        }

        public void RefreshAccounts(AccountsToRefreshRequest request, IOwnedReadOnlyList<byte[]> response)
        {
            int respLength = response.Count;
            for (int reqIndex = 0; reqIndex < request.Paths.Count; reqIndex++)
            {
                var requestedPath = request.Paths[reqIndex];

                if (reqIndex < respLength)
                {
                    byte[] nodeData = response[reqIndex];

                    if (nodeData.Length == 0)
                    {
                        RetryAccountRefresh(requestedPath);
                        _logger.Trace($"SNAP - Empty Account Refresh: {requestedPath.PathAndAccount.Path}");
                        continue;
                    }

                    Hash256? storageRoot = _trieFactory.ResolveStorageRoot(nodeData);
                    if (storageRoot is null)
                    {
                        RetryAccountRefresh(requestedPath);
                        _logger.Warn($"SNAP - Failed to resolve node: {requestedPath.PathAndAccount.Path}:{Bytes.ToHexString(nodeData)}");
                        continue;
                    }

                    requestedPath.PathAndAccount.Account = requestedPath.PathAndAccount.Account.WithChangedStorageRoot(storageRoot);

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

        private void RetryAccountRefresh(AccountWithStorageStartingHash requestedPath)
        {
            _progressTracker.EnqueueAccountRefresh(requestedPath.PathAndAccount, requestedPath.StorageStartingHash, requestedPath.StorageHashLimit);
        }

        public void AddCodes(IReadOnlyList<ValueHash256> requestedHashes, IOwnedReadOnlyList<byte[]> codes)
        {
            HashSet<ValueHash256> set = requestedHashes.ToHashSet();

            using (IWriteBatch writeBatch = _codeDb.StartWriteBatch())
            {
                for (int i = 0; i < codes.Count; i++)
                {
                    byte[] code = codes[i];
                    ValueHash256 codeHash = ValueKeccak.Compute(code);

                    if (set.Remove(codeHash))
                    {
                        Interlocked.Add(ref Metrics.SnapStateSynced, code.Length);
                        writeBatch[codeHash.Bytes] = code;
                    }
                }
            }

            Interlocked.Add(ref Metrics.SnapSyncedCodes, codes.Count);
            codes.Dispose();
            _progressTracker.ReportCodeRequestFinished(set.ToArray());
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

        public void UpdatePivot()
        {
            _progressTracker.UpdatePivot();
        }

        public void Dispose()
        {
            _codeExistKeyCache.Clear();
        }
    }
}
