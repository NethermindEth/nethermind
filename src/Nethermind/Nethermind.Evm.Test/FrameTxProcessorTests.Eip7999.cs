// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>EIP-7999 <c>max_fee</c> budgets for frame transactions, on Bogota with EIP-8141 switched on.</summary>
public partial class FrameTxProcessorTests
{
    private const ulong BudgetBaseFee = 7;

    private void UseBogotaFrames(bool eip7999)
    {
        _spec = new OverridableReleaseSpec(Bogota.Instance) { IsEip8141Enabled = true, IsEip7999Enabled = eip7999 };
        _specProvider = new TestSpecProvider(_spec);
        _transactionProcessor = BuildProcessor(_stateProvider, new EthereumCodeInfoRepository(_stateProvider));
    }

    private static Transaction WithMaxFee(Transaction tx, UInt256 maxFee, UInt256 maxPriorityFeePerGas)
    {
        tx.MaxFee = maxFee;
        tx.GasPrice = maxPriorityFeePerGas;
        // As the decoder sets it, so a per-gas reading of the transaction sees the cap it would on the wire.
        tx.DecodedMaxFeePerGas = FrameTxValidation.ImpliedMaxFeePerGas(maxFee, FrameTxValidation.TotalGasLimit(tx.Frames));
        tx.MaxFeePerBlobGas = null;
        return tx;
    }

    private ulong MaxGas(Transaction tx)
    {
        Assert.That(FrameTxValidation.TryCalculateGasBudget(tx, Spec, out _, out _, out ulong maxGas), Is.True);
        return maxGas;
    }

    private TransactionResult ProcessWith(Transaction tx, ExecutionOptions options, UInt256 baseFeePerGas)
    {
        Block block = Build.A.Block.WithNumber(1).WithBaseFeePerGas(baseFeePerGas).WithBeneficiary(Beneficiary)
            .WithTransactions(tx).WithGasLimit(30_000_000).TestObject;
        _transactionProcessor.SetBlockExecutionContext(new BlockExecutionContext(block.Header, Spec));
        return _transactionProcessor.Process(tx, NullTxTracer.Instance, options);
    }

    /// <param name="maxFeePerMaxGas">The budget as a per-gas rate over <c>max_gas</c>, plus <paramref name="extra"/> wei.</param>
    [TestCase(5ul, 8ul, 0ul, TestName = "Execute_MaxFee_UnusedGasHeadroomBacksTheTip")]
    [TestCase(5ul, BudgetBaseFee, 0ul, TestName = "Execute_MaxFee_BudgetExactlyRequired_TipFromUnusedGas")]
    [TestCase(1_000_000ul, BudgetBaseFee, 3ul, TestName = "Execute_MaxFee_TipCappedByBudget")]
    public void Execute_MaxFee_BurnsBaseFeeAndPaysTipWithinBudget(ulong priorityFee, ulong maxFeePerMaxGas, ulong extra)
    {
        UseBogotaFrames(eip7999: true);
        DeploySmartSender(ApproveCode(FrameFlags.ApproveExecutionAndPayment));
        Transaction tx = FrameTx(nonce: 0, SelfVerifyFrame());
        ulong maxGas = MaxGas(tx);
        UInt256 maxFee = (UInt256)maxGas * maxFeePerMaxGas + extra;
        WithMaxFee(tx, maxFee, priorityFee);

        CallOutputTracer output = new();
        FeesTracer fees = new();
        TransactionResult result = Process(tx, baseFeePerGas: BudgetBaseFee, tracer: new CompositeTxTracer(output, fees));

        Assert.That(result.TransactionExecuted, Is.True, result.ErrorDescription);
        UInt256 gasUsed = (UInt256)output.GasSpent;
        UInt256 required = (UInt256)maxGas * BudgetBaseFee;
        UInt256 maxCost = required + UInt256.Min((UInt256)priorityFee * maxGas, maxFee - required);
        UInt256 baseFeePaid = gasUsed * BudgetBaseFee;
        UInt256 tip = UInt256.Min((UInt256)priorityFee * gasUsed, maxCost - baseFeePaid);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(gasUsed, Is.LessThan((UInt256)maxGas), "the case relies on unused gas");
            Assert.That(_stateProvider.GetBalance(Beneficiary), Is.EqualTo(tip), "coinbase gets priority_fee_paid");
            Assert.That(_stateProvider.GetBalance(Sender), Is.EqualTo(1.Ether - baseFeePaid - tip), "payer is refunded the rest of max_cost");
            Assert.That(fees.Fees, Is.EqualTo(tip));
            Assert.That(fees.BurntFees, Is.EqualTo(baseFeePaid));
        }
    }

    [Test]
    public void Execute_MaxFee_TipUsesUnusedHeadroomWherePerGasCapWouldNot([Values] bool eip7999)
    {
        // max_fee_per_gas 8 at base fee 7 caps the per-gas tip at 1; the same money as a budget does not.
        UseBogotaFrames(eip7999);
        DeploySmartSender(ApproveCode(FrameFlags.ApproveExecutionAndPayment));
        Transaction tx = FrameTx(nonce: 0, SelfVerifyFrame());
        tx.GasPrice = 5;
        tx.DecodedMaxFeePerGas = 8;
        if (eip7999) WithMaxFee(tx, (UInt256)MaxGas(tx) * 8, 5);

        CallOutputTracer output = new();
        TransactionResult result = Process(tx, baseFeePerGas: BudgetBaseFee, tracer: output);

        Assert.That(result.TransactionExecuted, Is.True, result.ErrorDescription);
        Assert.That(_stateProvider.GetBalance(Beneficiary), Is.EqualTo((UInt256)output.GasSpent * (eip7999 ? 5ul : 1ul)));
    }

    [Test]
    public void Execute_MaxFee_BelowRequiredMaxFee_Invalid([Values] bool covered)
    {
        UseBogotaFrames(eip7999: true);
        DeploySmartSender(ApproveCode(FrameFlags.ApproveExecutionAndPayment));
        Transaction tx = FrameTx(nonce: 0, SelfVerifyFrame());
        UInt256 required = (UInt256)MaxGas(tx) * BudgetBaseFee;
        WithMaxFee(tx, covered ? required : required - 1, 1);

        TransactionResult result = Process(tx, baseFeePerGas: BudgetBaseFee);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TransactionExecuted, Is.EqualTo(covered), result.ErrorDescription);
            if (!covered)
            {
                Assert.That(result.Error, Is.EqualTo(TransactionResult.ErrorType.MaxFeePerGasBelowBaseFee));
                Assert.That(_stateProvider.GetBalance(Sender), Is.EqualTo(1.Ether));
                Assert.That(_stateProvider.GetNonce(Sender), Is.EqualTo(0UL));
            }
        }
    }

    [Test]
    public void Execute_MaxFee_CoversBlobGasWithoutPerBlobCap([Values] bool covered)
    {
        UseBogotaFrames(eip7999: true);
        DeploySmartSender(ApproveCode(FrameFlags.ApproveExecutionAndPayment));
        Transaction tx = FrameTx(nonce: 0, SelfVerifyFrame());
        tx.BlobVersionedHashes = [new byte[32]];
        const ulong excessBlobGas = 50_000_000;
        UInt256 blobFee = ExpectedBlobFee(excessBlobGas, blobCount: 1);
        Assert.That(FeePerBlobGas(excessBlobGas), Is.GreaterThan(UInt256.One), "a blob base fee no zero per-blob cap would cover");
        UInt256 required = (UInt256)MaxGas(tx) * BudgetBaseFee + blobFee;
        WithMaxFee(tx, covered ? required : required - 1, 0);

        CallOutputTracer output = new();
        TransactionResult result = ProcessWithBlobHeader(tx, excessBlobGas, baseFeePerGas: BudgetBaseFee, tracer: output);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TransactionExecuted, Is.EqualTo(covered), result.ErrorDescription);
            Assert.That(_stateProvider.GetBalance(Sender),
                Is.EqualTo(covered ? 1.Ether - (UInt256)output.GasSpent * BudgetBaseFee - blobFee : 1.Ether));
        }
    }

    [Test]
    public void Execute_FeeShape_MustMatchFork([Values] bool eip7999, [Values] bool maxFeeShape)
    {
        UseBogotaFrames(eip7999);
        DeploySmartSender(ApproveCode(FrameFlags.ApproveExecutionAndPayment));
        Transaction tx = FrameTx(nonce: 0, SelfVerifyFrame());
        if (maxFeeShape) WithMaxFee(tx, 1.Ether, 1);

        TransactionResult result = Process(tx, baseFeePerGas: 1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TransactionExecuted, Is.EqualTo(eip7999 == maxFeeShape), result.ErrorDescription);
            if (eip7999 != maxFeeShape)
            {
                Assert.That(result.Error, Is.EqualTo(TransactionResult.ErrorType.MalformedTransaction));
                Assert.That(result.ErrorDescription, Is.EqualTo(eip7999 ? FrameTxValidation.PerGasFeesNotAllowed : FrameTxValidation.MaxFeeNotEnabled));
            }
        }
    }

    [TestCase((byte)0x04, TestName = "Execute_MaxFee_TxParam_MaxFee")]
    [TestCase((byte)0x05, TestName = "Execute_MaxFee_TxParam_MaxBlobFeeHalts")]
    [TestCase((byte)0x06, TestName = "Execute_MaxFee_TxParam_MaxCost")]
    [TestCase((byte)Instruction.GASPRICE, TestName = "Execute_MaxFee_GasPrice_IsTheEscrowedPerGasPrice")]
    public void Execute_MaxFee_Introspection(byte param)
    {
        const ulong tip = 100;
        UseBogotaFrames(eip7999: true);
        Instruction read = param == (byte)Instruction.GASPRICE ? Instruction.GASPRICE : Instruction.TXPARAM;
        Prepare code = read == Instruction.TXPARAM ? Prepare.EvmCode.PushData(param).Op(read) : Prepare.EvmCode.Op(read);
        DeploySmartSender(ApproveCode(FrameFlags.ApproveExecutionAndPayment));
        DeployContract(Observer, code.PushData(0).Op(Instruction.SSTORE).Op(Instruction.STOP).Done);
        // Preset so a halting read, which never stores, is told apart from one that stores zero.
        _stateProvider.Set(new StorageCell(Observer, 0), (UInt256)1);
        _stateProvider.Commit(Spec);
        Transaction tx = FrameTx(nonce: 0, SelfVerifyFrame(), Frame(FrameMode.Default, target: Observer, stateGasLimit: 0));
        ulong maxGas = MaxGas(tx);
        // Under a wei of headroom per gas: the budget, not the tip, bounds max_priority_fee.
        UInt256 maxFee = (UInt256)maxGas * BudgetBaseFee + maxGas * 9 / 10;
        WithMaxFee(tx, maxFee, tip);

        TransactionResult result = Process(tx, baseFeePerGas: BudgetBaseFee);

        Assert.That(result.TransactionExecuted, Is.True, result.ErrorDescription);
        UInt256 maxCost = maxFee;
        UInt256 gasPrice = maxCost / maxGas;
        if (param == (byte)Instruction.GASPRICE)
        {
            Assert.That(gasPrice, Is.Not.EqualTo(UInt256.Min(tx.DecodedMaxFeePerGas, BudgetBaseFee + tip)), "a per-gas reading must differ, or this pins nothing");
        }

        AssertStorage(Observer, 0, param switch
        {
            0x04 => maxFee,
            0x05 => UInt256.One,
            0x06 => maxCost,
            _ => gasPrice
        });
    }

    // As for max_fee_per_gas, a simulation that names no fee is not held to the base fee, and one that names any is.
    [TestCase(0ul, true, TestName = "Execute_MaxFee_FeelessSimulation_SkipsTheBudgetCheck")]
    [TestCase(1ul, false, TestName = "Execute_MaxFee_PricedSimulation_IsHeldToTheBudget")]
    public void Execute_MaxFee_SimulationBudgetCheck(ulong maxFee, bool executes)
    {
        UseBogotaFrames(eip7999: true);
        DeploySmartSender(ApproveCode(FrameFlags.ApproveExecutionAndPayment));
        Transaction tx = WithMaxFee(FrameTx(nonce: 0, SelfVerifyFrame()), maxFee, 0);

        TransactionResult result = ProcessWith(tx, ExecutionOptions.SkipValidationAndCommit, BudgetBaseFee);

        Assert.That(result.TransactionExecuted, Is.EqualTo(executes), result.ErrorDescription);
    }

    [Test]
    public void Execute_MaxFee_GasSearchProbeDefersTheBudgetCheck([Values] bool probe)
    {
        UseBogotaFrames(eip7999: true);
        DeploySmartSender(ApproveCode(FrameFlags.ApproveExecutionAndPayment));
        Transaction tx = FrameTx(nonce: 0, SelfVerifyFrame());
        WithMaxFee(tx, (UInt256)MaxGas(tx) * BudgetBaseFee - 1, 0);
        ExecutionOptions options = probe ? ExecutionOptions.CommitAndRestore | ExecutionOptions.FrameGasEstimation : ExecutionOptions.CommitAndRestore;

        TransactionResult result = ProcessWith(tx, options, BudgetBaseFee);

        Assert.That(result.TransactionExecuted, Is.EqualTo(probe), result.ErrorDescription);
    }
}
