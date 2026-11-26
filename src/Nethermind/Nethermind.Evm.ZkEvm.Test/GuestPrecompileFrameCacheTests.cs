// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
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

/// <summary>
/// A precompile the guest runs inline in a frame takes it from the per-depth frame cache, not the shared pool; a value-less
/// CALL or STATICCALL takes no frame at all.
/// </summary>
public class GuestPrecompileFrameCacheTests
{
    private static readonly IReleaseSpec Fork = Osaka.Instance;
    private static readonly BlockHeader Header = new(Hash256.Zero, Hash256.Zero, Address.Zero, UInt256.Zero, 1, 60_000_000, 1, []);
    private static readonly Address Sender = new("0x00000000000000000000000000000000000000ca");
    private static readonly Address Identity = new("0x0000000000000000000000000000000000000004");

    [Test]
    public void Inline_precompile_calls_reuse_the_cached_frame([Values] bool outOfGas)
    {
        PrecompileVirtualMachine vm = new();
        byte[] code = [.. CallIdentity(Instruction.DELEGATECALL, outOfGas), .. CallIdentity(Instruction.DELEGATECALL, outOfGas)];

        vm.Call(code);
        VmState<EthereumGasPolicy>? first = vm.FrameCache[1];
        vm.Call(code);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first, Is.Not.Null, "the precompile frame is cached at its depth");
            Assert.That(vm.FrameCache[1], Is.SameAs(first), "the next precompile call reuses the cached frame");
        }
    }

    [Test]
    public void Value_less_precompile_calls_take_no_frame(
        [Values(Instruction.CALL, Instruction.STATICCALL)] Instruction op,
        [Values] bool outOfGas)
    {
        PrecompileVirtualMachine vm = new();

        vm.Call([.. CallIdentity(op, outOfGas), .. CallIdentity(op, outOfGas)]);

        Assert.That(vm.FrameCache[1], Is.Null);
    }

    /// <remarks>
    /// Under EIP-158 a value-less call touches an existing empty target only when the precompile succeeds. On the
    /// frame path a failed precompile reverts its touch.
    /// </remarks>
    [Test]
    public void Value_less_precompile_call_touches_an_existing_empty_target(
        [Values(Instruction.CALL, Instruction.STATICCALL)] Instruction op,
        [Values] bool outOfGas)
    {
        PrecompileVirtualMachine vm = new();
        vm.State.AccountExists(Identity).Returns(true);
        vm.State.IsDeadAccount(Identity).Returns(true);

        vm.Call(CallIdentity(op, outOfGas));

        int touches = vm.State.ReceivedCalls().Count(static call =>
            call.GetMethodInfo().Name == nameof(IWorldState.AddToBalanceAndCreateIfNotExists) && Identity.Equals(call.GetArguments()[0]));
        Assert.That(touches, Is.EqualTo(outOfGas ? 0 : 1));
    }

    /// <param name="op">The call; a CALL moves no value.</param>
    /// <param name="outOfGas">Forwards 1 gas, so identity fails and a frame it took is released on the hard-failure exit.</param>
    private static byte[] CallIdentity(Instruction op, bool outOfGas)
    {
        byte[] gas = outOfGas ? [(byte)Instruction.PUSH1, 1] : [(byte)Instruction.GAS];
        return
        [
            (byte)Instruction.PUSH0, (byte)Instruction.PUSH0, (byte)Instruction.PUSH0, (byte)Instruction.PUSH0,
            .. op == Instruction.CALL ? (byte[])[(byte)Instruction.PUSH0] : [],
            (byte)Instruction.PUSH20, .. Identity.Bytes, .. gas, (byte)op, (byte)Instruction.POP
        ];
    }

    /// <summary>A virtual machine over an Osaka block whose code repository resolves <see cref="Identity"/> to the identity precompile.</summary>
    private sealed class PrecompileVirtualMachine : VirtualMachine<EthereumGasPolicy>
    {
        public IWorldState State { get; } = Substitute.For<IWorldState>();

        public PrecompileVirtualMachine()
            : base(Substitute.For<IBlockhashProvider>(), new SingleReleaseSpecProvider(Fork, BlockchainIds.Mainnet, BlockchainIds.Mainnet), LimboLogs.Instance)
        {
            ICodeInfoRepository codeInfoRepository = Substitute.For<ICodeInfoRepository>();
            codeInfoRepository.GetCachedCodeInfo(Identity, Arg.Any<bool>(), Arg.Any<IReleaseSpec>(), out Arg.Any<Address?>())
                .Returns(new CodeInfo(IdentityPrecompile.Instance));

            SetBlockExecutionContext(new BlockExecutionContext(Header, Fork));
            SetTxExecutionContext(new TxExecutionContext(Sender, codeInfoRepository, null, UInt256.One));
        }

        /// <summary>Runs <paramref name="code"/> as a transaction from <see cref="Sender"/>.</summary>
        public void Call(byte[] code)
        {
            using ExecutionEnvironment env = ExecutionEnvironment.Rent(new CodeInfo(code), Sender, Sender, Sender, 0, UInt256.Zero, default);
            using VmState<EthereumGasPolicy> transaction = VmState<EthereumGasPolicy>.RentTopLevel(
                EthereumGasPolicy.FromULong(1_000_000), ExecutionType.TRANSACTION, env, new StackAccessTracker(), default);
            ExecuteTransaction<OffFlag>(transaction, State, NullTxTracer.Instance);
        }
    }
}
