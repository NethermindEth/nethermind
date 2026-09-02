// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test;

/// <summary>
/// Skips a ceiling the running binary cannot grant, rather than reporting
/// <see cref="Eip8141Constants.MaxVerifyGas"/>'s numbers under that ceiling's label.
/// </summary>
/// <remarks>
/// <c>TransactionProcessorBase.CapFrameGas</c> bounds every prefix frame at the lesser of its declared limit
/// and what remains of the constant, unconditionally on the frame-processing path — so this guard applies
/// equally to a harness that submits through <c>TxPool</c> and one that drives
/// <c>BlockProductionTransactionsExecutor</c> directly, which is why it is shared here rather than kept
/// local to one harness.
/// </remarks>
internal static class Eip8141MeasurementGuards
{
    public static void SkipIfCeilingUnreachable(ulong ceiling)
    {
        if (ceiling > Eip8141Constants.MaxVerifyGas)
        {
            Assert.Ignore($"a {ceiling} ceiling is clamped to Eip8141Constants.MaxVerifyGas = "
                          + $"{Eip8141Constants.MaxVerifyGas}; the constant is compile-time inlined, so this point "
                          + "needs a source edit and a full rebuild.");
        }
    }
}

/// <summary>
/// Counts the transactions a block-production executor actually hands to the processor, and the gas each
/// one burned inside its frame.
/// </summary>
/// <remarks>
/// <para>
/// Shared by the EIP-8141 measurement harnesses because a pass counter cannot stand in for it: a producer
/// that stopped re-executing a failing prefix would still run its passes, so a guard built on passes holds
/// while the measurement describes an ordinary empty block.
/// </para>
/// <para>
/// <paramref name="measureBurn"/> is opt-out because <see cref="BudgetProbe"/> sets
/// <c>IsTracingInstructions</c>, which <c>CompositeTxTracer</c> ORs into the tracer the processor sees — so
/// an execution that would otherwise run untraced takes the EVM's instrumented specialisation instead. A
/// caller timing that execution must not pay it; production block building does not trace instructions.
/// </para>
/// </remarks>
/// <param name="measureBurn">Whether to attach the gas probe. Leave off when the execution is being timed.</param>
internal sealed class CountingAdapter(ITransactionProcessorAdapter inner, bool measureBurn = true)
    : ITransactionProcessorAdapter
{
    public int Attempts { get; private set; }

    public List<ulong> BurnedPerAttempt { get; } = [];

    public TransactionResult Execute(Transaction transaction, ITxTracer txTracer)
    {
        Attempts++;
        if (!measureBurn) return inner.Execute(transaction, txTracer);

        BudgetProbe probe = new();
        TransactionResult result = inner.Execute(transaction, new CompositeTxTracer(txTracer, probe));
        BurnedPerAttempt.Add(probe.Consumed);
        return result;
    }

    public void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext) =>
        inner.SetBlockExecutionContext(in blockExecutionContext);
}

/// <summary>Records the gas span a frame actually ran through, so a burn is measured rather than inferred.</summary>
internal sealed class BudgetProbe : TxTracer
{
    public BudgetProbe() => IsTracingInstructions = true;

    private ulong _high;
    private ulong _low = ulong.MaxValue;

    public long Operations { get; private set; }

    public ulong Consumed => _high >= _low && Operations > 0 ? _high - _low : 0;

    public override void StartOperation(int pc, Instruction opcode, ulong gas, in ExecutionEnvironment env)
    {
        if (gas > _high) _high = gas;
        if (gas < _low) _low = gas;
        Operations++;
    }
}
