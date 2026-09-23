// SPDX-FileCopyrightText: 2023-2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;

namespace Nethermind.Blockchain.Tracing.GethStyle;

public class GethLikeTxFileTracer : GethLikeTxTracer<GethTxFileTraceEntry>
{
    private readonly Action<GethTxFileTraceEntry> _dumpCallback;
    private readonly Action<ReadOnlyMemory<byte>, ulong, string?>? _dumpActionEnd;
    private readonly Stack<ulong>? _actionGas;
    private GethTxFileTraceEntry? _reusableEntry;
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
        ulong? standardIntrinsicGas = null) : this(dumpCallback, null, options, destroyRefund, standardIntrinsicGas)
    {
    }

    internal GethLikeTxFileTracer(
        Action<GethTxFileTraceEntry> dumpCallback,
        Action<ReadOnlyMemory<byte>, ulong, string?>? dumpActionEnd,
        GethTraceOptions options,
        long destroyRefund,
        ulong? standardIntrinsicGas) : base(options, destroyRefund)
    {
        _dumpCallback = dumpCallback ?? throw new ArgumentNullException(nameof(dumpCallback));
        _gasTracker = new(standardIntrinsicGas);
        _dumpActionEnd = dumpActionEnd;
        if (dumpActionEnd is not null)
            _actionGas = new();

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
        _actionGas?.Push(gas);
    }

    public override void ReportActionEnd(ulong gas, ReadOnlyMemory<byte> output)
    {
        base.ReportActionEnd(gas, output);
        CompleteAction(gas, output);
    }

    public override void ReportActionEnd(ulong gas, Address deploymentAddress, ReadOnlyMemory<byte> deployedCode)
    {
        base.ReportActionEnd(gas, deploymentAddress, deployedCode);
        CompleteAction(gas, deployedCode);
    }

    public override void ReportActionRevert(ulong gasLeft, ReadOnlyMemory<byte> output)
    {
        base.ReportActionRevert(gasLeft, output);
        CompleteAction(gasLeft, output, EvmExceptionType.Revert.GetEvmExceptionDescription());
    }

    public override void ReportActionError(EvmExceptionType evmExceptionType)
    {
        base.ReportActionError(evmExceptionType);
        CompleteAction(0, default, evmExceptionType.GetEvmExceptionDescription());
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
        GethTxFileTraceEntry entry = _reusableEntry ??= new();

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
        entry.ReturnData = default;
        entry.Stack = default;
        entry.Storage = default;

        return entry;
    }

    private void CompleteAction(ulong gas, ReadOnlyMemory<byte> output, string? error = null)
    {
        if (_gasTracker.EndAction(gas) is ulong gasUsed)
            Trace.Gas = gasUsed;

        if (_actionGas?.TryPop(out ulong initialGas) == true && _actionGas.Count != 0)
        {
            // The terminal opcode (or caller's CALL for an empty child) precedes the exit record.
            if (CurrentTraceEntry is not null)
            {
                AddTraceEntry(CurrentTraceEntry);
                CurrentTraceEntry = null;
            }

            _dumpActionEnd!(output, initialGas.SaturatingSub(gas), error);
        }
    }

    private void SetReceiptGasFallback(in GasConsumed gasSpent)
    {
        if (_gasTracker.GetReceiptFallback(in gasSpent) is ulong gasUsed)
            Trace.Gas = gasUsed;
    }
}
