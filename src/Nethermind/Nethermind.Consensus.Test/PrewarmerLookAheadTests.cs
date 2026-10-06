// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Linq;
using Autofac;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Consensus.Test;

[TestFixtureSource(nameof(Forks))]
public class PrewarmerLookAheadTests(IReleaseSpec spec) : PrewarmerHandoffTestBase(spec)
{
    private static readonly IReleaseSpec[] Forks = [Osaka.Instance, Shanghai.Instance];

    private ProcessingLookAhead _queue = null!;

    [SetUp]
    public void EnableLookAhead()
    {
        _queue = new ProcessingLookAhead();
        PreWarmer.EnableLookAhead(_queue, new TestSpecProvider(Spec), depth: 2);
    }

    [Test]
    public void A_queued_block_run_ahead_ends_in_the_state_and_receipts_of_executing_it([Values] bool ownPass)
    {
        Block first = BuildBlock(
            Call(TestItem.PrivateKeyA, 0, Counter),
            Transfer(TestItem.PrivateKeyB, 0, TestItem.AddressC, 1.Wei),
            Transfer(TestItem.PrivateKeyD, 0, TestItem.AddressC, 2.Wei));
        BlockHeader firstHeader = Seal(ref first, Parent);

        // Below three transactions a block gets no pass of its own, and takes only what was run ahead.
        Block second = ownPass
            ? BuildBlock(firstHeader,
                Call(TestItem.PrivateKeyB, 1, Counter),
                Transfer(TestItem.PrivateKeyA, 1, TestItem.AddressC, 3.Wei),
                Transfer(TestItem.PrivateKeyD, 1, TestItem.AddressB, 4.Wei),
                Call(TestItem.PrivateKeyC, 0, Logger))
            : BuildBlock(firstHeader,
                Call(TestItem.PrivateKeyB, 1, Counter),
                Transfer(TestItem.PrivateKeyD, 1, TestItem.AddressB, 4.Wei));
        _queue.Publish(second);

        WarmWithSession(first, Parent);
        Run firstRun = Process(first, ProductionAdapter);
        Assert.That(firstRun.StateRoot, Is.EqualTo(firstHeader.StateRoot));
        PreWarmer.ClearCaches();

        Run executed = Process(second, adapter: null, parent: firstHeader);
        WarmWithSession(second, firstHeader);
        Run run = Process(second, ProductionAdapter, parent: firstHeader);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(run.Results, Is.EqualTo(executed.Results));
            Assert.That(run.StateRoot, Is.EqualTo(executed.StateRoot));
            Assert.That(run.Receipts.Select(r => (r.StatusCode, r.GasUsed, r.GasUsedTotal, r.Logs!.Length)),
                Is.EqualTo(executed.Receipts.Select(r => (r.StatusCode, r.GasUsed, r.GasUsedTotal, r.Logs!.Length))));
            // The counter's slot moved in the first block, so the run ahead that read it cannot be taken.
            Assert.That(run.Tally.Replayed, Is.GreaterThan(0));
            if (!ownPass) Assert.That(run.Tally, Is.EqualTo((1, 1, 0)));
        }
    }

    [Test]
    public void A_block_queued_two_deep_is_run_ahead_on_the_same_state()
    {
        Block first = BuildBlock(
            Transfer(TestItem.PrivateKeyA, 0, TestItem.AddressC, 1.Wei),
            Transfer(TestItem.PrivateKeyB, 0, TestItem.AddressC, 1.Wei),
            Transfer(TestItem.PrivateKeyD, 0, TestItem.AddressC, 1.Wei));
        BlockHeader firstHeader = Seal(ref first, Parent);
        Block second = BuildBlock(firstHeader, Transfer(TestItem.PrivateKeyA, 1, TestItem.AddressB, 1.Wei));
        BlockHeader secondHeader = Seal(ref second, firstHeader);
        Block third = BuildBlock(secondHeader,
            Call(TestItem.PrivateKeyA, 2, Counter),
            Transfer(TestItem.PrivateKeyB, 1, TestItem.AddressD, 1.Wei));
        _queue.Publish(second);
        _queue.Publish(third);

        WarmWithSession(first, Parent);
        Process(first, ProductionAdapter);
        PreWarmer.ClearCaches();
        _queue.Withdraw(second);
        WarmWithSession(second, firstHeader);
        Process(second, ProductionAdapter, parent: firstHeader);
        PreWarmer.ClearCaches();

        Run executed = Process(third, adapter: null, parent: secondHeader);
        WarmWithSession(third, secondHeader);
        Run run = Process(third, ProductionAdapter, parent: secondHeader);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(run.StateRoot, Is.EqualTo(executed.StateRoot));
            Assert.That(run.Results, Is.EqualTo(executed.Results));
            Assert.That(run.Tally, Is.EqualTo((2, 0, 0)));
        }
    }

    [Test]
    public void A_queued_block_whose_reads_the_processed_block_changed_is_executed()
    {
        Block first = BuildBlock(
            Call(TestItem.PrivateKeyA, 0, Counter),
            Call(TestItem.PrivateKeyB, 0, Counter),
            Call(TestItem.PrivateKeyD, 0, Counter));
        BlockHeader firstHeader = Seal(ref first, Parent);
        Block second = BuildBlock(firstHeader, Call(TestItem.PrivateKeyC, 0, Counter));
        _queue.Publish(second);

        WarmWithSession(first, Parent);
        Process(first, ProductionAdapter);
        PreWarmer.ClearCaches();

        Run executed = Process(second, adapter: null, parent: firstHeader);
        WarmWithSession(second, firstHeader);
        Run run = Process(second, ProductionAdapter, parent: firstHeader);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(run.StateRoot, Is.EqualTo(executed.StateRoot));
            Assert.That(run.Tally, Is.EqualTo((0, 1, 0)));
        }
    }

    /// <summary>Executes <paramref name="block"/> and replaces it with the block its processed header heads, as the queue holds it.</summary>
    private BlockHeader Seal(ref Block block, BlockHeader parent)
    {
        BlockHeader header = Processed(block, Process(block, adapter: null, parent: parent).StateRoot);
        block = new Block(header, block.Body);
        return header;
    }

    /// <summary>Warms <paramref name="block"/> on <paramref name="parent"/> the way processing does, and joins the session.</summary>
    private void WarmWithSession(Block block, BlockHeader parent)
    {
        IWorldState worldState = ProcessingScope.Resolve<IWorldState>();
        using (worldState.BeginScope(parent))
        {
            using IDisposable? session = PreWarmer.PreWarmCaches(block, parent, Spec);
            ((PrewarmingSession?)session)?.WaitForCompletion();
        }
    }
}
