// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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

    private ITransactionProcessor BuildMilliGasProcessor<TPrices>() where TPrices : struct, IMilliGasPrices =>
        new TransactionProcessor<MilliGasPolicy<TPrices>>(BlobBaseFeeCalculator.Instance, _specProvider, _stateProvider,
            new VirtualMachine<MilliGasPolicy<TPrices>>(new TestBlockhashProvider(_specProvider), _specProvider, LimboLogs.Instance),
            new EthereumCodeInfoRepository(_stateProvider), LimboLogs.Instance);

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
