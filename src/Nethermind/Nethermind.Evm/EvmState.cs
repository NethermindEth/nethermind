// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Evm.CodeAnalysis.IL;
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

        public StackAccessTracker AccessTracker => _accessTracker;

        private readonly StackAccessTracker _accessTracker;

        public int DataStackHead = 0;

        public int ReturnStackHead = 0;
        private bool _canRestore = true;
        /// <summary>
        /// Constructor for a top level <see cref="EvmState"/>.
        /// </summary>
        public EvmState(
            long gasAvailable,
            ExecutionEnvironment env,
            ExecutionType executionType,
            Snapshot snapshot,
            in StackAccessTracker accessedItems) : this(gasAvailable,
                                    env,
                                    executionType,
                                    true,
                                    snapshot,
                                    0L,
                                    0L,
                                    false,
                                    accessedItems,
                                    false)
        {
        }
        /// <summary>
        /// Constructor for a top level <see cref="EvmState"/>.
        /// </summary>
        public EvmState(
            long gasAvailable,
            ExecutionEnvironment env,
            ExecutionType executionType,
            Snapshot snapshot) : this(gasAvailable,
                                    env,
                                    executionType,
                                    true,
                                    snapshot,
                                    0L,
                                    0L,
                                    false,
                                    new StackAccessTracker(),
                                    false)
        {
        }
        /// <summary>
        /// Constructor for a frame <see cref="EvmState"/> beneath top level.
        /// </summary>
        internal EvmState(
            long gasAvailable,
            ExecutionEnvironment env,
            ExecutionType executionType,
            Snapshot snapshot,
            long outputDestination,
            long outputLength,
            bool isStatic,
            in StackAccessTracker stateForAccessLists,
            bool isCreateOnPreExistingAccount) :
            this(
                gasAvailable,
                env,
                executionType,
                false,
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
            ExecutionEnvironment env,
            ExecutionType executionType,
            bool isTopLevel,
            Snapshot snapshot,
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
            Env = env;
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

        public long GasAvailable { get; set; }
        public int ProgramCounter { get; set; }
        public long Refund { get; set; }

        public Address To => Env.CodeSource ?? Env.ExecutingAccount;
        internal bool IsPrecompile => Env.CodeInfo.IsPrecompile;
        public readonly ExecutionEnvironment Env;

        internal ExecutionType ExecutionType { get; } // TODO: move to CallEnv
        public bool IsTopLevel { get; } // TODO: move to CallEnv
        internal long OutputDestination { get; } // TODO: move to CallEnv
        internal long OutputLength { get; } // TODO: move to CallEnv
        public bool IsStatic { get; } // TODO: move to CallEnv
        public bool IsContinuation { get; set; } // TODO: move to CallEnv
        public bool IsCreateOnPreExistingAccount { get; } // TODO: move to CallEnv
        public Snapshot Snapshot { get; } // TODO: move to CallEnv

        private EvmPooledMemory _memory;
        public ref EvmPooledMemory Memory => ref _memory; // TODO: move to CallEnv
        internal IPrecompiledContract? ILedContract { get; set; }

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
