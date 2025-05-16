// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Trie.Pruning;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;
using Nethermind.Config;
using Nethermind.Core.Test;
using Nethermind.Evm;
using Nethermind.Facade.Find;
using Nethermind.Facade.Simulate;
using Nethermind.State;

namespace Nethermind.Facade.Test;

public class BlockchainBridgeTests
{
    private IBlockchainBridge _blockchainBridge;
    private IBlockTree _blockTree;
    private ITxPool _txPool;
    private IReceiptStorage _receiptStorage;
    private IFilterStore _filterStore;
    private IFilterManager _filterManager;
    private ITransactionProcessor _transactionProcessor;
    private IEthereumEcdsa _ethereumEcdsa;
    private ManualTimestamper _timestamper;
    private ISpecProvider _specProvider;
    private IDbProvider _dbProvider;

    private class TestReadOnlyTxProcessingEnv(
        IOverridableWorldScope worldStateManager,
        IReadOnlyBlockTree blockTree,
        ISpecProvider specProvider,
        ILogManager logManager,
        ITransactionProcessor transactionProcessor)
        : OverridableTxProcessingEnv(worldStateManager, blockTree, specProvider, logManager)
    {
        protected override ITransactionProcessor CreateTransactionProcessor() => transactionProcessor;
    }

    [SetUp]
    public async Task SetUp()
    {
        _dbProvider = await TestMemDbProvider.InitAsync();
        _timestamper = new ManualTimestamper();
        _blockTree = Substitute.For<IBlockTree>();
        _txPool = Substitute.For<ITxPool>();
        _receiptStorage = Substitute.For<IReceiptStorage>();
        _filterStore = Substitute.For<IFilterStore>();
        _filterManager = Substitute.For<IFilterManager>();
        _transactionProcessor = Substitute.For<ITransactionProcessor>();
        _ethereumEcdsa = Substitute.For<IEthereumEcdsa>();
        _specProvider = MainnetSpecProvider.Instance;

        WorldStateManager worldStateManager = TestWorldStateFactory.CreateForTest(_dbProvider, LimboLogs.Instance);
        IOverridableWorldScope overridableWorldScope = worldStateManager.CreateOverridableWorldScope();

        IReadOnlyBlockTree readOnlyBlockTree = _blockTree.AsReadOnly();
        TestReadOnlyTxProcessingEnv processingEnv = new(
            overridableWorldScope,
            readOnlyBlockTree,
            _specProvider,
            LimboLogs.Instance,
            _transactionProcessor);

        SimulateReadOnlyBlocksProcessingEnvFactory simulateProcessingEnvFactory = new SimulateReadOnlyBlocksProcessingEnvFactory(
            worldStateManager,
            readOnlyBlockTree,
            new ReadOnlyDbProvider(_dbProvider, true),
            _specProvider,
            SimulateTransactionProcessorFactory.Instance,
            LimboLogs.Instance);

        _blockchainBridge = new BlockchainBridge(
            processingEnv,
            simulateProcessingEnvFactory,
            _txPool,
            _receiptStorage,
            _filterStore,
            _filterManager,
            _ethereumEcdsa,
            _timestamper,
            Substitute.For<ILogFinder>(),
            _specProvider,
            new BlocksConfig(),
            false);
    }

    [TearDown]
    public void TearDown()
    {
        _filterStore.Dispose();
    }

    [Test]
    public void get_transaction_returns_null_when_transaction_not_found()
    {
        _blockchainBridge.GetTransaction(TestItem.KeccakA).Should().Be((null, null, null));
    }

    [Test]
    public void get_transaction_returns_null_when_block_not_found()
    {
        _receiptStorage.FindBlockHash(TestItem.KeccakA).Returns(TestItem.KeccakB);
        _blockchainBridge.GetTransaction(TestItem.KeccakA).Should().Be((null, null, null));
    }

    [Test]
    public void get_transaction_returns_receipt_and_transaction_when_found()
    {
        int index = 5;
        var receipt = Build.A.Receipt
            .WithBlockHash(TestItem.KeccakB)
            .WithTransactionHash(TestItem.KeccakA)
            .WithIndex(index)
            .TestObject;
        IEnumerable<Transaction> transactions = Enumerable.Range(0, 10)
            .Select(static i => Build.A.Transaction.WithNonce((UInt256)i).TestObject);
        var block = Build.A.Block
            .WithTransactions(transactions.ToArray())
            .TestObject;
        _blockTree.FindBlock(TestItem.KeccakB, Arg.Any<BlockTreeLookupOptions>()).Returns(block);
        _receiptStorage.FindBlockHash(TestItem.KeccakA).Returns(TestItem.KeccakB);
        _receiptStorage.Get(block).Returns(new[] { receipt });
        _blockchainBridge.GetTransaction(TestItem.KeccakA).Should()
            .BeEquivalentTo((receipt, Build.A.Transaction.WithNonce((UInt256)index).TestObject, UInt256.Zero));
    }

    [Test]
    public void Call_uses_valid_post_merge_and_random_value()
    {
        BlockHeader header = Build.A.BlockHeader
            .WithDifficulty(0)
            .WithMixHash(TestItem.KeccakA)
            .TestObject;

        Transaction tx = Build.A.Transaction.TestObject;

        _blockchainBridge.Call(header, tx);
        _transactionProcessor.Received().CallAndRestore(
            tx,
            Arg.Is<BlockExecutionContext>(static blkCtx =>
                blkCtx.Header.IsPostMerge && blkCtx.Header.Random == TestItem.KeccakA),
            Arg.Any<ITxTracer>());
    }

    [Test]
    public void Call_uses_valid_block_number()
    {
        _timestamper.UtcNow = DateTime.MinValue;
        _timestamper.Add(TimeSpan.FromDays(123));
        BlockHeader header = Build.A.BlockHeader.WithNumber(10).TestObject;
        Transaction tx = new() { GasLimit = Transaction.BaseTxGasCost };

        _blockchainBridge.Call(header, tx);
        _transactionProcessor.Received().CallAndRestore(
            tx,
            Arg.Is<BlockExecutionContext>(static blkCtx => blkCtx.Header.Number == 10),
            Arg.Any<ITxTracer>());
    }

    [Test]
    public void Call_uses_valid_mix_hash()
    {
        _timestamper.UtcNow = DateTime.MinValue;
        _timestamper.Add(TimeSpan.FromDays(123));
        BlockHeader header = Build.A.BlockHeader.WithMixHash(TestItem.KeccakA).TestObject;
        Transaction tx = new() { GasLimit = Transaction.BaseTxGasCost };

        _blockchainBridge.Call(header, tx);
        _transactionProcessor.Received().CallAndRestore(
            tx,
            Arg.Is<BlockExecutionContext>(static blkCtx => blkCtx.Header.MixHash == TestItem.KeccakA),
            Arg.Any<ITxTracer>());
    }

    [Test]
    public void Call_uses_valid_beneficiary()
    {
        _timestamper.UtcNow = DateTime.MinValue;
        _timestamper.Add(TimeSpan.FromDays(123));
        BlockHeader header = Build.A.BlockHeader.WithBeneficiary(TestItem.AddressB).TestObject;
        Transaction tx = new() { GasLimit = Transaction.BaseTxGasCost };

        _blockchainBridge.Call(header, tx);
        _transactionProcessor.Received().CallAndRestore(
            tx,
            Arg.Is<BlockExecutionContext>(static blkCtx => blkCtx.Header.Beneficiary == TestItem.AddressB),
            Arg.Any<ITxTracer>());
    }

    [TestCase(7)]
    [TestCase(0)]
    public void Bridge_head_is_correct(long headNumber)
    {
        WorldStateManager worldStateManager = TestWorldStateFactory.CreateForTest(_dbProvider, LimboLogs.Instance);
        IReadOnlyBlockTree roBlockTree = _blockTree.AsReadOnly();
        OverridableTxProcessingEnv processingEnv = new(
            worldStateManager.CreateOverridableWorldScope(),
            roBlockTree,
            _specProvider,
            LimboLogs.Instance);

        SimulateReadOnlyBlocksProcessingEnvFactory simulateProcessingEnv = new SimulateReadOnlyBlocksProcessingEnvFactory(
            worldStateManager,
            roBlockTree,
            new ReadOnlyDbProvider(_dbProvider, true),
            _specProvider,
            SimulateTransactionProcessorFactory.Instance,
            LimboLogs.Instance);

        Block head = Build.A.Block.WithNumber(headNumber).TestObject;
        Block bestSuggested = Build.A.Block.WithNumber(8).TestObject;

        _blockTree.Head.Returns(head);
        _blockTree.BestSuggestedBody.Returns(bestSuggested);

        _blockchainBridge = new BlockchainBridge(
            processingEnv,
            simulateProcessingEnv,
            _txPool,
            _receiptStorage,
            _filterStore,
            _filterManager,
            _ethereumEcdsa,
            _timestamper,
            Substitute.For<ILogFinder>(),
            _specProvider,
            new BlocksConfig(),
            false);

        _blockchainBridge.HeadBlock.Should().Be(head);
    }

    [TestCase(true, true)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(false, false)]
    public void GetReceiptAndGasInfo_returns_correct_results(bool isCanonical, bool postEip4844)
    {
        Hash256 txHash = TestItem.KeccakA;
        Hash256 blockHash = TestItem.KeccakB;
        UInt256 effectiveGasPrice = 123;

        Transaction tx = postEip4844
            ? Build.A.Transaction
                .WithGasPrice(effectiveGasPrice)
                .WithMaxFeePerGas(effectiveGasPrice)
                .WithType(TxType.Blob)
                .WithMaxFeePerBlobGas(2)
                .WithBlobVersionedHashes(2)
                .TestObject
            : Build.A.Transaction
                .WithGasPrice(effectiveGasPrice)
                .WithMaxFeePerGas(effectiveGasPrice)
                .TestObject;
        Block block = postEip4844
            ? Build.A.Block
                .WithNumber(MainnetSpecProvider.ParisBlockNumber)
                .WithTimestamp(MainnetSpecProvider.CancunBlockTimestamp)
                .WithTransactions(tx)
                .WithExcessBlobGas(2)
                .TestObject
            : Build.A.Block
                .WithNumber(MainnetSpecProvider.ParisBlockNumber)
                .WithTimestamp(MainnetSpecProvider.CancunBlockTimestamp)
                .WithTransactions(tx)
                .TestObject;
        TxReceipt receipt = Build.A.Receipt
            .WithBlockHash(blockHash)
            .WithTransactionHash(txHash)
            .TestObject;

        _blockTree.FindBlock(blockHash, Arg.Is(BlockTreeLookupOptions.RequireCanonical)).Returns(isCanonical ? block : null);
        _blockTree.FindBlock(blockHash, Arg.Is(BlockTreeLookupOptions.TotalDifficultyNotNeeded)).Returns(block);
        _receiptStorage.FindBlockHash(txHash).Returns(blockHash);
        _receiptStorage.Get(block).Returns(new[] { receipt });

        (TxReceipt? Receipt, TxGasInfo? GasInfo, int LogIndexStart) result = postEip4844
            ? (receipt, new(effectiveGasPrice, 1, 262144), 0)
            : (receipt, new(effectiveGasPrice), 0);

        if (!isCanonical)
        {
            result = (null, null, 0);
        }

        _blockchainBridge.GetReceiptAndGasInfo(txHash).Should().BeEquivalentTo(result);
    }

    [Test]
    public void Call_sets_maxFeePerBlobGas()
    {
        _timestamper.UtcNow = DateTime.MaxValue;
        BlockHeader header = Build.A.BlockHeader
            .WithBeneficiary(TestItem.AddressB)
            .WithExcessBlobGas(0)
            .WithBlobGasUsed(0)
            .WithNumber(long.MaxValue)
            .WithTimestamp(ulong.MaxValue)
            .TestObject;
        Transaction tx = new() { Type = TxType.Blob, MaxFeePerBlobGas = null, BlobVersionedHashes = [] };

        _blockchainBridge.Call(header, tx);
        _transactionProcessor.Received().CallAndRestore(
            Arg.Is<Transaction>(static tx => tx.MaxFeePerBlobGas == 1),
            Arg.Is<BlockExecutionContext>(static blkCtx => blkCtx.Header.Beneficiary == TestItem.AddressB),
            Arg.Any<ITxTracer>());
    }

    [Test]
    public void Call_tx_returns_InsufficientSenderBalanceError()
    {
        BlockHeader header = Build.A.BlockHeader
            .TestObject;
        Transaction tx = new() { GasLimit = 100 };
        _transactionProcessor.CallAndRestore(Arg.Any<Transaction>(), Arg.Any<BlockExecutionContext>(), Arg.Any<ITxTracer>())
            .Returns(TransactionResult.InsufficientSenderBalance);

        CallOutput callOutput = _blockchainBridge.Call(header, tx);

        Assert.That(callOutput.Error, Is.EqualTo("err: insufficient sender balance (supplied gas 100)"));
    }

    [Test]
    public void EstimateGas_tx_returns_InsufficientSenderBalanceError()
    {
        BlockHeader header = Build.A.BlockHeader
            .TestObject;
        Transaction tx = new() { GasLimit = 100 };
        _transactionProcessor.CallAndRestore(Arg.Any<Transaction>(), Arg.Any<BlockExecutionContext>(), Arg.Any<ITxTracer>())
            .Returns(TransactionResult.InsufficientSenderBalance);

        CallOutput callOutput = _blockchainBridge.EstimateGas(header, tx, 1);

        Assert.That(callOutput.Error, Is.EqualTo("err: insufficient sender balance (supplied gas 100)"));
    }

    [Test]
    public void Call_tx_returns_SenderNotSpecifiedError()
    {
        BlockHeader header = Build.A.BlockHeader
            .TestObject;
        Transaction tx = new() { GasLimit = 123 };
        _transactionProcessor.CallAndRestore(Arg.Any<Transaction>(), Arg.Any<BlockExecutionContext>(), Arg.Any<ITxTracer>())
            .Returns(TransactionResult.SenderNotSpecified);

        CallOutput callOutput = _blockchainBridge.Call(header, tx);

        Assert.That(callOutput.Error, Is.EqualTo("err: sender not specified (supplied gas 123)"));
    }

    [Test]
    public void EstimateGas_tx_returns_SenderNotSpecifiedError()
    {
        BlockHeader header = Build.A.BlockHeader
            .TestObject;
        Transaction tx = new() { GasLimit = 123 };
        _transactionProcessor.CallAndRestore(Arg.Any<Transaction>(), Arg.Any<BlockExecutionContext>(), Arg.Any<ITxTracer>())
            .Returns(TransactionResult.SenderNotSpecified);

        CallOutput callOutput = _blockchainBridge.EstimateGas(header, tx, 1);

        Assert.That(callOutput.Error, Is.EqualTo("err: sender not specified (supplied gas 123)"));
    }

    [Test]
    public void Call_tx_returns_MalformedTransactionError()
    {
        BlockHeader header = Build.A.BlockHeader
            .TestObject;
        Transaction tx = new();
        _transactionProcessor.CallAndRestore(Arg.Any<Transaction>(), Arg.Any<BlockExecutionContext>(), Arg.Any<ITxTracer>())
            .Returns(TransactionResult.MalformedTransaction);

        CallOutput callOutput = _blockchainBridge.Call(header, tx);

        Assert.That(callOutput.Error, Is.EqualTo("err: malformed (supplied gas 0)"));
    }

    [Test]
    public void EstimateGas_tx_returns_MalformedTransactionError()
    {
        BlockHeader header = Build.A.BlockHeader
            .TestObject;
        Transaction tx = new();
        _transactionProcessor.CallAndRestore(Arg.Any<Transaction>(), Arg.Any<BlockExecutionContext>(), Arg.Any<ITxTracer>())
            .Returns(TransactionResult.MalformedTransaction);

        CallOutput callOutput = _blockchainBridge.EstimateGas(header, tx, 1);

        Assert.That(callOutput.Error, Is.EqualTo("err: malformed (supplied gas 0)"));
    }

    [Test]
    public void Call_tx_returns_WrongTransactionNonceError()
    {
        BlockHeader header = Build.A.BlockHeader
            .TestObject;
        Transaction tx = new() { GasLimit = 456 };
        _transactionProcessor.CallAndRestore(Arg.Any<Transaction>(), Arg.Any<BlockExecutionContext>(), Arg.Any<ITxTracer>())
            .Returns(TransactionResult.WrongTransactionNonce);

        CallOutput callOutput = _blockchainBridge.Call(header, tx);

        Assert.That(callOutput.Error, Is.EqualTo("err: wrong transaction nonce (supplied gas 456)"));
    }

    [Test]
    public void EstimateGas_tx_returns_WrongTransactionNonceError()
    {
        BlockHeader header = Build.A.BlockHeader
            .TestObject;
        Transaction tx = new() { GasLimit = 456 };
        _transactionProcessor.CallAndRestore(Arg.Any<Transaction>(), Arg.Any<BlockExecutionContext>(), Arg.Any<ITxTracer>())
            .Returns(TransactionResult.WrongTransactionNonce);

        CallOutput callOutput = _blockchainBridge.EstimateGas(header, tx, 1);

        Assert.That(callOutput.Error, Is.EqualTo("err: wrong transaction nonce (supplied gas 456)"));
    }

    [Test]
    public void Call_tx_returns_NonceOverflowError()
    {
        BlockHeader header = Build.A.BlockHeader
            .TestObject;
        Transaction tx = new() { GasLimit = 0 };
        _transactionProcessor.CallAndRestore(Arg.Any<Transaction>(), Arg.Any<BlockExecutionContext>(), Arg.Any<ITxTracer>())
            .Returns(TransactionResult.NonceOverflow);

        CallOutput callOutput = _blockchainBridge.Call(header, tx);

        Assert.That(callOutput.Error, Is.EqualTo("err: nonce overflow (supplied gas 0)"));
    }

    [Test]
    public void EstimateGas_tx_returns_NonceOverflowError()
    {
        BlockHeader header = Build.A.BlockHeader
            .TestObject;
        Transaction tx = new() { GasLimit = 0 };
        _transactionProcessor.CallAndRestore(Arg.Any<Transaction>(), Arg.Any<BlockExecutionContext>(), Arg.Any<ITxTracer>())
            .Returns(TransactionResult.NonceOverflow);

        CallOutput callOutput = _blockchainBridge.EstimateGas(header, tx, 1);

        Assert.That(callOutput.Error, Is.EqualTo("err: nonce overflow (supplied gas 0)"));
    }

    [Test]
    public void Call_tx_returns_MinerPremiumIsNegativeError()
    {
        BlockHeader header = Build.A.BlockHeader
            .TestObject;
        Transaction tx = new() { GasLimit = 1 };
        _transactionProcessor.CallAndRestore(Arg.Any<Transaction>(), Arg.Any<BlockExecutionContext>(), Arg.Any<ITxTracer>())
            .Returns(TransactionResult.MinerPremiumNegative);

        CallOutput callOutput = _blockchainBridge.Call(header, tx);

        Assert.That(callOutput.Error, Is.EqualTo("err: miner premium is negative (supplied gas 1)"));
    }

    [Test]
    public void EstimateGas_tx_returns_MinerPremiumIsNegativeError()
    {
        BlockHeader header = Build.A.BlockHeader
            .TestObject;
        Transaction tx = new() { GasLimit = 1 };
        _transactionProcessor.CallAndRestore(Arg.Any<Transaction>(), Arg.Any<BlockExecutionContext>(), Arg.Any<ITxTracer>())
            .Returns(TransactionResult.MinerPremiumNegative);

        CallOutput callOutput = _blockchainBridge.EstimateGas(header, tx, 1);

        Assert.That(callOutput.Error, Is.EqualTo("err: miner premium is negative (supplied gas 1)"));
    }

    [Test]
    public void Call_tx_returns_BlockGasLimitExceededError()
    {
        BlockHeader header = Build.A.BlockHeader
            .TestObject;
        Transaction tx = new() { GasLimit = 1 };
        _transactionProcessor.CallAndRestore(Arg.Any<Transaction>(), Arg.Any<BlockExecutionContext>(), Arg.Any<ITxTracer>())
            .Returns(TransactionResult.BlockGasLimitExceeded);

        CallOutput callOutput = _blockchainBridge.Call(header, tx);

        Assert.That(callOutput.Error, Is.EqualTo("err: Block gas limit exceeded (supplied gas 1)"));
    }

    [Test]
    public void EstimateGas_tx_returns_BlockGasLimitExceededError()
    {
        BlockHeader header = Build.A.BlockHeader
            .TestObject;
        Transaction tx = new() { GasLimit = 1 };
        _transactionProcessor.CallAndRestore(Arg.Any<Transaction>(), Arg.Any<BlockExecutionContext>(), Arg.Any<ITxTracer>())
            .Returns(TransactionResult.BlockGasLimitExceeded);

        CallOutput callOutput = _blockchainBridge.EstimateGas(header, tx, 1);

        Assert.That(callOutput.Error, Is.EqualTo("err: Block gas limit exceeded (supplied gas 1)"));
    }


    [Test]
    public void Call_tx_returns_SenderHasDeployedCodeError()
    {
        BlockHeader header = Build.A.BlockHeader
            .TestObject;
        Transaction tx = new() { GasLimit = 1 };
        _transactionProcessor.CallAndRestore(Arg.Any<Transaction>(), Arg.Any<BlockExecutionContext>(), Arg.Any<ITxTracer>())
            .Returns(TransactionResult.SenderHasDeployedCode);

        CallOutput callOutput = _blockchainBridge.Call(header, tx);

        Assert.That(callOutput.Error, Is.EqualTo("err: sender has deployed code (supplied gas 1)"));
    }

    [Test]
    public void EstimateGas_tx_returns_SenderHasDeployedCodeError()
    {
        BlockHeader header = Build.A.BlockHeader
            .TestObject;
        Transaction tx = new() { GasLimit = 1 };
        _transactionProcessor.CallAndRestore(Arg.Any<Transaction>(), Arg.Any<BlockExecutionContext>(), Arg.Any<ITxTracer>())
            .Returns(TransactionResult.SenderHasDeployedCode);

        CallOutput callOutput = _blockchainBridge.EstimateGas(header, tx, 1);

        Assert.That(callOutput.Error, Is.EqualTo("err: sender has deployed code (supplied gas 1)"));
    }

    [Test]
    public void Call_tx_returns_TransactionSizeOverMaxInitCodeSizeError()
    {
        BlockHeader header = Build.A.BlockHeader
            .TestObject;
        Transaction tx = new();
        _transactionProcessor.CallAndRestore(Arg.Any<Transaction>(), Arg.Any<BlockExecutionContext>(), Arg.Any<ITxTracer>())
            .Returns(TransactionResult.TransactionSizeOverMaxInitCodeSize);

        CallOutput callOutput = _blockchainBridge.Call(header, tx);

        Assert.That(callOutput.Error, Is.EqualTo("err: EIP-3860 - transaction size over max init code size (supplied gas 0)"));
    }

    [Test]
    public void EstimateGas_tx_returns_TransactionSizeOverMaxInitCodeSizeError()
    {
        BlockHeader header = Build.A.BlockHeader
            .TestObject;
        Transaction tx = new();
        _transactionProcessor.CallAndRestore(Arg.Any<Transaction>(), Arg.Any<BlockExecutionContext>(), Arg.Any<ITxTracer>())
            .Returns(TransactionResult.TransactionSizeOverMaxInitCodeSize);

        CallOutput callOutput = _blockchainBridge.EstimateGas(header, tx, 1);

        Assert.That(callOutput.Error, Is.EqualTo("err: EIP-3860 - transaction size over max init code size (supplied gas 0)"));
    }

    [Test]
    public void Call_tx_returns_GasLimitBelowIntrinsicGasError()
    {
        BlockHeader header = Build.A.BlockHeader
            .TestObject;
        Transaction tx = new();
        _transactionProcessor.CallAndRestore(Arg.Any<Transaction>(), Arg.Any<BlockExecutionContext>(), Arg.Any<ITxTracer>())
            .Returns(TransactionResult.GasLimitBelowIntrinsicGas);

        CallOutput callOutput = _blockchainBridge.Call(header, tx);

        Assert.That(callOutput.Error, Is.EqualTo("err: gas limit below intrinsic gas (supplied gas 0)"));
    }

    [Test]
    public void EstimateGas_tx_returns_GasLimitBelowIntrinsicGasError()
    {
        BlockHeader header = Build.A.BlockHeader
            .TestObject;
        Transaction tx = new();
        _transactionProcessor.CallAndRestore(Arg.Any<Transaction>(), Arg.Any<BlockExecutionContext>(), Arg.Any<ITxTracer>())
            .Returns(TransactionResult.GasLimitBelowIntrinsicGas);

        CallOutput callOutput = _blockchainBridge.EstimateGas(header, tx, 1);

        Assert.That(callOutput.Error, Is.EqualTo("err: gas limit below intrinsic gas (supplied gas 0)"));
    }

    [Test]
    public void Call_tx_returns_InsufficientMaxFeePerGasForSenderBalanceError()
    {
        BlockHeader header = Build.A.BlockHeader
            .TestObject;
        Transaction tx = new();
        _transactionProcessor.CallAndRestore(Arg.Any<Transaction>(), Arg.Any<BlockExecutionContext>(), Arg.Any<ITxTracer>())
            .Returns(TransactionResult.InsufficientMaxFeePerGasForSenderBalance);

        CallOutput callOutput = _blockchainBridge.Call(header, tx);

        Assert.That(callOutput.Error, Is.EqualTo("err: insufficient MaxFeePerGas for sender balance (supplied gas 0)"));
    }

    [Test]
    public void EstimateGas_tx_returns_InsufficientMaxFeePerGasForSenderBalanceError()
    {
        BlockHeader header = Build.A.BlockHeader
            .TestObject;
        Transaction tx = new();
        _transactionProcessor.CallAndRestore(Arg.Any<Transaction>(), Arg.Any<BlockExecutionContext>(), Arg.Any<ITxTracer>())
            .Returns(TransactionResult.InsufficientMaxFeePerGasForSenderBalance);

        CallOutput callOutput = _blockchainBridge.EstimateGas(header, tx, 1);

        Assert.That(callOutput.Error, Is.EqualTo("err: insufficient MaxFeePerGas for sender balance (supplied gas 0)"));
    }

    [Test]
    public void Call_tx_returns_noError()
    {
        BlockHeader header = Build.A.BlockHeader
            .TestObject;
        Transaction tx = new();

        _transactionProcessor.CallAndRestore(Arg.Any<Transaction>(), Arg.Any<BlockExecutionContext>(), Arg.Any<ITxTracer>())
            .Returns(TransactionResult.Ok);

        CallOutput callOutput = _blockchainBridge.Call(header, tx);

        Assert.That(callOutput.Error, Is.Null);
    }

    [Test]
    public void EstimateGas_tx_returns_noError()
    {
        BlockHeader header = Build.A.BlockHeader
            .TestObject;
        Transaction tx = new();

        _transactionProcessor.CallAndRestore(Arg.Any<Transaction>(), Arg.Any<BlockExecutionContext>(), Arg.Any<ITxTracer>())
            .Returns(TransactionResult.Ok);

        CallOutput callOutput = _blockchainBridge.EstimateGas(header, tx, 1);

        Assert.That(callOutput.Error, Is.Null);
    }
}
