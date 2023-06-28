// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Evm.Tracing;
using Nethermind.Facade.Proxy.Models.MultiCall;
using Nethermind.Int256;

namespace Nethermind.Facade;

public class MultiCallBlockTracer : IBlockTracer
{
    public List<MultiCallBlockResult> _results = new();

    private List<MultiCallTxTracer> _txTracers = new();

    private Block currentBlock;
    public bool TraceDetails { get; set; } 
    public bool IsTracingRewards => false;

    public MultiCallBlockTracer(bool traceDetails= true)
    {
        TraceDetails = traceDetails;
    }


    public void ReportReward(Address author, string rewardType, UInt256 rewardValue)
    {
        throw new NotImplementedException();
    }

    public void StartNewBlockTrace(Block block)
    {
        _txTracers.Clear();
        currentBlock = block;
    }

    public ITxTracer StartNewTxTrace(Transaction? tx)
    {
        if (tx != null && tx.Hash != null)
        {
            MultiCallTxTracer result = new(tx, TraceDetails);
            _txTracers.Add(result);
            return result;
        }

        return NullTxTracer.Instance;
    }

    public void EndTxTrace()
    {
    }

    public void EndBlockTrace()
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
            baseFeePerGas = currentBlock.BaseFeePerGas,
            
        };
        result.Calls.ForEach(callResult =>
        {
            callResult.Logs.ForEach(log =>
            {
                log.BlockHash = currentBlock.Hash;
                log.BlockNumber = (ulong)currentBlock.Number;
            });
        });

        _results.Add(result);

    }
}
