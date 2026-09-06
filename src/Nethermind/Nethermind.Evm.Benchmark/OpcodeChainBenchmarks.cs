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
    private CodeInfo[] _codePool;
    private int _codeIndex;
    private byte[] _input = new byte[96];

    [Params("DivOne", "ModOne", "DivZero", "ModZero", "DivSmall", "ModSmall", "DivWide", "ModWide", "JumpScattered", "JumpScatteredRotating", "JumpScatteredPush3", "Arithmetic", "AddMod", "MulMod", "AddModZero", "MulModZero", "Bitwise", "Predicate", "Stack", "Byte", "Shift", "Sar", "Clz", "Environment", "SmallValue", "CallData", "CallDataPartial", "CallDataMissing", "Context", "ReturnDataSize", "PrevRandao", "Memory", "MemoryByte", "MemoryBoundary", "CallReturn", "CallRevert", "CallInput", "JumpTaken", "JumpUntaken", "JumpAlternating")]
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
        if (Chain is "CallReturn" or "CallRevert" or "CallInput")
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
        if (Chain == "JumpScatteredRotating")
        {
            _codePool = new CodeInfo[256];
            for (int i = 0; i < _codePool.Length; i++) _codePool[i] = new CodeInfo(_code.CodeSpan.ToArray());
        }

        ReadOnlyMemory<byte> output = ExecuteContract();
        UInt256 expected = Chain switch
        {
            "Arithmetic" => (UInt256)(BodyOpcodeCount / 2 + 1),
            "AddMod" => (UInt256)((BodyOpcodeCount / 4 + 1) % 251),
            "MulMod" => (UInt256)(ulong)System.Numerics.BigInteger.ModPow(3, BodyOpcodeCount / 4, 251),
            "AddModZero" or "MulModZero" or "DivZero" or "ModZero" => UInt256.Zero,
            "DivSmall" or "DivWide" => (UInt256)2,
            "ModSmall" or "ModWide" => UInt256.One,
            "DivOne" => (UInt256)3,
            "ModOne" => UInt256.Zero,
            "Predicate" => UInt256.MaxValue,
            "JumpTaken" => UInt256.Zero,
            "Byte" or "Shift" or "Sar" => UInt256.Zero,
            "Clz" => (UInt256)248,
            _ => UInt256.One
        };
        if (_vm.OpCodeCount != ExecutedOpcodeCount || !output.Span.SequenceEqual(expected.ToBigEndian()))
            throw new InvalidOperationException($"Invalid {Chain} chain output or opcode count.");

        for (int i = 0; i < 100_000; i++) ExecuteContract();
        CodeInfo chain = _code;
        CodeInfo[] codePool = _codePool;
        _codePool = null;
        _code = new CodeInfo(new byte[] { (byte)Instruction.STOP });
        // Advance the periodic table refresh after warming the selected workload's instruction bodies.
        for (int i = 0; i < 400_000; i++) ExecuteContract();
        _code = chain;
        _codePool = codePool;
    }

    [Benchmark(OperationsPerInvoke = ExecutedOpcodeCount)]
    public ReadOnlyMemory<byte> ExecuteContract()
    {
        CodeInfo code = _codePool is { } pool ? pool[_codeIndex++ & (pool.Length - 1)] : _code;
        using ExecutionEnvironment environment = ExecutionEnvironment.Rent(
            executingAccount: Address.Zero, codeSource: Address.Zero, caller: Address.Zero,
            codeInfo: code, callDepth: 0, value: 0, inputData: _input);
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
        if (Chain is "JumpScattered" or "JumpScatteredRotating" or "JumpScatteredPush3")
        {
            const int blocks = BodyOpcodeCount / 5;
            const int stride = 24;
            byte[] body = new byte[blocks * stride];
            for (int i = 0; i < blocks; i++)
            {
                int offset = i * 73 % blocks * stride;
                int destination = i + 1 == blocks ? 2 + body.Length : 2 + (i + 1) * 73 % blocks * stride;
                int cursor = offset;
                if (i != 0) body[cursor++] = (byte)Instruction.JUMPDEST;
                body[cursor++] = (byte)Instruction.DUP1;
                body[cursor++] = (byte)Instruction.POP;
                body[cursor++] = (byte)(Chain == "JumpScatteredPush3" ? Instruction.PUSH3 : Instruction.PUSH2);
                if (Chain == "JumpScatteredPush3") body[cursor++] = 0;
                body[cursor++] = (byte)(destination >> 8);
                body[cursor++] = (byte)destination;
                body[cursor] = (byte)Instruction.JUMP;
            }
            code.AddRange(body);
            code.Add((byte)Instruction.JUMPDEST);
        }
        else if (Chain.StartsWith("Div", StringComparison.Ordinal) || Chain.StartsWith("Mod", StringComparison.Ordinal))
        {
            bool wide = Chain.EndsWith("Wide", StringComparison.Ordinal);
            UInt256 divisor = Chain.EndsWith("Zero", StringComparison.Ordinal) ? UInt256.Zero
                : Chain.EndsWith("One", StringComparison.Ordinal) ? UInt256.One
                : wide ? UInt256.MaxValue / 4 : (UInt256)3;
            UInt256 dividend = divisor.IsZero ? UInt256.MaxValue : divisor * 2 + 1;
            code.Clear();
            code.AddRange([(byte)Instruction.PUSH32, .. divisor.ToBigEndian(),
                (byte)Instruction.PUSH32, .. dividend.ToBigEndian()]);
            Instruction opcode = Chain.StartsWith("Div", StringComparison.Ordinal) ? Instruction.DIV : Instruction.MOD;
            for (int i = 0; i < BodyOpcodeCount / 4; i++)
            {
                code.AddRange([(byte)Instruction.DUP2, (byte)Instruction.DUP2, (byte)opcode]);
                if (i + 1 != BodyOpcodeCount / 4) code.Add((byte)Instruction.POP);
            }
        }
        else if (Chain is "JumpTaken" or "JumpUntaken" or "JumpAlternating")
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
                "AddMod" or "MulMod" or "AddModZero" or "MulModZero" => ([(byte)Instruction.PUSH1, Chain is "AddModZero" or "MulModZero" ? (byte)0 : (byte)251,
                    (byte)Instruction.SWAP1, (byte)Instruction.PUSH1, Chain is "MulMod" or "MulModZero" ? (byte)3 : (byte)1,
                    (byte)(Chain is "MulMod" or "MulModZero" ? Instruction.MULMOD : Instruction.ADDMOD)], 4),
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
                "CallReturn" or "CallRevert" or "CallInput" => ([(byte)Instruction.PUSH1, 32, (byte)Instruction.PUSH1, 32,
                    .. (Chain == "CallInput" ? new byte[] { (byte)Instruction.PUSH1, 32 } : new byte[] { (byte)Instruction.PUSH0 }),
                    (byte)Instruction.PUSH0, (byte)Instruction.PUSH0,
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
