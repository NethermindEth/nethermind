// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
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
    /// preprocessing may leave the senders only to an executor that recovers them before it runs each transaction.
    /// </remarks>
    [TestCase(false, true, false)]
    [TestCase(true, false, false)]
    [TestCase(true, true, true)]
    public void Leaves_senders_to_the_executor_only_when_it_recovers_them_for_a_bounded_replay(
        bool executorRecoversSenders, bool bounded, bool expectDeferred)
    {
        RecordingStep step = new();
        BlockchainProcessorFacade facade = new(Substitute.For<IBlockProcessor>(), Substitute.For<ISpecProvider>(), [step], executorRecoversSenders);

        Process(facade, bounded);

        AssertRecovery(step, expectDeferred);
    }

    [Test]
    public void Public_constructor_recovers_every_sender_of_a_bounded_replay()
    {
        RecordingStep step = new();
        BlockchainProcessorFacade facade = new(Substitute.For<IBlockProcessor>(), Substitute.For<ISpecProvider>(), [step]);

        Process(facade, bounded: true);

        AssertRecovery(step, expectDeferred: false);
    }

    /// <remarks>
    /// A replay that loads nonces from state writes them into the transactions it runs, with the senders it recovers and
    /// the gas they spend, so it runs on copies, made before the preprocessor steps, unless the caller already made them.
    /// A run that keeps the signed nonces runs the caller's transactions.
    /// </remarks>
    [TestCase(TraceProcessingOptions.ReadOnlyReplay, false, true)]
    [TestCase(TraceProcessingOptions.ReadOnlyReplay, true, false)]
    [TestCase(TraceProcessingOptions.ReadOnlySigned, false, false)]
    public void Replay_that_loads_nonces_from_state_runs_on_copies_unless_the_caller_owns_them(
        ProcessingOptions options, bool ownsTransactions, bool expectCopies)
    {
        Transaction[] originals =
        [
            Build.A.NamedTransaction("first").WithNonce(7).SignedAndResolved(TestItem.PrivateKeyA).TestObject,
            Build.A.NamedTransaction("second").WithNonce(8).SignedAndResolved(TestItem.PrivateKeyA).TestObject,
        ];
        Block block = Build.A.Block.WithTransactions(originals).TestObject;
        Block processed = null;
        IBlockProcessor blockProcessor = Substitute.For<IBlockProcessor>();
        blockProcessor.ProcessOne(Arg.Do<Block>(b => processed = b), Arg.Any<ProcessingOptions>(), Arg.Any<IBlockTracer>(), Arg.Any<IReleaseSpec>(), Arg.Any<CancellationToken>())
            .Returns(default((Block, TxReceipt[])));
        RecordingStep step = new();
        BlockchainProcessorFacade facade = new(blockProcessor, Substitute.For<ISpecProvider>(), [step]);

        facade.Process(block, options, Substitute.For<IBlockTracer>(), ownsTransactions, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(processed, Is.Not.Null);
            Assert.That(step.Recovered, Is.SameAs(processed), "the preprocessor steps recover senders on the block the processor runs");
            Assert.That(block.Transactions, Is.EqualTo(originals).Using<Transaction>(ReferenceEquals), "the caller's block keeps its own transactions");
            for (int i = 0; i < originals.Length; i++)
            {
                Transaction original = originals[i];
                Transaction executed = processed.Transactions[i];
                if (!expectCopies)
                {
                    Assert.That(executed, Is.SameAs(original), $"transaction {i} runs as the caller's own");
                    continue;
                }

                Assert.That(executed, Is.Not.SameAs(original), $"transaction {i} runs as a copy");
                Assert.That(executed.Hash, Is.EqualTo(original.Hash));
                Assert.That(executed.SenderAddress, Is.EqualTo(original.SenderAddress));
                Assert.That((executed as NamedTransaction)?.Name, Is.EqualTo(((NamedTransaction)original).Name), "the copy keeps the runtime type and its state");
            }
        }
    }

    private static void Process(BlockchainProcessorFacade facade, bool bounded)
    {
        Block block = Build.A.Block.WithTransactions(Build.A.Transaction.SignedAndResolved().TestObject).TestObject;
        IBlockTracer tracer = Substitute.For<IBlockTracer>();
        IBlockTracer traced = bounded ? TransactionTraceBoundary.Wrap(tracer, block.Transactions[0].Hash!) : tracer;

        facade.Process(block, TraceProcessingOptions.ReadOnlyReplay, traced);
    }

    private static void AssertRecovery(RecordingStep step, bool expectDeferred)
    {
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
        public Block Recovered { get; private set; }

        public void RecoverData(Block block)
        {
            Full++;
            Recovered = block;
        }

        public void RecoverDataForTransactionTrace(Block block)
        {
            ForTransactionTrace++;
            Recovered = block;
        }
    }
}
