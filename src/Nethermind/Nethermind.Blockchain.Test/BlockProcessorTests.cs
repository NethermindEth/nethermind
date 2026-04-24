// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
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
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.JsonRpc.Test.Modules;
using Nethermind.Int256;
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
        IBlockProcessor.IBlockTransactionsExecutor transactionsExecutor = new BlockProcessor.ParallelBlockValidationTransactionsExecutor(new ExecuteTransactionProcessorAdapter(transactionProcessor), stateProvider, HoodiSpecProvider.Instance, balManager);
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
            new BlocksConfig(),
            preWarmer);

        return (processor, branchProcessor, stateProvider);
    }

    private static (IWorldState StateProvider, Hash256 StateRoot) CreateWorldStateWithEip2935Contract(IReleaseSpec spec)
    {
        IWorldState stateProvider = TestWorldStateFactory.CreateForTest();
        Hash256 stateRoot;

        using IDisposable _ = stateProvider.BeginScope(IWorldState.PreGenesis);
        stateProvider.CreateAccount(Eip2935Constants.BlockHashHistoryAddress, 0, Eip2935TestConstants.Nonce);
        stateProvider.InsertCode(Eip2935Constants.BlockHashHistoryAddress, Eip2935TestConstants.CodeHash, Eip2935TestConstants.Code, spec);
        stateProvider.Commit(spec);
        stateProvider.CommitTree(0);
        stateProvider.RecalculateStateRoot();
        stateRoot = stateProvider.StateRoot;

        return (stateProvider, stateRoot);
    }

    private static BlockAccessListManager CreateBalManager(IWorldState stateProvider, IReleaseSpec spec) =>
        new(
            stateProvider,
            new TestSingleReleaseSpecProvider(spec),
            Substitute.For<IBlockhashProvider>(),
            LimboLogs.Instance,
            new BlocksConfig { ParallelExecution = false },
            new WithdrawalProcessorFactory(LimboLogs.Instance));

    private static void SetupBalManager(BlockAccessListManager balManager, Block block, IReleaseSpec spec)
    {
        balManager.PrepareForProcessing(block, spec, ProcessingOptions.None);
        balManager.SetBlockExecutionContext(new BlockExecutionContext(block.Header, spec));
        balManager.Setup(block);
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
    public void ApplyBlockhashStateChanges_is_required_for_eip2935_pre_execution_bal_validation()
    {
        // Mirrors execution-specs `test_bal_2935_empty_block`: BAL index 0 must contain the
        // HISTORY_STORAGE_ADDRESS parent-hash write performed by the pre-execution system call.
        IReleaseSpec spec = Amsterdam.Instance;
        BlockHeader parent = Build.A.BlockHeader
            .WithNumber(0)
            .WithHash(TestItem.KeccakA)
            .TestObject;

        BlockAccessList expectedBal = Build.A.BlockAccessList
            .WithAccountChanges(
                Build.An.AccountChanges
                    .WithAddress(Eip2935Constants.BlockHashHistoryAddress)
                    .WithStorageChanges(
                        0,
                        new StorageChange(0, new UInt256(parent.Hash!.BytesToArray(), isBigEndian: true)))
                    .TestObject)
            .TestObject;

        (IWorldState stateWithoutPreExecutionCall, Hash256 stateRootWithoutPreExecutionCall) = CreateWorldStateWithEip2935Contract(spec);
        Block blockWithoutPreExecutionCall = Build.A.Block
            .WithHeader(Build.A.BlockHeader.WithParent(parent).WithStateRoot(stateRootWithoutPreExecutionCall).TestObject)
            .WithBlockAccessList(expectedBal)
            .TestObject;

        using (IDisposable _ = stateWithoutPreExecutionCall.BeginScope(blockWithoutPreExecutionCall.Header))
        {
            BlockAccessListManager balManagerWithoutPreExecutionCall = CreateBalManager(stateWithoutPreExecutionCall, spec);
            SetupBalManager(balManagerWithoutPreExecutionCall, blockWithoutPreExecutionCall, spec);
            balManagerWithoutPreExecutionCall.NextTransaction();

            Assert.Throws<BlockAccessListBasedWorldState.InvalidBlockLevelAccessListException>(
                () => balManagerWithoutPreExecutionCall.ValidateBlockAccessList(blockWithoutPreExecutionCall, 0));
        }

        (IWorldState stateWithPreExecutionCall, Hash256 stateRootWithPreExecutionCall) = CreateWorldStateWithEip2935Contract(spec);
        Block blockWithPreExecutionCall = Build.A.Block
            .WithHeader(Build.A.BlockHeader.WithParent(parent).WithStateRoot(stateRootWithPreExecutionCall).TestObject)
            .WithBlockAccessList(expectedBal)
            .TestObject;

        using (IDisposable _ = stateWithPreExecutionCall.BeginScope(blockWithPreExecutionCall.Header))
        {
            BlockAccessListManager balManagerWithPreExecutionCall = CreateBalManager(stateWithPreExecutionCall, spec);
            SetupBalManager(balManagerWithPreExecutionCall, blockWithPreExecutionCall, spec);
            balManagerWithPreExecutionCall.ApplyBlockhashStateChanges(blockWithPreExecutionCall.Header, spec);
            balManagerWithPreExecutionCall.NextTransaction();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(balManagerWithPreExecutionCall.GeneratedBlockAccessList, Is.EqualTo(expectedBal));
                Assert.That(balManagerWithPreExecutionCall.GeneratedBlockAccessList.GetAccountChanges(Address.SystemUser), Is.Null);
                Assert.DoesNotThrow(() => balManagerWithPreExecutionCall.ValidateBlockAccessList(blockWithPreExecutionCall, 0));
            }
        }
    }
}
