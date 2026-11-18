// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;

namespace Nethermind.Blockchain.Tracing.ParityStyle;

public partial class ParityLikeTxTracer : TxTracer
{
    private Transaction? _tx;
    private readonly ParityTraceTypes _parityTraceTypes;
    protected readonly ParityLikeTxTrace _trace;

    private readonly Stack<ParityTraceAction> _actionStack = new();
    private ParityTraceAction? _currentAction;

    private ParityVmOperationTrace? _currentOperation;
    private Instruction _currentOpcode;
    private readonly List<byte[]> _currentPushList = [];

    private readonly Stack<(ParityVmTrace VmTrace, List<ParityVmOperationTrace> Ops)> _vmTraceStack = new();
    private (ParityVmTrace VmTrace, List<ParityVmOperationTrace> Ops) _currentVmTrace;

    protected bool _treatGasParityStyle;
    protected bool _gasAlreadySetForCurrentOp;

    /// <summary>The default limit of the kept trace data, in bytes.</summary>
    public const long DefaultMaxRetainedBytes = 256 * 1024 * 1024;

    /// <summary>
    /// The bytes charged for each buffered <c>vmTrace</c> operation: its object (72 bytes), its slot in the operation
    /// list and the header of its push array, rounded up.
    /// </summary>
    public const int RetainedOperationBytes = 128;

    /// <summary>The bytes charged for each pushed stack item on top of its length: its array header and its slot in the push array.</summary>
    public const int RetainedStackItemBytes = 32;

    /// <summary>The smallest length charged for a stack item, as an array keeps at least 8 bytes of data.</summary>
    public const int MinStackItemBytes = 8;

    /// <summary>The bytes charged for a memory change on top of its data: its object and its array header.</summary>
    public const int RetainedMemoryChangeBytes = 56;

    /// <summary>The bytes charged for a storage change on top of its key and value: its object and the two array headers.</summary>
    public const int RetainedStorageChangeBytes = 80;

    /// <summary>The action, result, and subtrace list metadata and its parent list slot.</summary>
    public const int RetainedActionBytes = 192;
    private const int RetainedArrayHeaderBytes = 24;
    private const int RetainedAccountChangeBytes = 128;
    private const int RetainedScalarChangeBytes = 96;
    private const int RetainedByteChangeBytes = 32;
    private const int RetainedStorageDictionaryBytes = 96;
    private const int RetainedStorageSlotBytes = 80;

    private long _retainedBytes;

    private TraceDataBudget? _dataBudget;

    /// <summary>
    /// The budget that the data this tracer keeps is charged to. It is created with
    /// <see cref="DefaultMaxRetainedBytes"/> when not set.
    /// </summary>
    public TraceDataBudget DataBudget
    {
        get => _dataBudget ??= new TraceDataBudget(DefaultMaxRetainedBytes);
        init => _dataBudget = value;
    }

    /// <summary>The position in its block of the transaction traced now; null when no transaction is.</summary>
    public int? TransactionPosition => _trace.TransactionPosition;

    /// <param name="spec">The block's spec, which prices an EIP-8141 frame transaction's gas budget for its root;
    /// without it the root reports only the frame limits.</param>
    public ParityLikeTxTracer(Block block, Transaction? tx, ParityTraceTypes parityTraceTypes, IReleaseSpec? spec = null)
    {
        _parityTraceTypes = parityTraceTypes;
        _spec = spec;

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
        }

        if ((_parityTraceTypes & ParityTraceTypes.VmTrace) != 0)
        {
            IsTracingActions = true;
            IsTracingInstructions = true;
            IsTracingCallOutputMemory = true;
            IsTracingCode = true;
        }

        _frameTx = CreateFrameTxTraceBuilder(tx);
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
        ExecutionType.CREATE or ExecutionType.CREATE2 or ExecutionType.TCREATE => "create",
        ExecutionType.CALL or ExecutionType.TRANSACTION => "call",
        ExecutionType.DELEGATECALL => "delegatecall",
        ExecutionType.STATICCALL => "staticcall",
        ExecutionType.CALLCODE => "callcode",
        _ => throw new NotSupportedException($"Parity trace call type is undefined for {executionType}")
    };

    private static string GetActionType(ExecutionType executionType) => executionType switch
    {
        ExecutionType.CREATE or ExecutionType.CREATE2 or ExecutionType.TCREATE => "create",
        _ => "call"
    };

    /// <summary>
    /// Parity's and OpenEthereum's failure labels, with distinct labels for an address collision, which Parity reported
    /// as out of gas, and for a failed balance or call-depth check, for which Parity emitted no frame.
    /// </summary>
    private static string? GetErrorDescription(EvmExceptionType evmExceptionType) => evmExceptionType switch
    {
        EvmExceptionType.None => null,
        EvmExceptionType.BadInstruction => "Bad instruction",
        EvmExceptionType.StackOverflow => "Out of stack",
        EvmExceptionType.StackUnderflow => "Stack underflow",
        EvmExceptionType.OutOfGas => "Out of gas",
        EvmExceptionType.InvalidJumpDestination => "Bad jump destination",
        EvmExceptionType.AccessViolation => "Out of bounds",
        EvmExceptionType.StaticCallViolation => "Mutable Call In Static Context",
        EvmExceptionType.PrecompileFailure => "Built-in failed",
        EvmExceptionType.TransactionCollision => "Contract address collision",
        EvmExceptionType.NotEnoughBalance => "Insufficient balance for transfer",
        EvmExceptionType.InvalidCode => "Invalid code",
        EvmExceptionType.CallDepthExceeded => "Max call depth exceeded",
        EvmExceptionType.ReturnStackOverflow => "Return stack overflow",
        EvmExceptionType.ReturnStackUnderflow => "Return stack underflow",
        EvmExceptionType.Revert => "Reverted",
        _ => "Error",
    };

    /// <summary>
    /// Frame labels by the exception name a failed substate reports as its error.
    /// </summary>
    private static readonly FrozenDictionary<string, string> ErrorDescriptionsBySubstateError = new[]
    {
        EvmExceptionType.BadInstruction, EvmExceptionType.StackOverflow, EvmExceptionType.StackUnderflow,
        EvmExceptionType.OutOfGas, EvmExceptionType.InvalidJumpDestination, EvmExceptionType.AccessViolation,
        EvmExceptionType.StaticCallViolation, EvmExceptionType.PrecompileFailure, EvmExceptionType.TransactionCollision,
        EvmExceptionType.NotEnoughBalance, EvmExceptionType.Other, EvmExceptionType.InvalidCode,
    }.ToFrozenDictionary(static type => type.FastToString(), static type => GetErrorDescription(type)!);

    public virtual ParityLikeTxTrace BuildResult()
    {
        _frameTx?.CloseRoot();

        if ((_parityTraceTypes & ParityTraceTypes.Trace) == ParityTraceTypes.None)
        {
            _trace.Action = null;
        }

        return _trace;
    }

    protected virtual ParityTraceAction RentAction() => new();

    private ParityTraceAction CreateRetainedAction()
    {
        Retain(RetainedActionBytes);
        return RentAction();
    }

    /// <summary>The approximate retained metadata charge for an action and its address array.</summary>
    public static int GetActionMetadataSize(ParityTraceAction action) => RetainedActionBytes + TraceAddressSize(action.TraceAddress);

    private static int TraceAddressSize(in CappedArray<int> address) =>
        address.UnderlyingLength > 0 ? RetainedArrayHeaderBytes + address.UnderlyingLength * sizeof(int) : 0;

    private CappedArray<int> CreateRetainedTraceAddress(int length)
    {
        CappedArray<int> address = RentTraceAddress(length);
        try
        {
            Retain(TraceAddressSize(address));
            return address;
        }
        catch (TraceDataLimitExceededException)
        {
            ReturnTraceAddress(address);
            throw;
        }
    }

    protected virtual CappedArray<int> RentTraceAddress(int length) =>
        length == 0 ? CappedArray<int>.Empty : new CappedArray<int>(new int[length], length);

    protected virtual void ReturnTraceAddress(CappedArray<int> address) { }

    protected virtual ParityAccountStateChange RentAccountStateChange() => new();

    protected virtual Dictionary<UInt256, ParityStateChange<byte[]>> RentStorageDictionary() => [];

    protected virtual ParityStateChange<byte[]> RentByteStateChange(byte[]? before, byte[]? after) => new(before, after);

    private static byte[]? AsArrayOrNull(ReadOnlyMemory<byte> code) => code.IsNull() ? null : code.AsArray();

    protected virtual ParityStateChange<UInt256?> RentNullableUInt256StateChange(UInt256? before, UInt256? after) => new(before, after);

    protected virtual CappedArray<byte> CopyInput(ReadOnlyMemory<byte> input)
    {
        if (input.IsEmpty) return CappedArray<byte>.Empty;
        Retain(input.Length);
        byte[] copy = new byte[input.Length];
        input.Span.CopyTo(copy);
        return new CappedArray<byte>(copy, copy.Length);
    }

    protected virtual void ReturnInputBytes(in CappedArray<byte> input) { }

    /// <summary>Takes back an action that left its frame without joining the trace, which nothing references any more.</summary>
    protected virtual void ReturnExcludedAction(ParityTraceAction action) =>
        ReleaseRetainedData(action.Input.UnderlyingLength);

    // Only the action tree reads the input, and BuildResult drops it unless Trace was requested.
    private CappedArray<byte> CopyActionInput(ReadOnlyMemory<byte> input)
    {
        if ((_parityTraceTypes & ParityTraceTypes.Trace) == 0) return CappedArray<byte>.Empty;
        return CopyInput(input);
    }

    protected void Retain(int length)
    {
        _retainedBytes += length;
        ChargeRetainedData(length);
    }

    protected virtual void ChargeRetainedData(int length) => DataBudget.Add(length);

    protected void TransferRetainedData(int length) => _retainedBytes -= length;

    /// <summary>Counts in the trace <paramref name="length"/> bytes that are charged already, without charging them again.</summary>
    protected void TakeOverRetainedData(int length) => _retainedBytes += length;

    private void ReleaseRetainedData(int length)
    {
        TransferRetainedData(length);
        DataBudget.Release(length);
    }

    /// <summary>
    /// Gives back to <see cref="DataBudget"/> the bytes this tracer charged since it started or last gave them back.
    /// Called once the trace it kept is released, also when the budget is shared with other tracers.
    /// </summary>
    public virtual void ReleaseRetainedData()
    {
        DataBudget.Release(_retainedBytes);
        _retainedBytes = 0;
    }

    protected void ResetTracerState(Block block, Transaction? tx)
    {
        ReleaseRetainedData();
        _tx = tx;
        _trace.TransactionHash = tx?.Hash;
        _trace.TransactionPosition = tx is null ? null : Array.IndexOf(block.Transactions!, tx);
        _trace.BlockNumber = block.Number;
        _trace.BlockHash = block.Hash!;
        _treatGasParityStyle = false;
        _gasAlreadySetForCurrentOp = false;
        _frameTx = CreateFrameTxTraceBuilder(tx);
    }

    protected void ClearTraceReferences()
    {
        _tx = null;
        _frameTx = null;
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
    }

    private void PushAction(ParityTraceAction action)
    {
        AttachAction(action);
        _actionStack.Push(action);
        _currentAction = action;

        OnEnterVmFrame(action);
    }

    /// <summary>
    /// Makes the action the root, or the next subtrace of the current action, numbered after its earlier ones.
    /// </summary>
    private void AttachAction(ParityTraceAction action)
    {
        if (_currentAction is not null)
        {
            int parentLen = _currentAction.TraceAddress.Length;
            CappedArray<int> traceAddress;
            try
            {
                traceAddress = CreateRetainedTraceAddress(parentLen + 1);
            }
            catch (TraceDataLimitExceededException)
            {
                ReturnExcludedAction(action);
                throw;
            }
            ReadOnlySpan<int> parentSpan = _currentAction.TraceAddress.AsSpan();
            Span<int> childSpan = traceAddress.AsSpan();
            parentSpan.CopyTo(childSpan);
            childSpan[parentLen] = _currentAction.Subtraces.Count;
            action.TraceAddress = traceAddress;
            _frameTx?.OnChildPushed(_currentAction, action);
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
    }

    protected void ReturnActiveExcludedActions()
    {
        foreach (ParityTraceAction action in _actionStack)
        {
            if (!action.IncludeInTrace && action != _trace.Action) ReturnExcludedAction(action);
        }
    }

    protected virtual void OnEnterVmFrame(ParityTraceAction action)
    {
        if (!IsTracingInstructions) return;

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
        _frameTx?.OnPopped(popped);

        _actionStack.Pop();
        _currentAction = _actionStack.Count == 0 ? null : _actionStack.Peek();

        if (!popped.IncludeInTrace && popped != _trace.Action)
        {
            ReleaseRetainedData(popped.Result?.Output?.Length ?? 0);
            ReleaseRetainedData(GetActionMetadataSize(popped));
            ReturnExcludedAction(popped);
        }
    }

    protected virtual void OnLeaveVmFrame(ParityTraceAction action)
    {
        if (!IsTracingInstructions) return;

        _currentVmTrace.VmTrace.Operations = _currentVmTrace.Ops;
        _vmTraceStack.Pop();
        _currentVmTrace = _vmTraceStack.Count == 0 ? (null, null) : _vmTraceStack.Peek();
        _currentOperation = _currentVmTrace.Ops?.Last();
        _gasAlreadySetForCurrentOp = false;

        if (action.Type != "suicide")
        {
            _treatGasParityStyle = true;
        }
    }

    public override void MarkAsSuccess(Address recipient, in GasConsumed gasSpent, byte[] output, LogEntry[] logs,
        Hash256? stateRoot = null)
    {
        _frameTx?.MarkSucceeded(gasSpent);

        if (_currentAction is not null)
        {
            throw new InvalidOperationException($"Closing trace at level {_currentAction.TraceAddress.Length}");
        }

        if (!IsTracingActions)
        {
            Retain(output.Length);
            _trace.Output = output;
            return;
        }

        _trace.Action ??= CreateRootActionFromTx();

        if (_trace.Action.TraceAddress.Length == 0)
        {
            _trace.Output = output;
        }

        _trace.Action.Result!.Output = output;
    }

    public override void MarkAsFailed(Address recipient, in GasConsumed gasSpent, byte[] output, string? error,
        Hash256? stateRoot = null)
    {
        _frameTx?.MarkFailed(gasSpent, output, error);

        if (_currentAction is not null)
        {
            throw new InvalidOperationException($"Closing trace at level {_currentAction!.TraceAddress.Length}");
        }

        if (!IsTracingActions) Retain(output.Length);
        _trace.Output = output;

        if (!IsTracingActions) return;

        if (_trace.Action is null)
        {
            // No frame was entered, e.g. a creation colliding with an existing account: the frame halted, so it has
            // no result, and its error is the substate's exception name, which is given the frame label.
            ParityTraceAction action = CreateRootActionFromTx();
            action.Result = null;
            action.Error = error is not null && ErrorDescriptionsBySubstateError.TryGetValue(error, out string? description)
                ? description
                : error;
            _trace.Action = action;
        }
    }

    private ParityTraceAction CreateRootActionFromTx()
    {
        ParityTraceAction action = CreateRetainedAction();
        action.From = _tx!.SenderAddress;
        action.To = _tx.To;
        action.Value = _tx.Value;
        action.Input = CopyActionInput(_tx.Data);
        action.Gas = _tx.GasLimit;
        action.CallType = _tx.IsMessageCall ? "call" : "init";
        return action;
    }

    public override void StartOperation(int pc, Instruction opcode, ulong gas, in ExecutionEnvironment env)
    {
        Retain(RetainedOperationBytes);
        ParityVmOperationTrace operationTrace = new();
        _gasAlreadySetForCurrentOp = false;
        operationTrace.Pc = pc;
        operationTrace.Cost = gas;
        _currentOperation = operationTrace;
        _currentOpcode = opcode;
        _currentPushList.Clear();
        _currentVmTrace.Ops.Add(operationTrace);
    }

    public override void ReportOperationError(EvmExceptionType error)
    {
        // A call or creation that fails its balance or depth precheck pushes 0 and execution continues.
        if (error is EvmExceptionType.NotEnoughBalance or EvmExceptionType.CallDepthExceeded) return;

        // A failed precompile frame reports its error without an operation of its own.
        List<ParityVmOperationTrace> ops = _currentVmTrace.Ops;
        if (ops is not { Count: > 0 } || !ReferenceEquals(ops[^1], _currentOperation)) return;

        if (IsRejectedBeforeExecution(_currentOpcode, error))
        {
            ops.RemoveAt(ops.Count - 1);
        }
        else
        {
            ops[^1].Halted = true;
        }
    }

    /// <summary>
    /// Undefined opcodes, the designated <c>INVALID</c> (which charges its gas first, so it can also run out of gas)
    /// and stack underflow or overflow reject an operation before it executes, so it is omitted. Any other error
    /// halts an operation that began executing, which is kept with a null <c>ex</c>.
    /// </summary>
    protected static bool IsRejectedBeforeExecution(Instruction opcode, EvmExceptionType error) =>
        opcode == Instruction.INVALID ||
        error is EvmExceptionType.BadInstruction or EvmExceptionType.StackUnderflow or EvmExceptionType.StackOverflow;

    public override void ReportOperationRemainingGas(ulong gas)
    {
        if (!_gasAlreadySetForCurrentOp && _currentOperation is not null)
        {
            _gasAlreadySetForCurrentOp = true;

            _currentOperation.Cost -= (_treatGasParityStyle ? 0UL : gas);
            _currentOperation.Push = _currentPushList.ToArray();
            _currentOperation.Used = gas;

            _treatGasParityStyle = false;
        }
    }

    public override void ReportStackPush(in ReadOnlySpan<byte> stackItem)
    {
        Retain(Math.Max(stackItem.Length, MinStackItemBytes) + RetainedStackItemBytes);
        _currentPushList.Add(stackItem.ToArray());
    }

    public override void ReportMemoryChange(long offset, in ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return;
        if (_currentOperation!.Memory is not { } previous)
        {
            Retain(data.Length + RetainedMemoryChangeBytes);
            _currentOperation.Memory = new ParityMemoryChangeTrace { Offset = offset, Data = data.ToArray() };
            return;
        }

        int delta = data.Length - previous.Data.Length;
        if (delta > 0) Retain(delta);
        previous.Data = data.ToArray();
        previous.Offset = offset;
        if (delta < 0) ReleaseRetainedData(-delta);
    }

    public override void ReportOperationStorageChange(in ReadOnlySpan<byte> key, in ReadOnlySpan<byte> value)
    {
        Retain(key.Length + value.Length + RetainedStorageChangeBytes);
        _currentOperation!.Store = new ParityStorageChangeTrace { Key = key.ToArray(), Value = value.ToArray() };
    }

    public override void ReportBalanceChange(Address address, UInt256? before, UInt256? after)
    {
        ParityAccountStateChange value = GetAccountChange(address);
        value.Balance = UpdateScalarChange(value.Balance, before, after);
    }

    public override void ReportCodeChange(Address address, ReadOnlyMemory<byte> before, ReadOnlyMemory<byte> after)
    {
        ParityAccountStateChange value = GetAccountChange(address);
        value.Code = UpdateByteChange(value.Code, value.Code?.Before is { } previous ? previous : before, after);
    }

    public override void ReportNonceChange(Address address, UInt256? before, UInt256? after)
    {
        ParityAccountStateChange value = GetAccountChange(address);
        value.Nonce = UpdateScalarChange(value.Nonce, before, after);
    }

    public override void ReportStorageChange(in StorageCell storageCell, byte[] before, byte[] after)
    {
        ParityAccountStateChange value = GetAccountChange(storageCell.Address);
        if (value.Storage is null)
        {
            Retain(RetainedStorageDictionaryBytes);
            value.Storage = RentStorageDictionary();
        }

        value.Storage.TryGetValue(storageCell.Index, out ParityStateChange<byte[]>? existing);
        if (existing is null) Retain(RetainedStorageSlotBytes);
        value.Storage[storageCell.Index] = UpdateByteChange(existing, existing?.Before ?? before, after);
    }

    private ParityAccountStateChange GetAccountChange(Address address)
    {
        Dictionary<Address, ParityAccountStateChange> changes = _trace.StateChanges ??
            throw new InvalidOperationException($"{nameof(ParityLikeTxTracer)} did not expect state change report.");
        if (!changes.TryGetValue(address, out ParityAccountStateChange? value))
        {
            Retain(RetainedAccountChangeBytes);
            value = RentAccountStateChange();
            changes.Add(address, value);
        }
        return value;
    }

    private ParityStateChange<UInt256?> UpdateScalarChange(ParityStateChange<UInt256?>? change, UInt256? before, UInt256? after)
    {
        if (change is null)
        {
            Retain(RetainedScalarChangeBytes);
            return RentNullableUInt256StateChange(before, after);
        }
        change.Before ??= before;
        change.After = after;
        return change;
    }

    private ParityStateChange<byte[]> UpdateByteChange(ParityStateChange<byte[]>? change, ReadOnlyMemory<byte> before, ReadOnlyMemory<byte> after)
    {
        int newSize = ArraySize(before.Length) + ArraySize(after.Length);
        if (change is null)
        {
            Retain(RetainedByteChangeBytes + newSize);
            return RentByteStateChange(AsArrayOrNull(before), AsArrayOrNull(after));
        }

        int delta = newSize - ArraySize(change.Before?.Length ?? 0) - ArraySize(change.After?.Length ?? 0);
        if (delta > 0) Retain(delta);
        change.Before = AsArrayOrNull(before);
        change.After = AsArrayOrNull(after);
        if (delta < 0) ReleaseRetainedData(-delta);
        return change;

        static int ArraySize(int length) => length > 0 ? RetainedArrayHeaderBytes + length : 0;
    }

    public override void ReportAction(ulong gas, UInt256 value, Address from, Address to, ReadOnlyMemory<byte> input,
        ExecutionType callType, bool isPrecompileCall = false)
    {
        _frameTx?.EnsureRoot();

        ParityTraceAction action = CreateAction(gas, value, from, to, input, callType, isPrecompileCall);

        if (_currentOperation is not null && callType.IsAnyCreate())
        {
            _currentOperation.Cost += gas;
        }

        PushAction(action);
    }

    /// <summary>
    /// A call or creation that failed its precheck or collided entered no frame, so its action is complete: it has an
    /// error, no result, no subtraces and no <c>vmTrace</c> sub-trace.
    /// </summary>
    /// <remarks>
    /// As for a creation that entered its frame, the operation's cost includes the gas made available to the creation,
    /// which a failed precheck returns at once and a collision consumes.
    /// </remarks>
    public override void ReportRejectedAction(ulong gas, ulong gasLeft, UInt256 value, Address from, Address to,
        ReadOnlyMemory<byte> input, ExecutionType callType, EvmExceptionType error, bool isPrecompileCall = false)
    {
        if (_currentOperation is not null && callType.IsAnyCreate())
        {
            _currentOperation.Cost += gas;
        }

        // Like one that entered its frame, a nested zero-value precompile call is left out of the trace.
        if (isPrecompileCall && value.IsZero) return;

        // A creation that failed its precheck has no destination, so stored traces and OTS do not list or index it.
        Address? actionTo = callType.IsAnyCreate() && error != EvmExceptionType.TransactionCollision ? null : to;
        ParityTraceAction action = CreateAction(gas, value, from, actionTo, input, callType, isPrecompileCall);
        action.Result = null;
        action.Error = GetErrorDescription(error);
        AttachAction(action);
    }

    private ParityTraceAction CreateAction(ulong gas, UInt256 value, Address from, Address? to, ReadOnlyMemory<byte> input,
        ExecutionType callType, bool isPrecompileCall)
    {
        ParityTraceAction action = CreateRetainedAction();
        action.IsPrecompiled = isPrecompileCall;
        // ignore pre compile calls with Zero value that originates from contracts
        action.IncludeInTrace = !(isPrecompileCall && callType != ExecutionType.TRANSACTION && value.IsZero);
        action.From = from;
        action.To = to;
        action.Value = value;
        action.Input = CopyActionInput(input);
        action.Gas = gas;
        action.CallType = GetCallType(callType);
        action.Type = GetActionType(callType);
        action.CreationMethod = GetCreateMethod(callType);
        return action;
    }

    private static string? GetCreateMethod(ExecutionType callType) => callType switch
    {
        ExecutionType.CREATE => "create",
        ExecutionType.CREATE2 => "create2",
        ExecutionType.TCREATE => "tcreate",
        _ => null
    };

    public override void ReportSelfDestruct(Address address, UInt256 balance, Address refundAddress)
    {
        ParityTraceAction action = CreateRetainedAction();
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

        Retain(output.Length);
        _currentAction.Result.Output = output.AsArray();
        _currentAction.Result.GasUsed = _currentAction.Gas - gas;
        PopAction();
    }

    public override void ReportActionError(EvmExceptionType evmExceptionType) => HandleActionError(evmExceptionType);

    /// <summary>
    /// Unlike an exceptional halt, a REVERT returns its data and unused gas, so the frame keeps a result with both.
    /// A reverted creation deployed nothing, so its result has no address or code.
    /// </summary>
    public override void ReportActionRevert(ulong gas, ReadOnlyMemory<byte> output)
    {
        ParityTraceResult result = _currentAction!.Result!;
        Retain(output.Length);
        result.Output = output.AsArray();
        result.GasUsed = _currentAction.Gas - gas;
        _currentAction.Error = GetErrorDescription(EvmExceptionType.Revert);
        PopAction();
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

        Retain(deployedCode.Length);
        _currentAction.Result.Address = deploymentAddress;
        _currentAction.Result.Code = deployedCode.AsArray();
        _currentAction.Result.GasUsed = _currentAction.Gas - gas;
        PopAction();
    }

    public override void ReportByteCode(ReadOnlyMemory<byte> byteCode)
    {
        Retain(byteCode.Length);
        _currentVmTrace.VmTrace.Code = byteCode.ToArray();
    }

    public override void ReportGasUpdateForVmTrace(ulong refund, ulong gasAvailable)
    {
        if (_currentOperation is null) return;
        _currentOperation.Used = gasAvailable;
        // Also after the operation's trace ended: a colliding CREATE pushes its 0 after that.
        _currentOperation.Push = _currentPushList.ToArray();

        if (!_gasAlreadySetForCurrentOp)
        {
            _gasAlreadySetForCurrentOp = true;
            _treatGasParityStyle = false;
        }
    }
}
