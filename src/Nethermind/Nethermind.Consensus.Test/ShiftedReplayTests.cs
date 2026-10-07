// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Autofac;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Consensus.Test;

[TestFixtureSource(nameof(Forks))]
public class ShiftedReplayTests(IReleaseSpec spec) : PrewarmerHandoffTestBase(spec)
{
    private static readonly IReleaseSpec[] Forks = [Osaka.Instance, Shanghai.Instance];

    [Test]
    public void Runs_bumping_one_counter_replay_shifted_into_the_state_and_receipts_of_executing_them()
    {
        // The counter starts above zero, so every bump costs what the warm runs paid.
        Block first = BuildBlock(Call(TestItem.PrivateKeyA, 0, Counter));
        BlockHeader firstHeader = Seal(ref first, Parent);
        Block second = BuildBlock(firstHeader,
            Call(TestItem.PrivateKeyB, 0, Counter),
            Call(TestItem.PrivateKeyC, 0, Counter),
            Call(TestItem.PrivateKeyD, 0, Counter),
            Call(TestItem.PrivateKeyA, 1, Counter));

        Run executed = Process(second, adapter: null, parent: firstHeader);
        WarmWithSession(second, firstHeader);
        (Run run, int shifted) = ProcessShifted(second, firstHeader);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(run.StateRoot, Is.EqualTo(executed.StateRoot));
            Assert.That(run.Results, Is.EqualTo(executed.Results));
            Assert.That(run.Receipts.Select(static r => (r.StatusCode, r.GasUsed, r.GasUsedTotal)),
                Is.EqualTo(executed.Receipts.Select(static r => (r.StatusCode, r.GasUsed, r.GasUsedTotal))));
            Assert.That(run.Tally, Is.EqualTo((4, 0, 0)));
            Assert.That(shifted, Is.EqualTo(3));
        }
    }

    [Test]
    public void Without_shifting_the_same_runs_are_rejected()
    {
        Block first = BuildBlock(Call(TestItem.PrivateKeyA, 0, Counter));
        BlockHeader firstHeader = Seal(ref first, Parent);
        Block second = BuildBlock(firstHeader,
            Call(TestItem.PrivateKeyB, 0, Counter),
            Call(TestItem.PrivateKeyC, 0, Counter),
            Call(TestItem.PrivateKeyD, 0, Counter));

        WarmWithSession(second, firstHeader);
        Run run = Process(second, ProductionAdapter, parent: firstHeader);

        Assert.That(run.Tally, Is.EqualTo((1, 2, 0)));
    }

    [Test]
    public void A_counter_starting_at_zero_replays_shifted_into_other_roots_which_only_the_header_can_tell()
    {
        // The first bump sets the slot from zero, which costs more than the bumps the stale runs are shifted into.
        Block block = BuildBlock(
            Call(TestItem.PrivateKeyB, 0, Counter),
            Call(TestItem.PrivateKeyC, 0, Counter),
            Call(TestItem.PrivateKeyD, 0, Counter));

        Run executed = Process(block, adapter: null);
        WarmWithSession(block, Parent);
        (Run run, int shifted) = ProcessShifted(block, Parent);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(shifted, Is.EqualTo(2));
            Assert.That(run.StateRoot, Is.Not.EqualTo(executed.StateRoot));
        }
    }

    [Test]
    public void A_run_ahead_that_reverted_on_a_stale_nonce_is_not_shifted()
    {
        // Run ahead on the first block's parent, the guard sees the nonce before the first block bumped it and reverts; it
        // flagged the slot written, but the revert left nothing to shift, and the real call succeeds.
        Block first = BuildBlock(
            Call(TestItem.PrivateKeyA, 0, NonceGuard, data: Word(0)),
            Transfer(TestItem.PrivateKeyB, 0, TestItem.AddressC, 1.Wei),
            Transfer(TestItem.PrivateKeyD, 0, TestItem.AddressC, 1.Wei));
        BlockHeader firstHeader = Seal(ref first, Parent);
        Block second = BuildBlock(firstHeader, Call(TestItem.PrivateKeyC, 0, NonceGuard, data: Word(1)));
        ProcessingLookAhead queue = new();
        PreWarmer.EnableLookAhead(queue, new TestSpecProvider(Spec), depth: 1);
        queue.Publish(second);

        WarmWithSession(first, Parent);
        Process(first, ProductionAdapter);
        PreWarmer.ClearCaches();

        Run executed = Process(second, adapter: null, parent: firstHeader);
        WarmWithSession(second, firstHeader);
        (Run run, int shifted) = ProcessShifted(second, firstHeader);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(executed.Receipts[0].StatusCode, Is.EqualTo(StatusCode.Success));
            Assert.That(run.Receipts[0].StatusCode, Is.EqualTo(StatusCode.Success));
            Assert.That(run.Receipts[0].GasUsed, Is.EqualTo(executed.Receipts[0].GasUsed));
            Assert.That(run.StateRoot, Is.EqualTo(executed.StateRoot));
            Assert.That(shifted, Is.Zero);
            Assert.That(run.Tally, Is.EqualTo((0, 1, 0)));
        }
    }

    private static byte[] Word(ulong value) => new UInt256(value).ToBigEndian();

    private (Run Run, int Shifted) ProcessShifted(Block block, BlockHeader parent)
    {
        ShiftedReplay.Allowed = true;
        ShiftedReplay.Used = 0;
        try
        {
            Run run = Process(block, ProductionAdapter, parent: parent);
            return (run, ShiftedReplay.Used);
        }
        finally
        {
            ShiftedReplay.Allowed = false;
        }
    }

    private BlockHeader Seal(ref Block block, BlockHeader parent)
    {
        BlockHeader header = Processed(block, Process(block, adapter: null, parent: parent).StateRoot);
        block = new Block(header, block.Body);
        return header;
    }

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

public class ShiftedReplayRetryTests
{
    [Test]
    public void A_block_whose_roots_reject_shifted_replays_is_processed_again_without_them_and_the_next_blocks_cool_down()
    {
        BlockHeader parent = Build.A.BlockHeader.WithNumber(0).TestObject;
        Block first = Build.A.Block.WithNumber(1).WithParent(parent).TestObject;
        Block second = Build.A.Block.WithNumber(2).WithParent(first.Header).TestObject;

        IWorldState state = Substitute.For<IWorldState>();
        int scopesOpened = 0;
        state.TryBeginScopeAtTarget(Arg.Any<BlockHeader>(), out Arg.Any<IDisposable?>()).Returns(call =>
        {
            scopesOpened++;
            call[1] = Substitute.For<IDisposable>();
            return true;
        });

        List<(ulong Number, ProcessingOptions Options)> attempts = [];
        IBlockProcessor blockProcessor = Substitute.For<IBlockProcessor>();
        blockProcessor.ProcessOne(Arg.Any<Block>(), Arg.Any<ProcessingOptions>(), Arg.Any<IBlockTracer>(), Arg.Any<IReleaseSpec>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                Block block = call.ArgAt<Block>(0);
                ProcessingOptions options = call.ArgAt<ProcessingOptions>(1);
                attempts.Add((block.Number, options));
                if (block.Number == 1 && options.ContainsFlag(ProcessingOptions.ShiftedReplay)) throw new ShiftedReplayRetryException(block);
                return (block, Array.Empty<TxReceipt>());
            });

        BranchProcessor branchProcessor = new(blockProcessor, new TestSpecProvider(Osaka.Instance), state, Substitute.For<IBlockhashProvider>(),
            Substitute.For<IInclusionListSatisfactionChecker>(), LimboLogs.Instance);

        Block[] processed = branchProcessor.Process(parent, [first, second], ProcessingOptions.ShiftedReplay | ProcessingOptions.StoreReceipts, NullBlockTracer.Instance);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(processed, Has.Length.EqualTo(2));
            Assert.That(attempts, Is.EqualTo(new[]
            {
                (1UL, ProcessingOptions.ShiftedReplay | ProcessingOptions.StoreReceipts),
                (1UL, ProcessingOptions.StoreReceipts),
                (2UL, ProcessingOptions.StoreReceipts)
            }));
            // The rejected attempt's state is dropped: the block is processed again on a scope of its own.
            Assert.That(scopesOpened, Is.EqualTo(2));
        }
    }
}
