// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
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
using NUnit.Framework;
using DispatchState = Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.DispatchState;

namespace Nethermind.Evm.Test;

/// <summary>Checks that scalar execution-gas dispatch leaves the rest of the Ethereum policy authoritative.</summary>
[Parallelizable(ParallelScope.All)]
public sealed unsafe class HostExecutionGasDispatchTests
{
    private const byte PUSH0 = (byte)Instruction.PUSH0;
    private const byte PUSH1 = (byte)Instruction.PUSH1;
    private const byte MLOAD = (byte)Instruction.MLOAD;
    private const byte SSTORE = (byte)Instruction.SSTORE;
    private const byte STOP = (byte)Instruction.STOP;
    private const long InitialStateReservoir = GasCostOf.SSetState + 17;
    private const ulong BoundaryGasCost = 2 * GasCostOf.Base + 2 * GasCostOf.VeryLow;
    private const ulong StatePoolOogGas = GasCostOf.Base + GasCostOf.VeryLow
        + Eip8038Constants.ColdStorageAccess + Eip8038Constants.StorageWrite + 1;

    private static readonly IReleaseSpec ReleaseSpec = Amsterdam.Instance;
    private static readonly BlockHeader Header = new(Hash256.Zero, Hash256.Zero, Address.Zero, UInt256.Zero, 1, 30_000_000, 1, []);

    [Test]
    public void Fast_slow_fast_chain_preserves_state_policy()
    {
        // MLOAD is a host fast path on either side of SSTORE's state-charging slow body.
        byte[] code = [PUSH0, MLOAD, PUSH1, 1, PUSH0, SSTORE, PUSH0, MLOAD, STOP];
        EthereumGasPolicy initial = SentinelPolicy(ulong.MaxValue, outOfGas: false);

        Assert.That(ReleaseSpec.IsEip8037Enabled, Is.True);

        Result fast = Run(code, initial, useFastTable: true, withWorldState: true);
        Result plain = Run(code, initial, useFastTable: false, withWorldState: true);

        AssertEquivalent(fast, plain);
        Assert.That(fast.Exception, Is.EqualTo(EvmExceptionType.Stop));
        Assert.That(fast.Policy.StateReservoir, Is.EqualTo(InitialStateReservoir - GasCostOf.SSetState));
        Assert.That(fast.Policy.StateGasUsed, Is.EqualTo(initial.StateGasUsed + (long)GasCostOf.SSetState));
        Assert.That(fast.Policy.StateGasSpill, Is.EqualTo(initial.StateGasSpill));
        Assert.That(fast.Policy.StateGasSpillRefunded, Is.EqualTo(initial.StateGasSpillRefunded));
        Assert.That(fast.Policy.IndependentStatePool, Is.True);
        Assert.That(fast.Policy.OutOfGas, Is.False);
    }

    [TestCase(0UL)]
    [TestCase(1UL)]
    [TestCase(ulong.MaxValue)]
    public void Fast_dispatch_preserves_full_policy_at_execution_gas_boundaries(ulong value)
    {
        byte[] code = [PUSH0, MLOAD, PUSH0, MLOAD, STOP];
        EthereumGasPolicy initial = SentinelPolicy(value, outOfGas: true);

        Result fast = Run(code, initial, useFastTable: true, withWorldState: false);
        Result plain = Run(code, initial, useFastTable: false, withWorldState: false);

        AssertEquivalent(fast, plain);
        Assert.That(fast.Policy.StateReservoir, Is.EqualTo(initial.StateReservoir));
        Assert.That(fast.Policy.StateGasUsed, Is.EqualTo(initial.StateGasUsed));
        Assert.That(fast.Policy.StateGasSpill, Is.EqualTo(initial.StateGasSpill));
        Assert.That(fast.Policy.StateGasSpillRefunded, Is.EqualTo(initial.StateGasSpillRefunded));
        Assert.That(fast.Policy.IndependentStatePool, Is.True);
        Assert.That(fast.Policy.OutOfGas, Is.True);
        Assert.That(fast.Exception, Is.EqualTo(value <= 1 ? EvmExceptionType.OutOfGas : EvmExceptionType.Stop));
        Assert.That(fast.Policy.Value, Is.EqualTo(value <= 1 ? 0UL : ulong.MaxValue - BoundaryGasCost));
    }

    [Test]
    public void State_charge_out_of_gas_preserves_policy_on_terminal_fault()
    {
        // The execution component is affordable, while the independent state pool is not.
        byte[] code = [PUSH1, 1, PUSH0, SSTORE, STOP];
        EthereumGasPolicy initial = SentinelPolicy(StatePoolOogGas, outOfGas: false);
        initial.StateReservoir = 1;
        initial.OutOfGas = false;

        Result fast = Run(code, initial, useFastTable: true, withWorldState: true);
        Result plain = Run(code, initial, useFastTable: false, withWorldState: true);

        AssertEquivalent(fast, plain);
        Assert.That(fast.Exception, Is.EqualTo(EvmExceptionType.OutOfGas));
        Assert.That(fast.Policy.StateReservoir, Is.EqualTo(initial.StateReservoir));
        Assert.That(fast.Policy.StateGasUsed, Is.EqualTo(initial.StateGasUsed));
        Assert.That(fast.Policy.StateGasSpill, Is.EqualTo(initial.StateGasSpill));
        Assert.That(fast.Policy.StateGasSpillRefunded, Is.EqualTo(initial.StateGasSpillRefunded));
        Assert.That(fast.Policy.IndependentStatePool, Is.True);
        Assert.That(fast.Policy.OutOfGas, Is.True);
        Assert.That(fast.Policy.Value, Is.EqualTo(1UL));
    }

    private static EthereumGasPolicy SentinelPolicy(ulong value, bool outOfGas) => new()
    {
        Value = value,
        StateReservoir = InitialStateReservoir,
        StateGasUsed = 23,
        StateGasSpill = 31,
        StateGasSpillRefunded = 7,
        IndependentStatePool = true,
        OutOfGas = outOfGas,
    };

    private static void AssertEquivalent(in Result fast, in Result plain)
    {
        Assert.That(fast.Exception, Is.EqualTo(plain.Exception));
        Assert.That(fast.Policy.Value, Is.EqualTo(plain.Policy.Value));
        Assert.That(fast.Policy.StateReservoir, Is.EqualTo(plain.Policy.StateReservoir));
        Assert.That(fast.Policy.StateGasUsed, Is.EqualTo(plain.Policy.StateGasUsed));
        Assert.That(fast.Policy.StateGasSpill, Is.EqualTo(plain.Policy.StateGasSpill));
        Assert.That(fast.Policy.StateGasSpillRefunded, Is.EqualTo(plain.Policy.StateGasSpillRefunded));
        Assert.That(fast.Policy.IndependentStatePool, Is.EqualTo(plain.Policy.IndependentStatePool));
        Assert.That(fast.Policy.OutOfGas, Is.EqualTo(plain.Policy.OutOfGas));
        Assert.That(fast.ProgramCounter, Is.EqualTo(plain.ProgramCounter));
        Assert.That(fast.OpCodeCount, Is.EqualTo(plain.OpCodeCount));
    }

    private static Result Run(byte[] code, EthereumGasPolicy initial, bool useFastTable, bool withWorldState)
    {
        CodeInfo codeInfo = new(code);
        using ExecutionEnvironment environment = ExecutionEnvironment.Rent(codeInfo, Address.Zero, Address.Zero, null, 0, UInt256.Zero, ReadOnlyMemory<byte>.Empty);
        using StackAccessTracker accessTracker = new();
        using VmState<EthereumGasPolicy> frame = VmState<EthereumGasPolicy>.RentTopLevel(
            initial, ExecutionType.TRANSACTION, environment, accessTracker, default);
        // Keep both MLOADs on the active-word fast path; the intervening SSTORE is the slow state-charging body.
        if (!frame.Memory.TrySave(UInt256.Zero, new byte[EvmStack.WordSize]))
            throw new InvalidOperationException("Could not seed the inline memory word for the dispatch test.");
        IWorldState worldState = TestWorldStateFactory.CreateForTest();
        using IDisposable worldScope = worldState.BeginScope(IWorldState.PreGenesis);
        if (withWorldState)
        {
            // Commit a non-empty account with an untouched slot: SSTORE then sees current=original=zero,
            // which is the fresh-slot EIP-8037 state-charge branch.
            worldState.CreateAccountIfNotExists(Address.Zero, 1);
            worldState.Commit(ReleaseSpec);
        }

        DispatchingVirtualMachine vm = new(ReleaseSpec);
        vm.Enter(frame, worldState);
        byte[] stackBytes = GC.AllocateArray<byte>((EvmStack.MaxStackSize + 1) * EvmStack.WordSize, pinned: true);
        int alignment = (int)((nuint)Unsafe.AsPointer(ref stackBytes[0]) & (EvmStack.WordSize - 1));
        int stackStart = alignment == 0 ? 0 : EvmStack.WordSize - alignment;
        delegate*<ref EvmStack, ref EthereumGasPolicy, ref DispatchState, nint, nint, EvmExceptionType>[] handlers =
            vm.GetOpcodeHandlers<OffFlag, OffFlag>();
        nint pc = 0;
        nint opCodeCount = 0;
        EvmExceptionType exception;

        fixed (delegate*<ref EvmStack, ref EthereumGasPolicy, ref DispatchState, nint, nint, EvmExceptionType>* entries = handlers)
        {
            delegate*<ref EvmStack, ref EthereumGasPolicy, ref DispatchState, nint, nint, EvmExceptionType>* dispatch =
                useFastTable ? entries : entries + VirtualMachine<EthereumGasPolicy>.FallbackHandlersOffset;
            EvmStack stack = new(0, NullTxTracer.Instance, ref stackBytes[stackStart], codeInfo.ExecutionCodeSpan, codeInfo);
            DispatchState state = new()
            {
                Gas = ref Unsafe.AsRef(in initial),
                OpcodeHandlers = dispatch,
                Vm = vm,
            };
            exception = ((delegate*<ref EvmStack, ulong, ref DispatchState, nint, nint, EvmExceptionType>)(nint)dispatch[code[0]])(
                ref stack, initial.Value, ref state, pc, opCodeCount);
            pc = state.FinalProgramCounter;
            opCodeCount = state.OpCodeCount;
        }

        return new Result(exception, initial, pc, opCodeCount);
    }

    private readonly record struct Result(EvmExceptionType Exception, EthereumGasPolicy Policy, nint ProgramCounter, nint OpCodeCount);

    private sealed unsafe class DispatchingVirtualMachine : VirtualMachine<EthereumGasPolicy>
    {
        public DispatchingVirtualMachine(IReleaseSpec spec)
            : base(new NoBlockhashProvider(), new SingleReleaseSpecProvider(spec, BlockchainIds.Mainnet, BlockchainIds.Mainnet), LimboLogs.Instance)
            => SetBlockExecutionContext(new BlockExecutionContext(Header, spec));

        public void Enter(VmState<EthereumGasPolicy> frame, IWorldState worldState)
        {
            _worldState = worldState;
            VmState = frame;
        }
    }

    private sealed class NoBlockhashProvider : IBlockhashProvider
    {
        public Hash256? GetBlockhash(BlockHeader currentBlock, ulong number, IReleaseSpec spec) => null;

        public System.Threading.Tasks.Task Prefetch(BlockHeader currentBlock, System.Threading.CancellationToken token) =>
            System.Threading.Tasks.Task.CompletedTask;
    }
}
