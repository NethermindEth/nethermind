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
using Nethermind.Core.Exceptions;
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
using Nethermind.Evm;
using Nethermind.Core.Threading;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;

namespace Nethermind.Blockchain.Test;

[Parallelizable(ParallelScope.All)]
public class BlockProcessorTests
{
    [Test]
    public void ApplyStateChanges_uses_parent_state_without_prestate_sentinels()
    {
        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList
            .WithAccountChanges(Build.An.AccountChanges
                .WithAddress(TestItem.AddressA)
                .WithBalanceChanges(new BalanceChange(0, 150))
                .WithNonceChanges(new NonceChange(0, 3))
                .WithStorageChanges(1, new StorageChange(0, 0x2Au))
                .TestObject)
            .TestObject;

        ApplyStateChangesInParentScope(
            bal,
            genesisSetup: stateProvider => stateProvider.CreateAccount(TestItem.AddressA, 100),
            assertState: stateProvider =>
            {
                StorageCell storageCell = new(TestItem.AddressA, 1);
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(stateProvider.GetBalance(TestItem.AddressA), Is.EqualTo((UInt256)150));
                    Assert.That(stateProvider.GetNonce(TestItem.AddressA), Is.EqualTo(3ul));
                    Assert.That(new UInt256(stateProvider.Get(storageCell), isBigEndian: true), Is.EqualTo((UInt256)0x2Au));
                }
            });
    }

    [Test]
    public void ApplyStateChanges_creates_missing_account_from_balance_change()
    {
        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList
            .WithAccountChanges(Build.An.AccountChanges
                .WithAddress(TestItem.AddressA)
                .WithBalanceChanges(new BalanceChange(0, 25))
                .TestObject)
            .TestObject;

        ApplyStateChangesInParentScope(
            bal,
            genesisSetup: null,
            assertState: stateProvider =>
            {
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(stateProvider.AccountExists(TestItem.AddressA), Is.True);
                    Assert.That(stateProvider.GetBalance(TestItem.AddressA), Is.EqualTo((UInt256)25));
                }
            });
    }

    [Test]
    public void Parallel_validation_parent_reader_scope_is_per_worker_and_disposed_on_return()
    {
        IWorldState stateProvider = TestWorldStateFactory.CreateForTest();
        Hash256 parentStateRoot;
        using (stateProvider.BeginScope(IWorldState.PreGenesis))
        {
            stateProvider.Commit(Amsterdam.Instance, isGenesis: true);
            stateProvider.CommitTree(0);
            parentStateRoot = stateProvider.StateRoot;
        }

        Hash256 parentHash = TestItem.KeccakA;
        BlockHeader parentHeader = Build.A.BlockHeader
            .WithNumber(6)
            .WithHash(parentHash)
            .WithStateRoot(parentStateRoot)
            .TestObject;

        using IDisposable parentScope = stateProvider.BeginScope(parentHeader);
        TrackingReadOnlyTxProcessingEnvFactory parentReaderFactory = new();
        using BlockAccessListManager balManager = new(
            stateProvider,
            new TestSingleReleaseSpecProvider(Amsterdam.Instance),
            Substitute.For<IBlockhashProvider>(),
            LimboLogs.Instance,
            new BlocksConfig { ParallelExecution = true },
            new WithdrawalProcessorFactory(LimboLogs.Instance),
            CodeInfoRepositoryFactories.Caching,
            readOnlyTxProcessingEnvFactory: parentReaderFactory);

        Transaction firstTx = Build.A.Transaction.WithNonce(0).TestObject;
        Transaction secondTx = Build.A.Transaction.WithNonce(1).TestObject;
        Block block = Build.A.Block
            .WithNumber(7)
            .WithParentHash(parentHash)
            .WithTransactions(firstTx, secondTx)
            .WithBlockAccessList(new ReadOnlyBlockAccessList())
            .TestObject;

        PrepareSetup(balManager, block, Amsterdam.Instance);

        _ = balManager.GetTxProcessor(1);
        _ = balManager.GetTxProcessor(2);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(parentReaderFactory.CreatedSources, Is.EqualTo(2));
            Assert.That(parentReaderFactory.BuiltHeaders.Count, Is.EqualTo(2));
            Assert.That(parentReaderFactory.BuiltWorldStates.Count, Is.EqualTo(2));
            Assert.That(parentReaderFactory.BuiltWorldStates[0], Is.Not.SameAs(parentReaderFactory.BuiltWorldStates[1]));
            Assert.That(parentReaderFactory.DisposedScopes, Is.EqualTo(0));
        }

        for (int i = 0; i < parentReaderFactory.BuiltHeaders.Count; i++)
        {
            BlockHeader builtHeader = parentReaderFactory.BuiltHeaders[i]!;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(builtHeader.Number, Is.EqualTo(6));
                Assert.That(builtHeader.Hash, Is.EqualTo(parentHash));
                Assert.That(builtHeader.StateRoot, Is.EqualTo(parentStateRoot));
            }
        }

        balManager.ReturnTxProcessor(1);
        balManager.ReturnTxProcessor(2);

        Assert.That(parentReaderFactory.DisposedScopes, Is.EqualTo(2));
    }

    [Test]
    public void Parallel_validation_parent_reader_uses_parent_root_captured_before_pre_block_changes()
    {
        IWorldState stateProvider = TestWorldStateFactory.CreateForTest();
        Hash256 parentStateRoot;
        using (stateProvider.BeginScope(IWorldState.PreGenesis))
        {
            stateProvider.Commit(Amsterdam.Instance, isGenesis: true);
            stateProvider.CommitTree(0);
            parentStateRoot = stateProvider.StateRoot;
        }

        Hash256 parentHash = TestItem.KeccakA;
        BlockHeader parentHeader = Build.A.BlockHeader
            .WithNumber(6)
            .WithHash(parentHash)
            .WithStateRoot(parentStateRoot)
            .TestObject;

        using IDisposable parentScope = stateProvider.BeginScope(parentHeader);
        TrackingReadOnlyTxProcessingEnvFactory parentReaderFactory = new();
        using BlockAccessListManager balManager = new(
            stateProvider,
            new TestSingleReleaseSpecProvider(Amsterdam.Instance),
            Substitute.For<IBlockhashProvider>(),
            LimboLogs.Instance,
            new BlocksConfig { ParallelExecution = true },
            new WithdrawalProcessorFactory(LimboLogs.Instance),
            CodeInfoRepositoryFactories.Caching,
            readOnlyTxProcessingEnvFactory: parentReaderFactory);

        Transaction tx = Build.A.Transaction.WithNonce(0).TestObject;
        Block block = Build.A.Block
            .WithNumber(7)
            .WithParentHash(parentHash)
            .WithTransactions(tx)
            .WithBlockAccessList(new ReadOnlyBlockAccessList())
            .TestObject;

        balManager.PrepareForProcessing(block, Amsterdam.Instance, ProcessingOptions.None);

        stateProvider.CreateAccount(TestItem.AddressB, 1);
        stateProvider.Commit(Amsterdam.Instance, commitRoots: false);

        balManager.SetBlockExecutionContext(new(block.Header, Amsterdam.Instance));
        Assert.DoesNotThrow(() => balManager.Setup(block));

        _ = balManager.GetTxProcessor(1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(parentReaderFactory.BuiltHeaders.Count, Is.EqualTo(1));
            Assert.That(parentReaderFactory.BuiltHeaders[0]!.StateRoot, Is.EqualTo(parentStateRoot));
        }

        balManager.ReturnTxProcessor(1);
    }

    private static void ApplyStateChangesInParentScope(
        ReadOnlyBlockAccessList bal,
        Action<IWorldState>? genesisSetup,
        Action<IWorldState> assertState)
    {
        IWorldState stateProvider = TestWorldStateFactory.CreateForTest();
        Hash256 stateRoot;
        using (stateProvider.BeginScope(IWorldState.PreGenesis))
        {
            genesisSetup?.Invoke(stateProvider);
            stateProvider.Commit(Amsterdam.Instance, isGenesis: true);
            stateProvider.CommitTree(0);
            stateRoot = stateProvider.StateRoot;
        }

        BlockHeader parent = Build.A.BlockHeader.WithStateRoot(stateRoot).WithNumber(0).TestObject;
        using (stateProvider.BeginScope(parent))
        {
            BlockAccessListManager.ApplyStateChanges(bal, stateProvider, Amsterdam.Instance, shouldComputeStateRoot: false);
            assertState(stateProvider);
        }
    }

    private static (BlockProcessor processor, BranchProcessor branchProcessor, IWorldState stateProvider) CreateProcessorAndBranch(
        IRewardCalculator? rewardCalculator = null,
        IBlockCachePreWarmer? preWarmer = null)
    {
        IWorldState stateProvider = TestWorldStateFactory.CreateForTest();
        ITransactionProcessor transactionProcessor = Substitute.For<ITransactionProcessor>();
        BlockAccessListManager balManager = new(stateProvider, HoodiSpecProvider.Instance, Substitute.For<IBlockhashProvider>(), LimboLogs.Instance, new BlocksConfig(), new WithdrawalProcessorFactory(LimboLogs.Instance), CodeInfoRepositoryFactories.Caching);
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
            new BlockhashStore(stateProvider),
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

        int externalHandlerCallCount = 0;
        processor.TransactionsExecuted += () => externalHandlerCallCount++;

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
        public Task StartSpeculativePreWarm(BlockHeader head, IReleaseSpec spec, long generation, Func<CancellationToken, Block?> nextDelta, int idlePassDelayMs, CancellationToken cancellationToken) => Task.CompletedTask;
        public void Dispose() { }
    }

    public static IEnumerable<TestCaseData> BlockValidationTransactionsExecutor_bal_validation_cases()
    {
        yield return new TestCaseData(ProcessingOptions.None, true)
            .SetName("BlockValidationTransactionsExecutor_uses_block_gas_for_bal_validation_budget");
        yield return new TestCaseData(ProcessingOptions.NoValidation, false)
            .SetName("BlockValidationTransactionsExecutor_skips_bal_validation_when_no_validation_requested");
    }

    [TestCase(2000ul, false, TestName = "BAL_read_budget_at_2000_gas_passes")]
    [TestCase(1999ul, true, TestName = "BAL_read_budget_at_1999_gas_fails")]
    public void ValidateBlockAccessList_storage_read_budget_uses_ItemCost(ulong gasRemaining, bool shouldThrow)
    {
        // One extra storage read in suggested BAL costs Eip7928Constants.ItemCost (2000) gas
        IWorldState stateProvider = TestWorldStateFactory.CreateForTest();
        BlockAccessListManager balManager = new(
            stateProvider,
            new TestSingleReleaseSpecProvider(Amsterdam.Instance),
            Substitute.For<IBlockhashProvider>(),
            LimboLogs.Instance,
            new BlocksConfig { ParallelExecution = false },
            new WithdrawalProcessorFactory(LimboLogs.Instance),
            CodeInfoRepositoryFactories.Caching);

        // Prepare with a block that has gasUsed = gasRemaining (sets _gasRemaining)
        ReadOnlyBlockAccessList suggestedBal = Build.A.BlockAccessList
            .WithAccountChanges(Build.An.AccountChanges
                .WithAddress(TestItem.AddressA)
                .WithStorageReads(1)
                .TestObject)
            .TestObject;

        Block block = Build.A.Block
            .WithNumber(1ul)
            .WithGasUsed(gasRemaining)
            .WithBlockAccessList(suggestedBal)
            .TestObject;

        PrepareSetup(balManager, block, Amsterdam.Instance);
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
    public void ValidateBlockAccessList_matches_accounts_by_address_when_insertion_order_differs()
    {
        IWorldState stateProvider = TestWorldStateFactory.CreateForTest();
        BlockAccessListManager balManager = new(
            stateProvider,
            new TestSingleReleaseSpecProvider(Amsterdam.Instance),
            Substitute.For<IBlockhashProvider>(),
            LimboLogs.Instance,
            new BlocksConfig { ParallelExecution = false },
            new WithdrawalProcessorFactory(LimboLogs.Instance),
            CodeInfoRepositoryFactories.Caching);

        Address lowAddress = TestItem.AddressA;
        Address highAddress = TestItem.AddressB;
        if (lowAddress.CompareTo(highAddress) > 0)
        {
            (lowAddress, highAddress) = (highAddress, lowAddress);
        }

        // Build suggested BAL with accounts declared in high→low order so the validator must
        // match by address rather than by insertion order.
        ReadOnlyBlockAccessList suggestedBal = Build.A.BlockAccessList
            .WithAccountChanges(Build.An.AccountChanges
                .WithAddress(highAddress)
                .WithBalanceChanges(new BalanceChange(0, 2))
                .TestObject)
            .WithAccountChanges(Build.An.AccountChanges
                .WithAddress(lowAddress)
                .WithBalanceChanges(new BalanceChange(0, 1))
                .TestObject)
            .TestObject;

        Block block = Build.A.Block
            .WithNumber(1)
            .WithGasUsed(0)
            .WithBlockAccessList(suggestedBal)
            .TestObject;

        PrepareSetup(balManager, block, Amsterdam.Instance);

        // Generated BAL in low→high (canonical) order — index lookup must reconcile despite
        // the suggested side using the opposite insertion order.
        BlockAccessListAtIndex slice = new();
        slice.AddBalanceChange(lowAddress, before: 0, after: 1);
        slice.AddBalanceChange(highAddress, before: 0, after: 2);
        balManager.GeneratedBlockAccessList.Merge(slice);

        Assert.DoesNotThrow(() => balManager.ValidateBlockAccessList(block, 0));
    }

    // Verify-only structural-equivalence check: covers the mismatch classes the column-index
    // and gas-budget checks don't catch (their inputs slip through to the structural walk in
    // SetBlockAccessList).
    [TestCaseSource(nameof(VerifyOnlyStructuralMismatchCases))]
    public void SetBlockAccessList_verify_only_rejects_structural_mismatch(
        ReadOnlyBlockAccessList suggested,
        Action<BlockAccessListAtIndex> populateGenerated)
    {
        IWorldState stateProvider = TestWorldStateFactory.CreateForTest();
        using IDisposable scope = stateProvider.BeginScope(IWorldState.PreGenesis);
        using BlockAccessListManager balManager = CreateAmsterdamBalManager(stateProvider);

        Block block = Build.A.Block
            .WithNumber(1)
            .WithGasUsed(0)
            .WithBlockAccessList(suggested)
            .TestObject;

        PrepareSetup(balManager, block, Amsterdam.Instance);

        BlockAccessListAtIndex slice = new();
        populateGenerated(slice);
        balManager.GeneratedBlockAccessList.Merge(slice);

        Assert.Throws<BlockAccessListBasedWorldState.InvalidBlockLevelAccessListException>(
            () => balManager.SetBlockAccessList(block));
    }

    private static IEnumerable<TestCaseData> VerifyOnlyStructuralMismatchCases()
    {
        // storage_reads content mismatch (count matches, values differ): the one mismatch class
        // nothing else catches — column-index only tracks read counts via the gas-budget check.
        yield return new TestCaseData(
            Build.A.BlockAccessList
                .WithAccountChanges(Build.An.AccountChanges
                    .WithAddress(TestItem.AddressA)
                    .WithStorageReads((UInt256)5)
                    .TestObject)
                .TestObject,
            (Action<BlockAccessListAtIndex>)(s => s.AddStorageRead(TestItem.AddressA, (UInt256)7)))
            .SetName("storage_reads content mismatch (same count, different value)");

        // Per-account presence mismatch with matching count: suggested has AddressA, generated
        // touched AddressB — the structural walk catches via `generated.GetAccountChanges` miss.
        yield return new TestCaseData(
            Build.A.BlockAccessList
                .WithAccountChanges(Build.An.AccountChanges
                    .WithAddress(TestItem.AddressA)
                    .WithBalanceChanges(new BalanceChange(0, 1))
                    .TestObject)
                .TestObject,
            (Action<BlockAccessListAtIndex>)(s => s.AddBalanceChange(TestItem.AddressB, before: 0, after: 1)))
            .SetName("account presence mismatch (same count, different address)");
    }

    [TestCase(1)]
    [TestCase(2)]
    public void PrepareForProcessing_keeps_parallel_bal_execution_for_validated_eip8037_blocks(int txCount) =>
        WithScopedAmsterdamBalManager(balManager => AssertParallelBalExecutionEnabled(balManager, txCount));

    [Test]
    public void PrepareForProcessing_disables_parallel_bal_execution_when_state_provider_is_not_scoped()
    {
        using BlockAccessListManager balManager = CreateAmsterdamBalManager();
        Block block = Build.A.Block
            .WithNumber(1)
            .WithTransactions(1, Amsterdam.Instance)
            .WithBlockAccessList(new ReadOnlyBlockAccessList())
            .TestObject;

        balManager.PrepareForProcessing(block, Amsterdam.Instance, ProcessingOptions.None);
        balManager.SetBlockExecutionContext(new(block.Header, Amsterdam.Instance));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(balManager.ParallelExecutionEnabled, Is.False);
            Assert.DoesNotThrow(() => balManager.Setup(block));
        }
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

        PrepareSetup(balManager, block, Amsterdam.Instance);

        GasValidationResultSlot[] gasResults = BuildGasResults(block,
            (65_000, 0, null),
            (21_000, 0, null));

        InvalidBlockException? exception = Assert.Throws<InvalidBlockException>(() =>
            balManager.IncrementalValidation(block, gasResults, new BlockReceiptsTracer[2], null, CancellationToken.None));

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

        PrepareSetup(balManager, block, Amsterdam.Instance);

        GasValidationResultSlot[] gasResults = BuildGasResults(block,
            (80_000, 0, null),
            (21_000, 0, null));

        InvalidBlockException? exception = Assert.Throws<InvalidBlockException>(() =>
            balManager.IncrementalValidation(block, gasResults, new BlockReceiptsTracer[2], null, CancellationToken.None));

        Assert.That(exception!.Message, Does.Contain("Block gas limit exceeded"));
    }

    [Test]
    public void IncrementalValidation_eip8037_block_gas_check_uses_actual_state_dimension()
    {
        // CREATE tx GasLimit sized to pass the EIP-8037 worst-case inclusion check
        // (worst-case state = GasLimit - intrinsic.regular must <= state_available
        // = 250_000 - 60_000 = 190_000). A 165_000 limit gives worst-case state of
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
            .WithGasLimit(250_000)
            .WithTransactions(firstTx, createTx)
            .WithBlockAccessList(new ReadOnlyBlockAccessList())
            .TestObject;

        PrepareSetup(balManager, block, Amsterdam.Instance);

        GasValidationResultSlot[] gasResults = BuildGasResults(block,
            (0, 60_000, null),
            (50_000, GasCostOf.CreateState, null));

        Assert.DoesNotThrow(() =>
            balManager.IncrementalValidation(block, gasResults, new BlockReceiptsTracer[2], null, CancellationToken.None));
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

        PrepareSetup(balManager, block, Amsterdam.Instance);

        InvalidBlockException workerException = new(block, "worker-original-cause");
        GasValidationResultSlot[] gasResults = BuildGasResults(block, (0, 0, workerException));

        BlockAccessListManager.ParallelExecutionException? thrown = Assert.Throws<BlockAccessListManager.ParallelExecutionException>(() =>
            balManager.IncrementalValidation(block, gasResults, new BlockReceiptsTracer[1], null, CancellationToken.None));

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

        PrepareSetup(balManager, block, Amsterdam.Instance);

        InvalidBlockException workerException = new(block, "worker-original-cause");
        // tx1 row uses legacy buggy shape: charges tx.GasLimit on rejection. Cumulative 80k+50k > 100k limit.
        GasValidationResultSlot[] gasResults = BuildGasResults(block,
            (80_000, 0, null),
            (50_000, 0, workerException));

        BlockAccessListManager.ParallelExecutionException? thrown = Assert.Throws<BlockAccessListManager.ParallelExecutionException>(() =>
            balManager.IncrementalValidation(block, gasResults, new BlockReceiptsTracer[2], null, CancellationToken.None));

        Assert.That(thrown!.InnerException, Is.SameAs(workerException));
    }

    [TestCase(true)]
    [TestCase(false)]
    public void Parallel_validation_preserves_processing_thread_metric_scope_for_worker_transactions(bool isBlockProcessingThread)
    {
        Assume.That(Environment.ProcessorCount, Is.GreaterThan(1));

        const int txCount = 64;
        IWorldState stateProvider = TestWorldStateFactory.CreateForTest();
        using IDisposable scope = stateProvider.BeginScope(IWorldState.PreGenesis);

        Transaction[] transactions = CreateParallelValidationTransactions(txCount);
        Block block = Build.A.Block
            .WithNumber(1)
            .WithGasLimit(txCount * 21_000)
            .WithTransactions(transactions)
            .WithBlockAccessList(new ReadOnlyBlockAccessList())
            .TestObject;

        using RecordingTransactionProcessorAdapter transactionProcessor = new();
        BlockProcessor.ParallelBlockValidationTransactionsExecutor executor = CreateParallelValidationExecutor(stateProvider, transactionProcessor);

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

        Transaction[] transactions = CreateParallelValidationTransactions(txCount);
        Block block = Build.A.Block
            .WithNumber(1)
            .WithGasLimit(txCount * 21_000)
            .WithTransactions(transactions)
            .WithBlockAccessList(new ReadOnlyBlockAccessList())
            .TestObject;

        using RecordingTransactionProcessorAdapter transactionProcessor = new(traceOperation: true);
        BlockProcessor.ParallelBlockValidationTransactionsExecutor executor = CreateParallelValidationExecutor(stateProvider, transactionProcessor);
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

    [Test]
    public void Parallel_validation_execution_order_keeps_canonical_lead_and_sorts_tail_by_gas_limit()
    {
        Transaction[] transactions =
        [
            CreateTxForExecutionOrder(0, 100_000),
            CreateTxForExecutionOrder(1, 90_000),
            CreateTxForExecutionOrder(2, 30_000),
            CreateTxForExecutionOrder(3, 500_000),
            CreateTxForExecutionOrder(4, 120_000),
        ];
        int[] order = new int[transactions.Length];

        BlockProcessor.ParallelBlockValidationTransactionsExecutor.BuildTxExecutionOrder(transactions, order, canonicalLead: 2);

        Assert.That(order, Is.EqualTo(new[] { 0, 1, 3, 4, 2 }));
    }

    [Test]
    public void Parallel_validation_execution_order_uses_stable_estimated_work_tie_breakers()
    {
        Transaction[] transactions =
        [
            CreateTxForExecutionOrder(0, 100_000, dataLength: 1),
            CreateTxForExecutionOrder(1, 100_000, dataLength: 5),
            CreateTxForExecutionOrder(2, 100_000, dataLength: 5, authorizationCount: 1),
            CreateTxForExecutionOrder(3, 100_000, dataLength: 5, authorizationCount: 1, accessListStorageKeys: 1),
            CreateTxForExecutionOrder(4, 100_000, dataLength: 5, authorizationCount: 1, accessListStorageKeys: 1, contractCreation: true),
            CreateTxForExecutionOrder(5, 100_000, dataLength: 5, authorizationCount: 1, accessListStorageKeys: 1),
        ];
        int[] order = new int[transactions.Length];

        BlockProcessor.ParallelBlockValidationTransactionsExecutor.BuildTxExecutionOrder(transactions, order, canonicalLead: 0);

        Assert.That(order, Is.EqualTo(new[] { 4, 3, 5, 2, 1, 0 }));
    }

    [Test]
    public void Parallel_validation_uses_canonical_receipt_and_bal_indexes_with_scheduled_work_order()
    {
        int txCount = BlockProcessor.ParallelBlockValidationTransactionsExecutor.GetCanonicalExecutionLead(256) + 4;
        IWorldState stateProvider = TestWorldStateFactory.CreateForTest();
        using IDisposable scope = stateProvider.BeginScope(IWorldState.PreGenesis);

        Transaction[] transactions = CreateParallelValidationTransactions(txCount);
        transactions[txCount - 1].GasLimit = 1_000_000;
        transactions[txCount - 2].GasLimit = 900_000;
        transactions[txCount - 3].GasLimit = 800_000;

        Block block = Build.A.Block
            .WithNumber(1)
            .WithGasLimit((ulong)txCount * 1_000_000ul)
            .WithTransactions(transactions)
            .WithBlockAccessList(new ReadOnlyBlockAccessList())
            .TestObject;

        ConcurrentBag<(int TxIndex, uint BalIndex)> balIndexes = [];
        BlockProcessor.ParallelBlockValidationTransactionsExecutor executor = new(
            Substitute.For<IBlockProcessor.IBlockTransactionsExecutor>(),
            stateProvider,
            new TestSingleReleaseSpecProvider(Amsterdam.Instance),
            new ParallelTestBlockAccessListManager(balIndex => new BalIndexRecordingTransactionProcessorAdapter(balIndex.GetValueOrDefault(), balIndexes)),
            LimboLogs.Instance);

        TxReceipt[] receipts = executor.ProcessTransactions(
            block,
            ProcessingOptions.None,
            new BlockReceiptsTracer(),
            CancellationToken.None);

        Assert.That(receipts, Has.Length.EqualTo(txCount));
        Assert.That(balIndexes.Count, Is.EqualTo(txCount));
        for (int i = 0; i < txCount; i++)
        {
            Assert.That(receipts[i].Index, Is.EqualTo(i));
            Assert.That(receipts[i].GasUsed, Is.EqualTo(21_000 + i));
        }

        foreach ((int txIndex, uint balIndex) in balIndexes)
        {
            Assert.That(balIndex, Is.EqualTo((uint)(txIndex + 1)));
        }
    }

    [Test]
    public void Parallel_validation_cancel_incomplete_gas_results_preserves_completed_slots()
    {
        GasValidationResultSlot[] gasResults = ResultsForCount(2);
        gasResults[0].TrySetResult(new GasValidationResult(1, 2, null));

        BlockProcessor.ParallelBlockValidationTransactionsExecutor.CancelIncompleteGasResults(gasResults, gasResults.Length);

        Assert.That(gasResults[0].GetResult().BlockGasUsed, Is.EqualTo(1));
        Assert.Throws<TaskCanceledException>(() => gasResults[1].GetResult());
    }

    private static BlockAccessListManager CreateAmsterdamBalManager()
    {
        IWorldState stateProvider = TestWorldStateFactory.CreateForTest();
        return CreateAmsterdamBalManager(stateProvider);
    }

    private static BlockAccessListManager CreateAmsterdamBalManager(IWorldState stateProvider) =>
        new(
            stateProvider,
            new TestSingleReleaseSpecProvider(Amsterdam.Instance),
            Substitute.For<IBlockhashProvider>(),
            LimboLogs.Instance,
            new BlocksConfig { ParallelExecution = true },
            new WithdrawalProcessorFactory(LimboLogs.Instance),
            CodeInfoRepositoryFactories.Caching,
            readOnlyTxProcessingEnvFactory: Substitute.For<IReadOnlyTxProcessingEnvFactory>());

    private static void WithScopedAmsterdamBalManager(Action<BlockAccessListManager> action)
    {
        IWorldState stateProvider = TestWorldStateFactory.CreateForTest();
        using IDisposable scope = stateProvider.BeginScope(IWorldState.PreGenesis);
        using BlockAccessListManager balManager = CreateAmsterdamBalManager(stateProvider);
        action(balManager);
    }

    private static void AssertParallelBalExecutionEnabled(BlockAccessListManager balManager, int txCount)
    {
        Block block = Build.A.Block
            .WithNumber(1)
            .WithTransactions(txCount, Amsterdam.Instance)
            .WithBlockAccessList(new ReadOnlyBlockAccessList())
            .TestObject;

        balManager.PrepareForProcessing(block, Amsterdam.Instance, ProcessingOptions.None);

        Assert.That(balManager.ParallelExecutionEnabled, Is.True);
    }

    private static GasValidationResultSlot[] ResultsForCount(int count)
    {
        GasValidationResultSlot[] results = new GasValidationResultSlot[count];
        for (int i = 0; i < results.Length; i++)
        {
            results[i] = new();
        }

        return results;
    }

    private static GasValidationResult
        GasResult(Block block, int txIndex, ulong blockGasUsed, ulong blockStateGasUsed, InvalidBlockException? exception = null) =>
        new(blockGasUsed, blockStateGasUsed, exception);

    private static void PrepareSetup(BlockAccessListManager balManager, Block block, IReleaseSpec spec, ProcessingOptions options = ProcessingOptions.None)
    {
        balManager.PrepareForProcessing(block, spec, options);
        balManager.SetBlockExecutionContext(new(block.Header, spec));
        balManager.Setup(block);
    }

    private static GasValidationResultSlot[] BuildGasResults(Block block, params (ulong Gas, ulong StateGas, InvalidBlockException? Exception)[] rows)
    {
        GasValidationResultSlot[] slots = ResultsForCount(rows.Length);
        for (int i = 0; i < rows.Length; i++)
        {
            slots[i].TrySetResult(GasResult(block, i, rows[i].Gas, rows[i].StateGas, rows[i].Exception));
        }
        return slots;
    }

    private static Transaction[] CreateParallelValidationTransactions(int txCount)
    {
        Transaction[] transactions = new Transaction[txCount];
        for (uint i = 0; i < transactions.Length; i++)
        {
            transactions[i] = Build.A.Transaction
                 .WithNonce(i)
                 .WithGasLimit(21_000ul)
                .TestObject;
        }

        return transactions;
    }

    private static Transaction CreateTxForExecutionOrder(
        uint nonce,
        ulong gasLimit,
        int dataLength = 0,
        int authorizationCount = 0,
        int accessListStorageKeys = 0,
        bool contractCreation = false)
    {
        byte[] data = dataLength == 0 ? [] : new byte[dataLength];
        TransactionBuilder<Transaction> builder = Build.A.Transaction
            .WithNonce(nonce)
            .WithGasLimit(gasLimit);

        if (contractCreation)
        {
            builder.WithCode(data);
        }
        else
        {
            builder.WithData(data);
        }

        if (authorizationCount > 0)
        {
            builder.WithAuthorizationCode(CreateAuthorizationList(authorizationCount));
        }

        if (accessListStorageKeys > 0)
        {
            builder.WithAccessList(CreateAccessList(accessListStorageKeys));
        }

        return builder.TestObject;
    }

    private static AuthorizationTuple[] CreateAuthorizationList(int count)
    {
        AuthorizationTuple[] authorizations = new AuthorizationTuple[count];
        for (int i = 0; i < count; i++)
        {
            authorizations[i] = new(0, Address.Zero, 0, new Signature(new byte[64], 0));
        }

        return authorizations;
    }

    private static AccessList CreateAccessList(int storageKeys)
    {
        AccessList.Builder builder = new();
        builder.AddAddress(TestItem.AddressA);
        for (int i = 0; i < storageKeys; i++)
        {
            builder.AddStorage((UInt256)i);
        }

        return builder.Build();
    }

    private static BlockProcessor.ParallelBlockValidationTransactionsExecutor CreateParallelValidationExecutor(
        IWorldState stateProvider,
        ITransactionProcessorAdapter transactionProcessor)
    {
        IBlockProcessor.IBlockTransactionsExecutor inner = Substitute.For<IBlockProcessor.IBlockTransactionsExecutor>();
        return new(
            inner,
            stateProvider,
            new TestSingleReleaseSpecProvider(Amsterdam.Instance),
            new ParallelTestBlockAccessListManager(transactionProcessor),
            LimboLogs.Instance);
    }

    private sealed class TrackingReadOnlyTxProcessingEnvFactory : IReadOnlyTxProcessingEnvFactory
    {
        private readonly ITransactionProcessor _transactionProcessor = Substitute.For<ITransactionProcessor>();

        public int CreatedSources { get; private set; }
        public int DisposedScopes { get; private set; }
        public List<BlockHeader?> BuiltHeaders { get; } = [];
        public List<IWorldState> BuiltWorldStates { get; } = [];

        public IReadOnlyTxProcessorSource Create()
        {
            CreatedSources++;
            return new Source(this, _transactionProcessor);
        }

        private void OnScopeDisposed() => DisposedScopes++;

        private sealed class Source(
            TrackingReadOnlyTxProcessingEnvFactory factory,
            ITransactionProcessor transactionProcessor) : IReadOnlyTxProcessorSource
        {
            public IReadOnlyTxProcessingScope Build(BlockHeader? baseBlock)
            {
                IWorldState worldState = Substitute.For<IWorldState>();
                factory.BuiltHeaders.Add(baseBlock);
                factory.BuiltWorldStates.Add(worldState);
                return new Scope(factory, transactionProcessor, worldState);
            }

            public void Dispose() { }
        }

        private sealed class Scope(
            TrackingReadOnlyTxProcessingEnvFactory factory,
            ITransactionProcessor transactionProcessor,
            IWorldState worldState) : IReadOnlyTxProcessingScope
        {
            private bool _disposed;

            public ITransactionProcessor TransactionProcessor => transactionProcessor;
            public IWorldState WorldState => worldState;

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                factory.OnScopeDisposed();
            }
        }
    }

    private sealed class ParallelTestBlockAccessListManager(Func<uint?, ITransactionProcessorAdapter> transactionProcessorFactory) : IBlockAccessListManager
    {
        public ParallelTestBlockAccessListManager(ITransactionProcessorAdapter transactionProcessor)
            : this(_ => transactionProcessor)
        {
        }

        public GeneratedBlockAccessList GeneratedBlockAccessList { get; set; } = new();
        public bool Enabled => true;
        public bool ParallelExecutionEnabled => true;
        public bool BatchReadEnabled => false;
        public bool ForceConstructGeneratedBlockAccessList { get; set; }

        public void PrepareForProcessing(Block suggestedBlock, IReleaseSpec spec, ProcessingOptions options)
        {
        }

        public void WaitForBalWarmup()
        {
        }

        public void Setup(Block block)
        {
        }

        public void SpendGas(ulong gas)
        {
        }

        public void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext)
        {
        }

        public ITransactionProcessorAdapter GetTxProcessor(uint? balIndex = null) => transactionProcessorFactory(balIndex);

        public void NextTransaction()
        {
        }

        public void Rollback()
        {
        }

        public void ReturnTxProcessor(uint balIndex)
        {
        }

        public void IncrementalValidation(Block block, GasValidationResultSlot[] gasResults, BlockReceiptsTracer[] receiptsTracers, BlockProcessor.BlockValidationTransactionsExecutor.ITransactionProcessedEventHandler? transactionProcessedEventHandler, CancellationToken token)
        {
        }

        public void SetBlockAccessList(Block block)
        {
        }

        public void ValidateBlockAccessList(Block block, uint index, bool validateStorageReads = true)
        {
        }

        public void StoreBeaconRoot(Block block, IReleaseSpec spec)
        {
        }

        public void ApplyBlockhashStateChanges(BlockHeader header, IReleaseSpec spec)
        {
        }

        public void ProcessWithdrawals(Block block, IReleaseSpec spec)
        {
        }

        public void ProcessExecutionRequests(Block block, TxReceipt[] txReceipts, IReleaseSpec spec)
        {
        }
    }

    private sealed class BalIndexRecordingTransactionProcessorAdapter(
        uint balIndex,
        ConcurrentBag<(int TxIndex, uint BalIndex)> balIndexes)
        : ITransactionProcessorAdapter
    {
        public TransactionResult Execute(Transaction transaction, ITxTracer txTracer)
        {
            int txIndex = (int)transaction.Nonce;
            balIndexes.Add((txIndex, balIndex));

            ulong gasUsed = 21_000ul + (ulong)txIndex;
            transaction.BlockGasUsed = gasUsed;
            txTracer.MarkAsSuccess(Address.Zero, gasUsed, [], []);

            return TransactionResult.Ok;
        }

        public void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext)
        {
        }
    }

    private sealed class RecordingTransactionProcessorAdapter(bool traceOperation = false) : ITransactionProcessorAdapter, IDisposable
    {
        private readonly ManualResetEventSlim _parallelExecutionStarted = new();
        private int _executedCount;

        public ConcurrentBag<bool> ObservedProcessingThreadFlags { get; } = [];
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

            transaction.BlockGasUsed = 21_000;
            txTracer.MarkAsSuccess(Address.Zero, 21_000, [], []);

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

            public override void StartOperation(int pc, Instruction opcode, ulong gas, in ExecutionEnvironment env) =>
                blockTracer.RecordOpcode();
        }
    }
}
