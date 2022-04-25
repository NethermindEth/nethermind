using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
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
        // TODO: to be removed, only for dev
        int _testStorePartResponsesCount;
        int _testStoreFullResponsesCount;
        int _testCodePartResponsesCount;
        int _testCodeFullResponsesCount;

        long _testStorageRespSize;
        int _testStorageReqCount;
        long _testCodeRespSize;
        int _testCodeReqCount;

        private readonly ITrieStore _store;
        private readonly IDbProvider _dbProvider;
        private readonly ILogManager _logManager;
        private readonly ILogger _logger;

        public ProgressTracker _progressTracker;

        public SnapProvider(IBlockTree blockTree, IDbProvider dbProvider, ILogManager logManager)
        {
            _dbProvider = dbProvider ?? throw new ArgumentNullException(nameof(dbProvider));
            _progressTracker = new ProgressTracker(blockTree, dbProvider.StateDb, logManager);

            _store = new TrieStore(
                _dbProvider.StateDb,
                No.Pruning,
                Persist.EveryBlock,
                logManager);

            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _logger = logManager.GetClassLogger();
        }

        public bool CanSync() => _progressTracker.CanSync();

        public (SnapSyncBatch request, bool finished) GetNextRequest() => _progressTracker.GetNextRequest();

        public void AddAccountRange(AccountRange request, AccountsAndProofs response)
        {
            if (response.PathAndAccounts.Length == 0 && response.Proofs.Length == 0)
            {
                _logger.Warn($"GetAccountRange: Requested expired RootHash:{request.RootHash}");
            }
            else
            {
                bool success = AddAccountRange(request.BlockNumber.Value, request.RootHash, request.StartingHash, response.PathAndAccounts, response.Proofs);

                if (success && response.PathAndAccounts.Length > 0)
                {
                    Interlocked.Add(ref Metrics.SyncedAccounts, response.PathAndAccounts.Length);
                }
            }

            _progressTracker.ReportAccountRequestFinished();
        }

        public bool AddAccountRange(long blockNumber, Keccak expectedRootHash, Keccak startingHash, PathWithAccount[] accounts, byte[][] proofs = null)
        {
            StateTree tree = new(_store, _logManager);

            (AddRangeResult result, bool moreChildrenToRight, IList<PathWithAccount> accountsWithStorage, IList<Keccak> codeHashes) =
                SnapProviderHelper.AddAccountRange(tree, blockNumber, expectedRootHash, startingHash, accounts, proofs);

            if (result == AddRangeResult.OK)
            {
                foreach (var item in accountsWithStorage)
                {
                    _progressTracker.EnqueueAccountStorage(item);
                }

                _progressTracker.EnqueueCodeHashes(codeHashes);

                _progressTracker.NextAccountPath = accounts[accounts.Length - 1].Path;
                _progressTracker.MoreAccountsToRight = moreChildrenToRight;

                return true;
            }
            else if(result == AddRangeResult.MissingRootHashInProofs)
            {
                _logger.Warn($"SNAP - AddAccountRange failed, missing root hash {tree.RootHash} in the proofs, startingHash:{startingHash}");
            }
            else if(result == AddRangeResult.DifferentRootHash)
            {
                _logger.Warn($"SNAP - AddAccountRange failed, expected {blockNumber}:{expectedRootHash} but was {tree.RootHash}, startingHash:{startingHash}");
            }

            return false;
        }

        public void AddStorageRange(StorageRange request, SlotsAndProofs response)
        {
            if (response.PathsAndSlots.Length == 0 && response.Proofs.Length == 0)
            {
                _logger.Warn($"GetStorageRange - expired BlockNumber:{request.BlockNumber}, RootHash:{request.RootHash}, (Accounts:{request.Accounts.Count()}), {request.StartingHash}");

                _progressTracker.ReportStorageRangeRequestFinished(request);
            }
            else
            {
                int slotCount = 0;

                int requestLength = request.Accounts.Length;
                int responseLength = response.PathsAndSlots.Length;

                if(requestLength == responseLength)
                {
                    _testStoreFullResponsesCount++;
                }
                else
                {
                    _testStorePartResponsesCount++;
                }

                if (requestLength > 1)
                {
                    _testStorageReqCount++;
                    _testStorageRespSize += responseLength;
                }

                for (int i = 0; i < responseLength; i++)
                {
                    // only the last can have proofs
                    byte[][] proofs = null;
                    if (i == responseLength - 1)
                    {
                        proofs = response.Proofs;
                    }

                    AddStorageRange(request.BlockNumber.Value, request.Accounts[i], request.Accounts[i].Account.StorageRoot, request.StartingHash, response.PathsAndSlots[i], proofs);

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

                if (slotCount > 0)
                {
                    Interlocked.Add(ref Metrics.SyncedStorageSlots, slotCount);
                }
            }
        }

        public bool AddStorageRange(long blockNumber, PathWithAccount pathWithAccount, Keccak expectedRootHash, Keccak startingHash, PathWithStorageSlot[] slots, byte[][] proofs = null)
        {
            // TODO: use expectedRootHash (StorageRootHash from Account), it can change when PIVOT changes

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

                return true;
            }
            else if(result == AddRangeResult.MissingRootHashInProofs)
            {
                _logger.Warn($"SNAP - AddStorageRange failed, missing root hash {expectedRootHash} in the proofs, startingHash:{startingHash}");

                _progressTracker.EnqueueAccountRefresh(pathWithAccount, startingHash);
            }
            else if(result == AddRangeResult.DifferentRootHash)
            {
                _logger.Warn($"SNAP - AddStorageRange failed, expected storage root hash:{expectedRootHash} but was {tree.RootHash}, startingHash:{startingHash}");

                _progressTracker.EnqueueAccountRefresh(pathWithAccount, startingHash);
            }

            return false;
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

                    if(nodeData.Length == 0)
                    {
                        RetryAccountRefresh(requestedPath);
                        _logger.Warn($"Empty Account Refresh:{requestedPath.PathAndAccount.Path}");
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
                        _logger.Warn($"{exc.Message}:{requestedPath.PathAndAccount.Path}:{Bytes.ToHexString(nodeData)}");
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
            if (requestedHashes.Length == codes.Length)
            {
                _testCodeFullResponsesCount++;
            }
            else
            {
                _testCodePartResponsesCount++;
            }

            if (requestedHashes.Length > 1)
            {
                _testCodeReqCount++;
                _testCodeRespSize += codes.Length;

                if (_testCodeReqCount % 10 == 0)
                {
                    _logger.Warn($"SNAP - Storage AVG:{_testStorageRespSize / _testStorageReqCount}, Full:{_testStoreFullResponsesCount}, Part:{_testStorePartResponsesCount}, Codes AVG:{_testCodeRespSize / _testCodeReqCount}, Full:{_testCodeFullResponsesCount}, Part:{_testCodePartResponsesCount}");
                }
            }

            HashSet<Keccak> set = requestedHashes.ToHashSet();

            for (int i = 0; i < codes.Length; i++)
            {
                byte[] code = codes[i];
                Keccak codeHash = Keccak.Compute(code);

                if (set.Remove(codeHash))
                {
                    _dbProvider.CodeDb.Set(codeHash, code);
                }
            }

            _progressTracker.ReportCodeRequestFinished(set);
        }

        public void RetryRequest(SnapSyncBatch batch)
        {
            if (batch.AccountRangeRequest != null)
            {
                _progressTracker.ReportAccountRequestFinished();
            }
            else if (batch.StorageRangeRequest != null)
            {
                _progressTracker.ReportStorageRangeRequestFinished(batch.StorageRangeRequest);
            }
            else if (batch.CodesRequest != null)
            {
                _progressTracker.ReportCodeRequestFinished(batch.CodesRequest);
            }
            else if (batch.AccountsToRefreshRequest != null)
            {
                _progressTracker.ReportAccountRefreshFinished(batch.AccountsToRefreshRequest);
            }
        }

        public bool IsSnapGetRangesFinished() => _progressTracker.IsSnapGetRangesFinished();
    }
}
