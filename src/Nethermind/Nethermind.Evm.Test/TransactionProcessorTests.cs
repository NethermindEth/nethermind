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

    [SetUp]
    public void Setup()
    {
        MemDb stateDb = new();
        TrieStore trieStore = new(stateDb, LimboLogs.Instance);
        _stateProvider = new WorldState(trieStore, new MemDb(), LimboLogs.Instance);
        _stateProvider.CreateAccount(TestItem.AddressA, 1.Ether());
        _stateProvider.Commit(_specProvider.GenesisSpec);
        _stateProvider.CommitTree(0);

        VirtualMachine virtualMachine = new(TestBlockhashProvider.Instance, _specProvider, LimboLogs.Instance);
        _transactionProcessor = new TransactionProcessor(_specProvider, _stateProvider, virtualMachine, LimboLogs.Instance);
        _ethereumEcdsa = new EthereumEcdsa(_specProvider.ChainId, LimboLogs.Instance);
    }

    [TestCase(true, true)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(false, false)]
    public void Can_process_simple_transaction(bool withStateDiff, bool withTrace)
    {
        Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, _isEip155Enabled).WithGasLimit(100000).TestObject;

        Block block = Build.A.Block.WithNumber(1).WithTransactions(tx).TestObject;

        BlockReceiptsTracer tracer = BuildTracer(block, tx, withStateDiff, withTrace);
        Execute(tracer, tx, block);

        Assert.That(tracer.TxReceipts[0].StatusCode, Is.EqualTo(StatusCode.Success));
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
        Execute(tracer, tx, block);

        if (_isEip155Enabled) // we use eip155 check just as a proxy on 658
        {
            Assert.Null(tracer.TxReceipts![0].PostTransactionState);
        }
        else
        {
            Assert.NotNull(tracer.TxReceipts![0].PostTransactionState);
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

        BlockReceiptsTracer tracer = BuildTracer(block, tx, withStateDiff, withTrace);
        Execute(tracer, tx, block);

        Assert.That(tracer.TxReceipts[0].StatusCode, Is.EqualTo(StatusCode.Failure));
    }

    [TestCase(true, true)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(false, false)]
    public void Can_handle_quick_fail_on_missing_sender(bool withStateDiff, bool withTrace)
    {
        Transaction tx = Build.A.Transaction.Signed(_ethereumEcdsa, TestItem.PrivateKeyA, _isEip155Enabled).WithGasLimit(100000).TestObject;

        Block block = Build.A.Block.WithNumber(1).WithTransactions(tx).TestObject;

        BlockReceiptsTracer tracer = BuildTracer(block, tx, withStateDiff, withTrace);
        Execute(tracer, tx, block);

        Assert.That(tracer.TxReceipts[0].StatusCode, Is.EqualTo(StatusCode.Failure));
    }

    [TestCase(true, true)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(false, false)]
    public void Can_handle_quick_fail_on_non_existing_sender_account(bool withStateDiff, bool withTrace)
    {
        Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyB, _isEip155Enabled).WithGasLimit(100000).TestObject;

        Block block = Build.A.Block.WithNumber(1).WithTransactions(tx).TestObject;

        BlockReceiptsTracer tracer = BuildTracer(block, tx, withStateDiff, withTrace);
        Execute(tracer, tx, block);

        Assert.That(tracer.TxReceipts[0].StatusCode, Is.EqualTo(StatusCode.Failure));
    }

    [TestCase(true, true)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(false, false)]
    public void Can_handle_quick_fail_on_invalid_nonce(bool withStateDiff, bool withTrace)
    {
        Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, _isEip155Enabled).WithGasLimit(100000).WithNonce(100).TestObject;

        Block block = Build.A.Block.WithNumber(1).WithTransactions(tx).TestObject;

        BlockReceiptsTracer tracer = BuildTracer(block, tx, withStateDiff, withTrace);
        Execute(tracer, tx, block);

        Assert.That(tracer.TxReceipts[0].StatusCode, Is.EqualTo(StatusCode.Failure));
    }

    [TestCase(true, true)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(false, false)]
    public void Can_handle_quick_fail_on_not_enough_balance_on_intrinsic_gas(bool withStateDiff, bool withTrace)
    {
        AccessListBuilder accessListBuilder = new();
        foreach (Address address in TestItem.Addresses)
        {
            accessListBuilder.AddAddress(address);
        }

        Transaction tx = Build.A.Transaction
            .WithGasLimit(GasCostOf.Transaction * 2)
            .WithAccessList(accessListBuilder.ToAccessList())
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, _isEip155Enabled)
            .TestObject;

        tx.Value = 1.Ether() - 3 * GasCostOf.Transaction;

        Block block = Build.A.Block.WithNumber(MainnetSpecProvider.BerlinBlockNumber).WithTransactions(tx).TestObject;

        BlockReceiptsTracer tracer = BuildTracer(block, tx, withStateDiff, withTrace);
        Execute(tracer, tx, block);

        Assert.That(tracer.TxReceipts[0].StatusCode, Is.EqualTo(StatusCode.Failure));
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

        tx.Value = 1.Ether() - GasCostOf.Transaction;

        Block block = Build.A.Block.WithNumber(MainnetSpecProvider.BerlinBlockNumber).WithTransactions(tx).TestObject;

        BlockReceiptsTracer tracer = BuildTracer(block, tx, withStateDiff, withTrace);
        Execute(tracer, tx, block);

        Assert.That(tracer.TxReceipts[0].StatusCode, Is.EqualTo(StatusCode.Failure));
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

        BlockReceiptsTracer tracer = BuildTracer(block, tx, withStateDiff, withTrace);
        Execute(tracer, tx, block);

        Assert.That(tracer.TxReceipts[0].StatusCode, Is.EqualTo(StatusCode.Failure));
    }

    [TestCase(true, true)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(false, false)]
    public void Can_handle_quick_fail_on_above_block_gas_limit(bool withStateDiff, bool withTrace)
    {
        Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, _isEip155Enabled).WithGasLimit(100000).TestObject;

        Block block = Build.A.Block.WithNumber(1).WithTransactions(tx).WithGasLimit(20000).TestObject;

        BlockReceiptsTracer tracer = BuildTracer(block, tx, withTrace, withTrace);
        Execute(tracer, tx, block);

        Assert.That(tracer.TxReceipts[0].StatusCode, Is.EqualTo(StatusCode.Failure));
    }

    [TestCase(true, true)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(false, false)]
    public void Will_not_cause_quick_fail_above_block_gas_limit_during_calls(bool withStateDiff, bool withTrace)
    {
        Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, _isEip155Enabled).WithGasLimit(100000).TestObject;

        Block block = Build.A.Block.WithNumber(1).WithTransactions(tx).WithGasLimit(20000).TestObject;

        BlockReceiptsTracer tracer = BuildTracer(block, tx, withTrace, withTrace);
        CallAndRestore(tracer, tx, block);

        Assert.That(tracer.TxReceipts[0].StatusCode, Is.EqualTo(StatusCode.Success));
    }

    [TestCase]
    public void Balance_is_not_changed_on_call_and_restore()
    {
        long gasLimit = 100000;
        Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, _isEip155Enabled).WithValue(1.Ether() - (UInt256)gasLimit).WithGasPrice(1).WithGasLimit(gasLimit).TestObject;
        Block block = Build.A.Block.WithNumber(1).WithTransactions(tx).WithGasLimit(gasLimit).TestObject;

        _transactionProcessor.CallAndRestore(tx, block.Header, NullTxTracer.Instance);

        _stateProvider.GetBalance(TestItem.PrivateKeyA.Address).Should().Be(1.Ether());
    }

    [TestCase]
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
        _transactionProcessor.CallAndRestore(tx, block.Header, NullTxTracer.Instance);
        _stateProvider.AccountExists(TestItem.PrivateKeyD.Address).Should().BeFalse();
    }

    [TestCase]
    public void Nonce_is_not_changed_on_call_and_restore()
    {
        long gasLimit = 100000;
        Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, _isEip155Enabled).WithValue(1.Ether() - (UInt256)gasLimit).WithGasPrice(1).WithGasLimit(gasLimit).TestObject;
        Block block = Build.A.Block.WithNumber(1).WithTransactions(tx).WithGasLimit(gasLimit).TestObject;

        _transactionProcessor.CallAndRestore(tx, block.Header, NullTxTracer.Instance);
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
        Action action = () => _transactionProcessor.CallAndRestore(tx, block.Header, tracer);
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

    [Test]
    public void Should_reject_tx_with_high_value()
    {
        Transaction tx = Build.A.Transaction.WithValue(UInt256.MaxValue).WithGasLimit(21000)
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, _isEip155Enabled)
            .TestObject;

        long blockNumber = _isEip155Enabled
            ? MainnetSpecProvider.ByzantiumBlockNumber
            : MainnetSpecProvider.ByzantiumBlockNumber - 1;
        Block block = Build.A.Block.WithNumber(blockNumber).WithTransactions(tx).TestObject;
        BlockReceiptsTracer tracer = BuildTracer(block, tx, true, true);

        Execute(tracer, tx, block);

        tracer.TxReceipts[0].StatusCode.Should().Be(StatusCode.Failure);
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
        BlockReceiptsTracer tracer = BuildTracer(block, tx, true, true);

        Execute(tracer, tx, block);

        tracer.TxReceipts[0].StatusCode.Should().Be(StatusCode.Failure);
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
        _transactionProcessor.CallAndRestore(tx, block.Header, tracer);

        tracer.GasSpent.Should().Be(21000);
        estimator.Estimate(tx, block.Header, tracer).Should().Be(21000);
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

        long intrinsic = IntrinsicGasCalculator.Calculate(tx, MuirGlacier.Instance);

        GethLikeTxMemoryTracer gethTracer = new(GethTraceOptions.Default);
        _transactionProcessor.CallAndRestore(tx, block.Header, gethTracer);
        TestContext.WriteLine(new EthereumJsonSerializer().Serialize(gethTracer.BuildResult(), true));

        EstimateGasTracer tracer = new();
        _transactionProcessor.CallAndRestore(tx, block.Header, tracer);

        BlocksConfig blocksConfig = new();
        GasEstimator estimator = new(_transactionProcessor, _stateProvider, _specProvider, blocksConfig);

        long actualIntrinsic = tx.GasLimit - tracer.IntrinsicGasAt;
        actualIntrinsic.Should().Be(intrinsic);
        IReleaseSpec releaseSpec = Berlin.Instance;
        tracer.CalculateAdditionalGasRequired(tx, releaseSpec).Should().Be(RefundOf.SSetReversedEip2200 + GasCostOf.CallStipend - GasCostOf.SStoreNetMeteredEip2200 + 1);
        tracer.GasSpent.Should().Be(54764L);
        long estimate = estimator.Estimate(tx, block.Header, tracer);
        estimate.Should().Be(75465L);

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
        long intrinsic = IntrinsicGasCalculator.Calculate(tx, releaseSpec);

        _transactionProcessor.Execute(initTx, block.Header, NullTxTracer.Instance);

        EstimateGasTracer tracer = new();
        GethLikeTxMemoryTracer gethTracer = new(GethTraceOptions.Default);
        _transactionProcessor.CallAndRestore(tx, block.Header, tracer);
        _transactionProcessor.CallAndRestore(tx, block.Header, gethTracer);
        TestContext.WriteLine(new EthereumJsonSerializer().Serialize(gethTracer.BuildResult(), true));

        BlocksConfig blocksConfig = new();
        GasEstimator estimator = new(_transactionProcessor, _stateProvider, _specProvider, blocksConfig);

        long actualIntrinsic = tx.GasLimit - tracer.IntrinsicGasAt;
        actualIntrinsic.Should().Be(intrinsic);
        tracer.CalculateAdditionalGasRequired(tx, releaseSpec).Should().Be(24080);
        tracer.GasSpent.Should().Be(35228L);
        long estimate = estimator.Estimate(tx, block.Header, tracer);
        estimate.Should().Be(59307);

        ConfirmEnoughEstimate(tx, block, estimate);
    }


    private void ConfirmEnoughEstimate(Transaction tx, Block block, long estimate)
    {
        CallOutputTracer outputTracer = new();
        tx.GasLimit = estimate;
        TestContext.WriteLine(tx.GasLimit);

        GethLikeTxMemoryTracer gethTracer = new(GethTraceOptions.Default);
        _transactionProcessor.CallAndRestore(tx, block.Header, gethTracer);
        string traceEnoughGas = new EthereumJsonSerializer().Serialize(gethTracer.BuildResult(), true);

        _transactionProcessor.CallAndRestore(tx, block.Header, outputTracer);
        traceEnoughGas.Should().NotContain("OutOfGas");

        outputTracer = new CallOutputTracer();
        tx.GasLimit = Math.Min(estimate - 1, estimate * 63 / 64);
        TestContext.WriteLine(tx.GasLimit);

        gethTracer = new GethLikeTxMemoryTracer(GethTraceOptions.Default);
        _transactionProcessor.CallAndRestore(tx, block.Header, gethTracer);

        string traceOutOfGas = new EthereumJsonSerializer().Serialize(gethTracer.BuildResult(), true);
        TestContext.WriteLine(traceOutOfGas);

        _transactionProcessor.CallAndRestore(tx, block.Header, outputTracer);

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
        long intrinsic = IntrinsicGasCalculator.Calculate(tx, releaseSpec);

        GethLikeTxMemoryTracer gethTracer = new(GethTraceOptions.Default);
        _transactionProcessor.CallAndRestore(tx, block.Header, gethTracer);
        TestContext.WriteLine(new EthereumJsonSerializer().Serialize(gethTracer.BuildResult(), true));

        EstimateGasTracer tracer = new();
        _transactionProcessor.CallAndRestore(tx, block.Header, tracer);

        BlocksConfig blocksConfig = new();
        GasEstimator estimator = new(_transactionProcessor, _stateProvider, _specProvider, blocksConfig);

        long actualIntrinsic = tx.GasLimit - tracer.IntrinsicGasAt;
        actualIntrinsic.Should().Be(intrinsic);
        tracer.CalculateAdditionalGasRequired(tx, releaseSpec).Should().Be(2300);
        tracer.GasSpent.Should().Be(85669L);
        long estimate = estimator.Estimate(tx, block.Header, tracer);
        estimate.Should().Be(87969L);

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
        long intrinsic = IntrinsicGasCalculator.Calculate(tx, releaseSpec);

        GethLikeTxMemoryTracer gethTracer = new(GethTraceOptions.Default);
        _transactionProcessor.CallAndRestore(tx, block.Header, gethTracer);
        TestContext.WriteLine(new EthereumJsonSerializer().Serialize(gethTracer.BuildResult(), true));

        EstimateGasTracer tracer = new();
        _transactionProcessor.CallAndRestore(tx, block.Header, tracer);

        BlocksConfig blocksConfig = new();
        GasEstimator estimator = new(_transactionProcessor, _stateProvider, _specProvider, blocksConfig);

        long actualIntrinsic = tx.GasLimit - tracer.IntrinsicGasAt;
        actualIntrinsic.Should().Be(intrinsic);
        tracer.CalculateAdditionalGasRequired(tx, releaseSpec).Should().Be(RefundOf.SSetReversedEip2200 + GasCostOf.CallStipend);
        tracer.GasSpent.Should().Be(87429L);
        long estimate = estimator.Estimate(tx, block.Header, tracer);
        estimate.Should().Be(108130L);

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
        long intrinsic = IntrinsicGasCalculator.Calculate(tx, releaseSpec);

        _transactionProcessor.Execute(initTx, block.Header, NullTxTracer.Instance);

        EstimateGasTracer tracer = new();
        _transactionProcessor.CallAndRestore(tx, block.Header, tracer);

        BlocksConfig blocksConfig = new();
        GasEstimator estimator = new(_transactionProcessor, _stateProvider, _specProvider, blocksConfig);

        long actualIntrinsic = tx.GasLimit - tracer.IntrinsicGasAt;
        actualIntrinsic.Should().Be(intrinsic);
        tracer.CalculateAdditionalGasRequired(tx, releaseSpec).Should().Be(1);
        tracer.GasSpent.Should().Be(54224L);
        long estimate = estimator.Estimate(tx, block.Header, tracer);
        estimate.Should().Be(54224L);

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
        Execute(tracer, tx, block);
        _stateProvider.AccountExists(tx.SenderAddress).Should().BeTrue();
    }



    [TestCase]
    public void Balance_is_changed_on_buildup_and_restored()
    {
        long gasLimit = 100000;
        Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, _isEip155Enabled).WithValue(0).WithGasPrice(1).WithGasLimit(gasLimit).TestObject;
        Block block = Build.A.Block.WithNumber(MainnetSpecProvider.ByzantiumBlockNumber).WithTransactions(tx).WithGasLimit(gasLimit).TestObject;

        Snapshot state = _stateProvider.TakeSnapshot();
        _transactionProcessor.BuildUp(tx, block.Header, NullTxTracer.Instance);
        _stateProvider.GetBalance(TestItem.PrivateKeyA.Address).Should().Be(1.Ether() - 21000);

        _stateProvider.Restore(state);
        _stateProvider.GetBalance(TestItem.PrivateKeyA.Address).Should().Be(1.Ether());
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
        _transactionProcessor.BuildUp(tx, block.Header, NullTxTracer.Instance);
        _stateProvider.AccountExists(TestItem.PrivateKeyD.Address).Should().BeTrue();
        _stateProvider.Restore(state);
        _stateProvider.AccountExists(TestItem.PrivateKeyD.Address).Should().BeFalse();
    }



    [TestCase]
    public void Nonce_is_not_changed_on_buildup_and_restore()
    {
        long gasLimit = 100000;
        Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, _isEip155Enabled).WithValue(1.Ether() - (UInt256)gasLimit).WithGasPrice(1).WithGasLimit(gasLimit).TestObject;
        Block block = Build.A.Block.WithNumber(MainnetSpecProvider.ByzantiumBlockNumber).WithTransactions(tx).WithGasLimit(gasLimit).TestObject;

        Snapshot state = _stateProvider.TakeSnapshot();
        _transactionProcessor.BuildUp(tx, block.Header, NullTxTracer.Instance);
        _stateProvider.GetNonce(TestItem.PrivateKeyA.Address).Should().Be(1);
        _stateProvider.Restore(state);
        _stateProvider.GetNonce(TestItem.PrivateKeyA.Address).Should().Be(0);
    }



    [TestCase]
    public void State_changed_twice_in_buildup_should_have_correct_gas_cost()
    {
        long gasLimit = 100000;
        Transaction tx1 = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, _isEip155Enabled).WithValue(0).WithGasPrice(1).WithGasLimit(21000).TestObject;
        Transaction tx2 = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, _isEip155Enabled).WithValue(0).WithNonce(1).WithGasPrice(1).WithGasLimit(21000).TestObject;
        Block block = Build.A.Block.WithNumber(MainnetSpecProvider.ByzantiumBlockNumber).WithTransactions(tx1, tx2).WithGasLimit(gasLimit).TestObject;

        Snapshot state = _stateProvider.TakeSnapshot();
        _transactionProcessor.BuildUp(tx1, block.Header, NullTxTracer.Instance);
        _stateProvider.GetBalance(TestItem.PrivateKeyA.Address).Should().Be(1.Ether() - 21000);

        _transactionProcessor.BuildUp(tx2, block.Header, NullTxTracer.Instance);
        _stateProvider.GetBalance(TestItem.PrivateKeyA.Address).Should().Be(1.Ether() - 42000);

        _stateProvider.Restore(state);
        _stateProvider.GetBalance(TestItem.PrivateKeyA.Address).Should().Be(1.Ether());
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

    private void Execute(BlockReceiptsTracer tracer, Transaction tx, Block block)
    {
        tracer.StartNewBlockTrace(block);
        tracer.StartNewTxTrace(tx);
        _transactionProcessor.Execute(tx, block.Header, tracer);
        tracer.EndTxTrace();
        tracer.EndBlockTrace();
    }

    private void CallAndRestore(BlockReceiptsTracer tracer, Transaction tx, Block block)
    {
        tracer.StartNewBlockTrace(block);
        tracer.StartNewTxTrace(tx);
        _transactionProcessor.CallAndRestore(tx, block.Header, tracer);
        tracer.EndTxTrace();
        tracer.EndBlockTrace();
    }
}
