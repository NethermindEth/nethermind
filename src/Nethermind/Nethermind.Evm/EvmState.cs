/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using Nethermind.Core;

namespace Nethermind.Evm
{
    [DebuggerDisplay("{ExecutionType} to {Env.ExecutingAccount}, G {GasAvailable} R {Refund} PC {ProgramCounter} OUT {OutputDestination}:{OutputLength}")]
    public class EvmState : IDisposable
    {
        private class StackPool
        {
            private readonly int _capacity;

            public StackPool(int capacity = VirtualMachine.MaxCallDepth * 2) // TODO: we have wrong call depth calculation somehwere
            {
                _capacity = capacity;
            }
            
            private readonly Stack<byte[][]> _bytesOnStackPool = new Stack<byte[][]>();
            private readonly Stack<bool[]> _intPositionsPool = new Stack<bool[]>();
            private readonly Stack<BigInteger[]> _intsOnStackPool = new Stack<BigInteger[]>();

            private int _bytesOnStackCreated;
            private int _intsOnStackCreated;
            private int _intPositionsCreated;

            public void ReturnBytesOnStack(byte[][] bytesOnStack)
            {
                _bytesOnStackPool.Push(bytesOnStack);
            }
            
            public void ReturnIntsOnStack(BigInteger[] intsOnStack)
            {
                _intsOnStackPool.Push(intsOnStack);
            }
            
            public void ReturnIntPositions(bool[] intPositions)
            {
                _intPositionsPool.Push(intPositions);
            }
            
            public byte[][] RentBytesOnStack()
            {
                if (_bytesOnStackPool.Count == 0)
                {
                    _bytesOnStackCreated++;
                    if (_bytesOnStackCreated > _capacity)
                    {
                        throw new Exception();
                    }
                    
                    _bytesOnStackPool.Push(new byte[VirtualMachine.MaxStackSize][]);
                }

                return _bytesOnStackPool.Pop();
            }

            public BigInteger[] RentIntsOnStack()
            {
                if (_intsOnStackPool.Count == 0)
                {
                    _intsOnStackCreated++;
                    if (_intsOnStackCreated > _capacity)
                    {
                        throw new Exception();
                    }
                    
                    _intsOnStackPool.Push(new BigInteger[VirtualMachine.MaxStackSize]);
                }

                return _intsOnStackPool.Pop();
            }

            public bool[] RentIntPositions()
            {
                if (_intPositionsPool.Count == 0)
                {
                    _intPositionsCreated++;
                    if (_intPositionsCreated > _capacity)
                    {
                        throw new Exception();
                    }
                    
                    _intPositionsPool.Push(new bool[VirtualMachine.MaxStackSize]);
                }

                return _intPositionsPool.Pop();
            }
        }
        
        private static readonly StackPool _stackPool = new StackPool();

//        private const int InitialStackSize = 64;
        public byte[][] BytesOnStack = _stackPool.RentBytesOnStack();
        public bool[] IntPositions = _stackPool.RentIntPositions();
        public BigInteger[] IntsOnStack = _stackPool.RentIntsOnStack();

//        private static ArrayPool<byte[]> _arrayPool = ArrayPool<byte[]>.Shared;

        private HashSet<Address> _destroyList = new HashSet<Address>();
        private List<LogEntry> _logs = new List<LogEntry>();
        public int StackHead = 0;

        public EvmState(long gasAvailable, ExecutionEnvironment env, ExecutionType executionType, bool isContinuation)
            : this(gasAvailable, env, executionType, -1, -1, 0L, 0L, false, isContinuation)
        {
            GasAvailable = gasAvailable;
            Env = env;
        }

        internal EvmState(
            long gasAvailable,
            ExecutionEnvironment env,
            ExecutionType executionType,
            int stateSnapshot,
            int storageSnapshot,
            long outputDestination,
            long outputLength,
            bool isStatic,
            bool isContinuation)
        {
            GasAvailable = gasAvailable;
            ExecutionType = executionType;
            StateSnapshot = stateSnapshot;
            StorageSnapshot = storageSnapshot;
            Env = env;
            OutputDestination = outputDestination;
            OutputLength = outputLength;
            IsStatic = isStatic;
            IsContinuation = isContinuation;
        }

        public ExecutionEnvironment Env { get; }
        public long GasAvailable { get; set; }
        public BigInteger ProgramCounter { get; set; }

        internal ExecutionType ExecutionType { get; }
        internal long OutputDestination { get; }
        internal long OutputLength { get; }
        public bool IsStatic { get; }
        public bool IsContinuation { get; set; }
        public int StateSnapshot { get; }
        public int StorageSnapshot { get; }

        public long Refund { get; set; }
        public EvmMemory Memory { get; } = new EvmMemory();

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
            _stackPool.ReturnBytesOnStack(BytesOnStack);
            _stackPool.ReturnIntsOnStack(IntsOnStack);
            _stackPool.ReturnIntPositions(IntPositions);
            Memory.Dispose();
        }
    }
}