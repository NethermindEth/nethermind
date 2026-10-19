// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Threading;
using Nethermind.Logging;
using Nethermind.Shutter.Config;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Shutter.Test;

[TestFixture]
public class ShutterBlockImprovementContextTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    [Test]
    [NonParallelizable]
    public async Task Counts_every_missed_key_when_contexts_time_out_concurrently()
    {
        const int contexts = 10_000;
        const ulong slotTimestamp = ShutterTestsCommon.InitialSlotTimestamp;

        IBlockProducer blockProducer = Substitute.For<IBlockProducer>();
        blockProducer.BuildBlock(default, default, default, default, default).ReturnsForAnyArgs(Task.FromResult<Block?>(null));
        SlotTime slotTime = new(0, new Timestamper(DateTimeOffset.FromUnixTimeSeconds((long)slotTimestamp).UtcDateTime), ShutterTestsCommon.SlotLength, TimeSpan.Zero);
        Block block = Build.A.Block.TestObject;
        PayloadAttributes payloadAttributes = new() { Timestamp = slotTimestamp };

        ulong missedBefore = Metrics.ShutterKeysMissed;

        ShutterBlockImprovementContext[] improvements = Enumerable.Range(0, contexts).Select(_ =>
        {
            CancellationTokenSource cts = new();
            cts.Cancel();
            return new ShutterBlockImprovementContext(
                blockProducer,
                Substitute.For<IShutterTxSignal>(),
                new ShutterConfig(),
                slotTime,
                block,
                block.Header,
                payloadAttributes,
                DateTimeOffset.UtcNow,
                ShutterTestsCommon.SlotLength,
                LimboLogs.Instance,
                new SharedCancellationTokenSource(cts));
        }).ToArray();

        await Task.WhenAll(improvements.Select(static i => i.ImprovementTask));
        foreach (ShutterBlockImprovementContext improvement in improvements) improvement.Dispose();

        Assert.That(Metrics.ShutterKeysMissed - missedBefore, Is.EqualTo((ulong)contexts));
    }

    [Test]
    [CancelAfter(30000)]
    public async Task Build_finishing_after_dispose_does_not_replace_the_best_block()
    {
        Block empty = Build.A.Block.WithNumber(1).TestObject;
        TaskCompletionSource<Block?> build = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ShutterBlockImprovementContext context = CreateLateBuildContext(empty, build.Task);

        ((IBlockImprovementContext)context).DisposeAndCancelOngoingImprovements();
        build.SetResult(FullBlock());
        await context.ImprovementTask;

        Assert.That(context.Best.CurrentBestBlock, Is.SameAs(empty));
    }

    [Test]
    [CancelAfter(30000)]
    public async Task Concurrent_disposers_read_the_same_block_when_a_late_build_passed_its_disposed_check()
    {
        Block empty = Build.A.Block.WithNumber(1).TestObject;
        TaskCompletionSource<Block?> build = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ShutterBlockImprovementContext context = CreateLateBuildContext(empty, build.Task);

        using ManualResetEventSlim buildChecked = new(), firstPinning = new(), resumeBuild = new(), resumeFirst = new();
        int pins = 0;
        context.PassedDisposedCheck = () =>
        {
            buildChecked.Set();
            resumeBuild.Wait(WaitTimeout);
        };
        context.Pinning = () =>
        {
            if (Interlocked.Increment(ref pins) != 1) return;
            firstPinning.Set();
            resumeFirst.Wait(WaitTimeout);
        };

        // The late build passes its disposed check, then the first disposer marks the context disposed but has yet to pin.
        build.SetResult(FullBlock());
        Assert.That(buildChecked.Wait(WaitTimeout), Is.True, "build did not reach its CAS");
        Task<Block?> first = Task.Run(() => HandOut(context));
        Assert.That(firstPinning.Wait(WaitTimeout), Is.True, "first disposer did not start pinning");

        Block? second = HandOut(context);
        resumeBuild.Set();
        Block? built = await context.ImprovementTask;
        resumeFirst.Set();
        Block? handedOut = await first;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(second, Is.SameAs(empty));
            Assert.That(handedOut, Is.SameAs(second), "concurrent disposers must read the same payload");
            Assert.That(built, Is.SameAs(empty), "the late build must not become the best block");
            Assert.That(context.Best.CurrentBestBlock, Is.SameAs(empty));
        }
    }

    // What getPayload does: stop the context, then read the payload it hands out.
    private static Block? HandOut(ShutterBlockImprovementContext context)
    {
        ((IBlockImprovementContext)context).DisposeAndCancelOngoingImprovements();
        return context.Best.CurrentBestBlock;
    }

    private static Block FullBlock() => Build.A.Block.WithNumber(1).WithTransactions(Build.A.Transaction.TestObject).TestObject;

    private static ShutterBlockImprovementContext CreateLateBuildContext(Block empty, Task<Block?> build)
    {
        const ulong slotTimestamp = ShutterTestsCommon.InitialSlotTimestamp;
        IBlockProducer blockProducer = Substitute.For<IBlockProducer>();
        blockProducer.BuildBlock(default, default, default, default, default).ReturnsForAnyArgs(build);
        IShutterTxSignal txSignal = Substitute.For<IShutterTxSignal>();
        txSignal.HaveTransactionsArrived(Arg.Any<ulong>()).Returns(true);
        SlotTime slotTime = new(0, new Timestamper(DateTimeOffset.FromUnixTimeSeconds((long)slotTimestamp).UtcDateTime), ShutterTestsCommon.SlotLength, TimeSpan.Zero);

        return new ShutterBlockImprovementContext(
            blockProducer,
            txSignal,
            new ShutterConfig(),
            slotTime,
            empty,
            empty.Header,
            new PayloadAttributes { Timestamp = slotTimestamp },
            DateTimeOffset.UtcNow,
            ShutterTestsCommon.SlotLength,
            LimboLogs.Instance,
            new SharedCancellationTokenSource(new CancellationTokenSource()));
    }
}
