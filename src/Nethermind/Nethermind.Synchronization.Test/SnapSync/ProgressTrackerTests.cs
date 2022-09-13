using System.Diagnostics;
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
    [TestCase(500, true, false, ProgressTracker.MAX_ACCOUNT_REQUEST_WAIT)]
    [TestCase(10000, false, false, 0)]
    [TestCase(5000, true, false, 0)]
    [TestCase(500, true, true, 0)]
    public void Test_wait_when_batch_is_not_full(int queuedStorageRange, bool firstRequestIsAccountRequest, bool accountRequestFinishReported, int expectedWaitTime)
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
}
