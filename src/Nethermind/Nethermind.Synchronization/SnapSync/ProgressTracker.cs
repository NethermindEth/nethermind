// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
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
        private ConcurrentQueue<PathWithAccount> StoragesToRetrieve { get; set; } = new();
        private ConcurrentQueue<ValueHash256> CodesToRetrieve { get; set; } = new();
        private ConcurrentQueue<AccountWithStorageStartingHash> AccountsToRefresh { get; set; } = new();

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

        /// <summary>The lowest pivot a request was issued at, see <see cref="SnapCheckpoint.AnchorNumber"/>.</summary>
        private (ulong Number, Hash256 Hash)? _anchor;

        /// <summary>The requests handed out and not released yet, tracked while checkpoints are on.</summary>
        /// <remarks>A checkpoint counts them as still to do: whatever their responses write lands after it.</remarks>
        private readonly ConcurrentDictionary<SnapSyncBatch, byte> _requestsInFlight = new();

        /// <summary>Held shared by every response from its first write to its request's release, and exclusively by a checkpoint while it reads the progress.</summary>
        /// <remarks>
        /// A checkpoint then sees each request either still in flight with nothing of it recorded, or handled and
        /// released in full with its writes committed, never a response half handled.
        /// </remarks>
        private readonly ReaderWriterLockSlim _responseLock = new(LockRecursionPolicy.NoRecursion);
        private readonly IDisposable _endResponse;

        /// <param name="checkpointInterval">Overrides the interval between checkpoints, so tests do not have to wait it out.</param>
        public ProgressTracker(ISnapTrieFactory snapTrieFactory, ISyncConfig syncConfig, FastSync.IStateSyncPivot pivot, ILogManager? logManager, TimeSpan? checkpointInterval = null)
        {
            _logger = logManager?.GetClassLogger<ProgressTracker>() ?? throw new ArgumentNullException(nameof(logManager));
            _snapTrieFactory = snapTrieFactory ?? throw new ArgumentNullException(nameof(snapTrieFactory));

            _pivot = pivot;
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
            if (_pivot.GetPivotHeader() is null)
            {
                nextBatch = null;
                return false;
            }

            Interlocked.Increment(ref _reqCount);

            BlockHeader? pivotHeader = _pivot.GetPivotHeader();
            Hash256 rootHash = pivotHeader!.StateRoot!;
            ulong blockNumber = pivotHeader.Number;

            if (_anchor is not { } anchor || blockNumber < anchor.Number) _anchor = (blockNumber, pivotHeader.Hash!);

            if (_checkpointInterval is { } interval
                && Stopwatch.GetElapsedTime(_lastCheckpointTimestamp) >= interval
                && !IsSnapGetRangesFinished())
            {
                SaveCheckpoint();
            }

            if (!AccountsToRefresh.IsEmpty && Volatile.Read(ref _consecutiveAccountRefreshes) < MAX_CONSECUTIVE_ACCOUNT_REFRESHES)
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
            else if (!AccountsToRefresh.IsEmpty)
            {
                // Out of turns, but nothing else is queued: the tail of the sync must not run at one request per turn.
                nextBatch = DequeAccountToRefresh(rootHash);
            }
            else
            {
                nextBatch = null;
                bool rangePhaseFinished = IsSnapGetRangesFinished();
                if (rangePhaseFinished)
                {
                    _logger.Info("Snap - State Ranges (Phase 1 of 2) finished.");
                    _snapTrieFactory.MarkRangePhaseFinished();
                    PublishCoveredAccountKeySpace(AccountKeySpace);
                    // Healing writes are not checkpointed, so a restart from here on has to start the sync over.
                    if (_checkpointInterval is not null) _snapTrieFactory.SaveCheckpoint(null);
                }

                LogRequest(NO_REQUEST);

                return IsSnapGetRangesFinished();
            }

            TakeTurn(nextBatch);
            if (_checkpointInterval is not null && nextBatch is not null) _requestsInFlight.TryAdd(nextBatch, 0);

            return false;
        }

        /// <summary>Stops counting <paramref name="batch"/> as in flight, once its response has been handled or handed back.</summary>
        public void ReportRequestReleased(SnapSyncBatch batch) => _requestsInFlight.TryRemove(batch, out _);

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

        private SnapSyncBatch DequeStorageToRetrieveRequest(Hash256 rootHash, ulong blockNumber)
        {
            Interlocked.Increment(ref _activeStorageRequests); // for race condition so that snap does not exit prematurely

            ArrayPoolList<PathWithAccount> storagesToQuery = [with(STORAGE_BATCH_SIZE)];
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

        private SnapSyncBatch CreateNextSlowRangeRequest(StorageRange slotRange, Hash256 rootHash, ulong blockNumber)
        {
            slotRange.RootHash = rootHash;
            slotRange.BlockNumber = blockNumber;

            LogRequest($"NextSlotRange:{slotRange.Accounts.Count}");

            return new SnapSyncBatch { StorageRangeRequest = slotRange };
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

        private SnapSyncBatch? DequeAccountToRefresh(Hash256 rootHash)
        {
            // One account per request: each refresh is served by a single GetAccountRange.
            if (!AccountsToRefresh.TryDequeue(out AccountWithStorageStartingHash acc))
                return null;

            Interlocked.Increment(ref _activeAccRefreshRequests);

            LogRequest($"AccountsToRefresh: {AccountsToRefresh.Count}");

            ArrayPoolList<AccountWithStorageStartingHash> paths = new(1) { acc };

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
                EnqueueCodeHash(hash);
            }
        }

        public void EnqueueCodeHash(ValueHash256 hash) => CodesToRetrieve.Enqueue(hash);

        public void ReportCodeRequestFinished(ReadOnlySpan<ValueHash256> codeHashes)
        {
            EnqueueCodeHashes(codeHashes);

            Interlocked.Decrement(ref _activeCodeRequests);
        }

        public void ReportAccountRefreshFinished(AccountsToRefreshRequest? accountsToRefreshRequest = null)
        {
            if (accountsToRefreshRequest is not null)
            {
                foreach (AccountWithStorageStartingHash path in accountsToRefreshRequest.Paths.AsSpan())
                {
                    AccountsToRefresh.Enqueue(path);
                }
            }

            Interlocked.Decrement(ref _activeAccRefreshRequests);
        }

        public void EnqueueAccountStorage(PathWithAccount pwa) => StoragesToRetrieve.Enqueue(pwa);

        public void EnqueueAccountRefresh(PathWithAccount pathWithAccount, in ValueHash256? startingHash, in ValueHash256? hashLimit)
        {
            TrackAccountToHeal(pathWithAccount.Path);
            AccountsToRefresh.Enqueue(new AccountWithStorageStartingHash() { PathAndAccount = pathWithAccount, StorageStartingHash = startingHash.GetValueOrDefault(), StorageHashLimit = hashLimit ?? Keccak.MaxValue });
        }

        public void ReportStorageRequestFinished(int accountCount) =>
            Interlocked.Add(ref _activeStorageRequests, -accountCount);

        public void EnqueueNextSlot(StorageRange? storageRange)
        {
            if (storageRange is not null)
            {
                NextSlotRange.Enqueue(storageRange);
            }
        }

        public void EnqueueNextSlot(StorageRange parentRequest, int accountIndex, ValueHash256 lastProcessedHash, int slotCount)
        {
            ValueHash256 limitHash = parentRequest.LimitHash ?? Keccak.MaxValue;
            if (lastProcessedHash > limitHash)
                return;

            ValueHash256? startingHash = parentRequest.StartingHash;
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
        }

        public void RequeueStorageRange(StorageRange storageRange)
        {
            // A single account is a continuation, so it keeps its starting hash and limit.
            if (storageRange.Accounts.Count == 1)
            {
                EnqueueNextSlot(storageRange);
                return;
            }

            foreach (PathWithAccount account in storageRange.Accounts.AsSpan())
            {
                EnqueueAccountStorage(account);
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

        public bool IsSnapGetRangesFinished() => AccountRangeReadyForRequest.IsEmpty
                   && StoragesToRetrieve.IsEmpty
                   && NextSlotRange.IsEmpty
                   && CodesToRetrieve.IsEmpty
                   && AccountsToRefresh.IsEmpty
                   && _activeAccountRequests == 0
                   && _activeStorageRequests == 0
                   && _activeCodeRequests == 0
                   && _activeAccRefreshRequests == 0;

        public void Reset()
        {
            AccountRangeReadyForRequest.Clear();
            SetupAccountRangePartition();
            _pivot.UpdatedStorages.Clear();

            _largeStorageProgress.Clear();
            _estimatedStorageRemaining = null;
            _shouldStartLoggingLargeStorage = false;
            _anchor = null;
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
                }
                finally
                {
                    _responseLock.ExitWriteLock();
                }

                // Responses handled from here on write after the progress read above, so the flush still covers
                // everything it records as done, and resuming discards what they wrote.
                _snapTrieFactory.SaveCheckpoint(checkpoint);
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
            foreach (StorageRange range in NextSlotRange) storageRanges.Add(ToProgress(range));

            // An account range in flight needs nothing here: its partition records where it starts either way.
            foreach ((SnapSyncBatch batch, byte _) in _requestsInFlight)
            {
                if (batch.StorageRangeRequest is { } storageRange) storageRanges.Add(ToProgress(storageRange));
                if (batch.CodesRequest is { } codes) codesToRetrieve.AddRange(codes.AsSpan());
                if (batch.AccountsToRefreshRequest is { } refresh) accountsToRefresh.AddRange(refresh.Paths.AsSpan());
            }

            (ulong anchorNumber, Hash256 anchorHash) = _anchor!.Value;
            return new SnapCheckpoint
            {
                AnchorNumber = anchorNumber,
                AnchorHash = anchorHash,
                Partitions = partitions,
                StoragesToRetrieve = StoragesToRetrieve.ToArray(),
                StorageRanges = [.. storageRanges],
                AccountsToRefresh = [.. accountsToRefresh],
                CodesToRetrieve = [.. codesToRetrieve],
                UpdatedStorages = [.. _pivot.UpdatedStorages],
            };

            static StorageRangeProgress ToProgress(StorageRange range) =>
                new(range.Accounts.AsSpan().ToArray(), range.StartingHash, range.LimitHash);
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

            foreach (PathWithAccount account in checkpoint.StoragesToRetrieve) StoragesToRetrieve.Enqueue(account);

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

            foreach (AccountWithStorageStartingHash refresh in checkpoint.AccountsToRefresh) AccountsToRefresh.Enqueue(refresh);
            foreach (ValueHash256 code in checkpoint.CodesToRetrieve) CodesToRetrieve.Enqueue(code);
            foreach (Hash256 address in checkpoint.UpdatedStorages) _pivot.UpdatedStorages.Add(address);
            _anchor = (checkpoint.AnchorNumber, checkpoint.AnchorHash);

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
                _largeStorageProgress.AddOrUpdate(item.Accounts.AsSpan()[0].Path,
                    (key, progress) => new LargeProgressStatus().UpdateProgress(progress),
                    (key, progress, range) => progress.UpdateProgress(range),
                    item
                );
            }

            return true;
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
