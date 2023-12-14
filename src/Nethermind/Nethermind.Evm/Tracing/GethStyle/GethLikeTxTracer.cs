// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Evm.Tracing.GethStyle;

public abstract class GethLikeTxTracer : TxTracer
{
    protected GethLikeTxTracer(GethTraceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        IsTracingOpLevelStorage = !options.DisableStorage;
        IsTracingStack = !options.DisableStack;
        IsTracingFullMemory = options.EnableMemory;
        IsTracing = IsTracing || IsTracingFullMemory;
    }

    private GethLikeTxTrace? _trace;
    protected GethLikeTxTrace Trace => _trace ??= CreateTrace();
    protected virtual GethLikeTxTrace CreateTrace() => new();
    public override bool IsTracingReceipt => true;
    public sealed override bool IsTracingOpLevelStorage { get; protected set; }
    public sealed override bool IsTracingMemory { get; protected set; }
    public override bool IsTracingInstructions => true;
    public sealed override bool IsTracingStack { get; protected set; }
    protected bool IsTracingFullMemory { get; }

    public override void MarkAsSuccess(Address recipient, long gasSpent, byte[] output, LogEntry[] logs, Hash256? stateRoot = null, ulong? depositNonce = null, ulong? depositReceiptVersion = null)
    {
        Trace.ReturnValue = output;
    }

    public override void MarkAsFailed(Address recipient, long gasSpent, byte[]? output, string error, Hash256? stateRoot = null, ulong? depositNonce = null, ulong? depositReceiptVersion = null)
    {
        Trace.Failed = true;
        Trace.ReturnValue = output ?? Array.Empty<byte>();
    }

    protected static string? GetErrorDescription(EvmExceptionType evmExceptionType)
    {
        return evmExceptionType switch
        {
            EvmExceptionType.None => null,
            EvmExceptionType.BadInstruction => "BadInstruction",
            EvmExceptionType.StackOverflow => "StackOverflow",
            EvmExceptionType.StackUnderflow => "StackUnderflow",
            EvmExceptionType.OutOfGas => "OutOfGas",
            EvmExceptionType.InvalidSubroutineEntry => "InvalidSubroutineEntry",
            EvmExceptionType.InvalidSubroutineReturn => "InvalidSubroutineReturn",
            EvmExceptionType.InvalidJumpDestination => "BadJumpDestination",
            EvmExceptionType.AccessViolation => "AccessViolation",
            EvmExceptionType.StaticCallViolation => "StaticCallViolation",
            _ => "Error"
        };
    }

    public virtual GethLikeTxTrace BuildResult() => Trace;
}

public abstract class GethLikeTxTracer<TEntry> : GethLikeTxTracer where TEntry : GethTxTraceEntry, new()
{
    protected TEntry? CurrentTraceEntry { get; set; }

    protected GethLikeTxTracer(GethTraceOptions options) : base(options) { }

    public override void StartOperation(int depth, long gas, Instruction opcode, int pc, bool isPostMerge = false)
    {
        if (CurrentTraceEntry is not null)
            AddTraceEntry(CurrentTraceEntry);

        CurrentTraceEntry = CreateTraceEntry(opcode);
        CurrentTraceEntry.Depth = depth;
        CurrentTraceEntry.Gas = gas;
        CurrentTraceEntry.Opcode = opcode.GetName(isPostMerge);
        CurrentTraceEntry.ProgramCounter = pc;
    }

    public override void ReportOperationError(EvmExceptionType error) => CurrentTraceEntry.Error = GetErrorDescription(error);

    public override void ReportOperationRemainingGas(long gas) => CurrentTraceEntry.GasCost = CurrentTraceEntry.Gas - gas;

    public override void SetOperationMemorySize(ulong newSize) => CurrentTraceEntry.UpdateMemorySize(newSize);

    public override void SetOperationStack(TraceStack stack)
    {
        CurrentTraceEntry.Stack = stack.ToHexWordList();
    }

    public override void SetOperationMemory(TraceMemory memoryTrace)
    {
        if (IsTracingFullMemory)
            CurrentTraceEntry.Memory = memoryTrace.ToHexWordList();
    }

    public override GethLikeTxTrace BuildResult()
    {
        if (CurrentTraceEntry is not null)
            AddTraceEntry(CurrentTraceEntry);

        return base.BuildResult();
    }

    protected virtual void AddTraceEntry(TEntry entry) => Trace.Entries.Add(entry);

    protected virtual TEntry CreateTraceEntry(Instruction opcode) => new();
}
