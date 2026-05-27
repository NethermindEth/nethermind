// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.Precompiles;
using Nethermind.Evm.Tracing;
using Nethermind.Blockchain.Tracing.ParityStyle;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.Evm.State;
using NUnit.Framework;
using Nethermind.Config;
using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core.Test;

namespace Nethermind.Evm.Test;

[TestFixture(true)]
[TestFixture(false)]
[Todo(Improve.Refactor, "Check why fixture test cases did not work")]
[Parallelizable(ParallelScope.All)]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public class TransactionProcessorTests(bool eip155Enabled)
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
            Assert.That(result, Is.EqualTo(TransactionResult.InsufficientSenderBalance));
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

    [TestCaseSource(nameof(SimpleTransferFastPathCases))]
    public void Simple_transfer_fast_path_predicate_enters_vm_when_expected(SimpleTransferFastPathCase testCase)
    {
        Address recipient = testCase.Kind == SimpleTransferRecipientKind.Precompile
            ? IdentityPrecompile.Address
            : Address.FromNumber((UInt256)(uint)(1100 + (int)testCase.Kind));
        IReleaseSpec spec = Prague.Instance;
        PrepareSimpleTransferRecipient(testCase.Kind, recipient, spec);
        _stateProvider.Commit(spec);
        _stateProvider.CommitTree(0);

        (CountingVirtualMachine virtualMachine, EthereumTransactionProcessor transactionProcessor) = CreateProcessor(_specProvider);

        Transaction tx = BuildSimpleTransfer(recipient, testCase.Value, testCase.WithAuthorizationList);
        Block block = BuildPragueBlock(tx);

        UInt256 senderBalanceBefore = _stateProvider.GetBalance(TestItem.AddressA);
        UInt256 recipientBalanceBefore = _stateProvider.GetBalance(recipient);

        TransactionResult result = transactionProcessor.Execute(tx, new BlockExecutionContext(block.Header, spec), NullTxTracer.Instance);

        Assert.That(result.TransactionExecuted, Is.True);
        Assert.That(virtualMachine.ExecuteTransactionCalls, Is.EqualTo(testCase.ExpectedVmCalls));
        Assert.That(_stateProvider.GetNonce(TestItem.AddressA), Is.EqualTo((UInt256)1));
        Assert.That(tx.SpentGas, Is.EqualTo(testCase.ExpectedSpentGas));
        Assert.That(_stateProvider.GetBalance(TestItem.AddressA), Is.EqualTo(senderBalanceBefore - testCase.ExpectedSenderDebit));
        Assert.That(_stateProvider.GetBalance(recipient), Is.EqualTo(recipientBalanceBefore + testCase.Value));
    }

    [Test]
    public void Simple_transfer_fast_path_reports_action_trace()
    {
        Address recipient = Address.FromNumber((UInt256)1400);
        IReleaseSpec spec = Prague.Instance;
        PrepareSimpleTransferRecipient(SimpleTransferRecipientKind.Empty, recipient, spec);
        _stateProvider.Commit(spec);
        _stateProvider.CommitTree(0);

        (CountingVirtualMachine virtualMachine, EthereumTransactionProcessor transactionProcessor) = CreateProcessor(_specProvider);

        Transaction tx = BuildSimpleTransfer(recipient, 7.Wei, withAuthorizationList: false);
        Block block = BuildPragueBlock(tx);
        SimpleTransferActionTracer tracer = new();

        TransactionResult result = transactionProcessor.Execute(tx, new BlockExecutionContext(block.Header, spec), tracer);

        Assert.That(result.TransactionExecuted, Is.True);
        Assert.That(virtualMachine.ExecuteTransactionCalls, Is.EqualTo(0));
        Assert.That(tracer.ActionCalls, Is.EqualTo(1));
        Assert.That(tracer.ActionEndCalls, Is.EqualTo(1));
        Assert.That(tracer.ActionGas, Is.EqualTo(tx.GasLimit - GasCostOf.Transaction));
        Assert.That(tracer.ActionEndGas, Is.EqualTo(tx.GasLimit - GasCostOf.Transaction));
        Assert.That(tracer.ActionValue, Is.EqualTo((UInt256)7.Wei));
        Assert.That(tracer.ActionFrom, Is.EqualTo(TestItem.AddressA));
        Assert.That(tracer.ActionTo, Is.EqualTo(recipient));
        Assert.That(tracer.ActionInput, Is.Empty);
        Assert.That(tracer.ActionType, Is.EqualTo(ExecutionType.TRANSACTION));
        Assert.That(tracer.IsPrecompileCall, Is.False);
        Assert.That(tracer.ActionOutput, Is.Empty);
    }

    [Test]
    public void Simple_transfer_fast_path_restores_state_on_call_and_restore()
    {
        Address recipient = Address.FromNumber((UInt256)1401);
        IReleaseSpec spec = Prague.Instance;
        _stateProvider.Commit(spec);
        _stateProvider.CommitTree(0);

        (CountingVirtualMachine virtualMachine, EthereumTransactionProcessor transactionProcessor) = CreateProcessor(_specProvider);

        Transaction tx = BuildSimpleTransfer(recipient, 7.Wei, withAuthorizationList: false);
        Block block = BuildPragueBlock(tx);
        UInt256 senderBalanceBefore = _stateProvider.GetBalance(TestItem.AddressA);
        UInt256 senderNonceBefore = _stateProvider.GetNonce(TestItem.AddressA);

        TransactionResult result = transactionProcessor.CallAndRestore(tx, new BlockExecutionContext(block.Header, spec), NullTxTracer.Instance);

        Assert.That(result.TransactionExecuted, Is.True);
        Assert.That(virtualMachine.ExecuteTransactionCalls, Is.EqualTo(0));
        Assert.That(_stateProvider.GetBalance(TestItem.AddressA), Is.EqualTo(senderBalanceBefore));
        Assert.That(_stateProvider.GetNonce(TestItem.AddressA), Is.EqualTo(senderNonceBefore));
        Assert.That(_stateProvider.AccountExists(recipient), Is.False);
    }

    [TestCase(false, 1ul, true, true)]
    [TestCase(false, 1ul, false, true)]
    [TestCase(false, 0ul, true, false)]
    [TestCase(true, 1ul, true, false)]
    public void Simple_transfer_fast_path_reports_eip7708_log_only_for_non_zero_transfer_to_different_account(
        bool senderIsRecipient,
        ulong value,
        bool isTracingLogs,
        bool expectTransferLog)
    {
        Address recipient = senderIsRecipient ? TestItem.AddressA : Address.FromNumber((UInt256)1300);
        IReleaseSpec spec = Amsterdam.Instance;
        ISpecProvider specProvider = new TestSpecProvider(spec);
        _stateProvider.Commit(spec);
        _stateProvider.CommitTree(0);

        (CountingVirtualMachine virtualMachine, EthereumTransactionProcessor transactionProcessor) = CreateProcessor(specProvider);

        Transaction tx = BuildSimpleTransfer(recipient, (UInt256)value, withAuthorizationList: false);
        Block block = Build.A.Block.WithNumber(1).WithTransactions(tx).WithGasLimit(1_000_000).TestObject;
        SimpleTransferLogTracer tracer = new(isTracingLogs);

        TransactionResult result = transactionProcessor.Execute(tx, new BlockExecutionContext(block.Header, spec), tracer);

        Assert.That(result.TransactionExecuted, Is.True);
        Assert.That(virtualMachine.ExecuteTransactionCalls, Is.EqualTo(0));
        Assert.That(tracer.ReceiptLogs, Has.Length.EqualTo(expectTransferLog ? 1 : 0));
        Assert.That(tracer.ReportLogCalls, Is.EqualTo(expectTransferLog && isTracingLogs ? 1 : 0));
        if (expectTransferLog)
        {
            AssertLog(tracer.ReceiptLogs[0], ExpectedTransferLog(TestItem.AddressA, recipient, value));
            if (isTracingLogs)
            {
                AssertLog(tracer.ReportedLogs[0], ExpectedTransferLog(TestItem.AddressA, recipient, value));
            }
        }
    }

    private (CountingVirtualMachine Vm, EthereumTransactionProcessor Processor) CreateProcessor(ISpecProvider specProvider)
    {
        CountingVirtualMachine vm = new(new TestBlockhashProvider(specProvider), specProvider, LimboLogs.Instance);
        EthereumCodeInfoRepository codeInfoRepository = new(_stateProvider);
        EthereumTransactionProcessor processor = new(
            BlobBaseFeeCalculator.Instance,
            specProvider,
            _stateProvider,
            vm,
            codeInfoRepository,
            LimboLogs.Instance);
        return (vm, processor);
    }

    private static Block BuildPragueBlock(Transaction tx) =>
        Build.A.Block
            .WithNumber(MainnetSpecProvider.PragueActivation.BlockNumber)
            .WithTimestamp(MainnetSpecProvider.PragueBlockTimestamp)
            .WithTransactions(tx)
            .WithGasLimit(1_000_000)
            .TestObject;

    public static IEnumerable<TestCaseData> SimpleTransferFastPathCases()
    {
        yield return new TestCaseData(new SimpleTransferFastPathCase(SimpleTransferRecipientKind.Empty, 1.Wei, false, 0, GasCostOf.Transaction, 1.Wei + GasCostOf.Transaction))
            .SetName("Empty-code recipient with value uses fast path");
        yield return new TestCaseData(new SimpleTransferFastPathCase(SimpleTransferRecipientKind.Empty, UInt256.Zero, false, 0, GasCostOf.Transaction, GasCostOf.Transaction))
            .SetName("Empty-code recipient with zero value uses fast path");
        yield return new TestCaseData(new SimpleTransferFastPathCase(SimpleTransferRecipientKind.Contract, 1.Wei, false, 1, GasCostOf.Transaction, 1.Wei + GasCostOf.Transaction))
            .SetName("Contract recipient enters VM");
        yield return new TestCaseData(new SimpleTransferFastPathCase(SimpleTransferRecipientKind.Precompile, 1.Wei, false, 1, GasCostOf.Transaction + 15, 1.Wei + GasCostOf.Transaction + 15))
            .SetName("Precompile recipient enters VM");
        yield return new TestCaseData(new SimpleTransferFastPathCase(SimpleTransferRecipientKind.Empty, 1.Wei, true, 1, GasCostOf.Transaction + 25_000, 1.Wei))
            .SetName("Authorization-list transaction enters VM");
        yield return new TestCaseData(new SimpleTransferFastPathCase(SimpleTransferRecipientKind.DelegatedToContract, 1.Wei, false, 1, GasCostOf.Transaction, 1.Wei + GasCostOf.Transaction))
            .SetName("Delegated recipient with executable target enters VM");
    }

    private Transaction BuildSimpleTransfer(Address recipient, UInt256 value, bool withAuthorizationList)
    {
        TransactionBuilder<Transaction> builder = Build.A.Transaction
            .WithTo(recipient)
            .WithValue(value)
            .WithGasPrice(1)
            .WithGasLimit(100_000);

        if (withAuthorizationList)
        {
            builder
                .WithType(TxType.SetCode)
                .WithAuthorizationCode(_ethereumEcdsa.Sign(TestItem.PrivateKeyB, _specProvider.ChainId, Address.Zero, 0));
        }

        return builder
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, eip155Enabled)
            .TestObject;
    }

    private void PrepareSimpleTransferRecipient(SimpleTransferRecipientKind kind, Address recipient, IReleaseSpec spec)
    {
        if (kind == SimpleTransferRecipientKind.Precompile)
        {
            return;
        }

        _stateProvider.CreateAccount(recipient, UInt256.Zero);
        switch (kind)
        {
            case SimpleTransferRecipientKind.Empty:
                CodeInfoRepository.InsertCode(_stateProvider, Array.Empty<byte>(), recipient, spec, out _);
                break;
            case SimpleTransferRecipientKind.Contract:
                CodeInfoRepository.InsertCode(_stateProvider, new byte[] { (byte)Instruction.STOP }, recipient, spec, out _);
                break;
            case SimpleTransferRecipientKind.DelegatedToContract:
                Address codeSource = Address.FromNumber(1200);
                _stateProvider.CreateAccount(codeSource, UInt256.Zero);
                CodeInfoRepository.InsertCode(_stateProvider, new byte[] { (byte)Instruction.STOP }, codeSource, spec, out _);
                CodeInfoRepository.SetDelegation(_stateProvider, codeSource, recipient, spec, out _, out _);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }
    }

    public readonly record struct SimpleTransferFastPathCase(
        SimpleTransferRecipientKind Kind,
        UInt256 Value,
        bool WithAuthorizationList,
        int ExpectedVmCalls,
        long ExpectedSpentGas,
        UInt256 ExpectedSenderDebit);

    public enum SimpleTransferRecipientKind
    {
        Empty,
        Contract,
        Precompile,
        DelegatedToContract
    }

    private static LogEntry ExpectedTransferLog(Address sender, Address recipient, UInt256 value) =>
        new(TransferLog.Sender, value.ToBigEndian(), [TransferLog.TransferSignature, sender.ToHash().ToHash256(), recipient.ToHash().ToHash256()]);

    private static void AssertLog(LogEntry actual, LogEntry expected)
    {
        Assert.That(actual.Address, Is.EqualTo(expected.Address));
        Assert.That(actual.Data, Is.EqualTo(expected.Data));
        Assert.That(actual.Topics, Is.EqualTo(expected.Topics));
    }

    private sealed class SimpleTransferLogTracer(bool isTracingLogs) : TxTracer
    {
        public override bool IsTracingReceipt { get; protected set; } = true;
        public override bool IsTracingLogs { get; protected set; } = isTracingLogs;
        public LogEntry[] ReceiptLogs { get; private set; } = [];
        public List<LogEntry> ReportedLogs { get; } = [];
        public int ReportLogCalls => ReportedLogs.Count;

        public override void MarkAsSuccess(Address recipient, in GasConsumed gasSpent, byte[] output, LogEntry[] logs, Hash256? stateRoot = null) =>
            ReceiptLogs = logs;

        public override void ReportLog(LogEntry log) => ReportedLogs.Add(log);
    }

    private sealed class SimpleTransferActionTracer : TxTracer
    {
        public override bool IsTracingActions { get; protected set; } = true;
        public int ActionCalls { get; private set; }
        public int ActionEndCalls { get; private set; }
        public long ActionGas { get; private set; }
        public long ActionEndGas { get; private set; }
        public UInt256 ActionValue { get; private set; }
        public Address ActionFrom { get; private set; } = Address.Zero;
        public Address ActionTo { get; private set; } = Address.Zero;
        public byte[] ActionInput { get; private set; } = [];
        public ExecutionType ActionType { get; private set; }
        public bool IsPrecompileCall { get; private set; }
        public byte[] ActionOutput { get; private set; } = [];

        public override void ReportAction(long gas, UInt256 value, Address from, Address to, ReadOnlyMemory<byte> input, ExecutionType callType, bool isPrecompileCall = false)
        {
            ActionCalls++;
            ActionGas = gas;
            ActionValue = value;
            ActionFrom = from;
            ActionTo = to;
            ActionInput = input.ToArray();
            ActionType = callType;
            IsPrecompileCall = isPrecompileCall;
        }

        public override void ReportActionEnd(long gas, ReadOnlyMemory<byte> output)
        {
            ActionEndCalls++;
            ActionEndGas = gas;
            ActionOutput = output.ToArray();
        }
    }

    private sealed class CountingVirtualMachine(
        IBlockhashProvider blockHashProvider,
        ISpecProvider specProvider,
        ILogManager logManager)
        : VirtualMachine<EthereumGasPolicy>(blockHashProvider, specProvider, logManager), IVirtualMachine
    {
        public int ExecuteTransactionCalls { get; private set; }

        public override TransactionSubstate ExecuteTransaction<TTracingInst>(
            VmState<EthereumGasPolicy> vmState,
            IWorldState worldState,
            ITxTracer txTracer)
        {
            ExecuteTransactionCalls++;
            return base.ExecuteTransaction<TTracingInst>(vmState, worldState, txTracer);
        }
    }
}
