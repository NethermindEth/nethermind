// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using FluentAssertions;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Test.Validators;
using Nethermind.Config;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Crypto;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.JsonRpc.Test.Modules;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Nethermind.Evm.State;
using Nethermind.Int256;
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
using Nethermind.State;

namespace Nethermind.Blockchain.Test;

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

    [Test]
    public void Parallel_eoa_transfers_match_sequential_state_root()
    {
        BlocksConfig parallelConfig = new()
        {
            ParallelEoaTransfersOnBlockProcessing = true,
            ParallelEoaTransfersConcurrency = 2
        };
        BlocksConfig sequentialConfig = new()
        {
            ParallelEoaTransfersOnBlockProcessing = false
        };

        using IContainer parallelContainer = BuildContainer(parallelConfig, Berlin.Instance);
        using IContainer sequentialContainer = BuildContainer(sequentialConfig, Berlin.Instance);

        (Address senderA, Address senderB, Address recipientC, Address recipientD) = (TestItem.AddressA, TestItem.AddressB, TestItem.AddressC, TestItem.AddressD);
        BlockHeader parallelBase = InitializeState(parallelContainer,
            (senderA, 10_000.Ether()),
            (senderB, 10_000.Ether()),
            (recipientC, 0.Ether()),
            (recipientD, 0.Ether()));

        BlockHeader sequentialBase = InitializeState(sequentialContainer,
            (senderA, 10_000.Ether()),
            (senderB, 10_000.Ether()),
            (recipientC, 0.Ether()),
            (recipientD, 0.Ether()));

        Transaction tx1 = BuildSimpleTransfer(TestItem.PrivateKeyA, recipientC, 0);
        Transaction tx2 = BuildSimpleTransfer(TestItem.PrivateKeyB, recipientD, 0);

        Block parallelBlock = BuildSimpleBlock(parallelBase, tx1, tx2);
        Block sequentialBlock = BuildSimpleBlock(sequentialBase, tx1, tx2);

        (Block processedParallel, TxReceipt[] receiptsParallel) = ProcessBlock(parallelContainer, parallelBase, parallelBlock);
        (Block processedSequential, TxReceipt[] receiptsSequential) = ProcessBlock(sequentialContainer, sequentialBase, sequentialBlock);

        processedSequential.StateRoot.Should().NotBeNull();
        processedParallel.StateRoot.Should().Be(processedSequential.StateRoot!);
        receiptsParallel.Length.Should().Be(receiptsSequential.Length);
        receiptsParallel[0].GasUsed.Should().Be(receiptsSequential[0].GasUsed);
    }

    [Test]
    public void Block_stm_matches_sequential_state_root()
    {
        BlocksConfig stmConfig = new()
        {
            BlockStmOnBlockProcessing = true,
            BlockStmConcurrency = 2,
            ParallelEoaTransfersOnBlockProcessing = false
        };
        BlocksConfig sequentialConfig = new()
        {
            BlockStmOnBlockProcessing = false,
            ParallelEoaTransfersOnBlockProcessing = false
        };

        using IContainer stmContainer = BuildContainer(stmConfig, Berlin.Instance);
        using IContainer sequentialContainer = BuildContainer(sequentialConfig, Berlin.Instance);

        (Address senderA, Address senderB, Address recipientC, Address recipientD) = (TestItem.AddressA, TestItem.AddressB, TestItem.AddressC, TestItem.AddressD);
        BlockHeader stmBase = InitializeState(stmContainer,
            (senderA, 10_000.Ether()),
            (senderB, 10_000.Ether()),
            (recipientC, 0.Ether()),
            (recipientD, 0.Ether()));

        BlockHeader sequentialBase = InitializeState(sequentialContainer,
            (senderA, 10_000.Ether()),
            (senderB, 10_000.Ether()),
            (recipientC, 0.Ether()),
            (recipientD, 0.Ether()));

        Transaction tx1 = BuildSimpleTransfer(TestItem.PrivateKeyA, recipientC, 0);
        Transaction tx2 = BuildSimpleTransfer(TestItem.PrivateKeyB, recipientD, 0);

        Block stmBlock = BuildSimpleBlock(stmBase, tx1, tx2);
        Block sequentialBlock = BuildSimpleBlock(sequentialBase, tx1, tx2);

        (Block processedStm, TxReceipt[] receiptsStm) = ProcessBlock(stmContainer, stmBase, stmBlock);
        (Block processedSequential, TxReceipt[] receiptsSequential) = ProcessBlock(sequentialContainer, sequentialBase, sequentialBlock);

        processedSequential.StateRoot.Should().NotBeNull();
        processedStm.StateRoot.Should().Be(processedSequential.StateRoot!);
        receiptsStm.Length.Should().Be(receiptsSequential.Length);
        receiptsStm[0].GasUsed.Should().Be(receiptsSequential[0].GasUsed);
    }

    [Test]
    public void Block_stm_conflicts_fall_back_to_sequential()
    {
        BlocksConfig stmConfig = new()
        {
            BlockStmOnBlockProcessing = true,
            BlockStmConcurrency = 2,
            ParallelEoaTransfersOnBlockProcessing = false
        };
        BlocksConfig sequentialConfig = new()
        {
            BlockStmOnBlockProcessing = false,
            ParallelEoaTransfersOnBlockProcessing = false
        };

        using IContainer stmContainer = BuildContainer(stmConfig, Berlin.Instance);
        using IContainer sequentialContainer = BuildContainer(sequentialConfig, Berlin.Instance);

        (Address senderA, Address recipientC, Address recipientD) = (TestItem.AddressA, TestItem.AddressC, TestItem.AddressD);
        BlockHeader stmBase = InitializeState(stmContainer,
            (senderA, 10_000.Ether()),
            (recipientC, 0.Ether()),
            (recipientD, 0.Ether()));

        BlockHeader sequentialBase = InitializeState(sequentialContainer,
            (senderA, 10_000.Ether()),
            (recipientC, 0.Ether()),
            (recipientD, 0.Ether()));

        Transaction tx1 = BuildSimpleTransfer(TestItem.PrivateKeyA, recipientC, 0);
        Transaction tx2 = BuildSimpleTransfer(TestItem.PrivateKeyA, recipientD, 1);

        Block stmBlock = BuildSimpleBlock(stmBase, tx1, tx2);
        Block sequentialBlock = BuildSimpleBlock(sequentialBase, tx1, tx2);

        (Block processedStm, TxReceipt[] receiptsStm) = ProcessBlock(stmContainer, stmBase, stmBlock);
        (Block processedSequential, TxReceipt[] receiptsSequential) = ProcessBlock(sequentialContainer, sequentialBase, sequentialBlock);

        processedSequential.StateRoot.Should().NotBeNull();
        processedStm.StateRoot.Should().Be(processedSequential.StateRoot!);
        receiptsStm.Length.Should().Be(receiptsSequential.Length);
    }

    [Test]
    public void Parallel_eoa_transfers_skip_for_contract_creation()
    {
        ParallelEoaTransferTransactionsExecutor executor = BuildExecutorForUnitTests(out var adapter, out var shareableSource, out var specProvider);

        Transaction tx = Build.A.Transaction
            .WithNonce(0)
            .WithGasLimit(21_000)
            .WithGasPrice(0)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .To(null)
            .TestObject;

        Transaction tx2 = BuildSimpleTransfer(TestItem.PrivateKeyB, TestItem.AddressC, 0);
        Block block = Build.A.Block.WithTransactions(tx, tx2).TestObject;
        BlockReceiptsTracer receiptsTracer = new();
        receiptsTracer.StartNewBlockTrace(block);

        executor.ProcessTransactions(block, ProcessingOptions.None, receiptsTracer, CancellationToken.None);

        adapter.Received(2).Execute(Arg.Any<Transaction>(), Arg.Any<ITxTracer>());
        shareableSource.DidNotReceive().Build(Arg.Any<BlockHeader>());
        specProvider.Received().GetSpec(Arg.Any<ForkActivation>());
    }

    [Test]
    public void Parallel_eoa_transfers_skip_for_contract_recipient()
    {
        IWorldState worldState = Substitute.For<IWorldState>();
        worldState.IsContract(TestItem.AddressB).Returns(true);
        ParallelEoaTransferTransactionsExecutor executor = BuildExecutorForUnitTests(out var adapter, out var shareableSource, out _, worldState: worldState);

        Transaction tx = BuildSimpleTransfer(TestItem.PrivateKeyA, TestItem.AddressB, 0);
        Transaction tx2 = BuildSimpleTransfer(TestItem.PrivateKeyB, TestItem.AddressC, 0);
        Block block = Build.A.Block.WithTransactions(tx, tx2).TestObject;
        BlockReceiptsTracer receiptsTracer = new();
        receiptsTracer.StartNewBlockTrace(block);

        executor.ProcessTransactions(block, ProcessingOptions.None, receiptsTracer, CancellationToken.None);

        adapter.Received(2).Execute(Arg.Any<Transaction>(), Arg.Any<ITxTracer>());
        shareableSource.DidNotReceive().Build(Arg.Any<BlockHeader>());
    }

    [Test]
    public void Parallel_eoa_transfers_skip_for_precompile_recipient()
    {
        Transaction tx = BuildSimpleTransfer(TestItem.PrivateKeyA, Address.FromNumber(1), 0);
        Transaction tx2 = BuildSimpleTransfer(TestItem.PrivateKeyB, TestItem.AddressC, 0);
        ParallelEoaTransferTransactionsExecutor executor = BuildExecutorForUnitTests(out var adapter, out var shareableSource, out _, releaseSpec: Berlin.Instance);

        Block block = Build.A.Block.WithTransactions(tx, tx2).TestObject;
        BlockReceiptsTracer receiptsTracer = new();
        receiptsTracer.StartNewBlockTrace(block);

        executor.ProcessTransactions(block, ProcessingOptions.None, receiptsTracer, CancellationToken.None);

        adapter.Received(2).Execute(Arg.Any<Transaction>(), Arg.Any<ITxTracer>());
        shareableSource.DidNotReceive().Build(Arg.Any<BlockHeader>());
    }

    [Test]
    public void Parallel_eoa_transfers_skip_for_system_transaction()
    {
        ParallelEoaTransferTransactionsExecutor executor = BuildExecutorForUnitTests(out var adapter, out var shareableSource, out _);

        Transaction tx = BuildSimpleTransfer(TestItem.PrivateKeyA, TestItem.AddressB, 0);
        Transaction tx2 = BuildSimpleTransfer(TestItem.PrivateKeyB, TestItem.AddressC, 0);
        tx.IsOPSystemTransaction = true;

        Block block = Build.A.Block.WithTransactions(tx, tx2).TestObject;
        BlockReceiptsTracer receiptsTracer = new();
        receiptsTracer.StartNewBlockTrace(block);

        executor.ProcessTransactions(block, ProcessingOptions.None, receiptsTracer, CancellationToken.None);

        adapter.Received(2).Execute(Arg.Any<Transaction>(), Arg.Any<ITxTracer>());
        shareableSource.DidNotReceive().Build(Arg.Any<BlockHeader>());
    }

    [Test]
    public void Parallel_eoa_transfers_skip_for_duplicate_sender()
    {
        ParallelEoaTransferTransactionsExecutor executor = BuildExecutorForUnitTests(out var adapter, out var shareableSource, out _);

        Transaction tx1 = BuildSimpleTransfer(TestItem.PrivateKeyA, TestItem.AddressB, 0);
        Transaction tx2 = BuildSimpleTransfer(TestItem.PrivateKeyA, TestItem.AddressC, 1);
        Block block = Build.A.Block.WithTransactions(tx1, tx2).TestObject;
        BlockReceiptsTracer receiptsTracer = new();
        receiptsTracer.StartNewBlockTrace(block);

        executor.ProcessTransactions(block, ProcessingOptions.None, receiptsTracer, CancellationToken.None);

        adapter.Received(2).Execute(Arg.Any<Transaction>(), Arg.Any<ITxTracer>());
        shareableSource.DidNotReceive().Build(Arg.Any<BlockHeader>());
    }

    [Test]
    public void Parallel_eoa_transfers_skip_for_duplicate_recipient()
    {
        ParallelEoaTransferTransactionsExecutor executor = BuildExecutorForUnitTests(out var adapter, out var shareableSource, out _);

        Transaction tx1 = BuildSimpleTransfer(TestItem.PrivateKeyA, TestItem.AddressB, 0);
        Transaction tx2 = BuildSimpleTransfer(TestItem.PrivateKeyB, TestItem.AddressB, 0);
        Block block = Build.A.Block.WithTransactions(tx1, tx2).TestObject;
        BlockReceiptsTracer receiptsTracer = new();
        receiptsTracer.StartNewBlockTrace(block);

        executor.ProcessTransactions(block, ProcessingOptions.None, receiptsTracer, CancellationToken.None);

        adapter.Received(2).Execute(Arg.Any<Transaction>(), Arg.Any<ITxTracer>());
        shareableSource.DidNotReceive().Build(Arg.Any<BlockHeader>());
    }

    [Test]
    public void Parallel_eoa_transfers_skip_on_sender_recipient_overlap()
    {
        ParallelEoaTransferTransactionsExecutor executor = BuildExecutorForUnitTests(out var adapter, out var shareableSource, out _);

        Transaction tx1 = BuildSimpleTransfer(TestItem.PrivateKeyA, TestItem.AddressB, 0);
        Transaction tx2 = BuildSimpleTransfer(TestItem.PrivateKeyB, TestItem.AddressC, 0);
        tx2.SenderAddress = TestItem.AddressB;

        Block block = Build.A.Block.WithTransactions(tx1, tx2).TestObject;
        BlockReceiptsTracer receiptsTracer = new();
        receiptsTracer.StartNewBlockTrace(block);

        executor.ProcessTransactions(block, ProcessingOptions.None, receiptsTracer, CancellationToken.None);

        adapter.Received(2).Execute(Arg.Any<Transaction>(), Arg.Any<ITxTracer>());
        shareableSource.DidNotReceive().Build(Arg.Any<BlockHeader>());
    }

    [Test]
    public void Parallel_eoa_transfers_skip_when_beneficiary_overlaps_sender()
    {
        ParallelEoaTransferTransactionsExecutor executor = BuildExecutorForUnitTests(out var adapter, out var shareableSource, out _);

        Transaction tx = BuildSimpleTransfer(TestItem.PrivateKeyA, TestItem.AddressB, 0);
        Transaction tx2 = BuildSimpleTransfer(TestItem.PrivateKeyB, TestItem.AddressC, 0);
        BlockHeader header = Build.A.BlockHeader.WithBeneficiary(tx.SenderAddress!).TestObject;
        Block block = Build.A.Block.WithHeader(header).WithTransactions(tx, tx2).TestObject;

        BlockReceiptsTracer receiptsTracer = new();
        receiptsTracer.StartNewBlockTrace(block);

        executor.ProcessTransactions(block, ProcessingOptions.None, receiptsTracer, CancellationToken.None);

        adapter.Received(2).Execute(Arg.Any<Transaction>(), Arg.Any<ITxTracer>());
        shareableSource.DidNotReceive().Build(Arg.Any<BlockHeader>());
    }

    [Test]
    public void Parallel_eoa_transfers_skip_when_fee_collector_overlaps_sender()
    {
        OverridableReleaseSpec spec = new(Osaka.Instance)
        {
            FeeCollector = TestItem.AddressA
        };

        ParallelEoaTransferTransactionsExecutor executor = BuildExecutorForUnitTests(out var adapter, out var shareableSource, out _, releaseSpec: spec);

        Transaction tx = BuildSimpleTransfer(TestItem.PrivateKeyA, TestItem.AddressB, 0);
        Transaction tx2 = BuildSimpleTransfer(TestItem.PrivateKeyB, TestItem.AddressC, 0);
        Block block = Build.A.Block.WithTransactions(tx, tx2).TestObject;

        BlockReceiptsTracer receiptsTracer = new();
        receiptsTracer.StartNewBlockTrace(block);

        executor.ProcessTransactions(block, ProcessingOptions.None, receiptsTracer, CancellationToken.None);

        adapter.Received(2).Execute(Arg.Any<Transaction>(), Arg.Any<ITxTracer>());
        shareableSource.DidNotReceive().Build(Arg.Any<BlockHeader>());
    }

    private static ParallelEoaTransferTransactionsExecutor BuildExecutorForUnitTests(
        out ITransactionProcessorAdapter adapter,
        out IShareableTxProcessorSource shareableSource,
        out ISpecProvider specProvider,
        IWorldState? worldState = null,
        IReleaseSpec? releaseSpec = null)
    {
        adapter = Substitute.For<ITransactionProcessorAdapter>();
        adapter.Execute(Arg.Any<Transaction>(), Arg.Any<ITxTracer>()).Returns(TransactionResult.Ok);

        if (worldState is null)
        {
            worldState = Substitute.For<IWorldState>();
            worldState.IsContract(Arg.Any<Address>()).Returns(false);
        }

        shareableSource = Substitute.For<IShareableTxProcessorSource>();
        specProvider = Substitute.For<ISpecProvider>();
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(releaseSpec ?? Osaka.Instance);

        IBlocksConfig blocksConfig = new BlocksConfig
        {
            ParallelEoaTransfersOnBlockProcessing = true
        };

        return new ParallelEoaTransferTransactionsExecutor(
            adapter,
            worldState,
            shareableSource,
            specProvider,
            blocksConfig,
            LimboLogs.Instance);
    }

    private static IContainer BuildContainer(IBlocksConfig blocksConfig, IReleaseSpec? specOverride = null)
    {
        IConfigProvider configProvider = new ConfigProvider(blocksConfig);
        ContainerBuilder builder = new();
        builder.AddModule(new TestNethermindModule(configProvider));
        if (specOverride is not null)
        {
            builder.AddSingleton<ISpecProvider>(_ => new TestSpecProvider(specOverride));
        }
        return builder.Build();
    }

    private static BlockHeader InitializeState(IContainer container, params (Address address, UInt256 balance)[] accounts)
    {
        IMainProcessingContext context = container.Resolve<IMainProcessingContext>();
        IWorldState worldState = context.WorldState;
        IReleaseSpec spec = container.Resolve<ISpecProvider>().GenesisSpec;

        Hash256 stateRoot;
        using (worldState.BeginScope(IWorldState.PreGenesis))
        {
            foreach ((Address address, UInt256 balance) in accounts)
            {
                worldState.CreateAccount(address, balance);
            }

            worldState.Commit(spec);
            worldState.CommitTree(0);
            stateRoot = worldState.StateRoot;
        }

        return Build.A.BlockHeader
            .WithNumber(0)
            .WithStateRoot(stateRoot)
            .WithBaseFee(0)
            .TestObject;
    }

    private static (Block Block, TxReceipt[] Receipts) ProcessBlock(IContainer container, BlockHeader baseHeader, Block block)
    {
        IMainProcessingContext context = container.Resolve<IMainProcessingContext>();
        IBlockProcessor blockProcessor = context.BlockProcessor;
        IWorldState worldState = context.WorldState;
        IReleaseSpec spec = container.Resolve<ISpecProvider>().GetSpec(block.Header);

        using (worldState.BeginScope(baseHeader))
        {
            return blockProcessor.ProcessOne(block, ProcessingOptions.NoValidation, NullBlockTracer.Instance, spec, CancellationToken.None);
        }
    }

    private static Transaction BuildSimpleTransfer(PrivateKey senderKey, Address recipient, ulong nonce)
    {
        return Build.A.Transaction
            .WithNonce(nonce)
            .WithGasLimit(21_000)
            .WithGasPrice(0)
            .WithValue(1.Ether())
            .To(recipient)
            .SignedAndResolved(senderKey)
            .TestObject;
    }

    private static Block BuildSimpleBlock(BlockHeader parent, params Transaction[] transactions)
    {
        BlockHeader header = Build.A.BlockHeader
            .WithParent(parent)
            .WithBeneficiary(TestItem.AddressE)
            .WithBaseFee(0)
            .TestObject;

        return Build.A.Block.WithHeader(header).WithTransactions(transactions).TestObject;
    }
}
