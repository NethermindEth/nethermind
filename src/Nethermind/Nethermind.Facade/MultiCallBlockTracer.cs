// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Evm.Tracing;
using Nethermind.Facade.Proxy.Models.MultiCall;
using Nethermind.Int256;
using ResultType = Nethermind.Facade.Proxy.Models.MultiCall.ResultType;

namespace Nethermind.Facade;

public class MultiCallBlockTracer : BlockTracer
{
    public List<MultiCallBlockResult> Results { get; } = new();

    private readonly List<MultiCallTxTracer> _txTracers = new();

    private Block _currentBlock;

    public override void StartNewBlockTrace(Block block)
    {
        _txTracers.Clear();
        _currentBlock = block;
    }

    public override ITxTracer StartNewTxTrace(Transaction? tx)
    {
        if (tx != null && tx.Hash != null)
        {
            MultiCallTxTracer result = new();
            _txTracers.Add(result);
            return result;
        }

        return NullTxTracer.Instance;
    }

    public override void EndBlockTrace()
    {
        MultiCallBlockResult? result = new()
        {
            Calls = _txTracers.Select(t => t.TraceResult).ToArray(),
            Number = (ulong)_currentBlock.Number,
            Hash = _currentBlock.Hash,
            GasLimit = (ulong)_currentBlock.GasLimit,
            GasUsed = (ulong)_currentBlock.GasUsed,
            Timestamp = _currentBlock.Timestamp,
            FeeRecipient = _currentBlock.Beneficiary,
            BaseFeePerGas = _currentBlock.BaseFeePerGas,
            PrevRandao = new UInt256(_currentBlock.Header.Random.Bytes)
        };

        result.Calls.ForEach(callResult =>
        {
            if (callResult.Type == ResultType.Success)
            {
                callResult.Logs.ForEach(log =>
                {
                    log.BlockHash = _currentBlock.Hash;
                    log.BlockNumber = (ulong)_currentBlock.Number;
                });
            }
        });

        //TODO: We could potentially improve performance, through streaming through enumerable and yield return rather than accumulating huge result list in memory.
        Results.Add(result);
    }
}
