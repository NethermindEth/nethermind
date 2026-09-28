// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.SnapSync;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.SnapSync;

[TestFixture]
public class SnapSyncRunnerTests
{
    public enum DispatcherOutcome { Completes, Throws, Cancels }

    [TestCase(DispatcherOutcome.Completes, null)]
    [TestCase(DispatcherOutcome.Throws, typeof(InvalidOperationException))]
    [TestCase(DispatcherOutcome.Cancels, typeof(OperationCanceledException))]
    public void Finalizes_whatever_the_dispatcher_does(DispatcherOutcome outcome, Type? expectedException)
    {
        ISnapTrieFactory factory = Substitute.For<ISnapTrieFactory>();
        using ProgressTracker tracker = CreateProgressTracker(factory);

        using CancellationTokenSource cts = new();
        if (outcome == DispatcherOutcome.Cancels) cts.Cancel();

        SnapSyncRunner runner = new(token => outcome switch
        {
            DispatcherOutcome.Throws => throw new InvalidOperationException("boom"),
            DispatcherOutcome.Cancels => throw new OperationCanceledException(token),
            _ => Task.CompletedTask,
        }, factory, tracker);

        Assert.That(async () => await runner.Run(cts.Token),
            expectedException is null ? Throws.Nothing : Throws.InstanceOf(expectedException));

        Received.InOrder(() =>
        {
            factory.EnsureInitialize();
            factory.FinalizeSync();
        });
    }

    // Healing restarts snap sync in the same process after a reorg, so a run has to start from scratch
    // rather than resume the partitions the previous one consumed.
    [Test]
    public async Task Requests_the_account_ranges_again_when_run_twice()
    {
        ISnapTrieFactory factory = Substitute.For<ISnapTrieFactory>();
        using ProgressTracker tracker = CreateProgressTracker(factory);
        SnapSyncRunner runner = new(_ => Task.CompletedTask, factory, tracker);

        await runner.Run(default);
        ConsumeAccountRange(tracker);
        Assert.That(tracker.IsSnapGetRangesFinished(), Is.True);

        await runner.Run(default);

        Assert.That(tracker.IsFinished(out SnapSyncBatch? batch), Is.False);
        Assert.That(batch!.AccountRangeRequest, Is.Not.Null);
        batch.Dispose();
    }

    private static void ConsumeAccountRange(ProgressTracker tracker)
    {
        tracker.IsFinished(out SnapSyncBatch? batch);
        ValueHash256 limit = batch!.AccountRangeRequest!.LimitHash!.Value;
        tracker.UpdateAccountRangePartitionProgress(limit, Keccak.MaxValue, false);
        tracker.ReportAccountRangePartitionFinished(limit);
        batch.Dispose();
    }

    private static ProgressTracker CreateProgressTracker(ISnapTrieFactory factory)
    {
        BlockTree blockTree = Build.A.BlockTree().WithStateRoot(Keccak.EmptyTreeHash).OfChainLength(2).TestObject;
        SyncConfig syncConfig = new TestSyncConfig { SnapSyncAccountRangePartitionCount = 1 };
        return new(factory, syncConfig, new StateSyncPivot(blockTree, syncConfig, LimboLogs.Instance), LimboLogs.Instance);
    }
}
