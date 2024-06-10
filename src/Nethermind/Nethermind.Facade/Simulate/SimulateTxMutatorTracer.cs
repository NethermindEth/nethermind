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
using Log = Nethermind.Facade.Proxy.Models.Simulate.Log;

namespace Nethermind.Facade.Simulate;

internal sealed class SimulateTxMutatorTracer : TxTracer, ITxLogsMutator
{
    public const int ExecutionError = -32015;

    private static readonly Hash256 transferSignature =
        new AbiSignature("Transfer", AbiType.Address, AbiType.Address, AbiType.UInt256).Hash;

    private static readonly Address Erc20Sender = new("0xeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");
    private readonly Hash256 _currentBlockHash;
    private readonly ulong _currentBlockNumber;
    private readonly Hash256 _txHash;
    private readonly ulong _txIndex;
    private ICollection<LogEntry>? _logsToMutate;

    public SimulateTxMutatorTracer(bool isTracingTransfers, Hash256 txHash, ulong currentBlockNumber, Hash256 currentBlockHash,
        ulong txIndex)
    {
        _txHash = txHash;
        _currentBlockNumber = currentBlockNumber;
        _currentBlockHash = currentBlockHash;
        _txIndex = txIndex;
        IsTracingReceipt = true;
        IsTracingActions = IsMutatingLogs = isTracingTransfers;
    }

    public SimulateCallResult? TraceResult { get; set; }

    public bool IsMutatingLogs { get; }

    public void SetLogsToMutate(ICollection<LogEntry> logsToMutate) => _logsToMutate = logsToMutate;

    public override void ReportAction(long gas, UInt256 value, Address from, Address to, ReadOnlyMemory<byte> input, ExecutionType callType, bool isPrecompileCall = false)
    {
        base.ReportAction(gas, value, from, to, input, callType, isPrecompileCall);
        if (value > UInt256.Zero)
        {
            var data = AbiEncoder.Instance.Encode(AbiEncodingStyle.Packed, new AbiSignature("", AbiType.UInt256),
                value);
            _logsToMutate?.Add(new LogEntry(Erc20Sender, data, [transferSignature, from.ToHash(), to.ToHash()]));
        }
    }

    public override void MarkAsSuccess(Address recipient, long gasSpent, byte[] output, LogEntry[] logs,
        Hash256? stateRoot = null)
    {
        TraceResult = new SimulateCallResult
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
            }).ToList()
        };
    }

    public override void MarkAsFailed(Address recipient, long gasSpent, byte[] output, string error,
        Hash256? stateRoot = null)
    {
        TraceResult = new SimulateCallResult
        {
            GasUsed = (ulong)gasSpent,
            Error = new Error
            {
                Code = ExecutionError, // revert error code stub
                Message = error
            },
            ReturnData = null,
            Status = StatusCode.Failure
        };
    }
}
