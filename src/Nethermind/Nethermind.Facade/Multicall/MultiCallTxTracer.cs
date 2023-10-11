// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Abi;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Facade.Proxy.Models.MultiCall;
using Nethermind.Int256;

namespace Nethermind.Facade.Multicall;

internal sealed class MultiCallTxTracer : TxTracer, ILogsTxTracer
{
    private static readonly Keccak[] _topics = { Keccak.Zero };

    public MultiCallTxTracer(bool isTracingTransfers)
    {
        IsTracingLogs = isTracingTransfers;
        IsTracingReceipt = true;
    }

    public MultiCallCallResult? TraceResult { get; set; }

    public override void MarkAsSuccess(Address recipient, long gasSpent, byte[] output, LogEntry[] logs, Keccak? stateRoot = null)
    {
        TraceResult = new MultiCallCallResult()
        {
            GasUsed = (ulong)gasSpent,
            ReturnData = output,
            Status = StatusCode.Success,
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
            Error = new Error
            {
                Code = StatusCode.Failure,
                Message = error
            },
            ReturnData = output,
            Status = StatusCode.Failure
        };
    }

    public bool IsTracingLogs { get; }

    IEnumerable<LogEntry> ILogsTxTracer.ReportAction(long gas, UInt256 value, Address from, Address to, ReadOnlyMemory<byte> input, ExecutionType callType, bool isPrecompileCall)
    {
        base.ReportAction(gas, value, from, to, input, callType, isPrecompileCall);
        byte[]? data = AbiEncoder.Instance.Encode(AbiEncodingStyle.Packed,
            new AbiSignature("Transfer", AbiType.Address, AbiType.Address, AbiType.UInt256), from, to, value);
        yield return new LogEntry(Address.Zero, data, _topics);
    }
}
