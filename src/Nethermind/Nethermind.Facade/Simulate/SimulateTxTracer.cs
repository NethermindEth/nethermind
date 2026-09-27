// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Facade.Proxy.Models.Simulate;
using Nethermind.Int256;
using Log = Nethermind.Facade.Proxy.Models.Simulate.Log;

namespace Nethermind.Facade.Simulate;

public sealed class SimulateTxTracer : TxTracer, IFrameTxReceiptTracer
{
    private readonly Hash256 _currentBlockHash;
    private readonly ulong _currentBlockNumber;
    private readonly ulong _currentBlockTimestamp;
    private readonly ulong _txIndex;
    private readonly ulong _logIndexStart;
    private readonly List<LogEntry> _logs;
    private readonly Transaction _tx;
    private readonly bool _isTracingTransfers;
    private bool _hasFrameReceipts;
    private int[]? _frameLogStarts;
    private int _framesEnded;
    private int _frameLogEnd;
    private HashSet<LogEntry>? _transferLogs;
    private EvmExceptionType? _frameError;

    public SimulateTxTracer(
        bool isTracingTransfers,
        Transaction tx,
        ulong currentBlockNumber,
        Hash256 currentBlockHash,
        ulong currentBlockTimestamp,
        ulong txIndex,
        ulong logIndexStart)
    {
        // Note: Tx hash will be mutated as tx is modified while processing the block
        _tx = tx;
        _currentBlockNumber = currentBlockNumber;
        _currentBlockHash = currentBlockHash;
        _currentBlockTimestamp = currentBlockTimestamp;
        _txIndex = txIndex;
        _logIndexStart = logIndexStart;
        _isTracingTransfers = isTracingTransfers;
        IsTracingReceipt = true;
        IsTracingLogs = true;
        IsTracingActions = true;
        _logs = [];
    }

    public int LogCount => _logs.Count;
    public SimulateCallResult? TraceResult { get; set; }

    public override void ReportAction(ulong gas, UInt256 value, Address from, Address to, ReadOnlyMemory<byte> input, ExecutionType callType, bool isPrecompileCall = false)
    {
        base.ReportAction(gas, value, from, to, input, callType, isPrecompileCall);
        if (!_isTracingTransfers) return;
        if (callType == ExecutionType.DELEGATECALL) return;
        if (!value.IsZero)
        {
            AddTransferLog(from, to, value);
        }
    }

    public override void ReportSelfDestruct(Address address, UInt256 balance, Address refundAddress)
    {
        base.ReportSelfDestruct(address, balance, refundAddress);
        if (!_isTracingTransfers) return;
        if (!balance.IsZero)
        {
            AddTransferLog(address, refundAddress, balance);
        }
    }

    private void AddTransferLog(Address from, Address to, in UInt256 value)
    {
        LogEntry transferLog = TransferLog.CreateSimulateTransfer(from, to, value);
        (_transferLogs ??= new HashSet<LogEntry>(ReferenceEqualityComparer.Instance)).Add(transferLog);
        _logs.Add(transferLog);
    }

    public override void ReportLog(LogEntry log)
    {
        base.ReportLog(log);
        _logs.Add(log);
    }

    /// <inheritdoc/>
    void IFrameTxReceiptTracer.ReportFrameTxReceipt(Address payer, TxFrameReceipt[] frameReceipts)
    {
        // The validation-prefix simulation reports an empty set, which pins no frame's outcome.
        if (frameReceipts.Length == 0) return;

        // Failed and rolled-back frames are already dropped; what is left is a reverted inner call's logs,
        // which the receipt does not hold. The synthetic transfer logs are in no receipt, so they stay.
        _hasFrameReceipts = true;
        HashSet<LogEntry> committed = new(TxFrameReceipt.ConcatLogs(frameReceipts), ReferenceEqualityComparer.Instance);
        _logs.RemoveAll(log => !committed.Contains(log) && _transferLogs?.Contains(log) != true);
    }

    /// <inheritdoc/>
    void IFrameTxReceiptTracer.ReportFrameEnd(int frameIndex, EvmExceptionType? error)
    {
        _frameLogStarts ??= new int[_tx.Frames?.Length ?? 0];
        if ((uint)frameIndex >= (uint)_frameLogStarts.Length) return;

        // A skipped frame emits nothing, so it starts where the next frame to run does.
        for (; _framesEnded <= frameIndex; _framesEnded++)
        {
            _frameLogStarts[_framesEnded] = _frameLogEnd;
        }

        if (error is not null)
        {
            _frameError = error == EvmExceptionType.None ? EvmExceptionType.Revert : error;
            TruncateLogs(_frameLogStarts[frameIndex]);
        }

        _frameLogEnd = _logs.Count;
    }

    /// <inheritdoc/>
    void IFrameTxReceiptTracer.ReportFramesRolledBack(int fromFrameIndex, int toFrameIndex)
    {
        if (_frameLogStarts is null || (uint)fromFrameIndex >= (uint)_framesEnded) return;

        TruncateLogs(_frameLogStarts[fromFrameIndex]);
        _frameLogEnd = _logs.Count;
    }

    private void TruncateLogs(int count) => _logs.RemoveRange(count, _logs.Count - count);

    public override void MarkAsSuccess(Address recipient, in GasConsumed gasSpent, byte[] output, LogEntry[] logs, Hash256? stateRoot = null) => TraceResult = new SimulateCallResult
    {
        GasUsed = gasSpent.SpentGas,
        MaxUsedGas = gasSpent.EffectiveMaxUsedGas,
        ReturnData = output,
        Status = StatusCode.Success,
        Logs = BuildLogs()
    };

    public override void MarkAsFailed(Address recipient, in GasConsumed gasSpent, byte[] output, string? error, Hash256? stateRoot = null) => TraceResult = new SimulateCallResult
    {
        GasUsed = gasSpent.SpentGas,
        MaxUsedGas = gasSpent.EffectiveMaxUsedGas,
        Error = new Error
        {
            Message = FailureMessage(error),
            // A frame transaction fails for the frame that failed, not for whichever call last errored.
            EvmException = _frameError ?? _exceptionType,
            Data = output
        },
        ReturnData = [],
        Status = StatusCode.Failure,
        // A failed frame transaction keeps the logs of the frames that committed, as its receipt does.
        Logs = _hasFrameReceipts ? BuildLogs() : []
    };

    private string FailureMessage(string? error) => _frameError switch
    {
        null => error is TransactionSubstate.Revert ? "execution reverted" : "execution reverted: " + error,
        EvmExceptionType.Revert => "execution reverted",
        EvmExceptionType frameError => frameError.GetEvmExceptionDescription() ?? frameError.ToString()
    };

    private List<Log> BuildLogs() => _logs.Select((entry, i) => new Log
    {
        Address = entry.Address,
        Topics = entry.Topics,
        Data = entry.Data,
        LogIndex = _logIndexStart + (ulong)i,
        TransactionHash = _tx.Hash!,
        TransactionIndex = _txIndex,
        BlockHash = _currentBlockHash,
        BlockNumber = _currentBlockNumber,
        BlockTimestamp = _currentBlockTimestamp
    }).ToList();

    private EvmExceptionType _exceptionType = EvmExceptionType.None;

    public override void ReportActionError(EvmExceptionType evmExceptionType)
    {
        base.ReportActionError(evmExceptionType);
        _exceptionType = evmExceptionType;
    }

    public override void ReportActionRevert(ulong gas, ReadOnlyMemory<byte> output)
    {
        base.ReportActionRevert(gas, output);
        _exceptionType = EvmExceptionType.Revert;
    }
}
