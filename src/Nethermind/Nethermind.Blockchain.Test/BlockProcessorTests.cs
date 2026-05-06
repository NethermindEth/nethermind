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
using Nethermind.Evm.GasPolicy;
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
using System.Collections.Generic;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Tracing;
using Nethermind.Evm;

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
        ReadOnlyBlockAccessList suggestedBal = Build.A.BlockAccessList
            .WithAccountChanges(Build.An.AccountChanges
                .WithAddress(TestItem.AddressA)
                .WithStorageReads(1)
                .TestObject)
            .TestObject;

        Block block = Build.A.Block
            .WithNumber(1)
            .WithGasUsed(gasRemaining)
            .WithBlockAccessList(suggestedBal)
            .TestObject;

        balManager.PrepareForProcessing(block, Amsterdam.Instance, ProcessingOptions.None);
        balManager.SetBlockExecutionContext(new(block.Header, Amsterdam.Instance));
        balManager.Setup(block);
        // Generated BAL has the account but no storage reads
        BlockAccessListAtIndex generatedAtIndex = new();
        generatedAtIndex.AddAccountRead(TestItem.AddressA);
        balManager.GeneratedBlockAccessList.Merge(generatedAtIndex);

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
            .WithBlockAccessList(new ReadOnlyBlockAccessList())
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
            .WithBlockAccessList(new ReadOnlyBlockAccessList())
            .TestObject;

        balManager.PrepareForProcessing(block, Amsterdam.Instance, ProcessingOptions.None);

        Assert.That(balManager.ParallelExecutionEnabled, Is.True);
    }

    [Test]
    public void IncrementalValidation_rejects_eip8037_tx_at_inclusion_when_worst_case_exceeds_remaining_budget()
    {
        // execution-specs PR 2703: the per-tx 2D inclusion check rejects a tx whose
        // worst-case dimension contribution exceeds the remaining budget, even if its
        // actual post-execution gas would have fit. tx1.GasLimit (50_000) exceeds the
        // remaining regular budget of 35_000 left after tx0's 65_000 actual usage, so
        // the spec rejects tx1 at inclusion regardless of its actual usage.
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
            .WithBlockAccessList(new ReadOnlyBlockAccessList())
            .TestObject;

        balManager.PrepareForProcessing(block, Amsterdam.Instance, ProcessingOptions.None);
        balManager.SetBlockExecutionContext(new(block.Header, Amsterdam.Instance));
        balManager.Setup(block);

        TaskCompletionSource<(long BlockGasUsed, long BlockStateGasUsed, IntrinsicGas<EthereumGasPolicy> IntrinsicGas, InvalidBlockException? Exception)>[] gasResults =
        [
            new(),
            new()
        ];
        gasResults[0].SetResult(GasResult(block, 0, 65_000, 0));
        gasResults[1].SetResult(GasResult(block, 1, 21_000, 0));

        InvalidBlockException? exception = Assert.Throws<InvalidBlockException>(() =>
            balManager.IncrementalValidation(block, gasResults, new BlockReceiptsTracer[2], null, Task.CompletedTask, CancellationToken.None));

        Assert.That(exception!.Message, Does.Contain("EIP-8037 inclusion check"));
        Assert.That(exception.Message, Does.Contain("RegularDimensionExceeded"));
    }

    [Test]
    public void IncrementalValidation_rejects_eip8037_tx_when_actual_cumulative_gas_exceeds_limit()
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
            .WithBlockAccessList(new ReadOnlyBlockAccessList())
            .TestObject;

        balManager.PrepareForProcessing(block, Amsterdam.Instance, ProcessingOptions.None);
        balManager.SetBlockExecutionContext(new(block.Header, Amsterdam.Instance));
        balManager.Setup(block);

        TaskCompletionSource<(long BlockGasUsed, long BlockStateGasUsed, IntrinsicGas<EthereumGasPolicy> IntrinsicGas, InvalidBlockException? Exception)>[] gasResults =
        [
            new(),
            new()
        ];
        gasResults[0].SetResult(GasResult(block, 0, 80_000, 0));
        gasResults[1].SetResult(GasResult(block, 1, 21_000, 0));

        InvalidBlockException? exception = Assert.Throws<InvalidBlockException>(() =>
            balManager.IncrementalValidation(block, gasResults, new BlockReceiptsTracer[2], null, Task.CompletedTask, CancellationToken.None));

        Assert.That(exception!.Message, Does.Contain("Block gas limit exceeded"));
    }

    [Test]
    public void IncrementalValidation_eip8037_block_gas_check_uses_actual_state_dimension()
    {
        // CREATE tx GasLimit sized to pass the EIP-8037 worst-case inclusion check
        // (worst-case state = GasLimit - intrinsic.regular must <= state_available
        // = 200_000 - 60_000 = 140_000). A 165_000 limit gives worst-case state of
        // ~135_000, which fits. Actual post-execution state is GasCostOf.CreateState
        // (the AccountCreationCost) so the post-exec max(R,S) check still ends up
        // verifying the state dimension drives block.Header.GasUsed.
        BlockAccessListManager balManager = CreateAmsterdamBalManager();
        Transaction firstTx = Build.A.Transaction
            .WithHash(TestItem.KeccakA)
            .WithGasLimit(21_000)
            .TestObject;
        Transaction createTx = Build.A.Transaction
            .WithHash(TestItem.KeccakB)
            .WithCode([])
            .WithGasLimit(165_000)
            .WithNonce(1)
            .TestObject;
        Block block = Build.A.Block
            .WithNumber(1)
            .WithGasLimit(200_000)
            .WithTransactions(firstTx, createTx)
            .WithBlockAccessList(new ReadOnlyBlockAccessList())
            .TestObject;

        balManager.PrepareForProcessing(block, Amsterdam.Instance, ProcessingOptions.None);
        balManager.SetBlockExecutionContext(new(block.Header, Amsterdam.Instance));
        balManager.Setup(block);

        TaskCompletionSource<(long BlockGasUsed, long BlockStateGasUsed, IntrinsicGas<EthereumGasPolicy> IntrinsicGas, InvalidBlockException? Exception)>[] gasResults =
        [
            new(),
            new()
        ];
        gasResults[0].SetResult(GasResult(block, 0, 0, 60_000));
        gasResults[1].SetResult(GasResult(block, 1, 50_000, GasCostOf.CreateState));

        Assert.DoesNotThrow(() =>
            balManager.IncrementalValidation(block, gasResults, new BlockReceiptsTracer[2], null, Task.CompletedTask, CancellationToken.None));
        Assert.That(block.Header.GasUsed, Is.EqualTo(60_000 + GasCostOf.CreateState));
    }

    [Test]
    public void IncrementalValidation_surfaces_worker_exception_before_gas_accounting()
    {
        // tx0's worker rejected the tx (e.g. signature/nonce/balance check inside ProcessTransaction
        // produced an InvalidBlockException). Even if tx0's reported gas would also trip downstream
        // gas accounting, the validator must surface the worker's original cause so parallel mode
        // reports the same root cause as sequential.
        // Tx GasLimit must pass the EIP-8037 inclusion check (worst-case <= block budget)
        // so the inclusion check doesn't fire before we can read the worker exception.
        // 50_000 worst-case <= 100_000 budget; the worker rejection then surfaces.
        BlockAccessListManager balManager = CreateAmsterdamBalManager();
        Transaction onlyTx = Build.A.Transaction
            .WithHash(TestItem.KeccakA)
            .WithGasLimit(50_000)
            .TestObject;
        Block block = Build.A.Block
            .WithNumber(1)
            .WithGasLimit(100_000)
            .WithTransactions(onlyTx)
            .WithBlockAccessList(new ReadOnlyBlockAccessList())
            .TestObject;

        balManager.PrepareForProcessing(block, Amsterdam.Instance, ProcessingOptions.None);
        balManager.SetBlockExecutionContext(new(block.Header, Amsterdam.Instance));
        balManager.Setup(block);

        InvalidBlockException workerException = new(block, "worker-original-cause");
        TaskCompletionSource<(long BlockGasUsed, long BlockStateGasUsed, IntrinsicGas<EthereumGasPolicy> IntrinsicGas, InvalidBlockException? Exception)>[] gasResults =
        [
            new()
        ];
        gasResults[0].SetResult(GasResult(block, 0, 0, 0, workerException));

        BlockAccessListManager.ParallelExecutionException? thrown = Assert.Throws<BlockAccessListManager.ParallelExecutionException>(() =>
            balManager.IncrementalValidation(block, gasResults, new BlockReceiptsTracer[1], null, Task.CompletedTask, CancellationToken.None));

        Assert.That(thrown!.InnerException, Is.SameAs(workerException));
    }

    [Test]
    public void IncrementalValidation_surfaces_worker_exception_even_when_reported_gas_would_trip_block_limit()
    {
        // Regression guard: the legacy parallel worker reported (tx.GasLimit, 0, ex) on rejection.
        // That value can push cumulative gas past block.GasLimit so CheckGasUsed throws an
        // InvalidBlockException("Block gas limit exceeded") that masks the worker's original cause.
        // Even with the worker now reporting (0, 0, ex), this test feeds the legacy shape so the
        // validator's exception-first ordering is verified directly, independent of worker behaviour.
        // tx0 has GasLimit 30_000 (passes inclusion: worst-case 30_000 <= 100_000) but
        // its ACTUAL gas reported by the worker is 80_000 (legacy buggy worker shape).
        // tx1 has GasLimit 20_000, which still passes inclusion (worst-case 20_000 <=
        // remaining 20_000 - strict > check) but its worker rejected. The validator
        // must surface tx1's worker exception, not the post-exec gas-limit overshoot
        // it would otherwise compute from the buggy (50_000, 0, ex) gas tuple.
        BlockAccessListManager balManager = CreateAmsterdamBalManager();
        Transaction firstTx = Build.A.Transaction
            .WithHash(TestItem.KeccakA)
            .WithGasLimit(30_000)
            .TestObject;
        Transaction secondTx = Build.A.Transaction
            .WithHash(TestItem.KeccakB)
            .WithGasLimit(20_000)
            .WithNonce(1)
            .TestObject;
        Block block = Build.A.Block
            .WithNumber(1)
            .WithGasLimit(100_000)
            .WithTransactions(firstTx, secondTx)
            .WithBlockAccessList(new ReadOnlyBlockAccessList())
            .TestObject;

        balManager.PrepareForProcessing(block, Amsterdam.Instance, ProcessingOptions.None);
        balManager.SetBlockExecutionContext(new(block.Header, Amsterdam.Instance));
        balManager.Setup(block);

        InvalidBlockException workerException = new(block, "worker-original-cause");
        TaskCompletionSource<(long BlockGasUsed, long BlockStateGasUsed, IntrinsicGas<EthereumGasPolicy> IntrinsicGas, InvalidBlockException? Exception)>[] gasResults =
        [
            new(),
            new()
        ];
        gasResults[0].SetResult(GasResult(block, 0, 80_000, 0));
        // Legacy buggy shape: charges tx.GasLimit on rejection. Cumulative 80k+50k > 100k limit.
        gasResults[1].SetResult(GasResult(block, 1, 50_000, 0, workerException));

        BlockAccessListManager.ParallelExecutionException? thrown = Assert.Throws<BlockAccessListManager.ParallelExecutionException>(() =>
            balManager.IncrementalValidation(block, gasResults, new BlockReceiptsTracer[2], null, Task.CompletedTask, CancellationToken.None));

        Assert.That(thrown!.InnerException, Is.SameAs(workerException));
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

    private static (long BlockGasUsed, long BlockStateGasUsed, IntrinsicGas<EthereumGasPolicy> IntrinsicGas, InvalidBlockException? Exception)
        GasResult(Block block, int txIndex, long blockGasUsed, long blockStateGasUsed, InvalidBlockException? exception = null)
        => (blockGasUsed, blockStateGasUsed, EthereumGasPolicy.CalculateIntrinsicGas(block.Transactions[txIndex], Amsterdam.Instance, block.Header.GasLimit), exception);
}
