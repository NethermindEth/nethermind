// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using Nethermind.Core;
using Nethermind.State;

namespace Nethermind.Evm
{
    /// <summary>
    /// State for EVM Calls
    /// </summary>
    [DebuggerDisplay("{ExecutionType} to {Env.ExecutingAccount}, G {GasAvailable} R {Refund} PC {ProgramCounter} OUT {OutputDestination}:{OutputLength}")]
    public class EvmState : IDisposable // TODO: rename to CallState
    {
        private static readonly StackPool _stackPool = new();

        public byte[]? DataStack;
        public int[]? ReturnStack;

        private readonly StackAccessTracker _accessTracker;
        private readonly ExecutionEnvironment _env;
        private EvmPooledMemory _memory;

        public int DataStackHead = 0;

        public int ReturnStackHead = 0;
        private bool _canRestore = true;

        public long GasAvailable { get; set; }
        public int ProgramCounter { get; set; }
        public long Refund { get; set; }

        internal ExecutionType ExecutionType { get; } // TODO: move to CallEnv
        public bool IsTopLevel { get; } // TODO: move to CallEnv
        internal long OutputDestination { get; } // TODO: move to CallEnv
        internal long OutputLength { get; } // TODO: move to CallEnv
        public bool IsStatic { get; } // TODO: move to CallEnv
        public bool IsContinuation { get; set; } // TODO: move to CallEnv
        public bool IsCreateOnPreExistingAccount { get; } // TODO: move to CallEnv
        public Snapshot Snapshot { get; } // TODO: move to CallEnv

        /// <summary>
        /// Constructor for a top level <see cref="EvmState"/>.
        /// </summary>
        public EvmState(
            long gasAvailable,
            in ExecutionEnvironment env,
            ExecutionType executionType,
            in Snapshot snapshot,
            in StackAccessTracker accessedItems) : this(gasAvailable,
                                    env,
                                    executionType,
                                    isTopLevel: true,
                                    snapshot,
                                    outputDestination: 0L,
                                    outputLength: 0L,
                                    isStatic: false,
                                    accessedItems,
                                    isCreateOnPreExistingAccount: false)
        {
        }
        /// <summary>
        /// Constructor for a frame <see cref="EvmState"/> beneath top level.
        /// </summary>
        internal EvmState(
            long gasAvailable,
            in ExecutionEnvironment env,
            ExecutionType executionType,
            in Snapshot snapshot,
            long outputDestination,
            long outputLength,
            bool isStatic,
            in StackAccessTracker stateForAccessLists,
            bool isCreateOnPreExistingAccount) :
            this(
                gasAvailable,
                env,
                executionType,
                isTopLevel: false,
                snapshot,
                outputDestination,
                outputLength,
                isStatic,
                stateForAccessLists,
                isCreateOnPreExistingAccount)
        {

        }
        private EvmState(
            long gasAvailable,
            in ExecutionEnvironment env,
            ExecutionType executionType,
            bool isTopLevel,
            in Snapshot snapshot,
            long outputDestination,
            long outputLength,
            bool isStatic,
            in StackAccessTracker stateForAccessLists,
            bool isCreateOnPreExistingAccount)
        {
            GasAvailable = gasAvailable;
            ExecutionType = executionType;
            IsTopLevel = isTopLevel;
            _canRestore = !isTopLevel;
            Snapshot = snapshot;
            _env = env;
            OutputDestination = outputDestination;
            OutputLength = outputLength;
            IsStatic = isStatic;
            IsContinuation = false;
            IsCreateOnPreExistingAccount = isCreateOnPreExistingAccount;
            _accessTracker = new(stateForAccessLists);
            if (executionType.IsAnyCreate())
            {
                _accessTracker.WasCreated(env.ExecutingAccount);
            }
            _accessTracker.TakeSnapshot();
        }

        public Address From
        {
            get
            {
                return ExecutionType switch
                {
                    ExecutionType.STATICCALL or ExecutionType.CALL or ExecutionType.CALLCODE or ExecutionType.CREATE or ExecutionType.CREATE2 or ExecutionType.TRANSACTION => Env.Caller,
                    ExecutionType.DELEGATECALL => Env.ExecutingAccount,
                    _ => throw new ArgumentOutOfRangeException(),
                };
            }
        }

        public Address To => Env.CodeSource ?? Env.ExecutingAccount;
        internal bool IsPrecompile => Env.CodeInfo.IsPrecompile;
        public ref readonly StackAccessTracker AccessTracker => ref _accessTracker;
        public ref readonly ExecutionEnvironment Env => ref _env;
        public ref EvmPooledMemory Memory => ref _memory; // TODO: move to CallEnv

        public void Dispose()
        {
            if (DataStack is not null)
            {
                // Only Dispose once
                _stackPool.ReturnStacks(DataStack, ReturnStack!);
                DataStack = null;
                ReturnStack = null;
            }
            Restore(); // we are trying to restore when disposing
            Memory.Dispose();
            Memory = default;
        }

        public void InitStacks()
        {
            if (DataStack is null)
            {
                (DataStack, ReturnStack) = _stackPool.RentStacks();
            }
        }

        public void CommitToParent(EvmState parentState)
        {
            parentState.Refund += Refund;
            _canRestore = false; // we can't restore if we committed
        }

        private void Restore()
        {
            if (_canRestore) // if we didn't commit and we are not top level, then we need to restore and drop the changes done in this call
            {
                _accessTracker.Restore();
            }
        }
    }
}
