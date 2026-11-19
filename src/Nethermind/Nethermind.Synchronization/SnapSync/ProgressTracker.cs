// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using ConcurrentCollections;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
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
        public const int CODES_BATCH_SIZE = 1_000;
        public const int HIGH_CODES_QUEUE_SIZE = CODES_BATCH_SIZE * 5;
        private const uint StorageRangeSplitFactor = 2;

        /// <remarks>
        /// This queue is served ahead of every other request type and a refresh answered with an expired root re-queues
        /// itself, so an unbounded priority starves everything below it until the pivot moves. Counted in a row rather
        /// than in flight: an in-flight bound does nothing at or below Sync.MaxProcessingThreads. The fallback below
        /// the priority chain still serves refreshes when they are the only work left.
        /// </remarks>
        internal const int MAX_CONSECUTIVE_ACCOUNT_REFRESHES = 4;

        // This does not need to be a lot as it spawn other requests. In fact 8 is probably too much. It is severely
        // bottlenecked by _syncCommit lock in SnapProviderHelper, which in turns is limited by the IO.
        // In any case, all partition will be touched when calculating progress, so we can't really put like 1024 for this.
        private readonly int _accountRangePartitionCount;

        public const long RangesProgressScale = 10_000;
        private const long AccountKeySpace = 1L << 32;

        private long _reqCount;
        private long _coveredAccountKeySpace;
        private int _activeAccountRequests;
        private int _activeStorageRequests;
        private readonly ConcurrentDictionary<ValueHash256, LargeProgressStatus> _largeStorageProgress = new();
        private long? _estimatedStorageRemaining = null;
        private bool _shouldStartLoggingLargeStorage = false;

        private int _activeCodeRequests;
        private int _activeAccRefreshRequests;

        /// <summary>Refreshes served in a row, see <see cref="MAX_CONSECUTIVE_ACCOUNT_REFRESHES"/>.</summary>
        private int _consecutiveAccountRefreshes;

        private readonly ILogger _logger;
        private readonly ISnapTrieFactory _snapTrieFactory;
        string? _lastStateRangesReport;
        private DateTimeOffset _lastLogTime = DateTimeOffset.MinValue;
        private readonly TimeSpan _maxTimeBetweenLog = TimeSpan.FromSeconds(5);

        // Partitions are indexed by its limit keccak/address as they are keep in the request struct and remain the same
        // throughout the sync. So its easy.
        private Dictionary<ValueHash256, AccountRangePartition> AccountRangePartitions { get; set; } = [];

        // Using a queue here to evenly distribute request across partitions. Don't want a situation where one really slow
        // partition is taking up most of the time at the end of the sync.
        private ConcurrentQueue<AccountRangePartition> AccountRangeReadyForRequest { get; set; } = new();
        private ConcurrentQueue<StorageRange> NextSlotRange { get; set; } = new();
        private ConcurrentQueue<StorageWork> StoragesToRetrieve { get; set; } = new();
        private ConcurrentQueue<ValueHash256> CodesToRetrieve { get; set; } = new();
        private ConcurrentQueue<AccountWithStorageStartingHash> AccountsToRefresh { get; set; } = new();
        private readonly Lock _accountRefreshLock = new();
        private bool _hasUnidentifiedRefreshCompletions;
        private readonly Dictionary<ValueHash256, RefreshWork> _accountRefreshes = [];
        private Func<PathWithAccount, (AccountStruct? Account, ValueHash256 Root)?>? _getAccountEvidence;
        private Action<PathWithAccount, AccountStruct?, ValueHash256>? _setAccountEvidence;

        internal Hash256? CurrentRoot => _pivot.GetPivotHeader()?.StateRoot;
        internal SnapRecoveryCoordinator Recovery { get; }
        internal bool HasPendingRootWork => !AccountRangeReadyForRequest.IsEmpty || !StoragesToRetrieve.IsEmpty ||
            !NextSlotRange.IsEmpty || !AccountsToRefresh.IsEmpty;

        internal void SetAccountEvidence(Func<PathWithAccount, (AccountStruct? Account, ValueHash256 Root)?> get,
            Action<PathWithAccount, AccountStruct?, ValueHash256> set)
        {
            _getAccountEvidence = get;
            _setAccountEvidence = set;
        }
        private readonly Dictionary<(ValueHash256 Account, ValueHash256 Start, ValueHash256 Limit), StorageRefreshWork> _storageRefreshRetries = [];
        private readonly Dictionary<StorageRange, StorageRetryBatch> _activeStorageRetryBatches = [];
        private readonly Dictionary<StorageRange, SnapRecoveryCoordinator.Cohort> _preparedCohorts = [];

        internal SnapRecoveryCoordinator.Cohort? GetPreparedCohort(StorageRange range)
        {
            lock (_accountRefreshLock) return _preparedCohorts.GetValueOrDefault(range);
        }

        internal void ReleasePreparedCohort(StorageRange range)
        {
            lock (_accountRefreshLock)
                if (_preparedCohorts.Remove(range, out SnapRecoveryCoordinator.Cohort cohort)) cohort.Complete();
        }

        private readonly record struct StorageWork(PathWithAccount Account, StorageRefreshWork? Retry = null);

        private sealed class StorageRetryBatch(int accountCount, List<(int Index, StorageRefreshWork Work)> retries)
        {
            public int AccountCount { get; } = accountCount;
            public List<(int Index, StorageRefreshWork Work)> Retries { get; } = retries;
        }

        private sealed class StorageRefreshWork(AccountWithStorageStartingHash request, StorageRange? range)
        {
            public AccountWithStorageStartingHash Request { get; } = request;
            public StorageRange? Range { get; } = range;
            public bool Active { get; set; }
            public bool RefreshRequested { get; set; }
            public bool StorageRequested { get; set; }
            public bool RefreshOnly { get; set; }
            public bool RefreshPaid { get; set; }
        }

        private enum RefreshRetry { None, Refresh, Storage }

        private sealed class RefreshWork(AccountWithStorageStartingHash request)
        {
            public AccountWithStorageStartingHash Request { get; } = request;
            public bool Active { get; set; }
            public RefreshRetry Retry { get; set; }
            public List<AccountWithStorageStartingHash> Waiting { get; } = [request];
            public (AccountStruct? Account, ValueHash256 Root)? Outcome { get; set; }
            public bool CohortOwned { get; set; }
        }

        internal sealed class ResponseAdmission(SnapRecoveryCoordinator recovery, SnapRecoveryCoordinator.Lease? lease,
            SnapRecoveryCoordinator.Cohort? cohort = null)
        {
            private int _remaining = SnapProvider.MaxQueuedEmptyStreakRefreshes;
            internal bool TryTake()
            {
                if (_remaining == 0 || !(lease is not null ? lease.TakeCohortSlot() || recovery.TryTakeCohortSlot()
                    : cohort?.Take() == true || recovery.TryTakeCohortSlot())) return false;
                _remaining--;
                return true;
            }
        }

        private static (ValueHash256, ValueHash256, ValueHash256) RefreshKey(AccountWithStorageStartingHash request) =>
            (request.PathAndAccount.Path, request.StorageStartingHash, request.StorageHashLimit);

        /// <summary>Served ahead of everything else, so a speculative caller has to keep it short - see
        /// <see cref="SnapProvider.MaxQueuedEmptyStreakRefreshes"/>.</summary>
        internal int AccountsToRefreshCount => AccountsToRefresh.Count;

        private readonly FastSync.IStateSyncPivot _pivot;
        private readonly bool _enableStorageRangeSplit;
        private readonly ulong _stateMinDistanceFromHead;

        private static readonly TimeSpan DefaultCheckpointInterval = TimeSpan.FromMinutes(5);

        /// <summary>
        /// How often the progress is saved for <see cref="ISyncConfig.SnapResume"/>, or <c>null</c> when it is off or the
        /// backend cannot resume.
        /// </summary>
        private readonly TimeSpan? _checkpointInterval;
        private long _lastCheckpointTimestamp = Stopwatch.GetTimestamp();

        /// <summary>The lowest requested pivot, used before any verified ranges contribute to the checkpoint.</summary>
        private (ulong Number, Hash256 Hash)? _anchor;
        private readonly ConcurrentHashSet<(ulong Number, Hash256 Hash)> _rangePivots = [];

        /// <summary>The requests handed out and not released yet, tracked while checkpoints are on.</summary>
        /// <remarks>A checkpoint counts them as still to do: whatever their responses write lands after it.</remarks>
        private readonly ConcurrentDictionary<SnapSyncBatch, (ulong Number, Hash256 Hash)> _requestsInFlight = new();

        /// <summary>Held shared by every response from its first write to its request's release, and exclusively by a checkpoint while it reads the progress.</summary>
        /// <remarks>
        /// A checkpoint then sees each request either still in flight with nothing of it recorded, or handled and
        /// released in full with its writes committed, never a response half handled.
        /// </remarks>
        private readonly ReaderWriterLockSlim _responseLock = new(LockRecursionPolicy.NoRecursion);
        private readonly IDisposable _endResponse;

        public ProgressTracker(ISnapTrieFactory snapTrieFactory, ISyncConfig syncConfig, FastSync.IStateSyncPivot pivot, ILogManager? logManager, TimeSpan? checkpointInterval = null)
            : this(snapTrieFactory, syncConfig, pivot, logManager, TimeProvider.System, checkpointInterval) { }

        internal ProgressTracker(ISnapTrieFactory snapTrieFactory, ISyncConfig syncConfig, FastSync.IStateSyncPivot pivot,
            ILogManager? logManager, TimeProvider timeProvider, TimeSpan? checkpointInterval = null)
        {
            _logger = logManager?.GetClassLogger<ProgressTracker>() ?? throw new ArgumentNullException(nameof(logManager));
            _snapTrieFactory = snapTrieFactory ?? throw new ArgumentNullException(nameof(snapTrieFactory));

            _pivot = pivot;
            Recovery = new(timeProvider, pivot);
            _checkpointInterval = syncConfig.SnapResume && snapTrieFactory.SupportsCheckpoints ? checkpointInterval ?? DefaultCheckpointInterval : null;
            _endResponse = new EndResponse(_responseLock);

            int accountRangePartitionCount = syncConfig.SnapSyncAccountRangePartitionCount;
            if (accountRangePartitionCount < 1)
                throw new ArgumentException($"Account range partition must be between 1 to {int.MaxValue}.");

            _accountRangePartitionCount = accountRangePartitionCount;
            _enableStorageRangeSplit = syncConfig.EnableSnapSyncStorageRangeSplit;
            _stateMinDistanceFromHead = syncConfig.StateMinDistanceFromHead;

            SetupAccountRangePartition();
        }

        private void SetupAccountRangePartition()
        {
            uint curStartingPath = 0;
            uint partitionSize = (uint)(AccountKeySpace / _accountRangePartitionCount);
            SetCoveredAccountKeySpace(0);

            for (int i = 0; i < _accountRangePartitionCount; i++)
            {
                AccountRangePartition partition = new();

                Hash256 startingPath = new(Keccak.Zero.Bytes);
                BinaryPrimitives.WriteUInt32BigEndian(startingPath.Bytes, curStartingPath);

                partition.NextAccountPath = startingPath;
                partition.AccountPathStart = startingPath;
                partition.KeySpaceWidth = i == _accountRangePartitionCount - 1 ? AccountKeySpace - curStartingPath : partitionSize;

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

        /// <summary>
        /// Moves the state sync pivot in response to a streak of unusable range responses, but only once the head has
        /// advanced at least <see cref="ISyncConfig.StateMinDistanceFromHead"/> blocks past it.
        /// </summary>
        /// <remarks>
        /// The forced move is for a pivot the peer set has pruned. It does not invalidate the ranges already in flight
        /// - each reply is checked against the root recorded in its own request - but it re-targets everything issued
        /// afterwards at the newest suggested header. A streak on a chain whose pivot is already at the head is not the
        /// case the move is for, and answering it by moving further ahead of the peers that caused it makes the next
        /// streak more likely: OP Mainnet ran 74,469 forced moves in 60 hours without finishing the account range
        /// (https://github.com/NethermindEth/nethermind/issues/13200).
        /// <para>
        /// The rate limit lives here rather than on the pivot because it is a property of this caller:
        /// <c>TreeSync.ResetStateRootToBestSuggested</c> needs the newest root on every round and must keep the
        /// unrestricted path. <see cref="FastSync.IStateSyncPivot.Diff"/> saturates, so a head behind the pivot reads 0
        /// and is suppressed rather than wrapping into a large distance. A declined move is invisible to the caller, as
        /// a declined <c>UpdateHeaderForcefully</c> always was; the metrics below are where it is observable.
        /// </para>
        /// </remarks>
        public void UpdatePivot()
        {
            if (_pivot.Diff < _stateMinDistanceFromHead)
            {
                Interlocked.Increment(ref Metrics.ForcedStatePivotUpdatesSuppressed);
                return;
            }

            Interlocked.Increment(ref Metrics.ForcedStatePivotUpdates);
            _pivot.UpdateHeaderForcefully();
        }

        public bool IsFinished(out SnapSyncBatch? nextBatch)
        {
            BlockHeader? pivotHeader = _pivot.GetPivotHeader();
            nextBatch = null;
            if (pivotHeader is null) return false;
            Interlocked.Increment(ref _reqCount);
            Hash256 rootHash = pivotHeader.StateRoot;
            ulong blockNumber = pivotHeader.Number;
            if (_anchor is not { } anchor || blockNumber < anchor.Number) _anchor = (blockNumber, pivotHeader.Hash!);
            if (_checkpointInterval is { } interval && Stopwatch.GetElapsedTime(_lastCheckpointTimestamp) >= interval && !IsSnapGetRangesFinished())
                SaveCheckpoint();
            if (Volatile.Read(ref _consecutiveAccountRefreshes) < MAX_CONSECUTIVE_ACCOUNT_REFRESHES)
                nextBatch = DequeAccountToRefresh(rootHash, out _);
            if (nextBatch is null && Recovery.CanPrepare(true, false) && ShouldRequestAccountRequests() && AccountRangeReadyForRequest.TryDequeue(out AccountRangePartition partition))
                nextBatch = CreateAccountRangeRequest(rootHash, partition, blockNumber);
            nextBatch ??= DequeStorageAndCodes(rootHash, blockNumber);
            if (nextBatch is null)
            {
                nextBatch = DequeAccountToRefresh(rootHash, out bool completedLocally);
                if (nextBatch is null && completedLocally) nextBatch = DequeStorageAndCodes(rootHash, blockNumber);
            }
            if (nextBatch is not null)
            {
                TakeTurn(nextBatch);
                if (_checkpointInterval is not null) _requestsInFlight.TryAdd(nextBatch, (blockNumber, pivotHeader.Hash!));
                return false;
            }
            bool finished = IsSnapGetRangesFinished();
            if (finished)
            {
                _logger.Info("Snap - State Ranges (Phase 1 of 2) finished.");
                _snapTrieFactory.MarkRangePhaseFinished();
                PublishCoveredAccountKeySpace(AccountKeySpace);
                if (_checkpointInterval is not null)
                {
                    _snapTrieFactory.SaveCheckpoint(null);
                    SaveCheckpoint();
                }
            }
            LogRequest(NO_REQUEST);
            return finished;
        }

        private SnapSyncBatch? DequeStorageAndCodes(Hash256 rootHash, ulong blockNumber)
        {
            if (TryDequeNextSlotRange(rootHash, out StorageRange slotRange))
                return CreateNextSlowRangeRequest(slotRange, rootHash, blockNumber);
            bool highStorage = StoragesToRetrieve.Count >= HIGH_STORAGE_QUEUE_SIZE;
            SnapSyncBatch? batch = highStorage ? DequeStorageToRetrieveRequest(rootHash, blockNumber) : null;
            if (batch is not null) return batch;
            if (CodesToRetrieve.Count >= HIGH_CODES_QUEUE_SIZE && Recovery.CanPrepare(false, false)) return DequeCodeRequest();
            if (!highStorage) batch = DequeStorageToRetrieveRequest(rootHash, blockNumber);
            return batch ?? (CodesToRetrieve.IsEmpty || !Recovery.CanPrepare(false, false) ? null : DequeCodeRequest());
        }

        /// <summary>Stops counting <paramref name="batch"/> as in flight, once its response has been handled or handed back.</summary>
        public void ReportRequestReleased(SnapSyncBatch batch, bool madeRangeProgress = false)
        {
            if (_requestsInFlight.TryRemove(batch, out (ulong Number, Hash256 Hash) pivot) && madeRangeProgress)
                _rangePivots.Add(pivot);
        }

        /// <summary>Marks a response as being handled until the returned scope is disposed, so that no checkpoint is taken meanwhile.</summary>
        /// <remarks>
        /// The scope has to cover the handling and the release of the request on the same thread, and must not be
        /// nested.
        /// </remarks>
        /// <returns>The scope, or <c>null</c> when no checkpoints are taken.</returns>
        public IDisposable? BeginResponse()
        {
            if (_checkpointInterval is null) return null;

            _responseLock.EnterReadLock();
            return _endResponse;
        }

        private sealed class EndResponse(ReaderWriterLockSlim responseLock) : IDisposable
        {
            public void Dispose() => responseLock.ExitReadLock();
        }

        /// <summary>
        /// Turn-taking, see <see cref="MAX_CONSECUTIVE_ACCOUNT_REFRESHES"/>. Saturating so a long run of refreshes
        /// cannot wrap the counter and hand the priority branch a free turn.
        /// </summary>
        private void TakeTurn(SnapSyncBatch? served)
        {
            if (served is null) return; // A queue that raced empty; nobody had a turn.

            if (served.AccountsToRefreshRequest is null)
            {
                Volatile.Write(ref _consecutiveAccountRefreshes, 0);
            }
            else if (Volatile.Read(ref _consecutiveAccountRefreshes) < MAX_CONSECUTIVE_ACCOUNT_REFRESHES)
            {
                Interlocked.Increment(ref _consecutiveAccountRefreshes);
            }
        }

        private SnapSyncBatch DequeCodeRequest()
        {
            Interlocked.Increment(ref _activeCodeRequests);

            ArrayPoolList<ValueHash256> codesToQuery = [with(CODES_BATCH_SIZE)];
            for (int i = 0; i < CODES_BATCH_SIZE && CodesToRetrieve.TryDequeue(out ValueHash256 codeHash); i++)
            {
                codesToQuery.Add(codeHash);
            }

            codesToQuery.AsSpan().Sort();

            LogRequest($"CodesToRetrieve:{codesToQuery.Count}");

            return new SnapSyncBatch { CodesRequest = codesToQuery };
        }

        private SnapSyncBatch? DequeStorageToRetrieveRequest(Hash256 rootHash, ulong blockNumber)
        {
            bool rootedEligible = Recovery.CanPrepare(true, false);
            if (!rootedEligible) return null;
            bool unknownEligible = Recovery.CanPrepare(true, true);

            Interlocked.Increment(ref _activeStorageRequests); // for race condition so that snap does not exit prematurely

            ArrayPoolList<PathWithAccount> storagesToQuery = [with(STORAGE_BATCH_SIZE)];
            StorageRange storageRange = new()
            {
                RootHash = rootHash,
                Accounts = storagesToQuery,
                StartingHash = ValueKeccak.Zero,
                BlockNumber = blockNumber
            };
            SnapRecoveryCoordinator.Cohort? cohort = null;

            lock (_accountRefreshLock)
            {
                bool? unknownBatch = null;
                List<(int Index, StorageRefreshWork Work)>? retries = null;
                int inspections = Math.Min(STORAGE_BATCH_SIZE, StoragesToRetrieve.Count);
                int unknownBatchLimit = Recovery.PeerPool?.InitializedPeers.Skip(1).Any() == true
                    ? SnapProvider.MaxQueuedEmptyStreakRefreshes / 4 : inspections;
                for (int i = 0; i < inspections && StoragesToRetrieve.TryDequeue(out StorageWork storage); i++)
                {
                    (AccountStruct? Account, ValueHash256 Root)? evidence = _getAccountEvidence?.Invoke(storage.Account);
                    bool unknown = evidence?.Root != rootHash;
                    if (unknown && !unknownEligible || unknownBatch is { } selectedUnknown && selectedUnknown != unknown)
                    {
                        StoragesToRetrieve.Enqueue(storage);
                        continue;
                    }
                    AccountWithStorageStartingHash waiting = storage.Retry?.Request ?? new() { PathAndAccount = storage.Account, StorageHashLimit = ValueKeccak.MaxValue };
                    if (ParkStorage(waiting, rootHash, storage.Retry is { RefreshOnly: true }, out bool rotate, evidence))
                    {
                        if (rotate) StoragesToRetrieve.Enqueue(storage);
                        else if (storage.Retry is not null) _storageRefreshRetries.Remove(RefreshKey(waiting));
                        continue;
                    }
                    int index = storagesToQuery.Count;
                    if (unknown && cohort is null)
                    {
                        cohort = Recovery.TryReserveCohort(Math.Min(inspections, unknownBatchLimit));
                        if (cohort is null) { StoragesToRetrieve.Enqueue(storage); continue; }
                    }
                    unknownBatch = unknown;
                    storagesToQuery.Add(storage.Account);
                    if (storage.Retry is { } retry)
                    {
                        retry.Active = true;
                        (retries ??= []).Add((index, retry));
                    }
                    if (unknown && storagesToQuery.Count >= unknownBatchLimit) break;
                }
                if (retries is not null)
                    _activeStorageRetryBatches.Add(storageRange, new(storagesToQuery.Count, retries));
                if (cohort is not null)
                {
                    HashSet<ValueHash256> paths = [];
                    foreach (PathWithAccount account in storagesToQuery) paths.Add(account.Path);
                    cohort.Trim(paths.Count);
                    _preparedCohorts.Add(storageRange, cohort);
                }
                Interlocked.Add(ref _activeStorageRequests, storagesToQuery.Count);
                Interlocked.Decrement(ref _activeStorageRequests);
            }

            if (storagesToQuery.Count == 0)
            {
                storageRange.Dispose();
                return null;
            }
            LogRequest($"StoragesToRetrieve:{storagesToQuery.Count}");

            return new SnapSyncBatch { StorageRangeRequest = storageRange, RecoveryCohort = cohort };
        }

        private SnapSyncBatch CreateNextSlowRangeRequest(StorageRange slotRange, Hash256 rootHash, ulong blockNumber)
        {
            slotRange.RootHash = rootHash;
            slotRange.BlockNumber = blockNumber;

            LogRequest($"NextSlotRange:{slotRange.Accounts.Count}");

            return new SnapSyncBatch { StorageRangeRequest = slotRange, RecoveryCohort = GetPreparedCohort(slotRange) };
        }

        private SnapSyncBatch CreateAccountRangeRequest(Hash256 rootHash, AccountRangePartition partition, ulong blockNumber)
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

        private SnapSyncBatch? DequeAccountToRefresh(Hash256 rootHash, out bool completedLocally)
        {
            completedLocally = false;
            if (!Recovery.CanPrepare(true, false)) return null;
            lock (_accountRefreshLock)
            {
                int inspections = Math.Min(AccountsToRefresh.Count, SnapProvider.MaxQueuedEmptyStreakRefreshes);
                for (int i = 0; i < inspections && AccountsToRefresh.TryDequeue(out AccountWithStorageStartingHash acc); i++)
                {
                    RefreshWork work = _accountRefreshes[acc.PathAndAccount.Path];
                    if (_getAccountEvidence?.Invoke(acc.PathAndAccount) is { } evidence && evidence.Root == rootHash)
                    {
                        work.Outcome = evidence;
                        completedLocally = true;
                        CompleteRefresh(work, true, rootHash);
                        continue;
                    }
                    work.Active = true;
                    Interlocked.Increment(ref _activeAccRefreshRequests);
                    LogRequest($"AccountsToRefresh: {AccountsToRefresh.Count}");
                    return new SnapSyncBatch
                    {
                        AccountsToRefreshRequest = new AccountsToRefreshRequest
                        {
                            RootHash = rootHash,
                            Paths = new ArrayPoolList<AccountWithStorageStartingHash>(1) { acc }
                        }
                    };
                }
            }
            return null;
        }

        private static void AttachWaiting(RefreshWork work, AccountWithStorageStartingHash request)
        {
            foreach (AccountWithStorageStartingHash waiting in work.Waiting)
                if (ReferenceEquals(waiting.PathAndAccount, request.PathAndAccount) && RefreshKey(waiting) == RefreshKey(request)) return;
            work.Waiting.Add(request);
        }

        private bool ParkStorage(AccountWithStorageStartingHash request, ValueHash256 root, bool refreshOnly, out bool rotate,
            (AccountStruct? Account, ValueHash256 Root)? capturedEvidence)
        {
            rotate = false;
            if (_accountRefreshes.TryGetValue(request.PathAndAccount.Path, out RefreshWork work))
            {
                AttachWaiting(work, request);
                return true;
            }
            if (capturedEvidence is { } evidence && evidence.Root == root)
            {
                if (evidence.Account is not { HasStorage: true })
                {
                    DropLargeStorageProgress(request.PathAndAccount);
                    return true;
                }
                return false;
            }
            if (!refreshOnly) return false;
            (ValueHash256, ValueHash256, ValueHash256) key = RefreshKey(request);
            _storageRefreshRetries.Remove(key, out StorageRefreshWork? transferred);
            if (!TryEnqueueAccountRefresh(request.PathAndAccount, request.StorageStartingHash, request.StorageHashLimit))
            {
                if (transferred is not null) _storageRefreshRetries.Add(key, transferred);
                rotate = true;
            }
            return true;
        }

        private bool ShouldRequestAccountRequests() => _activeAccountRequests < _accountRangePartitionCount
                   && NextSlotRange.Count < 10
                   && StoragesToRetrieve.Count < HIGH_STORAGE_QUEUE_SIZE
                   && CodesToRetrieve.Count < HIGH_CODES_QUEUE_SIZE;

        public void EnqueueCodeHashes(ReadOnlySpan<ValueHash256> codeHashes)
        {
            foreach (ValueHash256 hash in codeHashes)
            {
                EnqueueCodeHash(hash);
            }
        }

        public void EnqueueCodeHash(ValueHash256 hash)
        {
            CodesToRetrieve.Enqueue(hash);
            Recovery.NotifyWork();
        }

        public void ReportCodeRequestFinished(ReadOnlySpan<ValueHash256> codeHashes)
        {
            EnqueueCodeHashes(codeHashes);

            Interlocked.Decrement(ref _activeCodeRequests);
            Recovery.NotifyWork();
        }

        public void ReportAccountRefreshFinished(AccountsToRefreshRequest? accountsToRefreshRequest = null)
        {
            if (accountsToRefreshRequest is not null)
            {
                ReportAccountRefreshFinished(accountsToRefreshRequest, false);
                return;
            }

            Hash256? currentRoot = CurrentRoot;
            lock (_accountRefreshLock)
            {
                _hasUnidentifiedRefreshCompletions = true;
                CompleteUnidentifiedRefreshes(currentRoot);
                Interlocked.Decrement(ref _activeAccRefreshRequests);
            }
        }

        internal void ReportAccountRefreshFinished(AccountsToRefreshRequest request, bool responseHandled)
        {
            Hash256? currentRoot = CurrentRoot;
            lock (_accountRefreshLock)
            {
                foreach (AccountWithStorageStartingHash path in request.Paths.AsSpan())
                    if (_accountRefreshes.TryGetValue(path.PathAndAccount.Path, out RefreshWork work))
                        CompleteRefresh(work, responseHandled, currentRoot);
                CompleteUnidentifiedRefreshes(currentRoot);
                Interlocked.Decrement(ref _activeAccRefreshRequests);
            }
        }

        private void CompleteUnidentifiedRefreshes(Hash256? currentRoot)
        {
            if (!_hasUnidentifiedRefreshCompletions || _activeAccRefreshRequests != 1) return;
            RefreshWork[] active = new RefreshWork[_accountRefreshes.Count];
            _accountRefreshes.Values.CopyTo(active, 0);
            foreach (RefreshWork work in active)
                if (work.Active) CompleteRefresh(work, true, currentRoot);
            _hasUnidentifiedRefreshCompletions = false;
        }

        private void CompleteRefresh(RefreshWork work, bool responseHandled, Hash256? currentRoot)
        {
            work.Active = false;
            if (!responseHandled || work.Retry == RefreshRetry.Refresh || work.Outcome is { } stale && stale.Root != currentRoot)
            {
                work.Retry = RefreshRetry.None;
                AccountsToRefresh.Enqueue(work.Request);
                return;
            }

            _accountRefreshes.Remove(work.Request.PathAndAccount.Path);
            if (work.CohortOwned) Recovery.ReleaseCohortSlot();
            if (work.Outcome is { } outcome)
            {
                foreach (AccountWithStorageStartingHash waiting in work.Waiting)
                    _setAccountEvidence?.Invoke(waiting.PathAndAccount, outcome.Account, outcome.Root);
                foreach (AccountWithStorageStartingHash waiting in work.Waiting)
                {
                    if (outcome.Account is { HasStorage: true }) EnqueueStorageRefreshRetry(waiting);
                    else DropLargeStorageProgress(waiting.PathAndAccount);
                }
            }
            else if (work.Retry == RefreshRetry.Storage)
                foreach (AccountWithStorageStartingHash waiting in work.Waiting) EnqueueStorageRefreshRetry(waiting);
        }

        internal void RecordRefreshedAccount(AccountWithStorageStartingHash request, AccountStruct? account, ValueHash256 root)
        {
            lock (_accountRefreshLock)
            {
                _setAccountEvidence?.Invoke(request.PathAndAccount, account, root);
                if (_accountRefreshes.TryGetValue(request.PathAndAccount.Path, out RefreshWork work)) work.Outcome = (account, root);
            }
        }

        internal void RetryAccountRefresh(AccountWithStorageStartingHash request, bool storage)
        {
            lock (_accountRefreshLock)
            {
                if (_accountRefreshes.TryGetValue(request.PathAndAccount.Path, out RefreshWork work) && work.Active)
                {
                    work.Retry = storage ? RefreshRetry.Storage : RefreshRetry.Refresh;
                    return;
                }
                if (storage) EnqueueStorageRefreshRetry(request);
                else EnqueueAccountRefresh(request.PathAndAccount, request.StorageStartingHash, request.StorageHashLimit);
            }
        }

        internal void EnqueueAccountStorages(ReadOnlySpan<PathWithAccount> accounts)
        {
            lock (_accountRefreshLock)
                foreach (PathWithAccount account in accounts) EnqueueAccountStorageLocked(account);
        }

        public void EnqueueAccountStorage(PathWithAccount pwa)
        {
            lock (_accountRefreshLock) EnqueueAccountStorageLocked(pwa);
        }

        private void EnqueueAccountStorageLocked(PathWithAccount pwa)
        {
            if (DeferStorageRetry(pwa.Path, ValueKeccak.Zero, ValueKeccak.MaxValue)) return;
            StoragesToRetrieve.Enqueue(new(pwa));
            Recovery.NotifyWork();
        }

        private bool DeferStorageRetry(ValueHash256 account, ValueHash256 start, ValueHash256 limit)
        {
            if (!_storageRefreshRetries.TryGetValue((account, start, limit), out StorageRefreshWork work)) return false;
            if (work.Active) work.StorageRequested = true;
            return true;
        }

        public void EnqueueAccountRefresh(PathWithAccount pathWithAccount, in ValueHash256? startingHash, in ValueHash256? hashLimit)
        {
            lock (_accountRefreshLock)
            {
                if (TryEnqueueAccountRefresh(pathWithAccount, startingHash, hashLimit)) return;
                EnqueueStorageRefreshRetry(new AccountWithStorageStartingHash
                {
                    PathAndAccount = pathWithAccount,
                    StorageStartingHash = startingHash.GetValueOrDefault(),
                    StorageHashLimit = hashLimit ?? ValueKeccak.MaxValue
                }, refreshOnly: true);
            }
        }

        private void EnqueueStorageRefreshRetry(AccountWithStorageStartingHash request, bool refreshOnly = false)
        {
            if (_storageRefreshRetries.TryGetValue(RefreshKey(request), out StorageRefreshWork existing))
            {
                existing.RefreshOnly |= refreshOnly;
                return;
            }
            if (request.StorageStartingHash == ValueKeccak.Zero && request.StorageHashLimit == ValueKeccak.MaxValue)
            {
                StorageRefreshWork work = new(request, null) { RefreshOnly = refreshOnly };
                _storageRefreshRetries.Add(RefreshKey(request), work);
                StoragesToRetrieve.Enqueue(new(request.PathAndAccount, work));
                return;
            }
            StorageRange range = new()
            {
                Accounts = new ArrayPoolList<PathWithAccount>(1) { request.PathAndAccount },
                StartingHash = request.StorageStartingHash,
                LimitHash = request.StorageHashLimit
            };
            _storageRefreshRetries.Add(RefreshKey(request), new StorageRefreshWork(request, range) { RefreshOnly = refreshOnly });
            NextSlotRange.Enqueue(range);
        }

        internal bool TryEnqueueAccountRefresh(PathWithAccount pathWithAccount, in ValueHash256? startingHash, in ValueHash256? hashLimit)
            => TryEnqueueAccountRefresh(pathWithAccount, startingHash, hashLimit, null);

        internal bool TryEnqueueAccountRefresh(PathWithAccount pathWithAccount, in ValueHash256? startingHash, in ValueHash256? hashLimit,
            ResponseAdmission? admission)
        {
            ValueHash256 start = startingHash.GetValueOrDefault();
            ValueHash256 limit = hashLimit ?? ValueKeccak.MaxValue;
            lock (_accountRefreshLock)
            {
                (ValueHash256, ValueHash256, ValueHash256) key = (pathWithAccount.Path, start, limit);
                if (_accountRefreshes.TryGetValue(pathWithAccount.Path, out RefreshWork existing))
                {
                    AttachWaiting(existing, new() { PathAndAccount = pathWithAccount, StorageStartingHash = start, StorageHashLimit = limit });
                    return true;
                }
                if (_storageRefreshRetries.TryGetValue(key, out StorageRefreshWork storage))
                {
                    if (storage.Active)
                    {
                        if (storage.RefreshRequested) return true;
                        if (admission is not null && !admission.TryTake()) return false;
                        storage.RefreshRequested = true;
                        storage.RefreshPaid = admission is not null;
                        return true;
                    }
                    return admission is null;
                }
                if (_accountRefreshes.Count >= SnapProvider.MaxQueuedEmptyStreakRefreshes) return false;
                if (admission is not null && !admission.TryTake()) return false;
                AccountWithStorageStartingHash request = new()
                {
                    PathAndAccount = pathWithAccount,
                    StorageStartingHash = start,
                    StorageHashLimit = limit
                };
                _accountRefreshes.Add(pathWithAccount.Path, new RefreshWork(request) { CohortOwned = admission is not null });
                AccountsToRefresh.Enqueue(request);
                Recovery.NotifyWork();
            }
            TrackAccountToHeal(pathWithAccount.Path);
            return true;
        }

        internal bool CompleteStorageRefreshRetry(StorageRange range, bool responseHandled)
        {
            lock (_accountRefreshLock)
            {
                if (_activeStorageRetryBatches.Remove(range, out StorageRetryBatch batch))
                {
                    // SnapProvider trims committed accounts when a later account fails locally.
                    int completedPrefix = batch.AccountCount - range.Accounts.Count;
                    int remainingOwned = 0;
                    foreach ((int index, StorageRefreshWork owned) in batch.Retries)
                    {
                        bool completed = index < completedPrefix;
                        if (!completed) remainingOwned++;
                        CompleteStorageRefresh(owned, completed || responseHandled);
                    }
                    return remainingOwned == range.Accounts.Count;
                }
                if (range.Accounts.Count != 1) return false;
                PathWithAccount account = range.Accounts[0];
                (ValueHash256, ValueHash256, ValueHash256) key =
                    (account.Path, range.StartingHash.GetValueOrDefault(), range.LimitHash ?? ValueKeccak.MaxValue);
                if (!_storageRefreshRetries.TryGetValue(key, out StorageRefreshWork work) ||
                    !ReferenceEquals(work.Range, range)) return false;
                CompleteStorageRefresh(work, responseHandled);
                return true;
            }
        }

        private void CompleteStorageRefresh(StorageRefreshWork work, bool responseHandled)
        {
            AccountWithStorageStartingHash request = work.Request;
            _storageRefreshRetries.Remove(RefreshKey(request));
            if (responseHandled && work.RefreshRequested)
            {
                if (_accountRefreshes.TryGetValue(request.PathAndAccount.Path, out RefreshWork existing))
                {
                    AttachWaiting(existing, request);
                    if (work.RefreshPaid) Recovery.ReleaseCohortSlot();
                }
                else
                {
                    _accountRefreshes.Add(request.PathAndAccount.Path, new RefreshWork(request) { CohortOwned = work.RefreshPaid });
                    AccountsToRefresh.Enqueue(request);
                }
            }
            else if (!responseHandled || work.StorageRequested)
            {
                if (work.RefreshPaid) Recovery.ReleaseCohortSlot();
                EnqueueStorageRefreshRetry(request, work.RefreshOnly);
            }
            else if (work.RefreshPaid) Recovery.ReleaseCohortSlot();
        }

        public void ReportStorageRequestFinished(int accountCount) =>
            Interlocked.Add(ref _activeStorageRequests, -accountCount);

        public void EnqueueNextSlot(StorageRange? storageRange)
        {
            if (storageRange is null) return;
            bool dispose = false;
            lock (_accountRefreshLock)
            {
                if (storageRange.StartingHash.GetValueOrDefault() == ValueKeccak.Zero &&
                    (storageRange.LimitHash ?? ValueKeccak.MaxValue) == ValueKeccak.MaxValue)
                {
                    foreach (PathWithAccount account in storageRange.Accounts) EnqueueAccountStorage(account);
                    dispose = true;
                }
                else if (storageRange.Accounts.Count == 1 && DeferStorageRetry(storageRange.Accounts[0].Path,
                    storageRange.StartingHash.GetValueOrDefault(), storageRange.LimitHash ?? ValueKeccak.MaxValue))
                {
                    dispose = true;
                }
                else NextSlotRange.Enqueue(storageRange);
            }
            if (dispose) storageRange.Dispose();
        }

        /// <param name="runRoot">The storage root the range ending at <paramref name="lastProcessedHash"/> was verified against.</param>
        /// <param name="runStart">Where the consecutive ranges of the storage downloaded at <paramref name="runRoot"/> begin.</param>
        public void EnqueueNextSlot(StorageRange parentRequest, int accountIndex, ValueHash256 lastProcessedHash, int slotCount, in ValueHash256 runRoot, in ValueHash256 runStart)
        {
            ValueHash256 limitHash = accountIndex == 0 ? parentRequest.LimitHash ?? Keccak.MaxValue : ValueKeccak.MaxValue;
            if (lastProcessedHash > limitHash)
                return;

            ValueHash256? startingHash = accountIndex == 0 ? parentRequest.StartingHash : null;
            PathWithAccount account = parentRequest.Accounts.AsSpan()[accountIndex];
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
                    LimitHash = halfOfLeftHash,
                    PrevRunRoot = runRoot,
                    PrevRunStart = runStart
                });

                // Continues no range: the half before it may download at another root.
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
                    LimitHash = limitHash,
                    PrevRunRoot = runRoot,
                    PrevRunStart = runStart
                };
                NextSlotRange.Enqueue(storageRange);
            }
        }

        public void RequeueStorageRange(StorageRange storageRange)
        {
            // A single account is a continuation, so it keeps its starting hash and limit.
            if (storageRange.Accounts.Count == 1)
            {
                EnqueueNextSlot(storageRange);
                return;
            }

            for (int i = 0; i < storageRange.Accounts.Count; i++)
            {
                PathWithAccount account = storageRange.Accounts[i];
                if (i == 0 && (storageRange.StartingHash.GetValueOrDefault() > ValueKeccak.Zero ||
                    (storageRange.LimitHash ?? ValueKeccak.MaxValue) < ValueKeccak.MaxValue))
                    EnqueueNextSlot(new StorageRange
                    {
                        Accounts = new ArrayPoolList<PathWithAccount>(1) { account },
                        StartingHash = storageRange.StartingHash,
                        LimitHash = storageRange.LimitHash
                    });
                else EnqueueAccountStorage(account);
            }

            storageRange.Dispose();
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

        internal ValueHash256 GetAccountPartitionStart(in ValueHash256 hashLimit) => AccountRangePartitions[hashLimit].AccountPathStart;

        public void UpdateAccountRangePartitionProgress(in ValueHash256 hashLimit, in ValueHash256 nextPath, bool moreChildrenToRight)
        {
            AccountRangePartition partition = AccountRangePartitions[hashLimit];

            partition.NextAccountPath = nextPath;
            partition.MoreAccountsToRight = moreChildrenToRight && nextPath < hashLimit;

            long covered = GetCoveredKeySpace(partition);
            long delta = covered - partition.CoveredKeySpace;
            partition.CoveredKeySpace = covered;
            // Storage, code and refresh requests can run after the last account range; only the range phase end publishes the full scale.
            PublishCoveredAccountKeySpace(Math.Min(Interlocked.Add(ref _coveredAccountKeySpace, delta), AccountKeySpace - 1));
        }

        private static long GetCoveredKeySpace(AccountRangePartition partition) =>
            partition.MoreAccountsToRight
                ? Math.Clamp((long)BinaryPrimitives.ReadUInt32BigEndian(partition.NextAccountPath.Bytes) - BinaryPrimitives.ReadUInt32BigEndian(partition.AccountPathStart.Bytes), 0, partition.KeySpaceWidth)
                : partition.KeySpaceWidth;

        private void SetCoveredAccountKeySpace(long covered)
        {
            Volatile.Write(ref _coveredAccountKeySpace, covered);
            PublishCoveredAccountKeySpace(covered);
        }

        private static void PublishCoveredAccountKeySpace(long covered) =>
            Metrics.SnapRangesProgress = covered * RangesProgressScale / AccountKeySpace;

        public bool IsSnapGetRangesFinished()
        {
            lock (_accountRefreshLock)
            {
                return AccountRangeReadyForRequest.IsEmpty
                   && StoragesToRetrieve.IsEmpty
                   && NextSlotRange.IsEmpty
                   && CodesToRetrieve.IsEmpty
                   && AccountsToRefresh.IsEmpty
                   && _accountRefreshes.Count == 0
                   && _storageRefreshRetries.Count == 0
                   && _activeAccountRequests == 0
                   && _activeStorageRequests == 0
                   && _activeCodeRequests == 0
                   && _activeAccRefreshRequests == 0;
            }
        }

        public void Reset()
        {
            AccountRangeReadyForRequest.Clear();
            SetupAccountRangePartition();
            _pivot.UpdatedStorages.Clear();

            _largeStorageProgress.Clear();
            _estimatedStorageRemaining = null;
            _shouldStartLoggingLargeStorage = false;
            _anchor = null;
            _rangePivots.Clear();
            _requestsInFlight.Clear();
        }

        public void LoadProgress()
        {
            _lastCheckpointTimestamp = Stopwatch.GetTimestamp();
            if (_checkpointInterval is not null && _snapTrieFactory.LoadCheckpoint() is { } checkpoint)
            {
                Restore(checkpoint);
                return;
            }

            if (!_snapTrieFactory.IsRangePhaseFinished()) return;

            _logger.Info($"Snap - State Ranges (Phase 1 of 2) is finished.");
            foreach (KeyValuePair<ValueHash256, AccountRangePartition> partition in AccountRangePartitions)
            {
                partition.Value.MoreAccountsToRight = false;
                partition.Value.CoveredKeySpace = partition.Value.KeySpaceWidth;
            }
            SetCoveredAccountKeySpace(AccountKeySpace);
            AccountRangeReadyForRequest.Clear();
        }

        /// <summary>Saves a checkpoint of the progress made up to a shutdown, so that a restart after it loses nothing.</summary>
        /// <remarks>To be called once the dispatcher has stopped; the requests it dropped unanswered are counted as still to do.</remarks>
        public void SaveCheckpointAtShutdown()
        {
            // Nothing was requested, so there is nothing to keep.
            if (_checkpointInterval is null || _anchor is null) return;

            SaveCheckpoint(atShutdown: true);
        }

        /// <remarks>
        /// A failure is logged rather than thrown: thrown while syncing, it would come back on every request until the
        /// save worked, holding back every request meanwhile, and thrown at a shutdown it would read as a failed sync.
        /// The next attempt waits a full interval either way, and a restart resumes from the last checkpoint saved.
        /// </remarks>
        private void SaveCheckpoint(bool atShutdown = false)
        {
            long startTime = Stopwatch.GetTimestamp();
            try
            {
                SnapCheckpoint checkpoint;
                _responseLock.EnterWriteLock();
                try
                {
                    checkpoint = CreateCheckpoint();
                    // Cleanup may delete data covered by the snapshot. Publish it before a response can invalidate it.
                    _snapTrieFactory.SaveCheckpoint(checkpoint);
                }
                finally
                {
                    _responseLock.ExitWriteLock();
                }

                if (_logger.IsInfo) _logger.Info($"Snap - saved a resume checkpoint{(atShutdown ? " at shutdown" : "")} in {Stopwatch.GetElapsedTime(startTime).TotalSeconds:N1}s ({checkpoint.StoragesToRetrieve.Length + checkpoint.StorageRanges.Length} storage requests queued).");
            }
            catch (Exception e)
            {
                if (_logger.IsWarn) _logger.Warn($"Snap - could not save a resume checkpoint{(atShutdown ? " at shutdown" : "")}, a restart resumes from the last one: {e}");
            }
            finally
            {
                _lastCheckpointTimestamp = Stopwatch.GetTimestamp();
            }
        }

        private SnapCheckpoint CreateCheckpoint()
        {
            AccountRangeProgress[] partitions = new AccountRangeProgress[AccountRangePartitions.Count];
            int index = 0;
            foreach (AccountRangePartition partition in AccountRangePartitions.Values)
            {
                partitions[index++] = new AccountRangeProgress(partition.NextAccountPath, partition.AccountPathLimit, partition.MoreAccountsToRight);
            }

            List<StorageRangeProgress> storageRanges = [];
            List<AccountWithStorageStartingHash> accountsToRefresh = [.. AccountsToRefresh];
            List<ValueHash256> codesToRetrieve = [.. CodesToRetrieve];

            // A refresh deferred past the refresh cap waits as a storage retry marked refresh-only. Saved as a storage
            // range it would restore as one and lose that mark, so it is saved as a refresh and its range left out.
            HashSet<(ValueHash256, ValueHash256, ValueHash256)> deferredRefreshes = [];
            lock (_accountRefreshLock)
            {
                foreach (StorageRefreshWork work in _storageRefreshRetries.Values)
                {
                    if (!work.RefreshOnly) continue;
                    deferredRefreshes.Add(RefreshKey(work.Request));
                    accountsToRefresh.Add(work.Request);
                }
            }

            foreach (StorageRange range in NextSlotRange) AddStorageRange(range);

            // An account range in flight needs nothing here: its partition records where it starts either way.
            foreach ((SnapSyncBatch batch, _) in _requestsInFlight)
            {
                if (batch.StorageRangeRequest is { } storageRange) AddStorageRange(storageRange);
                if (batch.CodesRequest is { } codes) codesToRetrieve.AddRange(codes.AsSpan());
                if (batch.AccountsToRefreshRequest is { } refresh) accountsToRefresh.AddRange(refresh.Paths.AsSpan());
            }

            // Ranges parked behind a refresh of the same account live only in its registry entry: keep them too, or a
            // second shutdown drops every range but the one queued or in flight.
            HashSet<(ValueHash256, ValueHash256, ValueHash256)> savedRefreshes = [.. accountsToRefresh.Select(RefreshKey)];
            lock (_accountRefreshLock)
            {
                foreach (RefreshWork work in _accountRefreshes.Values)
                {
                    foreach (AccountWithStorageStartingHash waiting in work.Waiting)
                    {
                        if (savedRefreshes.Add(RefreshKey(waiting))) accountsToRefresh.Add(waiting);
                    }
                }
            }

            (ulong Number, Hash256 Hash)[] rangePivots = [.. _rangePivots];
            (ulong anchorNumber, Hash256 anchorHash) = rangePivots.Length == 0
                ? _anchor!.Value
                : rangePivots.MinBy(static pivot => pivot.Number);
            return new SnapCheckpoint
            {
                AnchorNumber = anchorNumber,
                AnchorHash = anchorHash,
                RangePivots = rangePivots,
                Partitions = partitions,
                StoragesToRetrieve = StoragesToRetrieve.Where(static work => work.Retry is not { RefreshOnly: true }).Select(static work => work.Account).ToArray(),
                StorageRanges = [.. storageRanges],
                AccountsToRefresh = [.. accountsToRefresh],
                CodesToRetrieve = [.. codesToRetrieve],
                UpdatedStorages = [.. _pivot.UpdatedStorages],
            };

            void AddStorageRange(StorageRange range)
            {
                ValueHash256 start = range.StartingHash.GetValueOrDefault();
                ValueHash256 limit = range.LimitHash ?? ValueKeccak.MaxValue;
                PathWithAccount[] accounts = deferredRefreshes.Count == 0
                    ? range.Accounts.AsSpan().ToArray()
                    : [.. range.Accounts.Where(account => !deferredRefreshes.Contains((account.Path, start, limit)))];
                if (accounts.Length > 0) storageRanges.Add(new(accounts, range.StartingHash, range.LimitHash));
            }
        }

        private void Restore(SnapCheckpoint checkpoint)
        {
            if (checkpoint.Partitions.Length != AccountRangePartitions.Count)
                throw new InvalidOperationException($"The snap checkpoint has {checkpoint.Partitions.Length} account range partitions, but this run has {AccountRangePartitions.Count}.");

            AccountRangeReadyForRequest.Clear();
            int accountsLeft = 0;
            long covered = 0;
            foreach (AccountRangeProgress progress in checkpoint.Partitions)
            {
                AccountRangePartition partition = AccountRangePartitions[progress.AccountPathLimit];
                partition.NextAccountPath = progress.NextAccountPath;
                partition.MoreAccountsToRight = progress.MoreAccountsToRight;
                partition.CoveredKeySpace = GetCoveredKeySpace(partition);
                covered += partition.CoveredKeySpace;
                if (partition.MoreAccountsToRight)
                {
                    AccountRangeReadyForRequest.Enqueue(partition);
                    accountsLeft++;
                }
            }

            Volatile.Write(ref _coveredAccountKeySpace, covered);
            PublishCoveredAccountKeySpace(Math.Min(covered, AccountKeySpace - 1));

            foreach (PathWithAccount account in checkpoint.StoragesToRetrieve) StoragesToRetrieve.Enqueue(new(account));

            foreach (StorageRangeProgress range in checkpoint.StorageRanges)
            {
                // A batch of accounts dropped in flight at a shutdown goes back to the batching queue: the slot range
                // queue counts one request per range, but a released request returns one per account.
                RequeueStorageRange(new StorageRange
                {
                    Accounts = new ArrayPoolList<PathWithAccount>(range.Accounts.Length, range.Accounts),
                    StartingHash = range.StartingHash,
                    LimitHash = range.LimitHash,
                });
            }

            // Every queued refresh needs its registry entry: the refresh queue is drained through it.
            foreach (AccountWithStorageStartingHash refresh in checkpoint.AccountsToRefresh)
                EnqueueAccountRefresh(refresh.PathAndAccount, refresh.StorageStartingHash, refresh.StorageHashLimit);
            foreach (ValueHash256 code in checkpoint.CodesToRetrieve) CodesToRetrieve.Enqueue(code);
            foreach (Hash256 address in checkpoint.UpdatedStorages) _pivot.UpdatedStorages.Add(address);
            _anchor = (checkpoint.AnchorNumber, checkpoint.AnchorHash);
            foreach ((ulong Number, Hash256 Hash) pivot in checkpoint.RangePivots) _rangePivots.Add(pivot);

            if (_logger.IsInfo) _logger.Info($"Snap - resuming the state ranges from a checkpoint: {accountsLeft} of {checkpoint.Partitions.Length} account partitions and {checkpoint.StoragesToRetrieve.Length + checkpoint.StorageRanges.Length} storage requests left.");
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
                float progress = (float)(Volatile.Read(ref _coveredAccountKeySpace) / (double)AccountKeySpace);

                if (_logger.IsInfo)
                {
                    string stateRangesReport = $"Snap         State Ranges (Phase 1 of 2): ({progress,8:P2}) {Progress.GetMeter(progress, 1)}";
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

        private bool TryDequeNextSlotRange(ValueHash256 root, out StorageRange item)
        {
            if (!Recovery.CanPrepare(true, false))
            {
                item = null!;
                return false;
            }
            bool unknownEligible = Recovery.CanPrepare(true, true);
            Interlocked.Increment(ref _activeStorageRequests);
            List<StorageRange> disposed = [];
            StorageRange? selected = null;
            lock (_accountRefreshLock)
            {
                int inspections = Math.Min(NextSlotRange.Count, STORAGE_BATCH_SIZE);
                for (int i = 0; i < inspections && NextSlotRange.TryDequeue(out StorageRange range); i++)
                {
                    (AccountStruct? Account, ValueHash256 Root)? evidence = _getAccountEvidence?.Invoke(range.Accounts[0]);
                    bool unknown = evidence?.Root != root;
                    if (unknown && !unknownEligible)
                    {
                        NextSlotRange.Enqueue(range);
                        continue;
                    }
                    AccountWithStorageStartingHash waiting = new()
                    {
                        PathAndAccount = range.Accounts[0],
                        StorageStartingHash = range.StartingHash.GetValueOrDefault(),
                        StorageHashLimit = range.LimitHash ?? ValueKeccak.MaxValue
                    };
                    bool retryOwned = _storageRefreshRetries.TryGetValue(RefreshKey(waiting), out StorageRefreshWork work) && ReferenceEquals(work.Range, range);
                    if (ParkStorage(waiting, root, retryOwned && work.RefreshOnly, out bool rotate, evidence))
                    {
                        if (rotate) NextSlotRange.Enqueue(range);
                        else
                        {
                            if (retryOwned) _storageRefreshRetries.Remove(RefreshKey(waiting));
                            disposed.Add(range);
                        }
                        continue;
                    }
                    selected = range;
                    if (unknown)
                    {
                        SnapRecoveryCoordinator.Cohort? cohort = Recovery.TryReserveCohort(range.Accounts.Count);
                        if (cohort is null) { selected = null; NextSlotRange.Enqueue(range); continue; }
                        _preparedCohorts.Add(range, cohort);
                    }
                    item = range;
                    if (retryOwned) work.Active = true;
                    _largeStorageProgress.AddOrUpdate(item.Accounts[0].Path,
                        (key, progress) => new LargeProgressStatus().UpdateProgress(progress),
                        (key, progress, owned) => progress.UpdateProgress(owned), item);
                    break;
                }
            }
            foreach (StorageRange range in disposed) range.Dispose();
            if (selected is not null)
            {
                item = selected;
                return true;
            }
            Interlocked.Decrement(ref _activeStorageRequests);
            item = null!;
            return false;
        }

        internal int LargeStorageProgressCount => _largeStorageProgress.Count;

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

        /// <summary>
        /// Drops the account's large-storage tracking whatever its partition count, for a caller that has established
        /// the account will not be fetched as a large storage any more.
        /// </summary>
        /// <remarks>
        /// <see cref="OnCompletedLargeStorage"/> retires one partition, so under
        /// <c>Sync.EnableSnapSyncStorageRangeSplit</c> a split account survives it. A re-download re-registers through
        /// <see cref="TryDequeNextSlotRange"/>.
        /// </remarks>
        public void DropLargeStorageProgress(PathWithAccount pathWithAccount) =>
            _largeStorageProgress.Remove(pathWithAccount.Path, out _);

        // A partition of the top level account range starting from `AccountPathStart` to `AccountPathLimit` (exclusive).
        private class AccountRangePartition
        {
            public ValueHash256 NextAccountPath { get; set; } = ValueKeccak.Zero;
            public ValueHash256 AccountPathStart { get; set; } = ValueKeccak.Zero; // Not really needed, but useful
            public ValueHash256 AccountPathLimit { get; set; } = ValueKeccak.MaxValue;
            public bool MoreAccountsToRight { get; set; } = true;
            public long KeySpaceWidth { get; set; }
            public long CoveredKeySpace { get; set; }
        }

        public void Dispose()
        {
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
