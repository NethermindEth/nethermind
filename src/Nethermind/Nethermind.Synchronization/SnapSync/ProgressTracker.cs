// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
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
using Nethermind.Serialization.Rlp;
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
        private const int SnapRangesProgressVersion = 1;
        internal static readonly byte[] ACC_PROGRESS_KEY = "AccountProgressKey"u8.ToArray();
        internal static readonly byte[] SNAP_RANGES_PROGRESS_KEY = "SnapRangesProgressKey"u8.ToArray();

        // This does not need to be a lot as it spawn other requests. In fact 8 is probably too much. It is severely
        // bottlenecked by _syncCommit lock in SnapProviderHelper, which in turns is limited by the IO.
        // In any case, all partition will be touched when calculating progress, so we can't really put like 1024 for this.
        private readonly int _accountRangePartitionCount;

        private long _reqCount;
        private int _activeAccountRequests;
        private int _activeStorageRequests;
        private readonly ConcurrentDictionary<ValueHash256, LargeProgressStatus> _largeStorageProgress = new();
        private long? _estimatedStorageRemaining = null;
        private bool _shouldStartLoggingLargeStorage = false;
        private bool _rangePhaseFinished = false;
        private bool _snapRangesProgressDirty = false;

        private int _activeCodeRequests;
        private int _activeAccRefreshRequests;

        private readonly ILogger _logger;
        private readonly IDb _db;
        string? _lastStateRangesReport;
        private DateTimeOffset _lastLogTime = DateTimeOffset.MinValue;
        private readonly TimeSpan _maxTimeBetweenLog = TimeSpan.FromSeconds(5);

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

        private readonly FastSync.IStateSyncPivot _pivot;
        private readonly bool _enableStorageRangeSplit;

        public ProgressTracker([KeyFilter(DbNames.State)] IDb db, ISyncConfig syncConfig, FastSync.IStateSyncPivot pivot, ILogManager? logManager)
        {
            _logger = logManager?.GetClassLogger<ProgressTracker>() ?? throw new ArgumentNullException(nameof(logManager));
            _db = db ?? throw new ArgumentNullException(nameof(db));

            _pivot = pivot;

            int accountRangePartitionCount = syncConfig.SnapSyncAccountRangePartitionCount;
            if (accountRangePartitionCount < 1)
                throw new ArgumentException($"Account range partition must be between 1 to {int.MaxValue}.");

            _accountRangePartitionCount = accountRangePartitionCount;
            _enableStorageRangeSplit = syncConfig.EnableSnapSyncStorageRangeSplit;

            SetupAccountRangePartition();

            //TODO: maybe better to move to a init method instead of the constructor
            if (!GetSyncProgress())
            {
                RestoreSnapRangesProgress();
            }
        }

        private void SetupAccountRangePartition()
        {
            uint curStartingPath = 0;
            uint partitionSize = (uint)(((ulong)uint.MaxValue + 1) / (ulong)_accountRangePartitionCount);

            for (int i = 0; i < _accountRangePartitionCount; i++)
            {
                AccountRangePartition partition = new();

                Hash256 startingPath = new(Keccak.Zero.Bytes);
                BinaryPrimitives.WriteUInt32BigEndian(startingPath.Bytes, curStartingPath);

                partition.NextAccountPath = startingPath;
                partition.AccountPathStart = startingPath;

                curStartingPath += partitionSize;

                ValueHash256 limitPath;

                // Special case for the last partition
                if (i == _accountRangePartitionCount - 1)
                {
                    limitPath = Keccak.MaxValue;
                }
                else
                {
                    limitPath = new Hash256(Keccak.Zero.Bytes);
                    BinaryPrimitives.WriteUInt32BigEndian(limitPath.BytesAsSpan, curStartingPath);
                    limitPath = limitPath.DecrementPath(); // Limit is inclusive
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

        public void UpdatePivot() => _pivot.UpdateHeaderForcefully();

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
            Interlocked.Increment(ref _activeStorageRequests); // for race condition so that snap does not exit prematurely

            ArrayPoolList<PathWithAccount> storagesToQuery = new(STORAGE_BATCH_SIZE);
            for (int i = 0; i < STORAGE_BATCH_SIZE && StoragesToRetrieve.TryDequeue(out PathWithAccount storage); i++)
            {
                storagesToQuery.Add(storage);
            }

            Interlocked.Add(ref _activeStorageRequests, storagesToQuery.Count);
            Interlocked.Decrement(ref _activeStorageRequests);

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

        private bool ShouldRequestAccountRequests() => _activeAccountRequests < _accountRangePartitionCount
                   && NextSlotRange.Count < 10
                   && StoragesToRetrieve.Count < HIGH_STORAGE_QUEUE_SIZE
                   && CodesToRetrieve.Count < HIGH_CODES_QUEUE_SIZE;

        public void EnqueueCodeHashes(ReadOnlySpan<ValueHash256> codeHashes)
        {
            foreach (ValueHash256 hash in codeHashes)
            {
                CodesToRetrieve.Enqueue(hash);
                _snapRangesProgressDirty = true;
            }
        }

        public void ReportCodeRequestFinished(ReadOnlySpan<ValueHash256> codeHashes)
        {
            EnqueueCodeHashes(codeHashes);

            Interlocked.Decrement(ref _activeCodeRequests);
            _snapRangesProgressDirty = true;
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
            _snapRangesProgressDirty = true;
        }

        public void EnqueueAccountStorage(PathWithAccount pwa)
        {
            StoragesToRetrieve.Enqueue(pwa);
            _snapRangesProgressDirty = true;
        }

        public void EnqueueAccountRefresh(PathWithAccount pathWithAccount, in ValueHash256? startingHash, in ValueHash256? hashLimit)
        {
            TrackAccountToHeal(pathWithAccount.Path);
            AccountsToRefresh.Enqueue(new AccountWithStorageStartingHash() { PathAndAccount = pathWithAccount, StorageStartingHash = startingHash.GetValueOrDefault(), StorageHashLimit = hashLimit ?? Keccak.MaxValue });
            _snapRangesProgressDirty = true;
        }

        public void ReportFullStorageRequestFinished(int originalStorageCount, IEnumerable<PathWithAccount>? storages = null)
        {
            if (storages is not null)
            {
                foreach (PathWithAccount pathWithAccount in storages)
                {
                    EnqueueAccountStorage(pathWithAccount);
                }
            }

            Interlocked.Add(ref _activeStorageRequests, -originalStorageCount);
            _snapRangesProgressDirty = true;
        }

        public void EnqueueNextSlot(StorageRange? storageRange)
        {
            if (storageRange is not null)
            {
                NextSlotRange.Enqueue(storageRange);
                _snapRangesProgressDirty = true;
            }
        }

        public void EnqueueNextSlot(StorageRange parentRequest, int accountIndex, ValueHash256 lastProcessedHash, int slotCount)
        {
            ValueHash256 limitHash = parentRequest.LimitHash ?? Keccak.MaxValue;
            if (lastProcessedHash > limitHash)
                return;

            ValueHash256? startingHash = parentRequest.StartingHash;
            PathWithAccount account = parentRequest.Accounts[accountIndex];
            UInt256 limit = new(limitHash.Bytes, true);
            UInt256 lastProcessed = new(lastProcessedHash.Bytes, true);
            UInt256 start = startingHash.HasValue ? new UInt256(startingHash.Value.Bytes, true) : UInt256.Zero;

            // Splitting storage will cause the storage proof to not get stitched completely, causing more healing time and
            // causes it to be tracked for healing, also, one more slot range to keep in memory.
            // So we only split if the estimated remaining slot count is large enough. This is recursive, so large
            // contract will continue getting split until the remaining slot count is low enough.
            double slotSize = lastProcessed == start ? 0 : (double)(lastProcessed - start) / slotCount;
            int estimatedRemainingSlotCount = slotSize == 0 ? 0 : (int)((double)(limit - lastProcessed) / slotSize);

            UInt256 fullRange = limit - start;

            if (estimatedRemainingSlotCount > 10_000_000 && _enableStorageRangeSplit && lastProcessed < fullRange / StorageRangeSplitFactor + start)
            {
                ValueHash256 halfOfLeftHash = ((limit - lastProcessed) / 2 + lastProcessed).ToValueHash();

                NextSlotRange.Enqueue(new StorageRange
                {
                    Accounts = new ArrayPoolList<PathWithAccount>(1) { account },
                    StartingHash = lastProcessedHash.IncrementPath(),
                    LimitHash = halfOfLeftHash
                });

                NextSlotRange.Enqueue(new StorageRange
                {
                    Accounts = new ArrayPoolList<PathWithAccount>(1) { account },
                    StartingHash = halfOfLeftHash.IncrementPath(),
                    LimitHash = limitHash
                });

                if (_logger.IsTrace)
                    _logger.Trace($"EnqueueStorageRange account {account.Path} start hash: {startingHash} | last processed: {lastProcessedHash} | limit: {limitHash} | split {halfOfLeftHash}");
            }
            else
            {
                //default - no split
                StorageRange storageRange = new()
                {
                    Accounts = new ArrayPoolList<PathWithAccount>(1) { account },
                    StartingHash = lastProcessedHash.IncrementPath(),
                    LimitHash = limitHash
                };
                NextSlotRange.Enqueue(storageRange);
            }

            _snapRangesProgressDirty = true;
        }

        public void RetryStorageRange(StorageRange storageRange)
        {
            bool dispose = false;
            if (storageRange.Accounts.Count == 1)
            {
                EnqueueNextSlot(storageRange);
            }
            else
            {
                foreach (PathWithAccount account in storageRange.Accounts)
                {
                    EnqueueAccountStorage(account);
                }

                dispose = true;
            }

            Interlocked.Add(ref _activeStorageRequests, -(storageRange?.Accounts.Count ?? 0));
            _snapRangesProgressDirty = true;
            if (dispose) storageRange.Dispose();
        }

        public void ReportAccountRangePartitionFinished(in ValueHash256 hashLimit)
        {
            AccountRangePartition partition = AccountRangePartitions[hashLimit];

            if (partition.MoreAccountsToRight)
            {
                AccountRangeReadyForRequest.Enqueue(partition);
            }
            Interlocked.Decrement(ref _activeAccountRequests);
            _snapRangesProgressDirty = true;
        }

        public void UpdateAccountRangePartitionProgress(in ValueHash256 hashLimit, in ValueHash256 nextPath, bool moreChildrenToRight)
        {
            AccountRangePartition partition = AccountRangePartitions[hashLimit];

            partition.NextAccountPath = nextPath;
            partition.MoreAccountsToRight = moreChildrenToRight && nextPath < hashLimit;
            _snapRangesProgressDirty = true;
        }

        public bool IsSnapGetRangesFinished() => AccountRangeReadyForRequest.IsEmpty
                   && StoragesToRetrieve.IsEmpty
                   && NextSlotRange.IsEmpty
                   && CodesToRetrieve.IsEmpty
                   && AccountsToRefresh.IsEmpty
                   && _activeAccountRequests == 0
                   && _activeStorageRequests == 0
                   && _activeCodeRequests == 0
                   && _activeAccRefreshRequests == 0;

        private bool GetSyncProgress()
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
                    _rangePhaseFinished = true;
                    return true;
                }
                else
                {
                    _logger.Info($"Snap - State Ranges (Phase 1) progress loaded from DB:{path}");
                }
            }

            return false;
        }

        private void FinishRangePhase()
        {
            _rangePhaseFinished = true;
            _db.Remove(SNAP_RANGES_PROGRESS_KEY);
            _db.PutSpan(ACC_PROGRESS_KEY, ValueKeccak.MaxValue.Bytes, WriteFlags.DisableWAL);
            _db.Flush();
        }

        private void PersistSnapRangesProgressIfSafe()
        {
            if (!CanPersistSnapRangesProgress())
            {
                return;
            }

            using MemoryStream stream = new();
            using BinaryWriter writer = new(stream);

            writer.Write(SnapRangesProgressVersion);
            writer.Write(_accountRangePartitionCount);

            writer.Write(AccountRangePartitions.Count);
            foreach (KeyValuePair<ValueHash256, AccountRangePartition> keyValuePair in AccountRangePartitions)
            {
                WriteValueHash256(writer, keyValuePair.Key);
                WriteValueHash256(writer, keyValuePair.Value.NextAccountPath);
                WriteValueHash256(writer, keyValuePair.Value.AccountPathStart);
                WriteValueHash256(writer, keyValuePair.Value.AccountPathLimit);
                writer.Write(keyValuePair.Value.MoreAccountsToRight);
            }

            AccountRangePartition[] readyPartitions = AccountRangeReadyForRequest.ToArray();
            writer.Write(readyPartitions.Length);
            for (int i = 0; i < readyPartitions.Length; i++)
            {
                WriteValueHash256(writer, readyPartitions[i].AccountPathLimit);
            }

            WriteStorageRanges(writer, NextSlotRange.ToArray());
            WritePathWithAccounts(writer, StoragesToRetrieve.ToArray());
            WriteValueHash256Array(writer, CodesToRetrieve.ToArray());
            WriteAccountsToRefresh(writer, AccountsToRefresh.ToArray());

            _db.PutSpan(SNAP_RANGES_PROGRESS_KEY, stream.ToArray(), WriteFlags.DisableWAL);
            _db.Flush();
            _snapRangesProgressDirty = false;
        }

        private bool CanPersistSnapRangesProgress() =>
            !_rangePhaseFinished
            && _snapRangesProgressDirty
            && _activeAccountRequests == 0
            && _activeStorageRequests == 0
            && _activeCodeRequests == 0
            && _activeAccRefreshRequests == 0;

        private void RestoreSnapRangesProgress()
        {
            byte[]? checkpoint = _db.Get(SNAP_RANGES_PROGRESS_KEY);
            if (checkpoint is null)
            {
                return;
            }

            try
            {
                using MemoryStream stream = new(checkpoint);
                using BinaryReader reader = new(stream);

                int version = reader.ReadInt32();
                if (version != SnapRangesProgressVersion)
                {
                    if (_logger.IsWarn) _logger.Warn($"Ignoring unsupported SnapRanges progress checkpoint version {version}.");
                    return;
                }

                int partitionCount = reader.ReadInt32();
                if (partitionCount != _accountRangePartitionCount)
                {
                    if (_logger.IsWarn) _logger.Warn($"Ignoring SnapRanges progress checkpoint for {partitionCount} partitions; current config uses {_accountRangePartitionCount}.");
                    return;
                }

                AccountRangeReadyForRequest.Clear();
                NextSlotRange.Clear();
                StoragesToRetrieve.Clear();
                CodesToRetrieve.Clear();
                AccountsToRefresh.Clear();

                int accountRangePartitionCount = reader.ReadInt32();
                for (int i = 0; i < accountRangePartitionCount; i++)
                {
                    ValueHash256 key = ReadValueHash256(reader);
                    if (!AccountRangePartitions.TryGetValue(key, out AccountRangePartition? partition))
                    {
                        throw new InvalidDataException($"Unknown account range partition {key}.");
                    }

                    partition.NextAccountPath = ReadValueHash256(reader);
                    partition.AccountPathStart = ReadValueHash256(reader);
                    partition.AccountPathLimit = ReadValueHash256(reader);
                    partition.MoreAccountsToRight = reader.ReadBoolean();
                }

                int readyPartitionCount = reader.ReadInt32();
                for (int i = 0; i < readyPartitionCount; i++)
                {
                    ValueHash256 key = ReadValueHash256(reader);
                    if (!AccountRangePartitions.TryGetValue(key, out AccountRangePartition? partition))
                    {
                        throw new InvalidDataException($"Unknown ready account range partition {key}.");
                    }

                    if (partition.MoreAccountsToRight)
                    {
                        AccountRangeReadyForRequest.Enqueue(partition);
                    }
                }

                foreach (StorageRange storageRange in ReadStorageRanges(reader))
                {
                    NextSlotRange.Enqueue(storageRange);
                }

                foreach (PathWithAccount pathWithAccount in ReadPathWithAccounts(reader))
                {
                    StoragesToRetrieve.Enqueue(pathWithAccount);
                }

                foreach (ValueHash256 codeHash in ReadValueHash256Array(reader))
                {
                    CodesToRetrieve.Enqueue(codeHash);
                }

                foreach (AccountWithStorageStartingHash account in ReadAccountsToRefresh(reader))
                {
                    AccountsToRefresh.Enqueue(account);
                }

                if (_logger.IsInfo) _logger.Info("Snap - State Ranges (Phase 1) progress restored from DB.");
            }
            catch (Exception exception)
            {
                if (_logger.IsWarn) _logger.Warn($"Ignoring invalid SnapRanges progress checkpoint. {exception}");

                AccountRangePartitions.Clear();
                AccountRangeReadyForRequest.Clear();
                NextSlotRange.Clear();
                StoragesToRetrieve.Clear();
                CodesToRetrieve.Clear();
                AccountsToRefresh.Clear();
                SetupAccountRangePartition();
            }
        }

        private static void WriteStorageRanges(BinaryWriter writer, StorageRange[] ranges)
        {
            writer.Write(ranges.Length);
            for (int i = 0; i < ranges.Length; i++)
            {
                WriteNullableLong(writer, ranges[i].BlockNumber);
                WriteNullableHash256(writer, ranges[i].RootHash);
                WritePathWithAccounts(writer, ranges[i].Accounts);
                WriteNullableValueHash256(writer, ranges[i].StartingHash);
                WriteNullableValueHash256(writer, ranges[i].LimitHash);
            }
        }

        private static StorageRange[] ReadStorageRanges(BinaryReader reader)
        {
            int count = reader.ReadInt32();
            StorageRange[] ranges = new StorageRange[count];
            for (int i = 0; i < count; i++)
            {
                ranges[i] = new StorageRange
                {
                    BlockNumber = ReadNullableLong(reader),
                    RootHash = ReadNullableHash256(reader),
                    Accounts = ReadPathWithAccounts(reader).ToPooledList(),
                    StartingHash = ReadNullableValueHash256(reader),
                    LimitHash = ReadNullableValueHash256(reader),
                };
            }

            return ranges;
        }

        private static void WritePathWithAccounts(BinaryWriter writer, IReadOnlyList<PathWithAccount> accounts)
        {
            writer.Write(accounts.Count);
            for (int i = 0; i < accounts.Count; i++)
            {
                WritePathWithAccount(writer, accounts[i]);
            }
        }

        private static PathWithAccount[] ReadPathWithAccounts(BinaryReader reader)
        {
            int count = reader.ReadInt32();
            PathWithAccount[] accounts = new PathWithAccount[count];
            for (int i = 0; i < count; i++)
            {
                accounts[i] = ReadPathWithAccount(reader);
            }

            return accounts;
        }

        private static void WriteAccountsToRefresh(BinaryWriter writer, AccountWithStorageStartingHash[] accounts)
        {
            writer.Write(accounts.Length);
            for (int i = 0; i < accounts.Length; i++)
            {
                WritePathWithAccount(writer, accounts[i].PathAndAccount);
                WriteValueHash256(writer, accounts[i].StorageStartingHash);
                WriteValueHash256(writer, accounts[i].StorageHashLimit);
            }
        }

        private static AccountWithStorageStartingHash[] ReadAccountsToRefresh(BinaryReader reader)
        {
            int count = reader.ReadInt32();
            AccountWithStorageStartingHash[] accounts = new AccountWithStorageStartingHash[count];
            for (int i = 0; i < count; i++)
            {
                accounts[i] = new AccountWithStorageStartingHash
                {
                    PathAndAccount = ReadPathWithAccount(reader),
                    StorageStartingHash = ReadValueHash256(reader),
                    StorageHashLimit = ReadValueHash256(reader),
                };
            }

            return accounts;
        }

        private static void WritePathWithAccount(BinaryWriter writer, PathWithAccount pathWithAccount)
        {
            WriteValueHash256(writer, pathWithAccount.Path);
            WriteBytes(writer, Rlp.Encode(pathWithAccount.Account).Bytes);
        }

        private static PathWithAccount ReadPathWithAccount(BinaryReader reader)
        {
            ValueHash256 path = ReadValueHash256(reader);
            Account? account = Rlp.Decode<Account>(ReadBytes(reader));
            return new PathWithAccount(path, account ?? Account.TotallyEmpty);
        }

        private static void WriteValueHash256Array(BinaryWriter writer, ValueHash256[] hashes)
        {
            writer.Write(hashes.Length);
            for (int i = 0; i < hashes.Length; i++)
            {
                WriteValueHash256(writer, hashes[i]);
            }
        }

        private static ValueHash256[] ReadValueHash256Array(BinaryReader reader)
        {
            int count = reader.ReadInt32();
            ValueHash256[] hashes = new ValueHash256[count];
            for (int i = 0; i < count; i++)
            {
                hashes[i] = ReadValueHash256(reader);
            }

            return hashes;
        }

        private static void WriteHash256(BinaryWriter writer, Hash256 hash) => writer.Write(hash.Bytes);

        private static Hash256 ReadHash256(BinaryReader reader) => new(ReadFixedBytes(reader, 32));

        private static void WriteNullableHash256(BinaryWriter writer, Hash256? hash)
        {
            writer.Write(hash is not null);
            if (hash is not null)
            {
                WriteHash256(writer, hash);
            }
        }

        private static Hash256? ReadNullableHash256(BinaryReader reader) =>
            reader.ReadBoolean() ? ReadHash256(reader) : null;

        private static void WriteValueHash256(BinaryWriter writer, ValueHash256 hash) => writer.Write(hash.Bytes);

        private static ValueHash256 ReadValueHash256(BinaryReader reader) => new(ReadFixedBytes(reader, 32));

        private static void WriteNullableValueHash256(BinaryWriter writer, ValueHash256? hash)
        {
            writer.Write(hash.HasValue);
            if (hash.HasValue)
            {
                WriteValueHash256(writer, hash.Value);
            }
        }

        private static ValueHash256? ReadNullableValueHash256(BinaryReader reader) =>
            reader.ReadBoolean() ? ReadValueHash256(reader) : null;

        private static void WriteNullableLong(BinaryWriter writer, long? value)
        {
            writer.Write(value.HasValue);
            if (value.HasValue)
            {
                writer.Write(value.Value);
            }
        }

        private static long? ReadNullableLong(BinaryReader reader) =>
            reader.ReadBoolean() ? reader.ReadInt64() : null;

        private static void WriteBytes(BinaryWriter writer, byte[] bytes)
        {
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }

        private static byte[] ReadBytes(BinaryReader reader)
        {
            int length = reader.ReadInt32();
            return ReadFixedBytes(reader, length);
        }

        private static byte[] ReadFixedBytes(BinaryReader reader, int length)
        {
            byte[] bytes = reader.ReadBytes(length);
            if (bytes.Length != length)
            {
                throw new EndOfStreamException();
            }

            return bytes;
        }

        public void TrackAccountToHeal(ValueHash256 path)
        {
            if (_logger.IsDebug) _logger.Debug($"Tracked {path} for healing");
            _pivot.UpdatedStorages.Add(path.ToCommitment());
        }

        private void LogRequest(string reqType)
        {
            if (_reqCount % 100 == 0 || _lastLogTime < DateTimeOffset.Now - _maxTimeBetweenLog)
            {
                int totalPathProgress = 0;
                foreach (KeyValuePair<ValueHash256, AccountRangePartition> kv in AccountRangePartitions)
                {
                    AccountRangePartition? partition = kv.Value;
                    int nextAccount = partition.NextAccountPath.Bytes[0] * 256 + partition.NextAccountPath.Bytes[1];
                    int startAccount = partition.AccountPathStart.Bytes[0] * 256 + partition.AccountPathStart.Bytes[1];
                    totalPathProgress += nextAccount - startAccount;
                }

                float progress = (float)(totalPathProgress / (double)(256 * 256));

                if (_logger.IsInfo)
                {
                    string stateRangesReport = $"Snap         State Ranges (Phase 1): ({progress,8:P2}) {Progress.GetMeter(progress, 1)}";
                    if (progress >= 0.995)
                    {
                        long queuedStorage = StoragesToRetrieve.Count;
                        long storagesToRetrieve = queuedStorage + _activeStorageRequests;
                        if (_estimatedStorageRemaining == null || storagesToRetrieve > _estimatedStorageRemaining)
                        {
                            _estimatedStorageRemaining = storagesToRetrieve;
                        }

                        if (storagesToRetrieve < 100)
                        {
                            // Not exactly accurate. It could be that total number of large storage is less than _largeStorageProgress.Count
                            // But that should be promptly resolved
                            _shouldStartLoggingLargeStorage = true;
                        }

                        if (storagesToRetrieve > 0 && !_shouldStartLoggingLargeStorage)
                        {
                            progress = _estimatedStorageRemaining != 0
                                ? (float)((_estimatedStorageRemaining - storagesToRetrieve) / (float)_estimatedStorageRemaining)
                                : 1;

                            stateRangesReport = $"Snap         Remaining storage: ({progress,8:P2}) {Progress.GetMeter(progress, 1)}";
                        }
                        else
                        {
                            double totalAllLargeStorageProgress = 0;
                            // totalLargeStorage changes over time, but thats fine.
                            long totalLargeStorage = queuedStorage;
                            foreach (KeyValuePair<ValueHash256, LargeProgressStatus> keyValuePair in _largeStorageProgress)
                            {
                                totalAllLargeStorageProgress += keyValuePair.Value.CalculateProgress();
                                totalLargeStorage++;
                            }

                            progress = totalLargeStorage != 0
                                ? (float)totalAllLargeStorageProgress / totalLargeStorage
                                : 1;

                            stateRangesReport = $"Snap         Large storage left: {totalLargeStorage} ({progress,8:P2}) {Progress.GetMeter(progress, 1)}";
                        }
                    }

                    if (_lastStateRangesReport != stateRangesReport || _lastLogTime < DateTimeOffset.Now - _maxTimeBetweenLog)
                    {
                        _logger.Info(stateRangesReport);
                        _lastStateRangesReport = stateRangesReport;
                        _lastLogTime = DateTimeOffset.Now;
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

            if (item.Accounts.Count == 1)
            {
                _largeStorageProgress.AddOrUpdate(item.Accounts[0].Path,
                    (key, progress) => new LargeProgressStatus().UpdateProgress(progress),
                    (key, progress, range) => progress.UpdateProgress(range),
                    item
                );
            }

            return true;
        }

        public void OnCompletedLargeStorage(PathWithAccount pathWithAccount)
        {
            if (_largeStorageProgress.TryGetValue(pathWithAccount.Path, out LargeProgressStatus progressStatus))
            {
                if (progressStatus.OnCompletedPartition())
                {
                    _largeStorageProgress.Remove(pathWithAccount.Path, out LargeProgressStatus value);
                }
            }
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
            PersistSnapRangesProgressIfSafe();
            while (NextSlotRange.TryDequeue(out StorageRange? range))
            {
                range?.Dispose();
            }
        }

        private class LargeProgressStatus()
        {
            private int _totalPartition = 0;
            private ConcurrentDictionary<ValueHash256, double> _partitionRemaining = new();

            internal LargeProgressStatus UpdateProgress(StorageRange item)
            {
                double start = 0.0;
                if (item.StartingHash is ValueHash256 startHash)
                {
                    start = BinaryPrimitives.ReadUInt32BigEndian(startHash.BytesAsSpan[..4]);
                    start /= UInt32.MaxValue;
                }

                double end = start;
                ValueHash256 limitHash = item.LimitHash ?? Keccak.MaxValue;
                end = BinaryPrimitives.ReadUInt32BigEndian(limitHash.BytesAsSpan[..4]);
                end /= UInt32.MaxValue;

                double progress = end - start;
                if (_partitionRemaining.TryAdd(limitHash, progress))
                {
                    Interlocked.Add(ref _totalPartition, 1);
                }
                _partitionRemaining.AddOrUpdate(limitHash, (k) => 0, (k, v) => progress);

                return this;
            }

            internal double CalculateProgress()
            {
                double total = 0;
                foreach (KeyValuePair<ValueHash256, double> keyValuePair in _partitionRemaining)
                {
                    total += keyValuePair.Value;
                }

                return 1.0 - total;
            }

            internal bool OnCompletedPartition() =>
                // Determine if this tracker could be removed
                Interlocked.Decrement(ref _totalPartition) == 0;
        }
    }
}
