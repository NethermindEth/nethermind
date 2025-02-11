// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.State;

namespace Nethermind.Evm;

/// <summary>
/// State for EVM Calls
/// </summary>
[DebuggerDisplay("{Env.ExecutionType} to {Env.ExecutingAccount}, G {GasAvailable} R {Refund} PC {ProgramCounter} OUT {Env.OutputDestination}:{Env.OutputLength}")]
public sealed class EvmState : IDisposable // TODO: rename to CallState
{
    private static readonly ConcurrentQueue<EvmState> _statePool = new();
    private static readonly StackPool _stackPool = new();

    public byte[]? DataStack;
    public int[]? ReturnStack;

    public long GasAvailable { get; set; }
    public long Refund { get; set; }

    public int DataStackHead;
    public int ReturnStackHead;
    public int ProgramCounter { get; set; }
    private bool _canRestore;

    private bool _isDisposed = true;

    private EvmPooledMemory _memory;
    private Snapshot _snapshot;
    private ExecutionEnvironment _env;
    private StackAccessTracker _accessTracker;

#if DEBUG
    private StackTrace? _creationStackTrace;
#endif

    /// <summary>
    /// Rent a top level <see cref="EvmState"/>.
    /// </summary>
    public static EvmState RentTopLevel(
        long gasAvailable,
        ExecutionType executionType,
        in Snapshot snapshot,
        in ExecutionEnvironment env,
        in StackAccessTracker accessedItems)
    {
        var newEnv = new ExecutionEnvironment(
            env.CodeInfo,
            env.ExecutingAccount,
            env.Caller,
            env.CodeSource,
            env.InputData,
            env.TxExecutionContext,
            env.TransferValue,
            env.Value,
            env.CallDepth,
            executionType: executionType,
            isTopLevel: true);

        EvmState state = Rent();
        state.Initialize(
            gasAvailable,
            executionType,
            snapshot: snapshot,
            env: newEnv,
            stateForAccessLists: accessedItems);
        return state;
    }

    /// <summary>
    /// Constructor for a frame <see cref="EvmState"/> beneath top level.
    /// </summary>
    public static EvmState RentFrame(
        long gasAvailable,
        long outputDestination,
        long outputLength,
        ExecutionType executionType,
        bool isStatic,
        bool isCreateOnPreExistingAccount,
        in Snapshot snapshot,
        in ExecutionEnvironment env,
        in StackAccessTracker stateForAccessLists)
    {
        EvmState state = Rent();

        var newEnv = new ExecutionEnvironment(
            env.CodeInfo,
            env.ExecutingAccount,
            env.Caller,
            env.CodeSource,
            env.InputData,
            env.TxExecutionContext,
            env.TransferValue,
            env.Value,
            env.CallDepth,
            outputDestination,
            outputLength,
            executionType,
            isTopLevel: false,
            isStatic: isStatic,
            isContinuation: false,
            isCreateOnPreExistingAccount: isCreateOnPreExistingAccount);

        state.Initialize(
            gasAvailable,
            executionType,
            snapshot: snapshot,
            env: newEnv,
            stateForAccessLists: stateForAccessLists);

        return state;
    }

    private static EvmState Rent() => _statePool.TryDequeue(out EvmState state) ? state : new EvmState();

    private void Initialize(
        long gasAvailable,
        ExecutionType executionType,
        in Snapshot snapshot,
        in ExecutionEnvironment env,
        in StackAccessTracker stateForAccessLists)
    {
        GasAvailable = gasAvailable;
        Refund = 0;
        DataStackHead = 0;
        ReturnStackHead = 0;
        ProgramCounter = 0;
        _canRestore = !env.IsTopLevel;
        _snapshot = snapshot;
        _env = env;
        _accessTracker = new(stateForAccessLists);
        if (executionType.IsAnyCreate())
        {
            _accessTracker.WasCreated(env.ExecutingAccount);
        }
        _accessTracker.TakeSnapshot();

        // Should be disposed when being initialized
        if (!_isDisposed)
        {
            ThrowIfNotUninitialized();
        }
        // Mark revived
        _isDisposed = false;

#if DEBUG
        _creationStackTrace = new();
#endif

        [DoesNotReturn]
        [StackTraceHidden]
        static void ThrowIfNotUninitialized()
        {
            throw new InvalidOperationException("Already in use");
        }
    }

    public Address From => _env.ExecutionType switch
    {
        ExecutionType.STATICCALL or ExecutionType.CALL or ExecutionType.CALLCODE or ExecutionType.CREATE
            or ExecutionType.CREATE2 or ExecutionType.TRANSACTION => _env.Caller,
        ExecutionType.DELEGATECALL => _env.ExecutingAccount,
        _ => throw new ArgumentOutOfRangeException(),
    };

    public Address To => _env.CodeSource ?? _env.ExecutingAccount;
    internal bool IsPrecompile => _env.CodeInfo.IsPrecompile;
    public ref readonly StackAccessTracker AccessTracker => ref _accessTracker;
    public ref readonly ExecutionEnvironment Env => ref _env;
    public ref EvmPooledMemory Memory => ref _memory;
    public ref readonly Snapshot Snapshot => ref _snapshot;

    public void Dispose()
    {
        // Shouldn't be called multiple times
        Debug.Assert(!_isDisposed);

        if (_isDisposed) return;

        _isDisposed = true;
        if (DataStack is not null)
        {
            // Only return if initialized
            _stackPool.ReturnStacks(DataStack, ReturnStack!);
            DataStack = null;
            ReturnStack = null;
        }
        if (_canRestore)
        {
            // if we didn't commit and we are not top level, then we need to restore and drop the changes done in this call
            _accessTracker.Restore();
        }
        _memory.Dispose();
        // Blank refs to not hold against GC
        _memory = default;
        _accessTracker = default;
        _env = default;
        _snapshot = default;

        _statePool.Enqueue(this);

#if DEBUG
        GC.SuppressFinalize(this);
#endif
    }

#if DEBUG
    ~EvmState()
    {
        if (!_isDisposed)
        {
            throw new InvalidOperationException($"{nameof(EvmState)} hasn't been disposed. Created {_creationStackTrace}");
        }
    }
#endif

    public void InitializeStacks()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        if (DataStack is null)
        {
            (DataStack, ReturnStack) = _stackPool.RentStacks();
        }
    }

    public void CommitToParent(EvmState parentState)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        parentState.Refund += Refund;
        _canRestore = false; // we can't restore if we committed
    }
}
