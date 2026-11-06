// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
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

    [Test]
    public void Check_fees_with_fee_collector([Values] bool isTransactionEip1559, [Values] bool withFeeCollector)
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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.Fees, Is.EqualTo((UInt256)189000));
            Assert.That(tracer.BurntFees, Is.EqualTo((UInt256)21000));
        }
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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(feesTracer.Fees, Is.EqualTo(UInt256.Zero));       // no tip to the coinbase
            Assert.That(feesTracer.BurntFees, Is.EqualTo(UInt256.Zero));  // capped base fee (0), not baseFee * gasUsed
            Assert.That(_stateProvider.GetBalance(TestItem.AddressC), Is.EqualTo(UInt256.Zero)); // fee collector not credited
        }
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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.Fees, Is.EqualTo((UInt256)525213));
            Assert.That(tracer.BurntFees, Is.EqualTo((UInt256)58357));
        }
    }

    [Test]
    public void Destroyed_gas_beneficiary_is_paid_before_destruction([Values] bool revert, [Values] bool eip8037, [Values] bool eip7708, [Values] bool eip8246)
    {
        OverridableReleaseSpec spec = new(Amsterdam.Instance) { IsEip8037Enabled = eip8037, IsEip7708Enabled = eip7708, IsEip8246Enabled = eip8246 };
        Address creator = ContractAddress.From(TestItem.AddressA, 0);
        Address beneficiary = ContractAddress.From(creator, 1);

        byte[] selfDestructInitCode = Prepare.EvmCode
            .PushData(beneficiary)
            .Op(Instruction.SELFDESTRUCT)
            .Done;
        Prepare initCode = Prepare.EvmCode
            .Create(selfDestructInitCode, UInt256.Zero)
            .Op(Instruction.POP);
        if (revert)
        {
            initCode.Revert(0, 0);
        }

        Transaction tx = Build.A.Transaction
            .WithGasPrice(10)
            .WithMaxFeePerGas(10)
            .WithType(TxType.EIP1559)
            .WithGasLimit(5_000_000)
            .WithCode(initCode.Done)
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
        Block block = Build.A.Block.WithNumber(1)
            .WithBeneficiary(beneficiary).WithBaseFeePerGas(1).WithTransactions(tx).WithGasLimit(30_000_000)
            .TestObject;

        FeesTracer feesTracer = new();
        BlockReceiptsTracer receiptsTracer = new();
        receiptsTracer.SetOtherTracer(feesTracer);
        receiptsTracer.StartNewBlockTrace(block);
        receiptsTracer.StartNewTxTrace(tx);
        _transactionProcessor.Execute(tx, new BlockExecutionContext(block.Header, spec), receiptsTracer);
        receiptsTracer.EndTxTrace();
        receiptsTracer.EndBlockTrace();

        // execution-specs pay the coinbase, then destroy: the fee is burned unless EIP-8246 keeps the balance.
        // A reverted tx destroys nothing, so the fee is always kept.
        bool feeKept = revert || eip8246;
        bool burnLogged = !revert && eip8037 && eip7708 && !eip8246;
        TxReceipt receipt = receiptsTracer.TxReceipts[0];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(receipt.StatusCode, Is.EqualTo(revert ? StatusCode.Failure : StatusCode.Success));
            Assert.That(feesTracer.Fees, Is.GreaterThan(UInt256.Zero));
            Assert.That(_stateProvider.GetBalance(beneficiary), Is.EqualTo(feeKept ? feesTracer.Fees : UInt256.Zero));
            Assert.That(_stateProvider.AccountExists(beneficiary), Is.EqualTo(feeKept));
            Assert.That(_stateProvider.GetNonce(beneficiary), Is.EqualTo(0UL));
            Assert.That(receipt.Logs!.Count(static log => log.Topics[0] == TransferLog.BurnSignature), Is.EqualTo(burnLogged ? 1 : 0));
        }
    }

    [Test]
    public void Check_paid_fees_multiple_transactions([Values] bool withFeeCollector)
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
        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.Fees, Is.EqualTo((UInt256)189000));

            Assert.That(block.GasUsed, Is.EqualTo(42000));
            Assert.That(tracer.BurntFees, Is.EqualTo((UInt256)84000));
        }
    }

    [Test]
    public void Check_paid_fees_with_blob([Values] bool withFeeCollector)
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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.Fees, Is.EqualTo(UInt256.Zero));

            Assert.That(block.GasUsed, Is.EqualTo(21000));
            Assert.That(tracer.BurntFees, Is.EqualTo((UInt256)131072));
        }

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
        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.Fees, Is.EqualTo((UInt256)291000));

            Assert.That(block.GasUsed, Is.EqualTo(102000));
            Assert.That(tracer.BurntFees, Is.EqualTo((UInt256)102000));
        }
    }

    [Test]
    public void Should_stop_when_cancellation([Values] bool withCancellation)
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
            using (Assert.EnterMultipleScope())
            {
                Assert.That(feesTracer.Fees, Is.EqualTo((UInt256)21000));
                Assert.That(feesTracer.BurntFees, Is.EqualTo((UInt256)42000));
            }
        }
        else
        {
            // tx2: (10 - 2) * 21000 = 168000
            using (Assert.EnterMultipleScope())
            {
                Assert.That(feesTracer.Fees, Is.EqualTo((UInt256)189000));
                Assert.That(feesTracer.BurntFees, Is.EqualTo((UInt256)84000));
            }
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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.Fees, Is.EqualTo((UInt256)42000));

            Assert.That(block.GasUsed, Is.EqualTo(42000));
            Assert.That(tracer.BurntFees, Is.EqualTo((UInt256)21000));
        }
    }

    [Test]
    public void CallAndRestore_returns_descriptive_error_when_maxFeePerGas_below_baseFee([Values(TxType.EIP1559, TxType.Legacy)] TxType txType)
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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Error, Is.EqualTo(TransactionResult.ErrorType.MaxFeePerGasBelowBaseFee));
            Assert.That(result.ErrorDescription, Does.Contain($"maxFeePerGas: {feeCap}"));
        }
        Assert.That(result.ErrorDescription, Does.Contain($"baseFee: {baseFee}"));
        Assert.That(result.ErrorDescription, Does.Contain(TestItem.AddressA.ToString(withEip55Checksum: true)));
    }

    private const ExecutionOptions FeeFreeCall = ExecutionOptions.CommitAndRestore
        | ExecutionOptions.SkipTransactionFees | ExecutionOptions.SkipLegacyGasPriceValidation;
    private static readonly byte[] FeeContextCode = Convert.FromHexString("3a6000524860205233316040524760605260806000f3");

    [Test]
    public void Fee_free_call_preserves_gas_opcodes_and_value_balances([Values] bool typed)
    {
        ObservingFeeProcessor processor = CreateObservingFeeProcessor();
        Transaction tx = FeeCallTransaction(TestItem.AddressA, typed, 10, value: 7);
        BlockHeader header = FeeCallHeader();
        CallOutputTracer tracer = new();

        processor.SetBlockExecutionContext(new BlockExecutionContext(header, _spec));
        TransactionResult result = processor.Process(tx, tracer, FeeFreeCall);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(TransactionResult.Ok));
            Assert.That(ReadFeeWord(tracer, 0), Is.EqualTo((UInt256)(typed ? 5 : 10)));
            Assert.That(ReadFeeWord(tracer, 1), Is.EqualTo((UInt256)2));
            Assert.That(ReadFeeWord(tracer, 2), Is.EqualTo(1.Ether - 7));
            Assert.That(ReadFeeWord(tracer, 3), Is.EqualTo((UInt256)130));
            Assert.That(processor.GasPurchases, Is.Zero);
            Assert.That(processor.RefundsPaid, Is.EqualTo(UInt256.Zero));
            Assert.That(processor.SenderAfterFees, Is.EqualTo(1.Ether - 7));
            Assert.That(processor.BeneficiaryAfterFees, Is.EqualTo(UInt256.Zero));
            Assert.That(processor.CollectorAfterFees, Is.EqualTo(UInt256.Zero));
            Assert.That(_stateProvider.GetBalance(TestItem.AddressA), Is.EqualTo(1.Ether));
            Assert.That(_stateProvider.GetBalance(TestItem.AddressB), Is.EqualTo((UInt256)123));
            Assert.That(_stateProvider.GetNonce(TestItem.AddressA), Is.EqualTo(0UL));
        }
    }

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    public void Fee_free_call_needs_no_gas_funds_or_fee_multiplication(bool typed, bool maximumPrice)
    {
        ObservingFeeProcessor processor = CreateObservingFeeProcessor();
        UInt256 price = maximumPrice ? UInt256.MaxValue : 10;
        Transaction tx = FeeCallTransaction(TestItem.AddressD, typed, price);
        CallOutputTracer tracer = new();

        processor.SetBlockExecutionContext(new BlockExecutionContext(FeeCallHeader(), _spec));
        TransactionResult result = processor.Process(tx, tracer, FeeFreeCall);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(TransactionResult.Ok));
            Assert.That(ReadFeeWord(tracer, 0), Is.EqualTo(typed ? (UInt256)5 : price));
            Assert.That(ReadFeeWord(tracer, 2), Is.EqualTo(UInt256.Zero));
            Assert.That(processor.GasPurchases, Is.Zero);
            Assert.That(processor.RefundsPaid, Is.EqualTo(UInt256.Zero));
            Assert.That(processor.BeneficiaryAfterFees, Is.EqualTo(UInt256.Zero));
            Assert.That(processor.CollectorAfterFees, Is.EqualTo(UInt256.Zero));
            Assert.That(_stateProvider.AccountExists(TestItem.AddressD), Is.False);
        }
    }

    [TestCase(0UL, 0UL, true)]
    [TestCase(7UL, 7UL, true)]
    [TestCase(7UL, 8UL, false)]
    public void Fee_free_call_still_requires_value_funds(ulong balance, ulong value, bool succeeds)
    {
        if (balance != 0) _stateProvider.CreateAccount(TestItem.AddressD, balance);
        ObservingFeeProcessor processor = CreateObservingFeeProcessor();
        Transaction tx = FeeCallTransaction(TestItem.AddressD, typed: false, 10, value);
        CallOutputTracer tracer = new();

        processor.SetBlockExecutionContext(new BlockExecutionContext(FeeCallHeader(), _spec));
        TransactionResult result = processor.Process(tx, tracer, FeeFreeCall);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(succeeds ? TransactionResult.Ok : TransactionResult.InsufficientSenderBalance));
            Assert.That(_stateProvider.GetBalance(TestItem.AddressD), Is.EqualTo((UInt256)balance));
            Assert.That(_stateProvider.GetBalance(TestItem.AddressB), Is.EqualTo((UInt256)123));
            Assert.That(processor.RefundsPaid, Is.EqualTo(UInt256.Zero));
            if (succeeds)
            {
                Assert.That(ReadFeeWord(tracer, 2), Is.EqualTo((UInt256)(balance - value)));
                Assert.That(ReadFeeWord(tracer, 3), Is.EqualTo((UInt256)(123 + value)));
            }
        }
    }

    [Test]
    public void Call_fee_charge_and_legacy_base_fee_validation_are_independent([Values] bool chargeFees, [Values] bool validateLegacy)
    {
        ObservingFeeProcessor processor = CreateObservingFeeProcessor();
        Transaction tx = FeeCallTransaction(TestItem.AddressA, typed: false, 1);
        ExecutionOptions options = ExecutionOptions.CommitAndRestore
            | (chargeFees ? ExecutionOptions.None : ExecutionOptions.SkipTransactionFees)
            | (validateLegacy ? ExecutionOptions.None : ExecutionOptions.SkipLegacyGasPriceValidation);

        processor.SetBlockExecutionContext(new BlockExecutionContext(FeeCallHeader(), _spec));
        TransactionResult result = processor.Process(tx, NullTxTracer.Instance, options);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Error, Is.EqualTo(validateLegacy ? TransactionResult.ErrorType.MaxFeePerGasBelowBaseFee : TransactionResult.ErrorType.None));
            Assert.That(processor.GasPurchases, Is.EqualTo(chargeFees ? 1 : 0));
            Assert.That(processor.RefundsPaid > 0, Is.EqualTo(chargeFees && !validateLegacy));
            Assert.That(_stateProvider.GetBalance(TestItem.AddressA), Is.EqualTo(1.Ether));
        }
    }

    [TestCase(0UL)]
    [TestCase(1UL)]
    public void Fee_free_call_keeps_typed_base_fee_validation(ulong feeCap)
    {
        ObservingFeeProcessor processor = CreateObservingFeeProcessor();
        Transaction tx = FeeCallTransaction(TestItem.AddressD, typed: true, feeCap);
        tx.GasPrice = 0;
        processor.SetBlockExecutionContext(new BlockExecutionContext(FeeCallHeader(), _spec));

        TransactionResult result = processor.Process(tx, NullTxTracer.Instance, FeeFreeCall);

        Assert.That(result.Error, Is.EqualTo(TransactionResult.ErrorType.MaxFeePerGasBelowBaseFee));
    }

    [Test]
    public void Fee_free_options_do_not_leak_to_the_next_consensus_transaction()
    {
        ObservingFeeProcessor processor = CreateObservingFeeProcessor();
        processor.SetBlockExecutionContext(new BlockExecutionContext(FeeCallHeader(), _spec));
        Assert.That(processor.Process(FeeCallTransaction(TestItem.AddressD, false, 10), NullTxTracer.Instance, FeeFreeCall), Is.EqualTo(TransactionResult.Ok));

        Transaction tx = FeeCallTransaction(TestItem.AddressA, typed: false, 10);
        Assert.That(processor.Process(tx, NullTxTracer.Instance, ExecutionOptions.Commit), Is.EqualTo(TransactionResult.Ok));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(processor.GasPurchases, Is.EqualTo(1));
            Assert.That(processor.RefundsPaid, Is.GreaterThan(UInt256.Zero));
            Assert.That(processor.BeneficiaryAfterFees, Is.GreaterThan(UInt256.Zero));
            Assert.That(processor.CollectorAfterFees, Is.GreaterThan(UInt256.Zero));
            Assert.That(_stateProvider.GetNonce(TestItem.AddressA), Is.EqualTo(1UL));
            Assert.That(_stateProvider.GetBalance(TestItem.AddressA), Is.LessThan(1.Ether));
        }
    }

    [TestCase(ExecutionOptions.Commit)]
    [TestCase(ExecutionOptions.SkipValidation)]
    [TestCase(ExecutionOptions.SkipValidationAndCommit)]
    [TestCase(ExecutionOptions.BuildUp)]
    [TestCase(ExecutionOptions.Warmup | ExecutionOptions.SkipValidation)]
    public void Call_fee_flags_are_ignored_outside_restoring_calls(ExecutionOptions options)
    {
        ObservingFeeProcessor processor = CreateObservingFeeProcessor();
        processor.SetBlockExecutionContext(new BlockExecutionContext(FeeCallHeader(), _spec));
        ExecutionOptions misplacedFlags = ExecutionOptions.SkipTransactionFees | ExecutionOptions.SkipLegacyGasPriceValidation;

        TransactionResult result = processor.Process(FeeCallTransaction(TestItem.AddressA, false, 10), NullTxTracer.Instance, options | misplacedFlags);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(TransactionResult.Ok));
            // The exact SkipValidation route is a system call, whose original fee semantics are no-op.
            Assert.That(processor.GasPurchases, Is.EqualTo(options == ExecutionOptions.SkipValidation ? 0 : 1));
            Assert.That(processor.RefundsPaid > 0, Is.EqualTo(options != ExecutionOptions.SkipValidation));
        }
    }

    private ObservingFeeProcessor CreateObservingFeeProcessor()
    {
        _spec.FeeCollector = TestItem.AddressC;
        _stateProvider.CreateAccount(TestItem.AddressB, 123);
        EthereumCodeInfoRepository repository = new(_stateProvider);
        repository.InsertCode(FeeContextCode, TestItem.AddressB, _spec);
        _stateProvider.Commit(_spec);
        _stateProvider.CommitTree(0);
        EthereumVirtualMachine vm = new(new TestBlockhashProvider(_specProvider), _specProvider, LimboLogs.Instance);
        return new(_specProvider, _stateProvider, vm, repository);
    }

    private Transaction FeeCallTransaction(Address sender, bool typed, UInt256 price, UInt256 value = default) =>
        Build.A.Transaction.WithType(typed ? TxType.EIP1559 : TxType.Legacy)
            .WithSenderAddress(sender).WithTo(TestItem.AddressB).WithGasLimit(100_000)
            .WithGasPrice(typed ? (UInt256)3 : price).WithMaxFeePerGas(price).WithValue(value).TestObject;

    private static BlockHeader FeeCallHeader() => Build.A.BlockHeader
        .WithNumber(1).WithBaseFee(2).WithGasLimit(1_000_000).WithBeneficiary(TestItem.AddressE).TestObject;

    private static UInt256 ReadFeeWord(CallOutputTracer tracer, int index) =>
        new(tracer.ReturnValue.AsSpan(index * 32, 32), isBigEndian: true);

    private sealed class ObservingFeeProcessor(
        ISpecProvider specProvider, IWorldState worldState, IVirtualMachine vm, ICodeInfoRepository repository)
        : EthereumTransactionProcessorBase(BlobBaseFeeCalculator.Instance, specProvider, worldState, vm, repository, LimboLogs.Instance)
    {
        public int GasPurchases { get; private set; }
        public UInt256 RefundsPaid { get; private set; }
        public UInt256 SenderAfterFees { get; private set; }
        public UInt256 BeneficiaryAfterFees { get; private set; }
        public UInt256 CollectorAfterFees { get; private set; }

        protected override TransactionResult BuyGas(Transaction tx, IReleaseSpec spec, ITxTracer tracer, ExecutionOptions opts,
            in UInt256 effectiveGasPrice, out UInt256 premiumPerGas, out UInt256 senderReservedGasPayment, out UInt256 blobBaseFee)
        {
            GasPurchases++;
            return base.BuyGas(tx, spec, tracer, opts, in effectiveGasPrice, out premiumPerGas, out senderReservedGasPayment, out blobBaseFee);
        }

        protected override void PayRefund(Transaction tx, UInt256 refundAmount, IReleaseSpec spec)
        {
            RefundsPaid += refundAmount;
            base.PayRefund(tx, refundAmount, spec);
        }

        protected override void PayFees(Transaction tx, BlockHeader header, IReleaseSpec spec, ITxTracer tracer,
            in TransactionSubstate substate, ulong spentGas, in UInt256 premiumPerGas, in UInt256 effectiveGasPrice, in UInt256 blobBaseFee, int statusCode, ExecutionOptions opts)
        {
            base.PayFees(tx, header, spec, tracer, in substate, spentGas, in premiumPerGas, in effectiveGasPrice, in blobBaseFee, statusCode, opts);
            SenderAfterFees = WorldState.GetBalance(tx.SenderAddress!);
            BeneficiaryAfterFees = WorldState.GetBalance(header.GasBeneficiary!);
            CollectorAfterFees = spec.FeeCollector is null ? UInt256.Zero : WorldState.GetBalance(spec.FeeCollector);
        }
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
