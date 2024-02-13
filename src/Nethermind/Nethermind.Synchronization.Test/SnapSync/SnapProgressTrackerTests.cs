// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Snap;
using Nethermind.Synchronization.SnapSync;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.SnapSync;

public class SnapProgressTrackerTests
{
    [Test]
    [Repeat(3)]
    public async Task Did_not_have_race_issue()
    {
        BlockTree blockTree = Build.A.BlockTree().WithBlocks(Build.A.Block.TestObject).TestObject;
        using SnapProgressTracker progressTracker = new(blockTree, new MemDb(), LimboLogs.Instance);
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
                progressTracker.IsGetRangesFinished().Should().BeFalse();
            }
        });

        await requestTask;
        await checkTask;
    }

    [Test]
    public void Will_create_multiple_get_address_range_request()
    {
        BlockTree blockTree = Build.A.BlockTree().WithBlocks(Build.A.Block.TestObject).TestObject;
        using SnapProgressTracker progressTracker = new(blockTree, new MemDb(), LimboLogs.Instance, 4);

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
        BlockTree blockTree = Build.A.BlockTree().WithBlocks(Build.A.Block.TestObject).TestObject;
        using SnapProgressTracker progressTracker = new SnapProgressTracker(blockTree, new MemDb(), LimboLogs.Instance);

        for (int i = 0; i < SnapProgressTracker.HIGH_STORAGE_QUEUE_SIZE - 1; i++)
        {
            progressTracker.EnqueueAccountStorage(new PathWithAccount()
            {
                Path = TestItem.ValueKeccaks[0]
            });
        }

        for (int i = 0; i < SnapProgressTracker.HIGH_CODES_QUEUE_SIZE; i++)
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
        BlockTree blockTree = Build.A.BlockTree().WithBlocks(Build.A.Block.TestObject).TestObject;
        using SnapProgressTracker progressTracker = new SnapProgressTracker(blockTree, new MemDb(), LimboLogs.Instance);

        for (int i = 0; i < SnapProgressTracker.HIGH_STORAGE_QUEUE_SIZE; i++)
        {
            progressTracker.EnqueueAccountStorage(new PathWithAccount()
            {
                Path = TestItem.ValueKeccaks[0]
            });
        }

        for (int i = 0; i < SnapProgressTracker.HIGH_CODES_QUEUE_SIZE; i++)
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
        using SnapProgressTracker progressTracker = new(blockTree, memDb, LimboLogs.Instance, 1);

        progressTracker.IsFinished(out SnapSyncBatch? request);
        request!.AccountRangeRequest.Should().NotBeNull();
        progressTracker.UpdateAccountRangePartitionProgress(request.AccountRangeRequest!.LimitHash!.Value, Keccak.MaxValue, false);
        progressTracker.ReportAccountRangePartitionFinished(request.AccountRangeRequest!.LimitHash!.Value);
        request.Dispose();
        bool finished = progressTracker.IsFinished(out _);
        finished.Should().BeTrue();

        memDb.WasFlushed.Should().BeTrue();
        memDb[SnapProgressTracker.ACC_PROGRESS_KEY].Should().BeEquivalentTo(Keccak.MaxValue.BytesToArray());
    }
}
