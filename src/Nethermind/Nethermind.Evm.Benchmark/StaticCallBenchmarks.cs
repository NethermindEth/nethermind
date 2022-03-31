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
using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Specs;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.Trie.Pruning;

namespace Nethermind.Evm.Benchmark
{
    [MemoryDiagnoser]
    public class StaticCallBenchmarks
    {
        private IReleaseSpec _spec = MainnetSpecProvider.Instance.GetSpec(MainnetSpecProvider.IstanbulBlockNumber);
        private ITxTracer _txTracer = NullTxTracer.Instance;
        private ExecutionEnvironment _environment;
        private IVirtualMachine _virtualMachine;
        private BlockHeader _header = new BlockHeader(Keccak.Zero, Keccak.Zero, Address.Zero, UInt256.One, MainnetSpecProvider.MuirGlacierBlockNumber, Int64.MaxValue, UInt256.One, Bytes.Empty);
        private IBlockhashProvider _blockhashProvider = new TestBlockhashProvider();
        private EvmState _evmState;
        private StateProvider _stateProvider;
        private StorageProvider _storageProvider;
        private WorldState _worldState;

        public IEnumerable<byte[]> Bytecodes 
        {
            get
            {
                yield return bytecode1;
                yield return bytecode2;
            }
        }

        byte[] bytecode1 = Prepare.EvmCode
            .Op(Instruction.JUMPDEST)
            .PushData(0)
            .Op(Instruction.DUP1)
            .Op(Instruction.DUP1)
            .Op(Instruction.DUP1)
            .PushData(4)
            .Op(Instruction.GAS)
            .Op(Instruction.STATICCALL)
            .Op(Instruction.POP)
            .PushData(0)
            .Op(Instruction.JUMP)
            .Done;
        
        byte[] bytecode2 = Prepare.EvmCode
            .Op(Instruction.JUMPDEST)
            .PushData(0)
            .Op(Instruction.DUP1)
            .Op(Instruction.DUP1)
            .Op(Instruction.DUP1)
            .PushData(4)
            .Op(Instruction.GAS)
            .Op(Instruction.POP)
            .Op(Instruction.POP)
            .Op(Instruction.POP)
            .Op(Instruction.POP)
            .Op(Instruction.POP)
            .Op(Instruction.POP)
            .PushData(0)
            .Op(Instruction.JUMP)
            .Done;
        
        [ParamsSource(nameof(Bytecodes))]
        public byte[] Bytecode { get; set; }

        [GlobalSetup]
        public void GlobalSetup()
        { 
            TrieStore trieStore = new(new MemDb(), new OneLoggerLogManager(NullLogger.Instance));
            IKeyValueStore codeDb = new MemDb();
            
            _stateProvider = new StateProvider(trieStore, codeDb, new OneLoggerLogManager(NullLogger.Instance));
            _stateProvider.CreateAccount(Address.Zero, 1000.Ether());
            _stateProvider.Commit(_spec);
            
            _storageProvider = new StorageProvider(trieStore, _stateProvider, new OneLoggerLogManager(NullLogger.Instance));
            
            _worldState = new WorldState(_stateProvider, _storageProvider);
            Console.WriteLine(MuirGlacier.Instance);
            _virtualMachine = new VirtualMachine(_blockhashProvider, MainnetSpecProvider.Instance, new OneLoggerLogManager(NullLogger.Instance));
            
            _environment = new ExecutionEnvironment
            {
                ExecutingAccount = Address.Zero,
                CodeSource = Address.Zero,
                Caller = Address.Zero,
                CodeInfo = new CodeInfo(Bytecode),
                Value = 0,
                TransferValue = 0,
                TxExecutionContext = new TxExecutionContext(_header, Address.Zero, 0)
            };
        
            _evmState = new EvmState(100_000_000L, _environment, ExecutionType.Transaction, true, _worldState.TakeSnapshot(), false);
        }

        [Benchmark]
        public void ExecuteCode()
        {
            _virtualMachine.Run(_evmState, _worldState, _txTracer);
            _stateProvider.Reset();
            _storageProvider.Reset();
        }
        
        [Benchmark(Baseline = true)]
        public void No_machine_running()
        {
            _stateProvider.Reset();
            _storageProvider.Reset();
        }
    }
}
