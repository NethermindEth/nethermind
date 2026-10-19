// SPDX-FileCopyrightText: 2023-2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;

namespace Nethermind.Blockchain.Tracing.GethStyle;

public class GethLikeTxFileTracer : GethLikeTxTracer<GethTxFileTraceEntry>
{
    private const int MinStackBufferSize = 16 * EvmStack.WordSize;
    private const int MinMemoryBufferSize = 1024;

    private readonly IGethFileTraceSink _sink;
    private readonly Stack<ulong>? _actionGas;
    private GethTxFileTraceEntry? _reusableEntry;
    private byte[] _stackBuffer = [];
    private byte[] _memoryBuffer = [];
    private TopLevelGasTracker _gasTracker;
    private bool _captureStopped;

    /// <summary>
    /// Creates a streaming Geth-style transaction tracer.
    /// </summary>
    /// <param name="dumpCallback">Callback invoked for each completed trace entry. The entry, and the memory its
    /// <see cref="GethTxTraceEntry.Stack"/> and <see cref="GethTxTraceEntry.Memory"/> point to, are reused for the
    /// next opcode, so they are valid only during the callback.</param>
    /// <param name="options">Geth trace configuration.</param>
    /// <param name="destroyRefund">Refund awarded for the first successful legacy self-destruct of an account.</param>
    /// <param name="standardIntrinsicGas">Standard intrinsic gas removed from the receipt fallback when no action is traced.</param>
    public GethLikeTxFileTracer(
        Action<GethTxFileTraceEntry> dumpCallback,
        GethTraceOptions options,
        long destroyRefund = 0,
        ulong? standardIntrinsicGas = null) : this(new EntryCallbackSink(dumpCallback), options, destroyRefund, standardIntrinsicGas, false)
    {
    }

    internal GethLikeTxFileTracer(IGethFileTraceSink sink, GethTraceOptions options,
        long destroyRefund, ulong? standardIntrinsicGas) : this(sink, options, destroyRefund, standardIntrinsicGas, true)
    {
    }

    private GethLikeTxFileTracer(IGethFileTraceSink sink, GethTraceOptions options,
        long destroyRefund, ulong? standardIntrinsicGas, bool traceActionEnds) : base(options, destroyRefund)
    {
        _sink = sink;
        _gasTracker = new(standardIntrinsicGas);
        if (traceActionEnds)
            _actionGas = new();

        IsTracingMemory = true;
        IsTracingOpLevelStorage = false;
        IsTracingRefunds = true;
        IsTracingActions = true;
        if (options.Limit < 0) StopCapture();
    }

    internal void StopCapture()
    {
        _captureStopped = true;
        CurrentTraceEntry = null;
        IsTracingMemory = false;
        IsTracingStack = false;
    }

    public override void StartOperation(int pc, Instruction opcode, ulong gas, in ExecutionEnvironment env)
    {
        if (_captureStopped) return;
        base.StartOperation(pc, opcode, gas, in env);
        if (_captureStopped) CurrentTraceEntry = null;
    }

    // The entry is written to the sink before the next opcode starts, so the snapshots can live in pooled buffers reused
    // across opcodes. The buffers go back to the pool on Dispose.
    public override void SetOperationStack(TraceStack stack)
    {
        if (CurrentTraceEntry is null) return;

        int length = stack.RawByteLength;
        EnsureCapacity(ref _stackBuffer, length, MinStackBufferSize);
        stack.CopyRawBytesTo(_stackBuffer);
        CurrentTraceEntry.Stack = _stackBuffer.AsMemory(0, length);
    }

    public override void SetOperationMemory(TraceMemory memoryTrace)
    {
        if (!IsTracingFullMemory || CurrentTraceEntry is null) return;

        int length = memoryTrace.RawWordByteLength;
        EnsureCapacity(ref _memoryBuffer, length, MinMemoryBufferSize);
        memoryTrace.CopyRawWordBytesTo(_memoryBuffer);
        CurrentTraceEntry.Memory = _memoryBuffer.AsMemory(0, length);
    }

    public override void Dispose()
    {
        if (_reusableEntry is not null)
        {
            _reusableEntry.Stack = default;
            _reusableEntry.Memory = default;
        }

        ReturnBuffer(ref _stackBuffer);
        ReturnBuffer(ref _memoryBuffer);
        base.Dispose();
    }

    private static void EnsureCapacity(ref byte[] buffer, int length, int minLength)
    {
        if (buffer.Length >= length) return;

        byte[] previous = buffer;
        buffer = ArrayPool<byte>.Shared.Rent(Math.Max(length, minLength));
        if (previous.Length != 0) ArrayPool<byte>.Shared.Return(previous);
    }

    private static void ReturnBuffer(ref byte[] buffer)
    {
        if (buffer.Length == 0) return;

        ArrayPool<byte>.Shared.Return(buffer);
        buffer = [];
    }

    public override void MarkAsSuccess(Address recipient, in GasConsumed gasSpent, byte[] output, LogEntry[] logs, Hash256? stateRoot = null)
    {
        base.MarkAsSuccess(recipient, gasSpent, output, logs, stateRoot);
        SetReceiptGasFallback(in gasSpent);
    }

    public override void MarkAsFailed(Address recipient, in GasConsumed gasSpent, byte[] output, string? error, Hash256? stateRoot = null)
    {
        ulong executionGas = Trace.Gas;
        base.MarkAsFailed(recipient, gasSpent, output, error, stateRoot);
        Trace.Gas = executionGas;
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
        => _sink.WriteEntry(entry);

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

            _sink.WriteActionEnd(output, initialGas.SaturatingSub(gas), error);
        }
    }

    private void SetReceiptGasFallback(in GasConsumed gasSpent)
    {
        if (_gasTracker.GetReceiptFallback(in gasSpent) is ulong gasUsed)
            Trace.Gas = gasUsed;
    }
    private sealed class EntryCallbackSink(Action<GethTxFileTraceEntry> callback) : IGethFileTraceSink
    {
        private readonly Action<GethTxFileTraceEntry> _callback = callback ?? throw new ArgumentNullException(nameof(callback));
        public void WriteEntry(GethTxFileTraceEntry entry) => _callback(entry);
        public void WriteActionEnd(ReadOnlyMemory<byte> output, ulong gas, string? error) { }
    }
}

internal interface IGethFileTraceSink
{
    void WriteEntry(GethTxFileTraceEntry entry);
    void WriteActionEnd(ReadOnlyMemory<byte> output, ulong gas, string? error);
}
