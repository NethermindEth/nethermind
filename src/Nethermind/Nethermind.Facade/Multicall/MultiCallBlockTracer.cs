// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Evm.Tracing;
using Nethermind.Facade.Proxy.Models.MultiCall;
using ResultType = Nethermind.Facade.Proxy.Models.MultiCall.ResultType;

namespace Nethermind.Facade.Multicall;

public class MultiCallBlockTracer : BlockTracer
{
    private readonly bool _isTracingLogs;
    public List<MultiCallBlockResult> Results { get; } = new();

    private readonly List<MultiCallTxTracer> _txTracers = new();

    private Block _currentBlock = null!;

    public MultiCallBlockTracer(bool isTracingLogs)
    {
        _isTracingLogs = isTracingLogs;
    }

    public override void StartNewBlockTrace(Block block)
    {
        _txTracers.Clear();
        _currentBlock = block;
    }

    public override ITxTracer StartNewTxTrace(Transaction? tx)
    {
        if (tx?.Hash is not null)
        {
            MultiCallTxTracer result = new(_isTracingLogs);
            _txTracers.Add(result);
            return result;
        }

        return NullTxTracer.Instance;
    }

    public override void EndBlockTrace()
    {
        MultiCallBlockResult? result = new()
        {
            Calls = _txTracers.Select(t => t.TraceResult),
            Number = (ulong)_currentBlock.Number,
            Hash = _currentBlock.Hash!,
            GasLimit = (ulong)_currentBlock.GasLimit,
            GasUsed = (ulong)_currentBlock.GasUsed,
            Timestamp = _currentBlock.Timestamp,
            FeeRecipient = _currentBlock.Beneficiary!,
            BaseFeePerGas = _currentBlock.BaseFeePerGas,
            PrevRandao = _currentBlock.Header!.Random,
        };

        result.Calls.ForEach(callResult =>
        {
            if (callResult.Type == ResultType.Success)
            {
                callResult.Logs?.ForEach(log =>
                {
                    log.BlockHash = _currentBlock.Hash!;
                    log.BlockNumber = (ulong)_currentBlock.Number;
                });
            }
        });

        //TODO: We could potentially improve performance, through streaming through enumerable and yield return rather than accumulating huge result list in memory.
        Results.Add(result);
    }
}
