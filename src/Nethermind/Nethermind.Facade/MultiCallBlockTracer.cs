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
    public List<MultiCallBlockResult> _results = new();

    private List<MultiCallTxTracer> _txTracers = new();

    private Block currentBlock;

    public override void StartNewBlockTrace(Block block)
    {
        _txTracers.Clear();
        currentBlock = block;
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
            Number = (ulong)currentBlock.Number,
            Hash = currentBlock.Hash,
            GasLimit = (ulong)currentBlock.GasLimit,
            GasUsed = (ulong)currentBlock.GasUsed,
            Timestamp = currentBlock.Timestamp,
            FeeRecipient = currentBlock.Beneficiary,
            BaseFeePerGas = currentBlock.BaseFeePerGas,
            PrevRandao = new UInt256(currentBlock.Header.Random.Bytes)
        };

        result.Calls.ForEach(callResult =>
        {
            if (callResult.Type == ResultType.Success)
            {
                callResult.Logs.ForEach(log =>
                {
                    log.BlockHash = currentBlock.Hash;
                    log.BlockNumber = (ulong)currentBlock.Number;
                });
            }
        });

        _results.Add(result);

    }
}
