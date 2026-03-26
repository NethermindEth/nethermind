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
using Nethermind.Core.Crypto;
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
        (BlockProcessor processor, _, IWorldState stateProvider) = CreateProcessorAndBranch();

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
        TokenCapturingPreWarmer preWarmer = new();
        (_, BranchProcessor branchProcessor, _) = CreateProcessorAndBranch(preWarmer: preWarmer);

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

        externalHandlerCallCount.Should().Be(1, "only the externally subscribed handler should fire, BranchProcessor should have unsubscribed");
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void BlockValidationTransactionsExecutor_uses_block_gas_for_bal_validation_budget()
    {
        TrackingBlockAccessListWorldState stateProvider = new(TestWorldStateFactory.CreateForTest());
        stateProvider.LoadSuggestedBlockAccessList(new BlockAccessList(), 37_568);

        ITransactionProcessorAdapter transactionProcessor = Substitute.For<ITransactionProcessorAdapter>();
        transactionProcessor.Execute(Arg.Any<Transaction>(), Arg.Any<ITxTracer>()).Returns(static callInfo =>
        {
            Transaction transaction = callInfo.Arg<Transaction>();
            transaction.SpentGas = 63_586;
            transaction.BlockGasUsed = 37_568;
            return TransactionResult.Ok;
        });

        BlockProcessor.BlockValidationTransactionsExecutor txExecutor = new(transactionProcessor, stateProvider);
        Block block = Build.A.Block.WithTransactions(Build.A.Transaction.SignedAndResolved().TestObject).TestObject;
        BlockReceiptsTracer receiptsTracer = new();
        receiptsTracer.StartNewBlockTrace(block);

        txExecutor.ProcessTransactions(block, ProcessingOptions.NoValidation, receiptsTracer, CancellationToken.None);

        stateProvider.ValidatedGasRemaining.Should().Equal([37_568L, 0L]);
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

        processedBlocks.Should().HaveCount(1, "block should process successfully without a prewarmer");
    }

    private static (TrackingPreWarmer preWarmer, BranchProcessor branchProcessor) CreateTrackingBranch(
        IRewardCalculator? rewardCalculator = null)
    {
        TrackingPreWarmer preWarmer = new();
        (_, BranchProcessor branchProcessor, _) = CreateProcessorAndBranch(
            rewardCalculator: rewardCalculator, preWarmer: preWarmer);
        return (preWarmer, branchProcessor);
    }

    private static BlockHeader BuildBaseBlock(string label) =>
        Build.A.BlockHeader
            .WithNumber(0)
            .WithHash(Keccak.Compute(label))
            .WithStateRoot(Keccak.EmptyTreeHash)
            .TestObject;

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void BranchProcessor_sequential_blocks_validate_once_and_finalize_each_block()
    {
        (TrackingPreWarmer preWarmer, BranchProcessor branchProcessor) = CreateTrackingBranch();
        BlockHeader baseBlock = BuildBaseBlock("base-a");
        Block block1 = Build.A.Block.WithParent(baseBlock).WithTransactions(3, MuirGlacier.Instance).TestObject;
        Block block2 = Build.A.Block.WithParent(block1).WithTransactions(3, MuirGlacier.Instance).TestObject;

        branchProcessor.Process(baseBlock, new List<Block> { block1, block2 },
            ProcessingOptions.NoValidation, NullBlockTracer.Instance);

        preWarmer.FinalizedBlocks.Should().Equal(block1.Header, block2.Header);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void BranchProcessor_non_sequential_block_invalidates_caches()
    {
        (TrackingPreWarmer preWarmer, BranchProcessor branchProcessor) = CreateTrackingBranch();
        BlockHeader baseBlock = BuildBaseBlock("base-b");
        Block blockA = Build.A.Block.WithParent(baseBlock).WithTransactions(3, MuirGlacier.Instance).TestObject;

        branchProcessor.Process(baseBlock, new List<Block> { blockA },
            ProcessingOptions.NoValidation, NullBlockTracer.Instance);

        BlockHeader mismatchedBase = Build.A.BlockHeader
            .WithNumber(1).WithHash(Keccak.Compute("base-c")).WithStateRoot(Keccak.EmptyTreeHash).TestObject;
        Block blockB = Build.A.Block.WithParent(mismatchedBase).WithTransactions(3, MuirGlacier.Instance).TestObject;

        branchProcessor.Process(mismatchedBase, new List<Block> { blockB },
            ProcessingOptions.NoValidation, NullBlockTracer.Instance);

        preWarmer.InvalidateCount.Should().Be(0, "cache validity is now checked in BeginScope, not on the prewarmer");
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void BranchProcessor_error_path_invalidates_caches_and_skips_finalize()
    {
        (TrackingPreWarmer preWarmer, BranchProcessor branchProcessor) =
            CreateTrackingBranch(rewardCalculator: new RewardCalculator(MainnetSpecProvider.Instance));
        BlockHeader baseBlock = BuildBaseBlock("base-d");
        Block block = Build.A.Block.WithParent(baseBlock).WithTransactions(1, MuirGlacier.Instance).TestObject;

        Assert.Throws<OperationCanceledException>(() => branchProcessor.Process(
            baseBlock, new List<Block> { block }, ProcessingOptions.None, AlwaysCancelBlockTracer.Instance));

        preWarmer.InvalidateCount.Should().BeGreaterThanOrEqualTo(1);
        preWarmer.FinalizedBlocks.Should().BeEmpty();
    }

    [Test]
    public void BranchProcessor_null_base_block_invalidates_caches()
    {
        (TrackingPreWarmer preWarmer, BranchProcessor branchProcessor) = CreateTrackingBranch();
        Block block = Build.A.Block.WithTransactions(3, MuirGlacier.Instance).TestObject;

        branchProcessor.Process(null, new List<Block> { block },
            ProcessingOptions.NoValidation, NullBlockTracer.Instance);

        preWarmer.InvalidateCount.Should().Be(0, "cache validity is now checked in BeginScope, not on the prewarmer");
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

        public void ClearCaches() { }
        public void InvalidateCaches() { }
        public void FinalizeProcessedBlock(BlockHeader block, IReleaseSpec spec) { }
        public void FlushCarryForwardWrites() { }
    }

    private sealed class TrackingPreWarmer : IBlockCachePreWarmer
    {
        private readonly PreBlockCaches _preBlockCaches = new();

        public List<BlockHeader> FinalizedBlocks { get; } = [];
        public int InvalidateCount { get; private set; }

        public Task PreWarmCaches(Block suggestedBlock, BlockHeader? parent, IReleaseSpec spec, CancellationToken cancellationToken = default, params ReadOnlySpan<IHasAccessList> systemAccessLists)
            => Task.CompletedTask;

        public void ClearCaches() { }

        public void InvalidateCaches()
        {
            InvalidateCount++;
            _preBlockCaches.InvalidateCaches();
        }

        public void FinalizeProcessedBlock(BlockHeader block, IReleaseSpec spec)
        {
            FinalizedBlocks.Add(block);
            _preBlockCaches.RecordCommittedBlock(block.Number, block.Hash);
        }

        public void FlushCarryForwardWrites()
        {
            _preBlockCaches.FlushCarryForwardWrites();
        }
    }

    private sealed class TrackingBlockAccessListWorldState(IWorldState innerWorldState)
        : WrappedWorldState(innerWorldState), IBlockAccessListBuilder
    {
        public bool TracingEnabled { get; set; }
        public BlockAccessList GeneratedBlockAccessList { get; set; } = new();
        public List<long> ValidatedGasRemaining { get; } = [];

        private long _gasUsed;

        public void AddAccountRead(Address address)
        {
        }

        public void LoadSuggestedBlockAccessList(BlockAccessList suggested, long gasUsed) => _gasUsed = gasUsed;

        public long GasUsed()
            => _gasUsed;

        public void ValidateBlockAccessList(BlockHeader block, ushort index, long gasRemaining)
            => ValidatedGasRemaining.Add(gasRemaining);

        public void SetBlockAccessList(Block block, IReleaseSpec spec) { }
    }
}
