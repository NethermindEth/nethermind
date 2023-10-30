// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.Evm.Tracing.GethStyle;

public abstract class GethLikeTxTracer<TEntry> : TxTracer where TEntry : GethTxTraceEntry, new()
{
    protected GethLikeTxTracer(GethTraceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        IsTracingFullMemory = options.EnableMemory;
        IsTracingOpLevelStorage = !options.DisableStorage;
        IsTracingStack = !options.DisableStack;
        IsTracing = IsTracing || IsTracingFullMemory;
    }

    public sealed override bool IsTracingOpLevelStorage { get; protected set; }
    public override bool IsTracingReceipt => true;
    public sealed override bool IsTracingMemory { get; protected set; }
    public override bool IsTracingInstructions => true;
    public sealed override bool IsTracingStack { get; protected set; }
    protected bool IsTracingFullMemory { get; }
    protected TEntry? CurrentTraceEntry { get; set; }
    protected GethLikeTxTrace Trace { get; } = new();

    public override void MarkAsSuccess(Address recipient, long gasSpent, byte[] output, LogEntry[] logs, Hash256? stateRoot = null)
    {
        Trace.ReturnValue = output;
    }

    public override void MarkAsFailed(Address recipient, long gasSpent, byte[]? output, string error, Hash256? stateRoot = null)
    {
        Trace.Failed = true;
        Trace.ReturnValue = output ?? Array.Empty<byte>();
    }

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

    protected string? GetErrorDescription(EvmExceptionType evmExceptionType)
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

    public virtual GethLikeTxTrace BuildResult()
    {
        if (CurrentTraceEntry is not null)
            AddTraceEntry(CurrentTraceEntry);

        return Trace;
    }

    protected virtual void AddTraceEntry(TEntry entry) => Trace.Entries.Add(entry);

    protected virtual TEntry CreateTraceEntry(Instruction opcode) => new();
}
