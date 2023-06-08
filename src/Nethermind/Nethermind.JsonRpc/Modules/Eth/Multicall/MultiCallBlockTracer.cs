// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;

namespace Nethermind.JsonRpc.Modules.Eth.Multicall;

public class MultiCallBlockTracer : IBlockTracer
{
    public Dictionary<Keccak, List<LogEntry>> TxActions = new();
    public bool Trace { get; set; }
    public bool IsTracingRewards => false;

    public void ReportReward(Address author, string rewardType, UInt256 rewardValue)
    {
        throw new NotImplementedException();
    }

    public void StartNewBlockTrace(Block block)
    {
        TxActions.Clear();
    }

    public ITxTracer StartNewTxTrace(Transaction? tx)
    {
        if (Trace && tx != null && tx.Hash != null)
        {
            List<LogEntry>? actionsLog = new();
            TxActions[tx.Hash] = actionsLog;
            return new MultiCallTxTracer(tx, actionsLog);
        }

        return NullTxTracer.Instance;
    }

    public void EndTxTrace()
    {
    }

    public void EndBlockTrace()
    {
    }
}
