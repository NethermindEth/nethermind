// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;

namespace Nethermind.Blockchain.Tracing.GethStyle;

public abstract class GethLikeTxTracer : TxTracer, ITraceImplicitStop
{
    private readonly RefundTracker? _refundTracker;

    protected GethLikeTxTracer(GethTraceOptions options, long? destroyRefund = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (destroyRefund.HasValue)
            _refundTracker = new(destroyRefund.Value);

        IsTracingOpLevelStorage = !options.DisableStorage;
        IsTracingStack = !options.DisableStack;
        IsTracingFullMemory = options.EnableMemory;
        IsTracingReturnData = options.EnableReturnData;
        IsTracing = IsTracing || IsTracingFullMemory;
    }

    private GethLikeTxTrace? _trace;
    protected GethLikeTxTrace Trace => _trace ??= CreateTrace();
    protected virtual GethLikeTxTrace CreateTrace() => new();

    protected void ResetTrace() => _trace = null;
    public override bool IsTracingReceipt => true;
    public sealed override bool IsTracingOpLevelStorage { get; protected set; }
    public sealed override bool IsTracingMemory { get; protected set; }
    public override bool IsTracingInstructions => true;
    public sealed override bool IsTracingStack { get; protected set; }
    protected bool IsTracingFullMemory { get; }
    protected long CurrentRefund => _refundTracker?.Refund ?? 0;

    public override void MarkAsSuccess(
        Address recipient, in GasConsumed gasSpent, byte[] output, LogEntry[] logs, Hash256? stateRoot = null) =>
        Trace.ReturnValue = output;

    public override void MarkAsFailed(Address recipient, in GasConsumed gasSpent, byte[] output, string? error, Hash256? stateRoot = null)
    {
        Trace.Failed = true;
        Trace.ReturnValue = output ?? [];
    }

    protected static string? GetErrorDescription(EvmExceptionType evmExceptionType) => evmExceptionType switch
    {
        EvmExceptionType.None => null,
        EvmExceptionType.BadInstruction => "BadInstruction",
        EvmExceptionType.StackOverflow => "StackOverflow",
        EvmExceptionType.StackUnderflow => "StackUnderflow",
        EvmExceptionType.OutOfGas => "OutOfGas",
        EvmExceptionType.InvalidJumpDestination => "BadJumpDestination",
        EvmExceptionType.AccessViolation => "AccessViolation",
        EvmExceptionType.StaticCallViolation => "StaticCallViolation",
        _ => "Error"
    };

    public override void ReportRefund(long refund) => _refundTracker?.Add(refund);

    public override void ReportSelfDestruct(Address address, UInt256 balance, Address refundAddress) =>
        _refundTracker?.CreditSelfDestruct(address);

    public override void ReportAction(ulong gas, UInt256 value, Address from, Address to, ReadOnlyMemory<byte> input, ExecutionType callType, bool isPrecompileCall = false)
    {
        base.ReportAction(gas, value, from, to, input, callType, isPrecompileCall);
        _refundTracker?.TakeSnapshot();
    }

    public override void ReportActionEnd(ulong gas, ReadOnlyMemory<byte> output)
    {
        base.ReportActionEnd(gas, output);
        _refundTracker?.CommitSnapshot();
    }

    public override void ReportActionEnd(ulong gas, Address deploymentAddress, ReadOnlyMemory<byte> deployedCode)
    {
        base.ReportActionEnd(gas, deploymentAddress, deployedCode);
        _refundTracker?.CommitSnapshot();
    }

    public override void ReportActionRevert(ulong gasLeft, ReadOnlyMemory<byte> output)
    {
        base.ReportActionRevert(gasLeft, output);
        _refundTracker?.RestoreSnapshot();
    }

    public override void ReportActionError(EvmExceptionType evmExceptionType)
    {
        base.ReportActionError(evmExceptionType);
        _refundTracker?.RestoreSnapshot();
    }

    protected void ResetRefund() => _refundTracker?.Reset();

    public virtual GethLikeTxTrace BuildResult() => Trace;
}

public abstract class GethLikeTxTracer<TEntry>(GethTraceOptions options, long? destroyRefund = null) : GethLikeTxTracer(options, destroyRefund) where TEntry : GethTxTraceEntry, new()
{
    protected TEntry? CurrentTraceEntry { get; set; }

    private bool _gasCostAlreadySetForCurrentOp;

    public override void StartOperation(int pc, Instruction opcode, ulong gas, in ExecutionEnvironment env)
    {
        if (CurrentTraceEntry is not null)
        {
            AddTraceEntry(CurrentTraceEntry);
        }

        CurrentTraceEntry = CreateTraceEntry(opcode);
        CurrentTraceEntry.Depth = env.GetGethTraceDepth();
        CurrentTraceEntry.Gas = gas;
        CurrentTraceEntry.Opcode = OpcodeJsonNames.GetName(opcode);
        CurrentTraceEntry.ProgramCounter = pc;
        _gasCostAlreadySetForCurrentOp = false;
    }

    public override void ReportOperationError(EvmExceptionType error)
    {
        if (CurrentTraceEntry is not null)
            CurrentTraceEntry.Error = GetErrorDescription(error);
    }

    public override void ReportOperationRemainingGas(ulong gas)
    {
        if (!_gasCostAlreadySetForCurrentOp && CurrentTraceEntry is not null)
        {
            CurrentTraceEntry.GasCost = CurrentTraceEntry.Gas - gas;
            _gasCostAlreadySetForCurrentOp = true;
        }
    }

    public override void SetOperationMemorySize(ulong newSize) => CurrentTraceEntry?.UpdateMemorySize(newSize);

    public override void SetOperationStack(TraceStack stack)
    {
        if (CurrentTraceEntry is not null)
            CurrentTraceEntry.Stack = stack.ToRawBytes();
    }

    public override void SetOperationMemory(TraceMemory memoryTrace)
    {
        if (IsTracingFullMemory && CurrentTraceEntry is not null)
            CurrentTraceEntry.Memory = memoryTrace.ToRawWordBytes();
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
