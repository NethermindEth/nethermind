// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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

    private ProgressTracker CreateProgressTracker(int accountRangePartition = 1, bool enableStorageSplits = false)
    {
        BlockTree blockTree = Build.A.BlockTree().WithStateRoot(Keccak.EmptyTreeHash).OfChainLength(2).TestObject;
        SyncConfig syncConfig = new TestSyncConfig() { SnapSyncAccountRangePartitionCount = accountRangePartition, EnableSnapSyncStorageRangeSplit = enableStorageSplits };
        return new(new MemDb(), syncConfig, new StateSyncPivot(blockTree, syncConfig, LimboLogs.Instance), LimboLogs.Instance);
    }
}
