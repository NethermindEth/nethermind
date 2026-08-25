// SPDX-FileCopyrightText: 2023-2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;

namespace Nethermind.Blockchain.Tracing.GethStyle;

public class GethLikeTxFileTracer : GethLikeTxTracer<GethTxFileTraceEntry>
{
    private readonly Action<GethTxFileTraceEntry> _dumpCallback;
    private ulong? _startGas;
    private long _refund;
    private readonly Stack<long> _refundCheckpoints = new();

    public GethLikeTxFileTracer(Action<GethTxFileTraceEntry> dumpCallback, GethTraceOptions options) : base(options)
    {
        _dumpCallback = dumpCallback ?? throw new ArgumentNullException(nameof(dumpCallback));

        IsTracingMemory = true;
        IsTracingOpLevelStorage = false;
        IsTracingRefunds = true;
        IsTracingActions = true;
    }

    public override GethLikeTxTrace BuildResult()
    {
        GethLikeTxTrace trace = base.BuildResult();

        if (_startGas.HasValue)
            trace.Gas = _startGas.Value - CurrentTraceEntry.Gas;

        return trace;
    }

    public override void StartOperation(int pc, Instruction opcode, ulong gas, in ExecutionEnvironment env)
    {
        base.StartOperation(pc, opcode, gas, env);
        CurrentTraceEntry.Refund = _refund != 0 ? _refund : null;
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

    protected override void AddTraceEntry(GethTxFileTraceEntry entry)
    {
        _dumpCallback(entry);

        _startGas ??= entry.Gas;
    }

    protected override GethTxFileTraceEntry CreateTraceEntry(Instruction opcode)
    {
        GethTxFileTraceEntry entry = GetOrCreateTraceEntry();

        entry.OpcodeRaw = opcode;

        return entry;
    }

    private GethTxFileTraceEntry GetOrCreateTraceEntry()
    {
        if (CurrentTraceEntry is null)
            return new();

        GethTxFileTraceEntry entry = CurrentTraceEntry;

        entry.Depth = default;
        entry.Error = default;
        entry.Gas = default;
        entry.GasCost = default;
        entry.Memory = default;
        entry.MemorySize = default;
        entry.Opcode = default;
        entry.OpcodeRaw = default;
        entry.ProgramCounter = default;
        entry.Refund = default;
        entry.Stack = default;
        entry.Storage = default;

        return entry;
    }

    private void RestoreRefundCheckpoint()
    {
        if (_refundCheckpoints.TryPop(out long checkpoint))
            _refund = checkpoint;
    }
}
