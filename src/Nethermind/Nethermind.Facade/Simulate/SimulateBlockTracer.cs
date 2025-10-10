// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.Tracing;
using Nethermind.Facade.Proxy.Models.Simulate;

namespace Nethermind.Facade.Simulate;

public class SimulateBlockTracer(bool isTracingLogs) : BlockTracerBase<SimulateCallResult, SimulateTxTracer>
{
    private ulong _txIndex = 0;

    private ulong _blockNumber;
    private ulong _blockTimestamp;

    protected override SimulateTxTracer OnStart(Transaction? tx)
    {
        if (tx?.Hash is not null)
        {
            return new(isTracingLogs, tx, _blockNumber, Hash256.Zero, _blockTimestamp, _txIndex++);
        }
        return (SimulateTxTracer)NullTxTracer.Instance;
    }

    protected override SimulateCallResult OnEnd(SimulateTxTracer txTracer) => txTracer.TraceResult!;

    public void ReapplyBlockHash(Hash256 hash)
    {
        foreach (SimulateCallResult simulateCallResult in TxTraces)
        {
            foreach (Log log in simulateCallResult.Logs)
            {
                log.BlockHash = hash;
            }
        }
    }

    public override void StartNewBlockTrace(Block block)
    {
        _txIndex = 0;
        _blockNumber = (ulong)block.Number;
        _blockTimestamp = block.Timestamp;
        base.StartNewBlockTrace(block);
    }
}
