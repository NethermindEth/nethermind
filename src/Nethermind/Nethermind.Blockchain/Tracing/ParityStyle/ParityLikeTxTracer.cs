// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;

namespace Nethermind.Blockchain.Tracing.ParityStyle;

public class ParityLikeTxTracer : TxTracer
{
    protected Transaction? _tx;
    protected readonly ParityTraceTypes _parityTraceTypes;
    protected ParityLikeTxTrace _trace;

    protected readonly Stack<ParityTraceAction> _actionStack = new();
    protected ParityTraceAction? _currentAction;

    protected ParityVmOperationTrace? _currentOperation;
    protected readonly List<byte[]> _currentPushList = [];

    protected readonly Stack<(ParityVmTrace VmTrace, List<ParityVmOperationTrace> Ops)> _vmTraceStack = new();
    protected (ParityVmTrace VmTrace, List<ParityVmOperationTrace> Ops) _currentVmTrace;

    protected bool _treatGasParityStyle; // strange cost calculation from parity
    protected bool _gasAlreadySetForCurrentOp; // workaround for jump destination errors

    public ParityLikeTxTracer(Block block, Transaction? tx, ParityTraceTypes parityTraceTypes)
    {
        _parityTraceTypes = parityTraceTypes;

        _tx = tx;
        _trace = new ParityLikeTxTrace
        {
            TransactionHash = tx?.Hash,
            TransactionPosition = tx is null ? null : Array.IndexOf(block.Transactions!, tx),
            BlockNumber = block.Number,
            BlockHash = block.Hash!
        };

        if ((_parityTraceTypes & ParityTraceTypes.StateDiff) != 0)
        {
            IsTracingState = true;
            IsTracingStorage = true;
            _trace.StateChanges = [];
        }

        if ((_parityTraceTypes & ParityTraceTypes.Trace) != 0)
        {
            IsTracingActions = true;
            IsTracingReceipt = true;
        }

        if ((_parityTraceTypes & ParityTraceTypes.VmTrace) != 0)
        {
            IsTracingActions = true;
            IsTracingInstructions = true;
            IsTracingCode = true;
            IsTracingReceipt = true;
        }
    }

    public sealed override bool IsTracingActions { get; protected set; }
    public sealed override bool IsTracingReceipt { get; protected set; }
    public sealed override bool IsTracingInstructions { get; protected set; }
    public sealed override bool IsTracingCode { get; protected set; }
    public sealed override bool IsTracingState { get; protected set; }
    public sealed override bool IsTracingStorage { get; protected set; }

    private static string GetCallType(ExecutionType executionType) => executionType switch
    {
        ExecutionType.CREATE or ExecutionType.CREATE2 => "create",
        ExecutionType.CALL or ExecutionType.TRANSACTION => "call",
        ExecutionType.DELEGATECALL => "delegatecall",
        ExecutionType.STATICCALL => "staticcall",
        ExecutionType.CALLCODE => "callcode",
        _ => throw new NotSupportedException($"Parity trace call type is undefined for {executionType}")
    };

    private static string GetActionType(ExecutionType executionType) => executionType switch
    {
        ExecutionType.CREATE or ExecutionType.CREATE2 => "create",
        _ => "call"
    };

    private static string? GetErrorDescription(EvmExceptionType evmExceptionType) => evmExceptionType switch
    {
        EvmExceptionType.None => null,
        EvmExceptionType.BadInstruction => "Bad instruction",
        EvmExceptionType.StackOverflow => "Stack overflow",
        EvmExceptionType.StackUnderflow => "Stack underflow",
        EvmExceptionType.OutOfGas => "Out of gas",
        EvmExceptionType.InvalidJumpDestination => "Bad jump destination",
        EvmExceptionType.AccessViolation => "Access violation",
        EvmExceptionType.StaticCallViolation => "Static call violation",
        EvmExceptionType.Revert => "Reverted",
        _ => "Error",
    };

    public ParityLikeTxTrace BuildResult()
    {
        if ((_parityTraceTypes & ParityTraceTypes.Trace) == ParityTraceTypes.None)
        {
            _trace.Action = null;
        }

        return _trace;
    }

    /// <summary>
    /// Reuse this tracer instance for a new transaction in the same block by resetting
    /// every per-tx field in place. Called by pooled callers (e.g.
    /// <see cref="StreamingParityLikeTxTracer"/>'s block tracer); keeps the tracer
    /// allocation, the action/vm-trace stacks, the push list, and the streaming frame
    /// pool alive across transactions.
    /// </summary>
    protected void ResetTracerState(Block block, Transaction? tx)
    {
        _tx = tx;
        _trace.TransactionHash = tx?.Hash;
        _trace.TransactionPosition = tx is null ? null : Array.IndexOf(block.Transactions!, tx);
        _trace.BlockNumber = block.Number;
        _trace.BlockHash = block.Hash!;
        _trace.Output = null;
        _trace.Action = null;
        _trace.VmTrace = null;

        if ((_parityTraceTypes & ParityTraceTypes.StateDiff) != 0)
        {
            if (_trace.StateChanges is null)
            {
                _trace.StateChanges = [];
            }
            else
            {
                _trace.StateChanges.Clear();
            }
        }
        else
        {
            _trace.StateChanges = null;
        }

        _actionStack.Clear();
        _currentAction = null;
        _currentOperation = null;
        _currentPushList.Clear();
        _vmTraceStack.Clear();
        _currentVmTrace = (null!, null!);
        _treatGasParityStyle = false;
        _gasAlreadySetForCurrentOp = false;
    }

    /// <summary>
    /// Provides a <see cref="ParityTraceAction"/> instance for the next call frame. The
    /// default implementation allocates a fresh one; subclasses (e.g. the streaming
    /// tracer) may override to hand back a pool-rented + reset instance. The returned
    /// action is in a "clean" state — every field is the same as a freshly-constructed
    /// instance.
    /// </summary>
    protected virtual ParityTraceAction RentAction() => new();

    /// <summary>Provides a <see cref="ParityAccountStateChange"/> instance for a newly touched account; default allocates fresh, subclasses may pool.</summary>
    protected virtual ParityAccountStateChange RentAccountStateChange() => new();

    /// <summary>Provides the per-account storage map; default allocates fresh, subclasses may pool.</summary>
    protected virtual Dictionary<UInt256, ParityStateChange<byte[]>> RentStorageDictionary() => [];


    protected virtual void PushAction(ParityTraceAction action)
    {
        if (_currentAction is not null)
        {
            int parentLen = _currentAction!.TraceAddress!.Length;
            action.TraceAddress = new int[parentLen + 1];
            for (int i = 0; i < parentLen; i++)
            {
                action.TraceAddress[i] = _currentAction.TraceAddress[i];
            }

            action.TraceAddress[parentLen] = _currentAction.IncludedSubtraceCount;
            if (action.IncludeInTrace)
            {
                RegisterSubtrace(_currentAction, action);
                _currentAction.IncludedSubtraceCount++;
            }
        }
        else
        {
            _trace.Action = action;
            action.TraceAddress = [];
        }

        _actionStack.Push(action);
        _currentAction = action;

        if (IsTracingInstructions)
        {
            PushVmTraceFrame(action);
        }
    }

    /// <summary>
    /// Records <paramref name="child"/> under <paramref name="parent"/> for later JSON
    /// emission. The default implementation appends to <see cref="ParityTraceAction.Subtraces"/>
    /// (required by the buffered <c>ParityTxTraceFromReplay</c> / <c>ParityTxTraceFromStore</c>
    /// emit paths which iterate the tree). Streaming subclasses override to skip the list
    /// when they're emitting actions post-order at <see cref="PopAction"/> time and don't
    /// need to hold the tree in memory.
    /// </summary>
    protected virtual void RegisterSubtrace(ParityTraceAction parent, ParityTraceAction child)
        => parent.Subtraces.Add(child);

    /// <summary>
    /// Pushes a new vmTrace frame onto the stack and links the parent's current operation
    /// to the new frame's <see cref="ParityVmTrace"/>. Override to redirect frame handling
    /// (e.g. to emit JSON directly instead of building the tree).
    /// </summary>
    protected virtual void PushVmTraceFrame(ParityTraceAction action)
    {
        (ParityVmTrace VmTrace, List<ParityVmOperationTrace> Ops) currentVmTrace = (new ParityVmTrace(),
            new List<ParityVmOperationTrace>());
        if (_currentOperation is not null)
        {
            if (action.Type != "suicide")
            {
                _currentOperation.Sub = currentVmTrace.VmTrace;
            }
        }

        _vmTraceStack.Push(currentVmTrace);
        _currentVmTrace = currentVmTrace;
        _trace.VmTrace ??= _currentVmTrace.VmTrace;
    }

    protected virtual void PopAction()
    {
        if (IsTracingInstructions)
        {
            PopVmTraceFrame();
        }

        ParityTraceAction popped = _actionStack.Pop();
        _currentAction = _actionStack.Count == 0 ? null : _actionStack.Peek();
        OnActionPopped(popped);
    }

    /// <summary>
    /// Called after an action has been popped off the action stack. Default does nothing;
    /// streaming subclasses use this to emit the popped action's JSON in post-order (the
    /// action's <see cref="ParityTraceAction.IncludedSubtraceCount"/> and its
    /// <see cref="ParityTraceAction.Result"/> / <see cref="ParityTraceAction.Error"/> are
    /// final at this point) and then return it to the pool.
    /// </summary>
    protected virtual void OnActionPopped(ParityTraceAction action) { }

    /// <summary>
    /// Finalizes the current vmTrace frame and pops it. Restores <see cref="_currentOperation"/>
    /// to the parent frame's last op. Override to redirect frame teardown (e.g. to close a
    /// streamed JSON envelope rather than materializing the operations array).
    /// </summary>
    protected virtual void PopVmTraceFrame()
    {
        _currentVmTrace.VmTrace.Operations = _currentVmTrace.Ops.ToArray();
        _vmTraceStack.Pop();
        _currentVmTrace = _vmTraceStack.Count == 0 ? (null, null) : _vmTraceStack.Peek();
        _currentOperation = _currentVmTrace.Ops?.Last();
        _gasAlreadySetForCurrentOp = false;

        if (_actionStack.Peek().Type != "suicide")
        {
            _treatGasParityStyle = true;
        }
    }

    public override void MarkAsSuccess(Address recipient, in GasConsumed gasSpent, byte[] output, LogEntry[] logs,
        Hash256? stateRoot = null)
    {
        if (_currentAction is not null)
        {
            throw new InvalidOperationException($"Closing trace at level {_currentAction.TraceAddress?.Length ?? 0}");
        }

        if (_trace.Action!.TraceAddress!.Length == 0)
        {
            _trace.Output = output;
        }

        _trace.Action!.Result!.Output = output;
    }

    public override void MarkAsFailed(Address recipient, in GasConsumed gasSpent, byte[] output, string? error,
        Hash256? stateRoot = null)
    {
        if (_currentAction is not null)
        {
            throw new InvalidOperationException($"Closing trace at level {_currentAction!.TraceAddress!.Length}");
        }

        _trace.Output = output;

        if (_trace.Action is null)
        {
            // quick tx fail (before execution) — synthesize a minimal action
            ParityTraceAction action = RentAction();
            action.From = _tx!.SenderAddress;
            action.To = _tx.To;
            action.Value = _tx.Value;
            action.Input = _tx.Data.AsArray();
            action.Gas = _tx.GasLimit;
            action.CallType = _tx.IsMessageCall ? "call" : "init";
            action.Error = error;
            _trace.Action = action;
        }
    }

    public override void StartOperation(int pc, Instruction opcode, long gas, in ExecutionEnvironment env)
    {
        ParityVmOperationTrace operationTrace = new();
        _gasAlreadySetForCurrentOp = false;
        operationTrace.Pc = pc;
        operationTrace.Cost = gas;
        // OnOperationStarted runs before _currentOperation is rebound so streaming
        // overrides can emit/discard the previous op while it is still _currentOperation.
        OnOperationStarted(operationTrace);
        _currentOperation = operationTrace;
        _currentPushList.Clear();
    }

    /// <summary>
    /// Called from <see cref="StartOperation"/> after the new <see cref="ParityVmOperationTrace"/>
    /// has been initialised. Base behavior appends it to the current vmTrace frame's ops list;
    /// override to emit the previously buffered op as JSON and discard it instead.
    /// </summary>
    protected virtual void OnOperationStarted(ParityVmOperationTrace operationTrace) => _currentVmTrace.Ops.Add(operationTrace);

    public override void ReportOperationError(EvmExceptionType error)
    {
        if (error != EvmExceptionType.InvalidJumpDestination &&
            error != EvmExceptionType.NotEnoughBalance)
        {
            OnOperationRemoved(_currentOperation);
        }
    }

    /// <summary>
    /// Called when an operation should be discarded (e.g. errors that don't surface as a
    /// real opcode in the vmTrace). Base removes it from the current frame's ops list;
    /// override to clear the in-flight streaming buffer instead.
    /// </summary>
    protected virtual void OnOperationRemoved(ParityVmOperationTrace? operationTrace) => _currentVmTrace.Ops.Remove(operationTrace);

    public override void ReportOperationRemainingGas(long gas)
    {
        if (!_gasAlreadySetForCurrentOp)
        {
            _gasAlreadySetForCurrentOp = true;

            _currentOperation!.Cost -= (_treatGasParityStyle ? 0 : gas);

            // based on Parity behaviour - adding stipend to the gas cost
            if (_currentOperation.Cost == 7400)
            {
                _currentOperation.Cost = 9700;
            }

            _currentOperation.Push = _currentPushList.ToArray();
            _currentOperation.Used = gas;

            _treatGasParityStyle = false;
        }
    }

    public override void ReportStackPush(in ReadOnlySpan<byte> stackItem) => _currentPushList.Add(stackItem.ToArray());

    public override void ReportMemoryChange(long offset, in ReadOnlySpan<byte> data)
    {
        if (data.Length != 0)
        {
            _currentOperation!.Memory = new ParityMemoryChangeTrace { Offset = offset, Data = data.ToArray() };
        }
    }

    public override void ReportStorageChange(in ReadOnlySpan<byte> key, in ReadOnlySpan<byte> value) =>
        _currentOperation!.Store = new ParityStorageChangeTrace { Key = key.ToArray(), Value = value.ToArray() };

    public override void ReportBalanceChange(Address address, UInt256? before, UInt256? after)
    {
        if (_trace.StateChanges is null)
        {
            throw new InvalidOperationException($"{nameof(ParityLikeTxTracer)} did not expect state change report.");
        }

        ref ParityAccountStateChange? value =
            ref CollectionsMarshal.GetValueRefOrAddDefault(_trace.StateChanges, address, out bool exists);
        if (!exists)
        {
            value = RentAccountStateChange();
        }
        else
        {
            before = value.Balance?.Before ?? before;
        }

        value.Balance = new ParityStateChange<UInt256?>(before, after);
    }

    public override void ReportCodeChange(Address address, byte[] before, byte[] after)
    {
        if (_trace.StateChanges is null)
        {
            throw new InvalidOperationException($"{nameof(ParityLikeTxTracer)} did not expect state change report.");
        }

        ref ParityAccountStateChange? value =
            ref CollectionsMarshal.GetValueRefOrAddDefault(_trace.StateChanges, address, out bool exists);
        if (!exists)
        {
            value = RentAccountStateChange();
        }
        else
        {
            before = value.Code?.Before ?? before;
        }

        value.Code = new ParityStateChange<byte[]>(before, after);
    }

    public override void ReportNonceChange(Address address, UInt256? before, UInt256? after)
    {
        ref ParityAccountStateChange? value =
            ref CollectionsMarshal.GetValueRefOrAddDefault(_trace.StateChanges, address, out bool exists);
        if (!exists)
        {
            value = RentAccountStateChange();
        }
        else
        {
            before = value.Nonce?.Before ?? before;
        }

        value.Nonce = new ParityStateChange<UInt256?>(before, after);
    }

    public override void ReportStorageChange(in StorageCell storageCell, byte[] before, byte[] after)
    {
        ref ParityAccountStateChange? value =
            ref CollectionsMarshal.GetValueRefOrAddDefault(_trace.StateChanges, storageCell.Address, out bool exists);
        if (!exists)
        {
            value = RentAccountStateChange();
        }

        Dictionary<UInt256, ParityStateChange<byte[]>> storage = value.Storage ??= RentStorageDictionary();
        ref ParityStateChange<byte[]>? change =
            ref CollectionsMarshal.GetValueRefOrAddDefault(storage, storageCell.Index, out exists);
        if (exists)
        {
            before = change.Before ?? before;
        }

        change = new ParityStateChange<byte[]>(before, after);
    }

    public override void ReportAction(long gas, UInt256 value, Address from, Address to, ReadOnlyMemory<byte> input,
        ExecutionType callType, bool isPrecompileCall = false)
    {
        ParityTraceAction action = RentAction();
        action.IsPrecompiled = isPrecompileCall;
        // ignore pre compile calls with Zero value that originates from contracts
        action.IncludeInTrace = !(isPrecompileCall && callType != ExecutionType.TRANSACTION && value.IsZero);
        action.From = from;
        action.To = to;
        action.Value = value;
        action.Input = input.ToArray();
        action.Gas = gas;
        action.CallType = GetCallType(callType);
        action.Type = GetActionType(callType);
        action.CreationMethod = GetCreateMethod(callType);

        if (_currentOperation is not null && callType.IsAnyCreate())
        {
            // another Parity quirkiness
            _currentOperation.Cost += gas;
        }

        PushAction(action);
    }

    private static string? GetCreateMethod(ExecutionType callType) => callType switch
    {
        ExecutionType.CREATE => "create",
        ExecutionType.CREATE2 => "create2",
        _ => null
    };

    public override void ReportSelfDestruct(Address address, UInt256 balance, Address refundAddress)
    {
        ParityTraceAction action = RentAction();
        action.From = address;
        action.To = refundAddress;
        action.Value = balance;
        action.Type = "suicide";
        PushAction(action);
        _currentAction!.Result = null;
        PopAction();
    }

    public override void ReportActionEnd(long gas, ReadOnlyMemory<byte> output)
    {
        if (_currentAction!.Result is null)
        {
            throw new InvalidOperationException(
                $"{nameof(ReportActionEnd)} called when result is not yet prepared.");
        }

        _currentAction.Result.Output = output.ToArray();
        _currentAction.Result.GasUsed = _currentAction.Gas - gas;
        PopAction();
    }

    public override void ReportActionError(EvmExceptionType evmExceptionType)
    {
        _currentAction!.Result = null;
        _currentAction.Error = GetErrorDescription(evmExceptionType);
        PopAction();
    }

    public override void ReportActionEnd(long gas, Address deploymentAddress, ReadOnlyMemory<byte> deployedCode)
    {
        if (_currentAction!.Result is null)
        {
            throw new InvalidOperationException(
                $"{nameof(ReportActionEnd)} called when result is not yet prepared.");
        }

        _currentAction.Result.Address = deploymentAddress;
        _currentAction.Result.Code = deployedCode.ToArray();
        _currentAction.Result.GasUsed = _currentAction.Gas - gas;
        PopAction();
    }

    public override void ReportByteCode(ReadOnlyMemory<byte> byteCode) =>
        // TODO: use memory pool?
        _currentVmTrace.VmTrace.Code = byteCode.ToArray();

    public override void ReportGasUpdateForVmTrace(long refund, long gasAvailable) =>
        _currentOperation!.Used = gasAvailable;
}
