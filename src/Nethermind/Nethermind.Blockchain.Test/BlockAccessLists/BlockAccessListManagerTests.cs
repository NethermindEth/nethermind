// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Cpu;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.BlockAccessLists;

[Parallelizable(ParallelScope.All)]
public class BlockAccessListManagerTests
{
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(10);

    private sealed class Harness
    {
        public IWorldState WorldState { get; } = Substitute.For<IWorldState>();
        public IReadOnlyTxProcessingEnvFactory ParentReaderEnvFactory { get; } = Substitute.For<IReadOnlyTxProcessingEnvFactory>();
        public BlockAccessListManager Manager { get; }

        public Harness(BlocksConfig? blocksConfig = null, ITransactionProcessorFactory? transactionProcessorFactory = null) => Manager = new BlockAccessListManager(
            WorldState,
            LimboLogs.Instance,
            blocksConfig ?? new BlocksConfig(), // ParallelExecution / ParallelExecutionBatchRead default to true
            Substitute.For<IWithdrawalProcessorFactory>(),
            new BalTxProcessorFactory(Substitute.For<IBlockhashProvider>(), Substitute.For<ISpecProvider>(), LimboLogs.Instance,
                transactionProcessorFactory: transactionProcessorFactory),
            // Enables parallel execution (and thus BAL read warmup), mirroring the production DI path.
            readOnlyTxProcessingEnvFactory: ParentReaderEnvFactory);

        /// <summary>
        /// Stubs <see cref="IWorldState.HintBal"/> to return <paramref name="hint"/>, then runs
        /// <see cref="BlockAccessListManager.PrepareForProcessing"/> with prerequisites met so
        /// the hint gets tracked. Subsequent calls re-stub and re-prepare for a new block.
        /// </summary>
        public Block IssueHint(Task hint, int txCount = 0)
        {
            WorldState.IsInScope.Returns(true);
            WorldState.HintBal(Arg.Any<ReadOnlyBlockAccessList>()).Returns(hint);

            IReleaseSpec spec = Substitute.For<IReleaseSpec>();
            spec.BlockLevelAccessListsEnabled.Returns(true);

            Block block = Build.A.Block
                .WithNumber(1) // not genesis — Enabled requires non-genesis
                .WithTransactions(Build.A.Transaction.TestObjectNTimes(txCount))
                .WithBlockAccessList(Build.A.BlockAccessList.TestObject)
                .TestObject;

            Manager.PrepareForProcessing(block, spec, ProcessingOptions.None);
            return block;
        }

        /// <summary>
        /// Prepares <paramref name="txCount"/> transactions for parallel execution, with a parent state every
        /// rented tx processor can read from.
        /// </summary>
        public void SetupParallelBlock(int txCount)
        {
            IReadOnlyTxProcessorSource source = Substitute.For<IReadOnlyTxProcessorSource>();
            source.TryBuildAtTarget(Arg.Any<BlockHeader>(), out Arg.Any<IReadOnlyTxProcessingScope?>())
                .Returns(call =>
                {
                    call[1] = Substitute.For<IReadOnlyTxProcessingScope>();
                    return true;
                });
            ParentReaderEnvFactory.Create().Returns(source);

            Block block = IssueHint(Task.CompletedTask, txCount);
            Manager.SetBlockExecutionContext(new BlockExecutionContext(block.Header, Substitute.For<IReleaseSpec>()));
            Manager.Setup(block);
            Assert.That(Manager.ParallelExecutionEnabled, Is.True);
        }
    }

    /// <summary>Hands out substitute processors and keeps the virtual machine each was built over.</summary>
    private sealed class MachineCapturingFactory : ITransactionProcessorFactory
    {
        private readonly ConcurrentQueue<(ITransactionProcessor Processor, IVirtualMachine Machine)> _built = new();

        public IEnumerable<IVirtualMachine> Machines => _built.Select(static b => b.Machine);

        /// <summary>The virtual machine of the one processor that was set up for a block, i.e. rented for an index.</summary>
        public IVirtualMachine RentedMachine => _built.Single(static b => b.Processor.ReceivedCalls().Any()).Machine;

        public ITransactionProcessor Create(ITransactionProcessor.IBlobBaseFeeCalculator blobBaseFeeCalculator, ISpecProvider specProvider,
            IWorldState worldState, IVirtualMachine virtualMachine, ICodeInfoRepository codeInfoRepository, ILogManager logManager, bool parallel)
        {
            ITransactionProcessor processor = Substitute.For<ITransactionProcessor>();
            _built.Enqueue((processor, virtualMachine));
            return processor;
        }
    }

    public enum HintState { NeverIssued, AlreadyCompleted, Canceled, Faulted }

    [TestCase(HintState.NeverIssued)]
    [TestCase(HintState.AlreadyCompleted)]
    [TestCase(HintState.Canceled)]
    [TestCase(HintState.Faulted)]
    public void WaitForBalWarmup_completes_without_throwing_for_non_pending_hint(HintState state)
    {
        Harness h = new();
        if (state != HintState.NeverIssued)
        {
            h.IssueHint(state switch
            {
                HintState.AlreadyCompleted => Task.CompletedTask,
                HintState.Canceled => Task.FromCanceled(new CancellationToken(canceled: true)),
                HintState.Faulted => Task.FromException(new InvalidOperationException("warm read failed")),
                _ => throw new ArgumentOutOfRangeException(nameof(state)),
            });
        }

        Task drain = Task.Run(h.Manager.WaitForBalWarmup);
        Assert.That(drain.Wait(DrainTimeout), Is.True);
        Assert.That(drain.Exception, Is.Null);
    }

    [Test]
    public void WaitForBalWarmup_blocks_until_pending_hint_completes()
    {
        Harness h = new();
        TaskCompletionSource hint = new(TaskCreationOptions.RunContinuationsAsynchronously);
        h.IssueHint(hint.Task);

        Task drain = Task.Run(h.Manager.WaitForBalWarmup);

        Assert.That(drain.Wait(TimeSpan.FromMilliseconds(50)), Is.False);
        hint.SetResult();
        Assert.That(drain.Wait(DrainTimeout), Is.True);
    }

    /// <summary>
    /// The manager owns the virtual machines it builds for its tx processors: disposing it disposes them, which hands
    /// back the data stacks their call-frame caches keep.
    /// </summary>
    [TestCase(true)]
    [TestCase(false)]
    public void Dispose_disposes_the_virtual_machines_of_its_tx_processors(bool parallel)
    {
        MachineCapturingFactory factory = new();
        Harness h = new(new BlocksConfig { ParallelExecution = parallel }, factory);
        Block block = h.IssueHint(Task.CompletedTask);
        h.Manager.SetBlockExecutionContext(new BlockExecutionContext(block.Header, Substitute.For<IReleaseSpec>()));
        h.Manager.Setup(block);
        Assert.That(h.Manager.ParallelExecutionEnabled, Is.EqualTo(parallel));
        Assert.That(factory.Machines, Is.Not.Empty, "the manager built its tx processors");
        Assert.That(factory.Machines.Select(FrameCacheLength), Is.All.Positive);

        h.Manager.Dispose();

        Assert.That(factory.Machines.Select(FrameCacheLength), Is.All.Zero, "every virtual machine was disposed");
    }

    /// <summary>
    /// A tx processor returned to a full pool is dropped, and its virtual machine with it: the pool disposes that one
    /// and keeps the others.
    /// </summary>
    [Test]
    public void Returning_a_tx_processor_to_a_full_pool_disposes_its_virtual_machine()
    {
        int processors = RuntimeInformation.ProcessorCount + 1; // one more than the pool holds
        MachineCapturingFactory factory = new();
        Harness h = new(transactionProcessorFactory: factory);
        h.SetupParallelBlock(txCount: processors);

        for (uint i = 0; i < processors; i++) h.Manager.GetTxProcessor(i);
        Assert.That(factory.Machines.Count(), Is.EqualTo(processors), "every index got its own tx processor");
        Assert.That(factory.Machines.Select(FrameCacheLength), Is.All.Positive);

        for (uint i = 0; i < processors; i++) h.Manager.ReturnTxProcessor(i);

        Assert.That(factory.Machines.Count(static m => FrameCacheLength(m) == 0), Is.EqualTo(1), "only the dropped processor's virtual machine was disposed");
    }

    /// <summary>
    /// Disposing the manager also disposes the virtual machine of a tx processor that is still rented for an index,
    /// not only those waiting in the pool.
    /// </summary>
    [Test]
    public void Dispose_disposes_the_virtual_machine_of_a_rented_tx_processor()
    {
        MachineCapturingFactory factory = new();
        Harness h = new(transactionProcessorFactory: factory);
        h.SetupParallelBlock(txCount: 1);

        h.Manager.GetTxProcessor(1);
        IVirtualMachine rented = factory.RentedMachine;
        Assert.That(FrameCacheLength(rented), Is.Positive);

        h.Manager.Dispose();

        Assert.That(FrameCacheLength(rented), Is.Zero, "the rented processor's virtual machine was disposed");
    }

    private static int FrameCacheLength(IVirtualMachine machine) =>
        ((Array)typeof(VirtualMachine<EthereumGasPolicy>)
            .GetField("FrameCache", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(machine)!).Length;

    [Test]
    public void PrepareForProcessing_drops_hint_tracked_for_previous_block()
    {
        Harness h = new();
        TaskCompletionSource stale = new(TaskCreationOptions.RunContinuationsAsynchronously);
        h.IssueHint(stale.Task);

        // Re-prepare for a new block with an already-completed hint — the stale TCS is left
        // unsignaled. If PrepareForProcessing didn't drop it, drain would block on `stale`.
        h.IssueHint(Task.CompletedTask);

        Task drain = Task.Run(h.Manager.WaitForBalWarmup);
        Assert.That(drain.Wait(DrainTimeout), Is.True, "a stale hint from the previous block must not be awaited");
    }
}
