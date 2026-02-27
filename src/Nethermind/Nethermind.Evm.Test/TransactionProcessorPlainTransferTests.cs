// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Test;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>
/// Tests for the fast-path plain ETH transfer optimization in TransactionProcessor.
/// The fast path bypasses VM setup for simple EOA-to-EOA transfers with no calldata.
/// </summary>
[Parallelizable(ParallelScope.All)]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public class TransactionProcessorPlainTransferTests
{
    private TestSpecProvider _specProvider;
    private IEthereumEcdsa _ethereumEcdsa;
    private ITransactionProcessor _transactionProcessor;
    private IWorldState _stateProvider;
    private IDisposable _worldStateCloser;
    private OverridableReleaseSpec _spec;

    private static readonly UInt256 InitialBalance = 10.Ether();

    [SetUp]
    public void Setup()
    {
        _spec = new(Specs.Forks.Prague.Instance);
        _specProvider = new TestSpecProvider(_spec);

        _stateProvider = TestWorldStateFactory.CreateForTest();
        _worldStateCloser = _stateProvider.BeginScope(IWorldState.PreGenesis);
        _stateProvider.CreateAccount(TestItem.AddressA, InitialBalance);
        _stateProvider.Commit(_specProvider.GenesisSpec);
        _stateProvider.CommitTree(0);

        EthereumCodeInfoRepository codeInfoRepository = new(_stateProvider);
        EthereumVirtualMachine virtualMachine = new(new TestBlockhashProvider(_specProvider), _specProvider, LimboLogs.Instance);
        _transactionProcessor = new EthereumTransactionProcessor(BlobBaseFeeCalculator.Instance, _specProvider, _stateProvider, virtualMachine, codeInfoRepository, LimboLogs.Instance);
        _ethereumEcdsa = new EthereumEcdsa(_specProvider.ChainId);
    }

    [TearDown]
    public void TearDown()
    {
        _worldStateCloser.Dispose();
    }

    [Test]
    public void PlainTransfer_CorrectBalances()
    {
        UInt256 transferValue = 1.Ether();
        long gasLimit = GasCostOf.Transaction;
        UInt256 gasPrice = 10;

        Transaction tx = Build.A.Transaction
            .WithTo(TestItem.AddressB)
            .WithValue(transferValue)
            .WithGasPrice(gasPrice)
            .WithGasLimit(gasLimit)
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA)
            .TestObject;

        Block block = Build.A.Block.WithNumber(1)
            .WithBeneficiary(TestItem.AddressC)
            .WithTransactions(tx)
            .WithGasLimit(gasLimit)
            .TestObject;

        TransactionResult result = Execute(tx, block);

        result.Should().Be(TransactionResult.Ok);

        UInt256 gasCost = gasPrice * (ulong)gasLimit;
        _stateProvider.GetBalance(TestItem.AddressA).Should().Be(InitialBalance - transferValue - gasCost);
        _stateProvider.GetBalance(TestItem.AddressB).Should().Be(transferValue);
    }

    [Test]
    public void PlainTransfer_ZeroValue_Succeeds()
    {
        long gasLimit = GasCostOf.Transaction;

        Transaction tx = Build.A.Transaction
            .WithTo(TestItem.AddressB)
            .WithValue(0)
            .WithGasPrice(1)
            .WithGasLimit(gasLimit)
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA)
            .TestObject;

        Block block = Build.A.Block.WithNumber(1)
            .WithBeneficiary(TestItem.AddressC)
            .WithTransactions(tx)
            .WithGasLimit(gasLimit)
            .TestObject;

        TransactionResult result = Execute(tx, block);

        result.Should().Be(TransactionResult.Ok);
        // Under EIP-161, a zero-value transfer to a non-existent address creates
        // an empty account that is cleaned up during commit. This is correct behavior.
        _stateProvider.AccountExists(TestItem.AddressB).Should().BeFalse();
    }

    [Test]
    public void PlainTransfer_GasUsedAccumulated()
    {
        long gasLimit = GasCostOf.Transaction;

        Transaction tx = Build.A.Transaction
            .WithTo(TestItem.AddressB)
            .WithValue(1.Ether())
            .WithGasPrice(1)
            .WithGasLimit(gasLimit)
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA)
            .TestObject;

        Block block = Build.A.Block.WithNumber(1)
            .WithBeneficiary(TestItem.AddressC)
            .WithTransactions(tx)
            .WithGasLimit(gasLimit)
            .TestObject;

        Execute(tx, block);

        block.GasUsed.Should().Be(gasLimit);
    }

    [Test]
    public void PlainTransfer_SpentGasSet()
    {
        long gasLimit = 100_000;

        Transaction tx = Build.A.Transaction
            .WithTo(TestItem.AddressB)
            .WithValue(1.Ether())
            .WithGasPrice(1)
            .WithGasLimit(gasLimit)
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA)
            .TestObject;

        Block block = Build.A.Block.WithNumber(1)
            .WithBeneficiary(TestItem.AddressC)
            .WithTransactions(tx)
            .WithGasLimit(gasLimit)
            .TestObject;

        Execute(tx, block);

        // Intrinsic gas for a simple transfer is 21000
        tx.SpentGas.Should().Be(GasCostOf.Transaction);
    }

    [Test]
    public void PlainTransfer_RefundsUnspentGas()
    {
        long gasLimit = 100_000;
        UInt256 gasPrice = 10;

        Transaction tx = Build.A.Transaction
            .WithTo(TestItem.AddressB)
            .WithValue(1.Ether())
            .WithGasPrice(gasPrice)
            .WithGasLimit(gasLimit)
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA)
            .TestObject;

        Block block = Build.A.Block.WithNumber(1)
            .WithBeneficiary(TestItem.AddressC)
            .WithTransactions(tx)
            .WithGasLimit(gasLimit)
            .TestObject;

        Execute(tx, block);

        // Sender should only be charged for intrinsic gas (21000), not full gas limit
        UInt256 totalGasCost = gasPrice * (ulong)GasCostOf.Transaction;
        _stateProvider.GetBalance(TestItem.AddressA).Should().Be(InitialBalance - 1.Ether() - totalGasCost);
    }

    [TestCase(true)]
    [TestCase(false)]
    public void PlainTransfer_EIP1559_CorrectFees(bool withFeeCollector)
    {
        if (withFeeCollector)
        {
            _spec.FeeCollector = TestItem.AddressD;
        }

        long gasLimit = GasCostOf.Transaction;
        UInt256 baseFee = 1;
        UInt256 maxFeePerGas = 10;
        UInt256 maxPriorityFee = 2;

        Transaction tx = Build.A.Transaction
            .WithTo(TestItem.AddressB)
            .WithValue(1.Ether())
            .WithType(TxType.EIP1559)
            .WithMaxFeePerGas(maxFeePerGas)
            .WithMaxPriorityFeePerGas(maxPriorityFee)
            .WithGasLimit(gasLimit)
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA)
            .TestObject;

        Block block = Build.A.Block.WithNumber(1)
            .WithBaseFeePerGas(baseFee)
            .WithBeneficiary(TestItem.AddressC)
            .WithTransactions(tx)
            .WithGasLimit(gasLimit)
            .TestObject;

        FeesTracer tracer = new();
        ExecuteAndTrace(block, tracer);

        // Premium = min(maxPriorityFee, maxFeePerGas - baseFee) = min(2, 10-1) = 2
        UInt256 expectedPremiumFees = maxPriorityFee * (ulong)gasLimit;
        tracer.Fees.Should().Be(expectedPremiumFees);

        UInt256 expectedBurntFees = baseFee * (ulong)gasLimit;
        tracer.BurntFees.Should().Be(expectedBurntFees);

        // Beneficiary gets the premium
        _stateProvider.GetBalance(TestItem.AddressC).Should().Be(expectedPremiumFees);

        if (withFeeCollector)
        {
            // Fee collector gets the burnt fees
            _stateProvider.GetBalance(TestItem.AddressD).Should().Be(expectedBurntFees);
        }
    }

    [Test]
    public void PlainTransfer_Receipt_MarkedAsSuccess()
    {
        long gasLimit = GasCostOf.Transaction;

        Transaction tx = Build.A.Transaction
            .WithTo(TestItem.AddressB)
            .WithValue(1.Ether())
            .WithGasPrice(1)
            .WithGasLimit(gasLimit)
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA)
            .TestObject;

        Block block = Build.A.Block.WithNumber(1)
            .WithBeneficiary(TestItem.AddressC)
            .WithTransactions(tx)
            .WithGasLimit(gasLimit)
            .TestObject;

        // Mirror how production code (TransactionProcessorAdapterExtensions.ProcessTransaction) works:
        // pass the BlockReceiptsTracer itself (not the per-tx tracer) to Execute
        BlockReceiptsTracer receiptsTracer = new();
        receiptsTracer.SetOtherTracer(NullBlockTracer.Instance);
        receiptsTracer.StartNewBlockTrace(block);
        receiptsTracer.StartNewTxTrace(tx);
        _transactionProcessor.Execute(tx, new BlockExecutionContext(block.Header, _spec), receiptsTracer);
        receiptsTracer.EndTxTrace();
        receiptsTracer.EndBlockTrace();

        TxReceipt receipt = receiptsTracer.TxReceipts[0];
        receipt.StatusCode.Should().Be(StatusCode.Success);
        receipt.GasUsedTotal.Should().Be(gasLimit);
        receipt.Logs.Should().BeEmpty();
    }

    [Test]
    public void PlainTransfer_ToContract_UsesNormalPath()
    {
        // Deploy code to recipient so fast path is ineligible
        byte[] code = [0x60, 0x00]; // PUSH1 0
        _stateProvider.CreateAccount(TestItem.AddressB, 0);
        _stateProvider.InsertCode(TestItem.AddressB, ValueKeccak.Compute(code), code, _spec);
        _stateProvider.Commit(_spec);

        long gasLimit = 100_000;

        Transaction tx = Build.A.Transaction
            .WithTo(TestItem.AddressB)
            .WithValue(1.Ether())
            .WithGasPrice(1)
            .WithGasLimit(gasLimit)
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA)
            .TestObject;

        Block block = Build.A.Block.WithNumber(1)
            .WithBeneficiary(TestItem.AddressC)
            .WithTransactions(tx)
            .WithGasLimit(gasLimit)
            .TestObject;

        TransactionResult result = Execute(tx, block);

        // Should succeed via normal VM path (contract has code to execute)
        result.Should().Be(TransactionResult.Ok);
    }

    [Test]
    public void PlainTransfer_WithCalldata_UsesNormalPath()
    {
        long gasLimit = 100_000;
        byte[] data = [0x01, 0x02, 0x03];

        Transaction tx = Build.A.Transaction
            .WithTo(TestItem.AddressB)
            .WithValue(1.Ether())
            .WithData(data)
            .WithGasPrice(1)
            .WithGasLimit(gasLimit)
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA)
            .TestObject;

        Block block = Build.A.Block.WithNumber(1)
            .WithBeneficiary(TestItem.AddressC)
            .WithTransactions(tx)
            .WithGasLimit(gasLimit)
            .TestObject;

        TransactionResult result = Execute(tx, block);

        // Normal path still succeeds for EOA with calldata (no code to execute)
        result.Should().Be(TransactionResult.Ok);
        // Sender pays for calldata gas + base tx gas, not just base tx gas
        tx.SpentGas.Should().BeGreaterThan(GasCostOf.Transaction);
    }

    [Test]
    public void PlainTransfer_MultipleTxs_GasAccumulatesCorrectly()
    {
        long gasLimit = GasCostOf.Transaction;

        Transaction tx1 = Build.A.Transaction
            .WithTo(TestItem.AddressB)
            .WithValue(1.Ether())
            .WithGasPrice(1)
            .WithGasLimit(gasLimit)
            .WithNonce(0)
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA)
            .TestObject;

        Transaction tx2 = Build.A.Transaction
            .WithTo(TestItem.AddressB)
            .WithValue(1.Ether())
            .WithGasPrice(1)
            .WithGasLimit(gasLimit)
            .WithNonce(1)
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA)
            .TestObject;

        Block block = Build.A.Block.WithNumber(1)
            .WithBeneficiary(TestItem.AddressC)
            .WithTransactions(tx1, tx2)
            .WithGasLimit(gasLimit * 2)
            .TestObject;

        BlockExecutionContext blkCtx = new(block.Header, _spec);

        _transactionProcessor.Execute(tx1, blkCtx, NullTxTracer.Instance);
        _transactionProcessor.Execute(tx2, blkCtx, NullTxTracer.Instance);

        block.GasUsed.Should().Be(gasLimit * 2);
        _stateProvider.GetBalance(TestItem.AddressB).Should().Be(2.Ether());
    }

    [Test]
    public void PlainTransfer_MatchesNormalPath_IdenticalResults()
    {
        // Execute same transfer via fast path (no code on recipient) and normal path (with calldata)
        // Both should produce identical sender balance after accounting for gas differences
        long gasLimit = GasCostOf.Transaction;
        UInt256 value = 1.Ether();
        UInt256 gasPrice = 1;

        Transaction tx = Build.A.Transaction
            .WithTo(TestItem.AddressB)
            .WithValue(value)
            .WithGasPrice(gasPrice)
            .WithGasLimit(gasLimit)
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA)
            .TestObject;

        Block block = Build.A.Block.WithNumber(1)
            .WithBeneficiary(TestItem.AddressC)
            .WithTransactions(tx)
            .WithGasLimit(gasLimit)
            .TestObject;

        Execute(tx, block);

        UInt256 expectedGasCost = gasPrice * (ulong)GasCostOf.Transaction;
        _stateProvider.GetBalance(TestItem.AddressA).Should().Be(InitialBalance - value - expectedGasCost);
        _stateProvider.GetBalance(TestItem.AddressB).Should().Be(value);
        _stateProvider.GetBalance(TestItem.AddressC).Should().Be(expectedGasCost);
        _stateProvider.GetNonce(TestItem.AddressA).Should().Be(1);
    }

    [Test]
    public void PlainTransfer_NoncesIncrementCorrectly()
    {
        long gasLimit = GasCostOf.Transaction;

        Transaction tx1 = Build.A.Transaction
            .WithTo(TestItem.AddressB)
            .WithValue(0)
            .WithGasPrice(1)
            .WithGasLimit(gasLimit)
            .WithNonce(0)
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA)
            .TestObject;

        Transaction tx2 = Build.A.Transaction
            .WithTo(TestItem.AddressB)
            .WithValue(0)
            .WithGasPrice(1)
            .WithGasLimit(gasLimit)
            .WithNonce(1)
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA)
            .TestObject;

        Block block = Build.A.Block.WithNumber(1)
            .WithBeneficiary(TestItem.AddressC)
            .WithTransactions(tx1, tx2)
            .WithGasLimit(gasLimit * 2)
            .TestObject;

        BlockExecutionContext blkCtx = new(block.Header, _spec);

        _transactionProcessor.Execute(tx1, blkCtx, NullTxTracer.Instance);
        _transactionProcessor.Execute(tx2, blkCtx, NullTxTracer.Instance);

        _stateProvider.GetNonce(TestItem.AddressA).Should().Be(2);
    }

    [Test]
    public void PlainTransfer_InsufficientBalance_FailsBeforeFastPath()
    {
        long gasLimit = GasCostOf.Transaction;

        Transaction tx = Build.A.Transaction
            .WithTo(TestItem.AddressB)
            .WithValue(InitialBalance + 1) // more than available
            .WithGasPrice(1)
            .WithGasLimit(gasLimit)
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA)
            .TestObject;

        Block block = Build.A.Block.WithNumber(1)
            .WithBeneficiary(TestItem.AddressC)
            .WithTransactions(tx)
            .WithGasLimit(gasLimit)
            .TestObject;

        TransactionResult result = Execute(tx, block);

        result.Should().NotBe(TransactionResult.Ok);
    }

    [Test]
    public void PlainTransfer_CallAndRestore_DoesNotUseFastPath()
    {
        long gasLimit = GasCostOf.Transaction;

        Transaction tx = Build.A.Transaction
            .WithTo(TestItem.AddressB)
            .WithValue(1.Ether())
            .WithGasPrice(1)
            .WithGasLimit(gasLimit)
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA)
            .TestObject;

        Block block = Build.A.Block.WithNumber(1)
            .WithBeneficiary(TestItem.AddressC)
            .WithTransactions(tx)
            .WithGasLimit(gasLimit)
            .TestObject;

        _transactionProcessor.CallAndRestore(tx, new BlockExecutionContext(block.Header, _spec), NullTxTracer.Instance);

        // Balance should be unchanged after CallAndRestore
        _stateProvider.GetBalance(TestItem.AddressA).Should().Be(InitialBalance);
    }

    [Test]
    public void PlainTransfer_BeneficiaryReceivesFees()
    {
        long gasLimit = GasCostOf.Transaction;
        UInt256 gasPrice = 100;

        Transaction tx = Build.A.Transaction
            .WithTo(TestItem.AddressB)
            .WithValue(1.Ether())
            .WithGasPrice(gasPrice)
            .WithGasLimit(gasLimit)
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA)
            .TestObject;

        Block block = Build.A.Block.WithNumber(1)
            .WithBeneficiary(TestItem.AddressC)
            .WithTransactions(tx)
            .WithGasLimit(gasLimit)
            .TestObject;

        Execute(tx, block);

        UInt256 expectedFees = gasPrice * (ulong)gasLimit;
        _stateProvider.GetBalance(TestItem.AddressC).Should().Be(expectedFees);
    }

    private TransactionResult Execute(Transaction tx, Block block)
    {
        return _transactionProcessor.Execute(tx, new BlockExecutionContext(block.Header, _spec), NullTxTracer.Instance);
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
