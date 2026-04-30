// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Config;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Test.Validators;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.JsonRpc.Test.Modules;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Evm.State;
using Nethermind.State;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core.Threading;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;

namespace Nethermind.Blockchain.Test;

[Parallelizable(ParallelScope.All)]
public class BlockProcessorTests
{
    private static (BlockProcessor processor, BranchProcessor branchProcessor, IWorldState stateProvider) CreateProcessorAndBranch(
        IRewardCalculator? rewardCalculator = null,
        IBlockCachePreWarmer? preWarmer = null)
    {
        IWorldState stateProvider = TestWorldStateFactory.CreateForTest();
        ITransactionProcessor transactionProcessor = Substitute.For<ITransactionProcessor>();
        BlockAccessListManager balManager = new(stateProvider, HoodiSpecProvider.Instance, Substitute.For<IBlockhashProvider>(), LimboLogs.Instance, new BlocksConfig(), new WithdrawalProcessorFactory(LimboLogs.Instance));
        ExecuteTransactionProcessorAdapter txAdapter = new(transactionProcessor);
        IBlockProcessor.IBlockTransactionsExecutor transactionsExecutor = new BlockProcessor.ParallelBlockValidationTransactionsExecutor(
            new BlockProcessor.BlockValidationTransactionsExecutor(txAdapter, stateProvider),
            stateProvider, HoodiSpecProvider.Instance, balManager, LimboLogs.Instance);
        BlockProcessor processor = new(HoodiSpecProvider.Instance,
            TestBlockValidator.AlwaysValid,
            rewardCalculator ?? NoBlockRewards.Instance,
            transactionsExecutor,
            stateProvider,
            NullReceiptStorage.Instance,
            new BeaconBlockRootHandler(transactionProcessor, stateProvider),
            Substitute.For<IBlockhashStore>(),
            LimboLogs.Instance,
            new WithdrawalProcessor(stateProvider, LimboLogs.Instance),
            new ExecutionRequestsProcessor(transactionProcessor),
            balManager);

        BranchProcessor branchProcessor = new(
            processor,
            HoodiSpecProvider.Instance,
            stateProvider,
            new BeaconBlockRootHandler(transactionProcessor, stateProvider),
            Substitute.For<IBlockhashProvider>(),
            LimboLogs.Instance,
            preWarmer);

        return (processor, branchProcessor, stateProvider);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Prepared_block_contains_author_field()
    {
        (_, BranchProcessor branchProcessor, _) = CreateProcessorAndBranch();

        BlockHeader header = Build.A.BlockHeader.WithAuthor(TestItem.AddressD).TestObject;
        Block block = Build.A.Block.WithHeader(header).TestObject;
        Block[] processedBlocks = branchProcessor.Process(
            null,
            new List<Block> { block },
            ProcessingOptions.None,
            NullBlockTracer.Instance);
        Assert.That(processedBlocks.Length, Is.EqualTo(1), "length");
        Assert.That(processedBlocks[0].Author, Is.EqualTo(block.Author), "author");
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Recovers_state_on_cancel()
    {
        (_, BranchProcessor branchProcessor, _) = CreateProcessorAndBranch(
            rewardCalculator: new RewardCalculator(MainnetSpecProvider.Instance));

        BlockHeader header = Build.A.BlockHeader.WithNumber(1).WithAuthor(TestItem.AddressD).TestObject;
        Block block = Build.A.Block.WithTransactions(1, MuirGlacier.Instance).WithHeader(header).TestObject;
        Assert.Throws<OperationCanceledException>(() => branchProcessor.Process(
            null,
            new List<Block> { block },
            ProcessingOptions.None,
            AlwaysCancelBlockTracer.Instance));

        Assert.Throws<OperationCanceledException>(() => branchProcessor.Process(
            null,
            new List<Block> { block },
            ProcessingOptions.None,
            AlwaysCancelBlockTracer.Instance));
    }

    [MaxTime(Timeout.MaxTestTime)]
    [TestCase(20)]
    [TestCase(63)]
    [TestCase(64)]
    [TestCase(65)]
    [TestCase(127)]
    [TestCase(128)]
    [TestCase(129)]
    [TestCase(130)]
    [TestCase(1000)]
    [TestCase(2000)]
    public async Task Process_long_running_branch(int blocksAmount)
    {
        Address address = TestItem.Addresses[0];
        TestSingleReleaseSpecProvider spec = new(ConstantinopleFix.Instance);
        TestRpcBlockchain testRpc = await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
            .Build(spec);
        testRpc.TestWallet.UnlockAccount(address, new SecureString());
        await testRpc.AddFunds(address, 1.Ether);
        await testRpc.AddBlock();
        SemaphoreSlim suggestedBlockResetEvent = new(0);
        testRpc.BlockTree.NewHeadBlock += (_, _) =>
        {
            suggestedBlockResetEvent.Release(1);
        };

        int branchLength = blocksAmount + (int)testRpc.BlockTree.BestKnownNumber + 1;
        ((BlockTree)testRpc.BlockTree).AddBranch(branchLength, (int)testRpc.BlockTree.BestKnownNumber);
        Assert.That(await suggestedBlockResetEvent.WaitAsync(TestBlockchain.DefaultTimeout * 10), Is.True);
        Assert.That((int)testRpc.BlockTree.BestKnownNumber, Is.EqualTo(branchLength - 1));
    }


    [Test, MaxTime(Timeout.MaxTestTime)]
    public void TransactionsExecuted_event_fires_during_ProcessOne()
    {
        (BlockProcessor processor, _, IWorldState stateProvider) = CreateProcessorAndBranch();

        bool eventFired = false;
        processor.TransactionsExecuted += () => eventFired = true;

        using IDisposable scope = stateProvider.BeginScope(null);
        BlockHeader header = Build.A.BlockHeader.WithAuthor(TestItem.AddressD).TestObject;
        Block block = Build.A.Block.WithHeader(header).TestObject;
        IReleaseSpec spec = HoodiSpecProvider.Instance.GetSpec(block.Header);

        processor.ProcessOne(block, ProcessingOptions.NoValidation, NullBlockTracer.Instance, spec, CancellationToken.None);

        Assert.That(eventFired, Is.True, "TransactionsExecuted should fire after ProcessTransactions completes");
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void BranchProcessor_cancels_prewarmer_via_TransactionsExecuted_event()
    {
        TokenCapturingPreWarmer preWarmer = new();
        (_, BranchProcessor branchProcessor, _) = CreateProcessorAndBranch(preWarmer: preWarmer);

        BlockHeader header = Build.A.BlockHeader.WithAuthor(TestItem.AddressD).TestObject;
        Block block = Build.A.Block.WithHeader(header).WithTransactions(3, MuirGlacier.Instance).TestObject;

        branchProcessor.Process(
            null,
            new List<Block> { block },
            ProcessingOptions.NoValidation,
            NullBlockTracer.Instance);

        Assert.That(preWarmer.CapturedToken.IsCancellationRequested, Is.True,
            "prewarmer CancellationToken should be cancelled via TransactionsExecuted event after tx processing");
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void BranchProcessor_unsubscribes_from_TransactionsExecuted_after_processing()
    {
        (BlockProcessor processor, BranchProcessor branchProcessor, IWorldState stateProvider) = CreateProcessorAndBranch();

        BlockHeader header = Build.A.BlockHeader.WithAuthor(TestItem.AddressD).TestObject;
        Block block = Build.A.Block.WithHeader(header).TestObject;

        branchProcessor.Process(
            null,
            new List<Block> { block },
            ProcessingOptions.NoValidation,
            NullBlockTracer.Instance);

        // After Process returns, the event handler should be unsubscribed.
        // Verify by checking that firing the event doesn't cause issues
        // (if still subscribed, it would try to cancel a disposed CTS).
        int externalHandlerCallCount = 0;
        processor.TransactionsExecuted += () => externalHandlerCallCount++;

        // Process another block to trigger the event — only our handler should fire
        using IDisposable scope = stateProvider.BeginScope(null);
        Block block2 = Build.A.Block.WithHeader(Build.A.BlockHeader.WithAuthor(TestItem.AddressD).TestObject).TestObject;
        IReleaseSpec spec = HoodiSpecProvider.Instance.GetSpec(block2.Header);
        processor.ProcessOne(block2, ProcessingOptions.NoValidation, NullBlockTracer.Instance, spec, CancellationToken.None);

        Assert.That(externalHandlerCallCount, Is.EqualTo(1),
            "only the externally subscribed handler should fire, BranchProcessor should have unsubscribed");
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void BranchProcessor_no_prewarmer_still_processes_successfully()
    {
        (_, BranchProcessor branchProcessor, _) = CreateProcessorAndBranch(preWarmer: null);

        BlockHeader header = Build.A.BlockHeader.WithAuthor(TestItem.AddressD).TestObject;
        Block block = Build.A.Block.WithHeader(header).WithTransactions(3, MuirGlacier.Instance).TestObject;

        Block[] processedBlocks = branchProcessor.Process(
            null,
            new List<Block> { block },
            ProcessingOptions.NoValidation,
            NullBlockTracer.Instance);

        Assert.That(processedBlocks, Has.Length.EqualTo(1), "block should process successfully without a prewarmer");
    }

    [Test]
    public void NullBlockProcessor_TransactionsExecuted_subscribe_unsubscribe_is_safe()
    {
        IBlockProcessor processor = NullBlockProcessor.Instance;

        // Should not throw
        Action handler = () => { };
        processor.TransactionsExecuted += handler;
        processor.TransactionsExecuted -= handler;
    }

    [Test]
    public void BlockProductionTransactionPicker_validates_block_length_using_proper_tx_form()
    {
        IReleaseSpec spec = Osaka.Instance;
        ISpecProvider specProvider = new TestSingleReleaseSpecProvider(spec);

        Transaction transactionWithNetworkForm = Build.A.Transaction
            .WithShardBlobTxTypeAndFields(1, true, spec)
            .SignedAndResolved()
            .TestObject;

        BlockProcessor.BlockProductionTransactionPicker txPicker = new(specProvider, transactionWithNetworkForm.GetLength(true) / 1.KiB - 1);
        BlockToProduce newBlock = new(Build.A.BlockHeader.WithExcessBlobGas(0).TestObject);
        WorldStateStab stateProvider = new();

        using IDisposable _ = stateProvider.BeginScope(IWorldState.PreGenesis);

        Transaction? addedTransaction = null;
        txPicker.AddingTransaction += (s, e) => addedTransaction = e.Transaction;

        txPicker.CanAddTransaction(newBlock, transactionWithNetworkForm, new HashSet<Transaction>(), WorldStateStab.GetUntrackedReader());

        Assert.That(addedTransaction, Is.EqualTo(transactionWithNetworkForm));
    }

    /// <summary>
    /// Manual IBlockCachePreWarmer that captures the CancellationToken for test verification.
    /// NSubstitute cannot proxy ReadOnlySpan&lt;T&gt; (ref struct) parameters.
    /// </summary>
    private class TokenCapturingPreWarmer : IBlockCachePreWarmer
    {
        public CancellationToken CapturedToken { get; private set; }

        public Task PreWarmCaches(Block suggestedBlock, BlockHeader? parent, IReleaseSpec spec,
            CancellationToken cancellationToken = default, params ReadOnlySpan<IHasAccessList> systemAccessLists)
        {
            CapturedToken = cancellationToken;
            return Task.CompletedTask;
        }

        public CacheType ClearCaches() => default;
        public bool IsBalReadWarmingEnabled(IReleaseSpec spec) => false;
        public void Dispose() { }
    }

    public static IEnumerable<TestCaseData> BlockValidationTransactionsExecutor_bal_validation_cases()
    {
        yield return new TestCaseData(ProcessingOptions.None, true)
            .SetName("BlockValidationTransactionsExecutor_uses_block_gas_for_bal_validation_budget");
        yield return new TestCaseData(ProcessingOptions.NoValidation, false)
            .SetName("BlockValidationTransactionsExecutor_skips_bal_validation_when_no_validation_requested");
    }

    [TestCase(2000, false, TestName = "BAL_read_budget_at_2000_gas_passes")]
    [TestCase(1999, true, TestName = "BAL_read_budget_at_1999_gas_fails")]
    public void ValidateBlockAccessList_storage_read_budget_uses_ItemCost(long gasRemaining, bool shouldThrow)
    {
        // One extra storage read in suggested BAL costs Eip7928Constants.ItemCost (2000) gas
        IWorldState stateProvider = TestWorldStateFactory.CreateForTest();
        BlockAccessListManager balManager = new(
            stateProvider,
            new TestSingleReleaseSpecProvider(Amsterdam.Instance),
            Substitute.For<IBlockhashProvider>(),
            LimboLogs.Instance,
            new BlocksConfig { ParallelExecution = false },
            new WithdrawalProcessorFactory(LimboLogs.Instance));

        // Prepare with a block that has gasUsed = gasRemaining (sets _gasRemaining)
        BlockAccessList suggestedBal = new();
        suggestedBal.AddAccountRead(TestItem.AddressA);
        suggestedBal.AddStorageRead(TestItem.AddressA, 1);

        Block block = Build.A.Block
            .WithNumber(1)
            .WithGasUsed(gasRemaining)
            .WithBlockAccessList(suggestedBal)
            .TestObject;

        balManager.PrepareForProcessing(block, Amsterdam.Instance, ProcessingOptions.None);
        balManager.SetBlockExecutionContext(new(block.Header, Amsterdam.Instance));
        balManager.Setup(block);
        // Generated BAL has the account but no storage reads
        balManager.GeneratedBlockAccessList.AddAccountRead(TestItem.AddressA);

        if (shouldThrow)
        {
            Assert.Throws<BlockAccessListBasedWorldState.InvalidBlockLevelAccessListException>(
                () => balManager.ValidateBlockAccessList(block, 0));
        }
        else
        {
            Assert.DoesNotThrow(() => balManager.ValidateBlockAccessList(block, 0));
        }
    }

    [Test]
    public void PrepareForProcessing_keeps_parallel_bal_execution_for_validated_eip8037_multi_tx_blocks()
    {
        BlockAccessListManager balManager = CreateAmsterdamBalManager();

        Block block = Build.A.Block
            .WithNumber(1)
            .WithTransactions(2, Amsterdam.Instance)
            .WithBlockAccessList(new BlockAccessList())
            .TestObject;

        balManager.PrepareForProcessing(block, Amsterdam.Instance, ProcessingOptions.None);

        Assert.That(balManager.ParallelExecutionEnabled, Is.True);
    }

    [Test]
    public void PrepareForProcessing_keeps_parallel_bal_execution_for_validated_eip8037_single_tx_blocks()
    {
        BlockAccessListManager balManager = CreateAmsterdamBalManager();

        Block block = Build.A.Block
            .WithNumber(1)
            .WithTransactions(1, Amsterdam.Instance)
            .WithBlockAccessList(new BlockAccessList())
            .TestObject;

        balManager.PrepareForProcessing(block, Amsterdam.Instance, ProcessingOptions.None);

        Assert.That(balManager.ParallelExecutionEnabled, Is.True);
    }

    [Test]
    public void IncrementalValidation_rejects_eip8037_tx_when_worst_case_exceeds_ordered_remaining_gas()
    {
        BlockAccessListManager balManager = CreateAmsterdamBalManager();
        Transaction firstTx = Build.A.Transaction
            .WithHash(TestItem.KeccakA)
            .WithGasLimit(90_000)
            .TestObject;
        Transaction secondTx = Build.A.Transaction
            .WithHash(TestItem.KeccakB)
            .WithGasLimit(50_000)
            .WithNonce(1)
            .TestObject;
        Block block = Build.A.Block
            .WithNumber(1)
            .WithGasLimit(100_000)
            .WithTransactions(firstTx, secondTx)
            .WithBlockAccessList(new BlockAccessList())
            .TestObject;

        balManager.PrepareForProcessing(block, Amsterdam.Instance, ProcessingOptions.None);
        balManager.SetBlockExecutionContext(new(block.Header, Amsterdam.Instance));
        balManager.Setup(block);

        TaskCompletionSource<(long BlockGasUsed, long BlockStateGasUsed, InvalidBlockException? Exception)>[] gasResults =
        [
            new(),
            new()
        ];
        gasResults[0].SetResult((80_000, 0, null));
        gasResults[1].SetResult((21_000, 0, null));

        InvalidBlockException? exception = Assert.Throws<InvalidBlockException>(() =>
            balManager.IncrementalValidation(block, gasResults, new BlockReceiptsTracer[2], null, CancellationToken.None));

        Assert.That(exception!.Message, Does.Contain("Block gas limit exceeded"));
    }

    [TestCase(true)]
    [TestCase(false)]
    public void Parallel_validation_preserves_processing_thread_metric_scope_for_worker_transactions(bool isBlockProcessingThread)
    {
        Assume.That(Environment.ProcessorCount, Is.GreaterThan(1));

        const int txCount = 64;
        IWorldState stateProvider = TestWorldStateFactory.CreateForTest();
        using IDisposable scope = stateProvider.BeginScope(IWorldState.PreGenesis);

        Transaction[] transactions = new Transaction[txCount];
        for (int i = 0; i < transactions.Length; i++)
        {
            transactions[i] = Build.A.Transaction
                .WithNonce((UInt256)i)
                .WithGasLimit(1)
                .TestObject;
        }

        Block block = Build.A.Block
            .WithNumber(1)
            .WithGasLimit(txCount)
            .WithTransactions(transactions)
            .WithBlockAccessList(new BlockAccessList())
            .TestObject;

        using RecordingTransactionProcessorAdapter transactionProcessor = new();
        IBlockAccessListManager balManager = Substitute.For<IBlockAccessListManager>();
        balManager.Enabled.Returns(true);
        balManager.ParallelExecutionEnabled.Returns(true);
        balManager.GetTxProcessor(Arg.Any<int?>()).Returns(transactionProcessor);

        IBlockProcessor.IBlockTransactionsExecutor inner = Substitute.For<IBlockProcessor.IBlockTransactionsExecutor>();
        BlockProcessor.ParallelBlockValidationTransactionsExecutor executor = new(
            inner,
            stateProvider,
            new TestSingleReleaseSpecProvider(Amsterdam.Instance),
            balManager,
            LimboLogs.Instance);

        bool previousIsBlockProcessingThread = ProcessingThread.IsBlockProcessingThread;
        ProcessingThread.IsBlockProcessingThread = isBlockProcessingThread;
        try
        {
            TxReceipt[] receipts = executor.ProcessTransactions(
                block,
                ProcessingOptions.None,
                new BlockReceiptsTracer(),
                CancellationToken.None);
            Assert.That(receipts, Has.Length.EqualTo(txCount));
        }
        finally
        {
            ProcessingThread.IsBlockProcessingThread = previousIsBlockProcessingThread;
        }

        Assert.That(transactionProcessor.ThreadIds.Count, Is.GreaterThan(1));
        Assert.That(transactionProcessor.ObservedProcessingThreadFlags.Count, Is.EqualTo(txCount));
        foreach (bool observedProcessingThreadFlag in transactionProcessor.ObservedProcessingThreadFlags)
        {
            Assert.That(observedProcessingThreadFlag, Is.EqualTo(isBlockProcessingThread));
        }
    }

    [Test]
    public void Parallel_validation_forwards_parallel_safe_block_tracer_to_worker_transactions()
    {
        Assume.That(Environment.ProcessorCount, Is.GreaterThan(1));

        const int txCount = 64;
        IWorldState stateProvider = TestWorldStateFactory.CreateForTest();
        using IDisposable scope = stateProvider.BeginScope(IWorldState.PreGenesis);

        Transaction[] transactions = new Transaction[txCount];
        for (int i = 0; i < transactions.Length; i++)
        {
            transactions[i] = Build.A.Transaction
                .WithNonce((UInt256)i)
                .WithGasLimit(1)
                .TestObject;
        }

        Block block = Build.A.Block
            .WithNumber(1)
            .WithGasLimit(txCount)
            .WithTransactions(transactions)
            .WithBlockAccessList(new BlockAccessList())
            .TestObject;

        using RecordingTransactionProcessorAdapter transactionProcessor = new(traceOperation: true);
        IBlockAccessListManager balManager = Substitute.For<IBlockAccessListManager>();
        balManager.Enabled.Returns(true);
        balManager.ParallelExecutionEnabled.Returns(true);
        balManager.GetTxProcessor(Arg.Any<int?>()).Returns(transactionProcessor);

        IBlockProcessor.IBlockTransactionsExecutor inner = Substitute.For<IBlockProcessor.IBlockTransactionsExecutor>();
        BlockProcessor.ParallelBlockValidationTransactionsExecutor executor = new(
            inner,
            stateProvider,
            new TestSingleReleaseSpecProvider(Amsterdam.Instance),
            balManager,
            LimboLogs.Instance);
        RecordingParallelSafeBlockTracer blockTracer = new();
        BlockReceiptsTracer receiptsTracer = new();
        receiptsTracer.SetOtherTracer(blockTracer);
        receiptsTracer.StartNewBlockTrace(block);

        TxReceipt[] receipts = executor.ProcessTransactions(
            block,
            ProcessingOptions.None,
            receiptsTracer,
            CancellationToken.None);
        receiptsTracer.EndBlockTrace();

        Assert.Multiple(() =>
        {
            Assert.That(receipts, Has.Length.EqualTo(txCount));
            Assert.That(blockTracer.StartedTransactions, Is.EqualTo(txCount));
            Assert.That(blockTracer.EndedTransactions, Is.EqualTo(txCount));
            Assert.That(blockTracer.OpcodeCount, Is.EqualTo(txCount));
        });
    }

    private static BlockAccessListManager CreateAmsterdamBalManager()
    {
        IWorldState stateProvider = TestWorldStateFactory.CreateForTest();
        return new(
            stateProvider,
            new TestSingleReleaseSpecProvider(Amsterdam.Instance),
            Substitute.For<IBlockhashProvider>(),
            LimboLogs.Instance,
            new BlocksConfig { ParallelExecution = true },
            new WithdrawalProcessorFactory(LimboLogs.Instance));
    }

    private sealed class RecordingTransactionProcessorAdapter(bool traceOperation = false) : ITransactionProcessorAdapter, IDisposable
    {
        private readonly ManualResetEventSlim _parallelExecutionStarted = new();
        private int _executedCount;

        public ConcurrentBag<bool> ObservedProcessingThreadFlags { get; } = new();
        public ConcurrentDictionary<int, byte> ThreadIds { get; } = new();

        public TransactionResult Execute(Transaction transaction, ITxTracer txTracer)
        {
            ThreadIds.TryAdd(Environment.CurrentManagedThreadId, 0);
            if (Interlocked.Increment(ref _executedCount) >= 2)
            {
                _parallelExecutionStarted.Set();
            }

            _parallelExecutionStarted.Wait(TimeSpan.FromSeconds(5));
            ObservedProcessingThreadFlags.Add(ProcessingThread.IsBlockProcessingThread);
            if (traceOperation && txTracer.IsTracingInstructions)
            {
                txTracer.StartOperation(0, Instruction.ADD, 0, null!);
            }

            transaction.BlockGasUsed = 1;
            txTracer.MarkAsSuccess(Address.Zero, 1, [], []);

            return TransactionResult.Ok;
        }

        public void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext)
        {
        }

        public void Dispose() => _parallelExecutionStarted.Dispose();
    }

    private sealed class RecordingParallelSafeBlockTracer : IParallelSafeBlockTracer
    {
        private int _startedTransactions;
        private int _endedTransactions;
        private int _opcodeCount;

        public int StartedTransactions => _startedTransactions;
        public int EndedTransactions => _endedTransactions;
        public int OpcodeCount => _opcodeCount;
        public bool IsTracingRewards => false;

        public void ReportReward(Address author, string rewardType, UInt256 rewardValue)
        {
        }

        public void StartNewBlockTrace(Block block)
        {
        }

        public ITxTracer StartNewTxTrace(Transaction? tx)
        {
            if (tx is null)
            {
                return NullTxTracer.Instance;
            }

            Interlocked.Increment(ref _startedTransactions);
            return new RecordingInstructionTxTracer(this);
        }

        public void EndTxTrace() =>
            Interlocked.Increment(ref _endedTransactions);

        public void EndBlockTrace()
        {
        }

        private void RecordOpcode() =>
            Interlocked.Increment(ref _opcodeCount);

        private sealed class RecordingInstructionTxTracer(RecordingParallelSafeBlockTracer blockTracer) : TxTracer
        {
            public override bool IsTracingInstructions => true;

            public override void StartOperation(int pc, Instruction opcode, long gas, in ExecutionEnvironment env) =>
                blockTracer.RecordOpcode();
        }
    }
}
