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
    private readonly Hash256 _txHash;
    private readonly ulong _currentBlockNumber;
    private readonly Hash256 _currentBlockHash;
    private readonly ulong _txIndex;
    private static readonly Hash256[] _topics = [Keccak.Zero];
    private readonly bool _isTracingTransfers;

    public SimulateTxTracer(bool isTracingTransfers, Hash256 txHash, ulong currentBlockNumber, Hash256 currentBlockHash,
        ulong txIndex)
    {
        _txHash = txHash;
        _currentBlockNumber = currentBlockNumber;
        _currentBlockHash = currentBlockHash;
        _txIndex = txIndex;
        IsTracingReceipt = true;

        _isTracingTransfers = isTracingTransfers;
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
                Address = entry.LoggersAddress,
                Topics = entry.Topics,
                Data = entry.Data,
                LogIndex = (ulong)i,
                TransactionHash = _txHash,
                TransactionIndex = _txIndex,
                BlockHash = _currentBlockHash,
                BlockNumber = _currentBlockNumber

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
                Code = -32015, // revert error code stub
                Message = error
            },
            ReturnData = null,
            Status = StatusCode.Failure
        };
    }

    public bool IsTracingLogs => _isTracingTransfers;

    public IEnumerable<LogEntry> ReportActionAndAddResultsToState(long gas, UInt256 value, Address from, Address to, ReadOnlyMemory<byte> input, ExecutionType callType, bool isPrecompileCall = false)
    {
        base.ReportAction(gas, value, from, to, input, callType, isPrecompileCall);
        byte[] data = AbiEncoder.Instance.Encode(AbiEncodingStyle.Packed,
            new AbiSignature("Transfer", AbiType.Address, AbiType.Address, AbiType.UInt256), from, to, value);
        yield return new LogEntry(Address.Zero, data, _topics);
    }
}
