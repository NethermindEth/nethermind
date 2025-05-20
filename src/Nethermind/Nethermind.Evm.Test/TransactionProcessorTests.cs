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
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.Evm.Tracing.ParityStyle;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.Trie.Pruning;
using NUnit.Framework;
using Nethermind.Config;
using System.Collections.Generic;
using Nethermind.Core.Test;

namespace Nethermind.Evm.Test;

[TestFixture(true)]
[TestFixture(false)]
[Todo(Improve.Refactor, "Check why fixture test cases did not work")]
[Parallelizable(ParallelScope.Self)]
public class TransactionProcessorTests
{
    private readonly bool _isEip155Enabled;
    private readonly ISpecProvider _specProvider;
    private IEthereumEcdsa _ethereumEcdsa;
    private TransactionProcessor _transactionProcessor;
    private IWorldState _stateProvider;

    public TransactionProcessorTests(bool eip155Enabled)
    {
        _isEip155Enabled = eip155Enabled;
        _specProvider = MainnetSpecProvider.Instance;
    }

    private static readonly UInt256 AccountBalance = 1.Ether();

    [SetUp]
    public void Setup()
    {
        MemDb stateDb = new();
        TrieStore trieStore = TestTrieStoreFactory.Build(stateDb, LimboLogs.Instance);
        PreBlockCaches preBlockCaches = new();
        _stateProvider = new WorldState(trieStore, new MemDb(), LimboLogs.Instance, preBlockCaches);
        _stateProvider.CreateAccount(TestItem.AddressA, AccountBalance);
        _stateProvider.Commit(_specProvider.GenesisSpec);
        _stateProvider.CommitTree(0);

        CodeInfoRepository codeInfoRepository = new();
        VirtualMachine virtualMachine = new(new TestBlockhashProvider(_specProvider), _specProvider, LimboLogs.Instance);
        _transactionProcessor = new TransactionProcessor(_specProvider, _stateProvider, virtualMachine, codeInfoRepository, LimboLogs.Instance);
        _ethereumEcdsa = new EthereumEcdsa(_specProvider.ChainId);
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
        Assert.That(result.Success, Is.True);
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
        Assert.That(result.Fail, Is.True);
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
        Assert.That(result.Fail, Is.True);
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
        Assert.That(result.Fail, Is.True);
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
        Assert.That(result.Fail, Is.True);
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
        Assert.That(result.Fail, Is.True);
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
        Assert.That(result.Fail, Is.True);
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
        Assert.That(result.Fail, Is.True);
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
        Assert.That(result.Fail, Is.True);
    }

    [TestCase(true, true)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(false, false)]
    public void Will_not_cause_quick_fail_above_block_gas_limit_during_calls(bool withStateDiff, bool withTrace)
    {
        Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, _isEip155Enabled).WithGasLimit(100000).TestObject;
        Block block = Build.A.Block.WithNumber(1).WithTransactions(tx).WithGasLimit(20000).TestObject;
        TransactionResult result = CallAndRestore(tx, block);
        Assert.That(result.Success, Is.True);
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
        Block block = Build.A.Block.WithNumber(1).WithTransactions(tx).WithGasLimit(gasLimit).TestObject;

        EstimateGasTracer tracer = new();
        Action action = () => _transactionProcessor.CallAndRestore(tx, new BlockExecutionContext(block.Header, _specProvider.GetSpec(block.Header)), tracer);
        if (!systemUser)
        {
            action.Should().Throw<InsufficientBalanceException>();
        }
        else
        {
            action.Should().NotThrow();
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
        Assert.That(err, Is.Null);

        return estimate;
    }

    public static IEnumerable<TestCaseData> EstimateWithHighTxValueTestCases
    {
        get
        {
            yield return new TestCaseData((UInt256)1)
            { TestName = "Sanity check", ExpectedResult = GasCostOf.Transaction };
            yield return new TestCaseData(AccountBalance - 1)
            { TestName = "Less than account balance", ExpectedResult = GasCostOf.Transaction };
            yield return new TestCaseData(AccountBalance - GasCostOf.Transaction)
            { TestName = "Account balance - tx cost", ExpectedResult = GasCostOf.Transaction };
            yield return new TestCaseData(AccountBalance - GasCostOf.Transaction + 1)
            { TestName = "More than (account balance - tx cost)", ExpectedResult = GasCostOf.Transaction };
            yield return new TestCaseData(AccountBalance)
            { TestName = "Exactly account balance", ExpectedResult = GasCostOf.Transaction };

            yield return new TestCaseData(AccountBalance + 1)
            { TestName = "More than account balance", ExpectedResult = 0L };
            yield return new TestCaseData(UInt256.MaxValue)
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
        Assert.That(result.Fail, Is.True);
    }

    [TestCase]
    public void Can_estimate_simple()
    {
        long gasLimit = 100000;
        Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, _isEip155Enabled).WithGasLimit(gasLimit).TestObject;
        Block block = Build.A.Block.WithNumber(1).WithTransactions(tx).WithGasLimit(gasLimit).TestObject;

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

        IntrinsicGas intrinsicGas = IntrinsicGasCalculator.Calculate(tx, MuirGlacier.Instance);

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
        IntrinsicGas intrinsicGas = IntrinsicGasCalculator.Calculate(tx, releaseSpec);
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
        estimate.Should().Be(59307);
        Assert.That(err, Is.Null);

        ConfirmEnoughEstimate(tx, block, estimate);
    }

    private void ConfirmEnoughEstimate(Transaction tx, Block block, long estimate)
    {
        CallOutputTracer outputTracer = new();
        tx.GasLimit = estimate;
        TestContext.Out.WriteLine(tx.GasLimit);

        GethLikeTxMemoryTracer gethTracer = new(tx, GethTraceOptions.Default);
        var blkCtx = new BlockExecutionContext(block.Header, _specProvider.GetSpec(block.Header));
        _transactionProcessor.CallAndRestore(tx, blkCtx, gethTracer);
        string traceEnoughGas = new EthereumJsonSerializer().Serialize(gethTracer.BuildResult(), true);

        _transactionProcessor.CallAndRestore(tx, blkCtx, outputTracer);
        traceEnoughGas.Should().NotContain("OutOfGas");

        outputTracer = new CallOutputTracer();
        tx.GasLimit = Math.Min(estimate - 1, estimate * 63 / 64);
        TestContext.Out.WriteLine(tx.GasLimit);

        gethTracer = new GethLikeTxMemoryTracer(tx, GethTraceOptions.Default);
        _transactionProcessor.CallAndRestore(tx, blkCtx, gethTracer);

        string traceOutOfGas = new EthereumJsonSerializer().Serialize(gethTracer.BuildResult(), true);
        TestContext.Out.WriteLine(traceOutOfGas);

        _transactionProcessor.CallAndRestore(tx, blkCtx, outputTracer);

        bool failed = traceEnoughGas.Contains("failed") || traceEnoughGas.Contains("OutOfGas");
        failed.Should().BeTrue();
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
        IntrinsicGas intrinsicGas = IntrinsicGasCalculator.Calculate(tx, releaseSpec);

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

        IReleaseSpec releaseSpec = MuirGlacier.Instance;
        IntrinsicGas intrinsicGas = IntrinsicGasCalculator.Calculate(tx, releaseSpec);

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

        IReleaseSpec releaseSpec = Berlin.Instance;
        IntrinsicGas intrinsicGas = IntrinsicGasCalculator.Calculate(tx, releaseSpec);

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
        _stateProvider.AccountExists(tx.SenderAddress).Should().BeTrue();
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

        IBlockTracer otherTracer = types != ParityTraceTypes.None ? new ParityLikeBlockTracer(tx.Hash, ParityTraceTypes.Trace | ParityTraceTypes.StateDiff) : (IBlockTracer)NullBlockTracer.Instance;
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
}
