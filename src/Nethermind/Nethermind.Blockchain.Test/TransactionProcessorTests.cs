// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Blockchain.Tracing;
using Nethermind.Blockchain.Tracing.ParityStyle;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NUnit.Framework;
using System;
using System.Collections.Generic;

namespace Nethermind.Evm.Test;

[TestFixture(true)]
[TestFixture(false)]
[Todo(Improve.Refactor, "Check why fixture test cases did not work")]
[Parallelizable(ParallelScope.All)]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public partial class TransactionProcessorTests(bool eip155Enabled)
{
    private readonly ISpecProvider _specProvider = MainnetSpecProvider.Instance;
    private IEthereumEcdsa _ethereumEcdsa;
    private ITransactionProcessor _transactionProcessor;
    private IWorldState _stateProvider;
    private BlockHeader _baseBlock = null!;
    private IDisposable _stateCloser;

    private static readonly UInt256 AccountBalance = 1.Ether;
    [SetUp]
    public void Setup()
    {
        _stateProvider = TestWorldStateFactory.CreateForTest();
        _stateCloser = _stateProvider.BeginScope(IWorldState.PreGenesis);
        _stateProvider.CreateAccount(TestItem.AddressA, AccountBalance);
        _stateProvider.Commit(_specProvider.GenesisSpec);
        _stateProvider.CommitTree(0);
        _baseBlock = Build.A.BlockHeader.WithStateRoot(_stateProvider.StateRoot).TestObject;

        EthereumCodeInfoRepository codeInfoRepository = new(_stateProvider);
        EthereumVirtualMachine virtualMachine = new(new TestBlockhashProvider(_specProvider), _specProvider, LimboLogs.Instance);
        _transactionProcessor = new EthereumTransactionProcessor(BlobBaseFeeCalculator.Instance, _specProvider, _stateProvider, virtualMachine, codeInfoRepository, LimboLogs.Instance);
        _ethereumEcdsa = new EthereumEcdsa(_specProvider.ChainId);
    }

    [TearDown]
    public void Teardown() => _stateCloser.Dispose();

    [Test]
    public void Can_process_simple_transaction()
    {
        Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, eip155Enabled).WithGasLimit(100000).TestObject;
        Block block = Build.A.Block.WithNumber(1).WithTransactions(tx).TestObject;
        TransactionResult result = Execute(tx, block);
        Assert.That(result.TransactionExecuted, Is.True);
    }

    [TestCase(true, true)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(false, false)]
    public void Sets_state_root_on_receipts_before_eip658(bool withStateDiff, bool withTrace)
    {
        Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, eip155Enabled).WithGasLimit(100000).TestObject;

        long blockNumber = eip155Enabled
            ? MainnetSpecProvider.ByzantiumBlockNumber
            : MainnetSpecProvider.ByzantiumBlockNumber - 1;
        Block block = Build.A.Block.WithNumber(blockNumber).WithTransactions(tx).TestObject;

        BlockReceiptsTracer tracer = BuildTracer(tx, withStateDiff, withTrace);
        _ = Execute(tx, block, tracer);

        if (eip155Enabled) // we use eip155 check just as a proxy on 658
        {
            Assert.That(tracer.TxReceipts![0].PostTransactionState, Is.Null);
        }
        else
        {
            Assert.That(tracer.TxReceipts![0].PostTransactionState, Is.Not.Null);
        }
    }

    [Test]
    public void Can_handle_quick_fail_on_intrinsic_gas()
    {
        Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, eip155Enabled).WithGasLimit(20000).TestObject;
        Block block = Build.A.Block.WithNumber(1).WithTransactions(tx).TestObject;
        TransactionResult result = Execute(tx, block);
        Assert.That(result.TransactionExecuted, Is.False);
    }

    [Test]
    public void Can_handle_quick_fail_on_missing_sender()
    {
        Transaction tx = Build.A.Transaction.Signed(_ethereumEcdsa, TestItem.PrivateKeyA, eip155Enabled).WithGasLimit(100000).TestObject;
        Block block = Build.A.Block.WithNumber(1).WithTransactions(tx).TestObject;
        TransactionResult result = Execute(tx, block);
        Assert.That(result.TransactionExecuted, Is.False);
    }

    [Test]
    public void Can_handle_quick_fail_on_non_existing_sender_account()
    {
        Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyB, eip155Enabled).WithGasLimit(100000).TestObject;
        Block block = Build.A.Block.WithNumber(1).WithTransactions(tx).TestObject;
        TransactionResult result = Execute(tx, block);
        Assert.That(result.TransactionExecuted, Is.False);
    }

    [Test]
    public void Can_handle_quick_fail_on_invalid_nonce()
    {
        Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, eip155Enabled).WithGasLimit(100000).WithNonce(100).TestObject;
        Block block = Build.A.Block.WithNumber(1).WithTransactions(tx).TestObject;
        TransactionResult result = Execute(tx, block);
        Assert.That(result.TransactionExecuted, Is.False);
    }

    [Test]
    public void Can_handle_quick_fail_on_not_enough_balance_on_intrinsic_gas()
    {
        AccessList.Builder accessListBuilder = new();
        foreach (Address address in TestItem.Addresses)
        {
            accessListBuilder.AddAddress(address);
        }

        Transaction tx = Build.A.Transaction
            .WithGasLimit(GasCostOf.Transaction * 2)
            .WithAccessList(accessListBuilder.Build())
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, eip155Enabled)
            .TestObject;

        tx.Value = AccountBalance - 3 * GasCostOf.Transaction;

        Block block = Build.A.Block.WithNumber(MainnetSpecProvider.BerlinBlockNumber).WithTransactions(tx).TestObject;

        TransactionResult result = Execute(tx, block);
        Assert.That(result.TransactionExecuted, Is.False);
    }

    [Test]
    public void Can_handle_quick_fail_on_not_enough_balance_on_reserved_gas_payment()
    {
        Transaction tx = Build.A.Transaction
            .WithGasLimit(GasCostOf.Transaction * 2)
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, eip155Enabled)
            .TestObject;

        tx.Value = AccountBalance - GasCostOf.Transaction;

        Block block = Build.A.Block.WithNumber(MainnetSpecProvider.BerlinBlockNumber).WithTransactions(tx).TestObject;
        TransactionResult result = Execute(tx, block);
        Assert.That(result.TransactionExecuted, Is.False);
    }

    [Test]
    public void Can_handle_quick_fail_when_balance_is_lower_than_fee_cap_times_gas()
    {
        Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, eip155Enabled)
            .WithMaxPriorityFeePerGas(5.GWei)
            .WithMaxFeePerGas(10.Ether)
            .WithType(TxType.EIP1559)
            .WithGasLimit(100000).TestObject;

        Block block = Build.A.Block.WithNumber(MainnetSpecProvider.LondonBlockNumber).WithTransactions(tx).TestObject;
        TransactionResult result = Execute(tx, block);
        Assert.That(result.TransactionExecuted, Is.False);
    }

    [Test]
    public void Can_handle_quick_fail_on_above_block_gas_limit()
    {
        Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, eip155Enabled).WithGasLimit(100000).TestObject;
        Block block = Build.A.Block.WithNumber(1).WithTransactions(tx).WithGasLimit(20000).TestObject;
        TransactionResult result = Execute(tx, block);
        Assert.That(result.TransactionExecuted, Is.False);
    }

    [Test]
    public void Balance_is_not_changed_on_call_and_restore()
    {
        long gasLimit = 100000;
        Transaction tx = Build.A.Transaction
            .WithValue(AccountBalance - (UInt256)gasLimit)
            .WithGasPrice(1)
            .WithGasLimit(gasLimit)
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, eip155Enabled).TestObject;
        Block block = Build.A.Block.WithNumber(1).WithTransactions(tx).WithGasLimit(gasLimit).TestObject;

        _transactionProcessor.CallAndRestore(tx, new BlockExecutionContext(block.Header, _specProvider.GetSpec(block.Header)), NullTxTracer.Instance);

        Assert.That(_stateProvider.GetBalance(TestItem.PrivateKeyA.Address), Is.EqualTo(1.Ether));
    }

    [Test]
    public void Account_is_not_created_on_call_and_restore()
    {
        long gasLimit = 100000;
        Transaction tx = Build.A.Transaction
            .WithValue(0.Ether)
            .WithGasPrice(1)
            .WithGasLimit(gasLimit)
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyD, eip155Enabled)
            .TestObject;
        Block block = Build.A.Block.WithNumber(1).WithTransactions(tx).WithGasLimit(gasLimit).TestObject;

        Assert.That(_stateProvider.AccountExists(TestItem.PrivateKeyD.Address), Is.False);
        _transactionProcessor.CallAndRestore(tx, new BlockExecutionContext(block.Header, _specProvider.GetSpec(block.Header)), NullTxTracer.Instance);
        Assert.That(_stateProvider.AccountExists(TestItem.PrivateKeyD.Address), Is.False);
    }

    [Test]
    public void Nonce_is_not_changed_on_call_and_restore()
    {
        long gasLimit = 100000;
        Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, eip155Enabled).WithValue(1.Ether - (UInt256)gasLimit).WithGasPrice(1).WithGasLimit(gasLimit).TestObject;
        Block block = Build.A.Block.WithNumber(1).WithTransactions(tx).WithGasLimit(gasLimit).TestObject;

        _transactionProcessor.CallAndRestore(tx, new BlockExecutionContext(block.Header, _specProvider.GetSpec(block.Header)), NullTxTracer.Instance);
        Assert.That(_stateProvider.GetNonce(TestItem.PrivateKeyA.Address), Is.EqualTo(UInt256.Zero));
    }

    [TestCase(true)]
    [TestCase(false)]
    public void Can_estimate_with_value(bool systemUser)
    {
        long gasLimit = 100000;
        Transaction tx = Build.A.Transaction.WithValue(UInt256.MaxValue).WithGasLimit(gasLimit)
            .WithSenderAddress(systemUser ? Address.SystemUser : TestItem.AddressA).TestObject;
        Block block = Build.A.Block.WithParent(_baseBlock).WithTransactions(tx).WithGasLimit(gasLimit).TestObject;

        EstimateGasTracer tracer = new();
        TransactionResult result = _transactionProcessor.CallAndRestore(tx, new BlockExecutionContext(block.Header, _specProvider.GetSpec(block.Header)), tracer);

        if (!systemUser)
        {
            Assert.That(result, Is.EqualTo(TransactionResult.InsufficientMaxFeePerGasForSenderBalance));
        }
        else
        {
            Assert.That(tracer.GasSpent, Is.EqualTo(21000));
        }
    }

    [TestCaseSource(nameof(EstimateWithHighTxValueTestCases))]
    public long Should_not_estimate_tx_with_high_value(UInt256 txValue)
    {
        long gasLimit = 100000;

        Transaction tx = Build.A.Transaction
            .WithValue(txValue)
            .WithGasPrice(0)
            .WithGasLimit(gasLimit)
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, eip155Enabled)
            .TestObject;
        Block block = Build.A.Block.WithNumber(1).WithTransactions(tx).WithGasLimit(gasLimit).TestObject;

        EstimateGasTracer tracer = new();
        BlocksConfig blocksConfig = new();
        GasEstimator estimator = new(_transactionProcessor, _stateProvider, _specProvider, blocksConfig);

        long estimate = estimator.Estimate(tx, block.Header, tracer, out string? err, 0);

        if (txValue == AccountBalance)
        {
            // Gas price is zero so no gas payment is needed; sending the full balance as value is valid.
            Assert.That(err, Is.Null);
            Assert.That(estimate, Is.EqualTo(GasCostOf.Transaction));
        }
        else if (txValue + (UInt256)gasLimit > AccountBalance)
        {
            Assert.That(err, Is.Not.Null); // Should have error
            Assert.That(err, Is.EqualTo("insufficient sender balance for transfer"));
        }
        else
        {
            Assert.That(err, Is.Null); // Should succeed
        }

        return estimate;
    }

    public static IEnumerable<TestCaseData> EstimateWithHighTxValueTestCases
    {
        get
        {
            UInt256 gasLimit = 100000;
            yield return new TestCaseData((UInt256)1)
            { TestName = "Sanity check", ExpectedResult = GasCostOf.Transaction };
            yield return new TestCaseData(AccountBalance - 1 - gasLimit)
            { TestName = "Less than account balance", ExpectedResult = GasCostOf.Transaction };
            yield return new TestCaseData(AccountBalance - GasCostOf.Transaction - gasLimit)
            { TestName = "Account balance - tx cost", ExpectedResult = GasCostOf.Transaction };
            yield return new TestCaseData(AccountBalance - GasCostOf.Transaction - gasLimit + 1)
            { TestName = "More than (account balance - tx cost)", ExpectedResult = GasCostOf.Transaction };
            yield return new TestCaseData(AccountBalance)
            { TestName = "Exactly account balance", ExpectedResult = (long)GasCostOf.Transaction };

            yield return new TestCaseData(AccountBalance + 1)
            { TestName = "More than account balance", ExpectedResult = 0L };
            yield return new TestCaseData(UInt256.MaxValue - gasLimit)
            { TestName = "Max value possible", ExpectedResult = 0L };
        }
    }


    [TestCase(562949953421312ul)]
    [TestCase(562949953421311ul)]
    public void Should_reject_tx_with_high_max_fee_per_gas(ulong topDigit)
    {
        Transaction tx = Build.A.Transaction.WithMaxFeePerGas(new(0, 0, 0, topDigit)).WithGasLimit(32768)
            .WithType(TxType.EIP1559).WithValue(0)
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, eip155Enabled)
            .TestObject;

        long blockNumber = MainnetSpecProvider.LondonBlockNumber;
        Block block = Build.A.Block.WithNumber(blockNumber).WithTransactions(tx).TestObject;
        TransactionResult result = Execute(tx, block);
        Assert.That(result.TransactionExecuted, Is.False);
    }

    [TestCase]
    public void Can_estimate_simple()
    {
        long gasLimit = 100000;
        Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, eip155Enabled).WithGasLimit(gasLimit).TestObject;
        Block block = Build.A.Block.WithParent(_baseBlock).WithTransactions(tx).WithGasLimit(gasLimit).TestObject;

        EstimateGasTracer tracer = new();
        BlocksConfig blocksConfig = new();
        GasEstimator estimator = new(_transactionProcessor, _stateProvider, _specProvider, blocksConfig);

        _transactionProcessor.CallAndRestore(tx, new BlockExecutionContext(block.Header, _specProvider.GetSpec(block.Header)), tracer);

        Assert.That(tracer.GasSpent, Is.EqualTo(21000));
        Assert.That(estimator.Estimate(tx, block.Header, tracer, out string? err, 0), Is.EqualTo(21000));
        Assert.That(err, Is.Null);
    }

    [TestCase]
    public void Can_estimate_with_refund()
    {
        byte[] initByteCode = Prepare.EvmCode
            .PushData(1)
            .PushData(1)
            .Op(Instruction.SSTORE)
            .PushData(0)
            .PushData(1)
            .Op(Instruction.SSTORE)
            .Op(Instruction.STOP)
            .Done;

        long gasLimit = 100000;

        Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, eip155Enabled).WithCode(initByteCode).WithGasLimit(gasLimit).TestObject;
        Block block = Build.A.Block.WithNumber(MainnetSpecProvider.MuirGlacierBlockNumber).WithTransactions(tx).WithGasLimit(2 * gasLimit).TestObject;

        EthereumIntrinsicGas intrinsicGas = IntrinsicGasCalculator.Calculate(tx, MuirGlacier.Instance);

        BlockExecutionContext blkCtx = new(block.Header, _specProvider.GetSpec(block.Header));

        EstimateGasTracer tracer = new();
        _transactionProcessor.CallAndRestore(tx, blkCtx, tracer);

        BlocksConfig blocksConfig = new();
        GasEstimator estimator = new(_transactionProcessor, _stateProvider, _specProvider, blocksConfig);

        long actualIntrinsic = tx.GasLimit - tracer.IntrinsicGasAt;
        Assert.That(actualIntrinsic, Is.EqualTo(intrinsicGas.Standard));
        IReleaseSpec releaseSpec = Berlin.Instance;
        Assert.That(tracer.CalculateAdditionalGasRequired(tx, releaseSpec), Is.EqualTo(RefundOf.SSetReversedEip2200 + GasCostOf.CallStipend - GasCostOf.SStoreNetMeteredEip2200 + 1));
        Assert.That(tracer.GasSpent, Is.EqualTo(54764L));
        long estimate = estimator.Estimate(tx, block.Header, tracer, out string? err, 0);
        Assert.That(estimate, Is.EqualTo(75465L));
        Assert.That(err, Is.Null);

        ConfirmEnoughEstimate(tx, block, estimate);
    }


    [Test(Description = "Since the second call is a CREATE operation it has intrinsic gas of 21000 + 32000 + data")]
    public void Can_estimate_with_destroy_refund_and_below_intrinsic_pre_berlin()
    {
        byte[] initByteCode = Prepare.EvmCode.ForInitOf(Prepare.EvmCode.PushData(Address.Zero).Op(Instruction.SELFDESTRUCT).Done).Done;
        Address contractAddress = ContractAddress.From(TestItem.PrivateKeyA.Address, 0);

        byte[] byteCode = Prepare.EvmCode
            .Call(contractAddress, 46179)
            .Op(Instruction.STOP).Done;

        long gasLimit = 100000;

        Transaction initTx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, eip155Enabled).WithCode(initByteCode).WithGasLimit(gasLimit).TestObject;
        Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, eip155Enabled).WithCode(byteCode).WithGasLimit(gasLimit).WithNonce(1).TestObject;
        Block block = Build.A.Block.WithNumber(MainnetSpecProvider.MuirGlacierBlockNumber).WithTransactions(tx).WithGasLimit(2 * gasLimit).TestObject;

        IReleaseSpec releaseSpec = MuirGlacier.Instance;
        EthereumIntrinsicGas intrinsicGas = IntrinsicGasCalculator.Calculate(tx, releaseSpec);
        BlockExecutionContext blkCtx = new(block.Header, releaseSpec);
        _transactionProcessor.Execute(initTx, blkCtx, NullTxTracer.Instance);

        EstimateGasTracer tracer = new();
        _transactionProcessor.CallAndRestore(tx, blkCtx, tracer);

        BlocksConfig blocksConfig = new();
        GasEstimator estimator = new(_transactionProcessor, _stateProvider, _specProvider, blocksConfig);

        long actualIntrinsic = tx.GasLimit - tracer.IntrinsicGasAt;
        Assert.That(actualIntrinsic, Is.EqualTo(intrinsicGas.Standard));
        Assert.That(tracer.CalculateAdditionalGasRequired(tx, releaseSpec), Is.EqualTo(24080));
        Assert.That(tracer.GasSpent, Is.EqualTo(35228L));
        long estimate = estimator.Estimate(tx, block.Header, tracer, out string? err, 0);
        Assert.That(estimate, Is.EqualTo(54225));
        Assert.That(err, Is.Null);

        ConfirmEnoughEstimate(tx, block, estimate);
    }

    private void ConfirmEnoughEstimate(Transaction tx, Block block, long estimate)
    {
        BlockExecutionContext blkCtx = new(block.Header, _specProvider.GetSpec(block.Header));

        CallOutputTracer outputTracer = new();
        tx.GasLimit = estimate;
        _transactionProcessor.CallAndRestore(tx, blkCtx, outputTracer);
        Assert.That(outputTracer.StatusCode, Is.EqualTo(StatusCode.Success), $"transaction should succeed at the estimate ({estimate})");

        outputTracer = new CallOutputTracer();
        tx.GasLimit = Math.Min(estimate - 1, estimate * 63 / 64);
        _transactionProcessor.CallAndRestore(tx, blkCtx, outputTracer);
        Assert.That(outputTracer.StatusCode, Is.EqualTo(StatusCode.Failure), $"transaction should fail below the estimate ({tx.GasLimit})");
    }

    [TestCase]
    public void Can_estimate_with_stipend()
    {
        byte[] initByteCode = Prepare.EvmCode
            .CallWithValue(Address.Zero, 0, 1)
            .Op(Instruction.STOP)
            .Done;

        long gasLimit = 100000;

        Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, eip155Enabled).WithCode(initByteCode).WithGasLimit(gasLimit).TestObject;
        Block block = Build.A.Block.WithNumber(MainnetSpecProvider.MuirGlacierBlockNumber).WithTransactions(tx).WithGasLimit(2 * gasLimit).TestObject;

        IReleaseSpec releaseSpec = MuirGlacier.Instance;
        EthereumIntrinsicGas intrinsicGas = IntrinsicGasCalculator.Calculate(tx, releaseSpec);

        BlockExecutionContext blkCtx = new(block.Header, releaseSpec);

        EstimateGasTracer tracer = new();
        _transactionProcessor.CallAndRestore(tx, blkCtx, tracer);

        BlocksConfig blocksConfig = new();
        GasEstimator estimator = new(_transactionProcessor, _stateProvider, _specProvider, blocksConfig);

        long actualIntrinsic = tx.GasLimit - tracer.IntrinsicGasAt;
        Assert.That(actualIntrinsic, Is.EqualTo(intrinsicGas.Standard));
        Assert.That(tracer.CalculateAdditionalGasRequired(tx, releaseSpec), Is.EqualTo(2300));
        Assert.That(tracer.GasSpent, Is.EqualTo(85669L));
        long estimate = estimator.Estimate(tx, block.Header, tracer, out string? err, 0);
        Assert.That(estimate, Is.EqualTo(87969L));
        Assert.That(err, Is.Null);

        ConfirmEnoughEstimate(tx, block, estimate);
    }

    [TestCase]
    public void Can_estimate_with_stipend_and_refund()
    {
        byte[] initByteCode = Prepare.EvmCode
            .CallWithValue(Address.Zero, 0, 1)
            .PushData(1)
            .PushData(1)
            .Op(Instruction.SSTORE)
            .PushData(0)
            .PushData(1)
            .Op(Instruction.SSTORE)
            .Op(Instruction.STOP)
            .Done;

        long gasLimit = 200000;

        Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, eip155Enabled).WithCode(initByteCode).WithGasLimit(gasLimit).TestObject;
        Block block = Build.A.Block.WithNumber(MainnetSpecProvider.MuirGlacierBlockNumber).WithTransactions(tx).WithGasLimit(2 * gasLimit).TestObject;

        IReleaseSpec releaseSpec = _specProvider.GetSpec(block.Header);
        EthereumIntrinsicGas intrinsicGas = IntrinsicGasCalculator.Calculate(tx, releaseSpec);

        BlockExecutionContext blkCtx = new(block.Header, releaseSpec);

        EstimateGasTracer tracer = new();
        _transactionProcessor.CallAndRestore(tx, blkCtx, tracer);

        BlocksConfig blocksConfig = new();
        GasEstimator estimator = new(_transactionProcessor, _stateProvider, _specProvider, blocksConfig);

        long actualIntrinsic = tx.GasLimit - tracer.IntrinsicGasAt;
        Assert.That(actualIntrinsic, Is.EqualTo(intrinsicGas.Standard));
        Assert.That(tracer.CalculateAdditionalGasRequired(tx, releaseSpec), Is.EqualTo(RefundOf.SSetReversedEip2200 + GasCostOf.CallStipend));
        Assert.That(tracer.GasSpent, Is.EqualTo(87429L));
        long estimate = estimator.Estimate(tx, block.Header, tracer, out string? err, 0);
        Assert.That(estimate, Is.EqualTo(108130L));
        Assert.That(err, Is.Null);

        ConfirmEnoughEstimate(tx, block, estimate);
    }

    [TestCase]
    public void Can_estimate_with_single_call()
    {
        byte[] initByteCode = Prepare.EvmCode
            .ForInitOf(Bytes.FromHexString("6000")).Done;

        Address contractAddress = ContractAddress.From(TestItem.PrivateKeyA.Address, 0);

        byte[] byteCode = Prepare.EvmCode
            .Call(contractAddress, 46179).Done;

        long gasLimit = 100000;

        Transaction initTx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, eip155Enabled).WithCode(initByteCode).WithGasLimit(gasLimit).TestObject;
        Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, eip155Enabled).WithCode(byteCode).WithGasLimit(gasLimit).WithNonce(1).TestObject;
        Block block = Build.A.Block.WithNumber(MainnetSpecProvider.MuirGlacierBlockNumber).WithTransactions(tx).WithGasLimit(2 * gasLimit).TestObject;

        IReleaseSpec releaseSpec = _specProvider.GetSpec(block.Header);
        EthereumIntrinsicGas intrinsicGas = IntrinsicGasCalculator.Calculate(tx, releaseSpec);

        BlockExecutionContext blkCtx = new(block.Header, releaseSpec);
        _transactionProcessor.Execute(initTx, blkCtx, NullTxTracer.Instance);

        EstimateGasTracer tracer = new();
        _transactionProcessor.CallAndRestore(tx, blkCtx, tracer);

        BlocksConfig blocksConfig = new();
        GasEstimator estimator = new(_transactionProcessor, _stateProvider, _specProvider, blocksConfig);

        long actualIntrinsic = tx.GasLimit - tracer.IntrinsicGasAt;
        Assert.That(actualIntrinsic, Is.EqualTo(intrinsicGas.Standard));
        Assert.That(tracer.CalculateAdditionalGasRequired(tx, releaseSpec), Is.EqualTo(1));
        Assert.That(tracer.GasSpent, Is.EqualTo(54224L));
        long estimate = estimator.Estimate(tx, block.Header, tracer, out string? err, 0);
        Assert.That(estimate, Is.EqualTo(54224L));
        Assert.That(err, Is.Null);

        ConfirmEnoughEstimate(tx, block, estimate);
    }

    [TestCase]
    public void Disables_Eip158_for_system_transactions()
    {
        long blockNumber = MainnetSpecProvider.SpuriousDragonBlockNumber + 1;

        _stateProvider.CreateAccount(TestItem.PrivateKeyA.Address, 0.Ether);
        IReleaseSpec spec = _specProvider.GetSpec((ForkActivation)blockNumber);
        _stateProvider.Commit(spec);
        Transaction tx = Build.A.SystemTransaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, eip155Enabled)
            .WithGasPrice(0)
            .WithValue(0)
            .TestObject;

        Block block = Build.A.Block.WithNumber(blockNumber).WithTransactions(tx).TestObject;

        BlockReceiptsTracer tracer = BuildTracer(tx, false, false);
        Execute(tx, block, tracer);
        Assert.That(_stateProvider.AccountExists(tx.SenderAddress!), Is.True);
    }

    [TestCase]
    public void Balance_is_changed_on_buildup_and_restored()
    {
        long gasLimit = 100000;
        Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, eip155Enabled).WithValue(0).WithGasPrice(1).WithGasLimit(gasLimit).TestObject;
        Block block = Build.A.Block.WithNumber(MainnetSpecProvider.ByzantiumBlockNumber).WithTransactions(tx).WithGasLimit(gasLimit).TestObject;

        Snapshot state = _stateProvider.TakeSnapshot();
        _transactionProcessor.BuildUp(tx, new BlockExecutionContext(block.Header, _specProvider.GetSpec(block.Header)), NullTxTracer.Instance);
        Assert.That(_stateProvider.GetBalance(TestItem.PrivateKeyA.Address), Is.EqualTo(AccountBalance - GasCostOf.Transaction));

        _stateProvider.Restore(state);
        Assert.That(_stateProvider.GetBalance(TestItem.PrivateKeyA.Address), Is.EqualTo(AccountBalance));
    }

    [TestCase]
    public void Account_is_not_created_on_buildup_and_restore()
    {
        long gasLimit = 100000;
        Transaction tx = Build.A.Transaction
            .WithValue(0.Ether)
            .WithGasPrice(1)
            .WithGasLimit(gasLimit)
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyD, eip155Enabled)
            .TestObject;
        Block block = Build.A.Block.WithNumber(MainnetSpecProvider.ByzantiumBlockNumber).WithTransactions(tx).WithGasLimit(gasLimit).TestObject;

        Assert.That(_stateProvider.AccountExists(TestItem.PrivateKeyD.Address), Is.False);
        Snapshot state = _stateProvider.TakeSnapshot();
        _transactionProcessor.BuildUp(tx, new BlockExecutionContext(block.Header, _specProvider.GetSpec(block.Header)), NullTxTracer.Instance);
        Assert.That(_stateProvider.AccountExists(TestItem.PrivateKeyD.Address), Is.True);
        _stateProvider.Restore(state);
        Assert.That(_stateProvider.AccountExists(TestItem.PrivateKeyD.Address), Is.False);
    }

    [TestCase]
    public void Nonce_is_not_changed_on_buildup_and_restore()
    {
        long gasLimit = 100000;
        Transaction tx = Build.A.Transaction
            .WithValue(AccountBalance - (UInt256)gasLimit)
            .WithGasPrice(1)
            .WithGasLimit(gasLimit)
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, eip155Enabled)
            .TestObject;
        Block block = Build.A.Block.WithNumber(MainnetSpecProvider.ByzantiumBlockNumber).WithTransactions(tx).WithGasLimit(gasLimit).TestObject;

        Snapshot state = _stateProvider.TakeSnapshot();
        _transactionProcessor.BuildUp(tx, new BlockExecutionContext(block.Header, _specProvider.GetSpec(block.Header)), NullTxTracer.Instance);
        Assert.That(_stateProvider.GetNonce(TestItem.PrivateKeyA.Address), Is.EqualTo((UInt256)1));
        _stateProvider.Restore(state);
        Assert.That(_stateProvider.GetNonce(TestItem.PrivateKeyA.Address), Is.EqualTo(UInt256.Zero));
    }

    [TestCase]
    public void State_changed_twice_in_buildup_should_have_correct_gas_cost()
    {
        long gasLimit = 100000;
        Transaction tx1 = Build.A.Transaction
            .WithValue(0).WithGasPrice(1).WithGasLimit(GasCostOf.Transaction)
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, eip155Enabled)
            .TestObject;
        Transaction tx2 = Build.A.Transaction
            .WithValue(0).WithNonce(1).WithGasPrice(1).WithGasLimit(GasCostOf.Transaction)
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, eip155Enabled)
            .TestObject;
        Block block = Build.A.Block.WithNumber(MainnetSpecProvider.ByzantiumBlockNumber).WithTransactions(tx1, tx2).WithGasLimit(gasLimit).TestObject;

        Snapshot state = _stateProvider.TakeSnapshot();
        BlockExecutionContext blkCtx = new(block.Header, _specProvider.GetSpec(block.Header));
        _transactionProcessor.BuildUp(tx1, blkCtx, NullTxTracer.Instance);
        Assert.That(_stateProvider.GetBalance(TestItem.PrivateKeyA.Address), Is.EqualTo(AccountBalance - GasCostOf.Transaction));

        _transactionProcessor.BuildUp(tx2, blkCtx, NullTxTracer.Instance);
        Assert.That(_stateProvider.GetBalance(TestItem.PrivateKeyA.Address), Is.EqualTo(AccountBalance - GasCostOf.Transaction * 2));

        _stateProvider.Restore(state);
        Assert.That(_stateProvider.GetBalance(TestItem.PrivateKeyA.Address), Is.EqualTo(AccountBalance));
    }

    private BlockReceiptsTracer BuildTracer(Transaction tx, bool stateDiff, bool trace)
    {
        ParityTraceTypes types = ParityTraceTypes.None;
        if (stateDiff)
        {
            types |= ParityTraceTypes.StateDiff;
        }

        if (trace)
        {
            types |= ParityTraceTypes.Trace;
        }

        IBlockTracer otherTracer = types != ParityTraceTypes.None ? new ParityLikeBlockTracer(tx.Hash!, ParityTraceTypes.Trace | ParityTraceTypes.StateDiff) : NullBlockTracer.Instance;
        BlockReceiptsTracer tracer = new();
        tracer.SetOtherTracer(otherTracer);
        return tracer;
    }

    private TransactionResult Execute(Transaction tx, Block block, BlockReceiptsTracer? tracer = null)
    {
        tracer?.StartNewBlockTrace(block);
        tracer?.StartNewTxTrace(tx);
        TransactionResult result = _transactionProcessor.Execute(tx, new BlockExecutionContext(block.Header, _specProvider.GetSpec(block.Header)), tracer ?? NullTxTracer.Instance);
        if (result)
        {
            tracer?.EndTxTrace();
            tracer?.EndBlockTrace();
        }

        return result;
    }

    [Test]
    public void Warmup_does_not_update_SpentGas()
    {
        Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, eip155Enabled)
            .WithGasLimit(100000).TestObject;
        Block block = Build.A.Block.WithNumber(1).WithTransactions(tx).TestObject;

        // Use a sentinel value because the SpentGas getter returns GasLimit when _spentGas is 0
        const long sentinel = 42;
        tx.SpentGas = sentinel;

        _transactionProcessor.Warmup(tx, new BlockExecutionContext(block.Header, _specProvider.GetSpec(block.Header)), NullTxTracer.Instance);

        Assert.That(tx.SpentGas, Is.EqualTo(sentinel), "Warmup must not modify tx.SpentGas");
    }

    [Test]
    public void Warmup_does_not_modify_sender_nonce()
    {
        Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, eip155Enabled)
            .WithGasLimit(100000).TestObject;
        Block block = Build.A.Block.WithNumber(1).WithTransactions(tx).TestObject;

        UInt256 nonceBefore = _stateProvider.GetNonce(TestItem.AddressA);

        _transactionProcessor.Warmup(tx, new BlockExecutionContext(block.Header, _specProvider.GetSpec(block.Header)), NullTxTracer.Instance);

        Assert.That(_stateProvider.GetNonce(TestItem.AddressA), Is.EqualTo(nonceBefore), "Warmup must not increment sender nonce");
    }

    [Test]
    public void Warmup_does_not_deduct_sender_balance()
    {
        Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, eip155Enabled)
            .WithGasLimit(100000).TestObject;
        Block block = Build.A.Block.WithNumber(1).WithTransactions(tx).TestObject;

        UInt256 balanceBefore = _stateProvider.GetBalance(TestItem.AddressA);

        _transactionProcessor.Warmup(tx, new BlockExecutionContext(block.Header, _specProvider.GetSpec(block.Header)), NullTxTracer.Instance);

        Assert.That(_stateProvider.GetBalance(TestItem.AddressA), Is.EqualTo(balanceBefore), "Warmup must not deduct sender balance (should use SystemTransactionProcessor path)");
    }

    [Test]
    public void ShouldExecuteEvm_returning_false_skips_EVM()
    {
        IReleaseSpec spec = Prague.Instance;
        Address recipient = CreateContractRecipient(_stateProvider, spec, 1502);
        _stateProvider.Commit(spec);
        _stateProvider.CommitTree(0);

        CountingVirtualMachine vm = new(new TestBlockhashProvider(_specProvider), _specProvider, LimboLogs.Instance);
        EthereumCodeInfoRepository codeInfoRepository = new(_stateProvider);
        NoOpTransactionProcessor processor = new(
            BlobBaseFeeCalculator.Instance, _specProvider, _stateProvider, vm, codeInfoRepository, LimboLogs.Instance);

        Transaction tx = Build.A.Transaction
            .WithTo(recipient)
            .WithGasPrice(1)
            .WithGasLimit(100_000)
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, eip155Enabled)
            .TestObject;
        Block block = BuildPragueBlock(tx);

        TransactionResult result = processor.Execute(tx, new BlockExecutionContext(block.Header, spec), NullTxTracer.Instance);

        Assert.That(result.TransactionExecuted, Is.True);
        Assert.That(vm.ExecuteTransactionCalls, Is.EqualTo(0));
        // Only intrinsic gas consumed — unused gas was refunded via FinalizeTransaction
        Assert.That(tx.SpentGas, Is.EqualTo(GasCostOf.Transaction));
        Assert.That(block.Header.GasUsed, Is.EqualTo(GasCostOf.Transaction));
        Assert.That(_stateProvider.GetNonce(TestItem.AddressA), Is.EqualTo(UInt256.One));
    }

    /// <summary>
    /// Demonstrates the correct override pattern for <see cref="TransactionProcessorBase{TGasPolicy}.ExecuteEvm"/>:
    /// to omit execution, the override must call <see cref="TransactionProcessorBase{TGasPolicy}.Refund"/>,
    /// <see cref="TransactionProcessorBase{TGasPolicy}.UpdateHeaderGasUsedAndPayFees"/>, and
    /// <see cref="TransactionProcessorBase{TGasPolicy}.FinalizeTransaction"/> itself so that gas accounting,
    /// fee payment, and state commit/restore are handled correctly.
    /// </summary>
    private sealed class NoOpTransactionProcessor(
        ITransactionProcessor.IBlobBaseFeeCalculator blobBaseFeeCalculator,
        ISpecProvider specProvider,
        IWorldState worldState,
        IVirtualMachine virtualMachine,
        ICodeInfoRepository codeInfoRepository,
        ILogManager logManager)
        : EthereumTransactionProcessorBase(blobBaseFeeCalculator, specProvider, worldState, virtualMachine, codeInfoRepository, logManager)
    {
        protected override TransactionResult ExecuteEvm(
            Transaction tx,
            BlockHeader header,
            IReleaseSpec spec,
            ITxTracer tracer,
            ExecutionOptions opts,
            bool restore,
            bool commit,
            bool deleteCallerAccount,
            in IntrinsicGas<EthereumGasPolicy> intrinsicGas,
            EthereumGasPolicy gasAvailable,
            in UInt256 opcodeGasPrice,
            in UInt256 premiumPerGas,
            in UInt256 senderReservedGasPayment,
            in UInt256 blobBaseFee,
            bool useSimpleTransferFastPath,
            CodeInfo? preloadedCodeInfo,
            Address? preloadedDelegationAddress)
        {
            TransactionSubstate substate = new(default, 0, null, null, false, false, logger: Logger);
            EthereumGasPolicy floorGas = intrinsicGas.FloorGas;
            EthereumGasPolicy standardGas = intrinsicGas.Standard;
            long postIntrinsicStateReservoir = EthereumGasPolicy.GetStateReservoir(in gasAvailable);
            GasConsumed gasConsumed = Refund(tx, header, spec, opts, in substate, in gasAvailable,
                in opcodeGasPrice, 0, in floorGas, in standardGas, postIntrinsicStateReservoir);
            UpdateHeaderGasUsedAndPayFees(tx, header, spec, tracer, opts, in substate, in gasConsumed,
                in premiumPerGas, in blobBaseFee, StatusCode.Success);
            Address executingAccount = tx.GetRecipient(tx.IsContractCreation ? WorldState.GetNonce(tx.SenderAddress!) : 0)!;
            return FinalizeTransaction(tx, spec, tracer, opts, restore, commit, deleteCallerAccount,
                in senderReservedGasPayment, executingAccount, in substate, gasConsumed, StatusCode.Success);
        }
    }
}
