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
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Facade.Proxy.Models.Simulate;
using Nethermind.Int256;
using Log = Nethermind.Facade.Proxy.Models.Simulate.Log;

namespace Nethermind.Facade.Simulate;

public sealed class SimulateTxTracer : TxTracer
{
    private static readonly Hash256 TransferSignature =
        new AbiSignature("Transfer", AbiType.Address, AbiType.Address, AbiType.UInt256).Hash;

    private static readonly AbiSignature AbiTransferSignature = new("", AbiType.UInt256);

    private static readonly Address Erc20Sender = new("0xeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");
    private readonly Hash256 _currentBlockHash;
    private readonly ulong _currentBlockNumber;
    private readonly ulong _currentBlockTimestamp;
    private readonly ulong _txIndex;
    private readonly List<LogEntry> _logs;
    private readonly Transaction _tx;

    public SimulateTxTracer(bool isTracingTransfers, Transaction tx, ulong currentBlockNumber, Hash256 currentBlockHash,
        ulong currentBlockTimestamp, ulong txIndex)
    {
        // Note: Tx hash will be mutated as tx is modified while processing block
        _tx = tx;
        _currentBlockNumber = currentBlockNumber;
        _currentBlockHash = currentBlockHash;
        _currentBlockTimestamp = currentBlockTimestamp;
        _txIndex = txIndex;
        IsTracingReceipt = true;
        IsTracingLogs = true;
        IsTracingActions = isTracingTransfers;
        _logs = new();
    }

    public SimulateCallResult? TraceResult { get; set; }

    public override void ReportAction(long gas, UInt256 value, Address from, Address to, ReadOnlyMemory<byte> input, ExecutionType callType, bool isPrecompileCall = false)
    {
        base.ReportAction(gas, value, from, to, input, callType, isPrecompileCall);
        if (callType == ExecutionType.DELEGATECALL) return;
        if (value > UInt256.Zero)
        {
            var data = AbiEncoder.Instance.Encode(AbiEncodingStyle.Packed, AbiTransferSignature, value);
            _logs.Add(new LogEntry(Erc20Sender, data, [TransferSignature, from.ToHash().ToHash256(), to.ToHash().ToHash256()]));
        }
    }

    public override void ReportSelfDestruct(Address address, UInt256 balance, Address refundAddress)
    {
        base.ReportSelfDestruct(address, balance, refundAddress);
        if (balance > UInt256.Zero)
        {
            var data = AbiEncoder.Instance.Encode(AbiEncodingStyle.Packed, AbiTransferSignature, balance);
            _logs.Add(new LogEntry(Erc20Sender, data, [TransferSignature, address.ToHash().ToHash256(), refundAddress.ToHash().ToHash256()]));
        }
    }

    public override void ReportLog(LogEntry log)
    {
        base.ReportLog(log);
        _logs.Add(log);
    }

    public override void MarkAsSuccess(Address recipient, GasConsumed gasSpent, byte[] output, LogEntry[] logs, Hash256? stateRoot = null)
    {
        TraceResult = new SimulateCallResult
        {
            GasUsed = (ulong)gasSpent.SpentGas,
            ReturnData = output,
            Status = StatusCode.Success,
            Logs = _logs.Select((entry, i) => new Log
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
