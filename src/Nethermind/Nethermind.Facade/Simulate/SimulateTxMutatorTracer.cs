// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Abi;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Facade.Proxy.Models.Simulate;
using Nethermind.Int256;
using Log = Nethermind.Facade.Proxy.Models.Simulate.Log;

namespace Nethermind.Facade.Simulate;

public sealed class SimulateTxMutatorTracer : TxTracer, ITxLogsMutator
{
    private static readonly Hash256 transferSignature =
        new AbiSignature("Transfer", AbiType.Address, AbiType.Address, AbiType.UInt256).Hash;

    private static readonly Address Erc20Sender = new("0xeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");
    private readonly Hash256 _currentBlockHash;
    private readonly ulong _currentBlockNumber;
    private readonly ulong _currentBlockTimestamp;
    private readonly ulong _txIndex;
    private ICollection<LogEntry>? _logsToMutate;
    private readonly Transaction _tx;

    public SimulateTxMutatorTracer(bool isTracingTransfers, Transaction tx, ulong currentBlockNumber, Hash256 currentBlockHash,
        ulong currentBlockTimestamp, ulong txIndex)
    {
        // Note: Tx hash will be mutated as tx is modified while processing block
        _tx = tx;
        _currentBlockNumber = currentBlockNumber;
        _currentBlockHash = currentBlockHash;
        _currentBlockTimestamp = currentBlockTimestamp;
        _txIndex = txIndex;
        IsTracingReceipt = true;
        IsTracingActions = IsMutatingLogs = isTracingTransfers;
        IsMutatingLogs = false;
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
            _logsToMutate?.Add(new LogEntry(Erc20Sender, data, [transferSignature, (Hash256)from.ToHash(), (Hash256)to.ToHash()]));
        }
    }

    public override void MarkAsSuccess(Address recipient, GasConsumed gasSpent, byte[] output, LogEntry[] logs, Hash256? stateRoot = null)
    {
        TraceResult = new SimulateCallResult
        {
            GasUsed = (ulong)gasSpent.SpentGas,
            ReturnData = output,
            Status = StatusCode.Success,
            Logs = logs.Select((entry, i) => new Log
            {
                Address = entry.Address,
                Topics = entry.Topics,
                Data = entry.Data,
                LogIndex = (ulong)i,
                TransactionHash = _tx.Hash!,
                TransactionIndex = _txIndex,
                BlockHash = _currentBlockHash,
                BlockNumber = _currentBlockNumber,
                BlockTimestamp = _currentBlockTimestamp
            }).ToList()
        };
    }

    public override void MarkAsFailed(Address recipient, GasConsumed gasSpent, byte[] output, string? error, Hash256? stateRoot = null)
    {
        TraceResult = new SimulateCallResult
        {
            GasUsed = (ulong)gasSpent.SpentGas,
            Error = new Error
            {
                Message = error
            },
            ReturnData = output,
            Status = StatusCode.Failure
        };
    }
}
