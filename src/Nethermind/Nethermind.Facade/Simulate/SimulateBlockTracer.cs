// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.Tracing.GethStyle.Custom.JavaScript;
using Nethermind.Facade.Proxy.Models.Simulate;
using ResultType = Nethermind.Facade.Proxy.Models.Simulate.ResultType;

namespace Nethermind.Facade.Simulate;

public class SimulateBlockTracer(bool isTracingLogs) : BlockTracer
{
    public List<SimulateBlockResult> Results { get; } = new();

    private readonly List<SimulateTxTracer> _txTracers = new();

    private Block _currentBlock = null!;

    public override void StartNewBlockTrace(Block block)
    {
        _txTracers.Clear();
        _currentBlock = block;
    }

    public override ITxTracer StartNewTxTrace(Transaction? tx)
    {

        if (tx?.Hash is not null)
        {
            ulong txIndex = (ulong)_txTracers.Count;
            SimulateTxTracer result = new(isTracingLogs, tx.Hash, (ulong)_currentBlock.Number, _currentBlock.Hash, txIndex);
            _txTracers.Add(result);
            return result;
        }

        return NullTxTracer.Instance;
    }

    public override void EndBlockTrace()
    {
        SimulateBlockResult? result = new()
        {
            Calls = _txTracers.Select(t => t.TraceResult).ToList(),
            Number = (ulong)_currentBlock.Number,
            Hash = _currentBlock.Hash!,
            GasLimit = (ulong)_currentBlock.GasLimit,
            GasUsed = _txTracers.Aggregate(0ul, (s, t) => s + t.TraceResult!.GasUsed ?? 0ul),
            Timestamp = _currentBlock.Timestamp,
            FeeRecipient = _currentBlock.Beneficiary!,
            BaseFeePerGas = _currentBlock.BaseFeePerGas,
            PrevRandao = _currentBlock.Header!.Random?.BytesToArray(),
            BlobGasUsed = _currentBlock.BlobGasUsed ?? 0,
            ExcessBlobGas = _currentBlock.ExcessBlobGas ?? 0,
            BlobBaseFee = new BlockExecutionContext(_currentBlock.Header).BlobBaseFee ?? 0,
            Withdrawals = _currentBlock.Withdrawals ?? Array.Empty<Withdrawal>()
        };

        Results.Add(result);
    }
}
