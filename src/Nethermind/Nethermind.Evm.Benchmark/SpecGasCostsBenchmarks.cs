// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Specs;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Blockchain;

namespace Nethermind.Evm.Benchmark;

/// <summary>
/// Benchmarks for PR #10591: SpecGasCosts optimization
/// Tests EVM execution with opcodes that rely heavily on gas cost lookups
/// </summary>
[MemoryDiagnoser]
[SimpleJob]
public class SpecGasCostsBenchmarks
{
    private IReleaseSpec _spec = null!;
    private ITxTracer _txTracer = NullTxTracer.Instance;
    private IVirtualMachine _virtualMachine = null!;
    private BlockHeader _header = null!;
    private IBlockhashProvider _blockhashProvider = new TestBlockhashProvider();
    private IWorldState _stateProvider = null!;
    private EthereumCodeInfoRepository _codeInfoRepository = null!;
    private IDisposable _worldStateScope = null!;

    // Bytecode that does many SLOAD operations
    private byte[] _sloadIntensiveBytecode = null!;

    // Bytecode that does many BALANCE operations using CALLER
    private byte[] _balanceIntensiveBytecode = null!;

    // Bytecode that does EXP operations
    private byte[] _expIntensiveBytecode = null!;

    // Simple counter bytecode for baseline
    private byte[] _simpleCounterBytecode = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        // Use Istanbul spec like other working EVM benchmarks
        _spec = MainnetSpecProvider.Instance.GetSpec((ForkActivation)MainnetSpecProvider.IstanbulBlockNumber);
        _header = new BlockHeader(
            Keccak.Zero,
            Keccak.Zero,
            Address.Zero,
            UInt256.One,
            MainnetSpecProvider.IstanbulBlockNumber,
            Int64.MaxValue,
            1UL,
            Bytes.Empty);

        _stateProvider = TestWorldStateFactory.CreateForTest();
        _worldStateScope = _stateProvider.BeginScope(IWorldState.PreGenesis);
        _stateProvider.CreateAccount(Address.Zero, 1000.Ether());
        _stateProvider.Commit(_spec);

        _codeInfoRepository = new EthereumCodeInfoRepository(_stateProvider);
        _virtualMachine = new EthereumVirtualMachine(_blockhashProvider, MainnetSpecProvider.Instance, LimboLogs.Instance);
        _virtualMachine.SetBlockExecutionContext(new BlockExecutionContext(_header, _spec));
        _virtualMachine.SetTxExecutionContext(new TxExecutionContext(Address.Zero, _codeInfoRepository, null, 0));

        BuildBytecodes();
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _worldStateScope?.Dispose();
    }

    private void BuildBytecodes()
    {
        // SLOAD loop - tests SLoadCost
        _sloadIntensiveBytecode = Prepare.EvmCode
            .PushData(0)              // counter = 0
            .Op(Instruction.JUMPDEST) // offset 2: loop start
            .Op(Instruction.DUP1)     // dup counter for SLOAD
            .Op(Instruction.SLOAD)    // load storage[counter]
            .Op(Instruction.POP)      // discard value
            .PushData(1)
            .Op(Instruction.ADD)      // counter++
            .Op(Instruction.DUP1)     // dup counter for comparison
            .PushData(100)
            .Op(Instruction.LT)       // counter < 100?
            .PushData(2)              // jump destination
            .Op(Instruction.JUMPI)    // conditional jump
            .Op(Instruction.STOP)
            .Done;

        // BALANCE loop using CALLER - tests BalanceCost
        _balanceIntensiveBytecode = Prepare.EvmCode
            .PushData(0)              // counter = 0
            .Op(Instruction.JUMPDEST) // offset 2: loop start
            .Op(Instruction.CALLER)   // push caller address
            .Op(Instruction.BALANCE)  // get balance of caller
            .Op(Instruction.POP)      // discard balance
            .PushData(1)
            .Op(Instruction.ADD)      // counter++
            .Op(Instruction.DUP1)     // dup counter for comparison
            .PushData(100)
            .Op(Instruction.LT)       // counter < 100?
            .PushData(2)              // jump destination
            .Op(Instruction.JUMPI)
            .Op(Instruction.STOP)
            .Done;

        // EXP loop - tests ExpByteCost
        _expIntensiveBytecode = Prepare.EvmCode
            .PushData(0)              // counter
            .Op(Instruction.JUMPDEST) // loop start at offset 2
            .PushData(128)            // exponent
            .PushData(2)              // base
            .Op(Instruction.EXP)
            .Op(Instruction.POP)
            .PushData(1)
            .Op(Instruction.ADD)
            .Op(Instruction.DUP1)
            .PushData(100)
            .Op(Instruction.LT)
            .PushData(2)
            .Op(Instruction.JUMPI)
            .Op(Instruction.STOP)
            .Done;

        // Simple counter for baseline
        _simpleCounterBytecode = Prepare.EvmCode
            .PushData(0)
            .Op(Instruction.JUMPDEST)
            .PushData(1)
            .Op(Instruction.ADD)
            .Op(Instruction.DUP1)
            .PushData(1000)
            .Op(Instruction.LT)
            .PushData(2)
            .Op(Instruction.JUMPI)
            .Op(Instruction.STOP)
            .Done;
    }

    private void RunBytecode(byte[] bytecode)
    {
        var environment = ExecutionEnvironment.Rent(
            executingAccount: Address.Zero,
            codeSource: Address.Zero,
            caller: Address.Zero,
            codeInfo: new CodeInfo(bytecode),
            callDepth: 0,
            value: 0,
            transferValue: 0,
            inputData: default
        );

        using var evmState = VmState<EthereumGasPolicy>.RentTopLevel(
            EthereumGasPolicy.FromLong(100_000_000L),
            ExecutionType.TRANSACTION,
            environment,
            new StackAccessTracker(),
            _stateProvider.TakeSnapshot());

        _virtualMachine.ExecuteTransaction<OffFlag>(evmState, _stateProvider, _txTracer);
        _stateProvider.Reset();
        environment.Dispose();
    }

    [Benchmark(Description = "Baseline: Simple counter (1000 iterations)")]
    public void SimpleCounter()
    {
        RunBytecode(_simpleCounterBytecode);
    }

    [Benchmark(Description = "SLOAD intensive (100 storage reads)")]
    public void SLoadIntensive()
    {
        RunBytecode(_sloadIntensiveBytecode);
    }

    [Benchmark(Description = "BALANCE intensive (100 balance checks)")]
    public void BalanceIntensive()
    {
        RunBytecode(_balanceIntensiveBytecode);
    }

    [Benchmark(Description = "EXP intensive (100 EXP operations)")]
    public void ExpIntensive()
    {
        RunBytecode(_expIntensiveBytecode);
    }
}
