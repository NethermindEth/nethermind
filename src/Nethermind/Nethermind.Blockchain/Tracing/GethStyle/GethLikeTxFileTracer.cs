// SPDX-FileCopyrightText: 2023-2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Evm;
using Nethermind.Int256;

namespace Nethermind.Blockchain.Tracing.GethStyle;

public class GethLikeTxFileTracer : GethLikeTxTracer<GethTxFileTraceEntry>
{
    private readonly Action<GethTxFileTraceEntry> _dumpCallback;
    private readonly RefundTracker _refundTracker;
    private ulong? _startGas;

    public GethLikeTxFileTracer(Action<GethTxFileTraceEntry> dumpCallback, GethTraceOptions options, long destroyRefund) : base(options)
    {
        _dumpCallback = dumpCallback ?? throw new ArgumentNullException(nameof(dumpCallback));
        _refundTracker = new(destroyRefund);

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
        CurrentTraceEntry.Refund = _refundTracker.Refund != 0 ? _refundTracker.Refund : null;
    }

    public override void ReportRefund(long refund) => _refundTracker.Add(refund);

    // Credit legacy refunds at the opcode boundary. TransactionProcessor reports the value again
    // during post-execution finalization, after the last streamed entry has sampled the refund.
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
}
