// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.State;

namespace Nethermind.Evm;

/// <summary>
/// State for EVM Calls
/// </summary>
[DebuggerDisplay("{ExecutionType} to {Env.ExecutingAccount}, G {GasAvailable} R {Refund} PC {ProgramCounter} OUT {OutputDestination}:{OutputLength}")]
public class VmState<TGasPolicy> : IDisposable
    where TGasPolicy : struct, IGasPolicy<TGasPolicy>
{
    private static readonly ConcurrentQueue<VmState<TGasPolicy>> _statePool = new();
    private static readonly StackPool _stackPool = new();

    public byte[]? DataStack;
    public TGasPolicy Gas;
    public long InitialStateReservoir;
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
        bool isTopLevel = false)
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
            env: env,
            stateForAccessLists: stateForAccessLists,
            snapshot: snapshot);
        return state;
    }

    private static VmState<TGasPolicy> Rent()
        => _statePool.TryDequeue(out VmState<TGasPolicy>? state) ? state : new VmState<TGasPolicy>();

    [SkipLocalsInit]
    private void Initialize(
        TGasPolicy gas,
        long outputDestination,
        long outputLength,
        ExecutionType executionType,
        bool isTopLevel,
        bool isStatic,
        bool isCreateOnPreExistingAccount,
        ExecutionEnvironment env,
        in StackAccessTracker stateForAccessLists,
        in Snapshot snapshot)
    {
        _env = env;
        _snapshot = snapshot;
        _accessTracker = stateForAccessLists;
        if (executionType.IsAnyCreate())
        {
            _accessTracker.WasCreated(env.ExecutingAccount);
        }
        _accessTracker.TakeSnapshot();
        Gas = gas;
        InitialStateReservoir = TGasPolicy.GetStateReservoir(in gas);
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

        if (!_isDisposed)
        {
            ThrowIsInUse();
        }
        _isDisposed = false;

#if DEBUG
        _creationStackTrace = new StackTrace();
#endif
        [DoesNotReturn, StackTraceHidden]
        static void ThrowIsInUse()
        {
            throw new InvalidOperationException("Already in use");
        }
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

    public void InitializeStacks()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (DataStack is null)
        {
            DataStack = _stackPool.RentStacks();
        }
    }

    public void CommitToParent(VmState<TGasPolicy> parentState)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        parentState.Refund += Refund;
        _canRestore = false; // we can't restore if we committed

        if (Out.IsTargetBlock)
            Out.Log($"evm refund currentState={parentState.Refund} previousState={Refund}");
    }
}
