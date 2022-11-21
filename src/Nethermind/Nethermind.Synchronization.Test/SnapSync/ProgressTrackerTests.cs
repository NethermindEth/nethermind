// Copyright 2022 Demerzel Solutions Limited
// Licensed under the LGPL-3.0. For full terms, see LICENSE-LGPL in the project root.

using System.Diagnostics;
using System;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Snap;
using Nethermind.Synchronization.SnapSync;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.SnapSync;

public class ProgressTrackerTests
{
    [TestCase(100, true, false, ProgressTracker.MAX_ACCOUNT_REQUEST_WAIT)]
    [TestCase(10000, false, false, 0)]
    [TestCase(5000, true, false, 0)]
    [TestCase(500, true, true, 0)]
    public void Test_wait_when_batch_is_not_full(int queuedStorageRange, bool firstRequestIsAccountRequest,
        bool accountRequestFinishReported, int expectedWaitTime)
    {
        BlockTree blockTree = Build.A.BlockTree().OfChainLength(2).TestObject;

        ProgressTracker progressTracker = new(
            blockTree,
            Substitute.For<IDb>(),
            LimboLogs.Instance
        );


        for (int i = 0; i < queuedStorageRange; i++)
        {
            progressTracker.EnqueueAccountStorage(new PathWithAccount());
        }

        progressTracker.MoreAccountsToRight = true;
        if (firstRequestIsAccountRequest)
        {
            // First request is always immediate account range request. Unless the queued request is too high
            (SnapSyncBatch request, bool _) = progressTracker.GetNextRequest();
            request.AccountRangeRequest.Should().NotBeNull();
        }

        if (accountRequestFinishReported)
        {
            progressTracker.ReportAccountRequestFinished();
        }

        Stopwatch sw = Stopwatch.StartNew();
        (_, bool finished) = progressTracker.GetNextRequest();
        sw.Stop();

        finished.Should().BeFalse();

        sw.ElapsedMilliseconds.Should().BeInRange(expectedWaitTime, expectedWaitTime + 50);
    }

    [Test]
    [Repeat(3)]
    public async Task ProgressTracer_did_not_have_race_issue()
    {
        BlockTree blockTree = Build.A.BlockTree().WithBlocks(Build.A.Block.TestObject).TestObject;
        ProgressTracker progressTracker = new ProgressTracker(blockTree, new MemDb(), LimboLogs.Instance);
        progressTracker.MoreAccountsToRight = false;
        progressTracker.EnqueueStorageRange(new StorageRange()
        {
            Accounts = Array.Empty<PathWithAccount>(),
        });

        int loopIteration = 100000;
        Task requestTask = Task.Factory.StartNew(() =>
        {
            for (int i = 0; i < loopIteration; i++)
            {
                (SnapSyncBatch snapSyncBatch, bool ok) = progressTracker.GetNextRequest();
                ok.Should().BeFalse();
                progressTracker.EnqueueStorageRange(snapSyncBatch.StorageRangeRequest!);
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
}
