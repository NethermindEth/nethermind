using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Snap;

namespace Nethermind.Synchronization.SnapSync
{
    public class ProgressTracker
    {
        long _testReqCount;

        private const int STORAGE_BATCH_SIZE = 1_200;
        private const int CODES_BATCH_SIZE = 1_000;
        private readonly byte[] ACC_PROGRESS_KEY = Encoding.ASCII.GetBytes("AccountProgressKey");

        private int _activeAccountRequests;
        private int _activeStorageRequests;
        private int _activeCodeRequests;
        private int _activeAccRefreshRequests;

        private readonly ILogger _logger;
        private readonly IDb _db;

        public Keccak NextAccountPath { get; set; } = Keccak.Zero;
        //public Keccak NextAccountPath { get; set; } = new("0xffe0000000000000000000000000000000000000000000000000000000000000");
        private ConcurrentQueue<StorageRange> NextSlotRange { get; set; } = new();
        private ConcurrentQueue<PathWithAccount> StoragesToRetrieve { get; set; } = new();
        private ConcurrentQueue<Keccak> CodesToRetrieve { get; set; } = new();
        private ConcurrentQueue<AccountWithStorageStartingHash> AccountsToRefresh { get; set; } = new();

        public bool MoreAccountsToRight { get; set; } = true;

        private readonly Pivot _pivot;

        public ProgressTracker(IBlockTree blockTree, IDb db, ILogManager logManager)
        {
            _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _db = db ?? throw new ArgumentNullException(nameof(db));

            _pivot = new Pivot(blockTree, logManager);

            //TODO: maybe better to move to a init methot instead of the constructor
            GetSyncProgress();
        }

        public bool CanSync()
        {
            if (_pivot.GetPivotHeader() == null || _pivot.GetPivotHeader().Number == 0)
            {
                if (_logger.IsInfo) _logger.Info($"No Best Suggested Header available. Snap Sync not started.");

                return false;
            }

            if (_logger.IsInfo) _logger.Info($"Starting the SNAP data sync from the {_pivot.GetPivotHeader().ToString(BlockHeader.Format.Short)} {_pivot.GetPivotHeader().StateRoot} root");

            return true;
        }

        public (SnapSyncBatch request, bool finished) GetNextRequest()
        {
            var pivotHeader = _pivot.GetPivotHeader();
            var rootHash = pivotHeader.StateRoot;
            var blockNumber = pivotHeader.Number;

            SnapSyncBatch request = new();

            if(AccountsToRefresh.Count > 0)
            {
                LogRequest($"AccountsToRefresh:{AccountsToRefresh.Count}");

                int queueLength = AccountsToRefresh.Count;
                AccountWithStorageStartingHash[] paths = new AccountWithStorageStartingHash[queueLength];

                for (int i = 0; i < queueLength && AccountsToRefresh.TryDequeue(out var acc); i++)
                {
                    paths[i] = acc;
                }

                Interlocked.Increment(ref _activeAccRefreshRequests);

                request.AccountsToRefreshRequest = new AccountsToRefreshRequest() { RootHash = rootHash, Paths = paths};

                return (request, false);

            }
            else if (MoreAccountsToRight && _activeAccountRequests == 0 && NextSlotRange.Count < 10 && StoragesToRetrieve.Count < 5 * STORAGE_BATCH_SIZE && CodesToRetrieve.Count < 5 * CODES_BATCH_SIZE)
            {
                // some contract hardcoded
                //var path = Keccak.Compute(new Address("0x4c9A3f79801A189D98D3a5A18dD5594220e4d907").Bytes);
                // = new(_bestHeader.StateRoot, path, path, _bestHeader.Number);

                AccountRange range = new(rootHash, NextAccountPath, Keccak.MaxValue, blockNumber);

                LogRequest("AccountRange");

                Interlocked.Increment(ref _activeAccountRequests);

                request.AccountRangeRequest = range;

                return (request, false);
            }
            else if (NextSlotRange.TryDequeue(out StorageRange slotRange))
            {
                slotRange.RootHash = rootHash;
                slotRange.BlockNumber = blockNumber;

                LogRequest($"NextSlotRange:{slotRange.Accounts.Length}");

                Interlocked.Increment(ref _activeStorageRequests);

                request.StorageRangeRequest = slotRange;

                return (request, false);
            }
            else if (StoragesToRetrieve.Count > 0)
            {
                // TODO: optimize this
                List<PathWithAccount> storagesToQuery = new(STORAGE_BATCH_SIZE);

                for (int i = 0; i < STORAGE_BATCH_SIZE && StoragesToRetrieve.TryDequeue(out PathWithAccount storage); i++)
                {
                    storagesToQuery.Add(storage);
                }

                StorageRange storageRange = new()
                {
                    RootHash = rootHash,
                    Accounts = storagesToQuery.ToArray(),
                    StartingHash = Keccak.Zero,
                    BlockNumber = blockNumber
                };

                LogRequest($"StoragesToRetrieve:{storagesToQuery.Count}");

                Interlocked.Increment(ref _activeStorageRequests);

                request.StorageRangeRequest = storageRange;

                return (request, false);
            }
            else if (CodesToRetrieve.Count > 0)
            {
                // TODO: optimize this
                List<Keccak> codesToQuery = new(CODES_BATCH_SIZE);

                for (int i = 0; i < CODES_BATCH_SIZE && CodesToRetrieve.TryDequeue(out Keccak codeHash); i++)
                {
                    codesToQuery.Add(codeHash);
                }

                LogRequest($"CodesToRetrieve:{codesToQuery.Count}");

                Interlocked.Increment(ref _activeCodeRequests);

                request.CodesRequest = codesToQuery.ToArray();

                return (request, false);
            }

            bool rangePhaseFinished = IsSnapGetRangesFinished();
            if(rangePhaseFinished)
            {
                _logger.Info($"SNAP - State Ranges (Phase 1) finished.");
                FinishRangePhase();
            }

            return (null, IsSnapGetRangesFinished());
        }

        public void EnqueueCodeHashes(ICollection<Keccak> codeHashes)
        {
            if (codeHashes is not null)
            {
                foreach (var hash in codeHashes)
                {
                    CodesToRetrieve.Enqueue(hash);
                }
            }
        }

        public void ReportCodeRequestFinished(ICollection<Keccak> codeHashes = null)
        {
            EnqueueCodeHashes(codeHashes);

            Interlocked.Decrement(ref _activeCodeRequests);
        }

        public void ReportAccountRefreshFinished(AccountsToRefreshRequest accountsToRefreshRequest = null)
        {
            if (accountsToRefreshRequest is not null)
            {
                foreach (var path in accountsToRefreshRequest.Paths)
                {
                    AccountsToRefresh.Enqueue(path);
                }
            }

            Interlocked.Decrement(ref _activeAccRefreshRequests);
        }

        public void EnqueueAccountStorage(PathWithAccount pwa)
        {
            StoragesToRetrieve.Enqueue(pwa);
        }

        public void EnqueueAccountRefresh(PathWithAccount pathWithAccount, Keccak startingHash)
        {
            AccountsToRefresh.Enqueue(new AccountWithStorageStartingHash() { PathAndAccount = pathWithAccount, StorageStartingHash = startingHash});
        }

        public void ReportFullStorageRequestFinished(PathWithAccount[] storages = null)
        {
            if (storages is not null)
            {
                foreach (var s in storages)
                {
                    EnqueueAccountStorage(s);

                }
            }

            Interlocked.Decrement(ref _activeStorageRequests);
        }

        public void EnqueueStorageRange(StorageRange storageRange)
        {
            if (storageRange is not null)
            {
                NextSlotRange.Enqueue(storageRange);
            }
        }

        public void ReportStorageRangeRequestFinished(StorageRange storageRange = null)
        {
            EnqueueStorageRange(storageRange);

            Interlocked.Decrement(ref _activeStorageRequests);
        }

        public void ReportAccountRequestFinished()
        {
            Interlocked.Decrement(ref _activeAccountRequests);
        }

        public bool IsSnapGetRangesFinished()
        {
            return !MoreAccountsToRight
                && StoragesToRetrieve.Count == 0
                && NextSlotRange.Count == 0
                && CodesToRetrieve.Count == 0
                && AccountsToRefresh.Count == 0
                && _activeAccountRequests == 0
                && _activeStorageRequests == 0
                && _activeCodeRequests == 0
                && _activeAccRefreshRequests == 0;
        }

        private void GetSyncProgress()
        {
            byte[] progress = _db.Get(ACC_PROGRESS_KEY);
            if (progress != null)
            {
                NextAccountPath = new Keccak(progress);
                _logger.Info($"SNAP - State Ranges (Phase 1) progress loaded from DB:{NextAccountPath}");

                if (NextAccountPath == Keccak.MaxValue)
                {
                    _logger.Info($"SNAP - State Ranges (Phase 1) is finished. Healing (Phase 2) starting...");
                    MoreAccountsToRight = false;
                }
            }
        }

        private void FinishRangePhase()
        {
            MoreAccountsToRight = false;
            NextAccountPath = Keccak.MaxValue;
            _db.Set(ACC_PROGRESS_KEY, NextAccountPath.Bytes);
        }

        private void LogRequest(string reqType)
        {
            _testReqCount++;

            if(_testReqCount % 100 == 0)
            {

            }

            if (_testReqCount % 1 == 0)
            {
                _logger.Info($"SNAP - ({reqType}, diff:{_pivot.Diff}) {NextAccountPath}\t AccountRequests:{_activeAccountRequests} | StorageRequests:{_activeStorageRequests} | CodeRequests:{_activeCodeRequests} | AccountsToRefresh{_activeAccRefreshRequests} | {Environment.NewLine}Slots:{NextSlotRange.Count} | Storages:{StoragesToRetrieve.Count} | Codes:{CodesToRetrieve.Count} | AccountsToRefresh:{AccountsToRefresh.Count}");
            }
        }
    }
}
