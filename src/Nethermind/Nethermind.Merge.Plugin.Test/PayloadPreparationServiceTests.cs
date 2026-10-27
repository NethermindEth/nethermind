// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Threading;
using Nethermind.Core.Timers;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.BlockProduction;
using Nethermind.Merge.Plugin.BlockProduction.Boost;
using Nethermind.State;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

public class PayloadPreparationServiceTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan TimePerSlot = TimeSpan.FromSeconds(12);
    private static readonly BlockHeader ParentHeader = Build.A.BlockHeader.TestObject;
    private static readonly PayloadAttributes Attributes = new()
    {
        Timestamp = 100,
        PrevRandao = TestItem.KeccakA,
        SuggestedFeeRecipient = TestItem.AddressA
    };

    [Test]
    [CancelAfter(30000)]
    public async Task GetPayload_keeps_returning_the_block_it_handed_out_when_a_late_build_finishes([Values] bool boost)
    {
        Block empty = Build.A.Block.WithNumber(1).TestObject;
        TaskCompletionSource<Block?> build = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using TestPayloadPreparationService service = CreateLateBuildService(boost, empty, build.Task, Substitute.For<IBoostRelay>());
        string payloadId = service.StartPreparingPayload(ParentHeader, Attributes);

        Block? handedOut = (await service.GetPayload(payloadId))!.CurrentBestBlock;
        build.SetResult(FullBlock());
        Assert.That(() => service.Stored(payloadId)!.ImprovementTask.IsCompletedSuccessfully, Is.True.After(5000, 10));
        Block? again = (await service.GetPayload(payloadId))!.CurrentBestBlock;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(handedOut, Is.SameAs(empty));
            Assert.That(again, Is.SameAs(handedOut), "a second getPayload must return the payload the first one committed to");
        }
    }

    [Test]
    [CancelAfter(30000)]
    public async Task Concurrent_getPayloads_return_the_same_block_when_a_late_build_passed_its_disposed_check([Values] bool boost)
    {
        Block empty = Build.A.Block.WithNumber(1).TestObject;
        TaskCompletionSource<Block?> build = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IBoostRelay relay = Substitute.For<IBoostRelay>();
        using TestPayloadPreparationService service = CreateLateBuildService(boost, empty, build.Task, relay);
        string payloadId = service.StartPreparingPayload(ParentHeader, Attributes);
        Assert.That(() => service.Stored(payloadId), Is.Not.Null.After(5000, 10));
        IBlockImprovementContext context = service.Stored(payloadId)!;

        using ManualResetEventSlim buildChecked = new(), firstPinning = new(), resumeBuild = new(), resumeFirst = new();
        int pins = 0;
        SetRaceSeams(context,
            passedDisposedCheck: () =>
            {
                buildChecked.Set();
                resumeBuild.Wait(WaitTimeout);
            },
            pinning: () =>
            {
                if (Interlocked.Increment(ref pins) != 1) return;
                firstPinning.Set();
                resumeFirst.Wait(WaitTimeout);
            });

        // The late build passes its disposed check, then the first getPayload marks the context disposed but has yet to pin.
        build.SetResult(FullBlock());
        Assert.That(buildChecked.Wait(WaitTimeout), Is.True, "build did not reach its CAS");
        Task<IBlockProductionContext?> first = Task.Run(async () => await service.GetPayload(payloadId));
        Assert.That(firstPinning.Wait(WaitTimeout), Is.True, "first getPayload did not start pinning");

        Block? second = (await service.GetPayload(payloadId))!.CurrentBestBlock;
        resumeBuild.Set();
        Block? built = await context.ImprovementTask;
        resumeFirst.Set();
        Block? handedOut = (await first)!.CurrentBestBlock;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(second, Is.SameAs(empty));
            Assert.That(handedOut, Is.SameAs(second), "concurrent getPayloads must return the same payload");
            Assert.That(built, Is.SameAs(empty), "the late build must not become the best block");
            Assert.That(context.Best.CurrentBestBlock, Is.SameAs(empty));
            _ = relay.DidNotReceiveWithAnyArgs().SendPayload(default!, default);
        }
    }

    [Test]
    [CancelAfter(30000)]
    public async Task GetPayload_stops_the_context_before_reading_the_payload_it_hands_out()
    {
        Block handedOut = Build.A.Block.WithNumber(1).TestObject;
        using TestPayloadPreparationService service = CreateService(Substitute.For<IBlockImprovementContextFactory>());
        string payloadId = Attributes.GetPayloadId(ParentHeader);
        service.Store(payloadId, new BuildLandingAfterSecondReadContext(handedOut, Build.A.Block.WithNumber(1).WithTransactions(Build.A.Transaction.TestObject).TestObject));

        Block? first = (await service.GetPayload(payloadId))!.CurrentBestBlock;
        Block? second = (await service.GetPayload(payloadId))!.CurrentBestBlock;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first, Is.SameAs(handedOut));
            Assert.That(second, Is.SameAs(handedOut));
        }
    }

    [Test]
    [CancelAfter(30000)]
    public void GetPayload_disposes_the_replacement_published_after_it_cancelled()
    {
        RecordingBlockImprovementContextFactory factory = new();
        using TestPayloadPreparationService service = CreateService(factory);
        string payloadId = Attributes.GetPayloadId(ParentHeader);

        factory.OnFirstContextDispose = () => Retrieve(service, payloadId);

        service.StartPreparingPayload(ParentHeader, Attributes);

        Assert.That(() => factory.Contexts.Count, Is.EqualTo(2).After(10000, 10));
        Assert.That(() => factory.Contexts[1].Disposed, Is.True.After(5000, 10));
    }

    [Test]
    [CancelAfter(30000)]
    public void GetPayload_disposes_the_replacement_published_before_it_cancelled()
    {
        RecordingBlockImprovementContextFactory factory = new();
        using TestPayloadPreparationService service = CreateService(factory);
        string payloadId = Attributes.GetPayloadId(ParentHeader);
        TaskCompletionSource retrievalCancelling = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource replacementPublished = new(TaskCreationOptions.RunContinuationsAsynchronously);

        factory.OnFirstContextCancelling = () =>
        {
            retrievalCancelling.TrySetResult();
            replacementPublished.Task.Wait(WaitTimeout);
        };
        service.BeforeImprove = improvement =>
        {
            if (improvement > 1) retrievalCancelling.Task.Wait(WaitTimeout);
        };
        service.AfterImprove = improvement =>
        {
            if (improvement > 1) replacementPublished.TrySetResult();
        };

        service.StartPreparingPayload(ParentHeader, Attributes);
        Retrieve(service, payloadId);

        IReadOnlyList<IBlockImprovementContext> contexts = factory.Contexts;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(contexts, Has.Count.EqualTo(2));
            Assert.That(contexts[1].Disposed, Is.True);
            Assert.That(service.Stored(payloadId), Is.SameAs(contexts[0]));
        }
    }

    [Test]
    [CancelAfter(30000)]
    public void ImproveBlock_leaves_the_context_of_a_later_round_alone_when_its_own_round_was_cancelled()
    {
        RecordingBlockImprovementContextFactory factory = new();
        using TestPayloadPreparationService service = CreateService(factory);
        string payloadId = Attributes.GetPayloadId(ParentHeader);

        // A later round owns the storage entry, with a cancellation source of its own.
        (MockBlockImprovementContext laterContext, SharedCancellationTokenSource laterRound) = CreateRound(DateTimeOffset.UtcNow);
        service.Store(payloadId, laterContext);

        // A stale improvement from an earlier round, whose source has already been cancelled.
        SharedCancellationTokenSource earlierRound = new(new CancellationTokenSource());
        earlierRound.CancelAndDispose();
        service.Improve(payloadId, earlierRound);

        AssertLaterRoundUntouched(service, payloadId, laterContext, laterRound);
    }

    [Test]
    [CancelAfter(30000)]
    public void GetPayload_leaves_the_context_of_a_later_round_stored()
    {
        RecordingBlockImprovementContextFactory factory = new();
        using TestPayloadPreparationService service = CreateService(factory);
        string payloadId = Attributes.GetPayloadId(ParentHeader);

        // The entry ages out and a forkchoice request restarts the payload while the retrieval is in flight,
        // so what the retrieval finds stored is a later round rather than its own replacement.
        (MockBlockImprovementContext laterContext, SharedCancellationTokenSource laterRound) = CreateRound(DateTimeOffset.UtcNow + 3 * TimePerSlot);
        (MockBlockImprovementContext retrieved, _) = CreateRound(DateTimeOffset.UtcNow, onCancelling: () => service.Store(payloadId, laterContext));
        service.Store(payloadId, retrieved);

        Retrieve(service, payloadId);

        AssertLaterRoundUntouched(service, payloadId, laterContext, laterRound);
    }

    [Test]
    [CancelAfter(30000)]
    public void ImproveBlock_stores_nothing_when_its_round_was_cancelled_and_the_entry_is_gone()
    {
        RecordingBlockImprovementContextFactory factory = new();
        using TestPayloadPreparationService service = CreateService(factory);
        string payloadId = Attributes.GetPayloadId(ParentHeader);

        // Shutdown or the cleanup timer took the entry away and cancelled the round it belonged to.
        SharedCancellationTokenSource round = new(new CancellationTokenSource());
        round.CancelAndDispose();

        service.Improve(payloadId, round);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(factory.Contexts, Is.Empty, "a dead round starts no build");
            Assert.That(service.Stored(payloadId), Is.Null, "and does not re-add the id it was removed under");
        }
    }

    [Test]
    [CancelAfter(30000)]
    public void ImproveBlock_publishes_the_same_candidate_when_the_entry_moves_under_it()
    {
        RecordingBlockImprovementContextFactory factory = new();
        using TestPayloadPreparationService service = CreateService(factory);
        string payloadId = Attributes.GetPayloadId(ParentHeader);

        (MockBlockImprovementContext current, SharedCancellationTokenSource round) = CreateRound(DateTimeOffset.UtcNow);
        service.Store(payloadId, current);

        // The cleanup timer evicts the entry between the candidate's creation and its publication.
        factory.OnContextCreated = () => service.Remove(payloadId);

        // A round that started before the slot window makes the published candidate's own follow-up
        // improvement skip itself, so what this call leaves behind is what the assertions observe.
        service.Improve(payloadId, round, DateTimeOffset.UtcNow - 2 * TimePerSlot);

        IReadOnlyList<IBlockImprovementContext> contexts = factory.Contexts;
        IBlockImprovementContext? stored = service.Stored(payloadId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(contexts, Has.Count.EqualTo(1), "the candidate is re-offered, not rebuilt and orphaned");
            Assert.That(stored, Is.SameAs(contexts[0]));
            Assert.That(contexts[0].Disposed, Is.False);
        }
    }

    private static (MockBlockImprovementContext Context, SharedCancellationTokenSource Cts) CreateRound(DateTimeOffset startDateTime, Action? onCancelling = null)
    {
        SharedCancellationTokenSource cts = new(new CancellationTokenSource());
        return (new MockBlockImprovementContext(Build.A.Block.TestObject, startDateTime, cts, onCancelling: onCancelling), cts);
    }

    private static void AssertLaterRoundUntouched(TestPayloadPreparationService service, string payloadId, MockBlockImprovementContext laterContext, SharedCancellationTokenSource laterRound)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(service.Stored(payloadId), Is.SameAs(laterContext));
            Assert.That(laterContext.Disposed, Is.False);
            Assert.That(laterRound.IsCancellationRequested, Is.False);
        }
    }

    private static void Retrieve(PayloadPreparationService service, string payloadId) =>
        service.GetPayload(payloadId).AsTask().GetAwaiter().GetResult();

    [TestCase(999UL, 12, TestName = "GetTimePerSlot_BeforeTheFirstChange_IsTheConfiguredLength")]
    [TestCase(1_000UL, 10, TestName = "GetTimePerSlot_FromTheFirstChange_IsItsLength")]
    [TestCase(1_999UL, 10, TestName = "GetTimePerSlot_BeforeTheSecondChange_IsTheFirstLength")]
    [TestCase(2_000UL, 6, TestName = "GetTimePerSlot_FromTheSecondChange_IsItsLength")]
    public void GetTimePerSlot_FollowsThePayloadSlot(ulong payloadTimestamp, int expectedSeconds)
    {
        using PayloadPreparationService sut = new(
            Substitute.For<IBlockProducer>(),
            Substitute.For<ITxPool>(),
            Substitute.For<IBlockImprovementContextFactory>(),
            Substitute.For<ITimerFactory>(),
            LimboLogs.Instance,
            TimeSpan.FromSeconds(12),
            slots: new SlotSchedule(12, null, [(1_000, 10_000), (2_000, 6_000)]));

        Assert.That(sut.GetTimePerSlot(payloadTimestamp), Is.EqualTo(TimeSpan.FromSeconds(expectedSeconds)));
    }

    private static Block FullBlock() =>
        Build.A.Block.WithNumber(1).WithGasUsed(21000).WithTransactions(Build.A.Transaction.TestObject).TestObject;

    /// <summary>A service whose empty block is <paramref name="empty"/> and whose improvement build finishes with <paramref name="build"/>.</summary>
    private static TestPayloadPreparationService CreateLateBuildService(bool boost, Block empty, Task<Block?> build, IBoostRelay relay)
    {
        IBlockProducer producer = Substitute.For<IBlockProducer>();
        producer.BuildBlock(Arg.Any<BlockHeader>(), Arg.Any<IBlockTracer>(), Arg.Any<PayloadAttributes>(), IBlockProducer.Flags.PrepareEmptyBlock, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Block?>(empty));
        // A build past its last cancellation check finishes anyway.
        producer.BuildBlock(Arg.Any<BlockHeader>(), Arg.Any<IBlockTracer>(), Arg.Any<PayloadAttributes>(), IBlockProducer.Flags.None, Arg.Any<CancellationToken>())
            .Returns(build);
        relay.GetPayloadAttributes(Arg.Any<PayloadAttributes>(), Arg.Any<CancellationToken>())
            .Returns(new BoostPayloadAttributes { Timestamp = Attributes.Timestamp, PrevRandao = Attributes.PrevRandao, SuggestedFeeRecipient = Attributes.SuggestedFeeRecipient });
        IBlockImprovementContextFactory factory = boost
            ? new BoostBlockImprovementContextFactory(producer, TimePerSlot, relay, Substitute.For<IStateReader>())
            : new BlockImprovementContextFactory(producer, TimePerSlot);
        return new TestPayloadPreparationService(producer, Substitute.For<ITxPool>(),
            factory, Substitute.For<ITimerFactory>(), LimboLogs.Instance, TimePerSlot, TimeSpan.FromMilliseconds(1));
    }

    private static void SetRaceSeams(IBlockImprovementContext context, Action passedDisposedCheck, Action pinning)
    {
        switch (context)
        {
            case BlockImprovementContext plain:
                plain.PassedDisposedCheck = passedDisposedCheck;
                plain.Pinning = pinning;
                break;
            case BoostBlockImprovementContext boost:
                boost.PassedDisposedCheck = passedDisposedCheck;
                boost.Pinning = pinning;
                break;
            default:
                Assert.Fail($"Unexpected context {context.GetType().Name}");
                break;
        }
    }

    private static TestPayloadPreparationService CreateService(IBlockImprovementContextFactory factory)
    {
        IBlockProducer blockProducer = Substitute.For<IBlockProducer>();
        blockProducer
            .BuildBlock(Arg.Any<BlockHeader>(), Arg.Any<IBlockTracer>(), Arg.Any<PayloadAttributes>(), Arg.Any<IBlockProducer.Flags>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Block?>(Build.A.Block.TestObject));

        ITxPool txPool = Substitute.For<ITxPool>();
        // The improvement loop only rebuilds once new transactions have arrived.
        txPool.PendingTransactionsAdded.Returns(0, 1);

        return new TestPayloadPreparationService(
            blockProducer,
            txPool,
            factory,
            Substitute.For<ITimerFactory>(),
            LimboLogs.Instance,
            TimePerSlot,
            TimeSpan.FromMilliseconds(1));
    }

    private static TestPayloadPreparationService CreateService(IBlockProducer producer) =>
        new(producer, Substitute.For<ITxPool>(), new BlockImprovementContextFactory(producer, TimePerSlot),
            Substitute.For<ITimerFactory>(), LimboLogs.Instance, TimePerSlot, TimeSpan.FromMilliseconds(1));

    private static Task<Block?> BuildCall(IBlockProducer producer, IBlockProducer.Flags flags, Action<CancellationToken>? onToken = null) =>
        producer.BuildBlock(Arg.Any<BlockHeader>(), Arg.Any<IBlockTracer>(), Arg.Any<PayloadAttributes>(), flags,
            onToken is null ? Arg.Any<CancellationToken>() : Arg.Do(onToken));

    [Test]
    [CancelAfter(30000)]
    public async Task StartPreparingPayload_does_not_wait_for_the_empty_block()
    {
        Block empty = Build.A.Block.WithNumber(1).WithTransactions(Build.A.Transaction.TestObject).TestObject;
        TaskCompletionSource<Block?> emptyBuild = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IBlockProducer producer = Substitute.For<IBlockProducer>();
        BuildCall(producer, IBlockProducer.Flags.PrepareEmptyBlock)
            .Returns(emptyBuild.Task);
        BuildCall(producer, IBlockProducer.Flags.None)
            .Returns(new TaskCompletionSource<Block?>(TaskCreationOptions.RunContinuationsAsynchronously).Task);
        using TestPayloadPreparationService service = CreateService(producer);

        try
        {
            string payloadId = await Task.Run(() => service.StartPreparingPayload(ParentHeader, Attributes)).WaitAsync(WaitTimeout);
            ValueTask<IBlockProductionContext?> payload = service.GetPayload(payloadId);
            bool waitedForEmptyBlock = !payload.IsCompleted;
            emptyBuild.SetResult(empty);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(waitedForEmptyBlock, Is.True);
                Assert.That((await payload)?.CurrentBestBlock, Is.SameAs(empty));
            }
        }
        finally
        {
            emptyBuild.TrySetResult(empty);
        }
    }

    [Test]
    [CancelAfter(30000)]
    public async Task Failed_empty_block_still_starts_the_improvement()
    {
        Block improved = Build.A.Block.WithNumber(1).WithTransactions(Build.A.Transaction.TestObject).TestObject;
        IBlockProducer producer = Substitute.For<IBlockProducer>();
        BuildCall(producer, IBlockProducer.Flags.PrepareEmptyBlock)
            .Returns(Task.FromException<Block?>(new InvalidOperationException()));
        BuildCall(producer, IBlockProducer.Flags.None)
            .Returns(improved);
        using TestPayloadPreparationService service = CreateService(producer);

        string payloadId = service.StartPreparingPayload(ParentHeader, Attributes);

        Assert.That((await service.GetPayload(payloadId, skipCancel: true))?.CurrentBestBlock, Is.SameAs(improved));
    }

    [Test]
    [CancelAfter(30000)]
    public async Task Cancel_or_dispose_stops_a_build_whose_empty_block_is_pending([Values] bool dispose)
    {
        TaskCompletionSource<Block?> emptyBuild = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<CancellationToken> emptyBuildToken = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IBlockProducer producer = Substitute.For<IBlockProducer>();
        BuildCall(producer, IBlockProducer.Flags.PrepareEmptyBlock, t => emptyBuildToken.TrySetResult(t))
            .Returns(emptyBuild.Task);
        using TestPayloadPreparationService service = CreateService(producer);

        string payloadId = service.StartPreparingPayload(ParentHeader, Attributes);
        CancellationToken token = await emptyBuildToken.Task.WaitAsync(WaitTimeout);
        if (dispose) service.Dispose();
        else service.CancelBlockProduction(payloadId);
        bool emptyBuildCancelled = token.IsCancellationRequested;
        emptyBuild.SetResult(Build.A.Block.WithNumber(1).TestObject);
        IBlockProductionContext? payload = await service.GetPayload(payloadId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(emptyBuildCancelled, Is.True);
            Assert.That(payload, Is.Null);
            Assert.That(service.Stored(payloadId), Is.Null);
            _ = BuildCall(producer.DidNotReceive(), IBlockProducer.Flags.None);
        }
    }

    [Test]
    [CancelAfter(30000)]
    public async Task Dispose_before_publishing_the_context_leaves_nothing_stored()
    {
        RecordingBlockImprovementContextFactory factory = new();
        using TestPayloadPreparationService service = CreateService(factory);
        factory.OnContextCreated = () => service.Dispose();

        string payloadId = service.StartPreparingPayload(ParentHeader, Attributes);
        await service.GetPayload(payloadId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(factory.Contexts, Has.Count.EqualTo(1));
            Assert.That(service.Stored(payloadId), Is.Null);
        }
    }

    [Test]
    [CancelAfter(30000)]
    public async Task StartPreparingPayload_after_dispose_returns_an_id_without_a_payload([Values] bool disposedAfterShutdownRead)
    {
        IBlockProducer producer = Substitute.For<IBlockProducer>();
        producer.BuildBlock(Arg.Any<BlockHeader>(), Arg.Any<IBlockTracer>(), Arg.Any<PayloadAttributes>(), Arg.Any<IBlockProducer.Flags>(), Arg.Any<CancellationToken>())
            .Returns(Build.A.Block.WithNumber(1).TestObject);
        using TestPayloadPreparationService service = CreateService(producer);
        FieldInfo shutdownField = typeof(PayloadPreparationService).GetField("_shutdown", BindingFlags.NonPublic | BindingFlags.Instance)!;
        CancellationTokenSource shutdown = (CancellationTokenSource)shutdownField.GetValue(service)!;
        if (disposedAfterShutdownRead)
        {
            // Dispose ran after StartPreparingPayload read the source, so the source is still visible but disposed.
            shutdown.Cancel();
            shutdown.Dispose();
        }
        else
        {
            service.Dispose();
        }

        string payloadId = service.StartPreparingPayload(ParentHeader, Attributes);
        shutdownField.SetValue(service, null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await service.GetPayload(payloadId), Is.Null);
            _ = BuildCall(producer.DidNotReceive(), IBlockProducer.Flags.None);
        }
    }

    [Test]
    [CancelAfter(30000)]
    public async Task StartPreparingPayload_does_not_build_a_published_payload_again()
    {
        IBlockProducer producer = Substitute.For<IBlockProducer>();
        BuildCall(producer, IBlockProducer.Flags.PrepareEmptyBlock)
            .Returns(Build.A.Block.WithNumber(1).TestObject);
        BuildCall(producer, IBlockProducer.Flags.None)
            .Returns(new TaskCompletionSource<Block?>(TaskCreationOptions.RunContinuationsAsynchronously).Task);
        using TestPayloadPreparationService service = CreateService(producer);

        string payloadId = service.StartPreparingPayload(ParentHeader, Attributes);
        Assert.That(() => service.Stored(payloadId), Is.Not.Null.After(5000, 10));
        service.StartPreparingPayload(ParentHeader, Attributes);
        await service.GetPayload(payloadId);

        _ = BuildCall(producer.Received(1), IBlockProducer.Flags.PrepareEmptyBlock);
    }

    [Test]
    [CancelAfter(30000)]
    public async Task GetPayload_waits_for_the_improvement_when_there_is_no_empty_block([Values] bool skipCancel)
    {
        using TestPayloadPreparationService service = CreateService(new RecordingBlockImprovementContextFactory());
        TaskCompletionSource<Block?> improvement = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Block improved = Build.A.Block.WithTransactions(Build.A.Transaction.TestObject).TestObject;
        BlockProductionSnapshot best = new(null, UInt256.Zero);
        IBlockImprovementContext context = Substitute.For<IBlockImprovementContext>();
        context.Best.Returns(_ => best);
        context.ImprovementTask.Returns(improvement.Task);
        // Cancelling would fail the build, as it does when the improvement is waiting on the producer lock.
        context.When(c => c.CancelOngoingImprovements()).Do(_ => improvement.TrySetCanceled());
        service.Store("payload", context);

        ValueTask<IBlockProductionContext?> payload = service.GetPayload("payload", skipCancel);
        bool returnedEarly = payload.IsCompleted;
        best = new(improved, UInt256.One);
        improvement.TrySetResult(improved);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(returnedEarly, Is.False);
            Assert.That((await payload)?.CurrentBestBlock, Is.SameAs(improved));
        }
    }

    [Test]
    [CancelAfter(30000)]
    public async Task GetPayload_keeps_a_block_finished_after_the_missing_block_wait_times_out()
    {
        using TestPayloadPreparationService service = CreateService(new RecordingBlockImprovementContextFactory());
        TaskCompletionSource<Block?> improvement = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Block partial = Build.A.Block.WithTransactions(Build.A.Transaction.TestObject).TestObject;
        BlockProductionSnapshot best = new(null, UInt256.Zero);
        bool disposed = false;
        IBlockImprovementContext context = Substitute.For<IBlockImprovementContext>();
        context.Best.Returns(_ => best);
        context.ImprovementTask.Returns(improvement.Task);
        context.When(c => c.Dispose()).Do(_ => Volatile.Write(ref disposed, true));
        context.When(c => c.DisposeAndCancelOngoingImprovements()).Do(_ => Volatile.Write(ref disposed, true));
        // The build only finishes once cancelled, and like TrySetBest it is rejected once the context is disposed.
        context.When(c => c.CancelOngoingImprovements()).Do(_ => Task.Run(() =>
        {
            if (!Volatile.Read(ref disposed)) best = new(partial, UInt256.One);
            improvement.TrySetResult(partial);
        }));
        service.Store("payload", context);

        IBlockProductionContext? payload = await service.GetPayload("payload");

        Assert.That(payload?.CurrentBestBlock, Is.SameAs(partial));
    }

    [Test]
    [CancelAfter(30000)]
    public void CancelBlockProduction_does_not_wait_for_a_missing_block()
    {
        using TestPayloadPreparationService service = CreateService(new RecordingBlockImprovementContextFactory());
        TaskCompletionSource<Block?> improvement = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool disposed = false;
        IBlockImprovementContext context = Substitute.For<IBlockImprovementContext>();
        context.Best.Returns(new BlockProductionSnapshot(null, UInt256.Zero));
        context.ImprovementTask.Returns(improvement.Task);
        context.When(c => c.Dispose()).Do(_ => disposed = true);
        context.When(c => c.DisposeAndCancelOngoingImprovements()).Do(_ => disposed = true);
        service.Store("payload", context);

        service.CancelBlockProduction("payload");

        Assert.That(disposed, Is.True);
    }

    private sealed class TestPayloadPreparationService(
        IBlockProducer blockProducer,
        ITxPool txPool,
        IBlockImprovementContextFactory blockImprovementContextFactory,
        ITimerFactory timerFactory,
        ILogManager logManager,
        TimeSpan timePerSlot,
        TimeSpan improvementDelay)
        : PayloadPreparationService(blockProducer, txPool, blockImprovementContextFactory, timerFactory, logManager, timePerSlot, improvementDelay: improvementDelay)
    {
        private int _improvements;

        public Action<int>? BeforeImprove { get; set; }
        public Action<int>? AfterImprove { get; set; }

        public IBlockImprovementContext? Stored(string payloadId) =>
            _payloadStorage.TryGetValue(payloadId, out IBlockImprovementContext? context) ? context : null;

        public void Store(string payloadId, IBlockImprovementContext context) => _payloadStorage[payloadId] = context;

        public void Remove(string payloadId) => _payloadStorage.TryRemove(payloadId, out _);

        public void Improve(string payloadId, SharedCancellationTokenSource cts, DateTimeOffset? startDateTime = null) =>
            ImproveBlock(payloadId, ParentHeader, Attributes, Build.A.Block.TestObject, startDateTime ?? DateTimeOffset.UtcNow, UInt256.Zero, cts);

        protected override void ImproveBlock(string payloadId, BlockHeader parentHeader, PayloadAttributes payloadAttributes, Block? currentBestBlock, DateTimeOffset startDateTime, UInt256 currentBlockFees, SharedCancellationTokenSource cts)
        {
            int improvement = Interlocked.Increment(ref _improvements);
            BeforeImprove?.Invoke(improvement);
            base.ImproveBlock(payloadId, parentHeader, payloadAttributes, currentBestBlock, startDateTime, currentBlockFees, cts);
            AfterImprove?.Invoke(improvement);
        }
    }

    /// <summary>
    /// A context whose build lands, unless disposed, right after <see cref="Best"/> is read for the second time:
    /// the read getPayload hands out when the build has completed.
    /// </summary>
    private sealed class BuildLandingAfterSecondReadContext(Block current, Block late) : IBlockImprovementContext
    {
        private BlockProductionSnapshot _best = new(current, UInt256.Zero);
        private int _reads;

        public BlockProductionSnapshot Best
        {
            get
            {
                BlockProductionSnapshot best = _best;
                if (++_reads == 2 && !Disposed) _best = new(late, UInt256.Zero);
                return best;
            }
        }

        public Task<Block?> ImprovementTask { get; } = Task.FromResult<Block?>(current);
        public bool Disposed { get; private set; }
        public DateTimeOffset StartDateTime { get; } = DateTimeOffset.UtcNow;
        public void CancelOngoingImprovements() { }
        public void Dispose() => Disposed = true;
    }

    private sealed class RecordingBlockImprovementContextFactory : IBlockImprovementContextFactory
    {
        private readonly List<IBlockImprovementContext> _contexts = [];

        public Action? OnFirstContextDispose { get; set; }
        public Action? OnFirstContextCancelling { get; set; }

        /// <summary>Invoked once a context has been created, before the service gets a chance to publish it.</summary>
        public Action? OnContextCreated { get; set; }

        public IReadOnlyList<IBlockImprovementContext> Contexts
        {
            get
            {
                lock (_contexts)
                {
                    return _contexts.ToArray();
                }
            }
        }

        public IBlockImprovementContext StartBlockImprovementContext(Block? currentBestBlock, BlockHeader parentHeader, PayloadAttributes payloadAttributes, DateTimeOffset startDateTime, UInt256 currentBlockFees, SharedCancellationTokenSource cts)
        {
            MockBlockImprovementContext context;
            lock (_contexts)
            {
                bool isFirst = _contexts.Count == 0;
                context = new(
                    currentBestBlock,
                    startDateTime,
                    cts,
                    isFirst ? () => OnFirstContextDispose?.Invoke() : null,
                    isFirst ? () => OnFirstContextCancelling?.Invoke() : null);
                _contexts.Add(context);
            }

            OnContextCreated?.Invoke();
            return context;
        }
    }
}
