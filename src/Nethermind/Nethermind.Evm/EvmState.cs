// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.State;

namespace Nethermind.Evm
{
    /// <summary>
    /// State for EVM Calls
    /// </summary>
    [DebuggerDisplay("{ExecutionType} to {Env.ExecutingAccount}, G {GasAvailable} R {Refund} PC {ProgramCounter} OUT {OutputDestination}:{OutputLength}")]
    public class EvmState : IDisposable // TODO: rename to CallState
    {
        private class StackPool
        {
            private readonly int _maxCallStackDepth;
            private readonly struct StackItem(byte[] dataStack, int[] returnStack)
            {
                public readonly byte[] DataStack = dataStack;
                public readonly int[] ReturnStack = returnStack;
            }

            // TODO: we have wrong call depth calculation somewhere
            public StackPool(int maxCallStackDepth = VirtualMachine.MaxCallDepth * 2)
            {
                _maxCallStackDepth = maxCallStackDepth;
            }

            private readonly Stack<StackItem> _stackPool = new(32);

            private int _stackPoolDepth;

            /// <summary>
            /// The word 'return' acts here once as a verb 'to return stack to the pool' and once as a part of the
            /// compound noun 'return stack' which is a stack of subroutine return values.
            /// </summary>
            /// <param name="dataStack"></param>
            /// <param name="returnStack"></param>
            public void ReturnStacks(byte[] dataStack, int[] returnStack)
            {
                _stackPool.Push(new(dataStack, returnStack));
            }

            public (byte[], int[]) RentStacks()
            {
                if (_stackPool.TryPop(out StackItem result))
                {
                    return (result.DataStack, result.ReturnStack);
                }

                _stackPoolDepth++;
                if (_stackPoolDepth > _maxCallStackDepth)
                {
                    EvmStack.ThrowEvmStackOverflowException();
                }

                return
                (
                    new byte[(EvmStack.MaxStackSize + EvmStack.RegisterLength) * 32],
                    new int[EvmStack.ReturnStackSize]
                );
            }
        }
        private static readonly ThreadLocal<StackPool> _stackPool = new(() => new StackPool());

        public byte[]? DataStack;

        public int[]? ReturnStack;

        /// <summary>
        /// EIP-2929 accessed addresses
        /// </summary>
        public IReadOnlySet<Address> AccessedAddresses => _accessTracker.AccessedAddresses;

        /// <summary>
        /// EIP-2929 accessed storage keys
        /// </summary>
        public IReadOnlySet<StorageCell> AccessedStorageCells => _accessTracker.AccessedStorageCells;

        // As we can add here from VM, we need it as ICollection
        public ICollection<Address> DestroyList => _accessTracker.DestroyList;
        // As we can add here from VM, we need it as ICollection
        public ICollection<AddressAsKey> CreateList => _accessTracker.CreateList;
        // As we can add here from VM, we need it as ICollection
        public ICollection<LogEntry> Logs => _accessTracker.Logs;

        public AccessTracker AccessTracker => _accessTracker;

        private readonly AccessTracker _accessTracker;
        
        public int DataStackHead = 0;

        public int ReturnStackHead = 0;
        private bool _canRestore = true;
        public EvmState(
            long gasAvailable,
            ExecutionEnvironment env,
            ExecutionType executionType,
            bool isTopLevel,
            Snapshot snapshot,
            AccessTracker accessedItems)
            : this(gasAvailable,
                env,
                executionType,
                isTopLevel,
                snapshot,
                0L,
                0L,
                false,
                accessedItems,
                false,
                false)
        {
        }

        internal EvmState(
            long gasAvailable,
            ExecutionEnvironment env,
            ExecutionType executionType,
            bool isTopLevel,
            Snapshot snapshot,
            bool isContinuation)
            : this(gasAvailable,
                env,
                executionType,
                isTopLevel,
                snapshot,
                0L,
                0L,
                false,
                null,
                isContinuation,
                false)
        {
            GasAvailable = gasAvailable;
            Env = env;
        }

        internal EvmState(
            long gasAvailable,
            ExecutionEnvironment env,
            ExecutionType executionType,
            bool isTopLevel,
            Snapshot snapshot,
            long outputDestination,
            long outputLength,
            bool isStatic,
            AccessTracker? stateForAccessLists,
            bool isContinuation,
            bool isCreateOnPreExistingAccount)
        {
            if (isTopLevel && isContinuation)
            {
                throw new InvalidOperationException("Top level continuations are not valid");
            }

            GasAvailable = gasAvailable;
            ExecutionType = executionType;
            IsTopLevel = isTopLevel;
            _canRestore = !isTopLevel;
            Snapshot = snapshot;
            Env = env;
            OutputDestination = outputDestination;
            OutputLength = outputLength;
            IsStatic = isStatic;
            IsContinuation = isContinuation;
            IsCreateOnPreExistingAccount = isCreateOnPreExistingAccount;
            if (stateForAccessLists is not null)
            {
                // if we are sub-call, then we use the existing access list for this transaction
                _accessTracker = new(stateForAccessLists);
            }
            else
            {
                // if we are top level, then we create a new access tracker
                _accessTracker = new();
            }
            if (executionType.IsAnyCreate())
            {
                _accessTracker.CreateList.Add(env.ExecutingAccount);
            }
            _accessTracker.TakeSnapshot();
        }

        public Address From
        {
            get
            {
                switch (ExecutionType)
                {
                    case ExecutionType.STATICCALL:
                    case ExecutionType.CALL:
                    case ExecutionType.CALLCODE:
                    case ExecutionType.CREATE:
                    case ExecutionType.CREATE2:
                    case ExecutionType.TRANSACTION:
                        return Env.Caller;
                    case ExecutionType.DELEGATECALL:
                        return Env.ExecutingAccount;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
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

        public void Dispose()
        {
            if (DataStack is not null)
            {
                // Only Dispose once
                _stackPool.Value.ReturnStacks(DataStack, ReturnStack!);
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
                (DataStack, ReturnStack) = _stackPool.Value.RentStacks();
            }
        }

        public bool IsCold(Address? address) => !_accessTracker.AccessedAddresses.Contains(address);

        public bool IsCold(in StorageCell storageCell) => !_accessTracker.AccessedStorageCells.Contains(storageCell);

        public void WarmUp(Address address) => _accessTracker.Add(address);

        public void WarmUp(in StorageCell storageCell) => _accessTracker.Add(storageCell);

        public void CommitToParent(EvmState parentState)
        {
            parentState.Refund += Refund;
            _canRestore = false; // we can't restore if we commited
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
