// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Test.Validators;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.JsonRpc.Test.Modules;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Nethermind.Evm.State;
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
using Nethermind.Evm.Tracing;
using Nethermind.Core.BlockAccessLists;

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
        BlockProcessor processor = new(HoodiSpecProvider.Instance,
            TestBlockValidator.AlwaysValid,
            rewardCalculator ?? NoBlockRewards.Instance,
            new BlockProcessor.BlockValidationTransactionsExecutor(new ExecuteTransactionProcessorAdapter(transactionProcessor), stateProvider),
            stateProvider,
            NullReceiptStorage.Instance,
            new BeaconBlockRootHandler(transactionProcessor, stateProvider),
            Substitute.For<IBlockhashStore>(),
            LimboLogs.Instance,
            new WithdrawalProcessor(stateProvider, LimboLogs.Instance),
            new ExecutionRequestsProcessor(transactionProcessor));
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
    [TestCaseSource(nameof(BlockProcessor_bal_validation_cases))]
    public void BlockProcessor_validates_bal_after_execution_requests_only_when_validation_enabled(
        ProcessingOptions processingOptions,
        bool shouldValidateBlockAccessList)
    {
        TrackingBlockAccessListWorldState stateProvider = new(TestWorldStateFactory.CreateForTest());
        List<string> events = [];
        stateProvider.OnValidate = (index, gasRemaining) => events.Add($"bal-{index}:{gasRemaining}");

        ITransactionProcessorAdapter transactionProcessor = Substitute.For<ITransactionProcessorAdapter>();
        transactionProcessor.Execute(Arg.Any<Transaction>(), Arg.Any<ITxTracer>()).Returns(callInfo =>
        {
            events.Add("tx");
            Transaction transaction = callInfo.Arg<Transaction>();
            transaction.SpentGas = 63_586;
            transaction.BlockGasUsed = 37_568;
            return TransactionResult.Ok;
        });

        IExecutionRequestsProcessor executionRequestsProcessor = Substitute.For<IExecutionRequestsProcessor>();
        executionRequestsProcessor
            .When(static x => x.ProcessExecutionRequests(Arg.Any<Block>(), Arg.Any<IWorldState>(), Arg.Any<TxReceipt[]>(), Arg.Any<IReleaseSpec>()))
            .Do(_ => events.Add("requests"));

        OverridableReleaseSpec spec = new(London.Instance) { IsEip7928Enabled = true };
        BlockProcessor processor = CreateBalTestBlockProcessor(stateProvider, transactionProcessor, executionRequestsProcessor, spec);
        Block block = Build.A.Block
            .WithTransactions(Build.A.Transaction.SignedAndResolved().TestObject)
            .WithGasUsed(37_568)
            .WithBlockAccessList(new BlockAccessList())
            .TestObject;

        using IDisposable _ = stateProvider.BeginScope(null);
        processor.ProcessOne(block, processingOptions, NullBlockTracer.Instance, spec, CancellationToken.None);

        if (shouldValidateBlockAccessList)
        {
            Assert.That(events, Is.EqualTo(new[] { "tx", "requests", "bal-0:37568", "bal-1:0" }));
        }
        else
        {
            Assert.That(events, Is.EqualTo(new[] { "tx", "requests" }));
        }
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void BlockProcessor_prioritizes_execution_request_error_over_bal_validation()
    {
        TrackingBlockAccessListWorldState stateProvider = new(TestWorldStateFactory.CreateForTest());
        ITransactionProcessorAdapter transactionProcessor = SuccessfulTransactionProcessor();
        IExecutionRequestsProcessor executionRequestsProcessor = Substitute.For<IExecutionRequestsProcessor>();
        executionRequestsProcessor
            .When(static x => x.ProcessExecutionRequests(Arg.Any<Block>(), Arg.Any<IWorldState>(), Arg.Any<TxReceipt[]>(), Arg.Any<IReleaseSpec>()))
            .Do(callInfo => throw new InvalidBlockException(callInfo.Arg<Block>(), "DepositsInvalid: Invalid deposit event layout: test"));

        OverridableReleaseSpec spec = new(London.Instance) { IsEip7928Enabled = true };
        BlockProcessor processor = CreateBalTestBlockProcessor(stateProvider, transactionProcessor, executionRequestsProcessor, spec);
        Block block = Build.A.Block
            .WithTransactions(Build.A.Transaction.SignedAndResolved().TestObject)
            .WithGasUsed(37_568)
            .WithBlockAccessList(new BlockAccessList())
            .TestObject;

        using IDisposable _ = stateProvider.BeginScope(null);
        InvalidBlockException exception = Assert.Throws<InvalidBlockException>(
            () => processor.ProcessOne(block, ProcessingOptions.None, NullBlockTracer.Instance, spec, CancellationToken.None))!;

        Assert.That(exception.Message, Does.StartWith("DepositsInvalid: Invalid deposit event layout"));
        Assert.That(stateProvider.ValidatedGasRemaining, Is.Empty);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void BlockProcessor_prioritizes_transaction_error_over_bal_item_gas_limit()
    {
        TrackingBlockAccessListWorldState stateProvider = new(TestWorldStateFactory.CreateForTest());
        ITransactionProcessorAdapter transactionProcessor = Substitute.For<ITransactionProcessorAdapter>();
        transactionProcessor.Execute(Arg.Any<Transaction>(), Arg.Any<ITxTracer>()).Returns(TransactionResult.BlockGasLimitExceeded);
        IExecutionRequestsProcessor executionRequestsProcessor = Substitute.For<IExecutionRequestsProcessor>();

        OverridableReleaseSpec spec = new(London.Instance) { IsEip7928Enabled = true };
        BlockProcessor processor = CreateBalTestBlockProcessor(stateProvider, transactionProcessor, executionRequestsProcessor, spec);
        Block block = Build.A.Block
            .WithTransactions(Build.A.Transaction.SignedAndResolved().TestObject)
            .WithGasLimit(21_000)
            .WithGasUsed(21_000)
            .WithBlockAccessList(BlockAccessListWithAccountReads(15))
            .TestObject;

        using IDisposable _ = stateProvider.BeginScope(null);
        Nethermind.Blockchain.InvalidTransactionException exception = Assert.Throws<Nethermind.Blockchain.InvalidTransactionException>(
            () => processor.ProcessOne(block, ProcessingOptions.None, NullBlockTracer.Instance, spec, CancellationToken.None))!;

        Assert.That(exception.Message, Does.Contain("Block gas limit exceeded"));
        Assert.That(stateProvider.ValidatedGasRemaining, Is.Empty);
        executionRequestsProcessor
            .DidNotReceive()
            .ProcessExecutionRequests(Arg.Any<Block>(), Arg.Any<IWorldState>(), Arg.Any<TxReceipt[]>(), Arg.Any<IReleaseSpec>());
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void BlockProcessor_validates_bal_item_gas_limit_after_transactions()
    {
        TrackingBlockAccessListWorldState stateProvider = new(TestWorldStateFactory.CreateForTest());
        ITransactionProcessorAdapter transactionProcessor = SuccessfulTransactionProcessor(blockGasUsed: 21_000);
        IExecutionRequestsProcessor executionRequestsProcessor = Substitute.For<IExecutionRequestsProcessor>();

        OverridableReleaseSpec spec = new(London.Instance) { IsEip7928Enabled = true };
        BlockProcessor processor = CreateBalTestBlockProcessor(stateProvider, transactionProcessor, executionRequestsProcessor, spec);
        Block block = Build.A.Block
            .WithTransactions(Build.A.Transaction.SignedAndResolved().TestObject)
            .WithGasLimit(21_000)
            .WithGasUsed(21_000)
            .WithBlockAccessList(BlockAccessListWithAccountReads(15))
            .TestObject;

        using IDisposable _ = stateProvider.BeginScope(null);
        InvalidBlockException exception = Assert.Throws<InvalidBlockException>(
            () => processor.ProcessOne(block, ProcessingOptions.None, NullBlockTracer.Instance, spec, CancellationToken.None))!;

        Assert.That(exception.Message, Does.StartWith("BlockAccessListGasLimitExceeded"));
        Assert.That(stateProvider.ValidatedGasRemaining, Is.Empty);
        executionRequestsProcessor
            .Received(1)
            .ProcessExecutionRequests(Arg.Any<Block>(), Arg.Any<IWorldState>(), Arg.Any<TxReceipt[]>(), Arg.Any<IReleaseSpec>());
    }

    [TestCase(2_000, false)]
    [TestCase(1_999, true)]
    [MaxTime(Timeout.MaxTestTime)]
    public void ParallelWorldState_bal_read_budget_uses_eip_7928_item_cost(long gasRemaining, bool shouldThrow)
    {
        ParallelWorldState stateProvider = new(TestWorldStateFactory.CreateForTest());
        BlockAccessList suggestedBlockAccessList = new();
        suggestedBlockAccessList.AddStorageRead(TestItem.AddressA, 1);
        stateProvider.LoadSuggestedBlockAccessList(suggestedBlockAccessList, gasRemaining);

        TestDelegate act = () => stateProvider.ValidateBlockAccessList(Build.A.BlockHeader.TestObject, 0, gasRemaining);

        if (shouldThrow)
        {
            Assert.Throws<ParallelWorldState.InvalidBlockLevelAccessListException>(act);
        }
        else
        {
            Assert.That(act, Throws.Nothing);
        }
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void ParallelWorldState_bal_read_budget_ignores_reads_already_in_generated_bal()
    {
        ParallelWorldState stateProvider = new(TestWorldStateFactory.CreateForTest());
        BlockAccessList suggestedBlockAccessList = new();
        suggestedBlockAccessList.AddStorageRead(TestItem.AddressA, 1);
        stateProvider.LoadSuggestedBlockAccessList(suggestedBlockAccessList, -1);
        stateProvider.GeneratedBlockAccessList.AddStorageRead(TestItem.AddressA, 1);

        TestDelegate act = () => stateProvider.ValidateBlockAccessList(Build.A.BlockHeader.TestObject, 0, -1);

        Assert.That(act, Throws.Nothing);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void ParallelWorldState_bal_validation_rejects_missing_account_only_reads()
    {
        ParallelWorldState stateProvider = new(TestWorldStateFactory.CreateForTest());
        stateProvider.LoadSuggestedBlockAccessList(new BlockAccessList(), 10_000);
        stateProvider.GeneratedBlockAccessList.IncrementBlockAccessIndex();
        stateProvider.GeneratedBlockAccessList.AddAccountRead(TestItem.AddressA);

        TestDelegate act = () => stateProvider.ValidateBlockAccessList(Build.A.BlockHeader.TestObject, 1, 10_000);

        Assert.Throws<ParallelWorldState.InvalidBlockLevelAccessListException>(act);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void ParallelWorldState_bal_generation_skips_pre_execution_system_account_read()
    {
        ParallelWorldState stateProvider = new(TestWorldStateFactory.CreateForTest())
        {
            TracingEnabled = true
        };

        using (stateProvider.BeginSystemAccountReadSuppression())
        {
            stateProvider.AddAccountRead(Address.SystemUser);
        }

        Assert.That(stateProvider.GeneratedBlockAccessList.AccountChanges, Is.Empty);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void ParallelWorldState_bal_generation_suppresses_system_account_read_across_multiple_system_call_indexes()
    {
        ParallelWorldState stateProvider = new(TestWorldStateFactory.CreateForTest())
        {
            TracingEnabled = true
        };

        AddSystemCallAccountReads(TestItem.AddressA);
        AddSystemCallAccountReads(TestItem.AddressB);

        stateProvider.GeneratedBlockAccessList.IncrementBlockAccessIndex();
        stateProvider.GeneratedBlockAccessList.IncrementBlockAccessIndex();

        AddSystemCallAccountReads(TestItem.AddressC);
        AddSystemCallAccountReads(TestItem.AddressD);

        Assert.That(stateProvider.GeneratedBlockAccessList.GetAccountChanges(Address.SystemUser), Is.Null);
        Assert.That(stateProvider.GeneratedBlockAccessList.GetAccountChanges(TestItem.AddressA), Is.Not.Null);
        Assert.That(stateProvider.GeneratedBlockAccessList.GetAccountChanges(TestItem.AddressB), Is.Not.Null);
        Assert.That(stateProvider.GeneratedBlockAccessList.GetAccountChanges(TestItem.AddressC), Is.Not.Null);
        Assert.That(stateProvider.GeneratedBlockAccessList.GetAccountChanges(TestItem.AddressD), Is.Not.Null);

        void AddSystemCallAccountReads(Address target)
        {
            using (stateProvider.BeginSystemAccountReadSuppression())
            {
                stateProvider.AddAccountRead(Address.SystemUser);
                stateProvider.AddAccountRead(target);
            }
        }
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void ParallelWorldState_bal_validation_rejects_surplus_pre_execution_system_account_read()
    {
        ParallelWorldState stateProvider = new(TestWorldStateFactory.CreateForTest());
        BlockAccessList suggestedBlockAccessList = new();
        suggestedBlockAccessList.AddAccountRead(Address.SystemUser);
        stateProvider.LoadSuggestedBlockAccessList(suggestedBlockAccessList, 10_000);
        Block block = Build.A.Block.WithNumber(1).TestObject;

        Assert.That(() => stateProvider.ValidateBlockAccessList(block.Header, 0, 10_000), Throws.Nothing);

        TestDelegate act = () => stateProvider.SetBlockAccessList(block, Amsterdam.Instance);
        Assert.Throws<ParallelWorldState.InvalidBlockLevelAccessListException>(act);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void ParallelWorldState_bal_validation_allows_system_account_changed_after_pre_execution()
    {
        ParallelWorldState stateProvider = new(TestWorldStateFactory.CreateForTest());
        BlockAccessList suggestedBlockAccessList = new();
        suggestedBlockAccessList.IncrementBlockAccessIndex();
        suggestedBlockAccessList.AddBalanceChange(Address.SystemUser, 0, 1);
        stateProvider.LoadSuggestedBlockAccessList(suggestedBlockAccessList, 10_000);
        BlockHeader header = Build.A.BlockHeader.WithNumber(1).TestObject;

        Assert.That(() => stateProvider.ValidateBlockAccessList(header, 0, 10_000), Throws.Nothing);

        stateProvider.GeneratedBlockAccessList.IncrementBlockAccessIndex();
        stateProvider.GeneratedBlockAccessList.AddBalanceChange(Address.SystemUser, 0, 1);

        Assert.That(() => stateProvider.ValidateBlockAccessList(header, 1, 10_000), Throws.Nothing);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void ParallelWorldState_bal_validation_rejects_missing_explicit_system_account_read()
    {
        ParallelWorldState stateProvider = new(TestWorldStateFactory.CreateForTest())
        {
            TracingEnabled = true
        };
        stateProvider.LoadSuggestedBlockAccessList(new BlockAccessList(), 10_000);
        stateProvider.GeneratedBlockAccessList.IncrementBlockAccessIndex();
        stateProvider.AddAccountRead(Address.SystemUser);

        TestDelegate act = () => stateProvider.ValidateBlockAccessList(Build.A.BlockHeader.TestObject, 1, 10_000);

        Assert.Throws<ParallelWorldState.InvalidBlockLevelAccessListException>(act);
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
        IWorldState stateProvider = new WorldStateStab();

        using IDisposable _ = stateProvider.BeginScope(IWorldState.PreGenesis);

        Transaction? addedTransaction = null;
        txPicker.AddingTransaction += (s, e) => addedTransaction = e.Transaction;

        txPicker.CanAddTransaction(newBlock, transactionWithNetworkForm, new HashSet<Transaction>(), stateProvider.GetUntrackedReader());

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
        public void Dispose() { }
    }

    private sealed class TrackingBlockAccessListWorldState(IWorldState innerWorldState)
        : WrappedWorldState(innerWorldState), IBlockAccessListBuilder
    {
        public bool TracingEnabled { get; set; }
        public BlockAccessList GeneratedBlockAccessList { get; set; } = new();
        public List<long> ValidatedGasRemaining { get; } = [];
        public Action<ushort, long>? OnValidate { get; set; }

        private long _gasUsed;

        public void AddAccountRead(Address address)
        {
        }

        public void LoadSuggestedBlockAccessList(BlockAccessList suggested, long gasUsed) => _gasUsed = gasUsed;

        public long GasUsed()
            => _gasUsed;

        public void ValidateBlockAccessList(BlockHeader block, ushort index, long gasRemaining)
        {
            OnValidate?.Invoke(index, gasRemaining);
            ValidatedGasRemaining.Add(gasRemaining);
        }

        public void SetBlockAccessList(Block block, IReleaseSpec spec) { }
        public IDisposable BeginSystemAccountReadSuppression() => EmptyDisposable.Instance;

        private sealed class EmptyDisposable : IDisposable
        {
            public static EmptyDisposable Instance { get; } = new();
            public void Dispose() { }
        }
    }

    private static BlockProcessor CreateBalTestBlockProcessor(
        IWorldState stateProvider,
        ITransactionProcessorAdapter transactionProcessor,
        IExecutionRequestsProcessor executionRequestsProcessor,
        IReleaseSpec spec)
    {
        ITransactionProcessor systemTransactionProcessor = Substitute.For<ITransactionProcessor>();
        return new(
            new TestSingleReleaseSpecProvider(spec),
            TestBlockValidator.AlwaysValid,
            NoBlockRewards.Instance,
            new BlockProcessor.BlockValidationTransactionsExecutor(transactionProcessor, stateProvider),
            stateProvider,
            NullReceiptStorage.Instance,
            new BeaconBlockRootHandler(systemTransactionProcessor, stateProvider),
            Substitute.For<IBlockhashStore>(),
            LimboLogs.Instance,
            new WithdrawalProcessor(stateProvider, LimboLogs.Instance),
            executionRequestsProcessor);
    }

    private static ITransactionProcessorAdapter SuccessfulTransactionProcessor(long blockGasUsed = 37_568)
    {
        ITransactionProcessorAdapter transactionProcessor = Substitute.For<ITransactionProcessorAdapter>();
        transactionProcessor.Execute(Arg.Any<Transaction>(), Arg.Any<ITxTracer>()).Returns(callInfo =>
        {
            Transaction transaction = callInfo.Arg<Transaction>();
            transaction.SpentGas = blockGasUsed;
            transaction.BlockGasUsed = blockGasUsed;
            return TransactionResult.Ok;
        });

        return transactionProcessor;
    }

    private static BlockAccessList BlockAccessListWithAccountReads(int count)
    {
        BlockAccessList blockAccessList = new();
        for (int i = 0; i < count; i++)
        {
            blockAccessList.AddAccountRead(Address.FromNumber((UInt256)(ulong)(i + 1)));
        }

        return blockAccessList;
    }

    public static IEnumerable<TestCaseData> BlockProcessor_bal_validation_cases()
    {
        yield return new TestCaseData(ProcessingOptions.None, true)
            .SetName("BlockProcessor_uses_block_gas_for_bal_validation_budget");
        yield return new TestCaseData(ProcessingOptions.NoValidation, false)
            .SetName("BlockProcessor_skips_bal_validation_when_no_validation_requested");
    }
}
