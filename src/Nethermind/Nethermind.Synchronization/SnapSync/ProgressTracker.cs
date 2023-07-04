// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
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
        private const string NO_REQUEST = "Skipped Request";

        private const int STORAGE_BATCH_SIZE = 1_200;
        public const int HIGH_STORAGE_QUEUE_SIZE = STORAGE_BATCH_SIZE * 100;
        private const int CODES_BATCH_SIZE = 1_000;
        public const int HIGH_CODES_QUEUE_SIZE = CODES_BATCH_SIZE * 5;
        private readonly byte[] ACC_PROGRESS_KEY = Encoding.ASCII.GetBytes("AccountProgressKey");

        // This does not need to be a lot as it spawn other requests. In fact 8 is probably too much. It is severely
        // bottlenecked by _syncCommit lock in SnapProviderHelper, which in turns is limited by the IO.
        // In any case, all partition will be touched when calculating progress, so we can't really put like 1024 for this.
        private readonly int _accountRangePartitionCount;

        private long _reqCount;
        private int _activeAccountRequests;
        private int _activeStorageRequests;
        private int _activeCodeRequests;
        private int _activeAccRefreshRequests;

        private readonly ILogger _logger;
        private readonly IDb _db;

        // Partitions are indexed by its limit keccak/address as they are keep in the request struct and remain the same
        // throughout the sync. So its easy.
        private Dictionary<ValueKeccak, AccountRangePartition> AccountRangePartitions { get; set; } = new();

        // Using a queue here to evenly distribute request across partitions. Don't want a situation where one really slow
        // partition is taking up most of the time at the end of the sync.
        private ConcurrentQueue<AccountRangePartition> AccountRangeReadyForRequest { get; set; } = new();
        private ConcurrentQueue<StorageRange> NextSlotRange { get; set; } = new();
        private ConcurrentQueue<PathWithAccount> StoragesToRetrieve { get; set; } = new();
        private ConcurrentQueue<ValueKeccak> CodesToRetrieve { get; set; } = new();
        private ConcurrentQueue<AccountWithStorageStartingHash> AccountsToRefresh { get; set; } = new();


        private readonly Pivot _pivot;

        public ProgressTracker(IBlockTree blockTree, IDb db, ILogManager logManager, int accountRangePartitionCount = 8)
        {
            _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _db = db ?? throw new ArgumentNullException(nameof(db));

            _pivot = new Pivot(blockTree, logManager);

            if (accountRangePartitionCount < 1 || accountRangePartitionCount > 256)
                throw new ArgumentException("Account range partition must be between 1 to 256.");

            _accountRangePartitionCount = accountRangePartitionCount;
            SetupAccountRangePartition();

            //TODO: maybe better to move to a init method instead of the constructor
            GetSyncProgress();
        }

        private void SetupAccountRangePartition()
        {
            // Confusingly dividing the range evenly via UInt256 for example, consistently cause root hash mismatch.
            // The mismatch happens on exactly the same partition every time, suggesting tome kind of boundary issues
            // either on proof generation or validation.
            byte curStartingPath = 0;
            int partitionSize = (256 / _accountRangePartitionCount);

            for (var i = 0; i < _accountRangePartitionCount; i++)
            {
                AccountRangePartition partition = new AccountRangePartition();

                Keccak startingPath = new Keccak(Keccak.Zero.Bytes);
                startingPath.Bytes[0] = curStartingPath;

                partition.NextAccountPath = startingPath;
                partition.AccountPathStart = startingPath;

                curStartingPath += (byte)partitionSize;

                Keccak limitPath;

                // Special case for the last partition
                if (i == _accountRangePartitionCount - 1)
                {
                    limitPath = Keccak.MaxValue;
                }
                else
                {
                    limitPath = new Keccak(Keccak.Zero.Bytes);
                    limitPath.Bytes[0] = curStartingPath;
                }

                partition.AccountPathLimit = limitPath;

                AccountRangePartitions[limitPath] = partition;
                AccountRangeReadyForRequest.Enqueue(partition);
            }
        }

        public bool CanSync()
        {
            BlockHeader? header = _pivot.GetPivotHeader();
            if (header is null || header.Number == 0)
            {
                if (_logger.IsInfo) _logger.Info($"No Best Suggested Header available. Snap Sync not started.");

                return false;
            }

            if (_logger.IsInfo) _logger.Info($"Starting the Snap data sync from the {header.ToString(BlockHeader.Format.Short)} {header.StateRoot} root");

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

            if (!AccountsToRefresh.IsEmpty)
            {
                return DequeAccountToRefresh(request, rootHash);
            }

            if (ShouldRequestAccountRequests() && AccountRangeReadyForRequest.TryDequeue(out AccountRangePartition partition))
            {
                return CreateAccountRangeRequest(rootHash, partition, blockNumber, request);
            }

            if (TryDequeNextSlotRange(out StorageRange slotRange))
            {
                return CreateNextSlowRangeRequest(slotRange, rootHash, blockNumber, request);
            }

            if (StoragesToRetrieve.Count >= HIGH_STORAGE_QUEUE_SIZE)
            {
                return DequeStorageToRetrieveRequest(rootHash, blockNumber, request);
            }

            if (CodesToRetrieve.Count >= HIGH_CODES_QUEUE_SIZE)
            {
                return DequeCodeRequest(request);
            }

            if (!StoragesToRetrieve.IsEmpty)
            {
                return DequeStorageToRetrieveRequest(rootHash, blockNumber, request);
            }

            if (!CodesToRetrieve.IsEmpty)
            {
                return DequeCodeRequest(request);
            }

            bool rangePhaseFinished = IsSnapGetRangesFinished();
            if (rangePhaseFinished)
            {
                _logger.Info($"Snap - State Ranges (Phase 1) finished.");
                FinishRangePhase();
            }

            LogRequest(NO_REQUEST);

            return (null, IsSnapGetRangesFinished());
        }

        private (SnapSyncBatch request, bool finished) DequeCodeRequest(SnapSyncBatch request)
        {
            Interlocked.Increment(ref _activeCodeRequests);

            // TODO: optimize this
            List<ValueKeccak> codesToQuery = new(CODES_BATCH_SIZE);
            for (int i = 0; i < CODES_BATCH_SIZE && CodesToRetrieve.TryDequeue(out ValueKeccak codeHash); i++)
            {
                codesToQuery.Add(codeHash);
            }

            codesToQuery.Sort();

            LogRequest($"CodesToRetrieve:{codesToQuery.Count}");

            request.CodesRequest = codesToQuery.ToArray();

            return (request, false);
        }

        private (SnapSyncBatch request, bool finished) DequeStorageToRetrieveRequest(Keccak rootHash, long blockNumber,
            SnapSyncBatch request)
        {
            Interlocked.Increment(ref _activeStorageRequests);

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
                StartingHash = ValueKeccak.Zero,
                BlockNumber = blockNumber
            };

            LogRequest($"StoragesToRetrieve:{storagesToQuery.Count}");

            request.StorageRangeRequest = storageRange;

            return (request, false);
        }

        private (SnapSyncBatch request, bool finished) CreateNextSlowRangeRequest(StorageRange slotRange, Keccak rootHash,
            long blockNumber, SnapSyncBatch request)
        {
            slotRange.RootHash = rootHash;
            slotRange.BlockNumber = blockNumber;

            LogRequest($"NextSlotRange:{slotRange.Accounts.Length}");

            request.StorageRangeRequest = slotRange;

            return (request, false);
        }

        private (SnapSyncBatch request, bool finished) CreateAccountRangeRequest(Keccak rootHash,
            AccountRangePartition partition, long blockNumber, SnapSyncBatch request)
        {
            Interlocked.Increment(ref _activeAccountRequests);

            AccountRange range = new(
                rootHash,
                partition.NextAccountPath,
                partition.AccountPathLimit,
                blockNumber);

            LogRequest("AccountRange");

            request.AccountRangeRequest = range;

            return (request, false);
        }

        private (SnapSyncBatch request, bool finished) DequeAccountToRefresh(SnapSyncBatch request, Keccak rootHash)
        {
            Interlocked.Increment(ref _activeAccRefreshRequests);

            LogRequest($"AccountsToRefresh: {AccountsToRefresh.Count}");

            int queueLength = AccountsToRefresh.Count;
            AccountWithStorageStartingHash[] paths = new AccountWithStorageStartingHash[queueLength];

            for (int i = 0; i < queueLength && AccountsToRefresh.TryDequeue(out var acc); i++)
            {
                paths[i] = acc;
            }

            request.AccountsToRefreshRequest = new AccountsToRefreshRequest() { RootHash = rootHash, Paths = paths };

            return (request, false);
        }

        private bool ShouldRequestAccountRequests()
        {
            return _activeAccountRequests < _accountRangePartitionCount
                   && NextSlotRange.Count < 10
                   && StoragesToRetrieve.Count < HIGH_STORAGE_QUEUE_SIZE
                   && CodesToRetrieve.Count < HIGH_CODES_QUEUE_SIZE;
        }

        public void EnqueueCodeHashes(ReadOnlySpan<ValueKeccak> codeHashes)
        {
            foreach (var hash in codeHashes)
            {
                CodesToRetrieve.Enqueue(hash);
            }
        }

        public void ReportCodeRequestFinished(ReadOnlySpan<ValueKeccak> codeHashes)
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

        public void EnqueueAccountRefresh(PathWithAccount pathWithAccount, in ValueKeccak? startingHash)
        {
            AccountsToRefresh.Enqueue(new AccountWithStorageStartingHash() { PathAndAccount = pathWithAccount, StorageStartingHash = startingHash.GetValueOrDefault() });
        }

        public void ReportFullStorageRequestFinished(ReadOnlySpan<PathWithAccount> storages = default)
        {
            for (int index = 0; index < storages.Length; index++)
            {
                EnqueueAccountStorage(storages[index]);
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

        public void ReportAccountRangePartitionFinished(in ValueKeccak hashLimit)
        {
            AccountRangePartition partition = AccountRangePartitions[hashLimit];

            if (partition.MoreAccountsToRight)
            {
                AccountRangeReadyForRequest.Enqueue(partition);
            }
            Interlocked.Decrement(ref _activeAccountRequests);
        }

        public void UpdateAccountRangePartitionProgress(in ValueKeccak hashLimit, in ValueKeccak nextPath, bool moreChildrenToRight)
        {
            AccountRangePartition partition = AccountRangePartitions[hashLimit];

            partition.NextAccountPath = nextPath;
            partition.MoreAccountsToRight = moreChildrenToRight && nextPath < hashLimit;
        }

        public bool IsSnapGetRangesFinished()
        {
            return AccountRangeReadyForRequest.IsEmpty
                && StoragesToRetrieve.IsEmpty
                && NextSlotRange.IsEmpty
                && CodesToRetrieve.IsEmpty
                && AccountsToRefresh.IsEmpty
                && _activeAccountRequests == 0
                && _activeStorageRequests == 0
                && _activeCodeRequests == 0
                && _activeAccRefreshRequests == 0;
        }

        private void GetSyncProgress()
        {
            // Note, as before, the progress actually only store MaxValue or 0. So we can't actually resume
            // snap sync on restart.
            byte[] progress = _db.Get(ACC_PROGRESS_KEY);
            if (progress is { Length: 32 })
            {
                ValueKeccak path = new ValueKeccak(progress);

                if (path == ValueKeccak.MaxValue)
                {
                    _logger.Info($"Snap - State Ranges (Phase 1) is finished.");
                    foreach (KeyValuePair<ValueKeccak, AccountRangePartition> partition in AccountRangePartitions)
                    {
                        partition.Value.MoreAccountsToRight = false;
                    }
                    AccountRangeReadyForRequest.Clear();
                }
                else
                {
                    _logger.Info($"Snap - State Ranges (Phase 1) progress loaded from DB:{path}");
                }
            }
        }

        private void FinishRangePhase()
        {
            _db.Set(ACC_PROGRESS_KEY, ValueKeccak.MaxValue.Bytes);
        }

        private void LogRequest(string reqType)
        {
            if (_reqCount % 100 == 0)
            {
                int totalPathProgress = 0;
                foreach (KeyValuePair<ValueKeccak, AccountRangePartition> kv in AccountRangePartitions)
                {
                    AccountRangePartition? partiton = kv.Value;
                    int nextAccount = partiton.NextAccountPath.Bytes[0] * 256 + partiton.NextAccountPath.Bytes[1];
                    int startAccount = partiton.AccountPathStart.Bytes[0] * 256 + partiton.AccountPathStart.Bytes[1];
                    totalPathProgress += nextAccount - startAccount;
                }

                float progress = (float)(totalPathProgress / (double)(256 * 256));

                if (_logger.IsInfo) _logger.Info($"Snap         State Ranges (Phase 1): ({progress,8:P2}) [{new string('*', (int)(progress * 71))}{new string(' ', 71 - (int)(progress * 71))}]");
            }

            if (_logger.IsTrace || _reqCount % 1000 == 0)
            {
                int moreAccountCount = AccountRangePartitions.Count(kv => kv.Value.MoreAccountsToRight);

                _logger.Info(
                    $"Snap - ({reqType}, diff: {_pivot.Diff}) {moreAccountCount} - Requests Account: {_activeAccountRequests} | Storage: {_activeStorageRequests} | Code: {_activeCodeRequests} | Refresh: {_activeAccRefreshRequests} - Queues Slots: {NextSlotRange.Count} | Storages: {StoragesToRetrieve.Count} | Codes: {CodesToRetrieve.Count} | Refresh: {AccountsToRefresh.Count}");
            }
        }

        private bool TryDequeNextSlotRange(out StorageRange item)
        {
            Interlocked.Increment(ref _activeStorageRequests);
            if (!NextSlotRange.TryDequeue(out item))
            {
                Interlocked.Decrement(ref _activeStorageRequests);
                return false;
            }

            return true;
        }

        // A partition of the top level account range starting from `AccountPathStart` to `AccountPathLimit` (exclusive).
        private class AccountRangePartition
        {
            public ValueKeccak NextAccountPath { get; set; } = ValueKeccak.Zero;
            public ValueKeccak AccountPathStart { get; set; } = ValueKeccak.Zero; // Not really needed, but useful
            public ValueKeccak AccountPathLimit { get; set; } = ValueKeccak.MaxValue;
            public bool MoreAccountsToRight { get; set; } = true;
        }
    }
}
