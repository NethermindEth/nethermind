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
using Nethermind.State;
using Nethermind.State.Snap;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.Synchronization.SnapSync
{
    public class SnapProvider : ISnapProvider
    {
        private readonly IDb _codeDb;
        private readonly ILogManager _logManager;
        private readonly ILogger _logger;

        private readonly ProgressTracker _progressTracker;
        private readonly INodeStorage _nodeStorage;

        // This is actually close to 97% effective.
        private readonly ClockKeyCache<ValueHash256> _codeExistKeyCache = new(1024 * 16);
        private readonly RawScopedTrieStore _stateTrieStore;

        public SnapProvider(ProgressTracker progressTracker, [KeyFilter(DbNames.Code)] IDb codeDb, INodeStorage nodeStorage, ILogManager logManager)
        {
            _codeDb = codeDb;
            _progressTracker = progressTracker;
            _nodeStorage = nodeStorage;
            _stateTrieStore = new RawScopedTrieStore(_nodeStorage, null);

            _logManager = logManager;
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
            StateTree tree = new(_stateTrieStore, _logManager);

            ValueHash256 effectiveHashLimit = hashLimit ?? ValueKeccak.MaxValue;

            (AddRangeResult result, bool moreChildrenToRight, List<PathWithAccount> accountsWithStorage, List<ValueHash256> codeHashes) =
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
                _logger.Trace($"SNAP - AddAccountRange failed, missing root hash {tree.RootHash} in the proofs, startingHash:{startingHash}");
            }
            else if (result == AddRangeResult.DifferentRootHash)
            {
                _logger.Trace($"SNAP - AddAccountRange failed, expected {blockNumber}:{expectedRootHash} but was {tree.RootHash}, startingHash:{startingHash}");
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

                _progressTracker.ReportStorageRangeRequestFinished(request.Copy());

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

                    slotCount += responses[i].Count;
                }

                if (requestLength > responses.Count)
                {
                    _progressTracker.ReportFullStorageRequestFinished(request.Accounts.Skip(responses.Count));
                }
                else
                {
                    _progressTracker.ReportFullStorageRequestFinished();
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
            StorageTree tree = new(new RawScopedTrieStore(_nodeStorage, pathWithAccount.Path.ToCommitment()), _logManager);

            (AddRangeResult result, bool moreChildrenToRight) = SnapProviderHelper.AddStorageRange(tree, pathWithAccount, slots, request.StartingHash, request.LimitHash, proofs);

            if (result == AddRangeResult.OK)
            {
                if (moreChildrenToRight)
                {
                    _progressTracker.EnqueueStorageRange(request, accountIndex, slots[^1].Path);
                }
            }
            else if (result == AddRangeResult.MissingRootHashInProofs)
            {
                _logger.Trace($"SNAP - AddStorageRange failed, missing root hash {pathWithAccount.Account.StorageRoot} in the proofs, startingHash:{request.StartingHash}");

                _progressTracker.EnqueueAccountRefresh(pathWithAccount, request.StartingHash, request.LimitHash);
            }
            else if (result == AddRangeResult.DifferentRootHash)
            {
                _logger.Trace($"SNAP - AddStorageRange failed, expected storage root hash:{pathWithAccount.Account.StorageRoot} but was {tree.RootHash}, startingHash:{request.StartingHash}");

                _progressTracker.EnqueueAccountRefresh(pathWithAccount, request.StartingHash, request.LimitHash);
            }

            return result;
        }

        public void RefreshAccounts(AccountsToRefreshRequest request, IOwnedReadOnlyList<byte[]> response)
        {
            int respLength = response.Count;
            IScopedTrieStore stateStore = _stateTrieStore;
            for (int reqi = 0; reqi < request.Paths.Count; reqi++)
            {
                var requestedPath = request.Paths[reqi];

                if (reqi < respLength)
                {
                    byte[] nodeData = response[reqi];

                    if (nodeData.Length == 0)
                    {
                        RetryAccountRefresh(requestedPath);
                        _logger.Trace($"SNAP - Empty Account Refresh: {requestedPath.PathAndAccount.Path}");
                        continue;
                    }

                    try
                    {
                        TreePath emptyTreePath = TreePath.Empty;
                        TrieNode node = new(NodeType.Unknown, nodeData, isDirty: true);
                        node.ResolveNode(stateStore, emptyTreePath);
                        node.ResolveKey(stateStore, ref emptyTreePath, true);

                        requestedPath.PathAndAccount.Account = requestedPath.PathAndAccount.Account.WithChangedStorageRoot(node.Keccak);

                        if (requestedPath.StorageStartingHash > ValueKeccak.Zero)
                        {
                            StorageRange range = new()
                            {
                                Accounts = new ArrayPoolList<PathWithAccount>(1) { requestedPath.PathAndAccount },
                                StartingHash = requestedPath.StorageStartingHash,
                                LimitHash = requestedPath.StorageHashLimit
                            };

                            _progressTracker.EnqueueStorageRange(range);
                        }
                        else
                        {
                            _progressTracker.EnqueueAccountStorage(requestedPath.PathAndAccount);
                        }
                    }
                    catch (Exception exc)
                    {
                        RetryAccountRefresh(requestedPath);
                        _logger.Warn($"SNAP - {exc.Message}:{requestedPath.PathAndAccount.Path}:{Bytes.ToHexString(nodeData)}");
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
                _progressTracker.ReportStorageRangeRequestFinished(batch.StorageRangeRequest.Copy());
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
