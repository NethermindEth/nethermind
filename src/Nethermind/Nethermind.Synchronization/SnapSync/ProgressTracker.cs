// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Autofac.Features.AttributeFilters;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Snap;

namespace Nethermind.Synchronization.SnapSync
{
    public class ProgressTracker : IDisposable
    {
        private const string NO_REQUEST = "Skipped Request";

        private const int STORAGE_BATCH_SIZE = 1_200;
        public const int HIGH_STORAGE_QUEUE_SIZE = STORAGE_BATCH_SIZE * 100;
        private const int CODES_BATCH_SIZE = 1_000;
        public const int HIGH_CODES_QUEUE_SIZE = CODES_BATCH_SIZE * 5;
        private const uint StorageRangeSplitFactor = 2;
        internal static readonly byte[] ACC_PROGRESS_KEY = "AccountProgressKey"u8.ToArray();

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
        string? _lastStateRangesReport;

        // Partitions are indexed by its limit keccak/address as they are keep in the request struct and remain the same
        // throughout the sync. So its easy.
        private Dictionary<ValueHash256, AccountRangePartition> AccountRangePartitions { get; set; } = new();

        // Using a queue here to evenly distribute request across partitions. Don't want a situation where one really slow
        // partition is taking up most of the time at the end of the sync.
        private ConcurrentQueue<AccountRangePartition> AccountRangeReadyForRequest { get; set; } = new();
        private ConcurrentQueue<StorageRange> NextSlotRange { get; set; } = new();
        private ConcurrentQueue<PathWithAccount> StoragesToRetrieve { get; set; } = new();
        private ConcurrentQueue<ValueHash256> CodesToRetrieve { get; set; } = new();
        private ConcurrentQueue<AccountWithStorageStartingHash> AccountsToRefresh { get; set; } = new();

        private readonly FastSync.StateSyncPivot _pivot;
        private readonly bool _enableStorageRangeSplit;

        public ProgressTracker([KeyFilter(DbNames.State)] IDb db, ISyncConfig syncConfig, FastSync.StateSyncPivot pivot, ILogManager? logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _db = db ?? throw new ArgumentNullException(nameof(db));

            _pivot = pivot;

            int accountRangePartitionCount = syncConfig.SnapSyncAccountRangePartitionCount;
            if (accountRangePartitionCount < 1)
                throw new ArgumentException($"Account range partition must be between 1 to {int.MaxValue}.");

            _accountRangePartitionCount = accountRangePartitionCount;
            _enableStorageRangeSplit = syncConfig.EnableSnapSyncStorageRangeSplit;

            SetupAccountRangePartition();

            //TODO: maybe better to move to a init method instead of the constructor
            GetSyncProgress();
        }

        private void SetupAccountRangePartition()
        {
            uint curStartingPath = 0;
            uint partitionSize = (uint)(((ulong)uint.MaxValue + 1) / (ulong)_accountRangePartitionCount);

            for (var i = 0; i < _accountRangePartitionCount; i++)
            {
                AccountRangePartition partition = new AccountRangePartition();

                Hash256 startingPath = new Hash256(Keccak.Zero.Bytes);
                BinaryPrimitives.WriteUInt32BigEndian(startingPath.Bytes, curStartingPath);

                partition.NextAccountPath = startingPath;
                partition.AccountPathStart = startingPath;

                curStartingPath += partitionSize;

                Hash256 limitPath;

                // Special case for the last partition
                if (i == _accountRangePartitionCount - 1)
                {
                    limitPath = Keccak.MaxValue;
                }
                else
                {
                    limitPath = new Hash256(Keccak.Zero.Bytes);
                    BinaryPrimitives.WriteUInt32BigEndian(limitPath.Bytes, curStartingPath);
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

        public bool IsFinished(out SnapSyncBatch? nextBatch)
        {
            if (_pivot.GetPivotHeader() is null)
            {
                nextBatch = null;
                return false;
            }

            Interlocked.Increment(ref _reqCount);

            BlockHeader? pivotHeader = _pivot.GetPivotHeader();
            Hash256 rootHash = pivotHeader!.StateRoot!;
            long blockNumber = pivotHeader.Number;

            if (!AccountsToRefresh.IsEmpty)
            {
                nextBatch = DequeAccountToRefresh(rootHash);
            }
            else if (ShouldRequestAccountRequests() && AccountRangeReadyForRequest.TryDequeue(out AccountRangePartition partition))
            {
                nextBatch = CreateAccountRangeRequest(rootHash, partition, blockNumber);
            }
            else if (TryDequeNextSlotRange(out StorageRange slotRange))
            {
                nextBatch = CreateNextSlowRangeRequest(slotRange, rootHash, blockNumber);
            }
            else if (StoragesToRetrieve.Count >= HIGH_STORAGE_QUEUE_SIZE)
            {
                nextBatch = DequeStorageToRetrieveRequest(rootHash, blockNumber);
            }
            else if (CodesToRetrieve.Count >= HIGH_CODES_QUEUE_SIZE)
            {
                nextBatch = DequeCodeRequest();
            }
            else if (!StoragesToRetrieve.IsEmpty)
            {
                nextBatch = DequeStorageToRetrieveRequest(rootHash, blockNumber);
            }
            else if (!CodesToRetrieve.IsEmpty)
            {
                nextBatch = DequeCodeRequest();
            }
            else
            {
                nextBatch = null;
                bool rangePhaseFinished = IsSnapGetRangesFinished();
                if (rangePhaseFinished)
                {
                    _logger.Info("Snap - State Ranges (Phase 1) finished.");
                    FinishRangePhase();
                }

                LogRequest(NO_REQUEST);

                return IsSnapGetRangesFinished();
            }

            return false;
        }

        private SnapSyncBatch DequeCodeRequest()
        {
            Interlocked.Increment(ref _activeCodeRequests);

            ArrayPoolList<ValueHash256> codesToQuery = new(CODES_BATCH_SIZE);
            for (int i = 0; i < CODES_BATCH_SIZE && CodesToRetrieve.TryDequeue(out ValueHash256 codeHash); i++)
            {
                codesToQuery.Add(codeHash);
            }

            codesToQuery.AsSpan().Sort();

            LogRequest($"CodesToRetrieve:{codesToQuery.Count}");

            return new SnapSyncBatch { CodesRequest = codesToQuery };
        }

        private SnapSyncBatch DequeStorageToRetrieveRequest(Hash256 rootHash, long blockNumber)
        {
            Interlocked.Increment(ref _activeStorageRequests);

            ArrayPoolList<PathWithAccount> storagesToQuery = new(STORAGE_BATCH_SIZE);
            for (int i = 0; i < STORAGE_BATCH_SIZE && StoragesToRetrieve.TryDequeue(out PathWithAccount storage); i++)
            {
                storagesToQuery.Add(storage);
            }

            StorageRange storageRange = new()
            {
                RootHash = rootHash,
                Accounts = storagesToQuery,
                StartingHash = ValueKeccak.Zero,
                BlockNumber = blockNumber
            };

            LogRequest($"StoragesToRetrieve:{storagesToQuery.Count}");

            return new SnapSyncBatch { StorageRangeRequest = storageRange };
        }

        private SnapSyncBatch CreateNextSlowRangeRequest(StorageRange slotRange, Hash256 rootHash, long blockNumber)
        {
            slotRange.RootHash = rootHash;
            slotRange.BlockNumber = blockNumber;

            LogRequest($"NextSlotRange:{slotRange.Accounts.Count}");

            return new SnapSyncBatch { StorageRangeRequest = slotRange };
        }

        private SnapSyncBatch CreateAccountRangeRequest(Hash256 rootHash, AccountRangePartition partition, long blockNumber)
        {
            Interlocked.Increment(ref _activeAccountRequests);

            AccountRange range = new(
                rootHash,
                partition.NextAccountPath,
                partition.AccountPathLimit,
                blockNumber);

            LogRequest("AccountRange");
            return new SnapSyncBatch { AccountRangeRequest = range };
        }

        private SnapSyncBatch DequeAccountToRefresh(Hash256 rootHash)
        {
            Interlocked.Increment(ref _activeAccRefreshRequests);

            LogRequest($"AccountsToRefresh: {AccountsToRefresh.Count}");

            int queueLength = AccountsToRefresh.Count;
            ArrayPoolList<AccountWithStorageStartingHash> paths = new(queueLength);

            for (int i = 0; i < queueLength && AccountsToRefresh.TryDequeue(out AccountWithStorageStartingHash acc); i++)
            {
                paths.Add(acc);
            }

            return new SnapSyncBatch { AccountsToRefreshRequest = new AccountsToRefreshRequest { RootHash = rootHash, Paths = paths } };
        }

        private bool ShouldRequestAccountRequests()
        {
            return _activeAccountRequests < _accountRangePartitionCount
                   && NextSlotRange.Count < 10
                   && StoragesToRetrieve.Count < HIGH_STORAGE_QUEUE_SIZE
                   && CodesToRetrieve.Count < HIGH_CODES_QUEUE_SIZE;
        }

        public void EnqueueCodeHashes(ReadOnlySpan<ValueHash256> codeHashes)
        {
            foreach (var hash in codeHashes)
            {
                CodesToRetrieve.Enqueue(hash);
            }
        }

        public void ReportCodeRequestFinished(ReadOnlySpan<ValueHash256> codeHashes)
        {
            EnqueueCodeHashes(codeHashes);

            Interlocked.Decrement(ref _activeCodeRequests);
        }

        public void ReportAccountRefreshFinished(AccountsToRefreshRequest? accountsToRefreshRequest = null)
        {
            if (accountsToRefreshRequest is not null)
            {
                foreach (AccountWithStorageStartingHash path in accountsToRefreshRequest.Paths)
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

        public void EnqueueAccountRefresh(PathWithAccount pathWithAccount, in ValueHash256? startingHash, in ValueHash256? hashLimit)
        {
            _pivot.UpdatedStorages.Add(pathWithAccount.Path.ToCommitment());
            AccountsToRefresh.Enqueue(new AccountWithStorageStartingHash() { PathAndAccount = pathWithAccount, StorageStartingHash = startingHash.GetValueOrDefault(), StorageHashLimit = hashLimit ?? Keccak.MaxValue });
        }

        public void ReportFullStorageRequestFinished(IEnumerable<PathWithAccount>? storages = null)
        {
            if (storages is not null)
            {
                foreach (PathWithAccount pathWithAccount in storages)
                {
                    EnqueueAccountStorage(pathWithAccount);
                }
            }

            Interlocked.Decrement(ref _activeStorageRequests);
        }

        public void EnqueueStorageRange(StorageRange? storageRange)
        {
            if (storageRange is not null)
            {
                NextSlotRange.Enqueue(storageRange);
            }
        }

        public void EnqueueStorageRange(StorageRange parentRequest, int accountIndex, ValueHash256 lastProcessedHash)
        {
            ValueHash256 limitHash = parentRequest.LimitHash ?? Keccak.MaxValue;
            if (lastProcessedHash > limitHash)
                return;

            ValueHash256? startingHash = parentRequest.StartingHash;
            PathWithAccount account = parentRequest.Accounts[accountIndex];
            UInt256 limit = new UInt256(limitHash.Bytes, true);
            UInt256 lastProcessed = new UInt256(lastProcessedHash.Bytes, true);
            UInt256 start = startingHash.HasValue ? new UInt256(startingHash.Value.Bytes, true) : UInt256.Zero;

            UInt256 fullRange = limit - start;

            if (_enableStorageRangeSplit && lastProcessed < fullRange / StorageRangeSplitFactor + start)
            {
                ValueHash256 halfOfLeftHash = ((limit - lastProcessed) / 2 + lastProcessed).ToValueHash();

                NextSlotRange.Enqueue(new StorageRange
                {
                    Accounts = new ArrayPoolList<PathWithAccount>(1) { account },
                    StartingHash = lastProcessedHash,
                    LimitHash = halfOfLeftHash
                });

                NextSlotRange.Enqueue(new StorageRange
                {
                    Accounts = new ArrayPoolList<PathWithAccount>(1) { account },
                    StartingHash = halfOfLeftHash,
                    LimitHash = limitHash
                });

                if (_logger.IsTrace)
                    _logger.Trace($"EnqueueStorageRange account {account.Path} start hash: {startingHash} | last processed: {lastProcessedHash} | limit: {limitHash} | split {halfOfLeftHash}");
            }
            else
            {
                //default - no split
                var storageRange = new StorageRange
                {
                    Accounts = new ArrayPoolList<PathWithAccount>(1) { account },
                    StartingHash = lastProcessedHash,
                    LimitHash = limitHash
                };
                NextSlotRange.Enqueue(storageRange);
            }
        }

        public void ReportStorageRangeRequestFinished(StorageRange? storageRange = null)
        {
            EnqueueStorageRange(storageRange);

            Interlocked.Decrement(ref _activeStorageRequests);
        }

        public void ReportAccountRangePartitionFinished(in ValueHash256 hashLimit)
        {
            AccountRangePartition partition = AccountRangePartitions[hashLimit];

            if (partition.MoreAccountsToRight)
            {
                AccountRangeReadyForRequest.Enqueue(partition);
            }
            Interlocked.Decrement(ref _activeAccountRequests);
        }

        public void UpdateAccountRangePartitionProgress(in ValueHash256 hashLimit, in ValueHash256 nextPath, bool moreChildrenToRight)
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
                ValueHash256 path = new(progress);

                if (path == ValueKeccak.MaxValue)
                {
                    _logger.Info($"Snap - State Ranges (Phase 1) is finished.");
                    foreach (KeyValuePair<ValueHash256, AccountRangePartition> partition in AccountRangePartitions)
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
            _db.PutSpan(ACC_PROGRESS_KEY, ValueKeccak.MaxValue.Bytes, WriteFlags.DisableWAL);
            _db.Flush();
        }

        private void LogRequest(string reqType)
        {
            if (_reqCount % 100 == 0)
            {
                int totalPathProgress = 0;
                foreach (KeyValuePair<ValueHash256, AccountRangePartition> kv in AccountRangePartitions)
                {
                    AccountRangePartition? partiton = kv.Value;
                    int nextAccount = partiton.NextAccountPath.Bytes[0] * 256 + partiton.NextAccountPath.Bytes[1];
                    int startAccount = partiton.AccountPathStart.Bytes[0] * 256 + partiton.AccountPathStart.Bytes[1];
                    totalPathProgress += nextAccount - startAccount;
                }

                float progress = (float)(totalPathProgress / (double)(256 * 256));

                if (_logger.IsInfo)
                {
                    string stateRangesReport = $"Snap         State Ranges (Phase 1): ({progress,8:P2}) {Progress.GetMeter(progress, 1)}";
                    if (_lastStateRangesReport != stateRangesReport)
                    {
                        _logger.Info(stateRangesReport);
                        _lastStateRangesReport = stateRangesReport;
                    }
                }
            }

            if (_logger.IsTrace || (_logger.IsDebug && _reqCount % 1000 == 0))
            {
                int moreAccountCount = AccountRangePartitions.Count(static kv => kv.Value.MoreAccountsToRight);

                _logger.Debug(
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
            public ValueHash256 NextAccountPath { get; set; } = ValueKeccak.Zero;
            public ValueHash256 AccountPathStart { get; set; } = ValueKeccak.Zero; // Not really needed, but useful
            public ValueHash256 AccountPathLimit { get; set; } = ValueKeccak.MaxValue;
            public bool MoreAccountsToRight { get; set; } = true;
        }

        public void Dispose()
        {
            while (NextSlotRange.TryDequeue(out StorageRange? range))
            {
                range?.Dispose();
            }
        }
    }
}
