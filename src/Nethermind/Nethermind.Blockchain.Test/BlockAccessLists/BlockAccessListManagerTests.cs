// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System;
using Autofac;
using NSubstitute;
using NUnit.Framework;
using Nethermind.Blockchain.BlockAccessLists;
using Nethermind.Blockchain.Tracing;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Cpu;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Container;
using Nethermind.Core;
using Nethermind.Crypto;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Evm;
using Nethermind.Init.Modules;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.Specs;

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
#pragma warning disable NUnit2045 // Completion must be confirmed before inspecting or advancing the operation.
        Assert.That(drain.Wait(DrainTimeout), Is.True);
#pragma warning restore NUnit2045
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
    [Test]
    public void Dispose_disposes_the_virtual_machines_of_its_tx_processors([Values] bool parallel)
    {
        MachineCapturingFactory factory = new();
        Harness h = new(new BlocksConfig { ParallelExecution = parallel }, factory);
        Block block = h.IssueHint(Task.CompletedTask);
        h.Manager.SetBlockExecutionContext(new BlockExecutionContext(block.Header, Substitute.For<IReleaseSpec>()));
        h.Manager.Setup(block);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(h.Manager.ParallelExecutionEnabled, Is.EqualTo(parallel));
            Assert.That(factory.Machines, Is.Not.Empty, "the manager built its tx processors");
            Assert.That(factory.Machines.Select(FrameCacheLength), Is.All.Positive);
        }

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
        using (Assert.EnterMultipleScope())
        {
            Assert.That(factory.Machines.Count(), Is.EqualTo(processors), "every index got its own tx processor");
            Assert.That(factory.Machines.Select(FrameCacheLength), Is.All.Positive);
        }

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

    private static int FrameCacheLength(IVirtualMachine machine) => ((VirtualMachine<EthereumGasPolicy>)machine).FrameCache.Length;

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

    // Parallel BAL execution keeps one parent-state reader per tx processor for the whole block, so later transactions
    // on a processor read through a scope earlier ones already used. This block makes the transactions share accounts and
    // slots, and checks that parallel execution still matches sequential execution.
    private static readonly Address Counter = TestItem.AddressF;
    private static readonly Address Recipient = new("0x00000000000000000000000000000000000000ee");
    private static readonly UInt256 InitialCount = new(5ul);
    private static readonly UInt256 ReadOnlyValue = new(7ul);

    // Reads slot 1 (never written), then increments slot 0.
    private static readonly byte[] ReadAndIncrement = Bytes.FromHexString("0x6001545060005460010160005500");

    [Test]
    public async Task Parallel_execution_matches_sequential_execution_for_transactions_sharing_accounts_and_slots()
    {
        using BasicTestBlockchain chain = await BasicTestBlockchain.Create(builder => builder
            .AddSingleton<ISpecProvider>(new TestSpecProvider(Amsterdam.Instance))
            .Intercept<IBlocksConfig>(blocksConfig => blocksConfig.ParallelExecution = true)
            .WithGenesisPostProcessor((_, state, specProvider) =>
            {
                state.CreateAccount(Counter, UInt256.Zero);
                state.InsertCode(Counter, ReadAndIncrement, specProvider.GenesisSpec);
                state.Set(new StorageCell(Counter, UInt256.Zero), InitialCount);
                state.Set(new StorageCell(Counter, UInt256.One), ReadOnlyValue);
            }));

        // More transactions than pooled processors, so processors take later transactions and reuse their readers.
        int txCount = 2 * Environment.ProcessorCount + 2;
        Transaction[] transactions = new Transaction[txCount];
        for (int i = 0; i < txCount; i++)
        {
            PrivateKey sender = i % 2 == 0 ? TestItem.PrivateKeyB : TestItem.PrivateKeyC;
            ulong nonce = (ulong)(i / 2);
            transactions[i] = i % 3 == 2 ? Transfer(sender, nonce) : Call(sender, nonce);
        }
        int calls = transactions.Count(static tx => tx.To == Counter);

        Block block = await chain.AddBlock(transactions);
        BlockHeader parent = chain.BlockTree.FindHeader(block.ParentHash!, BlockTreeLookupOptions.None)!;
        ReadOnlyBlockAccessList bal = chain.Container.Resolve<IBlockAccessListStore>().Get(block.Number, block.Hash!)
            ?? throw new AssertionException($"block {block.Number} has no stored block access list");
        IBlockAccessListManager balManager = ((MainProcessingContext)chain.MainProcessingContext).LifetimeScope.Resolve<IBlockAccessListManager>();
        IReleaseSpec spec = chain.SpecProvider.GetSpec(block.Header);
        TxReceipt[] produced = chain.ReceiptStorage.Get(block);

        (Block Block, TxReceipt[] Receipts, bool Parallel) parallel = Process(ProcessingOptions.ReadOnlyChain);
        (Block Block, TxReceipt[] Receipts, bool Parallel) sequential = Process(ProcessingOptions.ReadOnlyChain | ProcessingOptions.ForceSequentialBlockAccessList);
        // A second parallel run sets up the pool again, so it releases the readers kept for the first run.
        (Block Block, TxReceipt[] Receipts, bool Parallel) parallelAgain = Process(ProcessingOptions.ReadOnlyChain);

        chain.StateReader.GetStorage(block.Header, Counter, UInt256.Zero, out UInt256 count);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(produced.Select(static r => r.StatusCode), Is.All.EqualTo(StatusCode.Success));
            Assert.That(count, Is.EqualTo(InitialCount + (UInt256)calls), "every call incremented the shared slot");
            Assert.That(parallel.Parallel, Is.True, "the first run took the parallel path");
            Assert.That(sequential.Parallel, Is.False, "the second run took the sequential path");
            Assert.That(parallelAgain.Parallel, Is.True, "the third run took the parallel path");

            foreach ((Block Block, TxReceipt[] Receipts, bool Parallel) run in new[] { parallel, sequential, parallelAgain })
            {
                Assert.That(run.Block.Header.StateRoot, Is.EqualTo(block.Header.StateRoot));
                Assert.That(run.Block.Header.BlockAccessListHash, Is.EqualTo(block.Header.BlockAccessListHash));
                Assert.That(run.Block.Header.GasUsed, Is.EqualTo(block.Header.GasUsed));
                Assert.That(Summarize(run.Receipts), Is.EqualTo(Summarize(produced)));
            }
        }

        (Block Block, TxReceipt[] Receipts, bool Parallel) Process(ProcessingOptions options)
        {
            Block suggested = block.WithReplacedHeader(block.Header.Clone());
            suggested.BlockAccessList = bal;
            using IDisposable scope = chain.MainWorldState.BeginScope(parent);
            (Block processed, TxReceipt[] receipts) = chain.BlockProcessor.ProcessOne(
                suggested, options, NullBlockTracer.Instance, spec, CancellationToken.None);
            return (processed, receipts, balManager.ParallelExecutionEnabled);
        }
    }

    public enum Tampering { ChangedValue, ExtraAccount, DroppedAccount }

    /// <summary>
    /// A wrong list the transactions execute against is rejected from the workers' logs, without merging them to name
    /// the first difference; one missing an account a transaction reads fails that transaction.
    /// </summary>
    [TestCase(Tampering.ChangedValue, "does not match the generated one.")]
    [TestCase(Tampering.ExtraAccount, "does not match the generated one.")]
    [TestCase(Tampering.DroppedAccount, "missing account changes for 0x00000000000000000000000000000000000000ee at index 1.")]
    public async Task Parallel_execution_rejects_a_wrong_access_list(Tampering tampering, string error)
    {
        using BasicTestBlockchain chain = await BasicTestBlockchain.Create(builder => builder
            .AddSingleton<ISpecProvider>(new TestSpecProvider(Amsterdam.Instance))
            .Intercept<IBlocksConfig>(blocksConfig => blocksConfig.ParallelExecution = true));

        Block block = await chain.AddBlock([.. Enumerable.Range(0, 4).Select(static nonce => Transfer(TestItem.PrivateKeyB, (ulong)nonce))]);
        BlockHeader parent = chain.BlockTree.FindHeader(block.ParentHash!, BlockTreeLookupOptions.None)!;
        ReadOnlyBlockAccessList bal = chain.Container.Resolve<IBlockAccessListStore>().Get(block.Number, block.Hash!)
            ?? throw new AssertionException($"block {block.Number} has no stored block access list");
        Block suggested = block.WithReplacedHeader(block.Header.Clone());
        suggested.BlockAccessList = Tamper(bal, tampering);

        using IDisposable scope = chain.MainWorldState.BeginScope(parent);
        Assert.That(
            () => chain.BlockProcessor.ProcessOne(suggested, ProcessingOptions.ReadOnlyChain, NullBlockTracer.Instance, chain.SpecProvider.GetSpec(block.Header), CancellationToken.None),
            Throws.InstanceOf<InvalidBlockException>().With.Message.EqualTo($"InvalidBlockLevelAccessList: Suggested block-level access list {error}"));
    }

    private static ReadOnlyBlockAccessList Tamper(ReadOnlyBlockAccessList bal, Tampering tampering)
    {
        ReadOnlyAccountChanges[] accounts = bal.AccountChanges.AsSpan().ToArray();
        int recipient = Array.FindIndex(accounts, static account => account.Address == Recipient);
        ReadOnlyAccountChanges original = accounts[recipient];
        switch (tampering)
        {
            case Tampering.DroppedAccount:
                return new([.. accounts.Where((_, i) => i != recipient)], bal.ItemCount);
            case Tampering.ExtraAccount:
                ReadOnlyAccountChanges extra = Build.An.AccountChanges.WithAddress(new Address("0x00000000000000000000000000000000000000dd"))
                    .WithBalanceChanges(new BalanceChange(2, UInt256.One)).TestObject;
                return new([.. accounts.Append(extra).OrderBy(static account => account.Address)], bal.ItemCount);
        }

        accounts[recipient] = new ReadOnlyAccountChanges(
            Recipient,
            original.StorageChanges,
            original.StorageReads,
            [.. original.BalanceChanges.Select(static change => change.Index == 2 ? new BalanceChange(2, change.Value + UInt256.One) : change)],
            original.NonceChanges,
            original.CodeChanges);
        return new(accounts, bal.ItemCount);
    }

    private static (byte StatusCode, ulong GasUsed, ulong GasUsedTotal)[] Summarize(TxReceipt[] receipts) =>
        receipts.Select(static r => (r.StatusCode, r.GasUsed, r.GasUsedTotal)).ToArray();

    private static Transaction Call(PrivateKey sender, ulong nonce) =>
        Build.A.Transaction.WithNonce(nonce).WithTo(Counter).WithGasLimit(100_000)
            .SignedAndResolved(sender).TestObject;

    private static Transaction Transfer(PrivateKey sender, ulong nonce) =>
        Build.A.Transaction.WithNonce(nonce).WithTo(Recipient).WithValue(UInt256.One).WithGasLimit(1_000_000)
            .SignedAndResolved(sender).TestObject;
}
