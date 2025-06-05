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
                finished.Should().BeFalse();
                progressTracker.EnqueueNextSlot(snapSyncBatch!.StorageRangeRequest!);
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
        BlockTree blockTree = Build.A.BlockTree()
            .WithStateRoot(Keccak.EmptyTreeHash)
            .OfChainLength(2).TestObject;
        TestMemDb memDb = new();
        SyncConfig syncConfig = new TestSyncConfig() { SnapSyncAccountRangePartitionCount = 1 };
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
        using ProgressTracker progressTracker = CreateProgressTracker(enableStorageSplits: true);

        var lastProcessedHash = new ValueHash256(lastProcessed);
        ValueHash256? limitHash = limit is null ? (ValueHash256?)null : new ValueHash256(limit);

        StorageRange storageRange = new StorageRange()
        {
            Accounts = new ArrayPoolList<PathWithAccount>(1) { TestItem.Tree.AccountsWithPaths[0] },
            StartingHash = new ValueHash256(start),
            LimitHash = limitHash
        };
        progressTracker.EnqueueNextSlot(storageRange, 0, lastProcessedHash);

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

        var lastProcessedHash = new ValueHash256(lastProcessed);
        ValueHash256? limitHash = limit is null ? (ValueHash256?)null : new ValueHash256(limit);

        StorageRange storageRange = new StorageRange()
        {
            Accounts = new ArrayPoolList<PathWithAccount>(1) { TestItem.Tree.AccountsWithPaths[0] },
            StartingHash = new ValueHash256(start),
            LimitHash = limitHash
        };
        progressTracker.EnqueueNextSlot(storageRange, 0, lastProcessedHash);

        //ignore account range
        bool isFinished = progressTracker.IsFinished(out _);

        //expecting 1 batch
        isFinished = progressTracker.IsFinished(out SnapSyncBatch? batch1);
        isFinished.Should().BeFalse();
        batch1.Should().NotBeNull();

        batch1?.StorageRangeRequest?.StartingHash.Should().Be(lastProcessedHash);
        batch1?.StorageRangeRequest?.LimitHash.Should().Be(limitHash ?? Keccak.MaxValue);
    }

    private ProgressTracker CreateProgressTracker(int accountRangePartition = 1, bool enableStorageSplits = false)
    {
        BlockTree blockTree = Build.A.BlockTree().WithStateRoot(Keccak.EmptyTreeHash).OfChainLength(2).TestObject;
        SyncConfig syncConfig = new TestSyncConfig() { SnapSyncAccountRangePartitionCount = accountRangePartition, EnableSnapSyncStorageRangeSplit = enableStorageSplits };
        return new(new MemDb(), syncConfig, new StateSyncPivot(blockTree, syncConfig, LimboLogs.Instance), LimboLogs.Instance);
    }
}
