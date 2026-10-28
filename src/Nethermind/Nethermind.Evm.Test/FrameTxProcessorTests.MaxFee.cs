// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>The EIP-8141 <c>max_fee</c> budget of frame transactions.</summary>
public partial class FrameTxProcessorTests
{
    private const ulong BudgetBaseFee = 7;

    private static Transaction WithMaxFee(Transaction tx, UInt256 maxFee, UInt256 maxPriorityFeePerGas)
    {
        tx.MaxFee = maxFee;
        tx.GasPrice = maxPriorityFeePerGas;
        // As the decoder sets it, so a per-gas reading of the transaction sees the cap it would on the wire.
        tx.DecodedMaxFeePerGas = FrameTxValidation.ImpliedMaxFeePerGas(maxFee, FrameTxValidation.TotalGasLimit(tx.Frames));
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
    public void Execute_MaxFee_TipUsesUnusedHeadroom()
    {
        // A per-gas cap of 8 at base fee 7 would cap the tip at 1 per gas; the same money as a budget does not.
        DeploySmartSender(ApproveCode(FrameFlags.ApproveExecutionAndPayment));
        Transaction tx = FrameTx(nonce: 0, SelfVerifyFrame());
        WithMaxFee(tx, (UInt256)MaxGas(tx) * 8, 5);

        CallOutputTracer output = new();
        TransactionResult result = Process(tx, baseFeePerGas: BudgetBaseFee, tracer: output);

#pragma warning disable NUnit2045 // The operation must succeed before inspecting its result.
        Assert.That(result.TransactionExecuted, Is.True, result.ErrorDescription);
#pragma warning restore NUnit2045
        Assert.That(_stateProvider.GetBalance(Beneficiary), Is.EqualTo((UInt256)output.GasSpent * 5));
    }

    [Test]
    public void Execute_MaxFee_BelowRequiredMaxFee_Invalid([Values] bool covered)
    {
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
    public void Execute_MaxFee_BlobCarrierTipCappedByBudget()
    {
        DeploySmartSender(ApproveCode(FrameFlags.ApproveExecutionAndPayment));
        Transaction tx = FrameTx(nonce: 0, SelfVerifyFrame());
        tx.BlobVersionedHashes = [new byte[32]];
        const ulong excessBlobGas = 50_000_000;
        UInt256 blobFee = ExpectedBlobFee(excessBlobGas, blobCount: 1);
        UInt256 maxFee = (UInt256)MaxGas(tx) * BudgetBaseFee + blobFee + 3;
        WithMaxFee(tx, maxFee, 1_000_000);

        CallOutputTracer output = new();
        TransactionResult result = ProcessWithBlobHeader(tx, excessBlobGas, baseFeePerGas: BudgetBaseFee, tracer: output);

        Assert.That(result.TransactionExecuted, Is.True, result.ErrorDescription);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_stateProvider.GetBalance(Beneficiary), Is.EqualTo(maxFee - (UInt256)output.GasSpent * BudgetBaseFee - blobFee));
            Assert.That(_stateProvider.GetBalance(Sender), Is.EqualTo(1.Ether - maxFee));
        }
    }

    [TestCase((byte)0x04, TestName = "Execute_MaxFee_TxParam_MaxFee")]
    [TestCase((byte)0x05, TestName = "Execute_MaxFee_TxParam_MaxBlobFeeHalts")]
    [TestCase((byte)0x06, TestName = "Execute_MaxFee_TxParam_MaxCostHalts")]
    public void Execute_MaxFee_Introspection(byte param)
    {
        const ulong tip = 100;
        DeploySmartSender(ApproveCode(FrameFlags.ApproveExecutionAndPayment));
        DeployContract(Observer, Prepare.EvmCode.PushData(param).Op(Instruction.TXPARAM).PushData(0).Op(Instruction.SSTORE).Op(Instruction.STOP).Done);
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
        AssertStorage(Observer, 0, param == 0x04 ? maxFee : UInt256.One);
    }

    // GASPRICE is base_fee_per_gas + max_priority_fee // max_gas: the tip max_cost escrows, spread over max_gas, with
    // blob fees left out. Two frames read it, as every frame reads the same value.
    [TestCase(false, false, TestName = "Execute_GasPrice_TipCovered_IsTheBaseFeePlusTheTip")]
    [TestCase(true, false, TestName = "Execute_GasPrice_TipCappedByTheBudget_IsTheBaseFeePlusTheBudgetedTipPerGas")]
    [TestCase(false, true, TestName = "Execute_GasPrice_LeavesOutTheBlobFee")]
    public void Execute_GasPrice_IsTheBaseFeePlusTheMaxPriorityFeePerMaxGas(bool tipCapped, bool blobs)
    {
        const ulong tip = 100;
        DeploySmartSender(ApproveCode(FrameFlags.ApproveExecutionAndPayment));
        byte[] readGasPrice = Prepare.EvmCode.Op(Instruction.GASPRICE).PushData(0).Op(Instruction.SSTORE).Op(Instruction.STOP).Done;
        DeployContract(Observer, readGasPrice);
        DeployContract(Recipient, readGasPrice);
        // Preset, so the reads overwrite a slot rather than create one.
        _stateProvider.Set(new StorageCell(Observer, 0), (UInt256)1);
        _stateProvider.Set(new StorageCell(Recipient, 0), (UInt256)1);
        _stateProvider.Commit(Spec);
        Transaction tx = FrameTx(nonce: 0, SelfVerifyFrame(),
            Frame(FrameMode.Default, target: Observer, stateGasLimit: 0), Frame(FrameMode.Default, target: Recipient, stateGasLimit: 0));
        if (blobs) tx.BlobVersionedHashes = [new byte[32]];
        ulong maxGas = MaxGas(tx);
        UInt256 blobFee = blobs ? ExpectedBlobFee(excessBlobGas: 0, blobCount: 1) : UInt256.Zero;
        UInt256 requiredMaxFee = (UInt256)maxGas * BudgetBaseFee + blobFee;
        // Capped: 3.5 wei of headroom per gas, so max_priority_fee // max_gas is 3 where the tip asks for 100.
        UInt256 maxFee = requiredMaxFee + (tipCapped ? (UInt256)maxGas * 3 + maxGas / 2 : (UInt256)maxGas * tip);
        WithMaxFee(tx, maxFee, tip);

        TransactionResult result = blobs
            ? ProcessWithBlobHeader(tx, excessBlobGas: 0, baseFeePerGas: BudgetBaseFee)
            : Process(tx, baseFeePerGas: BudgetBaseFee);

        Assert.That(result.TransactionExecuted, Is.True, result.ErrorDescription);
        UInt256 expected = BudgetBaseFee + (tipCapped ? 3ul : tip);
        using (Assert.EnterMultipleScope())
        {
            AssertStorage(Observer, 0, expected);
            AssertStorage(Recipient, 0, expected);
        }
    }

    // As for max_fee_per_gas, a simulation that names no fee is not held to the base fee, and one that names any is.
    [TestCase(0ul, true, TestName = "Execute_MaxFee_FeelessSimulation_SkipsTheBudgetCheck")]
    [TestCase(1ul, false, TestName = "Execute_MaxFee_PricedSimulation_IsHeldToTheBudget")]
    public void Execute_MaxFee_SimulationBudgetCheck(ulong maxFee, bool executes)
    {
        DeploySmartSender(ApproveCode(FrameFlags.ApproveExecutionAndPayment));
        Transaction tx = WithMaxFee(FrameTx(nonce: 0, SelfVerifyFrame()), maxFee, 0);

        TransactionResult result = ProcessWith(tx, ExecutionOptions.SkipValidationAndCommit, BudgetBaseFee);

        Assert.That(result.TransactionExecuted, Is.EqualTo(executes), result.ErrorDescription);
    }

    // A per-gas RPC call converted to a budget must buy what that price buys: max_gas at the base fee.
    [Test]
    public void Execute_PerGasRpcCallAtTheBaseFee_ConvertsToACoveringBudget()
    {
        DeploySmartSender(ApproveCode(FrameFlags.ApproveExecutionAndPayment));
        FrameTransactionForRpc rpc = (FrameTransactionForRpc)TransactionForRpc.FromTransaction(FrameTx(nonce: 0, SelfVerifyFrame()));
        rpc.MaxFee = null;
        rpc.MaxFeePerGas = BudgetBaseFee;
        rpc.MaxPriorityFeePerGas = 0;
        Transaction tx = rpc.ToTransaction(spec: Spec).Data!;
        tx.SenderAddress = Sender;

        TransactionResult result = Process(tx, baseFeePerGas: BudgetBaseFee);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tx.MaxFee, Is.EqualTo((UInt256)MaxGas(tx) * BudgetBaseFee));
            Assert.That(result.TransactionExecuted, Is.True, result.ErrorDescription);
        }
    }

    // The conversion must not measure what the processor's structural checks have not yet bounded.
    [Test]
    public void Execute_PerGasRpcCallWithOversizedNonceKeys_IsRejectedAsMalformed()
    {
        DeploySmartSender(ApproveCode(FrameFlags.ApproveExecutionAndPayment));
        FrameTransactionForRpc rpc = (FrameTransactionForRpc)TransactionForRpc.FromTransaction(FrameTx(nonce: 0, SelfVerifyFrame()));
        rpc.MaxFee = null;
        rpc.MaxFeePerGas = BudgetBaseFee;
        UInt256[] keys = new UInt256[Eip8250Constants.MaxNonceKeys + 1];
        // Full-width keys, so the set overruns the nonce_calldata bound the measurement enforces.
        for (int i = 0; i < keys.Length; i++) keys[i] = UInt256.MaxValue - (UInt256)i;
        rpc.NonceKeys = keys;

        Transaction tx = rpc.ToTransaction(spec: Spec).Data!;
        tx.SenderAddress = Sender;
        TransactionResult result = Process(tx, baseFeePerGas: BudgetBaseFee);

        Assert.That(result.Error, Is.EqualTo(TransactionResult.ErrorType.MalformedTransaction), result.ErrorDescription);
    }

    [Test]
    public void Execute_MaxFee_GasSearchProbeDefersTheBudgetCheck([Values] bool probe)
    {
        DeploySmartSender(ApproveCode(FrameFlags.ApproveExecutionAndPayment));
        Transaction tx = FrameTx(nonce: 0, SelfVerifyFrame());
        WithMaxFee(tx, (UInt256)MaxGas(tx) * BudgetBaseFee - 1, 0);
        ExecutionOptions options = probe ? ExecutionOptions.CommitAndRestore | ExecutionOptions.FrameGasEstimation : ExecutionOptions.CommitAndRestore;

        TransactionResult result = ProcessWith(tx, options, BudgetBaseFee);

        Assert.That(result.TransactionExecuted, Is.EqualTo(probe), result.ErrorDescription);
    }
}
