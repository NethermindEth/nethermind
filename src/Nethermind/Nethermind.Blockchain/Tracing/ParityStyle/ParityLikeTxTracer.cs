// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;

namespace Nethermind.Blockchain.Tracing.ParityStyle;

public class ParityLikeTxTracer : TxTracer, IFrameTxReceiptTracer
{
    private const string SkippedFrameError = "frame skipped";

    private Transaction? _tx;
    private readonly ParityTraceTypes _parityTraceTypes;
    protected readonly ParityLikeTxTrace _trace;

    private readonly Stack<ParityTraceAction> _actionStack = new();
    private ParityTraceAction? _currentAction;

    private ParityVmOperationTrace? _currentOperation;
    private readonly List<byte[]> _currentPushList = [];

    private readonly Stack<(ParityVmTrace VmTrace, List<ParityVmOperationTrace> Ops)> _vmTraceStack = new();
    private (ParityVmTrace VmTrace, List<ParityVmOperationTrace> Ops) _currentVmTrace;

    protected bool _treatGasParityStyle;
    protected bool _gasAlreadySetForCurrentOp;
    private protected ulong _actionGasLeft;

    private bool _isFrameTx;
    private ParityTraceAction? _frameTxRoot;
    private ParityTraceAction? _lastFrameAction;
    private ParityVmOperationTrace? _lastFrameOperation;
    private ParityTraceAction?[]? _frameActions;
    private EvmExceptionType?[]? _frameErrors;
    private bool _framesOrdered;

    public ParityLikeTxTracer(Block block, Transaction? tx, ParityTraceTypes parityTraceTypes)
    {
        _parityTraceTypes = parityTraceTypes;

        _tx = tx;
        _isFrameTx = tx?.Type == TxType.FrameTx;
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
        }

        if ((_parityTraceTypes & ParityTraceTypes.VmTrace) != 0)
        {
            IsTracingActions = true;
            IsTracingInstructions = true;
            IsTracingCode = true;
        }
    }

    public sealed override bool IsTracingActions { get; protected set; }
    public sealed override bool IsTracingReceipt => true;
    public override bool IsCollectingLogs => false;
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

    public virtual ParityLikeTxTrace BuildResult()
    {
        CloseFrameTxRoot();

        if ((_parityTraceTypes & ParityTraceTypes.Trace) == ParityTraceTypes.None)
        {
            _trace.Action = null;
        }

        return _trace;
    }

    protected virtual ParityTraceAction RentAction() => new();

    protected virtual CappedArray<int> RentTraceAddress(int length) =>
        length == 0 ? CappedArray<int>.Empty : new CappedArray<int>(new int[length], length);

    protected virtual ParityAccountStateChange RentAccountStateChange() => new();

    protected virtual Dictionary<UInt256, ParityStateChange<byte[]>> RentStorageDictionary() => [];

    protected virtual ParityStateChange<byte[]> RentByteStateChange(byte[]? before, byte[]? after) => new(before, after);

    protected virtual ParityStateChange<UInt256?> RentNullableUInt256StateChange(UInt256? before, UInt256? after) => new(before, after);

    protected virtual CappedArray<byte> CopyInput(ReadOnlyMemory<byte> input)
    {
        if (input.IsEmpty) return CappedArray<byte>.Empty;
        byte[] copy = new byte[input.Length];
        input.Span.CopyTo(copy);
        return new CappedArray<byte>(copy, copy.Length);
    }

    protected virtual void ReturnInputBytes(in CappedArray<byte> input) { }

    protected void ResetTracerState(Block block, Transaction? tx)
    {
        _tx = tx;
        _isFrameTx = tx?.Type == TxType.FrameTx;
        _trace.TransactionHash = tx?.Hash;
        _trace.TransactionPosition = tx is null ? null : Array.IndexOf(block.Transactions!, tx);
        _trace.BlockNumber = block.Number;
        _trace.BlockHash = block.Hash!;
        _trace.Output = null;
        _trace.Action = null;
        _trace.VmTrace = null;
        _trace.StateChanges?.Clear();

        _actionStack.Clear();
        _currentAction = null;
        _currentOperation = null;
        _currentPushList.Clear();
        _vmTraceStack.Clear();
        _currentVmTrace = (null!, null!);
        _treatGasParityStyle = false;
        _gasAlreadySetForCurrentOp = false;
        _actionGasLeft = 0;
        _frameTxRoot = null;
        _lastFrameAction = null;
        _lastFrameOperation = null;
        _frameActions = null;
        _frameErrors = null;
        _framesOrdered = false;
    }

    private void PushAction(ParityTraceAction action)
    {
        if (_currentAction is not null)
        {
            int parentLen = _currentAction.TraceAddress.Length;
            CappedArray<int> traceAddress = RentTraceAddress(parentLen + 1);
            ReadOnlySpan<int> parentSpan = _currentAction.TraceAddress.AsSpan();
            Span<int> childSpan = traceAddress.AsSpan();
            parentSpan.CopyTo(childSpan);
            childSpan[parentLen] = _currentAction.Subtraces.Count;
            action.TraceAddress = traceAddress;
            if (_currentAction == _frameTxRoot)
            {
                // A frame is a top-level invocation, so a precompile frame is kept like a top-level precompile call.
                action.IncludeInTrace = true;
                _lastFrameAction = action;
            }

            if (action.IncludeInTrace)
            {
                _currentAction.Subtraces.Add(action);
            }
        }
        else
        {
            _trace.Action = action;
            action.TraceAddress = CappedArray<int>.Empty;
        }

        _actionStack.Push(action);
        _currentAction = action;

        OnEnterVmFrame(action);
    }

    protected virtual void OnEnterVmFrame(ParityTraceAction action)
    {
        if (!IsTracingInstructions) return;

        if (IsFrameLevel(action))
        {
            // Each frame hangs off a synthetic operation of the root, as a callee hangs off its CALL.
            _lastFrameOperation = new ParityVmOperationTrace { Pc = action.TraceAddress.AsSpan()[0], Cost = action.Gas };
            _currentVmTrace.Ops.Add(_lastFrameOperation);
            _currentOperation = _lastFrameOperation;
        }

        (ParityVmTrace VmTrace, List<ParityVmOperationTrace> Ops) currentVmTrace = (new ParityVmTrace(),
            new List<ParityVmOperationTrace>());
        if (_currentOperation is not null && action.Type != "suicide")
        {
            _currentOperation.Sub = currentVmTrace.VmTrace;
        }

        _vmTraceStack.Push(currentVmTrace);
        _currentVmTrace = currentVmTrace;
        _trace.VmTrace ??= _currentVmTrace.VmTrace;
    }

    private void PopAction()
    {
        ParityTraceAction popped = _actionStack.Peek();
        OnLeaveVmFrame(popped);

        _actionStack.Pop();
        _currentAction = _actionStack.Count == 0 ? null : _actionStack.Peek();
    }

    protected virtual void OnLeaveVmFrame(ParityTraceAction action)
    {
        if (!IsTracingInstructions) return;

        _currentVmTrace.VmTrace.Operations = _currentVmTrace.Ops;
        _vmTraceStack.Pop();
        _currentVmTrace = _vmTraceStack.Count == 0 ? (null, null) : _vmTraceStack.Peek();
        _currentOperation = _currentVmTrace.Ops?.Last();
        _gasAlreadySetForCurrentOp = false;

        if (IsFrameLevel(action))
        {
            _lastFrameOperation!.Used = _actionGasLeft;
            _treatGasParityStyle = false;
        }
        else if (action.Type != "suicide")
        {
            _treatGasParityStyle = true;
        }
    }

    /// <summary>Whether <paramref name="action"/> is an EIP-8141 frame, the child of the synthetic transaction root.</summary>
    private protected bool IsFrameLevel(ParityTraceAction action) => _frameTxRoot is not null && action.TraceAddress.Length == 1;

    /// <summary>Opens the synthetic root an EIP-8141 frame transaction's frames hang off, as it has no single top-level call.</summary>
    private void EnsureFrameTxRoot()
    {
        if (_frameTxRoot is not null) return;
        _frameTxRoot = CreateRootActionFromTx();
        PushAction(_frameTxRoot);
    }

    private void CloseFrameTxRoot()
    {
        if (_frameTxRoot is not null && _currentAction == _frameTxRoot)
        {
            PopAction();
        }
    }

    /// <inheritdoc/>
    void IFrameTxReceiptTracer.ReportFrameEnd(int frameIndex, EvmExceptionType? error)
    {
        TxFrame[]? frames = _tx?.Frames;
        if (!_isFrameTx || !IsTracingActions || frames is null || (uint)frameIndex >= (uint)frames.Length) return;

        _frameActions ??= new ParityTraceAction?[frames.Length];
        _frameErrors ??= new EvmExceptionType?[frames.Length];
        _frameActions[frameIndex] = _lastFrameAction;
        _frameErrors[frameIndex] = error;
        _lastFrameAction = null;
        OnFrameEnd(frameIndex);
    }

    /// <summary>Called once the frame at <paramref name="frameIndex"/> completes, whether or not it entered the VM.</summary>
    private protected virtual void OnFrameEnd(int frameIndex)
    {
        if (_lastFrameOperation is null) return;
        _lastFrameOperation.Pc = frameIndex;
        _lastFrameOperation = null;
    }

    /// <inheritdoc/>
    /// <remarks>Places frame <c>i</c> at trace address <c>[i]</c>, rebuilding from the transaction the frames that
    /// never entered the VM; gas and gasUsed are the frame's limit and its receipt's spend, as in callTracer.</remarks>
    void IFrameTxReceiptTracer.ReportFrameTxReceipt(Address payer, TxFrameReceipt[] frameReceipts)
    {
        // The validation-prefix simulation reports no receipts, which pin no frame's outcome.
        if (!_isFrameTx || !IsTracingActions || frameReceipts.Length == 0 || _framesOrdered) return;
        _framesOrdered = true;

        EnsureFrameTxRoot();
        TxFrame[] frames = _tx!.Frames!;
        int frameCount = Math.Min(frameReceipts.Length, frames.Length);
        // The processor reports every frame's end before the receipts, so each dispatched frame is claimed here.
        List<ParityTraceAction> ordered = new(frameCount);
        for (int i = 0; i < frameCount; i++)
        {
            ParityTraceAction action = _frameActions?[i] ?? BuildUndispatchedFrameAction(frames[i], frameReceipts[i], _frameErrors?[i]);
            action.Gas = frames[i].GasLimit;
            action.Result?.GasUsed = frameReceipts[i].GasUsed;
            MoveToFramePosition(action, i);
            ordered.Add(action);
        }

        _frameTxRoot!.Subtraces = ordered;
    }

    private ParityTraceAction BuildUndispatchedFrameAction(TxFrame frame, TxFrameReceipt receipt, EvmExceptionType? error)
    {
        ParityTraceAction action = RentAction();
        action.TraceAddress = RentTraceAddress(1);
        action.Type = "call";
        action.CallType = frame.Mode is FrameMode.Verify or FrameMode.PostTx ? "staticcall" : "call";
        action.From = frame.Mode == FrameMode.Sender ? _tx!.SenderAddress : Eip8141Constants.EntryPointAddress;
        action.To = frame.Target ?? _tx!.SenderAddress;
        action.Value = frame.Value;
        action.Input = CopyInput(frame.Data);
        action.Error = receipt.Status switch
        {
            TxFrameReceipt.StatusSuccess => null,
            TxFrameReceipt.StatusSkipped => SkippedFrameError,
            _ => GetErrorDescription(error ?? EvmExceptionType.Revert)
        };

        if (action.Error is null)
        {
            action.Result!.Output = [];
        }
        else
        {
            action.Result = null;
        }

        return action;
    }

    private static void MoveToFramePosition(ParityTraceAction action, int position)
    {
        Span<int> traceAddress = action.TraceAddress.AsSpan();
        if (traceAddress[0] == position) return;
        traceAddress[0] = position;
        foreach (ParityTraceAction subtrace in action.Subtraces)
        {
            MoveToFramePosition(subtrace, position);
        }
    }

    public override void MarkAsSuccess(Address recipient, in GasConsumed gasSpent, byte[] output, LogEntry[] logs,
        Hash256? stateRoot = null)
    {
        FinishFrameTxRoot();

        if (_currentAction is not null)
        {
            throw new InvalidOperationException($"Closing trace at level {_currentAction.TraceAddress.Length}");
        }

        if (!IsTracingActions)
        {
            _trace.Output = output;
            return;
        }

        _trace.Action ??= CreateRootActionFromTx();

        if (_trace.Action.TraceAddress.Length == 0)
        {
            _trace.Output = output;
        }

        _trace.Action.Result!.Output = output;
        if (_isFrameTx)
        {
            _trace.Action.Result.GasUsed = gasSpent.SpentGas;
        }
    }

    public override void MarkAsFailed(Address recipient, in GasConsumed gasSpent, byte[] output, string? error,
        Hash256? stateRoot = null)
    {
        FinishFrameTxRoot();

        if (_currentAction is not null)
        {
            throw new InvalidOperationException($"Closing trace at level {_currentAction!.TraceAddress.Length}");
        }

        _trace.Output = output;

        if (!IsTracingActions) return;

        if (_trace.Action is null)
        {
            ParityTraceAction action = CreateRootActionFromTx();
            action.Error = error;
            _trace.Action = action;
        }
        else if (_isFrameTx)
        {
            // A frame transaction fails at the transaction level, so the reason is the root's.
            _trace.Action.Error = error;
            _trace.Action.Result = null;
        }
    }

    private void FinishFrameTxRoot()
    {
        if (!_isFrameTx || !IsTracingActions) return;
        EnsureFrameTxRoot();
        CloseFrameTxRoot();
    }

    private ParityTraceAction CreateRootActionFromTx()
    {
        ParityTraceAction action = RentAction();
        if (_isFrameTx)
        {
            action.Type = "call";
            action.CallType = "call";
            action.From = _tx!.SenderAddress;
            action.To = Eip8141Constants.EntryPointAddress;
            action.Input = CappedArray<byte>.Empty;
            action.Gas = _tx.GasLimit;
            return action;
        }

        action.From = _tx!.SenderAddress;
        action.To = _tx.To;
        action.Value = _tx.Value;
        action.Input = CopyInput(_tx.Data);
        action.Gas = _tx.GasLimit;
        action.CallType = _tx.IsMessageCall ? "call" : "init";
        return action;
    }

    public override void StartOperation(int pc, Instruction opcode, ulong gas, in ExecutionEnvironment env)
    {
        ParityVmOperationTrace operationTrace = new();
        _gasAlreadySetForCurrentOp = false;
        operationTrace.Pc = pc;
        operationTrace.Cost = gas;
        _currentOperation = operationTrace;
        _currentPushList.Clear();
        _currentVmTrace.Ops.Add(operationTrace);
    }

    public override void ReportOperationError(EvmExceptionType error)
    {
        if (error != EvmExceptionType.InvalidJumpDestination &&
            error != EvmExceptionType.NotEnoughBalance)
        {
            _currentVmTrace.Ops.Remove(_currentOperation);
        }
    }

    public override void ReportOperationRemainingGas(ulong gas)
    {
        if (!_gasAlreadySetForCurrentOp && _currentOperation is not null)
        {
            _gasAlreadySetForCurrentOp = true;

            _currentOperation.Cost -= (_treatGasParityStyle ? 0UL : gas);

            // based on Parity behaviour - adding stipend to the gas cost
            if (_currentOperation.Cost == 7400UL)
            {
                _currentOperation.Cost = 9700UL;
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

    public override void ReportOperationStorageChange(in ReadOnlySpan<byte> key, in ReadOnlySpan<byte> value) =>
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

        value.Balance = RentNullableUInt256StateChange(before, after);
    }

    public override void ReportCodeChange(Address address, byte[]? before, byte[]? after)
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

        value.Code = RentByteStateChange(before, after);
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

        value.Nonce = RentNullableUInt256StateChange(before, after);
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

        change = RentByteStateChange(before, after);
    }

    public override void ReportAction(ulong gas, UInt256 value, Address from, Address to, ReadOnlyMemory<byte> input,
        ExecutionType callType, bool isPrecompileCall = false)
    {
        if (_isFrameTx && _currentAction is null)
        {
            EnsureFrameTxRoot();
        }

        ParityTraceAction action = RentAction();
        action.IsPrecompiled = isPrecompileCall;
        // ignore pre compile calls with Zero value that originates from contracts
        action.IncludeInTrace = !(isPrecompileCall && callType != ExecutionType.TRANSACTION && value.IsZero);
        action.From = from;
        action.To = to;
        action.Value = value;
        action.Input = CopyInput(input);
        action.Gas = gas;
        action.CallType = GetCallType(callType);
        action.Type = GetActionType(callType);
        action.CreationMethod = GetCreateMethod(callType);

        if (_currentOperation is not null && callType.IsAnyCreate())
        {
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

    public override void ReportActionEnd(ulong gas, ReadOnlyMemory<byte> output)
    {
        if (_currentAction!.Result is null)
        {
            throw new InvalidOperationException(
                $"{nameof(ReportActionEnd)} called when result is not yet prepared.");
        }

        _currentAction.Result.Output = output.ToArray();
        _currentAction.Result.GasUsed = _currentAction.Gas - gas;
        _actionGasLeft = gas;
        PopAction();
    }

    public override void ReportActionError(EvmExceptionType evmExceptionType)
    {
        _actionGasLeft = 0;
        HandleActionError(evmExceptionType);
    }

    public override void ReportActionRevert(ulong gas, ReadOnlyMemory<byte> output)
    {
        _actionGasLeft = gas;
        HandleActionError(EvmExceptionType.Revert);
    }

    private void HandleActionError(EvmExceptionType evmExceptionType)
    {
        _currentAction!.Result = null;
        _currentAction.Error = GetErrorDescription(evmExceptionType);
        PopAction();
    }

    public override void ReportActionEnd(ulong gas, Address deploymentAddress, ReadOnlyMemory<byte> deployedCode)
    {
        if (_currentAction!.Result is null)
        {
            throw new InvalidOperationException(
                $"{nameof(ReportActionEnd)} called when result is not yet prepared.");
        }

        _currentAction.Result.Address = deploymentAddress;
        _currentAction.Result.Code = deployedCode.ToArray();
        _currentAction.Result.GasUsed = _currentAction.Gas - gas;
        _actionGasLeft = gas;
        PopAction();
    }

    public override void ReportByteCode(ReadOnlyMemory<byte> byteCode) =>
        // TODO: use memory pool?
        _currentVmTrace.VmTrace.Code = byteCode.ToArray();

    public override void ReportGasUpdateForVmTrace(ulong refund, ulong gasAvailable)
    {
        if (_currentOperation is null) return;
        _currentOperation.Used = gasAvailable;

        if (!_gasAlreadySetForCurrentOp)
        {
            _gasAlreadySetForCurrentOp = true;
            _currentOperation.Push = _currentPushList.ToArray();
            _treatGasParityStyle = false;
        }
    }
}
