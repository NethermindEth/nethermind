// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Evm.Tracing;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Blockchain.Tracing.ParityStyle;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs.Forks;
using Nethermind.Evm.State;
using NUnit.Framework;
using Nethermind.Config;
using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;

namespace Nethermind.Evm.Test;

[TestFixture(true)]
[TestFixture(false)]
[Todo(Improve.Refactor, "Check why fixture test cases did not work")]
[Parallelizable(ParallelScope.All)]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public class TransactionProcessorTests
{
    private readonly bool _isEip155Enabled;
    private readonly ISpecProvider _specProvider;
    private IEthereumEcdsa _ethereumEcdsa;
    private ITransactionProcessor _transactionProcessor;
    private IWorldState _stateProvider;
    private BlockHeader _baseBlock = null!;
    private IDisposable _stateCloser;

    public TransactionProcessorTests(bool eip155Enabled)
    {
        _isEip155Enabled = eip155Enabled;
        _specProvider = MainnetSpecProvider.Instance;
    }

    private static readonly UInt256 AccountBalance = 1.Ether();

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
    public void Teardown()
    {
        _stateCloser.Dispose();
    }

    [TestCase(true, true)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(false, false)]
    public void Can_process_simple_transaction(bool withStateDiff, bool withTrace)
    {
        Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, _isEip155Enabled).WithGasLimit(100000).TestObject;
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
        Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, _isEip155Enabled).WithGasLimit(100000).TestObject;

        long blockNumber = _isEip155Enabled
            ? MainnetSpecProvider.ByzantiumBlockNumber
            : MainnetSpecProvider.ByzantiumBlockNumber - 1;
        Block block = Build.A.Block.WithNumber(blockNumber).WithTransactions(tx).TestObject;

        BlockReceiptsTracer tracer = BuildTracer(block, tx, withStateDiff, withTrace);
        _ = Execute(tx, block, tracer);

        if (_isEip155Enabled) // we use eip155 check just as a proxy on 658
        {
            Assert.That(tracer.TxReceipts![0].PostTransactionState, Is.Null);
        }
        else
        {
            Assert.That(tracer.TxReceipts![0].PostTransactionState, Is.Not.Null);
        }
    }

    [TestCase(true, true)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(false, false)]
    public void Can_handle_quick_fail_on_intrinsic_gas(bool withStateDiff, bool withTrace)
    {
        Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, _isEip155Enabled).WithGasLimit(20000).TestObject;
        Block block = Build.A.Block.WithNumber(1).WithTransactions(tx).TestObject;
        TransactionResult result = Execute(tx, block);
        Assert.That(result.TransactionExecuted, Is.False);
    }

    [TestCase(true, true)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(false, false)]
    public void Can_handle_quick_fail_on_missing_sender(bool withStateDiff, bool withTrace)
    {
        Transaction tx = Build.A.Transaction.Signed(_ethereumEcdsa, TestItem.PrivateKeyA, _isEip155Enabled).WithGasLimit(100000).TestObject;
        Block block = Build.A.Block.WithNumber(1).WithTransactions(tx).TestObject;
        TransactionResult result = Execute(tx, block);
        Assert.That(result.TransactionExecuted, Is.False);
    }

    [TestCase(true, true)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(false, false)]
    public void Can_handle_quick_fail_on_non_existing_sender_account(bool withStateDiff, bool withTrace)
    {
        Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyB, _isEip155Enabled).WithGasLimit(100000).TestObject;
        Block block = Build.A.Block.WithNumber(1).WithTransactions(tx).TestObject;
        TransactionResult result = Execute(tx, block);
        Assert.That(result.TransactionExecuted, Is.False);
    }

    [TestCase(true, true)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(false, false)]
    public void Can_handle_quick_fail_on_invalid_nonce(bool withStateDiff, bool withTrace)
    {
        Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, _isEip155Enabled).WithGasLimit(100000).WithNonce(100).TestObject;
        Block block = Build.A.Block.WithNumber(1).WithTransactions(tx).TestObject;
        TransactionResult result = Execute(tx, block);
        Assert.That(result.TransactionExecuted, Is.False);
    }

    [TestCase(true, true)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(false, false)]
    public void Can_handle_quick_fail_on_not_enough_balance_on_intrinsic_gas(bool withStateDiff, bool withTrace)
    {
        AccessList.Builder accessListBuilder = new();
        foreach (Address address in TestItem.Addresses)
        {
            accessListBuilder.AddAddress(address);
        }

        Transaction tx = Build.A.Transaction
            .WithGasLimit(GasCostOf.Transaction * 2)
            .WithAccessList(accessListBuilder.Build())
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, _isEip155Enabled)
            .TestObject;

        tx.Value = AccountBalance - 3 * GasCostOf.Transaction;

        Block block = Build.A.Block.WithNumber(MainnetSpecProvider.BerlinBlockNumber).WithTransactions(tx).TestObject;

        TransactionResult result = Execute(tx, block);
        Assert.That(result.TransactionExecuted, Is.False);
    }

    [TestCase(true, true)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(false, false)]
    public void Can_handle_quick_fail_on_not_enough_balance_on_reserved_gas_payment(bool withStateDiff, bool withTrace)
    {
        Transaction tx = Build.A.Transaction
            .WithGasLimit(GasCostOf.Transaction * 2)
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, _isEip155Enabled)
            .TestObject;

        tx.Value = AccountBalance - GasCostOf.Transaction;

        Block block = Build.A.Block.WithNumber(MainnetSpecProvider.BerlinBlockNumber).WithTransactions(tx).TestObject;
        TransactionResult result = Execute(tx, block);
        Assert.That(result.TransactionExecuted, Is.False);
    }

    [TestCase(true, true)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(false, false)]
    public void Can_handle_quick_fail_when_balance_is_lower_than_fee_cap_times_gas(bool withStateDiff, bool withTrace)
    {
        Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, _isEip155Enabled)
            .WithMaxPriorityFeePerGas(5.GWei())
            .WithMaxFeePerGas(10.Ether())
            .WithType(TxType.EIP1559)
            .WithGasLimit(100000).TestObject;

        Block block = Build.A.Block.WithNumber(MainnetSpecProvider.LondonBlockNumber).WithTransactions(tx).TestObject;
        TransactionResult result = Execute(tx, block);
        Assert.That(result.TransactionExecuted, Is.False);
    }

    [TestCase(true, true)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(false, false)]
    public void Can_handle_quick_fail_on_above_block_gas_limit(bool withStateDiff, bool withTrace)
    {
        Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, _isEip155Enabled).WithGasLimit(100000).TestObject;
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
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, _isEip155Enabled).TestObject;
        Block block = Build.A.Block.WithNumber(1).WithTransactions(tx).WithGasLimit(gasLimit).TestObject;

        _transactionProcessor.CallAndRestore(tx, new BlockExecutionContext(block.Header, _specProvider.GetSpec(block.Header)), NullTxTracer.Instance);

        _stateProvider.GetBalance(TestItem.PrivateKeyA.Address).Should().Be(1.Ether());
    }

    [Test]
    public void Account_is_not_created_on_call_and_restore()
    {
        long gasLimit = 100000;
        Transaction tx = Build.A.Transaction
            .WithValue(0.Ether())
            .WithGasPrice(1)
            .WithGasLimit(gasLimit)
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyD, _isEip155Enabled)
            .TestObject;
        Block block = Build.A.Block.WithNumber(1).WithTransactions(tx).WithGasLimit(gasLimit).TestObject;

        _stateProvider.AccountExists(TestItem.PrivateKeyD.Address).Should().BeFalse();
        _transactionProcessor.CallAndRestore(tx, new BlockExecutionContext(block.Header, _specProvider.GetSpec(block.Header)), NullTxTracer.Instance);
        _stateProvider.AccountExists(TestItem.PrivateKeyD.Address).Should().BeFalse();
    }

    [Test]
    public void Nonce_is_not_changed_on_call_and_restore()
    {
        long gasLimit = 100000;
        Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, _isEip155Enabled).WithValue(1.Ether() - (UInt256)gasLimit).WithGasPrice(1).WithGasLimit(gasLimit).TestObject;
        Block block = Build.A.Block.WithNumber(1).WithTransactions(tx).WithGasLimit(gasLimit).TestObject;

        _transactionProcessor.CallAndRestore(tx, new BlockExecutionContext(block.Header, _specProvider.GetSpec(block.Header)), NullTxTracer.Instance);
        _stateProvider.GetNonce(TestItem.PrivateKeyA.Address).Should().Be(0);
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
            result.Should().Be(TransactionResult.InsufficientSenderBalance);
        }
        else
        {
            tracer.GasSpent.Should().Be(21000);
        }
    }

    [TestCaseSource(nameof(EstimateWithHighTxValueTestCases))]
    public long Should_not_estimate_tx_with_high_value(UInt256 txValue)
    {
        long gasLimit = 100000;

        Transaction tx = Build.A.Transaction
            .WithValue(txValue)
            .WithGasLimit(gasLimit)
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, _isEip155Enabled)
            .TestObject;
        Block block = Build.A.Block.WithNumber(1).WithTransactions(tx).WithGasLimit(gasLimit).TestObject;

        EstimateGasTracer tracer = new();
        BlocksConfig blocksConfig = new();
        GasEstimator estimator = new(_transactionProcessor, _stateProvider, _specProvider, blocksConfig);

        long estimate = estimator.Estimate(tx, block.Header, tracer, out string? err, 0);

        if (txValue + (UInt256)gasLimit > AccountBalance)
        {
            Assert.That(err, Is.Not.Null); // Should have error
            Assert.That(err, Is.EqualTo("Transaction execution fails"));
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
            { TestName = "Exactly account balance", ExpectedResult = 0L };

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
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, _isEip155Enabled)
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
        Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, _isEip155Enabled).WithGasLimit(gasLimit).TestObject;
        Block block = Build.A.Block.WithParent(_baseBlock).WithTransactions(tx).WithGasLimit(gasLimit).TestObject;

        EstimateGasTracer tracer = new();
        BlocksConfig blocksConfig = new();
        GasEstimator estimator = new(_transactionProcessor, _stateProvider, _specProvider, blocksConfig);

        _transactionProcessor.CallAndRestore(tx, new BlockExecutionContext(block.Header, _specProvider.GetSpec(block.Header)), tracer);

        tracer.GasSpent.Should().Be(21000);
        estimator.Estimate(tx, block.Header, tracer, out string? err, 0).Should().Be(21000);
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

        Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, _isEip155Enabled).WithCode(initByteCode).WithGasLimit(gasLimit).TestObject;
        Block block = Build.A.Block.WithNumber(MainnetSpecProvider.MuirGlacierBlockNumber).WithTransactions(tx).WithGasLimit(2 * gasLimit).TestObject;

        EthereumIntrinsicGas intrinsicGas = IntrinsicGasCalculator.Calculate(tx, MuirGlacier.Instance);

        GethLikeTxMemoryTracer gethTracer = new(tx, GethTraceOptions.Default);
        var blkCtx = new BlockExecutionContext(block.Header, _specProvider.GetSpec(block.Header));
        _transactionProcessor.CallAndRestore(tx, blkCtx, gethTracer);
        TestContext.Out.WriteLine(new EthereumJsonSerializer().Serialize(gethTracer.BuildResult(), true));

        EstimateGasTracer tracer = new();
        _transactionProcessor.CallAndRestore(tx, blkCtx, tracer);

        BlocksConfig blocksConfig = new();
        GasEstimator estimator = new(_transactionProcessor, _stateProvider, _specProvider, blocksConfig);

        long actualIntrinsic = tx.GasLimit - tracer.IntrinsicGasAt;
        actualIntrinsic.Should().Be(intrinsicGas.Standard);
        IReleaseSpec releaseSpec = Berlin.Instance;
        tracer.CalculateAdditionalGasRequired(tx, releaseSpec).Should().Be(RefundOf.SSetReversedEip2200 + GasCostOf.CallStipend - GasCostOf.SStoreNetMeteredEip2200 + 1);
        tracer.GasSpent.Should().Be(54764L);
        long estimate = estimator.Estimate(tx, block.Header, tracer, out string? err, 0);
        estimate.Should().Be(75465L);
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

        Transaction initTx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, _isEip155Enabled).WithCode(initByteCode).WithGasLimit(gasLimit).TestObject;
        Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, _isEip155Enabled).WithCode(byteCode).WithGasLimit(gasLimit).WithNonce(1).TestObject;
        Block block = Build.A.Block.WithNumber(MainnetSpecProvider.MuirGlacierBlockNumber).WithTransactions(tx).WithGasLimit(2 * gasLimit).TestObject;

        IReleaseSpec releaseSpec = MuirGlacier.Instance;
        EthereumIntrinsicGas intrinsicGas = IntrinsicGasCalculator.Calculate(tx, releaseSpec);
        var blkCtx = new BlockExecutionContext(block.Header, releaseSpec);
        _transactionProcessor.Execute(initTx, blkCtx, NullTxTracer.Instance);

        EstimateGasTracer tracer = new();
        GethLikeTxMemoryTracer gethTracer = new(tx, GethTraceOptions.Default);
        _transactionProcessor.CallAndRestore(tx, blkCtx, tracer);
        _transactionProcessor.CallAndRestore(tx, blkCtx, gethTracer);
        TestContext.Out.WriteLine(new EthereumJsonSerializer().Serialize(gethTracer.BuildResult(), true));

        BlocksConfig blocksConfig = new();
        GasEstimator estimator = new(_transactionProcessor, _stateProvider, _specProvider, blocksConfig);

        long actualIntrinsic = tx.GasLimit - tracer.IntrinsicGasAt;
        actualIntrinsic.Should().Be(intrinsicGas.Standard);
        tracer.CalculateAdditionalGasRequired(tx, releaseSpec).Should().Be(24080);
        tracer.GasSpent.Should().Be(35228L);
        long estimate = estimator.Estimate(tx, block.Header, tracer, out string? err, 0);
        estimate.Should().Be(54225);
        Assert.That(err, Is.Null);

        ConfirmEnoughEstimate(tx, block, estimate);
    }

    private void ConfirmEnoughEstimate(Transaction tx, Block block, long estimate)
    {
        var blkCtx = new BlockExecutionContext(block.Header, _specProvider.GetSpec(block.Header));

        CallOutputTracer outputTracer = new();
        tx.GasLimit = estimate;
        _transactionProcessor.CallAndRestore(tx, blkCtx, outputTracer);
        outputTracer.StatusCode.Should().Be(StatusCode.Success,
            $"transaction should succeed at the estimate ({estimate})");

        outputTracer = new CallOutputTracer();
        tx.GasLimit = Math.Min(estimate - 1, estimate * 63 / 64);
        _transactionProcessor.CallAndRestore(tx, blkCtx, outputTracer);
        outputTracer.StatusCode.Should().Be(StatusCode.Failure,
            $"transaction should fail below the estimate ({tx.GasLimit})");
    }

    [TestCase]
    public void Can_estimate_with_stipend()
    {
        byte[] initByteCode = Prepare.EvmCode
            .CallWithValue(Address.Zero, 0, 1)
            .Op(Instruction.STOP)
            .Done;

        long gasLimit = 100000;

        Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, _isEip155Enabled).WithCode(initByteCode).WithGasLimit(gasLimit).TestObject;
        Block block = Build.A.Block.WithNumber(MainnetSpecProvider.MuirGlacierBlockNumber).WithTransactions(tx).WithGasLimit(2 * gasLimit).TestObject;

        IReleaseSpec releaseSpec = MuirGlacier.Instance;
        EthereumIntrinsicGas intrinsicGas = IntrinsicGasCalculator.Calculate(tx, releaseSpec);

        GethLikeTxMemoryTracer gethTracer = new(tx, GethTraceOptions.Default);
        var blkCtx = new BlockExecutionContext(block.Header, releaseSpec);
        _transactionProcessor.CallAndRestore(tx, blkCtx, gethTracer);
        TestContext.Out.WriteLine(new EthereumJsonSerializer().Serialize(gethTracer.BuildResult(), true));

        EstimateGasTracer tracer = new();
        _transactionProcessor.CallAndRestore(tx, blkCtx, tracer);

        BlocksConfig blocksConfig = new();
        GasEstimator estimator = new(_transactionProcessor, _stateProvider, _specProvider, blocksConfig);

        long actualIntrinsic = tx.GasLimit - tracer.IntrinsicGasAt;
        actualIntrinsic.Should().Be(intrinsicGas.Standard);
        tracer.CalculateAdditionalGasRequired(tx, releaseSpec).Should().Be(2300);
        tracer.GasSpent.Should().Be(85669L);
        long estimate = estimator.Estimate(tx, block.Header, tracer, out string? err, 0);
        estimate.Should().Be(87969L);
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

        Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, _isEip155Enabled).WithCode(initByteCode).WithGasLimit(gasLimit).TestObject;
        Block block = Build.A.Block.WithNumber(MainnetSpecProvider.MuirGlacierBlockNumber).WithTransactions(tx).WithGasLimit(2 * gasLimit).TestObject;

        IReleaseSpec releaseSpec = _specProvider.GetSpec(block.Header);
        EthereumIntrinsicGas intrinsicGas = IntrinsicGasCalculator.Calculate(tx, releaseSpec);

        GethLikeTxMemoryTracer gethTracer = new(tx, GethTraceOptions.Default);
        var blkCtx = new BlockExecutionContext(block.Header, releaseSpec);
        _transactionProcessor.CallAndRestore(tx, blkCtx, gethTracer);
        TestContext.Out.WriteLine(new EthereumJsonSerializer().Serialize(gethTracer.BuildResult(), true));

        EstimateGasTracer tracer = new();
        _transactionProcessor.CallAndRestore(tx, blkCtx, tracer);

        BlocksConfig blocksConfig = new();
        GasEstimator estimator = new(_transactionProcessor, _stateProvider, _specProvider, blocksConfig);

        long actualIntrinsic = tx.GasLimit - tracer.IntrinsicGasAt;
        actualIntrinsic.Should().Be(intrinsicGas.Standard);
        tracer.CalculateAdditionalGasRequired(tx, releaseSpec).Should().Be(RefundOf.SSetReversedEip2200 + GasCostOf.CallStipend);
        tracer.GasSpent.Should().Be(87429L);
        long estimate = estimator.Estimate(tx, block.Header, tracer, out string? err, 0);
        estimate.Should().Be(108130L);
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

        Transaction initTx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, _isEip155Enabled).WithCode(initByteCode).WithGasLimit(gasLimit).TestObject;
        Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, _isEip155Enabled).WithCode(byteCode).WithGasLimit(gasLimit).WithNonce(1).TestObject;
        Block block = Build.A.Block.WithNumber(MainnetSpecProvider.MuirGlacierBlockNumber).WithTransactions(tx).WithGasLimit(2 * gasLimit).TestObject;

        IReleaseSpec releaseSpec = _specProvider.GetSpec(block.Header);
        EthereumIntrinsicGas intrinsicGas = IntrinsicGasCalculator.Calculate(tx, releaseSpec);

        var blkCtx = new BlockExecutionContext(block.Header, releaseSpec);
        _transactionProcessor.Execute(initTx, blkCtx, NullTxTracer.Instance);

        EstimateGasTracer tracer = new();
        _transactionProcessor.CallAndRestore(tx, blkCtx, tracer);

        BlocksConfig blocksConfig = new();
        GasEstimator estimator = new(_transactionProcessor, _stateProvider, _specProvider, blocksConfig);

        long actualIntrinsic = tx.GasLimit - tracer.IntrinsicGasAt;
        actualIntrinsic.Should().Be(intrinsicGas.Standard);
        tracer.CalculateAdditionalGasRequired(tx, releaseSpec).Should().Be(1);
        tracer.GasSpent.Should().Be(54224L);
        long estimate = estimator.Estimate(tx, block.Header, tracer, out string? err, 0);
        estimate.Should().Be(54224L);
        Assert.That(err, Is.Null);

        ConfirmEnoughEstimate(tx, block, estimate);
    }

    [TestCase]
    public void Disables_Eip158_for_system_transactions()
    {
        long blockNumber = MainnetSpecProvider.SpuriousDragonBlockNumber + 1;

        _stateProvider.CreateAccount(TestItem.PrivateKeyA.Address, 0.Ether());
        IReleaseSpec spec = _specProvider.GetSpec((ForkActivation)blockNumber);
        _stateProvider.Commit(spec);
        Transaction tx = Build.A.SystemTransaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, _isEip155Enabled)
            .WithGasPrice(0)
            .WithValue(0)
            .TestObject;

        Block block = Build.A.Block.WithNumber(blockNumber).WithTransactions(tx).TestObject;

        BlockReceiptsTracer tracer = BuildTracer(block, tx, false, false);
        Execute(tx, block, tracer);
        _stateProvider.AccountExists(tx.SenderAddress!).Should().BeTrue();
    }

    [TestCase]
    public void Balance_is_changed_on_buildup_and_restored()
    {
        long gasLimit = 100000;
        Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, _isEip155Enabled).WithValue(0).WithGasPrice(1).WithGasLimit(gasLimit).TestObject;
        Block block = Build.A.Block.WithNumber(MainnetSpecProvider.ByzantiumBlockNumber).WithTransactions(tx).WithGasLimit(gasLimit).TestObject;

        Snapshot state = _stateProvider.TakeSnapshot();
        _transactionProcessor.BuildUp(tx, new BlockExecutionContext(block.Header, _specProvider.GetSpec(block.Header)), NullTxTracer.Instance);
        _stateProvider.GetBalance(TestItem.PrivateKeyA.Address).Should().Be(AccountBalance - GasCostOf.Transaction);

        _stateProvider.Restore(state);
        _stateProvider.GetBalance(TestItem.PrivateKeyA.Address).Should().Be(AccountBalance);
    }

    [TestCase]
    public void Account_is_not_created_on_buildup_and_restore()
    {
        long gasLimit = 100000;
        Transaction tx = Build.A.Transaction
            .WithValue(0.Ether())
            .WithGasPrice(1)
            .WithGasLimit(gasLimit)
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyD, _isEip155Enabled)
            .TestObject;
        Block block = Build.A.Block.WithNumber(MainnetSpecProvider.ByzantiumBlockNumber).WithTransactions(tx).WithGasLimit(gasLimit).TestObject;

        _stateProvider.AccountExists(TestItem.PrivateKeyD.Address).Should().BeFalse();
        Snapshot state = _stateProvider.TakeSnapshot();
        _transactionProcessor.BuildUp(tx, new BlockExecutionContext(block.Header, _specProvider.GetSpec(block.Header)), NullTxTracer.Instance);
        _stateProvider.AccountExists(TestItem.PrivateKeyD.Address).Should().BeTrue();
        _stateProvider.Restore(state);
        _stateProvider.AccountExists(TestItem.PrivateKeyD.Address).Should().BeFalse();
    }

    [TestCase]
    public void Nonce_is_not_changed_on_buildup_and_restore()
    {
        long gasLimit = 100000;
        Transaction tx = Build.A.Transaction
            .WithValue(AccountBalance - (UInt256)gasLimit)
            .WithGasPrice(1)
            .WithGasLimit(gasLimit)
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, _isEip155Enabled)
            .TestObject;
        Block block = Build.A.Block.WithNumber(MainnetSpecProvider.ByzantiumBlockNumber).WithTransactions(tx).WithGasLimit(gasLimit).TestObject;

        Snapshot state = _stateProvider.TakeSnapshot();
        _transactionProcessor.BuildUp(tx, new BlockExecutionContext(block.Header, _specProvider.GetSpec(block.Header)), NullTxTracer.Instance);
        _stateProvider.GetNonce(TestItem.PrivateKeyA.Address).Should().Be(1);
        _stateProvider.Restore(state);
        _stateProvider.GetNonce(TestItem.PrivateKeyA.Address).Should().Be(0);
    }

    [TestCase]
    public void State_changed_twice_in_buildup_should_have_correct_gas_cost()
    {
        long gasLimit = 100000;
        Transaction tx1 = Build.A.Transaction
            .WithValue(0).WithGasPrice(1).WithGasLimit(GasCostOf.Transaction)
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, _isEip155Enabled)
            .TestObject;
        Transaction tx2 = Build.A.Transaction
            .WithValue(0).WithNonce(1).WithGasPrice(1).WithGasLimit(GasCostOf.Transaction)
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, _isEip155Enabled)
            .TestObject;
        Block block = Build.A.Block.WithNumber(MainnetSpecProvider.ByzantiumBlockNumber).WithTransactions(tx1, tx2).WithGasLimit(gasLimit).TestObject;

        Snapshot state = _stateProvider.TakeSnapshot();
        var blkCtx = new BlockExecutionContext(block.Header, _specProvider.GetSpec(block.Header));
        _transactionProcessor.BuildUp(tx1, blkCtx, NullTxTracer.Instance);
        _stateProvider.GetBalance(TestItem.PrivateKeyA.Address).Should().Be(AccountBalance - GasCostOf.Transaction);

        _transactionProcessor.BuildUp(tx2, blkCtx, NullTxTracer.Instance);
        _stateProvider.GetBalance(TestItem.PrivateKeyA.Address).Should().Be(AccountBalance - GasCostOf.Transaction * 2);

        _stateProvider.Restore(state);
        _stateProvider.GetBalance(TestItem.PrivateKeyA.Address).Should().Be(AccountBalance);
    }

    private BlockReceiptsTracer BuildTracer(Block block, Transaction tx, bool stateDiff, bool trace)
    {
        ParityTraceTypes types = ParityTraceTypes.None;
        if (stateDiff)
        {
            types = types | ParityTraceTypes.StateDiff;
        }

        if (trace)
        {
            types = types | ParityTraceTypes.Trace;
        }

        IBlockTracer otherTracer = types != ParityTraceTypes.None ? new ParityLikeBlockTracer(tx.Hash!, ParityTraceTypes.Trace | ParityTraceTypes.StateDiff) : (IBlockTracer)NullBlockTracer.Instance;
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

    private TransactionResult CallAndRestore(Transaction tx, Block block, BlockReceiptsTracer? tracer = null)
    {
        tracer?.StartNewBlockTrace(block);
        tracer?.StartNewTxTrace(tx);
        TransactionResult result = _transactionProcessor.CallAndRestore(tx, new BlockExecutionContext(block.Header, _specProvider.GetSpec(block.Header)), tracer ?? NullTxTracer.Instance);
        if (result)
        {
            tracer?.EndTxTrace();
            tracer?.EndBlockTrace();
        }

        return result;
    }

    // Direct transfer path tests

    private static readonly UInt256 HalfEther = 1.Ether() / 2;
    private static readonly UInt256 TenthEther = 1.Ether() / 10;
    private static readonly UInt256 HundredthEther = 1.Ether() / 100;
    private static readonly UInt256 FifthEther = 1.Ether() / 5;

    // value, recipientKey ("A"=self, "B"=existing, "D"=non-existent), gasLimit
    [TestCase(500_000_000_000_000_000L, "B", 100000L, TestName = "Direct_transfer_basic_ether")]
    [TestCase(100_000_000_000_000_000L, "A", 100000L, TestName = "Direct_transfer_self")]
    [TestCase(100_000_000_000_000_000L, "D", 100000L, TestName = "Direct_transfer_non_existent_recipient")]
    [TestCase(0L, "B", 100000L, TestName = "Direct_transfer_zero_value")]
    [TestCase(999_999_999_999_979_000L, "B", 21000L, TestName = "Direct_transfer_exact_balance")]
    [TestCase(100_000_000_000_000_000L, "B", 100000L, TestName = "Direct_transfer_refund_unused_gas")]
    public void Direct_transfer_success(long valueWei, string recipientKey, long gasLimit)
    {
        UInt256 value = (UInt256)valueWei;
        Address recipient = recipientKey switch
        {
            "A" => TestItem.AddressA,
            "B" => TestItem.AddressB,
            "D" => TestItem.AddressD,
            _ => throw new ArgumentOutOfRangeException(nameof(recipientKey))
        };

        Transaction tx = Build.A.Transaction
            .WithValue(value)
            .WithGasPrice(1)
            .WithGasLimit(gasLimit)
            .WithTo(recipient)
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, _isEip155Enabled)
            .TestObject;
        Block block = Build.A.Block.WithNumber(MainnetSpecProvider.ByzantiumBlockNumber).WithTransactions(tx).TestObject;

        TransactionResult result = Execute(tx, block);

        result.Should().Be(TransactionResult.Ok);
        UInt256 gasCost = (UInt256)GasCostOf.Transaction;
        bool isSelf = recipient == TestItem.AddressA;
        _stateProvider.GetBalance(TestItem.AddressA).Should().Be(
            isSelf ? AccountBalance - gasCost : AccountBalance - value - gasCost);
        if (!isSelf)
            _stateProvider.GetBalance(recipient).Should().Be(value);
        _stateProvider.GetNonce(TestItem.AddressA).Should().Be(1);
    }

    [Test]
    public void Direct_transfer_sender_is_beneficiary()
    {
        long gasLimit = 100000;
        Transaction tx = Build.A.Transaction
            .WithValue(TenthEther)
            .WithGasPrice(1)
            .WithGasLimit(gasLimit)
            .WithTo(TestItem.AddressB)
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, _isEip155Enabled)
            .TestObject;
        // Block beneficiary is the sender
        Block block = Build.A.Block
            .WithNumber(MainnetSpecProvider.ByzantiumBlockNumber)
            .WithBeneficiary(TestItem.AddressA)
            .WithTransactions(tx)
            .TestObject;

        TransactionResult result = Execute(tx, block);

        result.Should().Be(TransactionResult.Ok);
        // Sender pays gas + value, but gets the gas fee back as beneficiary
        // Net: only value is deducted (gas paid = gas received as fee)
        _stateProvider.GetBalance(TestItem.AddressA).Should().Be(AccountBalance - TenthEther);
        _stateProvider.GetBalance(TestItem.AddressB).Should().Be(TenthEther);
        _stateProvider.GetNonce(TestItem.AddressA).Should().Be(1);
    }

    [Test]
    public void Direct_transfer_recipient_is_beneficiary()
    {
        long gasLimit = 100000;

        // Create recipient account first
        _stateProvider.CreateAccount(TestItem.AddressB, 0);
        _stateProvider.Commit(_specProvider.GenesisSpec);

        Transaction tx = Build.A.Transaction
            .WithValue(TenthEther)
            .WithGasPrice(1)
            .WithGasLimit(gasLimit)
            .WithTo(TestItem.AddressB)
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, _isEip155Enabled)
            .TestObject;
        // Block beneficiary is the recipient
        Block block = Build.A.Block
            .WithNumber(MainnetSpecProvider.ByzantiumBlockNumber)
            .WithBeneficiary(TestItem.AddressB)
            .WithTransactions(tx)
            .TestObject;

        TransactionResult result = Execute(tx, block);

        result.Should().Be(TransactionResult.Ok);
        // Recipient gets value + gas fee
        _stateProvider.GetBalance(TestItem.AddressB).Should().Be(TenthEther + (UInt256)GasCostOf.Transaction);
        _stateProvider.GetBalance(TestItem.AddressA).Should().Be(AccountBalance - TenthEther - (UInt256)GasCostOf.Transaction);
    }

    [Test]
    public void Direct_transfer_sequential_nonce_increments()
    {
        long gasLimit = 100000;

        Transaction tx1 = Build.A.Transaction
            .WithValue(HundredthEther)
            .WithGasPrice(1)
            .WithGasLimit(gasLimit)
            .WithNonce(0)
            .WithTo(TestItem.AddressB)
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, _isEip155Enabled)
            .TestObject;
        Transaction tx2 = Build.A.Transaction
            .WithValue(HundredthEther)
            .WithGasPrice(1)
            .WithGasLimit(gasLimit)
            .WithNonce(1)
            .WithTo(TestItem.AddressB)
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, _isEip155Enabled)
            .TestObject;
        Block block = Build.A.Block.WithNumber(MainnetSpecProvider.ByzantiumBlockNumber).WithTransactions(tx1, tx2).TestObject;

        TransactionResult result1 = Execute(tx1, block);
        TransactionResult result2 = Execute(tx2, block);

        result1.Should().Be(TransactionResult.Ok);
        result2.Should().Be(TransactionResult.Ok);
        _stateProvider.GetNonce(TestItem.AddressA).Should().Be(2);
        _stateProvider.GetBalance(TestItem.AddressB).Should().Be(HundredthEther * 2);
        _stateProvider.GetBalance(TestItem.AddressA).Should().Be(AccountBalance - HundredthEther * 2 - (UInt256)GasCostOf.Transaction * 2);
    }

    [Test]
    public void Direct_transfer_eip1559_with_base_fee()
    {
        long gasLimit = 100000;
        UInt256 maxPriorityFee = 2.GWei();
        UInt256 maxFeePerGas = 10.GWei();

        Transaction tx = Build.A.Transaction
            .WithValue(TenthEther)
            .WithMaxPriorityFeePerGas(maxPriorityFee)
            .WithMaxFeePerGas(maxFeePerGas)
            .WithType(TxType.EIP1559)
            .WithGasLimit(gasLimit)
            .WithTo(TestItem.AddressB)
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, _isEip155Enabled)
            .TestObject;

        UInt256 baseFee = 1.GWei();
        Block block = Build.A.Block
            .WithNumber(MainnetSpecProvider.LondonBlockNumber)
            .WithBaseFeePerGas(baseFee)
            .WithTransactions(tx)
            .TestObject;

        TransactionResult result = Execute(tx, block);

        result.Should().Be(TransactionResult.Ok);
        // effectiveGasPrice = min(maxFeePerGas, baseFee + maxPriorityFee) = min(10 gwei, 1+2=3 gwei) = 3 gwei
        UInt256 effectiveGasPrice = 3.GWei();
        UInt256 gasCost = effectiveGasPrice * (UInt256)GasCostOf.Transaction;
        _stateProvider.GetBalance(TestItem.AddressA).Should().Be(AccountBalance - TenthEther - gasCost);
        _stateProvider.GetBalance(TestItem.AddressB).Should().Be(TenthEther);
        _stateProvider.GetNonce(TestItem.AddressA).Should().Be(1);
    }

    // nonce, valueWei, gasLimit
    [TestCase(5, 100_000_000_000_000_000L, 100000L, TestName = "Direct_transfer_wrong_nonce")]
    [TestCase(0, 2_000_000_000_000_000_000L, 100000L, TestName = "Direct_transfer_insufficient_balance")]
    [TestCase(0, 999_999_999_999_979_001L, 21000L, TestName = "Direct_transfer_insufficient_gas")]
    public void Direct_transfer_failure(int nonce, long valueWei, long gasLimit)
    {
        Transaction tx = Build.A.Transaction
            .WithValue((UInt256)valueWei)
            .WithGasPrice(1)
            .WithGasLimit(gasLimit)
            .WithNonce((UInt256)nonce)
            .WithTo(TestItem.AddressB)
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, _isEip155Enabled)
            .TestObject;
        Block block = Build.A.Block.WithNumber(MainnetSpecProvider.ByzantiumBlockNumber).WithTransactions(tx).TestObject;

        TransactionResult result = Execute(tx, block);

        result.Should().NotBe(TransactionResult.Ok);
        _stateProvider.GetBalance(TestItem.AddressA).Should().Be(AccountBalance);
    }

    [Test]
    public void Direct_transfer_receipt_tracing_marks_success()
    {
        Transaction tx = Build.A.Transaction
            .WithValue(TenthEther)
            .WithGasPrice(1)
            .WithGasLimit(100000)
            .WithTo(TestItem.AddressB)
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, _isEip155Enabled)
            .TestObject;
        Block block = Build.A.Block.WithNumber(MainnetSpecProvider.ByzantiumBlockNumber).WithTransactions(tx).TestObject;

        BlockReceiptsTracer tracer = BuildTracer(block, tx, false, false);
        TransactionResult result = Execute(tx, block, tracer);

        result.Should().Be(TransactionResult.Ok);
        tracer.LastReceipt.StatusCode.Should().Be(StatusCode.Success);
        tracer.LastReceipt.GasUsed.Should().Be(GasCostOf.Transaction);
    }

    [Test]
    public void Direct_transfer_gas_used_tracked_on_header()
    {
        Transaction tx = Build.A.Transaction
            .WithValue(TenthEther)
            .WithGasPrice(1)
            .WithGasLimit(100000)
            .WithTo(TestItem.AddressB)
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, _isEip155Enabled)
            .TestObject;
        Block block = Build.A.Block.WithNumber(MainnetSpecProvider.ByzantiumBlockNumber).WithTransactions(tx).TestObject;

        long gasUsedBefore = block.Header.GasUsed;

        Execute(tx, block);

        block.Header.GasUsed.Should().Be(gasUsedBefore + GasCostOf.Transaction);
    }

    [Test]
    public void Direct_transfer_falls_back_for_state_tracing()
    {
        Transaction tx = Build.A.Transaction
            .WithValue(TenthEther)
            .WithGasPrice(1)
            .WithGasLimit(100000)
            .WithTo(TestItem.AddressB)
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, _isEip155Enabled)
            .TestObject;
        Block block = Build.A.Block.WithNumber(MainnetSpecProvider.ByzantiumBlockNumber).WithTransactions(tx).TestObject;

        // Use state-diffing tracer (IsTracingState=true) – should still succeed via slow path
        BlockReceiptsTracer tracer = BuildTracer(block, tx, true, false);
        TransactionResult result = Execute(tx, block, tracer);

        result.Should().Be(TransactionResult.Ok);
        _stateProvider.GetBalance(TestItem.AddressA).Should().Be(AccountBalance - TenthEther - (UInt256)GasCostOf.Transaction);
        _stateProvider.GetBalance(TestItem.AddressB).Should().Be(TenthEther);
    }

    [Test]
    public void Direct_transfer_not_taken_for_contract_creation()
    {
        byte[] initCode = [0x60, 0x00]; // PUSH1 0x00
        Transaction tx = Build.A.Transaction
            .WithCode(initCode)
            .WithGasPrice(1)
            .WithGasLimit(100000)
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, _isEip155Enabled)
            .TestObject;
        Block block = Build.A.Block.WithNumber(MainnetSpecProvider.ByzantiumBlockNumber).WithTransactions(tx).TestObject;

        // Contract creation goes through VM path, not direct path
        TransactionResult result = Execute(tx, block);

        result.Should().Be(TransactionResult.Ok);
        _stateProvider.GetNonce(TestItem.AddressA).Should().Be(1);
    }

    [Test]
    public void Direct_transfer_not_taken_for_recipient_with_code()
    {
        // Deploy code at recipient address
        _stateProvider.CreateAccount(TestItem.AddressB, 0);
        IReleaseSpec spec = _specProvider.GetSpec((ForkActivation)MainnetSpecProvider.ByzantiumBlockNumber);
        _stateProvider.InsertCode(TestItem.AddressB, Keccak.Compute([0x60, 0x00]), new byte[] { 0x60, 0x00 }, spec);
        _stateProvider.Commit(spec);

        Transaction tx = Build.A.Transaction
            .WithValue(TenthEther)
            .WithGasPrice(1)
            .WithGasLimit(100000)
            .WithTo(TestItem.AddressB)
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, _isEip155Enabled)
            .TestObject;
        Block block = Build.A.Block.WithNumber(MainnetSpecProvider.ByzantiumBlockNumber).WithTransactions(tx).TestObject;

        // Should still succeed (through VM path), verifying it handles contracts
        TransactionResult result = Execute(tx, block);

        result.Should().Be(TransactionResult.Ok);
        _stateProvider.GetBalance(TestItem.AddressA).Should().BeLessThan(AccountBalance);
    }

    [Test]
    public void Direct_transfer_beneficiary_fee_to_non_existent_account()
    {
        Transaction tx = Build.A.Transaction
            .WithValue(TenthEther)
            .WithGasPrice(1)
            .WithGasLimit(100000)
            .WithTo(TestItem.AddressB)
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, _isEip155Enabled)
            .TestObject;
        // Block beneficiary is a new address (not in state)
        Block block = Build.A.Block
            .WithNumber(MainnetSpecProvider.ByzantiumBlockNumber)
            .WithBeneficiary(TestItem.AddressC)
            .WithTransactions(tx)
            .TestObject;

        TransactionResult result = Execute(tx, block);

        result.Should().Be(TransactionResult.Ok);
        // Beneficiary should receive gas fee
        _stateProvider.GetBalance(TestItem.AddressC).Should().Be((UInt256)GasCostOf.Transaction);
        _stateProvider.GetBalance(TestItem.AddressB).Should().Be(TenthEther);
    }

    [Test]
    public void Direct_transfer_multiple_txs_same_block_correct_state()
    {
        long gasLimit = 100000;

        Transaction tx1 = Build.A.Transaction
            .WithValue(TenthEther)
            .WithGasPrice(1)
            .WithGasLimit(gasLimit)
            .WithNonce(0)
            .WithTo(TestItem.AddressB)
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, _isEip155Enabled)
            .TestObject;
        Transaction tx2 = Build.A.Transaction
            .WithValue(FifthEther)
            .WithGasPrice(1)
            .WithGasLimit(gasLimit)
            .WithNonce(1)
            .WithTo(TestItem.AddressC)
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, _isEip155Enabled)
            .TestObject;
        Block block = Build.A.Block.WithNumber(MainnetSpecProvider.ByzantiumBlockNumber).WithTransactions(tx1, tx2).TestObject;

        Execute(tx1, block).Should().Be(TransactionResult.Ok);
        Execute(tx2, block).Should().Be(TransactionResult.Ok);

        UInt256 totalGas = (UInt256)GasCostOf.Transaction * 2;
        _stateProvider.GetBalance(TestItem.AddressA).Should().Be(AccountBalance - TenthEther - FifthEther - totalGas);
        _stateProvider.GetBalance(TestItem.AddressB).Should().Be(TenthEther);
        _stateProvider.GetBalance(TestItem.AddressC).Should().Be(FifthEther);
        _stateProvider.GetNonce(TestItem.AddressA).Should().Be(2);
    }

    [Test]
    public void Direct_transfer_eip1559_insufficient_max_fee_fails()
    {
        Transaction tx = Build.A.Transaction
            .WithValue(TenthEther)
            .WithMaxPriorityFeePerGas(5.GWei())
            .WithMaxFeePerGas(10.Ether())
            .WithType(TxType.EIP1559)
            .WithGasLimit(100000)
            .WithTo(TestItem.AddressB)
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, _isEip155Enabled)
            .TestObject;
        Block block = Build.A.Block
            .WithNumber(MainnetSpecProvider.LondonBlockNumber)
            .WithTransactions(tx)
            .TestObject;

        TransactionResult result = Execute(tx, block);

        result.Should().NotBe(TransactionResult.Ok);
        _stateProvider.GetBalance(TestItem.AddressA).Should().Be(AccountBalance);
    }

    [Test]
    public void Direct_transfer_spent_gas_set_on_transaction()
    {
        long gasLimit = 100000;
        Transaction tx = Build.A.Transaction
            .WithValue(TenthEther)
            .WithGasPrice(1)
            .WithGasLimit(gasLimit)
            .WithTo(TestItem.AddressB)
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, _isEip155Enabled)
            .TestObject;
        Block block = Build.A.Block.WithNumber(MainnetSpecProvider.ByzantiumBlockNumber).WithTransactions(tx).TestObject;

        Execute(tx, block);

        tx.SpentGas.Should().Be(GasCostOf.Transaction);
    }

    [Test]
    public void Direct_transfer_then_contract_call_nonce_correct()
    {
        // Regression test: direct path must not poison _intraTxCache so that a
        // subsequent slow-path transaction from the same sender reads the correct nonce.
        byte[] code = Prepare.EvmCode.Op(Instruction.STOP).Done;

        // Deploy a simple contract so the next tx goes through the VM (slow path)
        _stateProvider.CreateAccount(TestItem.AddressC, 0);
        IReleaseSpec spec = _specProvider.GetSpec((ForkActivation)MainnetSpecProvider.ByzantiumBlockNumber);
        _stateProvider.InsertCode(TestItem.AddressC, Keccak.Compute(code), code, spec);
        _stateProvider.Commit(spec);

        long gasLimit = 100000;

        // tx1: plain ether transfer → direct path
        Transaction tx1 = Build.A.Transaction
            .WithValue(HundredthEther)
            .WithGasPrice(1)
            .WithGasLimit(gasLimit)
            .WithNonce(0)
            .WithTo(TestItem.AddressB)
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, _isEip155Enabled)
            .TestObject;

        // tx2: call to contract → slow path (VM)
        Transaction tx2 = Build.A.Transaction
            .WithValue(0)
            .WithGasPrice(1)
            .WithGasLimit(gasLimit)
            .WithNonce(1)
            .WithTo(TestItem.AddressC)
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, _isEip155Enabled)
            .TestObject;

        Block block = Build.A.Block.WithNumber(MainnetSpecProvider.ByzantiumBlockNumber).WithTransactions(tx1, tx2).TestObject;

        TransactionResult result1 = Execute(tx1, block);
        TransactionResult result2 = Execute(tx2, block);

        result1.Should().Be(TransactionResult.Ok);
        result2.Should().Be(TransactionResult.Ok);
        _stateProvider.GetNonce(TestItem.AddressA).Should().Be(2);
    }

    [Test]
    public void Direct_transfer_eip158_deletes_empty_beneficiary_touched_with_zero_fee()
    {
        // Regression: slow path always calls AddToBalanceAndCreateIfNotExists(beneficiary, fee).
        // When fee=0, this touches the beneficiary; EIP-158 then deletes it if empty.
        // The direct path must do the same.
        Address beneficiary = TestItem.AddressD;

        // Create the beneficiary as an empty account (balance=0, nonce=0, no code).
        // Commit with GenesisSpec (Frontier) to avoid EIP-158 cleanup during setup.
        _stateProvider.CreateAccount(beneficiary, 0);
        _stateProvider.Commit(_specProvider.GenesisSpec);

        // gasPrice=0 → premiumPerGas=0 → beneficiaryFee=0
        Block block = Build.A.Block
            .WithNumber(MainnetSpecProvider.ByzantiumBlockNumber)
            .WithBeneficiary(beneficiary)
            .TestObject;

        Transaction tx = Build.A.Transaction
            .WithValue(HundredthEther)
            .WithGasPrice(0)
            .WithGasLimit(21000)
            .WithNonce(0)
            .WithTo(TestItem.AddressB)
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, _isEip155Enabled)
            .TestObject;

        TransactionResult result = Execute(tx, block);
        result.Should().Be(TransactionResult.Ok);

        // EIP-158: the beneficiary was empty, got touched with 0-fee → should be deleted
        _stateProvider.AccountExists(beneficiary).Should().BeFalse(
            "empty beneficiary should be deleted by EIP-158 after 0-fee transfer");
    }

    [Test]
    public void Direct_transfer_eip158_deletes_empty_recipient_on_zero_value()
    {
        // Regression: the VM always calls AddToBalanceAndCreateIfNotExists(recipient, value)
        // which touches the recipient even for 0-value transfers. EIP-158 deletes empty accounts.
        Address recipient = TestItem.AddressD;

        // Create the recipient as an empty account.
        // Commit with GenesisSpec (Frontier) to avoid EIP-158 cleanup during setup.
        _stateProvider.CreateAccount(recipient, 0);
        _stateProvider.Commit(_specProvider.GenesisSpec);

        Transaction tx = Build.A.Transaction
            .WithValue(0)
            .WithGasPrice(1)
            .WithGasLimit(21000)
            .WithNonce(0)
            .WithTo(recipient)
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, _isEip155Enabled)
            .TestObject;

        Block block = Build.A.Block
            .WithNumber(MainnetSpecProvider.ByzantiumBlockNumber)
            .TestObject;

        TransactionResult result = Execute(tx, block);
        result.Should().Be(TransactionResult.Ok);

        // EIP-158: the recipient was empty and got touched by the 0-value transfer → should be deleted
        _stateProvider.AccountExists(recipient).Should().BeFalse(
            "empty recipient should be deleted by EIP-158 after 0-value transfer");
    }

    [Test]
    public void Warmup_does_not_update_SpentGas()
    {
        Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, _isEip155Enabled)
            .WithGasLimit(100000).TestObject;
        Block block = Build.A.Block.WithNumber(1).WithTransactions(tx).TestObject;

        // Use a sentinel value because the SpentGas getter returns GasLimit when _spentGas is 0
        const long sentinel = 42;
        tx.SpentGas = sentinel;

        _transactionProcessor.Warmup(tx, new BlockExecutionContext(block.Header, _specProvider.GetSpec(block.Header)), NullTxTracer.Instance);

        tx.SpentGas.Should().Be(sentinel, "Warmup must not modify tx.SpentGas");
    }

    [Test]
    public void Warmup_does_not_modify_sender_nonce()
    {
        Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, _isEip155Enabled)
            .WithGasLimit(100000).TestObject;
        Block block = Build.A.Block.WithNumber(1).WithTransactions(tx).TestObject;

        UInt256 nonceBefore = _stateProvider.GetNonce(TestItem.AddressA);

        _transactionProcessor.Warmup(tx, new BlockExecutionContext(block.Header, _specProvider.GetSpec(block.Header)), NullTxTracer.Instance);

        _stateProvider.GetNonce(TestItem.AddressA).Should().Be(nonceBefore, "Warmup must not increment sender nonce");
    }

    [Test]
    public void Warmup_does_not_deduct_sender_balance()
    {
        Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, _isEip155Enabled)
            .WithGasLimit(100000).TestObject;
        Block block = Build.A.Block.WithNumber(1).WithTransactions(tx).TestObject;

        UInt256 balanceBefore = _stateProvider.GetBalance(TestItem.AddressA);

        _transactionProcessor.Warmup(tx, new BlockExecutionContext(block.Header, _specProvider.GetSpec(block.Header)), NullTxTracer.Instance);

        _stateProvider.GetBalance(TestItem.AddressA).Should().Be(balanceBefore, "Warmup must not deduct sender balance (should use SystemTransactionProcessor path)");
    }
}
