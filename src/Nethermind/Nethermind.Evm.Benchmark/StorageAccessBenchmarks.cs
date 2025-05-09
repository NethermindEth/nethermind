// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
    public class StorageAccessBenchmarks
    {
        private readonly IReleaseSpec _spec = MainnetSpecProvider.Instance.GetSpec(MainnetSpecProvider.PragueActivation);
        private readonly ITxTracer _txTracer = NullTxTracer.Instance;
        private ExecutionEnvironment _environment;
        private IVirtualMachine _virtualMachine;
        private readonly BlockHeader _header = new(Keccak.Zero, Keccak.Zero, Address.Zero, UInt256.One, MainnetSpecProvider.MuirGlacierBlockNumber, Int64.MaxValue, 1UL, Bytes.Empty);
        private readonly IBlockhashProvider _blockhashProvider = new TestBlockhashProvider(MainnetSpecProvider.Instance);
        private WorldState _stateProvider;

        public IEnumerable<byte[]> Bytecodes
        {
            get
            {
                yield return _fourStoresFourLoads;
                yield return _fourStoresFourLoadsSameCell;
                yield return _eightLoads;
            }
        }

        private readonly byte[] _fourStoresFourLoads = Prepare.EvmCode
            .PushData(1)
            .PushData(1)
            .Op(Instruction.SSTORE)

            .PushData(2)
            .PushData(2)
            .Op(Instruction.SSTORE)

            .PushData(3)
            .PushData(3)
            .Op(Instruction.SSTORE)

            .PushData(4)
            .PushData(4)
            .Op(Instruction.SSTORE)

            // 5 should have the mapping to 6, 6 to 7 etc.
            .PushData(5)
            .Op(Instruction.SLOAD)
            .Op(Instruction.SLOAD)
            .Op(Instruction.SLOAD)
            .Op(Instruction.SLOAD)
            .Op(Instruction.POP)
            .Done;

        private const byte Key = 1;
        private readonly byte[] _fourStoresFourLoadsSameCell = Prepare.EvmCode
            .PushData(Key)
            .Op(Instruction.SLOAD)
            .Op(Instruction.POP)

            .PushData(1)
            .PushData(Key)
            .Op(Instruction.SSTORE)

            .PushData(Key)
            .Op(Instruction.SLOAD)
            .Op(Instruction.POP)

            .PushData(2)
            .PushData(Key)
            .Op(Instruction.SSTORE)

            .PushData(Key)
            .Op(Instruction.SLOAD)
            .Op(Instruction.POP)

            .PushData(3)
            .PushData(Key)
            .Op(Instruction.SSTORE)

            .PushData(Key)
            .Op(Instruction.SLOAD)
            .Op(Instruction.POP)

            .PushData(4)
            .PushData(Key)
            .Op(Instruction.SSTORE)

            .Done;

        private readonly byte[] _eightLoads = Prepare.EvmCode
            // load 4
            .PushData(5)
            .Op(Instruction.SLOAD)
            .Op(Instruction.SLOAD)
            .Op(Instruction.SLOAD)
            .Op(Instruction.SLOAD)
            .Op(Instruction.POP)
            // roll again
            .PushData(5)
            .Op(Instruction.SLOAD)
            .Op(Instruction.SLOAD)
            .Op(Instruction.SLOAD)
            .Op(Instruction.SLOAD)
            .Op(Instruction.POP)
            .Done;

        [ParamsSource(nameof(Bytecodes))]
        public byte[] Bytecode { get; set; }

        [GlobalSetup]
        public void GlobalSetup()
        {
            var addr = Address.Zero;

            TrieStore trieStore = new(new MemDb(), new OneLoggerLogManager(NullLogger.Instance));
            MemDb codeDb = new MemDb();

            _stateProvider = new WorldState(trieStore, codeDb, new OneLoggerLogManager(NullLogger.Instance));
            _stateProvider.CreateAccount(addr, 1000.Ether());
            _stateProvider.Commit(_spec);

            Console.WriteLine(MuirGlacier.Instance);
            CodeInfoRepository codeInfoRepository = new();
            _virtualMachine = new VirtualMachine(_blockhashProvider, MainnetSpecProvider.Instance, new OneLoggerLogManager(NullLogger.Instance));

            _environment = new ExecutionEnvironment
            (
                executingAccount: addr,
                codeSource: addr,
                caller: addr,
                codeInfo: new CodeInfo(Bytecode),
                value: 0,
                transferValue: 0,
                txExecutionContext: new TxExecutionContext(new BlockExecutionContext(_header, _spec), addr, 0, null, codeInfoRepository),
                inputData: default
            );

            // Make them consecutive
            for (uint i = 0; i < 16; i++)
            {
                _stateProvider.Set(new StorageCell(addr, i), new StorageValue([(byte)(i + 1)]));
            }

            _stateProvider.Commit(Prague.Instance);
            _stateProvider.CommitTree(_header.Number - 1);

        }

        [Benchmark]
        public void Execute()
        {
            using var tracker = new StackAccessTracker();
            using var evmState = EvmState.RentTopLevel(100_000_000L, ExecutionType.TRANSACTION, _stateProvider.TakeSnapshot(), _environment, tracker);

            _virtualMachine.ExecuteTransaction<OffFlag>(evmState, _stateProvider, _txTracer);
            _stateProvider.Reset();
        }
    }
}
