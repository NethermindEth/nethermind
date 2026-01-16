// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Facade.Proxy.Models.Simulate;

namespace Nethermind.Facade.Simulate;

public class SimulateBlockTracer(bool isTracingLogs, ISpecProvider spec) : BlockTracerBase<SimulateCallResult, SimulateTxTracer>
{
    private ulong _txIndex = 0;

    private ulong _blockNumber;
    private ulong _blockTimestamp;
    private bool _isTracingLogs = isTracingLogs;

    protected override SimulateTxTracer OnStart(Transaction? tx) =>
        tx?.Hash is not null
            ? new(_isTracingLogs, tx, _blockNumber, Hash256.Zero, _blockTimestamp, _txIndex++)
            : throw new InvalidOperationException($"{nameof(SimulateBlockTracer)} does not support tracing rewards.");

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
        _isTracingLogs &= !spec.GetSpec(block.Header).IsEip7708Enabled;
        base.StartNewBlockTrace(block);
    }
}
