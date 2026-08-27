// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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

    private readonly RefundTracker _refundTracker;

    public GethLikeTxMemoryTracer(Transaction? transaction, GethTraceOptions options, long destroyRefund = 0) : base(options)
    {
        _transaction = transaction;
        _refundTracker = new(destroyRefund);
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

    public override void LoadOperationStorage(Address address, UInt256 storageIndex, ReadOnlySpan<byte> value)
    {
        base.LoadOperationStorage(address, storageIndex, value);

        RecordStorageSnapshot(address, storageIndex, value);
    }

    public override void SetOperationStorage(Address address, UInt256 storageIndex, ReadOnlySpan<byte> newValue, ReadOnlySpan<byte> currentValue)
    {
        base.SetOperationStorage(address, storageIndex, newValue, currentValue);

        RecordStorageSnapshot(address, storageIndex, newValue);
    }

    private void RecordStorageSnapshot(Address address, UInt256 storageIndex, ReadOnlySpan<byte> value)
    {
        if (CurrentTraceEntry is null)
            return;

        CurrentTraceEntry.StorageDelta = (address, storageIndex, new UInt256(value, isBigEndian: true));
    }

    public override void StartOperation(int pc, Instruction opcode, ulong gas, in ExecutionEnvironment env)
    {
        base.StartOperation(pc, opcode, gas, env);
        CurrentTraceEntry.Refund = _refundTracker.Refund != 0 ? _refundTracker.Refund : null;
    }

    public override void SetOperationReturnData(ReadOnlyMemory<byte> returnData)
    {
        if (CurrentTraceEntry is not null && !returnData.IsEmpty)
            CurrentTraceEntry.ReturnData = returnData.Span.ToHexString(true);
    }

    public override void ReportRefund(long refund) => _refundTracker.Add(refund);

    public override void ReportSelfDestruct(Address address, UInt256 balance, Address refundAddress) =>
        _refundTracker.CreditSelfDestruct(address);

    public override void ReportAction(ulong gas, UInt256 value, Address from, Address to, ReadOnlyMemory<byte> input, ExecutionType callType, bool isPrecompileCall = false)
    {
        base.ReportAction(gas, value, from, to, input, callType, isPrecompileCall);
        _refundTracker.TakeSnapshot();
    }

    public override void ReportActionEnd(ulong gas, ReadOnlyMemory<byte> output)
    {
        base.ReportActionEnd(gas, output);
        _refundTracker.CommitSnapshot();
    }

    public override void ReportActionEnd(ulong gas, Address deploymentAddress, ReadOnlyMemory<byte> deployedCode)
    {
        base.ReportActionEnd(gas, deploymentAddress, deployedCode);
        _refundTracker.CommitSnapshot();
    }

    public override void ReportActionRevert(ulong gasLeft, ReadOnlyMemory<byte> output)
    {
        base.ReportActionRevert(gasLeft, output);
        _refundTracker.RestoreSnapshot();
    }

    public override void ReportActionError(EvmExceptionType evmExceptionType)
    {
        base.ReportActionError(evmExceptionType);
        _refundTracker.RestoreSnapshot();
    }
}
