// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
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

    /*
    Type layout for 'EvmState'
    Size: 264 bytes. Paddings: 9 bytes (%3 of empty space)
    |=======================================================================|
    | Object Header (8 bytes)                                               |
    |-----------------------------------------------------------------------|
    | Method Table Ptr (8 bytes)                                            |
    |=======================================================================|
    |   0-7: Byte[] DataStack (8 bytes)                                     |
    |-----------------------------------------------------------------------|
    |  8-15: ReturnState[] ReturnStack (8 bytes)                            |
    |-----------------------------------------------------------------------|
    | 16-23: Int64 <GasAvailable>k__BackingField (8 bytes)                  |
    |-----------------------------------------------------------------------|
    | 24-31: Int64 <OutputDestination>k__BackingField (8 bytes)             |
    |-----------------------------------------------------------------------|
    | 32-39: Int64 <OutputLength>k__BackingField (8 bytes)                  |
    |-----------------------------------------------------------------------|
    | 40-47: Int64 <Refund>k__BackingField (8 bytes)                        |
    |-----------------------------------------------------------------------|
    | 48-51: Int32 DataStackHead (4 bytes)                                  |
    |-----------------------------------------------------------------------|
    | 52-55: Int32 ReturnStackHead (4 bytes)                                |
    |-----------------------------------------------------------------------|
    | 56-59: Int32 <ProgramCounter>k__BackingField (4 bytes)                |
    |-----------------------------------------------------------------------|
    | 60-63: Int32 <FunctionIndex>k__BackingField (4 bytes)                 |
    |-----------------------------------------------------------------------|
    |    64: ExecutionType <ExecutionType>k__BackingField (1 byte)          |
    |-----------------------------------------------------------------------|
    |    65: Boolean <IsTopLevel>k__BackingField (1 byte)                   |
    |-----------------------------------------------------------------------|
    |    66: Boolean _canRestore (1 byte)                                   |
    |-----------------------------------------------------------------------|
    |    67: Boolean <IsStatic>k__BackingField (1 byte)                     |
    |-----------------------------------------------------------------------|
    |    68: Boolean <IsContinuation>k__BackingField (1 byte)               |
    |-----------------------------------------------------------------------|
    |    69: Boolean <IsCreateOnPreExistingAccount>k__BackingField (1 byte) |
    |-----------------------------------------------------------------------|
    |    70: Boolean _isDisposed (1 byte)                                   |
    |-----------------------------------------------------------------------|
    |    71: padding (1 byte)                                               |
    |-----------------------------------------------------------------------|
    | 72-103: EvmPooledMemory _memory (32 bytes)                            |
    |-----------------------------------------------------------------------|
    | 104-223: ExecutionEnvironment _env (120 bytes)                        |
    |-----------------------------------------------------------------------|
    | 224-247: StackAccessTracker _accessTracker (24 bytes)                 |
    |-----------------------------------------------------------------------|
    | 248-259: Snapshot _snapshot (12 bytes)                                |
    |-----------------------------------------------------------------------|
    | 260-263: padding (4 bytes)                                            |
    |=======================================================================|
     */

    public byte[]? DataStack;
    public ReturnState[]? ReturnStack;
    public long GasAvailable { get; set; }
    internal long OutputDestination { get; private set; } // TODO: move to CallEnv
    internal long OutputLength { get; private set; } // TODO: move to CallEnv
    public long Refund { get; set; }
    public int DataStackHead;
    public int ReturnStackHead;
    internal ExecutionType ExecutionType { get; private set; } // TODO: move to CallEnv
    public int ProgramCounter { get; set; }
    public int FunctionIndex { get; set; }
    public bool IsTopLevel { get; private set; } // TODO: move to CallEnv
    private bool _canRestore;
    public bool IsStatic { get; private set; } // TODO: move to CallEnv
    public bool IsContinuation { get; set; } // TODO: move to CallEnv
    public bool IsCreateOnPreExistingAccount { get; private set; } // TODO: move to CallEnv

    private bool _isDisposed = true;

    private EvmPooledMemory _memory;
    private ExecutionEnvironment _env;
    private StackAccessTracker _accessTracker;
    private Snapshot _snapshot;

#if DEBUG
    private StackTrace? _creationStackTrace;
#endif

    /// <summary>
    /// Rent a top level <see cref="EvmState"/>.
    /// </summary>
    public static EvmState RentTopLevel(
        long gasAvailable,
        ExecutionType executionType,
        in ExecutionEnvironment env,
        in StackAccessTracker accessedItems,
        in Snapshot snapshot)
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
            env: env,
            stateForAccessLists: accessedItems,
            snapshot: snapshot);
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
        in ExecutionEnvironment env,
        in StackAccessTracker stateForAccessLists,
        in Snapshot snapshot)
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
            env: env,
            stateForAccessLists: stateForAccessLists,
            snapshot: snapshot);
        return state;
    }

    private static EvmState Rent()
        => _statePool.TryDequeue(out EvmState state) ? state : new EvmState();

    [SkipLocalsInit]
    private void Initialize(
        long gasAvailable,
        long outputDestination,
        long outputLength,
        ExecutionType executionType,
        bool isTopLevel,
        bool isStatic,
        bool isCreateOnPreExistingAccount,
        in ExecutionEnvironment env,
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
        GasAvailable = gasAvailable;
        OutputDestination = outputDestination;
        OutputLength = outputLength;
        Refund = 0;
        DataStackHead = 0;
        ReturnStackHead = 0;
        ProgramCounter = 0;
        FunctionIndex = 0;
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
        [DoesNotReturn]
        [StackTraceHidden]
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
    internal bool IsPrecompile => Env.CodeInfo?.IsPrecompile ?? false;

    public ref readonly StackAccessTracker AccessTracker => ref _accessTracker;
    public ref readonly ExecutionEnvironment Env => ref _env;
    public ref EvmPooledMemory Memory => ref _memory; // TODO: move to CallEnv
    public ref readonly Snapshot Snapshot => ref _snapshot; // TODO: move to CallEnv

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

        if (_canRestore)
        {
            // if we didn't commit and we are not top level, then we need to restore and drop the changes done in this call
            _accessTracker.Restore();
        }
        _memory.Dispose();
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

    public struct ReturnState
    {
        public int Index;
        public int Offset;
        public int Height;
    }
}
