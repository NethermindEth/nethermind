// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using BenchmarkDotNet.Attributes;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;

namespace Nethermind.Evm.Benchmark;

/// <summary>
/// EIP-7979 subroutine workloads on the production interpreter loop: a tight call/return loop, a recursive call
/// tree and the EIP's SQUARE example, next to the same SQUARE synthesized with <c>JUMP</c>.
/// </summary>
/// <remarks>Opcodes are the provisional <c>CALLSUB = 0xba</c>, <c>CALLDEST = 0xbb</c>, <c>RETURNSUB = 0xbc</c>.</remarks>
[MemoryDiagnoser]
public class SubroutineBenchmarks
{
    private const int WorkloadWarmupTransactions = 100_000;
    private const ulong GasLimit = 10_000_000;

    private const int Iterations = 1000;
    private const int LoopStart = 3;

    // 1000 iterations of PUSH2 SUB, CALLSUB; SUB: CALLDEST, RETURNSUB.
    private static readonly byte[] CallReturnLoopPush2 = EndLoop(BeginLoop()
            .PushData(Push2(0x12))
            .Op(Instruction.CALLSUB))
        .Op(Instruction.CALLDEST)
        .Op(Instruction.RETURNSUB)
        .Done;

    // As above with a PUSH1 destination.
    private static readonly byte[] CallReturnLoopPush1 = EndLoop(BeginLoop()
            .PushData(0x11)
            .Op(Instruction.CALLSUB))
        .Op(Instruction.CALLDEST)
        .Op(Instruction.RETURNSUB)
        .Done;

    // TREE(n): CALLDEST; if n == 0 return; TREE(n - 1); TREE(n - 1); RETURNSUB. 2^13 - 1 calls from TREE(12).
    private static readonly byte[] RecursiveCallTree = Prepare.EvmCode
        .PushData(12)
        .PushData(Push2(0x07))
        .Op(Instruction.CALLSUB)
        .Op(Instruction.STOP)
        .Op(Instruction.CALLDEST)
        .Op(Instruction.DUP1)
        .Op(Instruction.ISZERO)
        .PushData(Push2(0x1c))
        .Op(Instruction.JUMPI)
        .PushData(1)
        .Op(Instruction.SWAP1)
        .Op(Instruction.SUB)
        .Op(Instruction.DUP1)
        .PushData(Push2(0x07))
        .Op(Instruction.CALLSUB)
        .PushData(Push2(0x07))
        .Op(Instruction.CALLSUB)
        .Op(Instruction.RETURNSUB)
        .Op(Instruction.JUMPDEST)
        .Op(Instruction.POP)
        .Op(Instruction.RETURNSUB)
        .Done;

    // The EIP's SQUARE called 1000 times: PUSH1 2, PUSH2 SQUARE, CALLSUB, POP; SQUARE: CALLDEST, DUP1, MUL, RETURNSUB.
    private static readonly byte[] SquareCallSub = EndLoop(BeginLoop()
            .PushData(2)
            .PushData(Push2(0x15))
            .Op(Instruction.CALLSUB)
            .Op(Instruction.POP))
        .Op(Instruction.CALLDEST)
        .Op(Instruction.DUP1)
        .Op(Instruction.MUL)
        .Op(Instruction.RETURNSUB)
        .Done;

    // The same SQUARE synthesized with JUMP: PUSH2 RTN, PUSH1 2, PUSH2 SQUARE, JUMP; SQUARE ends in SWAP1, JUMP.
    private static readonly byte[] SquareJump = EndLoop(BeginLoop()
            .PushData(Push2(0x0d))
            .PushData(2)
            .PushData(Push2(0x19))
            .Op(Instruction.JUMP)
            .Op(Instruction.JUMPDEST)
            .Op(Instruction.POP))
        .Op(Instruction.JUMPDEST)
        .Op(Instruction.DUP1)
        .Op(Instruction.MUL)
        .Op(Instruction.SWAP1)
        .Op(Instruction.JUMP)
        .Done;

    // A PUSH1-heavy arithmetic loop with no subroutines.
    private static readonly byte[] ComputeLoop = BeginLoop()
        .PushData(1)
        .Op(Instruction.SWAP1)
        .Op(Instruction.SUB)
        .Op(Instruction.DUP1)
        .PushData(7)
        .Op(Instruction.ADD)
        .PushData(3)
        .Op(Instruction.MUL)
        .Op(Instruction.POP)
        .Op(Instruction.DUP1)
        .PushData(LoopStart)
        .Op(Instruction.JUMPI)
        .Op(Instruction.STOP)
        .Done;

    private readonly BlockHeader _header = new(Keccak.Zero, Keccak.Zero, Address.Zero, UInt256.One, MainnetSpecProvider.IstanbulBlockNumber, Int64.MaxValue, 1UL, Bytes.Empty);
    private IVirtualMachine _eip7979Vm = null!;
    private IVirtualMachine _plainVm = null!;
    private IWorldState _stateProvider = null!;
    private IDisposable _stateScope = null!;
    private CodeInfo _callReturnLoopPush2 = null!;
    private CodeInfo _callReturnLoopPush1 = null!;
    private CodeInfo _recursiveCallTree = null!;
    private CodeInfo _squareCallSub = null!;
    private CodeInfo _squareJump = null!;
    private CodeInfo _computeLoop = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        IReleaseSpec eip7979Spec = new OverridableReleaseSpec(Bogota.Instance) { IsEip7979Enabled = true };
        IReleaseSpec plainSpec = Bogota.Instance;

        _stateProvider = TestWorldStateFactory.CreateForTest();
        _stateScope = _stateProvider.BeginScope(IWorldState.PreGenesis);
        _stateProvider.CreateAccount(Address.Zero, 1000.Ether);
        _stateProvider.Commit(plainSpec);

        _eip7979Vm = CreateVirtualMachine(eip7979Spec);
        _plainVm = CreateVirtualMachine(plainSpec);

        _callReturnLoopPush2 = new CodeInfo(CallReturnLoopPush2);
        _callReturnLoopPush1 = new CodeInfo(CallReturnLoopPush1);
        _recursiveCallTree = new CodeInfo(RecursiveCallTree);
        _squareCallSub = new CodeInfo(SquareCallSub);
        _squareJump = new CodeInfo(SquareJump);
        _computeLoop = new CodeInfo(ComputeLoop);

        for (int i = 0; i < WorkloadWarmupTransactions; i++)
        {
            (IVirtualMachine vm, CodeInfo codeInfo) = (i % 8) switch
            {
                0 => (_eip7979Vm, _callReturnLoopPush2),
                1 => (_eip7979Vm, _callReturnLoopPush1),
                2 => (_eip7979Vm, _recursiveCallTree),
                3 => (_eip7979Vm, _squareCallSub),
                4 => (_eip7979Vm, _squareJump),
                5 => (_plainVm, _squareJump),
                6 => (_eip7979Vm, _computeLoop),
                _ => (_plainVm, _computeLoop),
            };
            Execute(vm, codeInfo);
        }
    }

    [GlobalCleanup]
    public void GlobalCleanup() => _stateScope.Dispose();

    [Benchmark]
    public void CallReturnLoop_Push2() => Execute(_eip7979Vm, _callReturnLoopPush2);

    [Benchmark]
    public void CallReturnLoop_Push1() => Execute(_eip7979Vm, _callReturnLoopPush1);

    [Benchmark]
    public void RecursiveCallTree_Depth12() => Execute(_eip7979Vm, _recursiveCallTree);

    [Benchmark]
    public void Square_CallSub() => Execute(_eip7979Vm, _squareCallSub);

    /// <summary>The JUMP-synthesized SQUARE on an EIP-7979 spec: measures what the subroutine paths cost plain jumps.</summary>
    [Benchmark]
    public void Square_Jump() => Execute(_eip7979Vm, _squareJump);

    /// <summary>The JUMP-synthesized SQUARE with EIP-7979 off.</summary>
    [Benchmark(Baseline = true)]
    public void Square_Jump_Eip7979Off() => Execute(_plainVm, _squareJump);

    /// <summary>A loop with no subroutines on an EIP-7979 spec: measures what the subroutine paths cost plain pushes.</summary>
    [Benchmark]
    public void ComputeLoop_Eip7979On() => Execute(_eip7979Vm, _computeLoop);

    [Benchmark]
    public void ComputeLoop_Eip7979Off() => Execute(_plainVm, _computeLoop);

    /// <summary>Pushes the iteration counter and opens the loop body at <see cref="LoopStart"/>.</summary>
    private static Prepare BeginLoop() => Prepare.EvmCode.PushData(Iterations).Op(Instruction.JUMPDEST);

    /// <summary>Decrements the counter, jumps back to <see cref="LoopStart"/> while it is non-zero, then stops.</summary>
    private static Prepare EndLoop(Prepare body) => body
        .PushData(1)
        .Op(Instruction.SWAP1)
        .Op(Instruction.SUB)
        .Op(Instruction.DUP1)
        .PushData(Push2(LoopStart))
        .Op(Instruction.JUMPI)
        .Op(Instruction.STOP);

    /// <summary>A two-byte immediate, since <see cref="Prepare.PushData(int)"/> picks the narrowest PUSH.</summary>
    private static byte[] Push2(int value) => [(byte)(value >> 8), (byte)value];

    private IVirtualMachine CreateVirtualMachine(IReleaseSpec spec)
    {
        EthereumVirtualMachine vm = new(new TestBlockhashProvider(), MainnetSpecProvider.Instance, new OneLoggerLogManager(NullLogger.Instance));
        vm.SetBlockExecutionContext(new BlockExecutionContext(_header, spec));
        vm.SetTxExecutionContext(new TxExecutionContext(Address.Zero, new EthereumCodeInfoRepository(_stateProvider), null, 0));
        return vm;
    }

    private void Execute(IVirtualMachine vm, CodeInfo codeInfo)
    {
        using ExecutionEnvironment environment = ExecutionEnvironment.Rent(
            executingAccount: Address.Zero,
            codeSource: Address.Zero,
            caller: Address.Zero,
            codeInfo: codeInfo,
            callDepth: 0,
            value: 0,
            inputData: default);

        using (VmState<EthereumGasPolicy> vmState = VmState<EthereumGasPolicy>.RentTopLevel(
            EthereumGasPolicy.FromULong(GasLimit),
            ExecutionType.TRANSACTION,
            environment,
            new StackAccessTracker(),
            _stateProvider.TakeSnapshot()))
        {
            TransactionSubstate substate = vm.ExecuteTransaction<OffFlag>(vmState, _stateProvider, NullTxTracer.Instance);
            if (substate.EvmExceptionType != EvmExceptionType.None)
                throw new InvalidOperationException($"Benchmark bytecode halted with {substate.EvmExceptionType}");
        }

        _stateProvider.Reset();
    }
}
