// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Tracing;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.Tracing;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.Tracing;

public class BlockchainProcessorFacadeTests
{
    /// <remarks>
    /// Tracers read the sender when a transaction trace starts, before the transaction processor would recover it, so
    /// preprocessing may leave the senders only to a processor that recovers them before it runs each transaction.
    /// </remarks>
    [TestCase(false, true, false)]
    [TestCase(true, false, false)]
    [TestCase(true, true, true)]
    public void Leaves_senders_to_the_processor_only_when_it_recovers_them_for_a_bounded_replay(
        bool processorRecoversSenders, bool bounded, bool expectDeferred)
    {
        Block block = Build.A.Block.WithTransactions(Build.A.Transaction.SignedAndResolved().TestObject).TestObject;
        IBlockProcessor processor = Substitute.For<IBlockProcessor>();
        processor.RecoversBoundedTraceSenders.Returns(processorRecoversSenders);
        RecordingStep step = new();
        BlockchainProcessorFacade facade = new(processor, Substitute.For<ISpecProvider>(), [step]);
        IBlockTracer tracer = Substitute.For<IBlockTracer>();
        IBlockTracer traced = bounded ? TransactionTraceBoundary.Wrap(tracer, block.Transactions[0].Hash!) : tracer;

        facade.Process(block, TraceProcessingOptions.ReadOnlyReplay, traced);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(step.Full, Is.EqualTo(expectDeferred ? 0 : 1));
            Assert.That(step.ForTransactionTrace, Is.EqualTo(expectDeferred ? 1 : 0));
        }
    }

    private sealed class RecordingStep : IBlockPreprocessorStep
    {
        public int Full { get; private set; }
        public int ForTransactionTrace { get; private set; }

        public void RecoverData(Block block) => Full++;

        public void RecoverDataForTransactionTrace(Block block) => ForTransactionTrace++;
    }
}
