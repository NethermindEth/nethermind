// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.Precompiles;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

public partial class FrameTxProcessorTests
{
    private readonly List<IDisposable> _milliGasMachines = [];

    [TearDown]
    public void DisposeMilliGasMachines()
    {
        foreach (IDisposable machine in _milliGasMachines) machine.Dispose();
        _milliGasMachines.Clear();
    }

    /// <remarks>With every price at 1,000 milli-gas per gas unit, milli-gas metering must match legacy metering exactly.</remarks>
    [Test]
    public void Eip8053_scaled_prices_match_legacy_for_a_frame_targeting_a_delegated_account([Values] bool designatePrecompile, [Values] bool coverOnlyTargetAccess)
    {
        DeploySmartSender(ApproveCode(FrameFlags.ApproveExecutionAndPayment));
        DeployContract(Recipient, Prepare.EvmCode.PushData(1).PushData(0).Op(Instruction.SSTORE).Done);
        DeployContract(Observer, [.. Eip7702Constants.DelegationHeader, .. (designatePrecompile ? IdentityPrecompile.Address : Recipient).Bytes]);
        TxFrame frame = coverOnlyTargetAccess
            ? new(FrameMode.Default, flags: FrameFlags.None, Observer, gasLimit: Eip8038Constants.ColdAccountAccess, UInt256.Zero, default)
            : Frame(FrameMode.Default, target: Observer);
        Transaction tx = FrameTx(nonce: 0, SelfVerifyFrame(), frame);

        FrameReceiptTracer legacy = ProcessAndRestore(_transactionProcessor, tx);
        FrameReceiptTracer scaled = ProcessAndRestore(BuildMilliGasProcessor<ScaledGasPrices>(), tx);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(scaled.StatusCode, Is.EqualTo(legacy.StatusCode));
            Assert.That(scaled.GasSpent, Is.EqualTo(legacy.GasSpent));
            Assert.That(scaled.FrameReceipts!.Select(static receipt => (receipt.Status, receipt.ExecutionGasUsed, receipt.StateGasUsed)),
                Is.EqualTo(legacy.FrameReceipts!.Select(static receipt => (receipt.Status, receipt.ExecutionGasUsed, receipt.StateGasUsed))));
        }
    }

    /// <remarks>
    /// Each frame runs 11 <c>JUMPDEST</c>s: 11 gas without EIP-8053 and 1.1 gas with its illustrative prices, which each
    /// frame rounds up to 2 on its own. The transaction pays the sum of the rounded frames.
    /// </remarks>
    [Test]
    public void Eip8053_rounds_execution_gas_up_once_per_frame()
    {
        DeploySmartSender(ApproveCode(FrameFlags.ApproveExecutionAndPayment));
        DeployContract(Recipient, [.. Enumerable.Repeat((byte)Instruction.JUMPDEST, 11), (byte)Instruction.STOP]);
        Transaction tx = FrameTx(nonce: 0, SelfVerifyFrame(), Frame(FrameMode.Default, target: Recipient), Frame(FrameMode.Default, target: Recipient));

        FrameReceiptTracer legacy = ProcessAndRestore(_transactionProcessor, tx);
        FrameReceiptTracer milliGas = ProcessAndRestore(BuildMilliGasProcessor<Eip8053IllustrativePrices>(), tx);

        ulong frameSavings = 0;
        for (int i = 0; i < legacy.FrameReceipts!.Length; i++)
            frameSavings += legacy.FrameReceipts[i].ExecutionGasUsed - milliGas.FrameReceipts![i].ExecutionGasUsed;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(milliGas.FrameReceipts![1].ExecutionGasUsed, Is.EqualTo(legacy.FrameReceipts[1].ExecutionGasUsed - 9));
            Assert.That(milliGas.FrameReceipts[2].ExecutionGasUsed, Is.EqualTo(legacy.FrameReceipts[2].ExecutionGasUsed - 9));
            Assert.That(legacy.GasSpent - milliGas.GasSpent, Is.EqualTo(frameSavings));
        }
    }

    /// <remarks>
    /// A frame's state use stays within its state limit and the transaction within its approved maximum gas under a
    /// binding calldata floor, with scaled prices (which must match legacy) and with fractional prices (the leading
    /// <c>JUMPDEST</c> leaves a fraction of a gas when the state charge runs).
    /// </remarks>
    [Test]
    public void Eip8053_frame_write_under_binding_calldata_floor_stays_within_limits([Values(GasCostOf.SSetState - 10_000, GasCostOf.SSetState)] long stateLimit)
    {
        DeploySmartSender(ApproveCode(FrameFlags.ApproveExecutionAndPayment));
        DeployContract(Recipient, [(byte)Instruction.JUMPDEST, .. Prepare.EvmCode.PushData(1).PushData(0).Op(Instruction.SSTORE).Op(Instruction.STOP).Done]);
        byte[] calldata = new byte[10_000];
        Array.Fill(calldata, (byte)1);
        Transaction tx = FrameTx(nonce: 0, SelfVerifyFrame(),
            new TxFrame(FrameMode.Default, FrameFlags.None, Recipient, executionGasLimit: 50_000, stateGasLimit: (ulong)stateLimit, UInt256.Zero, calldata));
        Assert.That(FrameTxValidation.TryCalculateGasBudget(tx, Spec, out _, out _, out ulong maxGas), Is.True);

        FrameReceiptTracer legacy = ProcessAndRestore(_transactionProcessor, tx);
        FrameReceiptTracer scaled = ProcessAndRestore(BuildMilliGasProcessor<ScaledGasPrices>(), tx);
        FrameReceiptTracer fractional = ProcessAndRestore(BuildMilliGasProcessor<Eip8053IllustrativePrices>(), tx);

        bool writes = stateLimit >= GasCostOf.SSetState;
        // JUMPDEST at 0.1 and two PUSH1 at 1.5 save 3.9 gas, which the frame rounds up to 3; a halted frame uses its whole limit.
        TxFrameReceipt legacyWriter = legacy.FrameReceipts![1];
        TxFrameReceipt fractionalWriter = fractional.FrameReceipts![1];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(scaled.GasSpent, Is.EqualTo(legacy.GasSpent));
            Assert.That(scaled.FrameReceipts!.Select(static receipt => (receipt.Status, receipt.ExecutionGasUsed, receipt.StateGasUsed)),
                Is.EqualTo(legacy.FrameReceipts.Select(static receipt => (receipt.Status, receipt.ExecutionGasUsed, receipt.StateGasUsed))));
            foreach (FrameReceiptTracer tracer in new[] { legacy, fractional })
            {
                Assert.That(tracer.GasSpent, Is.LessThanOrEqualTo(maxGas));
            }

            foreach (TxFrameReceipt writer in new[] { legacyWriter, fractionalWriter })
            {
                Assert.That(writer.Status, Is.EqualTo(writes ? TxFrameReceipt.StatusSuccess : TxFrameReceipt.StatusFailure));
                Assert.That(writer.StateGasUsed, Is.EqualTo(writes ? (ulong)GasCostOf.SSetState : 0UL));
            }

            Assert.That(fractionalWriter.ExecutionGasUsed, Is.EqualTo(writes ? legacyWriter.ExecutionGasUsed - 3 : 50_000UL));
            Assert.That(legacyWriter.ExecutionGasUsed, writes ? Is.LessThan(50_000UL) : Is.EqualTo(50_000UL));
        }
    }

    private ITransactionProcessor BuildMilliGasProcessor<TPrices>() where TPrices : struct, IMilliGasPrices
    {
        VirtualMachine<MilliGasPolicy<TPrices>> machine = new(new TestBlockhashProvider(_specProvider), _specProvider, LimboLogs.Instance);
        _milliGasMachines.Add(machine);
        return new TransactionProcessor<MilliGasPolicy<TPrices>>(BlobBaseFeeCalculator.Instance, _specProvider, _stateProvider, machine,
            new EthereumCodeInfoRepository(_stateProvider), LimboLogs.Instance);
    }

    private FrameReceiptTracer ProcessAndRestore(ITransactionProcessor processor, Transaction tx)
    {
        Block block = Build.A.Block.WithNumber(1)
            .WithBeneficiary(Beneficiary)
            .WithTransactions(tx)
            .WithGasLimit(30_000_000).TestObject;
        FrameReceiptTracer tracer = new();
        processor.CallAndRestore(tx, new BlockExecutionContext(block.Header, Spec), tracer);
        return tracer;
    }
}
