// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Facade.Proxy.Models.MultiCall;

namespace Nethermind.Facade;

internal class MultiCallTxTracer : TxTracer
{
    public MultiCallTxTracer()
    {
        IsTracingReceipt = true;
    }

    public MultiCallCallResult TraceResult { get; set; }

    public override void MarkAsSuccess(Address recipient, long gasSpent, byte[] output, LogEntry[] logs,
        Keccak? stateRoot = null)
    {
        TraceResult = new MultiCallCallResult()
        {
            GasUsed = (ulong)gasSpent,
            ReturnData = output,
            Status = StatusCode.Success.ToString(),
            Logs = logs.Select((entry, i) => new Log
            {
                Data = entry.Data,
                Address = entry.LoggersAddress,
                Topics = entry.Topics,
                LogIndex = (ulong)i
            }).ToArray()
        };
    }

    public override void MarkAsFailed(Address recipient, long gasSpent, byte[] output, string error, Keccak? stateRoot = null)
    {
        TraceResult = new MultiCallCallResult()
        {

            GasUsed = (ulong)gasSpent,
            Error = new Facade.Proxy.Models.MultiCall.Error
            {
                Code = StatusCode.Failure,
                Message = error
            },
            ReturnData = output,
            Status = StatusCode.Failure.ToString()
        };
    }
}
