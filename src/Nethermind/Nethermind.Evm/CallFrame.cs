// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;

namespace Nethermind.Evm;

/// <summary>
/// Per-call execution frame for the EVM.
/// </summary>
[DebuggerDisplay("{ExecutionType} to {ExecutingAccount}, G {GasAvailable} R {Refund} PC {ProgramCounter} OUT {OutputDestination}:{OutputLength}")]
public sealed class CallFrame<TGasPolicy> : IDisposable
    where TGasPolicy : struct, IGasPolicy<TGasPolicy>
{
    private static readonly ConcurrentQueue<CallFrame<TGasPolicy>> _framePool = new();
    private static readonly StackPool _stackPool = new();

    // --- Hot fields (accessed every opcode) ---
    public byte[]? DataStack;
    public ReturnState[]? ReturnStack;
    public TGasPolicy Gas;
    public int DataStackHead;
    public int ReturnStackHead;
    public int ProgramCounter { get; set; }
    public int FunctionIndex { get; set; }
    public Address ExecutingAccount { get; private set; } = null!;
    public Address Caller { get; private set; } = null!;
    public Address? CodeSource { get; private set; }
    public CodeInfo? CodeInfo { get; private set; }
    private UInt256 _value;
    public ReadOnlyMemory<byte> InputData { get; private set; }

    internal long OutputDestination { get; private set; }
    internal long OutputLength { get; private set; }
    public ExecutionType ExecutionType { get; private set; }
    public bool IsTopLevel { get; private set; }
    private bool _canRestore;
    public bool IsStatic { get; private set; }
    public bool IsContinuation { get; set; }
    public bool IsCreateOnPreExistingAccount { get; private set; }

    private bool _isDisposed = true;

    public long Refund { get; set; }
    private EvmPooledMemory _memory;
    private AccessSnapshot _accessTracker;
    private Snapshot _snapshot;

    /// <summary>
    /// Value passed to this call. For DELEGATECALL this is the inherited caller value
    /// (no ETH actually transfers). Use <see cref="ExecutionType"/> to determine whether
    /// a real transfer should occur.
    /// </summary>
    public ref readonly UInt256 Value => ref _value;

    /// <summary>
    /// Reconstructs an <see cref="ExecutionEnvironment"/> struct from inlined fields.
    /// Tracing-only — copies ~116 bytes per call. Use direct fields on hot paths.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ExecutionEnvironment GetEnv(int callDepth)
    {
        UInt256 transferValue = ExecutionType.IsAnyDelegateCall() ? UInt256.Zero : _value;
        return new(CodeInfo, ExecutingAccount, Caller, CodeSource, callDepth,
            in transferValue, in _value, InputData);
    }

    /// <summary>
    /// Rent a top level <see cref="CallFrame{TGasPolicy}"/>.
    /// </summary>
    [SkipLocalsInit]
    public static CallFrame<TGasPolicy> RentTopLevel(
        TGasPolicy gas,
        ExecutionType executionType,
        CodeInfo? codeInfo,
        Address executingAccount,
        Address caller,
        Address? codeSource,
        in UInt256 value,
        in ReadOnlyMemory<byte> inputData,
        in AccessSnapshot accessedItems,
        AccessTrackingState trackingState,
        in Snapshot snapshot)
    {
        CallFrame<TGasPolicy> frame = RentFromPool();
        frame.ThrowIfInUse();

        frame._accessTracker = accessedItems;
        if (executionType.IsAnyCreate())
        {
            trackingState.WasCreated(executingAccount);
        }
        frame._accessTracker.TakeSnapshot(trackingState);
        frame._snapshot = snapshot;

        frame.ExecutingAccount = executingAccount;
        frame.Caller = caller;
        frame.CodeSource = codeSource;
        frame.CodeInfo = codeInfo;
        frame._value = value;
        frame.InputData = inputData;

        frame.OutputDestination = 0;
        frame.OutputLength = 0;
        frame.Refund = 0;
        frame.DataStackHead = 0;
        frame.ReturnStackHead = 0;
        frame.ProgramCounter = 0;
        frame.FunctionIndex = 0;
        frame.ExecutionType = executionType;
        frame.IsTopLevel = true;
        frame._canRestore = false;
        frame.IsStatic = false;
        frame.IsContinuation = false;
        frame.IsCreateOnPreExistingAccount = false;
        frame.Gas = gas;
#if DEBUG
        frame._creationStackTrace = new StackTrace();
#endif
        return frame;
    }

    /// <summary>
    /// Rent a child <see cref="CallFrame{TGasPolicy}"/> beneath the top level.
    /// </summary>
    [SkipLocalsInit]
    public static CallFrame<TGasPolicy> Rent(
        TGasPolicy gas,
        long outputDestination,
        long outputLength,
        ExecutionType executionType,
        bool isStatic,
        bool isCreateOnPreExistingAccount,
        CodeInfo? codeInfo,
        Address executingAccount,
        Address caller,
        Address? codeSource,
        in UInt256 value,
        in ReadOnlyMemory<byte> inputData,
        in AccessSnapshot stateForAccessLists,
        AccessTrackingState trackingState,
        in Snapshot snapshot)
    {
        CallFrame<TGasPolicy> frame = RentFromPool();
        frame.ThrowIfInUse();

        frame._accessTracker = stateForAccessLists;
        if (executionType.IsAnyCreate())
        {
            trackingState.WasCreated(executingAccount);
        }
        frame._accessTracker.TakeSnapshot(trackingState);
        frame._snapshot = snapshot;

        frame.ExecutingAccount = executingAccount;
        frame.Caller = caller;
        frame.CodeSource = codeSource;
        frame.CodeInfo = codeInfo;
        frame._value = value;
        frame.InputData = inputData;

        frame.OutputDestination = outputDestination;
        frame.OutputLength = outputLength;
        frame.Refund = 0;
        frame.DataStackHead = 0;
        frame.ReturnStackHead = 0;
        frame.ProgramCounter = 0;
        frame.FunctionIndex = 0;
        frame.ExecutionType = executionType;
        frame.IsTopLevel = false;
        frame._canRestore = true;
        frame.IsStatic = isStatic;
        frame.IsContinuation = false;
        frame.IsCreateOnPreExistingAccount = isCreateOnPreExistingAccount;
        frame.Gas = gas;
#if DEBUG
        frame._creationStackTrace = new StackTrace();
#endif
        return frame;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static CallFrame<TGasPolicy> RentFromPool()
        => _framePool.TryDequeue(out CallFrame<TGasPolicy>? state) ? state : new CallFrame<TGasPolicy>();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ThrowIfInUse()
    {
        if (!_isDisposed)
        {
            ThrowAlreadyInUse();
        }
        _isDisposed = false;

        [DoesNotReturn, StackTraceHidden]
        static void ThrowAlreadyInUse()
        {
            throw new InvalidOperationException("Already in use");
        }
    }

    public Address From => ExecutionType.IsAnyDelegateCall() ? ExecutingAccount : Caller;

    public Address To => CodeSource ?? ExecutingAccount;
    public bool IsPrecompile => CodeInfo?.IsPrecompile ?? false;

    public ref readonly AccessSnapshot AccessTracker => ref _accessTracker;
    public ref EvmPooledMemory Memory => ref _memory;
    public ref readonly Snapshot Snapshot => ref _snapshot;

    /// <summary>
    /// Restores the access tracker snapshots if this frame was not committed.
    /// Called by the VM before Dispose for non-committed child frames.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void RestoreAccessTracker(AccessTrackingState trackingState)
    {
        if (_canRestore)
        {
            _accessTracker.Restore(trackingState);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public void Dispose()
    {
        Debug.Assert(!_isDisposed);
        if (_isDisposed)
        {
            return;
        }
        _isDisposed = true;

        if (DataStack is not null)
        {
            // Only return if initialized
            _stackPool.ReturnStacks(DataStack, ReturnStack!);
            DataStack = null;
            ReturnStack = null;
        }

        _memory.Dispose();
        _memory = default;
        _accessTracker = default;
        // Clear inlined env fields to prevent GC root retention through the pool
        ExecutingAccount = null!;
        Caller = null!;
        CodeSource = null;
        CodeInfo = null;
        InputData = default;
        _value = default;
        _snapshot = default;

        _framePool.Enqueue(this);

#if DEBUG
        GC.SuppressFinalize(this);
#endif
    }

#if DEBUG

    private StackTrace? _creationStackTrace;

    ~CallFrame()
    {
        if (!_isDisposed)
        {
            Console.Error.WriteLine($"Warning: {nameof(CallFrame<TGasPolicy>)} was not disposed. Created at: {_creationStackTrace}");
        }
    }
#endif

    public void InitializeStacks(ReadOnlySpan<byte> codeSpan, out EvmStack stack)
    {
        ref byte alignedRef = ref As32AlignedRef(EnsureStacks());
        stack = new(DataStackHead, ref alignedRef, codeSpan);
    }

    public void InitializeStacks(ITxTracer txTracer, ReadOnlySpan<byte> codeSpan, out EvmStack stack)
    {
        ref byte alignedRef = ref As32AlignedRef(EnsureStacks());
        stack = new(DataStackHead, txTracer, ref alignedRef, codeSpan);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte[] EnsureStacks()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        byte[] dataStack = DataStack;
        if (dataStack is null)
        {
            dataStack = AllocateStacks();
        }

        return dataStack;

        [MethodImpl(MethodImplOptions.NoInlining)]
        byte[] AllocateStacks()
        {
            (DataStack, ReturnStack) = _stackPool.RentStacks();
            return DataStack;
        }
    }

    private static ref byte As32AlignedRef(byte[] array)
    {
        nuint offset = GetAlignmentOffset32(array);
        return ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(array), offset);
    }

    public Memory<byte> MemoryStacks(int count)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        return AsAlignedMemory(DataStack, alignment: EvmStack.WordSize, size: count * EvmStack.WordSize);
    }

    private static Memory<byte> AsAlignedMemory(byte[] array, uint alignment, int size)
    {
        nuint offset = GetAlignmentOffset32(array);
        return array.AsMemory((int)(uint)offset, size);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe static nuint GetAlignmentOffset32(byte[] array)
    {
        // The input array should be pinned and we are just using the Pointer to
        // calculate alignment, not using data so not creating memory hole.
        Debug.Assert(array is not null);
        nint addr = (nint)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(array));
        return (nuint)((-addr) & 31);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CommitToParent(CallFrame<TGasPolicy> parentState)
    {
        parentState.Refund += Refund;
        _canRestore = false; // we can't restore if we committed
    }
}

/// <summary>
/// Return state for EVM call stack management.
/// </summary>
[StructLayout(LayoutKind.Auto)]
public struct ReturnState
{
    public int Index;
    public int Offset;
    public int Height;
}
