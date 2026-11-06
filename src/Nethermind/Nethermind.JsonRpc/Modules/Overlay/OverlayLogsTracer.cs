// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Facade.Filters;
using Nethermind.Int256;

namespace Nethermind.JsonRpc.Modules.Overlay;

/// <summary>Collects the logs of each successful transaction that match the filter, indexed as eth_getLogs indexes them.</summary>
internal sealed class OverlayLogsTracer : TxTracer, IBlockTracer
{
    private readonly LogFilter _filter;
    private readonly ICollection<FilterLog> _logs;
    private readonly ISpecProvider _specProvider;
    private bool _logIndexPerTransaction;
    private Block _block = null!;
    private Transaction _tx = null!;
    private int _txIndex;
    private long _logIndex;

    public OverlayLogsTracer(LogFilter filter, ICollection<FilterLog> logs, ISpecProvider specProvider)
    {
        _specProvider = specProvider;
        _filter = filter;
        _logs = logs;
        IsTracingReceipt = true;
    }

    public bool IsTracingRewards => false;

    public void ReportReward(Address author, string rewardType, UInt256 rewardValue) { }

    public void StartNewBlockTrace(Block block)
    {
        _block = block;
        _txIndex = -1;
        _logIndex = 0;
        _logIndexPerTransaction = _specProvider.GetSpec(block.Header).IsEip8116Enabled;
    }

    public ITxTracer StartNewTxTrace(Transaction? tx)
    {
        if (tx is null) return NullTxTracer.Instance;

        _tx = tx;
        _txIndex++;
        if (_logIndexPerTransaction) _logIndex = 0;
        return this;
    }

    public void EndTxTrace() { }

    public void EndBlockTrace() { }

    public override void MarkAsSuccess(Address recipient, in GasConsumed gasSpent, byte[] output, LogEntry[] logs, Hash256? stateRoot = null)
    {
        for (int i = 0; i < logs.Length; i++)
        {
            LogEntry log = logs[i];
            long logIndex = _logIndex++;
            if (_filter.Accepts(log))
            {
                _logs.Add(new FilterLog(logIndex, _block.Number, _block.Timestamp, _block.Hash!, _txIndex, _tx.Hash!, log.Address, log.Data, log.Topics));
            }
        }
    }
}
