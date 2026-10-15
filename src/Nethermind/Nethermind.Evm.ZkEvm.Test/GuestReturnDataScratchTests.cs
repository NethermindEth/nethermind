// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.Precompiles;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Evm.ZkEvm.Test;

/// <summary>Nested RETURN/REVERT outputs, and create outputs that are never deployed, are staged in one reused buffer.</summary>
/// <remarks>
/// The guest has no GC, so a copy per output would make heap use grow with the gas a block spends on outputs: a frame
/// returning just over 32 KiB pays about 5,300 gas for a copy that costs the guest heap about 64 KiB.
/// </remarks>
public class GuestReturnDataScratchTests
{
    // Past the host's 32 KiB scratch limit and Osaka's 24 KiB code size limit.
    private const int OutputSize = 33 * 1024;
    // Within the code size limit, but a reverted create output is never deployed either.
    private const int RevertedCreateOutputSize = 16 * 1024;
    private const int Outputs = 32;
    private const ulong Gas = 10_000_000;
    // A copy per output allocates Outputs * OutputSize; the rest is frame bookkeeping and the substitute world state.
    private const int AllowedAllocation = Outputs * OutputSize / 4;

    private static readonly IReleaseSpec Fork = Osaka.Instance;
    private static readonly BlockHeader Header = new(Hash256.Zero, Hash256.Zero, Address.Zero, UInt256.Zero, 1, 60_000_000, 1, []);
    private static readonly Address Sender = new("0x00000000000000000000000000000000000000ca");
    private static readonly Address Contract = new("0x00000000000000000000000000000000000000c0");

    [Test]
    public void Nested_outputs_reuse_one_buffer([Values(Instruction.RETURN, Instruction.REVERT)] Instruction halt)
    {
        ScratchVirtualMachine vm = new(Output(halt));
        byte[] code = CallContract(Outputs);

        vm.Call(code);
        long before = GC.GetAllocatedBytesForCurrentThread();
        int lastOutputLength = vm.Call(code);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(lastOutputLength, Is.EqualTo(OutputSize));
            Assert.That(vm.RetainedReturnDataScratchLength, Is.GreaterThanOrEqualTo(OutputSize));
            Assert.That(allocated, Is.LessThan(AllowedAllocation));
        }
    }

    [TestCase(Instruction.RETURN, OutputSize)]
    [TestCase(Instruction.REVERT, RevertedCreateOutputSize)]
    public void Undeployable_create_outputs_reuse_one_buffer(Instruction halt, int outputSize)
    {
        ScratchVirtualMachine vm = new([]);
        byte[] initCode = Output(halt, outputSize);

        vm.Create(initCode);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < Outputs; i++)
            vm.Create(initCode);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(vm.RetainedReturnDataScratchLength, Is.GreaterThanOrEqualTo(outputSize));
            Assert.That(allocated, Is.LessThan(AllowedAllocation));
        }
    }

    /// <summary>Code that halts with <paramref name="halt"/> over the first <paramref name="size"/> bytes of memory.</summary>
    private static byte[] Output(Instruction halt, int size = OutputSize) =>
        [(byte)Instruction.PUSH3, 0, (byte)(size >> 8), (byte)size, (byte)Instruction.PUSH0, (byte)halt];

    /// <summary>Code that calls <see cref="Contract"/> <paramref name="calls"/> times, copying none of the output.</summary>
    private static byte[] CallContract(int calls)
    {
        byte[] call =
        [
            (byte)Instruction.PUSH0, (byte)Instruction.PUSH0, (byte)Instruction.PUSH0, (byte)Instruction.PUSH0, (byte)Instruction.PUSH0,
            (byte)Instruction.PUSH20, .. Contract.Bytes, (byte)Instruction.GAS, (byte)Instruction.CALL, (byte)Instruction.POP
        ];
        byte[] code = new byte[call.Length * calls];
        for (int i = 0; i < calls; i++)
            call.CopyTo(code, i * call.Length);
        return code;
    }

    /// <summary>A virtual machine over an Osaka block whose world state holds <see cref="Contract"/> with the given code.</summary>
    private sealed class ScratchVirtualMachine : VirtualMachine<EthereumGasPolicy>
    {
        private readonly IWorldState _state = Substitute.For<IWorldState>();

        public ScratchVirtualMachine(byte[] contractCode)
            : base(Substitute.For<IBlockhashProvider>(), new SingleReleaseSpecProvider(Fork, BlockchainIds.Mainnet, BlockchainIds.Mainnet), LimboLogs.Instance)
        {
            ICodeInfoRepository codeInfoRepository = Substitute.For<ICodeInfoRepository>();
            CodeInfo codeInfo = new(contractCode);
            codeInfoRepository.GetCachedCodeInfo(Arg.Any<Address>(), Arg.Any<bool>(), Arg.Any<IReleaseSpec>(), out Arg.Any<Address?>())
                .Returns(codeInfo);
            codeInfoRepository.GetPrecompile(Arg.Any<Address>(), Arg.Any<IReleaseSpec>()).Returns((IPrecompile?)null);

            SetBlockExecutionContext(new BlockExecutionContext(Header, Fork));
            SetTxExecutionContext(new TxExecutionContext(Sender, codeInfoRepository, null, UInt256.One));
        }

        /// <summary>Runs <paramref name="code"/> as a transaction from <see cref="Sender"/>.</summary>
        /// <returns>The length of the last nested output.</returns>
        public int Call(byte[] code)
        {
            using ExecutionEnvironment env = ExecutionEnvironment.Rent(new CodeInfo(code), Sender, Sender, Sender, 0, UInt256.Zero, default);
            using VmState<EthereumGasPolicy> transaction = VmState<EthereumGasPolicy>.RentTopLevel(
                EthereumGasPolicy.FromULong(Gas), ExecutionType.TRANSACTION, env, new StackAccessTracker(), default);
            ExecuteTransaction<OffFlag>(transaction, _state, NullTxTracer.Instance);
            return ReturnDataBuffer.Length;
        }

        /// <summary>Runs <paramref name="initCode"/> as a CREATE frame of a transaction that runs no code of its own.</summary>
        /// <remarks>Entered directly, because the CREATE opcode derives the new address with Keccak, which a host process cannot run here.</remarks>
        public void Create(byte[] initCode)
        {
            using ExecutionEnvironment env = ExecutionEnvironment.Rent(CodeInfo.Empty, Sender, Sender, Sender, 0, UInt256.Zero, default);
            using VmState<EthereumGasPolicy> transaction = VmState<EthereumGasPolicy>.RentTopLevel(
                EthereumGasPolicy.FromULong(Gas), ExecutionType.TRANSACTION, env, new StackAccessTracker(), default);
            ExecutionEnvironment createEnv = ExecutionEnvironment.Rent(new CodeInfo(initCode), Contract, Sender, null, 1, UInt256.Zero, default);
            VmState<EthereumGasPolicy> create = VmState<EthereumGasPolicy>.RentFrame(
                EthereumGasPolicy.FromULong(Gas / 2), 0, 0, ExecutionType.CREATE, isStatic: false, isCreateOnPreExistingAccount: false,
                createEnv, new StackAccessTracker(), default);
            StateStack.Push(transaction);
            ExecuteTransaction<OffFlag>(create, _state, NullTxTracer.Instance);
        }
    }
}
