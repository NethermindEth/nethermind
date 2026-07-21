// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;

namespace Nethermind.Evm;

/// <summary>
/// State for EVM Calls
/// </summary>
[DebuggerDisplay("{ExecutionType} to {Env.ExecutingAccount}, G {GasAvailable} R {Refund} PC {ProgramCounter} OUT {OutputDestination}:{OutputLength}")]
public class VmState<TGasPolicy> : IDisposable
    where TGasPolicy : struct, IGasPolicy<TGasPolicy>
{
    private static readonly
#if ZK_EVM
        ZkEvmQueue<VmState<TGasPolicy>>
#else
        System.Collections.Concurrent.ConcurrentQueue<VmState<TGasPolicy>>
#endif
        _statePool = new();

    private static readonly StackPool _stackPool = new();

    public byte[]? DataStack;
    public TGasPolicy Gas;
    public long InitialStateGasUsed;
    // State-gas refund already made spendable in this frame while its accounting correction
    // still has to reach the ancestor frame that originally paid the state gas.
    public long StateGasRefundAdvanced;
    internal long OutputDestination { get; private set; } // TODO: move to CallEnv
    internal long OutputLength { get; private set; } // TODO: move to CallEnv
    public long Refund { get; set; }
    public int DataStackHead;
    public ExecutionType ExecutionType { get; private set; } // TODO: move to CallEnv
    public int ProgramCounter { get; set; }
    public bool IsTopLevel { get; private set; } // TODO: move to CallEnv
    private bool _canRestore;
    public bool IsStatic { get; private set; } // TODO: move to CallEnv
    public bool IsContinuation { get; set; } // TODO: move to CallEnv
    public bool IsCreateOnPreExistingAccount { get; private set; } // TODO: move to CallEnv

    /// <summary>
    /// EIP-8037: the parent <c>*CALL</c> charged NEW_ACCOUNT state gas up-front for this (dead)
    /// recipient; on this frame's error/revert no account is created, so the parent refunds it.
    /// </summary>
    public bool NewAccountCharged { get; private set; } // TODO: move to CallEnv

    private bool _isDisposed = true;

    private EvmPooledMemory _memory;
    private ExecutionEnvironment? _env;
    private StackAccessTracker _accessTracker;
    private Snapshot _snapshot;

    /// <summary>
    /// Rent a top level <see cref="VmState{TGasPolicy}"/>.
    /// </summary>
    public static VmState<TGasPolicy> RentTopLevel(
        TGasPolicy gas,
        ExecutionType executionType,
        ExecutionEnvironment env,
        in StackAccessTracker accessedItems,
        in Snapshot snapshot)
    {
        VmState<TGasPolicy> state = Rent();
        state.Initialize(
            gas,
            outputDestination: 0L,
            outputLength: 0L,
            executionType: executionType,
            isTopLevel: true,
            isStatic: false,
            isCreateOnPreExistingAccount: false,
            newAccountCharged: false,
            env: env,
            stateForAccessLists: accessedItems,
            snapshot: snapshot);
        return state;
    }

    /// <summary>
    /// Constructor for a frame <see cref="VmState{TGasPolicy}"/> beneath top level.
    /// </summary>
    public static VmState<TGasPolicy> RentFrame(
        TGasPolicy gas,
        long outputDestination,
        long outputLength,
        ExecutionType executionType,
        bool isStatic,
        bool isCreateOnPreExistingAccount,
        ExecutionEnvironment env,
        in StackAccessTracker stateForAccessLists,
        in Snapshot snapshot,
        bool isTopLevel = false,
        bool newAccountCharged = false)
    {
        VmState<TGasPolicy> state = Rent();
        state.Initialize(
            gas,
            outputDestination,
            outputLength,
            executionType,
            isTopLevel: isTopLevel,
            isStatic: isStatic,
            isCreateOnPreExistingAccount: isCreateOnPreExistingAccount,
            newAccountCharged: newAccountCharged,
            env: env,
            stateForAccessLists: stateForAccessLists,
            snapshot: snapshot);
        return state;
    }

    private static VmState<TGasPolicy> Rent()
    {
        if (_statePool.TryDequeue(out VmState<TGasPolicy>? state)) return state;
        return new VmState<TGasPolicy>();
    }

    [SkipLocalsInit]
    private void Initialize(
        TGasPolicy gas,
        long outputDestination,
        long outputLength,
        ExecutionType executionType,
        bool isTopLevel,
        bool isStatic,
        bool isCreateOnPreExistingAccount,
        bool newAccountCharged,
        ExecutionEnvironment env,
        in StackAccessTracker stateForAccessLists,
        in Snapshot snapshot)
    {
        _env = env;
        _snapshot = snapshot;
        _accessTracker = stateForAccessLists;
#if ZK_EVM
        // Guest only: the EVM memory buffer lives on the per-tx scratch arena (reclaimed at reset), so a
        // handle left from a prior transaction dangles — reset it so the next growth allocates fresh.
        // Mainline doesn't need this: Dispose() clears _memory before the VmState returns to the pool.
        _memory = default;
#endif
        if (executionType.IsAnyCreate())
        {
            _accessTracker.WasCreated(env.ExecutingAccount);
        }
        _accessTracker.TakeSnapshot();
        Debug.Assert(StateGasRefundAdvanced == 0, "Pooled VmState returned with uncleared StateGasRefundAdvanced.");
        Gas = gas;
        InitialStateGasUsed = TGasPolicy.GetStateGasUsed(in gas);
        StateGasRefundAdvanced = 0;
        OutputDestination = outputDestination;
        OutputLength = outputLength;
        Refund = 0;
        DataStackHead = 0;
        ProgramCounter = 0;
        ExecutionType = executionType;
        IsTopLevel = isTopLevel;
        _canRestore = !isTopLevel;
        IsStatic = isStatic;
        IsContinuation = false;
        IsCreateOnPreExistingAccount = isCreateOnPreExistingAccount;
        NewAccountCharged = newAccountCharged;

        if (!_isDisposed)
        {
            ThrowIsInUse();
        }
        _isDisposed = false;

#if DEBUG
        _creationStackTrace = new StackTrace();
#endif
        [DoesNotReturn, StackTraceHidden]
        static void ThrowIsInUse() => throw new InvalidOperationException("Already in use");
    }

    public Address From => ExecutionType switch
    {
        ExecutionType.STATICCALL or ExecutionType.CALL or ExecutionType.CALLCODE or ExecutionType.CREATE
            or ExecutionType.CREATE2 or ExecutionType.TRANSACTION => Env.Caller,
        ExecutionType.DELEGATECALL => Env.ExecutingAccount,
        _ => throw new ArgumentOutOfRangeException(),
    };

    public Address To => Env.CodeSource ?? Env.ExecutingAccount;
    public bool IsPrecompile => Env.CodeInfo?.IsPrecompile ?? false;

    public ref readonly StackAccessTracker AccessTracker => ref _accessTracker;
    public ExecutionEnvironment Env => _env!;
    public ref EvmPooledMemory Memory => ref _memory;
    public ref readonly Snapshot Snapshot => ref _snapshot;

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }
        _isDisposed = true;

        if (DataStack is not null)
        {
            // Only return if initialized
            _stackPool.ReturnStacks(DataStack);
            DataStack = null;
        }

        if (_canRestore)
        {
            // if we didn't commit and we are not top level, then we need to restore and drop the changes done in this call
            _accessTracker.Restore();
        }
        _memory.Dispose();
        _memory = default;
        _accessTracker = default;
        if (!IsTopLevel) _env?.Dispose();
        _env = null;
        _snapshot = default;
        StateGasRefundAdvanced = 0;

        _statePool.Enqueue(this);

#if DEBUG
        GC.SuppressFinalize(this);
#endif
    }

#if DEBUG

    private StackTrace? _creationStackTrace;

    ~VmState()
    {
        if (!_isDisposed)
        {
            Console.Error.WriteLine($"Warning: {nameof(VmState<TGasPolicy>)} was not disposed. Created at: {_creationStackTrace}");
        }
    }
#endif

    public void InitializeStacks(ReadOnlySpan<byte> codeSpan, out EvmStack stack)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        byte[] dataStack = DataStack;
        if (dataStack is null)
        {
            DataStack = dataStack = AllocateStacks();
        }

        stack = new(DataStackHead, ref As32AlignedRef(dataStack), codeSpan);
    }

    public void InitializeStacks(ITxTracer txTracer, ReadOnlySpan<byte> codeSpan, out EvmStack stack)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        byte[] dataStack = DataStack;
        if (dataStack is null)
        {
            DataStack = dataStack = AllocateStacks();
        }

        stack = new(DataStackHead, txTracer, ref As32AlignedRef(dataStack), codeSpan);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static byte[] AllocateStacks() => _stackPool.RentStacks();

    private static ref byte As32AlignedRef(byte[] array)
    {
        nuint offset = GetAlignmentOffset32(array);
        return ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(array), offset);
    }

    public Memory<byte> MemoryStacks(int count)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        byte[] dataStack = DataStack;
        if (dataStack is null)
        {
            DataStack = dataStack = AllocateStacks();
        }
        return AsAligned32Memory(dataStack, size: count * EvmStack.WordSize);
    }

    private static Memory<byte> AsAligned32Memory(byte[] array, int size)
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

    public void CommitToParent(VmState<TGasPolicy> parentState)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        // `checked` traps a buggy refund propagation that would otherwise wrap silently.
        parentState.Refund = checked(parentState.Refund + Refund);
        _canRestore = false; // we can't restore if we committed
    }
}
