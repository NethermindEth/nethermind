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

/// <summary>Skips runs whose requested ceiling would be clamped by the compiled EIP-8141 limit.</summary>
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

/// <summary>Counts production attempts and optionally measures their frame-gas burn.</summary>
/// <remarks>Burn tracing is disabled for timed paths because it selects the instrumented EVM path.</remarks>
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

/// <summary>Measures gas consumed from the observed instruction span rather than the declared limit.</summary>
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
