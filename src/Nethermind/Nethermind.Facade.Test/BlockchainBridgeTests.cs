// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autofac;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Specs;
using NSubstitute;
using NUnit.Framework;
using Nethermind.Consensus;
using Nethermind.Consensus.Tracing;
using Nethermind.Core.Test.Modules;
using Nethermind.Evm;
using Nethermind.Facade.Find;
using Nethermind.Facade.Proxy.Models.Simulate;
using Nethermind.Facade.Simulate;
using NSubstitute.Core;

namespace Nethermind.Facade.Test;

public class BlockchainBridgeTests
{
    private IBlockchainBridge _blockchainBridge;
    private IBlockTree _blockTree;
    private IReceiptStorage _receiptStorage;
    private ITransactionProcessor _transactionProcessor;
    private ManualTimestamper _timestamper;
    private IContainer _container;

    [SetUp]
    public Task SetUp()
    {
        _timestamper = new ManualTimestamper();
        _blockTree = Substitute.For<IBlockTree>();
        _receiptStorage = Substitute.For<IReceiptStorage>();
        _transactionProcessor = Substitute.For<ITransactionProcessor>();

        _container = new ContainerBuilder()
            .AddModule(new TestNethermindModule())
            .AddSingleton(_blockTree)
            .AddSingleton<IReceiptFinder>(_receiptStorage)
            .AddSingleton(_timestamper)
            .AddSingleton(Substitute.For<ILogFinder>())
            .AddSingleton<IMiningConfig>(new MiningConfig { Enabled = false })
            .AddScoped(_transactionProcessor)
            .Build();

        _blockchainBridge = _container.Resolve<IBlockchainBridge>();
        return Task.CompletedTask;
    }

    [TearDown]
    public void TearDown()
    {
        _container.Dispose();
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
        Transaction[] transactions = Enumerable.Range(0, 10)
            .Select(static i => Build.A.Transaction.WithNonce((UInt256)i).WithHash(TestItem.Keccaks[i]).TestObject)
            .ToArray();
        Block block = Build.A.Block
            .WithTransactions(transactions.ToArray())
            .TestObject;
        TxReceipt[] receipts = block.Transactions.Select((t, i) => Build.A.Receipt
            .WithBlockHash(TestItem.KeccakB)
            .WithIndex(i)
            .WithTransactionHash(t.Hash)
            .TestObject
        ).ToArray();
        ;
        _blockTree.FindBlock(TestItem.KeccakB, Arg.Any<BlockTreeLookupOptions>()).Returns(block);
        foreach (TxReceipt receipt in receipts)
        {
            _receiptStorage.FindBlockHash(receipt.TxHash!).Returns(TestItem.KeccakB);
        }
        _receiptStorage.Get(block).Returns(receipts);
        var expectation = (receipts[index], Build.A.Transaction.WithNonce((UInt256)index).WithHash(TestItem.Keccaks[index]).TestObject, UInt256.Zero);
        var result = _blockchainBridge.GetTransaction(transactions[index].Hash!);
        result.Should().BeEquivalentTo(expectation);
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
        _transactionProcessor.Received().SetBlockExecutionContext(Arg.Is<BlockExecutionContext>(static blkCtx =>
            blkCtx.Header.IsPostMerge && blkCtx.Header.Random == TestItem.KeccakA));
        _transactionProcessor.Received().CallAndRestore(
            tx,
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
        _transactionProcessor.Received().SetBlockExecutionContext(
            Arg.Is<BlockExecutionContext>(static blkCtx => blkCtx.Header.Number == 10));
        _transactionProcessor.Received().CallAndRestore(
            tx,
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
        _transactionProcessor.Received().SetBlockExecutionContext(
            Arg.Is<BlockExecutionContext>(static blkCtx => blkCtx.Header.MixHash == TestItem.KeccakA));
        _transactionProcessor.Received().CallAndRestore(
            tx,
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
        _transactionProcessor.Received().SetBlockExecutionContext(
            Arg.Is<BlockExecutionContext>(static blkCtx => blkCtx.Header.Beneficiary == TestItem.AddressB));
        _transactionProcessor.Received().CallAndRestore(
            tx,
            Arg.Any<ITxTracer>());
    }

    [TestCase(7)]
    [TestCase(0)]
    public void Bridge_head_is_correct(long headNumber)
    {
        Block head = Build.A.Block.WithNumber(headNumber).TestObject;
        Block bestSuggested = Build.A.Block.WithNumber(8).TestObject;

        _blockTree.Head.Returns(head);
        _blockTree.BestSuggestedBody.Returns(bestSuggested);

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
                .WithHash(txHash)
                .TestObject
            : Build.A.Transaction
                .WithGasPrice(effectiveGasPrice)
                .WithMaxFeePerGas(effectiveGasPrice)
                .WithHash(txHash)
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
        _receiptStorage.Get(block).Returns([receipt]);

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
        _transactionProcessor.Received().SetBlockExecutionContext(
            Arg.Is<BlockExecutionContext>(static blkCtx => blkCtx.Header.Beneficiary == TestItem.AddressB));
        _transactionProcessor.Received().CallAndRestore(
            Arg.Is<Transaction>(static tx => tx.MaxFeePerBlobGas == 1),
            Arg.Any<ITxTracer>());
    }

    [Test]
    public void Call_tx_returns_InsufficientSenderBalanceError()
    {
        BlockHeader header = Build.A.BlockHeader
            .TestObject;
        Transaction tx = new() { GasLimit = 100 };
        _transactionProcessor.CallAndRestore(Arg.Any<Transaction>(), Arg.Any<ITxTracer>())
            .Returns(TransactionResult.InsufficientSenderBalance);

        CallOutput callOutput = _blockchainBridge.Call(header, tx);

        Assert.That(callOutput.Error, Is.EqualTo("insufficient sender balance"));
    }

    [Test]
    public void EstimateGas_tx_returns_InsufficientSenderBalanceError()
    {
        BlockHeader header = Build.A.BlockHeader
            .TestObject;
        Transaction tx = new() { GasLimit = 100 };
        _transactionProcessor.CallAndRestore(Arg.Any<Transaction>(), Arg.Any<ITxTracer>())
            .Returns(TransactionResult.InsufficientSenderBalance);

        CallOutput callOutput = _blockchainBridge.EstimateGas(header, tx, 1);

        Assert.That(callOutput.Error, Is.EqualTo("insufficient sender balance"));
    }

    [Test]
    public void Call_tx_returns_SenderNotSpecifiedError()
    {
        BlockHeader header = Build.A.BlockHeader
            .TestObject;
        Transaction tx = new() { GasLimit = 123 };
        _transactionProcessor.CallAndRestore(Arg.Any<Transaction>(), Arg.Any<ITxTracer>())
            .Returns(TransactionResult.SenderNotSpecified);

        CallOutput callOutput = _blockchainBridge.Call(header, tx);

        Assert.That(callOutput.Error, Is.EqualTo("sender not specified"));
    }

    [Test]
    public void EstimateGas_tx_returns_SenderNotSpecifiedError()
    {
        BlockHeader header = Build.A.BlockHeader
            .TestObject;
        Transaction tx = new() { GasLimit = 123 };
        _transactionProcessor.CallAndRestore(Arg.Any<Transaction>(), Arg.Any<ITxTracer>())
            .Returns(TransactionResult.SenderNotSpecified);

        CallOutput callOutput = _blockchainBridge.EstimateGas(header, tx, 1);

        Assert.That(callOutput.Error, Is.EqualTo("sender not specified"));
    }

    [Test]
    public void Call_tx_returns_MalformedTransactionError()
    {
        BlockHeader header = Build.A.BlockHeader
            .TestObject;
        Transaction tx = new();
        _transactionProcessor.CallAndRestore(Arg.Any<Transaction>(), Arg.Any<ITxTracer>())
            .Returns(TransactionResult.MalformedTransaction);

        CallOutput callOutput = _blockchainBridge.Call(header, tx);

        Assert.That(callOutput.Error, Is.EqualTo("malformed"));
    }

    [Test]
    public void EstimateGas_tx_returns_MalformedTransactionError()
    {
        BlockHeader header = Build.A.BlockHeader
            .TestObject;
        Transaction tx = new();
        _transactionProcessor.CallAndRestore(Arg.Any<Transaction>(), Arg.Any<ITxTracer>())
            .Returns(TransactionResult.MalformedTransaction);

        CallOutput callOutput = _blockchainBridge.EstimateGas(header, tx, 1);

        Assert.That(callOutput.Error, Is.EqualTo("malformed"));
    }

    [Test]
    public void Call_tx_returns_WrongTransactionNonceError()
    {
        BlockHeader header = Build.A.BlockHeader
            .TestObject;
        Transaction tx = new() { GasLimit = 456 };
        _transactionProcessor.CallAndRestore(Arg.Any<Transaction>(), Arg.Any<ITxTracer>())
            .Returns(TransactionResult.WrongTransactionNonce);

        CallOutput callOutput = _blockchainBridge.Call(header, tx);

        Assert.That(callOutput.Error, Is.EqualTo("wrong transaction nonce"));
    }

    [Test]
    public void EstimateGas_tx_returns_WrongTransactionNonceError()
    {
        BlockHeader header = Build.A.BlockHeader
            .TestObject;
        Transaction tx = new() { GasLimit = 456 };
        _transactionProcessor.CallAndRestore(Arg.Any<Transaction>(), Arg.Any<ITxTracer>())
            .Returns(TransactionResult.WrongTransactionNonce);

        CallOutput callOutput = _blockchainBridge.EstimateGas(header, tx, 1);

        Assert.That(callOutput.Error, Is.EqualTo("wrong transaction nonce"));
    }

    [Test]
    public void Call_tx_returns_NonceOverflowError()
    {
        BlockHeader header = Build.A.BlockHeader
            .TestObject;
        Transaction tx = new() { GasLimit = 0 };
        _transactionProcessor.CallAndRestore(Arg.Any<Transaction>(), Arg.Any<ITxTracer>())
            .Returns(TransactionResult.NonceOverflow);

        CallOutput callOutput = _blockchainBridge.Call(header, tx);

        Assert.That(callOutput.Error, Is.EqualTo("nonce overflow"));
    }

    [Test]
    public void EstimateGas_tx_returns_NonceOverflowError()
    {
        BlockHeader header = Build.A.BlockHeader
            .TestObject;
        Transaction tx = new() { GasLimit = 0 };
        _transactionProcessor.CallAndRestore(Arg.Any<Transaction>(), Arg.Any<ITxTracer>())
            .Returns(TransactionResult.NonceOverflow);

        CallOutput callOutput = _blockchainBridge.EstimateGas(header, tx, 1);

        Assert.That(callOutput.Error, Is.EqualTo("nonce overflow"));
    }

    [Test]
    public void Call_tx_returns_MinerPremiumIsNegativeError()
    {
        BlockHeader header = Build.A.BlockHeader
            .TestObject;
        Transaction tx = new() { GasLimit = 1 };
        _transactionProcessor.CallAndRestore(Arg.Any<Transaction>(), Arg.Any<ITxTracer>())
            .Returns(TransactionResult.MinerPremiumNegative);

        CallOutput callOutput = _blockchainBridge.Call(header, tx);

        Assert.That(callOutput.Error, Is.EqualTo("miner premium is negative"));
    }

    [Test]
    public void EstimateGas_tx_returns_MinerPremiumIsNegativeError()
    {
        BlockHeader header = Build.A.BlockHeader
            .TestObject;
        Transaction tx = new() { GasLimit = 1 };
        _transactionProcessor.CallAndRestore(Arg.Any<Transaction>(), Arg.Any<ITxTracer>())
            .Returns(TransactionResult.MinerPremiumNegative);

        CallOutput callOutput = _blockchainBridge.EstimateGas(header, tx, 1);

        Assert.That(callOutput.Error, Is.EqualTo("miner premium is negative"));
    }

    [Test]
    public void Call_tx_returns_BlockGasLimitExceededError()
    {
        BlockHeader header = Build.A.BlockHeader
            .TestObject;
        Transaction tx = new() { GasLimit = 1 };
        _transactionProcessor.CallAndRestore(Arg.Any<Transaction>(), Arg.Any<ITxTracer>())
            .Returns(TransactionResult.BlockGasLimitExceeded);

        CallOutput callOutput = _blockchainBridge.Call(header, tx);

        Assert.That(callOutput.Error, Is.EqualTo("Block gas limit exceeded"));
    }

    [Test]
    public void EstimateGas_tx_returns_BlockGasLimitExceededError()
    {
        BlockHeader header = Build.A.BlockHeader
            .TestObject;
        Transaction tx = new() { GasLimit = 1 };
        _transactionProcessor.CallAndRestore(Arg.Any<Transaction>(), Arg.Any<ITxTracer>())
            .Returns(TransactionResult.BlockGasLimitExceeded);

        CallOutput callOutput = _blockchainBridge.EstimateGas(header, tx, 1);

        Assert.That(callOutput.Error, Is.EqualTo("Block gas limit exceeded"));
    }


    [Test]
    public void Call_tx_returns_SenderHasDeployedCodeError()
    {
        BlockHeader header = Build.A.BlockHeader
            .TestObject;
        Transaction tx = new() { GasLimit = 1 };
        _transactionProcessor.CallAndRestore(Arg.Any<Transaction>(), Arg.Any<ITxTracer>())
            .Returns(TransactionResult.SenderHasDeployedCode);

        CallOutput callOutput = _blockchainBridge.Call(header, tx);

        Assert.That(callOutput.Error, Is.EqualTo("sender has deployed code"));
    }

    [Test]
    public void EstimateGas_tx_returns_SenderHasDeployedCodeError()
    {
        BlockHeader header = Build.A.BlockHeader
            .TestObject;
        Transaction tx = new() { GasLimit = 1 };
        _transactionProcessor.CallAndRestore(Arg.Any<Transaction>(), Arg.Any<ITxTracer>())
            .Returns(TransactionResult.SenderHasDeployedCode);

        CallOutput callOutput = _blockchainBridge.EstimateGas(header, tx, 1);

        Assert.That(callOutput.Error, Is.EqualTo("sender has deployed code"));
    }

    [Test]
    public void Call_tx_returns_TransactionSizeOverMaxInitCodeSizeError()
    {
        BlockHeader header = Build.A.BlockHeader
            .TestObject;
        Transaction tx = new();
        _transactionProcessor.CallAndRestore(Arg.Any<Transaction>(), Arg.Any<ITxTracer>())
            .Returns(TransactionResult.TransactionSizeOverMaxInitCodeSize);

        CallOutput callOutput = _blockchainBridge.Call(header, tx);

        Assert.That(callOutput.Error, Is.EqualTo("EIP-3860 - transaction size over max init code size"));
    }

    [Test]
    public void EstimateGas_tx_returns_TransactionSizeOverMaxInitCodeSizeError()
    {
        BlockHeader header = Build.A.BlockHeader
            .TestObject;
        Transaction tx = new();
        _transactionProcessor.CallAndRestore(Arg.Any<Transaction>(), Arg.Any<ITxTracer>())
            .Returns(TransactionResult.TransactionSizeOverMaxInitCodeSize);

        CallOutput callOutput = _blockchainBridge.EstimateGas(header, tx, 1);

        Assert.That(callOutput.Error, Is.EqualTo("EIP-3860 - transaction size over max init code size"));
    }

    [Test]
    public void Call_tx_returns_GasLimitBelowIntrinsicGasError()
    {
        BlockHeader header = Build.A.BlockHeader
            .TestObject;
        Transaction tx = new();
        _transactionProcessor.CallAndRestore(Arg.Any<Transaction>(), Arg.Any<ITxTracer>())
            .Returns(TransactionResult.GasLimitBelowIntrinsicGas);

        CallOutput callOutput = _blockchainBridge.Call(header, tx);

        Assert.That(callOutput.Error, Is.EqualTo("gas limit below intrinsic gas"));
    }

    [Test]
    public void EstimateGas_tx_returns_GasLimitBelowIntrinsicGasError()
    {
        BlockHeader header = Build.A.BlockHeader
            .TestObject;
        Transaction tx = new();
        _transactionProcessor.CallAndRestore(Arg.Any<Transaction>(), Arg.Any<ITxTracer>())
            .Returns(TransactionResult.GasLimitBelowIntrinsicGas);

        CallOutput callOutput = _blockchainBridge.EstimateGas(header, tx, 1);

        Assert.That(callOutput.Error, Is.EqualTo("gas limit below intrinsic gas"));
    }

    [Test]
    public void EstimateGas_tx_returns_GasLimitOverCap()
    {
        BlockHeader header = Build.A.BlockHeader
            .TestObject;
        Transaction tx = new() {GasLimit = 30_000_000, Data = new byte[1_680_000]};

        CallOutput callOutput = _blockchainBridge.EstimateGas(header, tx, 1);

        Assert.That(callOutput.Error, Is.EqualTo("Cannot estimate gas, gas spent exceeded transaction and block gas limit or transaction gas limit cap"));
    }

    [Test]
    public void Call_tx_returns_InsufficientMaxFeePerGasForSenderBalanceError()
    {
        BlockHeader header = Build.A.BlockHeader
            .TestObject;
        Transaction tx = new();
        _transactionProcessor.CallAndRestore(Arg.Any<Transaction>(), Arg.Any<ITxTracer>())
            .Returns(TransactionResult.InsufficientMaxFeePerGasForSenderBalance);

        CallOutput callOutput = _blockchainBridge.Call(header, tx);

        Assert.That(callOutput.Error, Is.EqualTo("insufficient MaxFeePerGas for sender balance"));
    }

    [Test]
    public void EstimateGas_tx_returns_InsufficientMaxFeePerGasForSenderBalanceError()
    {
        BlockHeader header = Build.A.BlockHeader
            .TestObject;
        Transaction tx = new();
        _transactionProcessor.CallAndRestore(Arg.Any<Transaction>(), Arg.Any<ITxTracer>())
            .Returns(TransactionResult.InsufficientMaxFeePerGasForSenderBalance);

        CallOutput callOutput = _blockchainBridge.EstimateGas(header, tx, 1);

        Assert.That(callOutput.Error, Is.EqualTo("insufficient MaxFeePerGas for sender balance"));
    }

    [Test]
    public void Call_tx_returns_noError()
    {
        BlockHeader header = Build.A.BlockHeader
            .TestObject;
        Transaction tx = new();

        _transactionProcessor.CallAndRestore(Arg.Any<Transaction>(), Arg.Any<ITxTracer>())
            .Returns(TransactionResult.Ok);

        CallOutput callOutput = _blockchainBridge.Call(header, tx);

        Assert.That(callOutput.Error, Is.Null);
    }

    [Test]
    public void EstimateGas_invalid_tx_returns_error()
    {
        BlockHeader header = Build.A.BlockHeader
            .TestObject;
        Transaction tx = new();

        _transactionProcessor.CallAndRestore(Arg.Any<Transaction>(), Arg.Any<ITxTracer>())
            .Returns(TransactionResult.Ok);

        CallOutput callOutput = _blockchainBridge.EstimateGas(header, tx, 1);

        Assert.That(callOutput.Error, Is.Not.Null);
    }

    [Test]
    public void Eth_simulate_is_lazy()
    {
        ISimulateReadOnlyBlocksProcessingEnvFactory testFactory = Substitute.For<ISimulateReadOnlyBlocksProcessingEnvFactory>();
        testFactory.Create().Returns(Substitute.For<ISimulateReadOnlyBlocksProcessingEnv>());

        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule())
            .AddSingleton<ISimulateReadOnlyBlocksProcessingEnvFactory>(testFactory)
            .Build();

        IBlockchainBridge blockchainBridge = container.Resolve<IBlockchainBridgeFactory>().CreateBlockchainBridge();
        testFactory.DidNotReceive().Create();

        try
        {
            blockchainBridge.Simulate(Build.A.EmptyBlockHeader, new SimulatePayload<TransactionWithSourceDetails>(),
                Substitute.For<ISimulateBlockTracerFactory<GethStyleTracer>>(), 10_000_000, default);
        }
        catch (Exception)
        {
        }

        testFactory.Received().Create();
    }
}
