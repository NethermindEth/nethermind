// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.State.Snap;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.Synchronization.SnapSync
{
    public class SnapProvider : ISnapProvider
    {
        private readonly ITrieStore _store;
        private readonly IDbProvider _dbProvider;
        private readonly ILogManager _logManager;
        private readonly ILogger _logger;

        private readonly ProgressTracker _progressTracker;

        public SnapProvider(ProgressTracker progressTracker, IDbProvider dbProvider, ILogManager logManager)
        {
            _dbProvider = dbProvider ?? throw new ArgumentNullException(nameof(dbProvider));
            _progressTracker = progressTracker ?? throw new ArgumentNullException(nameof(progressTracker));

            _store = new TrieStore(
                _dbProvider.StateDb,
                Trie.Pruning.No.Pruning,
                Persist.EveryBlock,
                logManager);

            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _logger = logManager.GetClassLogger();
        }

        public bool CanSync() => _progressTracker.CanSync();

        public (SnapSyncBatch request, bool finished) GetNextRequest() => _progressTracker.GetNextRequest();

        public AddRangeResult AddAccountRange(AccountRange request, AccountsAndProofs response)
        {
            AddRangeResult result;

            if (response.PathAndAccounts.Length == 0 && response.Proofs.Length == 0)
            {
                _logger.Trace($"SNAP - GetAccountRange - requested expired RootHash:{request.RootHash}");

                result = AddRangeResult.ExpiredRootHash;
            }
            else
            {
                result = AddAccountRange(request.BlockNumber.Value, request.RootHash, request.StartingHash, response.PathAndAccounts, response.Proofs, hashLimit: request.LimitHash);

                if (result == AddRangeResult.OK)
                {
                    Interlocked.Add(ref Metrics.SnapSyncedAccounts, response.PathAndAccounts.Length);
                }
            }

            _progressTracker.ReportAccountRangePartitionFinished(request.LimitHash);

            return result;
        }

        public AddRangeResult AddAccountRange(long blockNumber, Keccak expectedRootHash, Keccak startingHash, PathWithAccount[] accounts, byte[][] proofs = null, Keccak hashLimit = null!)
        {
            StateTree tree = new(_store, _logManager);

            if (hashLimit == null) hashLimit = Keccak.MaxValue;

            (AddRangeResult result, bool moreChildrenToRight, IList<PathWithAccount> accountsWithStorage, IList<Keccak> codeHashes) =
                SnapProviderHelper.AddAccountRange(tree, blockNumber, expectedRootHash, startingHash, hashLimit, accounts, proofs);

            if (result == AddRangeResult.OK)
            {
                foreach (var item in accountsWithStorage)
                {
                    _progressTracker.EnqueueAccountStorage(item);
                }

                _progressTracker.EnqueueCodeHashes(codeHashes);
                _progressTracker.UpdateAccountRangePartitionProgress(hashLimit, accounts[^1].Path, moreChildrenToRight);
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

            if (response.PathsAndSlots.Length == 0 && response.Proofs.Length == 0)
            {
                _logger.Trace($"SNAP - GetStorageRange - expired BlockNumber:{request.BlockNumber}, RootHash:{request.RootHash}, (Accounts:{request.Accounts.Count()}), {request.StartingHash}");

                _progressTracker.ReportStorageRangeRequestFinished(request);

                return AddRangeResult.ExpiredRootHash;
            }
            else
            {
                int slotCount = 0;

                int requestLength = request.Accounts.Length;
                int responseLength = response.PathsAndSlots.Length;

                for (int i = 0; i < responseLength; i++)
                {
                    // only the last can have proofs
                    byte[][] proofs = null;
                    if (i == responseLength - 1)
                    {
                        proofs = response.Proofs;
                    }

                    result = AddStorageRange(request.BlockNumber.Value, request.Accounts[i], request.Accounts[i].Account.StorageRoot, request.StartingHash, response.PathsAndSlots[i], proofs);

                    slotCount += response.PathsAndSlots[i].Length;
                }

                if (requestLength > responseLength)
                {
                    _progressTracker.ReportFullStorageRequestFinished(request.Accounts[responseLength..requestLength]);
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

            return result;
        }

        public AddRangeResult AddStorageRange(long blockNumber, PathWithAccount pathWithAccount, Keccak expectedRootHash, Keccak? startingHash, PathWithStorageSlot[] slots, byte[][]? proofs = null)
        {
            StorageTree tree = new(_store, _logManager);
            (AddRangeResult result, bool moreChildrenToRight) = SnapProviderHelper.AddStorageRange(tree, blockNumber, startingHash, slots, expectedRootHash, proofs);

            if (result == AddRangeResult.OK)
            {
                if (moreChildrenToRight)
                {
                    StorageRange range = new()
                    {
                        Accounts = new[] { pathWithAccount },
                        StartingHash = slots.Last().Path
                    };

                    _progressTracker.EnqueueStorageRange(range);
                }
            }
            else if (result == AddRangeResult.MissingRootHashInProofs)
            {
                _logger.Trace($"SNAP - AddStorageRange failed, missing root hash {expectedRootHash} in the proofs, startingHash:{startingHash}");

                _progressTracker.EnqueueAccountRefresh(pathWithAccount, startingHash);
            }
            else if (result == AddRangeResult.DifferentRootHash)
            {
                _logger.Trace($"SNAP - AddStorageRange failed, expected storage root hash:{expectedRootHash} but was {tree.RootHash}, startingHash:{startingHash}");

                _progressTracker.EnqueueAccountRefresh(pathWithAccount, startingHash);
            }

            return result;
        }

        public void RefreshAccounts(AccountsToRefreshRequest request, byte[][] response)
        {
            int respLength = response.Length;

            for (int reqi = 0; reqi < request.Paths.Length; reqi++)
            {
                var requestedPath = request.Paths[reqi];

                if (reqi < respLength)
                {
                    byte[] nodeData = response[reqi];

                    if (nodeData.Length == 0)
                    {
                        RetryAccountRefresh(requestedPath);
                        _logger.Trace($"SNAP - Empty Account Refresh:{requestedPath.PathAndAccount.Path}");
                        continue;
                    }

                    try
                    {
                        var node = new TrieNode(NodeType.Unknown, nodeData, true);
                        node.ResolveNode(_store);
                        node.ResolveKey(_store, true);

                        requestedPath.PathAndAccount.Account = requestedPath.PathAndAccount.Account.WithChangedStorageRoot(node.Keccak);

                        if (requestedPath.StorageStartingHash > Keccak.Zero)
                        {
                            StorageRange range = new()
                            {
                                Accounts = new[] { requestedPath.PathAndAccount },
                                StartingHash = requestedPath.StorageStartingHash
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
            _progressTracker.EnqueueAccountRefresh(requestedPath.PathAndAccount, requestedPath.StorageStartingHash);
        }

        public void AddCodes(Keccak[] requestedHashes, byte[][] codes)
        {
            HashSet<Keccak> set = requestedHashes.ToHashSet();

            for (int i = 0; i < codes.Length; i++)
            {
                byte[] code = codes[i];
                Keccak codeHash = Keccak.Compute(code);

                if (set.Remove(codeHash))
                {
                    Interlocked.Add(ref Metrics.SnapStateSynced, code.Length);
                    _dbProvider.CodeDb.Set(codeHash, code);
                }
            }

            Interlocked.Add(ref Metrics.SnapSyncedCodes, codes.Length);

            _progressTracker.ReportCodeRequestFinished(set);
        }

        public void RetryRequest(SnapSyncBatch batch)
        {
            if (batch.AccountRangeRequest is not null)
            {
                _progressTracker.ReportAccountRangePartitionFinished(batch.AccountRangeRequest.LimitHash);
            }
            else if (batch.StorageRangeRequest is not null)
            {
                _progressTracker.ReportStorageRangeRequestFinished(batch.StorageRangeRequest);
            }
            else if (batch.CodesRequest is not null)
            {
                _progressTracker.ReportCodeRequestFinished(batch.CodesRequest);
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
    }
}
