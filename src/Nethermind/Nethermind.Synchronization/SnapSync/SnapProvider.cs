using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.State.Snap;
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
        
        public event EventHandler<EventArgs> RemoveSnapCapability;

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
                AddAccountRange(request.BlockNumber.Value, request.RootHash, request.StartingHash, response.PathAndAccounts, response.Proofs);

                if (response.PathAndAccounts.Length > 0)
                {
                    Interlocked.Add(ref Metrics.SyncedAccounts, response.PathAndAccounts.Length);
                }
            }

            _progressTracker.ReportAccountRequestFinished();
        }

        public bool AddAccountRange(long blockNumber, Keccak expectedRootHash, Keccak startingHash, PathWithAccount[] accounts, byte[][] proofs = null)
        {
            StateTree tree = new(_store, _logManager);
            (bool moreChildrenToRight, IList<PathWithAccount> accountsWithStorage, IList<Keccak> codeHashes) =
                SnapProviderHelper.AddAccountRange(tree, blockNumber, expectedRootHash, startingHash, accounts, proofs);

            bool success = expectedRootHash == tree.RootHash;

            if (success)
            {
                foreach (var item in accountsWithStorage)
                {
                    _progressTracker.EnqueueAccountStorage(item);
                }

                _progressTracker.EnqueueCodeHashes(codeHashes);

                _progressTracker.NextAccountPath = accounts[accounts.Length - 1].AddressHash;
                _progressTracker.MoreAccountsToRight = moreChildrenToRight;
            }
            else
            {
                _logger.Warn($"SNAP - AddAccountRange failed, {blockNumber}:{expectedRootHash}, startingHash:{startingHash}");
            }

            return success;
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
            (Keccak? calculatedRootHash, bool moreChildrenToRight) = SnapProviderHelper.AddStorageRange(tree, blockNumber, startingHash, slots, proofs: proofs);

            bool success = calculatedRootHash != Keccak.EmptyTreeHash;

            if (success)
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
            else
            {
                _logger.Warn($"SNAP - AddStorageRange failed, {blockNumber}:{expectedRootHash}, startingHash:{startingHash}");

                if (startingHash > Keccak.Zero)
                {
                    StorageRange range = new()
                    {
                        Accounts = new[] { pathWithAccount },
                        StartingHash = startingHash
                    };

                    _progressTracker.EnqueueStorageRange(range);
                }
                else
                {
                    _progressTracker.EnqueueAccountStorage(pathWithAccount);
                }
            }

            return success;
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


        }

        private bool _isSnapGetRangesFinished;
        public bool IsSnapGetRangesFinished() => _isSnapGetRangesFinished || CheckIfSnapGetRangesFinished();
        private bool CheckIfSnapGetRangesFinished()
        {
            _isSnapGetRangesFinished = _progressTracker.IsSnapGetRangesFinished();

            if (_isSnapGetRangesFinished)
            {
                RemoveSnapCapability?.Invoke(this, EventArgs.Empty);
            }

            return _isSnapGetRangesFinished;
        }
    }
}
