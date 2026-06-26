// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;

namespace Nethermind.Blockchain.Tracing.GethStyle;

public class GethLikeTxMemoryTracer : GethLikeTxTracer<GethTxMemoryTraceEntry>
{
    private readonly Transaction? _transaction;

    private long _refund;
    private readonly Stack<long> _refundCheckpoints = new();

    public GethLikeTxMemoryTracer(Transaction? transaction, GethTraceOptions options) : base(options)
    {
        _transaction = transaction;
        IsTracingMemory = IsTracingFullMemory;
        IsTracingRefunds = true;
        IsTracingActions = true;
    }

    public override GethLikeTxTrace BuildResult()
    {
        GethLikeTxTrace trace = base.BuildResult();

        trace.TxHash = _transaction?.Hash;

        return trace;
    }

    public override void MarkAsSuccess(Address recipient, in GasConsumed gasSpent, byte[] output, LogEntry[] logs, Hash256? stateRoot = null)
    {
        base.MarkAsSuccess(recipient, gasSpent, output, logs, stateRoot);

        Trace.Gas = gasSpent.SpentGas;
    }

    public override void SetOperationStorage(Address address, UInt256 storageIndex, ReadOnlySpan<byte> newValue, ReadOnlySpan<byte> currentValue)
    {
        base.SetOperationStorage(address, storageIndex, newValue, currentValue);

        byte[] bigEndian = new byte[32];

        storageIndex.ToBigEndian(bigEndian);

        CurrentTraceEntry.Storage[bigEndian.ToHexString(false)] = new ZeroPaddedSpan(newValue, 32 - newValue.Length, PadDirection.Left)
            .ToArray()
            .ToHexString(false);
    }

    public override void StartOperation(int pc, Instruction opcode, ulong gas, in ExecutionEnvironment env)
    {
        GethTxMemoryTraceEntry previousTraceEntry = CurrentTraceEntry;
        int previousDepth = CurrentTraceEntry?.Depth ?? 0;

        base.StartOperation(pc, opcode, gas, env);

        // Geth's struct logger captures the cumulative gas-refund counter before the opcode
        // executes and emits it only when non-zero (matching go-ethereum's pre-op GetRefund()).
        CurrentTraceEntry.Refund = _refund != 0 ? _refund : null;

        if (CurrentTraceEntry.Depth > previousDepth)
        {
            CurrentTraceEntry.Storage = [];

            Trace.StoragesByDepth.Push(previousTraceEntry is null ? [] : previousTraceEntry.Storage);
        }
        else if (CurrentTraceEntry.Depth < previousDepth)
        {
            if (previousTraceEntry is null)
                throw new InvalidOperationException("Missing the previous trace on leaving the call.");

            CurrentTraceEntry.Storage = new Dictionary<string, string>(Trace.StoragesByDepth.Pop());
        }
        else
        {
            if (previousTraceEntry is null)
                throw new InvalidOperationException("Missing the previous trace on continuation.");

            CurrentTraceEntry.Storage = new Dictionary<string, string>(previousTraceEntry.Storage);
        }
    }

    public override void SetOperationReturnData(ReadOnlyMemory<byte> returnData)
    {
        if (CurrentTraceEntry is not null && !returnData.IsEmpty)
            CurrentTraceEntry.ReturnData = returnData.Span.ToHexString(true);
    }

    public override void ReportRefund(long refund) => _refund += refund;

    public override void ReportAction(ulong gas, UInt256 value, Address from, Address to, ReadOnlyMemory<byte> input, ExecutionType callType, bool isPrecompileCall = false)
    {
        base.ReportAction(gas, value, from, to, input, callType, isPrecompileCall);
        _refundCheckpoints.Push(_refund);
    }

    public override void ReportActionEnd(ulong gas, ReadOnlyMemory<byte> output)
    {
        base.ReportActionEnd(gas, output);
        _refundCheckpoints.TryPop(out _);
    }

    public override void ReportActionEnd(ulong gas, Address deploymentAddress, ReadOnlyMemory<byte> deployedCode)
    {
        base.ReportActionEnd(gas, deploymentAddress, deployedCode);
        _refundCheckpoints.TryPop(out _);
    }

    public override void ReportActionRevert(ulong gasLeft, ReadOnlyMemory<byte> output)
    {
        base.ReportActionRevert(gasLeft, output);
        RestoreRefundCheckpoint();
    }

    public override void ReportActionError(EvmExceptionType evmExceptionType)
    {
        base.ReportActionError(evmExceptionType);
        RestoreRefundCheckpoint();
    }

    // A reverted or aborted frame rolls back every refund accrued within it (and its successful
    // children), mirroring go-ethereum's journaled refund counter.
    private void RestoreRefundCheckpoint()
    {
        if (_refundCheckpoints.TryPop(out long checkpoint))
            _refund = checkpoint;
    }
}
