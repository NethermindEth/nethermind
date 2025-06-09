// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
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
    private readonly IBlockhashProvider _blockhashProvider = new TestBlockhashProvider(MainnetSpecProvider.Instance);
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
        IWorldStateManager worldStateManager = TestWorldStateFactory.CreateForTest();
        _stateProvider = worldStateManager.GlobalWorldState;
        _stateProvider.CreateAccount(Address.Zero, 1000.Ether());
        _stateProvider.Commit(_spec);

        Console.WriteLine(MuirGlacier.Instance);
        CodeInfoRepository codeInfoRepository = new();
        _virtualMachine = new VirtualMachine(_blockhashProvider, MainnetSpecProvider.Instance, new OneLoggerLogManager(NullLogger.Instance));
        _virtualMachine.SetBlockExecutionContext(new BlockExecutionContext(_header, _spec));
        _virtualMachine.SetTxExecutionContext(new TxExecutionContext(Address.Zero, codeInfoRepository, null, 0));

        _environment = new ExecutionEnvironment
        (
            executingAccount: Address.Zero,
            codeSource: Address.Zero,
            caller: Address.Zero,
            codeInfo: new CodeInfo(_bytecode.Concat(_bytecode).Concat(_bytecode).Concat(_bytecode).ToArray()),
            callDepth: 0,
            value: 0,
            transferValue: 0,
            inputData: default
        );

        _evmState = EvmState.RentTopLevel(100_000_000L, ExecutionType.TRANSACTION, _environment, new StackAccessTracker(), _stateProvider.TakeSnapshot());
    }

    [Benchmark]
    public void ExecuteCode()
    {
        _virtualMachine.ExecuteTransaction<OffFlag>(_evmState, _stateProvider, _txTracer);
        _stateProvider.Reset();
    }

    [Benchmark(Baseline = true)]
    public void No_machine_running()
    {
        _stateProvider.Reset();
    }
}
