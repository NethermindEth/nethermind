// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Reflection;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Snap;
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.SnapSync;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.SnapSync;

public class ProgressTrackerTests
{
    [Test]
    [Repeat(3)]
    public async Task Did_not_have_race_issue()
    {
        using ProgressTracker progressTracker = CreateProgressTracker(accountRangePartition: 1, maxActiveStorageRangeBatches: 0);
        progressTracker.EnqueueNextSlot(new StorageRange()
        {
            Accounts = ArrayPoolList<PathWithAccount>.Empty(),
        });

        int loopIteration = 100000;
        Task requestTask = Task.Factory.StartNew(() =>
        {
            for (int i = 0; i < loopIteration; i++)
            {
                bool finished = progressTracker.IsFinished(out SnapSyncBatch? snapSyncBatch);
                Assert.That(finished, Is.False);
                progressTracker.EnqueueNextSlot(snapSyncBatch!.StorageRangeRequest!);
            }
        });

        Task checkTask = Task.Factory.StartNew(() =>
        {
            for (int i = 0; i < loopIteration; i++)
            {
                Assert.That(progressTracker.IsSnapGetRangesFinished(), Is.False);
            }
        });

        await requestTask;
        await checkTask;
    }

    [Test]
    public void Will_create_multiple_get_address_range_request()
    {
        using ProgressTracker progressTracker = CreateProgressTracker(accountRangePartition: 4);

        Hash256[] expectedStarts =
        [
            new("0x0000000000000000000000000000000000000000000000000000000000000000"),
            new("0x4000000000000000000000000000000000000000000000000000000000000000"),
            new("0x8000000000000000000000000000000000000000000000000000000000000000"),
            new("0xc000000000000000000000000000000000000000000000000000000000000000"),
        ];
        Hash256[] expectedLimits =
        [
            new("0x3fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"),
            new("0x7fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"),
            new("0xbfffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"),
            new("0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"),
        ];

        for (int i = 0; i < 4; i++)
        {
            bool finished = progressTracker.IsFinished(out SnapSyncBatch? request);
            Assert.That(request!.AccountRangeRequest, Is.Not.Null);
            Assert.That(request.AccountRangeRequest!.StartingHash, Is.EqualTo(expectedStarts[i]));
            Assert.That(request.AccountRangeRequest.LimitHash!.Value, Is.EqualTo(expectedLimits[i]));
            Assert.That(finished, Is.False);
            request.Dispose();
        }

        bool finalFinished = progressTracker.IsFinished(out SnapSyncBatch? finalRequest);
        Assert.That(finalRequest, Is.Null);
        Assert.That(finalFinished, Is.False);
    }

    [Test]
    public void Will_deque_code_request_if_high_even_if_storage_queue_is_not_empty()
    {
        using ProgressTracker progressTracker = CreateProgressTracker();

        for (int i = 0; i < ProgressTracker.HIGH_STORAGE_QUEUE_SIZE - 1; i++)
        {
            progressTracker.EnqueueAccountStorage(new PathWithAccount()
            {
                Path = TestItem.ValueKeccaks[0]
            });
        }

        for (int i = 0; i < ProgressTracker.HIGH_CODES_QUEUE_SIZE; i++)
        {
            progressTracker.EnqueueCodeHashes(new[] { TestItem.ValueKeccaks[0] });
        }

        progressTracker.IsFinished(out SnapSyncBatch? request);
        Assert.That(request!.CodesRequest, Is.Not.Null);
        Assert.That(request.StorageRangeRequest, Is.Null);
        request.Dispose();
    }

    [Test]
    public void Will_deque_storage_request_if_high()
    {
        using ProgressTracker progressTracker = CreateProgressTracker();

        for (int i = 0; i < ProgressTracker.HIGH_STORAGE_QUEUE_SIZE; i++)
        {
            progressTracker.EnqueueAccountStorage(new PathWithAccount()
            {
                Path = TestItem.ValueKeccaks[0]
            });
        }

        for (int i = 0; i < ProgressTracker.HIGH_CODES_QUEUE_SIZE; i++)
        {
            progressTracker.EnqueueCodeHashes(new[] { TestItem.ValueKeccaks[0] });
        }

        progressTracker.IsFinished(out SnapSyncBatch? request);
        Assert.That(request!.CodesRequest, Is.Null);
        Assert.That(request.StorageRangeRequest, Is.Not.Null);
        request.Dispose();
    }

    [Test]
    public void Will_mark_progress_and_flush_when_finished()
    {
        BlockTree blockTree = Build.A.BlockTree()
            .WithStateRoot(Keccak.EmptyTreeHash)
            .OfChainLength(2).TestObject;
        TestMemDb memDb = new();
        SyncConfig syncConfig = new TestSyncConfig() { SnapSyncAccountRangePartitionCount = 1 };
        using ProgressTracker progressTracker = new(memDb, syncConfig, new StateSyncPivot(blockTree, syncConfig, LimboLogs.Instance), LimboLogs.Instance);

        progressTracker.IsFinished(out SnapSyncBatch? request);
        Assert.That(request!.AccountRangeRequest, Is.Not.Null);
        progressTracker.UpdateAccountRangePartitionProgress(request.AccountRangeRequest!.LimitHash!.Value, Keccak.MaxValue, false);
        progressTracker.ReportAccountRangePartitionFinished(request.AccountRangeRequest!.LimitHash!.Value);
        request.Dispose();
        bool finished = progressTracker.IsFinished(out _);
        Assert.That(finished, Is.True);

        Assert.That(memDb.WasFlushed, Is.True);
        Assert.That(memDb[ProgressTracker.ACC_PROGRESS_KEY], Is.EqualTo(Keccak.MaxValue.BytesToArray()));
    }

    [Test]
    public void Requeued_account_refresh_request_preserves_paths()
    {
        using ProgressTracker progressTracker = CreateProgressTracker();
        progressTracker.EnqueueAccountRefresh(TestItem.Tree.AccountsWithPaths[0], null, null);

        Assert.That(progressTracker.IsFinished(out SnapSyncBatch? firstBatch), Is.False);
        Assert.That(firstBatch!.AccountsToRefreshRequest, Is.Not.Null);
        Assert.That(firstBatch.AccountsToRefreshRequest!.Paths.Count, Is.EqualTo(1));

        progressTracker.ReportAccountRefreshFinished(firstBatch.AccountsToRefreshRequest);
        firstBatch.Dispose();

        Assert.That(progressTracker.IsFinished(out SnapSyncBatch? retryBatch), Is.False);
        Assert.That(retryBatch!.AccountsToRefreshRequest, Is.Not.Null);
        Assert.That(retryBatch.AccountsToRefreshRequest!.Paths.Count, Is.EqualTo(1));
        retryBatch.Dispose();
    }

    [Test]
    public void Account_refresh_request_uses_queue_when_counter_lags()
    {
        using ProgressTracker progressTracker = CreateProgressTracker();
        progressTracker.EnqueueAccountRefresh(TestItem.Tree.AccountsWithPaths[0], null, null);
        SetAccountRefreshQueueCount(progressTracker, 0);

        Assert.That(progressTracker.IsFinished(out SnapSyncBatch? batch), Is.False);
        Assert.That(batch!.AccountsToRefreshRequest, Is.Not.Null);
        Assert.That(batch.AccountsToRefreshRequest!.Paths.Count, Is.EqualTo(1));
        batch.Dispose();
    }

    [Test]
    public void Queued_storage_request_completion_allows_range_phase_to_finish()
    {
        using ProgressTracker progressTracker = CreateProgressTracker();
        progressTracker.EnqueueAccountStorage(TestItem.Tree.AccountsWithPaths[0]);

        FinishAccountRangePhase(progressTracker);

        Assert.That(progressTracker.IsFinished(out SnapSyncBatch? storageBatch), Is.False);
        Assert.That(storageBatch!.StorageRangeRequest, Is.Not.Null);

        progressTracker.ReportFullStorageRequestFinished(storageBatch.StorageRangeRequest!.Accounts.Count);
        storageBatch.Dispose();

        Assert.That(progressTracker.IsFinished(out SnapSyncBatch? finalBatch), Is.True);
        Assert.That(finalBatch, Is.Null);
    }

    [Test]
    public void Slot_range_request_completion_allows_range_phase_to_finish()
    {
        using ProgressTracker progressTracker = CreateProgressTracker();
        progressTracker.EnqueueNextSlot(new StorageRange()
        {
            Accounts = new ArrayPoolList<PathWithAccount>(1) { TestItem.Tree.AccountsWithPaths[0] },
        });

        FinishAccountRangePhase(progressTracker);

        Assert.That(progressTracker.IsFinished(out SnapSyncBatch? storageBatch), Is.False);
        Assert.That(storageBatch!.StorageRangeRequest, Is.Not.Null);

        progressTracker.ReportFullStorageRequestFinished(storageBatch.StorageRangeRequest!.Accounts.Count);
        storageBatch.Dispose();

        Assert.That(progressTracker.IsFinished(out SnapSyncBatch? finalBatch), Is.True);
        Assert.That(finalBatch, Is.Null);
    }

    [Test]
    public void Storage_queue_waits_when_active_storage_batch_limit_is_reached()
    {
        using ProgressTracker progressTracker = CreateProgressTracker(maxActiveStorageRangeBatches: 1);
        FinishAccountRangePhase(progressTracker);

        progressTracker.EnqueueAccountStorage(TestItem.Tree.AccountsWithPaths[0]);
        Assert.That(progressTracker.IsFinished(out SnapSyncBatch? activeBatch), Is.False);
        Assert.That(activeBatch!.StorageRangeRequest, Is.Not.Null);

        progressTracker.EnqueueAccountStorage(TestItem.Tree.AccountsWithPaths[1]);
        Assert.That(progressTracker.IsFinished(out SnapSyncBatch? throttledBatch), Is.False);
        Assert.That(throttledBatch, Is.Null);
        Assert.That(progressTracker.IsSnapGetRangesFinished(), Is.False);

        progressTracker.ReportFullStorageRequestFinished(activeBatch.StorageRangeRequest!.Accounts.Count);
        activeBatch.Dispose();

        Assert.That(progressTracker.IsFinished(out SnapSyncBatch? resumedBatch), Is.False);
        Assert.That(resumedBatch!.StorageRangeRequest, Is.Not.Null);

        progressTracker.ReportFullStorageRequestFinished(resumedBatch.StorageRangeRequest!.Accounts.Count);
        resumedBatch.Dispose();

        Assert.That(progressTracker.IsFinished(out SnapSyncBatch? finalBatch), Is.True);
        Assert.That(finalBatch, Is.Null);
    }

    [Test]
    public void Code_queue_can_progress_when_storage_batch_limit_is_reached()
    {
        using ProgressTracker progressTracker = CreateProgressTracker(maxActiveStorageRangeBatches: 1);
        FinishAccountRangePhase(progressTracker);

        progressTracker.EnqueueAccountStorage(TestItem.Tree.AccountsWithPaths[0]);
        Assert.That(progressTracker.IsFinished(out SnapSyncBatch? activeBatch), Is.False);
        Assert.That(activeBatch!.StorageRangeRequest, Is.Not.Null);

        progressTracker.EnqueueAccountStorage(TestItem.Tree.AccountsWithPaths[1]);
        progressTracker.EnqueueCodeHashes([TestItem.ValueKeccaks[0]]);

        Assert.That(progressTracker.IsFinished(out SnapSyncBatch? codeBatch), Is.False);
        Assert.That(codeBatch!.CodesRequest, Is.Not.Null);
        Assert.That(codeBatch.StorageRangeRequest, Is.Null);

        progressTracker.ReportCodeRequestFinished(ReadOnlySpan<ValueHash256>.Empty);
        codeBatch.Dispose();
        progressTracker.ReportFullStorageRequestFinished(activeBatch.StorageRangeRequest!.Accounts.Count);
        activeBatch.Dispose();
    }

    [Test]
    public void Storage_queue_pauses_new_account_requests_until_storage_catches_up()
    {
        using ProgressTracker progressTracker = CreateProgressTracker(accountRangePartition: 2, maxQueuedStorageAccountsForAccountRequests: 1);

        Assert.That(progressTracker.IsFinished(out SnapSyncBatch? activeAccountBatch), Is.False);
        Assert.That(activeAccountBatch!.AccountRangeRequest, Is.Not.Null);

        progressTracker.EnqueueAccountStorage(TestItem.Tree.AccountsWithPaths[0]);

        Assert.That(progressTracker.IsFinished(out SnapSyncBatch? storageBatch), Is.False);
        Assert.That(storageBatch!.AccountRangeRequest, Is.Null);
        Assert.That(storageBatch.StorageRangeRequest, Is.Not.Null);

        progressTracker.ReportFullStorageRequestFinished(storageBatch.StorageRangeRequest!.Accounts.Count);
        storageBatch.Dispose();

        ValueHash256 hashLimit = activeAccountBatch.AccountRangeRequest!.LimitHash!.Value;
        progressTracker.ReportAccountRangePartitionFinished(hashLimit);
        activeAccountBatch.Dispose();

        Assert.That(progressTracker.IsFinished(out SnapSyncBatch? nextAccountBatch), Is.False);
        Assert.That(nextAccountBatch!.AccountRangeRequest, Is.Not.Null);
        nextAccountBatch.Dispose();
    }

    [Test]
    public void Storage_queue_at_pause_limit_is_requested_before_slot_ranges()
    {
        using ProgressTracker progressTracker = CreateProgressTracker(maxQueuedStorageAccountsForAccountRequests: 1);
        FinishAccountRangePhase(progressTracker);

        StorageRange slotRange = CreateStorageRange(TestItem.Tree.AccountsWithPaths[0]);
        slotRange.StartingHash = TestItem.ValueKeccaks[0];
        progressTracker.EnqueueNextSlot(slotRange);
        progressTracker.EnqueueAccountStorage(TestItem.Tree.AccountsWithPaths[1]);

        Assert.That(progressTracker.IsFinished(out SnapSyncBatch? storageBatch), Is.False);
        Assert.That(storageBatch!.StorageRangeRequest, Is.Not.Null);
        Assert.That(storageBatch.StorageRangeRequest!.StartingHash, Is.EqualTo(ValueKeccak.Zero));
        Assert.That(storageBatch.StorageRangeRequest.Accounts.AsSpan()[0], Is.EqualTo(TestItem.Tree.AccountsWithPaths[1]));

        progressTracker.ReportFullStorageRequestFinished(storageBatch.StorageRangeRequest.Accounts.Count);
        storageBatch.Dispose();
    }

    [Test]
    public void Storage_request_account_batch_size_limits_queued_storage_request()
    {
        using ProgressTracker progressTracker = CreateProgressTracker(storageRequestAccountBatchSize: 2);
        FinishAccountRangePhase(progressTracker);

        progressTracker.EnqueueAccountStorage(TestItem.Tree.AccountsWithPaths[0]);
        progressTracker.EnqueueAccountStorage(TestItem.Tree.AccountsWithPaths[1]);
        progressTracker.EnqueueAccountStorage(TestItem.Tree.AccountsWithPaths[2]);

        Assert.That(progressTracker.IsFinished(out SnapSyncBatch? firstBatch), Is.False);
        Assert.That(firstBatch!.StorageRangeRequest, Is.Not.Null);
        Assert.That(firstBatch.StorageRangeRequest!.Accounts.Count, Is.EqualTo(2));
        progressTracker.ReportFullStorageRequestFinished(firstBatch.StorageRangeRequest.Accounts.Count);
        firstBatch.Dispose();

        Assert.That(progressTracker.IsFinished(out SnapSyncBatch? secondBatch), Is.False);
        Assert.That(secondBatch!.StorageRangeRequest, Is.Not.Null);
        Assert.That(secondBatch.StorageRangeRequest!.Accounts.Count, Is.EqualTo(1));
        progressTracker.ReportFullStorageRequestFinished(secondBatch.StorageRangeRequest.Accounts.Count);
        secondBatch.Dispose();

        Assert.That(progressTracker.IsFinished(out SnapSyncBatch? finalBatch), Is.True);
        Assert.That(finalBatch, Is.Null);
    }

    [Test]
    public void Slot_range_queue_waits_when_active_storage_batch_limit_is_reached()
    {
        using ProgressTracker progressTracker = CreateProgressTracker(maxActiveStorageRangeBatches: 1);
        FinishAccountRangePhase(progressTracker);

        progressTracker.EnqueueNextSlot(CreateStorageRange(TestItem.Tree.AccountsWithPaths[0]));
        Assert.That(progressTracker.IsFinished(out SnapSyncBatch? activeBatch), Is.False);
        Assert.That(activeBatch!.StorageRangeRequest, Is.Not.Null);

        progressTracker.EnqueueNextSlot(CreateStorageRange(TestItem.Tree.AccountsWithPaths[1]));
        Assert.That(progressTracker.IsFinished(out SnapSyncBatch? throttledBatch), Is.False);
        Assert.That(throttledBatch, Is.Null);

        progressTracker.ReportFullStorageRequestFinished(activeBatch.StorageRangeRequest!.Accounts.Count);
        activeBatch.Dispose();

        Assert.That(progressTracker.IsFinished(out SnapSyncBatch? resumedBatch), Is.False);
        Assert.That(resumedBatch!.StorageRangeRequest, Is.Not.Null);
        progressTracker.ReportFullStorageRequestFinished(resumedBatch.StorageRangeRequest!.Accounts.Count);
        resumedBatch.Dispose();
    }

    [TestCase("0x0000000000000000000000000000000000000000000000000000000000000000", "0x2000000000000000000000000000000000000000000000000000000000000000", null, "0x8fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")]
    [TestCase("0x2000000000000000000000000000000000000000000000000000000000000000", "0x4000000000000000000000000000000000000000000000000000000000000000", "0x8fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", "0x67ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")]
    [TestCase("0x8fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", "0xbfffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", null, "0xdfffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")]
    public void Should_partition_storage_request_if_last_processed_less_than_threshold(string start, string lastProcessed, string? limit, string expectedSplit)
    {
        using ProgressTracker progressTracker = CreateProgressTracker(enableStorageSplits: true);

        ValueHash256 lastProcessedHash = new(lastProcessed);
        ValueHash256? limitHash = limit is null ? (ValueHash256?)null : new ValueHash256(limit);

        StorageRange storageRange = new()
        {
            Accounts = new ArrayPoolList<PathWithAccount>(1) { TestItem.Tree.AccountsWithPaths[0] },
            StartingHash = new ValueHash256(start),
            LimitHash = limitHash
        };
        progressTracker.EnqueueNextSlot(storageRange, 0, lastProcessedHash, 1_000_000_000);

        //ignore account range
        bool isFinished = progressTracker.IsFinished(out _);

        //expecting 2 batches
        isFinished = progressTracker.IsFinished(out SnapSyncBatch? batch1);
        Assert.That(isFinished, Is.False);
        Assert.That(batch1, Is.Not.Null);

        isFinished = progressTracker.IsFinished(out SnapSyncBatch? batch2);
        Assert.That(isFinished, Is.False);
        Assert.That(batch2, Is.Not.Null);

        Assert.That(batch2?.StorageRangeRequest?.StartingHash, Is.EqualTo(batch1?.StorageRangeRequest?.LimitHash?.IncrementPath()));
        Assert.That(batch1?.StorageRangeRequest?.StartingHash, Is.EqualTo(lastProcessedHash.IncrementPath()));
        Assert.That(batch2?.StorageRangeRequest?.LimitHash, Is.EqualTo(limitHash ?? Keccak.MaxValue));

        Assert.That(batch1?.StorageRangeRequest?.LimitHash, Is.EqualTo(new ValueHash256(expectedSplit)));
    }


    [TestCase("0x0000000000000000000000000000000000000000000000000000000000000000", "0xb100000000000000000000000000000000000000000000000000000000000000", null)]
    [TestCase("0x8fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", "0xdfffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", null)]
    public void Should_not_partition_storage_request_if_last_processed_more_than_threshold(string start, string lastProcessed, string? limit)
    {
        using ProgressTracker progressTracker = CreateProgressTracker();

        ValueHash256 lastProcessedHash = new(lastProcessed);
        ValueHash256? limitHash = limit is null ? (ValueHash256?)null : new ValueHash256(limit);

        StorageRange storageRange = new()
        {
            Accounts = new ArrayPoolList<PathWithAccount>(1) { TestItem.Tree.AccountsWithPaths[0] },
            StartingHash = new ValueHash256(start),
            LimitHash = limitHash
        };
        progressTracker.EnqueueNextSlot(storageRange, 0, lastProcessedHash, 100000000);

        //ignore account range
        bool isFinished = progressTracker.IsFinished(out _);

        //expecting 1 batch
        isFinished = progressTracker.IsFinished(out SnapSyncBatch? batch1);
        Assert.That(isFinished, Is.False);
        Assert.That(batch1, Is.Not.Null);

        Assert.That(batch1?.StorageRangeRequest?.StartingHash, Is.EqualTo(lastProcessedHash.IncrementPath()));
        Assert.That(batch1?.StorageRangeRequest?.LimitHash, Is.EqualTo(limitHash ?? Keccak.MaxValue));
    }

    private ProgressTracker CreateProgressTracker(
        int accountRangePartition = 1,
        bool enableStorageSplits = false,
        int maxActiveStorageRangeBatches = 2,
        int maxQueuedStorageAccountsForAccountRequests = 19_200,
        int storageRequestAccountBatchSize = 1_200)
    {
        BlockTree blockTree = Build.A.BlockTree().WithStateRoot(Keccak.EmptyTreeHash).OfChainLength(2).TestObject;
        SyncConfig syncConfig = new TestSyncConfig()
        {
            SnapSyncAccountRangePartitionCount = accountRangePartition,
            EnableSnapSyncStorageRangeSplit = enableStorageSplits,
            SnapSyncMaxActiveStorageRangeBatches = maxActiveStorageRangeBatches,
            SnapSyncMaxQueuedStorageAccountsForAccountRequests = maxQueuedStorageAccountsForAccountRequests,
            SnapSyncStorageRequestAccountBatchSize = storageRequestAccountBatchSize
        };
        return new(new MemDb(), syncConfig, new StateSyncPivot(blockTree, syncConfig, LimboLogs.Instance), LimboLogs.Instance);
    }

    private static StorageRange CreateStorageRange(PathWithAccount account) =>
        new()
        {
            Accounts = new ArrayPoolList<PathWithAccount>(1) { account },
        };

    private static void FinishAccountRangePhase(ProgressTracker progressTracker)
    {
        Assert.That(progressTracker.IsFinished(out SnapSyncBatch? request), Is.False);
        Assert.That(request!.AccountRangeRequest, Is.Not.Null);

        ValueHash256 hashLimit = request.AccountRangeRequest!.LimitHash!.Value;
        progressTracker.UpdateAccountRangePartitionProgress(hashLimit, Keccak.MaxValue, false);
        progressTracker.ReportAccountRangePartitionFinished(hashLimit);
        request.Dispose();
    }

    private static void SetAccountRefreshQueueCount(ProgressTracker progressTracker, int value)
    {
        FieldInfo? field = typeof(ProgressTracker).GetField("_accountRefreshQueueCount", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(field, Is.Not.Null);
        field!.SetValue(progressTracker, value);
    }
}
