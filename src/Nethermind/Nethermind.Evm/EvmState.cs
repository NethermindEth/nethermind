//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Eip2930;
using Nethermind.Int256;
using Nethermind.State;

namespace Nethermind.Evm
{
    [DebuggerDisplay("{ExecutionType} to {Env.ExecutingAccount}, G {GasAvailable} R {Refund} PC {ProgramCounter} OUT {OutputDestination}:{OutputLength}")]
    public class EvmState : IDisposable // TODO: rename to CallState
    {
        private class StackPool
        {
            private readonly int _maxCallStackDepth;

            // TODO: we have wrong call depth calculation somewhere
            public StackPool(int maxCallStackDepth = VirtualMachine.MaxCallDepth * 2)
            {
                _maxCallStackDepth = maxCallStackDepth;
            }

            private readonly ConcurrentStack<byte[]> _dataStackPool = new();
            private readonly ConcurrentStack<int[]> _returnStackPool = new();

            private int _dataStackPoolDepth;
            private int _returnStackPoolDepth;

            /// <summary>
            /// The word 'return' acts here once as a verb 'to return stack to the pool' and once as a part of the
            /// compound noun 'return stack' which is a stack of subroutine return values.  
            /// </summary>
            /// <param name="dataStack"></param>
            /// <param name="returnStack"></param>
            public void ReturnStacks(byte[] dataStack, int[] returnStack)
            {
                _dataStackPool.Push(dataStack);
                _returnStackPool.Push(returnStack);
            }

            private byte[] RentDataStack()
            {
                if (_dataStackPool.TryPop(out byte[] result))
                {
                    return result;
                }

                Interlocked.Increment(ref _dataStackPoolDepth);
                if (_dataStackPoolDepth > _maxCallStackDepth)
                {
                    throw new Exception();
                }

                return new byte[(EvmStack.MaxStackSize + EvmStack.RegisterLength) * 32];
            }
            
            private int[] RentReturnStack()
            {
                if (_returnStackPool.TryPop(out int[] result))
                {
                    return result;
                }

                Interlocked.Increment(ref _returnStackPoolDepth);
                if (_returnStackPoolDepth > _maxCallStackDepth)
                {
                    throw new Exception();
                }

                return new int[EvmStack.ReturnStackSize];
            }
            
            public (byte[], int[]) RentStacks()
            {
                return (RentDataStack(), RentReturnStack());
            }
        }
        private static readonly ThreadLocal<StackPool> _stackPool = new(() => new StackPool());

        public byte[]? DataStack;
       
        public int[]? ReturnStack;

        /// <summary>
        /// EIP-2929 accessed addresses
        /// </summary>
        public IReadOnlySet<Address> AccessedAddresses => _accessedAddresses;

        /// <summary>
        /// EIP-2929 accessed storage keys
        /// </summary>
        public IReadOnlySet<StorageCell> AccessedStorageCells => _accessedStorageCells;
        
        public ICollection<Address> DestroyList => _destroyList;
        public ICollection<LogEntry> Logs => _logs;

        private readonly JournalSet<Address> _accessedAddresses;
        private readonly JournalSet<StorageCell> _accessedStorageCells;
        private readonly JournalCollection<LogEntry> _logs;
        private readonly JournalSet<Address> _destroyList;
        private readonly int _accessedAddressesSnapshot;
        private readonly int _accessedStorageKeysSnapshot;
        private readonly int _destroyListSnapshot;
        private readonly int _logsSnapshot;

        public int DataStackHead = 0;
        
        public int ReturnStackHead = 0;

        public EvmState(
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
            EvmState? stateForAccessLists,
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
            Snapshot = snapshot;
            Env = env;
            OutputDestination = outputDestination;
            OutputLength = outputLength;
            IsStatic = isStatic;
            IsContinuation = isContinuation;
            IsCreateOnPreExistingAccount = isCreateOnPreExistingAccount;
            if (stateForAccessLists is not null)
            {
                _accessedAddresses = stateForAccessLists._accessedAddresses;
                _accessedStorageCells = stateForAccessLists._accessedStorageCells;
                _destroyList = stateForAccessLists._destroyList;
                _logs = stateForAccessLists._logs;
            }
            else
            {
                _accessedAddresses = new JournalSet<Address>();
                _accessedStorageCells = new JournalSet<StorageCell>();
                _destroyList = new JournalSet<Address>();
                _logs = new JournalCollection<LogEntry>();
            }

            _accessedAddressesSnapshot = _accessedAddresses.TakeSnapshot();
            _accessedStorageKeysSnapshot = _accessedStorageCells.TakeSnapshot();
            _destroyListSnapshot = _destroyList.TakeSnapshot();
            _logsSnapshot = _logs.TakeSnapshot();

        }

        public Address From
        {
            get
            {
                switch (ExecutionType)
                {
                    case ExecutionType.StaticCall:
                    case ExecutionType.Call:
                    case ExecutionType.CallCode:
                    case ExecutionType.Create:
                    case ExecutionType.Create2:
                    case ExecutionType.Transaction:
                        return Env.Caller;
                    case ExecutionType.DelegateCall:
                        return Env.ExecutingAccount;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }
        }

        public long GasAvailable { get; set; }
        public int ProgramCounter { get; set; }
        public long Refund { get; set; }
        
        public Address To => Env.CodeSource;
        internal bool IsPrecompile => Env.CodeInfo.IsPrecompile;
        public ExecutionEnvironment Env { get; }
        
        internal ExecutionType ExecutionType { get; } // TODO: move to CallEnv
        public bool IsTopLevel { get; } // TODO: move to CallEnv
        internal long OutputDestination { get; } // TODO: move to CallEnv
        internal long OutputLength { get; } // TODO: move to CallEnv
        public bool IsStatic { get; } // TODO: move to CallEnv
        public bool IsContinuation { get; set; } // TODO: move to CallEnv
        public bool IsCreateOnPreExistingAccount { get; } // TODO: move to CallEnv
        public Snapshot Snapshot { get; } // TODO: move to CallEnv
        public EvmPooledMemory? Memory { get; set; } // TODO: move to CallEnv

        public void Dispose()
        {
            if (DataStack != null) _stackPool.Value.ReturnStacks(DataStack, ReturnStack!);
            Memory?.Dispose();
        }

        public void InitStacks()
        {
            if (DataStack is null)
            {
                Memory = new EvmPooledMemory();
                (DataStack, ReturnStack) = _stackPool.Value.RentStacks();
            }
        }

        public bool IsCold(Address? address) => !_accessedAddresses.Contains(address);
        
        public bool IsCold(StorageCell storageCell) => !_accessedStorageCells.Contains(storageCell);

        public void WarmUp(AccessList? accessList)
        {
            if (accessList != null)
            {
                foreach ((Address address, IReadOnlySet<UInt256> storages) in accessList.Data)
                {
                    WarmUp(address);
                    foreach (UInt256 storage in storages)
                    {
                        WarmUp(new StorageCell(address, storage));
                    }
                }
            }
        }

        public void WarmUp(Address address) => _accessedAddresses.Add(address);

        public void WarmUp(StorageCell storageCell) => _accessedStorageCells.Add(storageCell);

        public void CommitToParent(EvmState parentState)
        {
            parentState.Refund += Refund;
        }

        public void Restore()
        {
            Refund = 0;
            _logs.Restore(_logsSnapshot);
            _destroyList.Restore(_destroyListSnapshot);
            _accessedAddresses.Restore(_accessedAddressesSnapshot);
            _accessedStorageCells.Restore(_accessedStorageKeysSnapshot);
        }
    }
}
