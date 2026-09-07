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
    [Test]
    [CancelAfter(30000)]
    public void GetPayload_disposes_the_improvement_context_that_replaced_the_retrieved_one()
    {
        Block emptyBlock = Build.A.Block.TestObject;
        IBlockProducer blockProducer = Substitute.For<IBlockProducer>();
        blockProducer
            .BuildBlock(Arg.Any<BlockHeader>(), Arg.Any<IBlockTracer>(), Arg.Any<PayloadAttributes>(), Arg.Any<IBlockProducer.Flags>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Block?>(emptyBlock));

        ITxPool txPool = Substitute.For<ITxPool>();
        txPool.PendingTransactionsAdded.Returns(0, 1);

        RaceReproducingContextFactory improvementContextFactory = new();
        using PayloadPreparationService service = new(
            blockProducer,
            txPool,
            improvementContextFactory,
            Substitute.For<ITimerFactory>(),
            LimboLogs.Instance,
            TimeSpan.FromSeconds(12),
            improvementDelay: TimeSpan.FromMilliseconds(1));

        BlockHeader parentHeader = Build.A.BlockHeader.TestObject;
        PayloadAttributes payloadAttributes = new() { Timestamp = 100, PrevRandao = TestItem.KeccakA, SuggestedFeeRecipient = TestItem.AddressA };
        string payloadId = payloadAttributes.GetPayloadId(parentHeader);

        improvementContextFactory.RetrievePayloadOnFirstDispose(() => service.GetPayload(payloadId).AsTask().GetAwaiter().GetResult());

        service.StartPreparingPayload(parentHeader, payloadAttributes);

        Assert.That(() => improvementContextFactory.Contexts.Count, Is.EqualTo(2).After(10000, 10));
        Assert.That(() => improvementContextFactory.Contexts[1].Disposed, Is.True.After(5000, 10));
    }

    private sealed class RaceReproducingContextFactory : IBlockImprovementContextFactory
    {
        private readonly List<TestBlockImprovementContext> _contexts = [];
        private Action? _onFirstDispose;

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

        public void RetrievePayloadOnFirstDispose(Action retrievePayload) => _onFirstDispose = retrievePayload;

        public IBlockImprovementContext StartBlockImprovementContext(Block currentBestBlock, BlockHeader parentHeader, PayloadAttributes payloadAttributes, DateTimeOffset startDateTime, UInt256 currentBlockFees, SharedCancellationTokenSource cts)
        {
            lock (_contexts)
            {
                TestBlockImprovementContext context = new(currentBestBlock, startDateTime, cts, _contexts.Count == 0 ? () => _onFirstDispose?.Invoke() : null);
                _contexts.Add(context);
                return context;
            }
        }
    }

    private sealed class TestBlockImprovementContext(Block currentBestBlock, DateTimeOffset startDateTime, SharedCancellationTokenSource cts, Action? onFirstDispose) : IBlockImprovementContext
    {
        private int _disposeCount;

        public Task<Block?> ImprovementTask { get; } = Task.FromResult<Block?>(currentBestBlock);
        public Block? CurrentBestBlock { get; } = currentBestBlock;
        public UInt256 BlockFees { get; }
        public bool Disposed { get; private set; }
        public DateTimeOffset StartDateTime { get; } = startDateTime;

        public void CancelOngoingImprovements() => cts.CancelAndDispose();

        public void Dispose()
        {
            Disposed = true;
            if (Interlocked.Increment(ref _disposeCount) == 1)
            {
                onFirstDispose?.Invoke();
            }
        }
    }
}
