// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
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
    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Prepared_block_contains_author_field()
    {
        IWorldState stateProvider = TestWorldStateFactory.CreateForTest();
        ITransactionProcessor transactionProcessor = Substitute.For<ITransactionProcessor>();
        BlockProcessor processor = new(HoodiSpecProvider.Instance,
            TestBlockValidator.AlwaysValid,
            NoBlockRewards.Instance,
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
            LimboLogs.Instance);

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
        IWorldState stateProvider = TestWorldStateFactory.CreateForTest();
        ITransactionProcessor transactionProcessor = Substitute.For<ITransactionProcessor>();
        BlockProcessor processor = new(
            HoodiSpecProvider.Instance,
            TestBlockValidator.AlwaysValid,
            new RewardCalculator(MainnetSpecProvider.Instance),
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
            LimboLogs.Instance);

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
        await testRpc.AddFunds(address, 1.Ether());
        await testRpc.AddBlock();
        SemaphoreSlim suggestedBlockResetEvent = new SemaphoreSlim(0);
        testRpc.BlockTree.NewHeadBlock += (_, _) =>
        {
            suggestedBlockResetEvent.Release(1);
        };

        int branchLength = blocksAmount + (int)testRpc.BlockTree.BestKnownNumber + 1;
        ((BlockTree)testRpc.BlockTree).AddBranch(branchLength, (int)testRpc.BlockTree.BestKnownNumber);
        (await suggestedBlockResetEvent.WaitAsync(TestBlockchain.DefaultTimeout * 10)).Should().BeTrue();
        Assert.That((int)testRpc.BlockTree.BestKnownNumber, Is.EqualTo(branchLength - 1));
    }


    [Test, MaxTime(Timeout.MaxTestTime)]
    public void TransactionsExecuted_event_fires_during_ProcessOne()
    {
        IWorldState stateProvider = TestWorldStateFactory.CreateForTest();
        ITransactionProcessor transactionProcessor = Substitute.For<ITransactionProcessor>();
        BlockProcessor processor = new(HoodiSpecProvider.Instance,
            TestBlockValidator.AlwaysValid,
            NoBlockRewards.Instance,
            new BlockProcessor.BlockValidationTransactionsExecutor(new ExecuteTransactionProcessorAdapter(transactionProcessor), stateProvider),
            stateProvider,
            NullReceiptStorage.Instance,
            new BeaconBlockRootHandler(transactionProcessor, stateProvider),
            Substitute.For<IBlockhashStore>(),
            LimboLogs.Instance,
            new WithdrawalProcessor(stateProvider, LimboLogs.Instance),
            new ExecutionRequestsProcessor(transactionProcessor));

        bool eventFired = false;
        processor.TransactionsExecuted += () => eventFired = true;

        using IDisposable scope = stateProvider.BeginScope(null);
        BlockHeader header = Build.A.BlockHeader.WithAuthor(TestItem.AddressD).TestObject;
        Block block = Build.A.Block.WithHeader(header).TestObject;
        IReleaseSpec spec = HoodiSpecProvider.Instance.GetSpec(block.Header);

        processor.ProcessOne(block, ProcessingOptions.NoValidation, NullBlockTracer.Instance, spec, CancellationToken.None);

        eventFired.Should().BeTrue("TransactionsExecuted should fire after ProcessTransactions completes");
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void BranchProcessor_cancels_prewarmer_via_TransactionsExecuted_event()
    {
        IWorldState stateProvider = TestWorldStateFactory.CreateForTest();
        ITransactionProcessor transactionProcessor = Substitute.For<ITransactionProcessor>();
        BlockProcessor processor = new(HoodiSpecProvider.Instance,
            TestBlockValidator.AlwaysValid,
            NoBlockRewards.Instance,
            new BlockProcessor.BlockValidationTransactionsExecutor(new ExecuteTransactionProcessorAdapter(transactionProcessor), stateProvider),
            stateProvider,
            NullReceiptStorage.Instance,
            new BeaconBlockRootHandler(transactionProcessor, stateProvider),
            Substitute.For<IBlockhashStore>(),
            LimboLogs.Instance,
            new WithdrawalProcessor(stateProvider, LimboLogs.Instance),
            new ExecutionRequestsProcessor(transactionProcessor));

        // Manual prewarmer implementation that captures the CancellationToken.
        // NSubstitute can't proxy ReadOnlySpan<T> (ref struct) parameters.
        TokenCapturingPreWarmer preWarmer = new();

        BranchProcessor branchProcessor = new(
            processor,
            HoodiSpecProvider.Instance,
            stateProvider,
            new BeaconBlockRootHandler(transactionProcessor, stateProvider),
            Substitute.For<IBlockhashProvider>(),
            LimboLogs.Instance,
            preWarmer);

        // Build a block with >= 3 txs so prewarming activates
        BlockHeader header = Build.A.BlockHeader.WithAuthor(TestItem.AddressD).TestObject;
        Block block = Build.A.Block.WithHeader(header).WithTransactions(3, MuirGlacier.Instance).TestObject;

        branchProcessor.Process(
            null,
            new List<Block> { block },
            ProcessingOptions.NoValidation,
            NullBlockTracer.Instance);

        preWarmer.CapturedToken.IsCancellationRequested.Should().BeTrue(
            "prewarmer CancellationToken should be cancelled via TransactionsExecuted event after tx processing");
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void BranchProcessor_unsubscribes_from_TransactionsExecuted_after_processing()
    {
        IWorldState stateProvider = TestWorldStateFactory.CreateForTest();
        ITransactionProcessor transactionProcessor = Substitute.For<ITransactionProcessor>();
        BlockProcessor processor = new(HoodiSpecProvider.Instance,
            TestBlockValidator.AlwaysValid,
            NoBlockRewards.Instance,
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
            LimboLogs.Instance);

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

        externalHandlerCallCount.Should().Be(1, "only the externally subscribed handler should fire, BranchProcessor should have unsubscribed");
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void BranchProcessor_no_prewarmer_still_processes_successfully()
    {
        IWorldState stateProvider = TestWorldStateFactory.CreateForTest();
        ITransactionProcessor transactionProcessor = Substitute.For<ITransactionProcessor>();
        BlockProcessor processor = new(HoodiSpecProvider.Instance,
            TestBlockValidator.AlwaysValid,
            NoBlockRewards.Instance,
            new BlockProcessor.BlockValidationTransactionsExecutor(new ExecuteTransactionProcessorAdapter(transactionProcessor), stateProvider),
            stateProvider,
            NullReceiptStorage.Instance,
            new BeaconBlockRootHandler(transactionProcessor, stateProvider),
            Substitute.For<IBlockhashStore>(),
            LimboLogs.Instance,
            new WithdrawalProcessor(stateProvider, LimboLogs.Instance),
            new ExecutionRequestsProcessor(transactionProcessor));

        // No prewarmer — TransactionsExecuted event still fires but cancel is a no-op
        BranchProcessor branchProcessor = new(
            processor,
            HoodiSpecProvider.Instance,
            stateProvider,
            new BeaconBlockRootHandler(transactionProcessor, stateProvider),
            Substitute.For<IBlockhashProvider>(),
            LimboLogs.Instance,
            preWarmer: null);

        BlockHeader header = Build.A.BlockHeader.WithAuthor(TestItem.AddressD).TestObject;
        Block block = Build.A.Block.WithHeader(header).WithTransactions(3, MuirGlacier.Instance).TestObject;

        Block[] processedBlocks = branchProcessor.Process(
            null,
            new List<Block> { block },
            ProcessingOptions.NoValidation,
            NullBlockTracer.Instance);

        processedBlocks.Should().HaveCount(1, "block should process successfully without a prewarmer");
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

        BlockProcessor.BlockProductionTransactionPicker txPicker = new(specProvider, transactionWithNetworkForm.GetLength(true) / 1.KiB() - 1);
        BlockToProduce newBlock = new(Build.A.BlockHeader.WithExcessBlobGas(0).TestObject);
        WorldStateStab stateProvider = new();

        using var _ = stateProvider.BeginScope(IWorldState.PreGenesis);

        Transaction? addedTransaction = null;
        txPicker.AddingTransaction += (s, e) => addedTransaction = e.Transaction;

        txPicker.CanAddTransaction(newBlock, transactionWithNetworkForm, new HashSet<Transaction>(), stateProvider);

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
    }
}
