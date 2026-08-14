// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Test;
using Nethermind.Evm.State;
using Nethermind.Int256;
using NUnit.Framework;
using Nethermind.Specs.GnosisForks;

namespace Nethermind.Evm.Test;

public class TransactionProcessorFeeTests
{
    private TestSpecProvider _specProvider;
    private IEthereumEcdsa _ethereumEcdsa;
    private ITransactionProcessor _transactionProcessor;
    private IWorldState _stateProvider;
    private IDisposable _worldStateCloser;
    private OverridableReleaseSpec _spec;

    [SetUp]
    public void Setup()
    {
        _spec = new(PragueGnosis.Instance);
        _specProvider = new TestSpecProvider(_spec);

        _stateProvider = TestWorldStateFactory.CreateForTest();
        _worldStateCloser = _stateProvider.BeginScope(IWorldState.PreGenesis);
        _stateProvider.CreateAccount(TestItem.AddressA, 1.Ether);
        _stateProvider.Commit(_specProvider.GenesisSpec);
        _stateProvider.CommitTree(0);

        EthereumCodeInfoRepository codeInfoRepository = new(_stateProvider);
        EthereumVirtualMachine virtualMachine = new(new TestBlockhashProvider(_specProvider), _specProvider, LimboLogs.Instance);
        _transactionProcessor = new EthereumTransactionProcessor(BlobBaseFeeCalculator.Instance, _specProvider, _stateProvider, virtualMachine, codeInfoRepository, LimboLogs.Instance);
        _ethereumEcdsa = new EthereumEcdsa(_specProvider.ChainId);
    }

    [TearDown]
    public void TearDown() => _worldStateCloser.Dispose();

    [TestCase(true, true)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(false, false)]
    public void Check_fees_with_fee_collector(bool isTransactionEip1559, bool withFeeCollector)
    {
        if (withFeeCollector)
        {
            _spec.FeeCollector = TestItem.AddressC;
        }

        Transaction tx = Build.A.Transaction
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).WithGasPrice(10).WithMaxFeePerGas(10)
            .WithType(isTransactionEip1559 ? TxType.EIP1559 : TxType.Legacy).WithGasLimit(21000).TestObject;
        Block block = Build.A.Block.WithNumber(1)
            .WithBeneficiary(TestItem.AddressB).WithBaseFeePerGas(1).WithTransactions(tx).WithGasLimit(21000)
            .TestObject;

        FeesTracer tracer = new();
        CompositeBlockTracer compositeTracer = new();
        compositeTracer.Add(tracer);
        compositeTracer.Add(NullBlockTracer.Instance);

        ExecuteAndTrace(block, compositeTracer);

        Assert.That(tracer.Fees, Is.EqualTo((UInt256)189000));
        Assert.That(tracer.BurntFees, Is.EqualTo((UInt256)21000));
    }

    [Test]
    public void Base_fee_collected_is_capped_at_effective_price_when_validation_skipped()
    {
        // Regression: eth_simulateV1 with validation disabled may run a maxFeePerGas = 0 < baseFee tx.
        // The sender pays effectiveGasPrice = 0, so the fee collector must not be credited baseFee * gasUsed
        // (= 0xa * 0x5208, which would mint value); it is capped at what was actually paid. A 0 < maxFeePerGas
        // < baseFee tx cannot reach PayFees (rejected as MaxFeePerGasBelowBaseFee in BuyGas), so the effective
        // price here is either 0 or >= baseFee.
        _spec.FeeCollector = TestItem.AddressC;

        Transaction tx = Build.A.Transaction
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA)
            .WithMaxFeePerGas(0).WithMaxPriorityFeePerGas(0)
            .WithType(TxType.EIP1559).WithGasLimit(21000).TestObject;
        Block block = Build.A.Block.WithNumber(1)
            .WithBeneficiary(TestItem.AddressB).WithBaseFeePerGas(10).WithTransactions(tx).WithGasLimit(21000)
            .TestObject;

        FeesTracer feesTracer = new();
        BlockReceiptsTracer receiptsTracer = new();
        receiptsTracer.SetOtherTracer(feesTracer);
        receiptsTracer.StartNewBlockTrace(block);
        ITxTracer txTracer = receiptsTracer.StartNewTxTrace(tx);
        _transactionProcessor.SetBlockExecutionContext(new BlockExecutionContext(block.Header, _spec));
        _transactionProcessor.Process(tx, txTracer, ExecutionOptions.SkipValidationAndCommit);
        receiptsTracer.EndTxTrace();
        receiptsTracer.EndBlockTrace();

        Assert.That(feesTracer.Fees, Is.EqualTo(UInt256.Zero));       // no tip to the coinbase
        Assert.That(feesTracer.BurntFees, Is.EqualTo(UInt256.Zero));  // capped base fee (0), not baseFee * gasUsed
        Assert.That(_stateProvider.GetBalance(TestItem.AddressC), Is.EqualTo(UInt256.Zero)); // fee collector not credited
    }

    private readonly Address SelfDestructAddress = new("0x89aa9b2ce05aaef815f25b237238c0b4ffff6ae3");

    [Test]
    public void Check_fees_with_fee_collector_destroy_coinbase()
    {
        _spec.FeeCollector = TestItem.AddressC;

        _stateProvider.CreateAccount(TestItem.AddressB, 100.Ether);

        byte[] byteCode = Prepare.EvmCode
            .PushData(SelfDestructAddress)
            .Op(Instruction.SELFDESTRUCT)
            .Done;

        Transaction tx = Build.A.Transaction
            .WithGasPrice(10)
            .WithMaxFeePerGas(10)
            .WithChainId(BlockchainIds.Gnosis)
            .WithType(TxType.EIP1559)
            .WithGasLimit(30000000)
            .WithCode(byteCode)
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyB).TestObject;
        Block block = Build.A.Block.WithNumber(1)
            .WithBeneficiary(SelfDestructAddress).WithBaseFeePerGas(1).WithTransactions(tx).WithGasLimit(30000000)
            .TestObject;

        FeesTracer tracer = new();
        CompositeBlockTracer compositeTracer = new();
        compositeTracer.Add(tracer);
        compositeTracer.Add(NullBlockTracer.Instance);

        ExecuteAndTrace(block, compositeTracer);

        Assert.That(tracer.Fees, Is.EqualTo((UInt256)525213));
        Assert.That(tracer.BurntFees, Is.EqualTo((UInt256)58357));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Check_paid_fees_multiple_transactions(bool withFeeCollector)
    {
        if (withFeeCollector)
        {
            _spec.FeeCollector = TestItem.AddressC;
        }

        Transaction tx1 = Build.A.Transaction
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).WithType(TxType.EIP1559)
            .WithMaxFeePerGas(3).WithMaxPriorityFeePerGas(1).WithGasLimit(21000).TestObject;
        Transaction tx2 = Build.A.Transaction.WithType(TxType.Legacy)
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).WithNonce(1)
            .WithGasPrice(10).WithGasLimit(21000).TestObject;
        Block block = Build.A.Block.WithNumber(0).WithBaseFeePerGas(2)
            .WithBeneficiary(TestItem.AddressB).WithTransactions(tx1, tx2).WithGasLimit(42000).TestObject;

        FeesTracer tracer = new();
        ExecuteAndTrace(block, tracer);

        // tx1: 1 * 21000
        // tx2: (10 - 2) * 21000 = 168000
        Assert.That(tracer.Fees, Is.EqualTo((UInt256)189000));

        Assert.That(block.GasUsed, Is.EqualTo(42000));
        Assert.That(tracer.BurntFees, Is.EqualTo((UInt256)84000));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Check_paid_fees_with_blob(bool withFeeCollector)
    {
        UInt256 initialBalance = 0;
        if (withFeeCollector)
        {
            _spec.FeeCollector = TestItem.AddressC;
            initialBalance = _stateProvider.GetBalance(TestItem.AddressC);
        }

        BlockHeader header = Build.A.BlockHeader.WithExcessBlobGas(0).TestObject;

        Transaction tx = Build.A.Transaction
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).WithType(TxType.Blob)
            .WithBlobVersionedHashes(1).WithMaxFeePerBlobGas(1).TestObject;

        Block block = Build.A.Block.WithNumber(0).WithBaseFeePerGas(1)
            .WithBeneficiary(TestItem.AddressB).WithTransactions(tx).WithGasLimit(21000).WithHeader(header).TestObject;

        FeesTracer tracer = new();
        ExecuteAndTrace(block, tracer);

        Assert.That(tracer.Fees, Is.EqualTo(UInt256.Zero));

        Assert.That(block.GasUsed, Is.EqualTo(21000));
        Assert.That(tracer.BurntFees, Is.EqualTo((UInt256)131072));

        if (withFeeCollector)
        {
            UInt256 currentBalance = _stateProvider.GetBalance(TestItem.AddressC);
            Assert.That((currentBalance - initialBalance), Is.EqualTo((UInt256)131072));
        }
    }

    [Test]
    public void Check_paid_fees_with_byte_code()
    {
        byte[] byteCode = Prepare.EvmCode
            .CallWithValue(Address.Zero, 0, 1)
            .PushData(1)
            .PushData(1)
            .Op(Instruction.SSTORE)
            .PushData(0)
            .PushData(1)
            .Op(Instruction.SSTORE)
            .Op(Instruction.STOP)
            .Done;
        Transaction tx1 = Build.A.Transaction
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).WithMaxFeePerGas(3).WithMaxPriorityFeePerGas(2)
            .WithType(TxType.EIP1559).WithGasLimit(21000).TestObject;
        Transaction tx2 = Build.A.Transaction
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).WithNonce(1).WithGasPrice(10)
            .WithType(TxType.Legacy).WithGasLimit(21000).TestObject;
        Transaction tx3 = Build.A.Transaction
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).WithNonce(2).WithMaxFeePerGas(2).WithMaxPriorityFeePerGas(1)
            .WithType(TxType.EIP1559).WithCode(byteCode)
            .WithGasLimit(60000).TestObject;
        Block block = Build.A.Block.WithNumber(MainnetSpecProvider.LondonBlockNumber)
            .WithBeneficiary(TestItem.AddressB).WithBaseFeePerGas(1).WithTransactions(tx1, tx2, tx3)
            .WithGasLimit(102000).TestObject;

        FeesTracer tracer = new();
        ExecuteAndTrace(block, tracer);

        // tx1: 2 * 21000
        // tx2: (10 - 1) * 21000
        // tx3: 1 * 60000
        Assert.That(tracer.Fees, Is.EqualTo((UInt256)291000));

        Assert.That(block.GasUsed, Is.EqualTo(102000));
        Assert.That(tracer.BurntFees, Is.EqualTo((UInt256)102000));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Should_stop_when_cancellation(bool withCancellation)
    {
        Transaction tx1 = Build.A.Transaction
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).WithType(TxType.EIP1559)
            .WithMaxFeePerGas(3).WithMaxPriorityFeePerGas(1).WithGasLimit(21000).TestObject;
        Transaction tx2 = Build.A.Transaction.WithType(TxType.Legacy)
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).WithNonce(1)
            .WithGasPrice(10).WithGasLimit(21000).TestObject;
        Block block = Build.A.Block.WithNumber(0).WithBaseFeePerGas(2)
            .WithBeneficiary(TestItem.AddressB).WithTransactions(tx1, tx2).WithGasLimit(42000).TestObject;

        FeesTracer feesTracer = new();

        CancellationTokenSource source = new();
        CancellationToken token = source.Token;

        CancellationBlockTracer cancellationBlockTracer = new(feesTracer, token);

        BlockReceiptsTracer blockTracer = new();
        blockTracer.SetOtherTracer(cancellationBlockTracer);

        BlockExecutionContext blkCtx = new(block.Header, _spec);
        blockTracer.StartNewBlockTrace(block);
        {
            ITxTracer txTracer = blockTracer.StartNewTxTrace(tx1);
            _transactionProcessor.Execute(tx1, blkCtx, txTracer);
            blockTracer.EndTxTrace();
        }

        if (withCancellation)
        {
            source.Cancel();
        }

        try
        {
            ITxTracer txTracer = blockTracer.StartNewTxTrace(tx2);
            _transactionProcessor.Execute(tx2, blkCtx, txTracer);
            blockTracer.EndTxTrace();
            blockTracer.EndBlockTrace();
        }
        catch (OperationCanceledException) { }

        if (withCancellation)
        {
            // tx1: 1 * 21000
            Assert.That(feesTracer.Fees, Is.EqualTo((UInt256)21000));
            Assert.That(feesTracer.BurntFees, Is.EqualTo((UInt256)42000));
        }
        else
        {
            // tx2: (10 - 2) * 21000 = 168000
            Assert.That(feesTracer.Fees, Is.EqualTo((UInt256)189000));
            Assert.That(feesTracer.BurntFees, Is.EqualTo((UInt256)84000));
        }
    }

    [Test]
    public void Check_fees_with_free_transaction()
    {
        Transaction tx1 = Build.A.Transaction
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).WithType(TxType.EIP1559)
            .WithMaxFeePerGas(3).WithMaxPriorityFeePerGas(1).WithGasLimit(21000).TestObject;
        Transaction tx2 = Build.A.Transaction
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).WithNonce(1).WithIsServiceTransaction(true)
            .WithType(TxType.EIP1559).WithMaxFeePerGas(3)
            .WithMaxPriorityFeePerGas(1).WithGasLimit(21000).TestObject;
        Transaction tx3 = new SystemTransaction();
        Block block = Build.A.Block.WithNumber(0).WithBaseFeePerGas(1)
            .WithBeneficiary(TestItem.AddressB).WithTransactions(tx1, tx2, tx3).WithGasLimit(42000).TestObject;

        FeesTracer tracer = new();
        ExecuteAndTrace(block, tracer);

        Assert.That(tracer.Fees, Is.EqualTo((UInt256)42000));

        Assert.That(block.GasUsed, Is.EqualTo(42000));
        Assert.That(tracer.BurntFees, Is.EqualTo((UInt256)21000));
    }

    [TestCase(TxType.EIP1559)]
    [TestCase(TxType.Legacy)]
    public void CallAndRestore_returns_descriptive_error_when_maxFeePerGas_below_baseFee(TxType txType)
    {
        UInt256 baseFee = 100;
        UInt256 feeCap = 50;

        Transaction tx = txType == TxType.EIP1559
            ? Build.A.Transaction
                .WithType(TxType.EIP1559)
                .WithMaxFeePerGas(feeCap)
                .WithMaxPriorityFeePerGas(feeCap)
                .WithGasLimit(21000)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA)
                .TestObject
            : Build.A.Transaction
                .WithType(TxType.Legacy)
                .WithGasPrice(feeCap)
                .WithGasLimit(21000)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA)
                .TestObject;

        Block block = Build.A.Block
            .WithNumber(1)
            .WithBaseFeePerGas(baseFee)
            .WithTransactions(tx)
            .WithGasLimit(21000)
            .TestObject;

        BlockExecutionContext blkCtx = new(block.Header, _spec);
        TransactionResult result = _transactionProcessor.CallAndRestore(tx, blkCtx, NullTxTracer.Instance);

        Assert.That(result.Error, Is.EqualTo(TransactionResult.ErrorType.MaxFeePerGasBelowBaseFee));
        Assert.That(result.ErrorDescription, Does.Contain($"maxFeePerGas: {feeCap}"));
        Assert.That(result.ErrorDescription, Does.Contain($"baseFee: {baseFee}"));
        Assert.That(result.ErrorDescription, Does.Contain(TestItem.AddressA.ToString(withEip55Checksum: true)));
    }

    private void ExecuteAndTrace(Block block, IBlockTracer otherTracer)
    {
        BlockReceiptsTracer tracer = new();
        tracer.SetOtherTracer(otherTracer);

        tracer.StartNewBlockTrace(block);
        foreach (Transaction tx in block.Transactions)
        {
            ITxTracer txTracer = tracer.StartNewTxTrace(tx);
            _transactionProcessor.Execute(tx, new BlockExecutionContext(block.Header, _spec), txTracer);
            tracer.EndTxTrace();
        }
        tracer.EndBlockTrace();
    }
}
