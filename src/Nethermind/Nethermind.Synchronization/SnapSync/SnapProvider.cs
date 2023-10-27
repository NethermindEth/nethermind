// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.ObjectPool;
using Nethermind.Core;
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
        private readonly ObjectPool<ITrieStore> _trieStorePool;
        private readonly IDbProvider _dbProvider;
        private readonly ILogManager _logManager;
        private readonly ILogger _logger;

        private readonly ProgressTracker _progressTracker;

        public SnapProvider(ProgressTracker progressTracker, IDbProvider dbProvider, ILogManager logManager)
        {
            _dbProvider = dbProvider ?? throw new ArgumentNullException(nameof(dbProvider));
            _progressTracker = progressTracker ?? throw new ArgumentNullException(nameof(progressTracker));
            _trieStorePool = new DefaultObjectPool<ITrieStore>(new TrieStorePoolPolicy(_dbProvider.StateDb, logManager));

            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _logger = logManager.GetClassLogger<SnapProvider>();
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

            _progressTracker.ReportAccountRangePartitionFinished(request.LimitHash.Value);

            return result;
        }

        public AddRangeResult AddAccountRange(long blockNumber, in ValueHash256 expectedRootHash, in ValueHash256 startingHash, PathWithAccount[] accounts, byte[][] proofs = null, in ValueHash256? hashLimit = null!)
        {
            ITrieStore store = _trieStorePool.Get();
            try
            {
                StateTree tree = new(store, _logManager);

                ValueHash256 effectiveHashLimit = hashLimit.HasValue ? hashLimit.Value : ValueKeccak.MaxValue;

                (AddRangeResult result, bool moreChildrenToRight, List<PathWithAccount> accountsWithStorage, List<ValueHash256> codeHashes) =
                    SnapProviderHelper.AddAccountRange(tree, blockNumber, expectedRootHash, startingHash, effectiveHashLimit, accounts, proofs);

                if (result == AddRangeResult.OK)
                {
                    foreach (PathWithAccount item in CollectionsMarshal.AsSpan(accountsWithStorage))
                    {
                        _progressTracker.EnqueueAccountStorage(item);
                    }

                    _progressTracker.EnqueueCodeHashes(CollectionsMarshal.AsSpan(codeHashes));
                    _progressTracker.UpdateAccountRangePartitionProgress(effectiveHashLimit, accounts[^1].Path, moreChildrenToRight);
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
            finally
            {
                _trieStorePool.Return(store);
            }
        }

        public AddRangeResult AddStorageRange(StorageRange request, SlotsAndProofs response)
        {
            AddRangeResult result = AddRangeResult.OK;

            PathWithStorageSlot[][] responses = response.PathsAndSlots;
            if (responses.Length == 0 && response.Proofs.Length == 0)
            {
                _logger.Trace($"SNAP - GetStorageRange - expired BlockNumber:{request.BlockNumber}, RootHash:{request.RootHash}, (Accounts:{request.Accounts.Length}), {request.StartingHash}");

                _progressTracker.ReportStorageRangeRequestFinished(request);

                return AddRangeResult.ExpiredRootHash;
            }
            else
            {
                int slotCount = 0;

                int requestLength = request.Accounts.Length;

                for (int i = 0; i < responses.Length; i++)
                {
                    // only the last can have proofs
                    byte[][] proofs = null;
                    if (i == responses.Length - 1)
                    {
                        proofs = response.Proofs;
                    }

                    PathWithAccount account = request.Accounts[i];
                    result = AddStorageRange(request.BlockNumber.Value, account, account.Account.StorageRoot, request.StartingHash, responses[i], proofs);

                    slotCount += responses[i].Length;
                }

                if (requestLength > responses.Length)
                {
                    _progressTracker.ReportFullStorageRequestFinished(request.Accounts.AsSpan(responses.Length, requestLength - responses.Length));
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

        public AddRangeResult AddStorageRange(long blockNumber, PathWithAccount pathWithAccount, in ValueHash256 expectedRootHash, in ValueHash256? startingHash, PathWithStorageSlot[] slots, byte[][]? proofs = null)
        {
            ITrieStore store = _trieStorePool.Get();
            StorageTree tree = new(store, _logManager);
            try
            {
                (AddRangeResult result, bool moreChildrenToRight) = SnapProviderHelper.AddStorageRange(tree, blockNumber, startingHash, slots, expectedRootHash, proofs);

                if (result == AddRangeResult.OK)
                {
                    if (moreChildrenToRight)
                    {
                        StorageRange range = new()
                        {
                            Accounts = new[] { pathWithAccount },
                            StartingHash = slots[^1].Path
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
            finally
            {
                _trieStorePool.Return(store);
            }
        }

        public void RefreshAccounts(AccountsToRefreshRequest request, byte[][] response)
        {
            int respLength = response.Length;
            ITrieStore store = _trieStorePool.Get();
            try
            {
                for (int reqi = 0; reqi < request.Paths.Length; reqi++)
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
                            TrieNode node = new(NodeType.Unknown, nodeData, isDirty: true);
                            node.ResolveNode(store);
                            node.ResolveKey(store, true);

                            requestedPath.PathAndAccount.Account = requestedPath.PathAndAccount.Account.WithChangedStorageRoot(node.Keccak);

                            if (requestedPath.StorageStartingHash > ValueKeccak.Zero)
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
            finally
            {
                _trieStorePool.Return(store);
            }
        }

        private void RetryAccountRefresh(AccountWithStorageStartingHash requestedPath)
        {
            _progressTracker.EnqueueAccountRefresh(requestedPath.PathAndAccount, requestedPath.StorageStartingHash);
        }

        public void AddCodes(ValueHash256[] requestedHashes, byte[][] codes)
        {
            HashSet<ValueHash256> set = requestedHashes.ToHashSet();

            using (IWriteBatch writeWriteBatch = _dbProvider.CodeDb.StartWriteBatch())
            {
                for (int i = 0; i < codes.Length; i++)
                {
                    byte[] code = codes[i];
                    ValueHash256 codeHash = ValueKeccak.Compute(code);

                    if (set.Remove(codeHash))
                    {
                        Interlocked.Add(ref Metrics.SnapStateSynced, code.Length);
                        writeWriteBatch[codeHash.Bytes] = code;
                    }
                }
            }

            Interlocked.Add(ref Metrics.SnapSyncedCodes, codes.Length);

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

        private class TrieStorePoolPolicy : IPooledObjectPolicy<ITrieStore>
        {
            private readonly IKeyValueStoreWithBatching _stateDb;
            private readonly ILogManager _logManager;

            public TrieStorePoolPolicy(IKeyValueStoreWithBatching stateDb, ILogManager logManager)
            {
                _stateDb = stateDb;
                _logManager = logManager;
            }

            public ITrieStore Create()
            {
                return new TrieStore(
                    _stateDb,
                    Nethermind.Trie.Pruning.No.Pruning,
                    Persist.EveryBlock,
                    _logManager);
            }

            public bool Return(ITrieStore obj)
            {
                return true;
            }
        }
    }
}
