// SPDX-FileCopyrightText: 2023-2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;

namespace Nethermind.Blockchain.Tracing.GethStyle;

public class GethLikeTxFileTracer : GethLikeTxTracer<GethTxFileTraceEntry>
{
    private readonly Action<GethTxFileTraceEntry> _dumpCallback;
    private TopLevelGasTracker _gasTracker;

    /// <summary>
    /// Creates a streaming Geth-style transaction tracer.
    /// </summary>
    /// <param name="dumpCallback">Callback invoked for each completed trace entry.</param>
    /// <param name="options">Geth trace configuration.</param>
    /// <param name="destroyRefund">Refund awarded for the first successful legacy self-destruct of an account.</param>
    /// <param name="standardIntrinsicGas">Standard intrinsic gas removed from the receipt fallback when no action is traced.</param>
    public GethLikeTxFileTracer(
        Action<GethTxFileTraceEntry> dumpCallback,
        GethTraceOptions options,
        long destroyRefund = 0,
        ulong? standardIntrinsicGas = null) : base(options, destroyRefund)
    {
        _dumpCallback = dumpCallback ?? throw new ArgumentNullException(nameof(dumpCallback));
        _gasTracker = new(standardIntrinsicGas);

        IsTracingMemory = true;
        IsTracingOpLevelStorage = false;
        IsTracingRefunds = true;
        IsTracingActions = true;
    }

    public override void MarkAsSuccess(Address recipient, in GasConsumed gasSpent, byte[] output, LogEntry[] logs, Hash256? stateRoot = null)
    {
        base.MarkAsSuccess(recipient, gasSpent, output, logs, stateRoot);
        SetReceiptGasFallback(in gasSpent);
    }

    public override void MarkAsFailed(Address recipient, in GasConsumed gasSpent, byte[] output, string? error, Hash256? stateRoot = null)
    {
        base.MarkAsFailed(recipient, gasSpent, output, error, stateRoot);
        SetReceiptGasFallback(in gasSpent);
    }

    public override void ReportAction(ulong gas, UInt256 value, Address from, Address to, ReadOnlyMemory<byte> input, ExecutionType callType, bool isPrecompileCall = false)
    {
        base.ReportAction(gas, value, from, to, input, callType, isPrecompileCall);

        _gasTracker.StartAction(gas);
    }

    public override void ReportActionEnd(ulong gas, ReadOnlyMemory<byte> output)
    {
        base.ReportActionEnd(gas, output);
        CompleteAction(gas);
    }

    public override void ReportActionEnd(ulong gas, Address deploymentAddress, ReadOnlyMemory<byte> deployedCode)
    {
        base.ReportActionEnd(gas, deploymentAddress, deployedCode);
        CompleteAction(gas);
    }

    public override void ReportActionRevert(ulong gasLeft, ReadOnlyMemory<byte> output)
    {
        base.ReportActionRevert(gasLeft, output);
        CompleteAction(gasLeft);
    }

    public override void ReportActionError(EvmExceptionType evmExceptionType)
    {
        base.ReportActionError(evmExceptionType);
        CompleteAction(0);
    }

    protected override void AddTraceEntry(GethTxFileTraceEntry entry)
        => _dumpCallback(entry);

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

    private void CompleteAction(ulong gas)
    {
        if (_gasTracker.EndAction(gas) is ulong gasUsed)
            Trace.Gas = gasUsed;
    }

    private void SetReceiptGasFallback(in GasConsumed gasSpent)
    {
        if (_gasTracker.GetReceiptFallback(in gasSpent) is ulong gasUsed)
            Trace.Gas = gasUsed;
    }
}
