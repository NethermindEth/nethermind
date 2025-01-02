// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using Nethermind.Core;
using Nethermind.State;

namespace Nethermind.Evm;

/// <summary>
/// State for EVM Calls
/// </summary>
[DebuggerDisplay("{ExecutionType} to {Env.ExecutingAccount}, G {GasAvailable} R {Refund} PC {ProgramCounter} OUT {OutputDestination}:{OutputLength}")]
public sealed class EvmState : IDisposable // TODO: rename to CallState
{
    private static readonly ConcurrentQueue<EvmState> _statePool = new();
    private static readonly StackPool _stackPool = new();

    public byte[]? DataStack;
    public int[]? ReturnStack;

    public long GasAvailable { get; set; }
    internal long OutputDestination { get; private set; } // TODO: move to CallEnv
    internal long OutputLength { get; private set; } // TODO: move to CallEnv
    public long Refund { get; set; }

    public int DataStackHead;

    public int ReturnStackHead;
    internal ExecutionType ExecutionType { get; private set; } // TODO: move to CallEnv
    public int ProgramCounter { get; set; }
    public bool IsTopLevel { get; private set; } // TODO: move to CallEnv
    private bool _canRestore;
    public bool IsStatic { get; private set; } // TODO: move to CallEnv
    public bool IsContinuation { get; set; } // TODO: move to CallEnv
    public bool IsCreateOnPreExistingAccount { get; private set; } // TODO: move to CallEnv

    private bool _isDisposed;

    private EvmPooledMemory _memory;
    private Snapshot _snapshot;
    private ExecutionEnvironment _env;
    private StackAccessTracker _accessTracker;

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
        EvmState state = Rent();
        state.Initialize(
            gasAvailable,
            outputDestination: 0L,
            outputLength: 0L,
            executionType: executionType,
            isTopLevel: true,
            isStatic: false,
            isCreateOnPreExistingAccount: false,
            snapshot: snapshot,
            env: env,
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

        state.Initialize(
            gasAvailable,
            outputDestination,
            outputLength,
            executionType,
            isTopLevel: false,
            isStatic: isStatic,
            isCreateOnPreExistingAccount: isCreateOnPreExistingAccount,
            snapshot: snapshot,
            env: env,
            stateForAccessLists: stateForAccessLists);

        return state;
    }

    private static EvmState Rent() => _statePool.TryDequeue(out EvmState state) ? state : new EvmState();

    private void Initialize(
        long gasAvailable,
        long outputDestination,
        long outputLength,
        ExecutionType executionType,
        bool isTopLevel,
        bool isStatic,
        bool isCreateOnPreExistingAccount,
        in Snapshot snapshot,
        in ExecutionEnvironment env,
        in StackAccessTracker stateForAccessLists)
    {
        GasAvailable = gasAvailable;
        OutputDestination = outputDestination;
        OutputLength = outputLength;
        Refund = 0;
        DataStackHead = 0;
        ReturnStackHead = 0;
        ExecutionType = executionType;
        ProgramCounter = 0;
        IsTopLevel = isTopLevel;
        _canRestore = !isTopLevel;
        IsStatic = isStatic;
        IsContinuation = false;
        IsCreateOnPreExistingAccount = isCreateOnPreExistingAccount;
        _snapshot = snapshot;
        _env = env;
        _accessTracker = new(stateForAccessLists);
        if (executionType.IsAnyCreate())
        {
            _accessTracker.WasCreated(env.ExecutingAccount);
        }
        _accessTracker.TakeSnapshot();
        _isDisposed = false;
    }

    public Address From => ExecutionType switch
    {
        ExecutionType.STATICCALL or ExecutionType.CALL or ExecutionType.CALLCODE or ExecutionType.CREATE
            or ExecutionType.CREATE2 or ExecutionType.TRANSACTION => Env.Caller,
        ExecutionType.DELEGATECALL => Env.ExecutingAccount,
        _ => throw new ArgumentOutOfRangeException(),
    };

    public Address To => Env.CodeSource ?? Env.ExecutingAccount;
    internal bool IsPrecompile => Env.CodeInfo.IsPrecompile;
    public ref readonly StackAccessTracker AccessTracker => ref _accessTracker;
    public ref readonly ExecutionEnvironment Env => ref _env;
    public ref EvmPooledMemory Memory => ref _memory; // TODO: move to CallEnv
    public ref readonly Snapshot Snapshot => ref _snapshot; // TODO: move to CallEnv

    public void Dispose()
    {
        if (_isDisposed) return;

        _isDisposed = true;
        if (DataStack is not null)
        {
            // Only Dispose once
            _stackPool.ReturnStacks(DataStack, ReturnStack!);
            DataStack = null;
            ReturnStack = null;
        }
        Restore(); // we are trying to restore when disposing
        _memory.Dispose();
        // Blank refs to not hold against GC
        _memory = default;
        _accessTracker = default;
        _env = default;
        _snapshot = default;

        _statePool.Enqueue(this);
    }

    public void InitStacks()
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

    private void Restore()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        if (_canRestore) // if we didn't commit and we are not top level, then we need to restore and drop the changes done in this call
        {
            _accessTracker.Restore();
        }
    }
}
