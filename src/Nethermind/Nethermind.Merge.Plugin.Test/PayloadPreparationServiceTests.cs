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
    public void ImproveBlock_leaves_the_context_of_a_later_round_alone_when_its_own_round_was_cancelled()
    {
        RecordingBlockImprovementContextFactory factory = new();
        using TestPayloadPreparationService service = CreateService(factory);
        string payloadId = Attributes.GetPayloadId(ParentHeader);

        // A later round owns the storage entry, with a cancellation source of its own.
        SharedCancellationTokenSource laterRound = new(new CancellationTokenSource());
        MockBlockImprovementContext laterContext = new(Build.A.Block.TestObject, DateTimeOffset.UtcNow, laterRound);
        service.Store(payloadId, laterContext);

        // A stale improvement from an earlier round, whose source has already been cancelled.
        SharedCancellationTokenSource earlierRound = new(new CancellationTokenSource());
        earlierRound.CancelAndDispose();
        service.Improve(payloadId, earlierRound);

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
            TimeSpan.FromSeconds(12),
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

        public void Improve(string payloadId, SharedCancellationTokenSource cts) =>
            ImproveBlock(payloadId, ParentHeader, Attributes, Build.A.Block.TestObject, DateTimeOffset.UtcNow, UInt256.Zero, cts);

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
            lock (_contexts)
            {
                bool isFirst = _contexts.Count == 0;
                MockBlockImprovementContext context = new(
                    currentBestBlock,
                    startDateTime,
                    cts,
                    isFirst ? () => OnFirstContextDispose?.Invoke() : null,
                    isFirst ? () => OnFirstContextCancelling?.Invoke() : null);
                _contexts.Add(context);
                return context;
            }
        }
    }
}
