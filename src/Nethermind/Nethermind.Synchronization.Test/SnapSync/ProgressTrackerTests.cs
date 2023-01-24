// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Snap;
using Nethermind.Synchronization.SnapSync;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.SnapSync;

public class ProgressTrackerTests
{
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
