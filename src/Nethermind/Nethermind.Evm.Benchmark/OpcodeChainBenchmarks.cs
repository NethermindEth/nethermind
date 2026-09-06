// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Autofac;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using Nethermind.State;

namespace Nethermind.Evm.Benchmark;

/// <summary>Measures dependent opcode chains through the production interpreter.</summary>
/// <remarks>
/// Results are normalized per executed opcode. Multiply by 3846 for time per contract execution.
/// Frame entry, return and cleanup are amortized across the chain; no harness call separates opcodes.
/// </remarks>
[MemoryDiagnoser]
public class OpcodeChainBenchmarks
{
    private const int BodyOpcodeCount = 3840;
    private const int ExecutedOpcodeCount = BodyOpcodeCount + 6;
    private IContainer _container = null!;
    private ILifetimeScope _processingScope = null!;
    private IDisposable _stateScope = null!;
    private IWorldState _state = null!;
    private IVirtualMachine _vm = null!;
    private ITxTracer _tracer = null!;
    private CodeInfo _code = null!;
    private byte[] _input = new byte[96];

    [Params("Arithmetic", "AddMod", "MulMod", "AddModZero", "Bitwise", "Predicate", "Stack", "Byte", "Shift", "Sar", "Clz", "Environment", "SmallValue", "CallData", "CallDataPartial", "CallDataMissing", "Context", "ReturnDataSize", "PrevRandao", "Memory", "MemoryByte", "MemoryBoundary", "CallReturn", "CallRevert", "JumpTaken", "JumpUntaken", "JumpAlternating")]
    public string Chain { get; set; } = "Arithmetic";

    [Params(false, true)]
    public bool Cancelable { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(Osaka.Instance))
            .Build();
        IWorldStateScopeProvider scopeProvider = _container.Resolve<IWorldStateManager>().GlobalWorldState;
        _processingScope = _container.BeginLifetimeScope(builder => builder.AddSingleton(scopeProvider));
        _state = _processingScope.Resolve<IWorldState>();
        _vm = _processingScope.Resolve<IVirtualMachine>();
        _stateScope = _state.BeginScope(IWorldState.PreGenesis);
        _state.CreateAccount(Address.Zero, UInt256.One);
        if (Chain is "CallReturn" or "CallRevert")
        {
            _state.CreateAccount(TestItem.AddressC, UInt256.One);
            byte[] childCode = [(byte)Instruction.PUSH1, 32, (byte)Instruction.PUSH0,
                (byte)(Chain == "CallRevert" ? Instruction.REVERT : Instruction.RETURN)];
            _state.InsertCode(TestItem.AddressC, childCode, Osaka.Instance);
        }
        _state.Commit(Osaka.Instance);
        _vm.SetBlockExecutionContext(new BlockExecutionContext(Build.A.BlockHeader.TestObject, Osaka.Instance));
        _vm.SetTxExecutionContext(new TxExecutionContext(Address.Zero, _processingScope.Resolve<ICodeInfoRepository>(), null, 0));
        _tracer = Cancelable ? new CancellationTxTracer(NullTxTracer.Instance) : NullTxTracer.Instance;
        _code = BuildCode();

        ReadOnlyMemory<byte> output = ExecuteContract();
        UInt256 expected = Chain switch
        {
            "Arithmetic" => (UInt256)(BodyOpcodeCount / 2 + 1),
            "AddMod" => (UInt256)((BodyOpcodeCount / 4 + 1) % 251),
            "MulMod" => (UInt256)(ulong)System.Numerics.BigInteger.ModPow(3, BodyOpcodeCount / 4, 251),
            "AddModZero" => UInt256.Zero,
            "Predicate" => UInt256.MaxValue,
            "JumpTaken" => UInt256.Zero,
            "Byte" or "Shift" or "Sar" => UInt256.Zero,
            "Clz" => (UInt256)248,
            _ => UInt256.One
        };
        if (_vm.OpCodeCount != ExecutedOpcodeCount || !output.Span.SequenceEqual(expected.ToBigEndian()))
            throw new InvalidOperationException($"Invalid {Chain} chain output or opcode count.");

        int warmupTransactions = Chain is "CallReturn" or "CallRevert" ? 1_000 : 100_000;
        for (int i = 0; i < warmupTransactions; i++) ExecuteContract();
        CodeInfo chain = _code;
        _code = new CodeInfo(new byte[] { (byte)Instruction.STOP });
        // Advance the periodic table refresh after warming the selected workload's instruction bodies.
        for (int i = 0; i < 400_000; i++) ExecuteContract();
        _code = chain;
    }

    [Benchmark(OperationsPerInvoke = ExecutedOpcodeCount)]
    public ReadOnlyMemory<byte> ExecuteContract()
    {
        using ExecutionEnvironment environment = ExecutionEnvironment.Rent(
            executingAccount: Address.Zero, codeSource: Address.Zero, caller: Address.Zero,
            codeInfo: _code, callDepth: 0, value: 0, inputData: _input);
        using VmState<EthereumGasPolicy> state = VmState<EthereumGasPolicy>.RentTopLevel(
            EthereumGasPolicy.FromULong(1_000_000), ExecutionType.TRANSACTION, environment,
            new StackAccessTracker(), _state.TakeSnapshot());
        TransactionSubstate result = _vm.ExecuteTransaction<OffFlag>(state, _state, _tracer);
        if (result.IsError || result.ShouldRevert)
            throw new InvalidOperationException($"Chain execution failed: {result.EvmExceptionType}");
        _state.Reset();
        return result.Output;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _stateScope?.Dispose();
        _processingScope?.Dispose();
        _container?.Dispose();
    }

    private CodeInfo BuildCode()
    {
        List<byte> code = [(byte)Instruction.PUSH1, Chain == "JumpTaken" ? (byte)0 : (byte)1];
        if (Chain is "JumpTaken" or "JumpUntaken" or "JumpAlternating")
        {
            for (int i = 0; i < BodyOpcodeCount / 5; i++)
            {
                code.Add((byte)(Chain == "JumpAlternating" ? Instruction.ISZERO : Instruction.DUP1));
                code.Add((byte)(Chain == "JumpAlternating" ? Instruction.DUP1 : Instruction.ISZERO));
                int destination = code.Count + 4;
                code.AddRange([(byte)Instruction.PUSH2, (byte)(destination >> 8), (byte)destination,
                    (byte)Instruction.JUMPI, (byte)Instruction.JUMPDEST]);
            }
        }
        else
        {
            (byte[] Sequence, int Opcodes) body = Chain switch
            {
                "Arithmetic" => ([(byte)Instruction.PUSH1, 1, (byte)Instruction.ADD], 2),
                "AddMod" or "MulMod" or "AddModZero" => ([(byte)Instruction.PUSH1, Chain == "AddModZero" ? (byte)0 : (byte)251,
                    (byte)Instruction.SWAP1, (byte)Instruction.PUSH1, Chain == "MulMod" ? (byte)3 : (byte)1,
                    (byte)(Chain == "MulMod" ? Instruction.MULMOD : Instruction.ADDMOD)], 4),
                "Bitwise" => ([(byte)Instruction.DUP1, (byte)Instruction.AND], 2),
                "Predicate" => ([(byte)Instruction.ISZERO, (byte)Instruction.NOT], 2),
                "Stack" => ([(byte)Instruction.DUP1, (byte)Instruction.SWAP1, (byte)Instruction.POP], 3),
                "Byte" => ([(byte)Instruction.PUSH1, 0, (byte)Instruction.BYTE], 2),
                "Shift" => ([(byte)Instruction.PUSH1, 1, (byte)Instruction.SHL], 2),
                "Sar" => ([(byte)Instruction.PUSH1, 1, (byte)Instruction.SAR], 2),
                "Clz" => ([(byte)Instruction.CLZ], 1),
                "Environment" => ([(byte)Instruction.PC, (byte)Instruction.POP,
                    (byte)Instruction.GAS, (byte)Instruction.POP, (byte)Instruction.CODESIZE, (byte)Instruction.POP], 6),
                "SmallValue" => ([(byte)Instruction.CALLDATASIZE, (byte)Instruction.DUP1, (byte)Instruction.AND, (byte)Instruction.POP,
                    (byte)Instruction.GAS, (byte)Instruction.DUP1, (byte)Instruction.AND, (byte)Instruction.POP], 8),
                "CallData" or "CallDataPartial" or "CallDataMissing" => ([(byte)Instruction.PUSH1,
                    Chain == "CallData" ? (byte)4 : Chain == "CallDataPartial" ? (byte)80 : (byte)96,
                    (byte)Instruction.CALLDATALOAD, (byte)Instruction.POP], 3),
                "Context" => ([(byte)Instruction.CALLER, (byte)Instruction.POP,
                    (byte)Instruction.CALLDATASIZE, (byte)Instruction.POP, (byte)Instruction.TIMESTAMP, (byte)Instruction.POP], 6),
                "ReturnDataSize" => ([(byte)Instruction.RETURNDATASIZE, (byte)Instruction.POP], 2),
                "PrevRandao" => ([(byte)Instruction.PREVRANDAO, (byte)Instruction.POP], 2),
                "Memory" => ([(byte)Instruction.DUP1, (byte)Instruction.PUSH0, (byte)Instruction.MSTORE,
                    (byte)Instruction.PUSH0, (byte)Instruction.MLOAD, (byte)Instruction.POP], 6),
                "MemoryByte" => ([(byte)Instruction.DUP1, (byte)Instruction.PUSH0, (byte)Instruction.MSTORE8,
                    (byte)Instruction.PUSH0, (byte)Instruction.MLOAD, (byte)Instruction.POP], 6),
                "MemoryBoundary" => ([(byte)Instruction.DUP1, (byte)Instruction.PUSH1, 63, (byte)Instruction.MSTORE8,
                    (byte)Instruction.PUSH1, 48, (byte)Instruction.MLOAD, (byte)Instruction.POP,
                    (byte)Instruction.DUP1, (byte)Instruction.PUSH1, 64, (byte)Instruction.MSTORE8,
                    (byte)Instruction.PUSH1, 48, (byte)Instruction.MLOAD, (byte)Instruction.POP], 12),
                // Includes the three opcodes executed by the child frame.
                "CallReturn" or "CallRevert" => ([(byte)Instruction.PUSH1, 32, (byte)Instruction.PUSH1, 32,
                    (byte)Instruction.PUSH0, (byte)Instruction.PUSH0, (byte)Instruction.PUSH0,
                    (byte)Instruction.PUSH20, .. TestItem.AddressC.Bytes, (byte)Instruction.PUSH2, 0xff, 0xff,
                    (byte)Instruction.CALL, (byte)Instruction.POP], 12),
                _ => throw new ArgumentOutOfRangeException(nameof(Chain))
            };
            for (int i = 0; i < BodyOpcodeCount / body.Opcodes; i++) code.AddRange(body.Sequence);
        }
        code.AddRange([(byte)Instruction.PUSH0, (byte)Instruction.MSTORE,
            (byte)Instruction.PUSH1, 32, (byte)Instruction.PUSH0, (byte)Instruction.RETURN]);
        return new CodeInfo(code.ToArray());
    }
}
