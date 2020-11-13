//  Copyright (c) 2018 Demerzel Solutions Limited
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
using System.Collections.Immutable;
using System.Diagnostics;
using System.Threading;
using Nethermind.Core;
using Nethermind.State;

namespace Nethermind.Evm
{
    [DebuggerDisplay("{ExecutionType} to {Env.ExecutingAccount}, G {GasAvailable} R {Refund} PC {ProgramCounter} OUT {OutputDestination}:{OutputLength}")]
    public class EvmState : IDisposable
    {
        private class StackPool
        {
            private readonly int _maxCallStackDepth;

            // TODO: we have wrong call depth calculation somewhere
            public StackPool(int maxCallStackDepth = VirtualMachine.MaxCallDepth * 2)
            {
                _maxCallStackDepth = maxCallStackDepth;
            }

            private readonly ConcurrentStack<byte[]> _dataStackPool = new ConcurrentStack<byte[]>();
            private readonly ConcurrentStack<int[]> _returnStackPool = new ConcurrentStack<int[]>();

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
                if (_dataStackPool.Count == 0)
                {
                    Interlocked.Increment(ref _dataStackPoolDepth);
                    if (_dataStackPoolDepth > _maxCallStackDepth)
                    {
                        throw new Exception();
                    }

                    _dataStackPool.Push(new byte[(EvmStack.MaxStackSize + EvmStack.RegisterLength) * 32]);
                }

                _dataStackPool.TryPop(out byte[] result);
                return result;
            }
            
            private int[] RentReturnStack()
            {
                if (_returnStackPool.Count == 0)
                {
                    Interlocked.Increment(ref _returnStackPoolDepth);
                    if (_returnStackPoolDepth > _maxCallStackDepth)
                    {
                        throw new Exception();
                    }

                    _returnStackPool.Push(new int[EvmStack.ReturnStackSize]);
                }

                _returnStackPool.TryPop(out int[] result);
                return result;
            }
            
            public (byte[], int[]) RentStacks()
            {
                return (RentDataStack(), RentReturnStack());
            }
        }

        private static readonly ThreadLocal<StackPool> _stackPool = new ThreadLocal<StackPool>(() => new StackPool());

        public byte[] DataStack;
        public int[] ReturnStack;

        private HashSet<Address> _accessedAddresses;
        private HashSet<StorageCell> _accessedStorageKeys;
        private HashSet<Address> _destroyList;
        private List<LogEntry> _logs;
        
        public int DataStackHead = 0;
        public int ReturnStackHead = 0;

        public EvmState(long gasAvailable, ExecutionEnvironment env, ExecutionType executionType, bool isTopLevel, bool isContinuation)
            : this(gasAvailable, env, executionType, isTopLevel, -1, -1, 0L, 0L, false, isContinuation, false)
        {
            GasAvailable = gasAvailable;
            Env = env;
        }

        internal EvmState(
            long gasAvailable,
            ExecutionEnvironment env,
            ExecutionType executionType,
            bool isTopLevel,
            int stateSnapshot,
            int storageSnapshot,
            long outputDestination,
            long outputLength,
            bool isStatic,
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
            StateSnapshot = stateSnapshot;
            StorageSnapshot = storageSnapshot;
            Env = env;
            OutputDestination = outputDestination;
            OutputLength = outputLength;
            IsStatic = isStatic;
            IsContinuation = isContinuation;
            IsCreateOnPreExistingAccount = isCreateOnPreExistingAccount;
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
                        return Env.Sender;
                    case ExecutionType.Create2:
                        return Env.Sender;
                    case ExecutionType.DelegateCall:
                        return Env.ExecutingAccount;
                    case ExecutionType.Transaction:
                        return Env.Originator;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }
        }

        public Address To => Env.CodeSource;
        
        public ExecutionEnvironment Env { get; }
        public long GasAvailable { get; set; }
        public int ProgramCounter { get; set; }
        internal ExecutionType ExecutionType { get; }
        internal bool IsPrecompile => Env.CodeInfo.IsPrecompile;
        public bool IsTopLevel { get; }
        internal long OutputDestination { get; }
        internal long OutputLength { get; }
        public bool IsStatic { get; }
        public bool IsContinuation { get; set; }
        public bool IsCreateOnPreExistingAccount { get; }
        public int StateSnapshot { get; }
        public int StorageSnapshot { get; }
        public long Refund { get; set; }
        public EvmPooledMemory Memory { get; private set; }

        public HashSet<Address> DestroyList
        {
            get { return LazyInitializer.EnsureInitialized(ref _destroyList, () => new HashSet<Address>()); }
        }

        public List<LogEntry> Logs
        {
            get { return LazyInitializer.EnsureInitialized(ref _logs, () => new List<LogEntry>()); }
        }

        public void Dispose()
        {
            if (DataStack != null) _stackPool.Value.ReturnStacks(DataStack, ReturnStack);
            Memory?.Dispose();
        }

        public void InitStacks()
        {
            if (DataStack == null)
            {
                Memory = new EvmPooledMemory();
                (DataStack, ReturnStack) = _stackPool.Value.RentStacks();
            }
        }

        public bool IsCold(Address address)
        {
            return _accessedAddresses == null || !AccessedAddresses.Contains(address);
        }
        
        public bool IsCold(StorageCell storageCell)
        {
            return _accessedStorageKeys == null || !AccessedStorageCells.Contains(storageCell);
        }

        public void WarmUp(ISet<Address> addresses, ISet<StorageCell> storageCells)
        {
            foreach (Address address in addresses)
            {
                WarmUp(address);
            }
                    
            foreach (StorageCell storageCell in storageCells)
            {
                WarmUp(storageCell);
            }
        }

        public void WarmUp(Address address)
        {
            AccessedAddresses.Add(address);
        }
        
        public void WarmUp(StorageCell storageCell)
        {
            AccessedStorageCells.Add(storageCell);
        }

        // TODO: all of this should be done via checkpoints the same way as storage and state changes in general
        public void CommitToParent(EvmState parentState)
        {
            parentState.Refund += Refund;
            
            if (_destroyList != null)
            {
                foreach (Address address in DestroyList)
                {
                    parentState.DestroyList.Add(address);
                }
            }

            if (_logs != null)
            {
                for (int i = 0; i < Logs.Count; i++)
                {
                    LogEntry logEntry = Logs[i];
                    parentState.Logs.Add(logEntry);
                }
            }

            if (_accessedAddresses != null)
            {
                foreach (Address address in AccessedAddresses)
                {
                    parentState.AccessedAddresses.Add(address);
                }
            }

            if (_accessedStorageKeys != null)
            {
                foreach (StorageCell storageCell in AccessedStorageCells)
                {
                    parentState.AccessedStorageCells.Add(storageCell);
                }
            }
        }
        
        /// <summary>
        /// EIP-2929 accessed addresses
        /// </summary>
        private HashSet<Address> AccessedAddresses
        {
            get { return LazyInitializer.EnsureInitialized(ref _accessedAddresses, () => new HashSet<Address>()); }
        }
        
        /// <summary>
        /// EIP-2929 accessed storage keys
        /// </summary>
        private HashSet<StorageCell> AccessedStorageCells
        {
            get { return LazyInitializer.EnsureInitialized(ref _accessedStorageKeys, () => new HashSet<StorageCell>()); }
        }
    }
}