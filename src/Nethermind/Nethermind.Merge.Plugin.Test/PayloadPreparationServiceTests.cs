// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
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

        protected override void ImproveBlock(string payloadId, BlockHeader parentHeader, PayloadAttributes payloadAttributes, Block currentBestBlock, DateTimeOffset startDateTime, UInt256 currentBlockFees, SharedCancellationTokenSource cts)
        {
            int improvement = Interlocked.Increment(ref _improvements);
            BeforeImprove?.Invoke(improvement);
            base.ImproveBlock(payloadId, parentHeader, payloadAttributes, currentBestBlock, startDateTime, currentBlockFees, cts);
            AfterImprove?.Invoke(improvement);
        }
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

        public IBlockImprovementContext StartBlockImprovementContext(Block currentBestBlock, BlockHeader parentHeader, PayloadAttributes payloadAttributes, DateTimeOffset startDateTime, UInt256 currentBlockFees, SharedCancellationTokenSource cts)
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
