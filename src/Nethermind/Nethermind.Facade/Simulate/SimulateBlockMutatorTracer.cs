// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Evm.Tracing;
using Nethermind.Facade.Proxy.Models.Simulate;

namespace Nethermind.Facade.Simulate;

public class SimulateBlockMutatorTracer(bool isTracingLogs) : BlockTracerBase<SimulateCallResult, SimulateTxMutatorTracer>
{
    private ulong _txIndex = 0;

    private Block? _currentBlock;

    protected override SimulateTxMutatorTracer OnStart(Transaction? tx)
    {
        if (tx?.Hash is not null)
        {
            _txIndex++;
            return new(isTracingLogs, tx.Hash, (ulong)_currentBlock!.Number, _currentBlock!.Hash!, _txIndex);
        }
        return (SimulateTxMutatorTracer)NullTxTracer.Instance;
    }

    protected override SimulateCallResult OnEnd(SimulateTxMutatorTracer txTracer) => txTracer.TraceResult!;

    public override void StartNewBlockTrace(Block block)
    {
        _txIndex = 0;
        _currentBlock = block;
        base.StartNewBlockTrace(block);
    }
}
