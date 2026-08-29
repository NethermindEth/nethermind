// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Blockchain.Test;

/// <summary>
/// Counts the transactions a block-production executor actually hands to the processor, and the gas each
/// one burned inside its frame.
/// </summary>
/// <remarks>
/// Shared by the EIP-8141 measurement harnesses because a pass counter cannot stand in for it: a producer
/// that stopped re-executing a failing prefix would still run its passes, so a guard built on passes holds
/// while the measurement describes an ordinary empty block.
/// </remarks>
internal sealed class CountingAdapter(ITransactionProcessorAdapter inner) : ITransactionProcessorAdapter
{
    public int Attempts { get; private set; }

    public List<ulong> BurnedPerAttempt { get; } = [];

    public TransactionResult Execute(Transaction transaction, ITxTracer txTracer)
    {
        Attempts++;
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
