// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
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
        using ProgressTracker progressTracker = CreateProgressTracker(accountRangePartition: 1);
        progressTracker.EnqueueStorageRange(new StorageRange()
        {
            Accounts = ArrayPoolList<PathWithAccount>.Empty(),
        });

        int loopIteration = 100000;
        Task requestTask = Task.Factory.StartNew(() =>
        {
            for (int i = 0; i < loopIteration; i++)
            {
                bool finished = progressTracker.IsFinished(out SnapSyncBatch? snapSyncBatch);
                finished.Should().BeFalse();
                progressTracker.EnqueueStorageRange(snapSyncBatch!.StorageRangeRequest!);
            }
        });

        Task checkTask = Task.Factory.StartNew(() =>
        {
            for (int i = 0; i < loopIteration; i++)
            {
                progressTracker.IsSnapGetRangesFinished().Should().BeFalse();
            }
        });

        await requestTask;
        await checkTask;
    }

    [Test]
    public void Will_create_multiple_get_address_range_request()
    {
        using ProgressTracker progressTracker = CreateProgressTracker(accountRangePartition: 4);

        bool finished = progressTracker.IsFinished(out SnapSyncBatch? request);
        request!.AccountRangeRequest.Should().NotBeNull();
        request.AccountRangeRequest!.StartingHash.Bytes[0].Should().Be(0);
        request.AccountRangeRequest.LimitHash!.Value.Bytes[0].Should().Be(64);
        finished.Should().BeFalse();
        request.Dispose();

        finished = progressTracker.IsFinished(out request);
        request!.AccountRangeRequest.Should().NotBeNull();
        request.AccountRangeRequest!.StartingHash.Bytes[0].Should().Be(64);
        request.AccountRangeRequest.LimitHash!.Value.Bytes[0].Should().Be(128);
        finished.Should().BeFalse();
        request.Dispose();

        finished = progressTracker.IsFinished(out request);
        request!.AccountRangeRequest.Should().NotBeNull();
        request.AccountRangeRequest!.StartingHash.Bytes[0].Should().Be(128);
        request.AccountRangeRequest.LimitHash!.Value.Bytes[0].Should().Be(192);
        finished.Should().BeFalse();
        request.Dispose();

        finished = progressTracker.IsFinished(out request);
        request!.AccountRangeRequest.Should().NotBeNull();
        request.AccountRangeRequest!.StartingHash.Bytes[0].Should().Be(192);
        request.AccountRangeRequest.LimitHash!.Value.Bytes[0].Should().Be(255);
        finished.Should().BeFalse();
        request.Dispose();

        finished = progressTracker.IsFinished(out request);
        request.Should().BeNull();
        finished.Should().BeFalse();
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
        request!.CodesRequest.Should().NotBeNull();
        request.StorageRangeRequest.Should().BeNull();
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
        request!.CodesRequest.Should().BeNull();
        request.StorageRangeRequest.Should().NotBeNull();
        request.Dispose();
    }

    [Test]
    public void Will_mark_progress_and_flush_when_finished()
    {
        BlockTree blockTree = Build.A.BlockTree().WithBlocks(Build.A.Block
            .WithStateRoot(Keccak.EmptyTreeHash)
            .TestObject).TestObject;
        TestMemDb memDb = new();
        SyncConfig syncConfig = new SyncConfig() { SnapSyncAccountRangePartitionCount = 1 };
        using ProgressTracker progressTracker = new(memDb, syncConfig, new StateSyncPivot(blockTree, syncConfig, LimboLogs.Instance), LimboLogs.Instance);

        progressTracker.IsFinished(out SnapSyncBatch? request);
        request!.AccountRangeRequest.Should().NotBeNull();
        progressTracker.UpdateAccountRangePartitionProgress(request.AccountRangeRequest!.LimitHash!.Value, Keccak.MaxValue, false);
        progressTracker.ReportAccountRangePartitionFinished(request.AccountRangeRequest!.LimitHash!.Value);
        request.Dispose();
        bool finished = progressTracker.IsFinished(out _);
        finished.Should().BeTrue();

        memDb.WasFlushed.Should().BeTrue();
        memDb[ProgressTracker.ACC_PROGRESS_KEY].Should().BeEquivalentTo(Keccak.MaxValue.BytesToArray());
    }

    [TestCase("0x0000000000000000000000000000000000000000000000000000000000000000", "0x2000000000000000000000000000000000000000000000000000000000000000", null, "0x8fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")]
    [TestCase("0x2000000000000000000000000000000000000000000000000000000000000000", "0x4000000000000000000000000000000000000000000000000000000000000000", "0x8fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", "0x67ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")]
    [TestCase("0x8fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", "0xbfffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", null, "0xdfffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")]
    public void Should_partition_storage_request_if_last_processed_less_than_threshold(string start, string lastProcessed, string? limit, string expectedSplit)
    {
        using ProgressTracker progressTracker = CreateProgressTracker();

        var startHash = new ValueHash256(start);
        var lastProcessedHash = new ValueHash256(lastProcessed);
        ValueHash256? limitHash = limit is null ? (ValueHash256?)null : new ValueHash256(limit);

        progressTracker.EnqueueStorageRange(TestItem.Tree.AccountsWithPaths[0], startHash, lastProcessedHash, limitHash);

        //ignore account range
        bool isFinished = progressTracker.IsFinished(out _);

        //expecting 2 batches
        isFinished = progressTracker.IsFinished(out SnapSyncBatch? batch1);
        isFinished.Should().BeFalse();
        batch1.Should().NotBeNull();

        isFinished = progressTracker.IsFinished(out SnapSyncBatch? batch2);
        isFinished.Should().BeFalse();
        batch2.Should().NotBeNull();

        batch2?.StorageRangeRequest?.StartingHash.Should().Be(batch1?.StorageRangeRequest?.LimitHash);
        batch1?.StorageRangeRequest?.StartingHash.Should().Be(lastProcessedHash);
        batch2?.StorageRangeRequest?.LimitHash.Should().Be(limitHash ?? Keccak.MaxValue);

        batch1?.StorageRangeRequest?.LimitHash.Should().Be(new ValueHash256(expectedSplit));
    }


    [TestCase("0x0000000000000000000000000000000000000000000000000000000000000000", "0xb100000000000000000000000000000000000000000000000000000000000000", null)]
    [TestCase("0x8fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", "0xdfffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", null)]
    public void Should_not_partition_storage_request_if_last_processed_more_than_threshold(string start, string lastProcessed, string? limit)
    {
        using ProgressTracker progressTracker = CreateProgressTracker();

        var startHash = new ValueHash256(start);
        var lastProcessedHash = new ValueHash256(lastProcessed);
        ValueHash256? limitHash = limit is null ? (ValueHash256?)null : new ValueHash256(limit);

        progressTracker.EnqueueStorageRange(TestItem.Tree.AccountsWithPaths[0], startHash, lastProcessedHash, limitHash);

        //ignore account range
        bool isFinished = progressTracker.IsFinished(out _);

        //expecting 1 batch
        isFinished = progressTracker.IsFinished(out SnapSyncBatch? batch1);
        isFinished.Should().BeFalse();
        batch1.Should().NotBeNull();

        batch1?.StorageRangeRequest?.StartingHash.Should().Be(lastProcessedHash);
        batch1?.StorageRangeRequest?.LimitHash.Should().Be(limitHash ?? Keccak.MaxValue);
    }

    [Test]
    public void Will_process_with_storage_range_request_locks()
    {
        using ProgressTracker progressTracker = CreateProgressTracker();
        var accountPath = TestItem.Tree.AccountAddress0;

        int threadNumber = 4;
        var tasks = new Task[threadNumber];
        CounterWrapper testValue = new(0);
        for (int i = 0; i < threadNumber; i++)
        {
            tasks[i] = Task.Run(() => EnqueueRange(progressTracker, testValue, true));
        }

        Task.WaitAll(tasks);

        var rangeLock = progressTracker.GetLockObjectForPath(accountPath);

        //all should be removed
        rangeLock.Should().BeOfType<ProgressTracker.StorageRangeLockPassThrough>();
        testValue.Counter.Should().Be(threadNumber * 20);

        //same test but don't remove lock structs at the end
        testValue = new(0);
        for (int i = 0; i < threadNumber; i++)
        {
            tasks[i] = Task.Run(() => EnqueueRange(progressTracker, testValue, false));
        }

        Task.WaitAll(tasks);

        rangeLock = progressTracker.GetLockObjectForPath(accountPath);
        rangeLock.Should().BeOfType<ProgressTracker.StorageRangeLock>();
        ((ProgressTracker.StorageRangeLock)rangeLock).Counter.Should().Be(0);
        testValue.Counter.Should().Be(threadNumber * 20);


        void EnqueueRange(ProgressTracker pt, CounterWrapper checkValue, bool shouldRemove)
        {
            ProgressTracker.IStorageRangeLock? rangeLock = null;
            bool canRemove = false;
            int loopCount = 20;

            //assume AddStorageRange does split
            pt?.IncrementStorageRangeLock(accountPath, 1);

            for (int i = 0; i < loopCount; i++)
            {
                try
                {
                    rangeLock = pt?.GetLockObjectForPath(accountPath);

                    rangeLock?.ExecuteSafe(() => checkValue.Counter++);

                    canRemove = shouldRemove && i == loopCount - 1;

                    if (i < loopCount - 1) //no more ranges on last iteration
                        pt?.IncrementStorageRangeLockIfExists(accountPath, 1);
                }
                finally
                {
                    rangeLock?.Decrement(canRemove);
                }
            }
        }

    }

    private ProgressTracker CreateProgressTracker(int accountRangePartition = 1)
    {
        BlockTree blockTree = Build.A.BlockTree().WithBlocks(Build.A.Block.WithStateRoot(Keccak.EmptyTreeHash).TestObject).TestObject;
        SyncConfig syncConfig = new SyncConfig() { SnapSyncAccountRangePartitionCount = accountRangePartition };
        return new(new MemDb(), syncConfig, new StateSyncPivot(blockTree, syncConfig, LimboLogs.Instance), LimboLogs.Instance);
    }
    private class CounterWrapper(int c)
    {
        public int Counter = c;
    };
}
