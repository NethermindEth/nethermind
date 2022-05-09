using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading;
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
        private const string NO_REQUEST = "NO REQUEST";

        private const int STORAGE_BATCH_SIZE = 1_200;
        private const int CODES_BATCH_SIZE = 1_000;
        private readonly byte[] ACC_PROGRESS_KEY = Encoding.ASCII.GetBytes("AccountProgressKey");

        private long _reqCount;
        private int _activeAccountRequests;
        private int _activeStorageRequests;
        private int _activeCodeRequests;
        private int _activeAccRefreshRequests;

        private readonly ILogger _logger;
        private readonly IDb _db;

        public Keccak NextAccountPath { get; set; } = Keccak.Zero;
        private ConcurrentQueue<StorageRange> NextSlotRange { get; set; } = new();
        private ConcurrentQueue<PathWithAccount> StoragesToRetrieve { get; set; } = new();
        private ConcurrentQueue<Keccak> CodesToRetrieve { get; set; } = new();
        private ConcurrentQueue<AccountWithStorageStartingHash> AccountsToRefresh { get; set; } = new();

        public bool MoreAccountsToRight { get; set; } = true;

        private readonly Pivot _pivot;
        public event EventHandler<EventArgs> SnapSyncFinished;

        public ProgressTracker(IBlockTree blockTree, IDb db, ILogManager logManager)
        {
            _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _db = db ?? throw new ArgumentNullException(nameof(db));

            _pivot = new Pivot(blockTree, logManager);
            
            //TODO: maybe better to move to a init method instead of the constructor
            GetSyncProgress();
        }

        public bool CanSync()
        {
            BlockHeader? header = _pivot.GetPivotHeader();
            if (header == null || header.Number == 0)
            {
                if (_logger.IsInfo) _logger.Info($"No Best Suggested Header available. Snap Sync not started.");

                return false;
            }

            if (_logger.IsInfo) _logger.Info($"Starting the SNAP data sync from the {header.ToString(BlockHeader.Format.Short)} {header.StateRoot} root");

            return true;
        }

        public void UpdatePivot()
        {
            _pivot.UpdateHeaderForcefully();
        }

        public (SnapSyncBatch request, bool finished) GetNextRequest()
        {
            Interlocked.Increment(ref _reqCount);
            
            var pivotHeader = _pivot.GetPivotHeader();
            var rootHash = pivotHeader.StateRoot;
            var blockNumber = pivotHeader.Number;

            SnapSyncBatch request = new();

            if (AccountsToRefresh.Count > 0)
            {
                LogRequest($"AccountsToRefresh:{AccountsToRefresh.Count}");

                int queueLength = AccountsToRefresh.Count;
                AccountWithStorageStartingHash[] paths = new AccountWithStorageStartingHash[queueLength];

                for (int i = 0; i < queueLength && AccountsToRefresh.TryDequeue(out var acc); i++)
                {
                    paths[i] = acc;
                }

                Interlocked.Increment(ref _activeAccRefreshRequests);

                request.AccountsToRefreshRequest = new AccountsToRefreshRequest() { RootHash = rootHash, Paths = paths };

                return (request, false);

            }
            else if (MoreAccountsToRight && _activeAccountRequests == 0 && NextSlotRange.Count < 10 && StoragesToRetrieve.Count < 5 * STORAGE_BATCH_SIZE && CodesToRetrieve.Count < 5 * CODES_BATCH_SIZE)
            {
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
            if (rangePhaseFinished)
            {
                _logger.Info($"SNAP - State Ranges (Phase 1) finished.");
                FinishRangePhase();
            }

            LogRequest(NO_REQUEST);
            
            return (null, IsSnapGetRangesFinished());
        }

        public void EnqueueCodeHashes(ICollection<Keccak>? codeHashes)
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
                for (int index = 0; index < storages.Length; index++)
                {
                    EnqueueAccountStorage(storages[index]);
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
            if (progress is { Length: 32 })
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
            
            SnapSyncFinished?.Invoke(this, EventArgs.Empty);
        }

        private void LogRequest(string reqType)
        {
            if (_reqCount % 100 == 0)
            {
                double progress = 100 * NextAccountPath.Bytes[0] / (double)256;

                if (_logger.IsInfo) _logger.Info($"SNAP - progress of State Ranges (Phase 1): {progress}% [{new string('*', (int)progress / 10)}{new string(' ', 10 - (int)progress / 10)}]");
            }

            if (_logger.IsTrace || _reqCount % 1000 == 0)
            {
                _logger.Info(
                    $"SNAP - ({reqType}, diff:{_pivot.Diff}) {MoreAccountsToRight}:{NextAccountPath} - Requests Account:{_activeAccountRequests} | Storage:{_activeStorageRequests} | Code:{_activeCodeRequests} | Refresh:{_activeAccRefreshRequests} - Queues Slots:{NextSlotRange.Count} | Storages:{StoragesToRetrieve.Count} | Codes:{CodesToRetrieve.Count} | Refresh:{AccountsToRefresh.Count}");
            }
        }
    }
}
