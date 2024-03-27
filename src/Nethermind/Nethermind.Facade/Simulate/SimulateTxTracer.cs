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
using Nethermind.Facade.Proxy.Models.Simulate;
using Nethermind.Int256;

namespace Nethermind.Facade.Simulate;

internal sealed class SimulateTxTracer : TxTracer, ILogsTxTracer
{
    private static readonly Hash256[] _topics = [Keccak.Zero];

    public SimulateTxTracer(bool isTracingTransfers)
    {
        IsTracingLogs = isTracingTransfers;
        IsTracingReceipt = true;
    }

    public SimulateCallResult? TraceResult { get; set; }

    public override void MarkAsSuccess(Address recipient, long gasSpent, byte[] output, LogEntry[] logs, Hash256? stateRoot = null)
    {
        TraceResult = new SimulateCallResult()
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
            })
        };
    }

    public override void MarkAsFailed(Address recipient, long gasSpent, byte[] output, string error, Hash256? stateRoot = null)
    {
        TraceResult = new SimulateCallResult()
        {
            GasUsed = (ulong)gasSpent,
            Error = new Error
            {
                Code = StatusCode.FailureBytes.ToArray(),
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
        byte[] data = AbiEncoder.Instance.Encode(AbiEncodingStyle.Packed,
            new AbiSignature("Transfer", AbiType.Address, AbiType.Address, AbiType.UInt256), from, to, value);
        yield return new LogEntry(Address.Zero, data, _topics);
    }
}
