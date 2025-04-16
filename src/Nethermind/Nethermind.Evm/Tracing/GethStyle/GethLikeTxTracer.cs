// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.TransactionProcessing;

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

    public override void MarkAsSuccess(Address recipient, GasConsumed gasSpent, byte[] output, LogEntry[] logs, Hash256? stateRoot = null)
    {
        Trace.ReturnValue = output;
    }

    public override void MarkAsFailed(Address recipient, GasConsumed gasSpent, byte[] output, string? error, Hash256? stateRoot = null)
    {
        Trace.Failed = true;
        Trace.ReturnValue = output ?? [];
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
    private bool _gasCostAlreadySetForCurrentOp;

    public override void StartOperation(int pc, Instruction opcode, long gas, in ExecutionEnvironment env, int codeSection = 0, int functionDepth = 0)
    {
        bool isPostMerge = env.IsPostMerge();
        if (CurrentTraceEntry is not null)
            AddTraceEntry(CurrentTraceEntry);

        CurrentTraceEntry = CreateTraceEntry(opcode);
        CurrentTraceEntry.Depth = env.GetGethTraceDepth();
        CurrentTraceEntry.Gas = gas;
        CurrentTraceEntry.Opcode = opcode.GetName(isPostMerge);
        CurrentTraceEntry.ProgramCounter = pc;
        // skip codeSection
        // skip functionDepth
        _gasCostAlreadySetForCurrentOp = false;
    }

    public override void ReportOperationError(EvmExceptionType error) => CurrentTraceEntry.Error = GetErrorDescription(error);

    public override void ReportOperationRemainingGas(long gas)
    {
        if (!_gasCostAlreadySetForCurrentOp)
        {
            CurrentTraceEntry.GasCost = CurrentTraceEntry.Gas - gas;
            _gasCostAlreadySetForCurrentOp = true;
        }
    }

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
