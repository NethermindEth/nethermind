// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.Trie.Pruning;

namespace Nethermind.Evm.Benchmark;

public class MultipleUnsignedOperations
{
    private readonly IReleaseSpec _spec = MainnetSpecProvider.Instance.GetSpec((ForkActivation)MainnetSpecProvider.IstanbulBlockNumber);
    private readonly ITxTracer _txTracer = NullTxTracer.Instance;
    private ExecutionEnvironment _environment;
    private IVirtualMachine _virtualMachine;
    private readonly BlockHeader _header = new(Keccak.Zero, Keccak.Zero, Address.Zero, UInt256.One, MainnetSpecProvider.MuirGlacierBlockNumber, Int64.MaxValue, 1UL, Bytes.Empty);
    private readonly IBlockhashProvider _blockhashProvider = new TestBlockhashProvider();
    private EvmState _evmState;
    private IWorldState _stateProvider;

    private readonly byte[] _bytecode = Prepare.EvmCode
        .PushData(2)
        .PushData(2)
        .Op(Instruction.ADD)

        .PushData(2)
        .Op(Instruction.MUL)

        .PushData(2)
        .Op(Instruction.DIV)

        .PushData(2)
        .Op(Instruction.SUB)

        .PushData(2)
        .PushData(2)
        .Op(Instruction.ADDMOD)

        .PushData(2)
        .PushData(2)
        .Op(Instruction.MULMOD)

        .PushData(2)
        .Op(Instruction.LT)

        .PushData(2)
        .Op(Instruction.GT)
        .Op(Instruction.POP)

        .Op(Instruction.GAS)
        .Op(Instruction.POP)

        .Done;

    [GlobalSetup]
    public void GlobalSetup()
    {
        TrieStore trieStore = new(new MemDb(), new OneLoggerLogManager(NullLogger.Instance));
        IKeyValueStore codeDb = new MemDb();

        _stateProvider = new WorldState(trieStore, codeDb, new OneLoggerLogManager(NullLogger.Instance));
        _stateProvider.CreateAccount(Address.Zero, 1000.Ether());
        _stateProvider.Commit(_spec);

        Console.WriteLine(MuirGlacier.Instance);
        _virtualMachine = new VirtualMachine(_blockhashProvider, MainnetSpecProvider.Instance, new OneLoggerLogManager(NullLogger.Instance));

        _environment = new ExecutionEnvironment
        (
            executingAccount: Address.Zero,
            codeSource: Address.Zero,
            caller: Address.Zero,
            codeInfo: new CodeInfo(_bytecode.Concat(_bytecode).Concat(_bytecode).Concat(_bytecode).ToArray()),
            value: 0,
            transferValue: 0,
            txExecutionContext: new TxExecutionContext(_header, Address.Zero, 0, null),
            inputData: default
        );

        _evmState = new EvmState(100_000_000L, _environment, ExecutionType.Transaction, true, _stateProvider.TakeSnapshot(), false);
    }

    [Benchmark]
    public void ExecuteCode()
    {
        _virtualMachine.Run(_evmState, _stateProvider, _txTracer);
        _stateProvider.Reset();
    }

    [Benchmark(Baseline = true)]
    public void No_machine_running()
    {
        _stateProvider.Reset();
    }
}
