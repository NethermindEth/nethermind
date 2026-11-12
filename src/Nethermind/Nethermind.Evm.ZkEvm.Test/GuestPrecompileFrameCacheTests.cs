// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
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

/// <summary>A precompile the guest runs inline takes its frame from the per-depth frame cache, not the shared pool.</summary>
public class GuestPrecompileFrameCacheTests
{
    private static readonly IReleaseSpec Fork = Osaka.Instance;
    private static readonly BlockHeader Header = new(Hash256.Zero, Hash256.Zero, Address.Zero, UInt256.Zero, 1, 60_000_000, 1, []);
    private static readonly Address Sender = new("0x00000000000000000000000000000000000000ca");
    private static readonly Address Identity = new("0x0000000000000000000000000000000000000004");

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_isDisposed")]
    private static extern ref bool IsDisposed(VmState<EthereumGasPolicy> state);

    [Test]
    public void Inline_precompile_calls_reuse_the_cached_frame()
    {
        PrecompileVirtualMachine vm = new();
        byte[] code = [.. CallIdentity(), .. CallIdentity()];

        vm.Call(code);
        VmState<EthereumGasPolicy>? first = vm.FrameCache[1];
        vm.Call(code);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first, Is.Not.Null, "the precompile frame is cached at its depth");
            Assert.That(vm.FrameCache[1], Is.SameAs(first), "the next precompile call reuses the cached frame");
            Assert.That(IsDisposed(first!), Is.True, "the precompile frame is released after the call");
        }
    }

    private static byte[] CallIdentity() =>
    [
        (byte)Instruction.PUSH0, (byte)Instruction.PUSH0, (byte)Instruction.PUSH0, (byte)Instruction.PUSH0, (byte)Instruction.PUSH0,
        (byte)Instruction.PUSH20, .. Identity.Bytes, (byte)Instruction.GAS, (byte)Instruction.CALL, (byte)Instruction.POP
    ];

    /// <summary>A virtual machine over an Osaka block whose code repository resolves <see cref="Identity"/> to the identity precompile.</summary>
    private sealed class PrecompileVirtualMachine : VirtualMachine<EthereumGasPolicy>
    {
        private readonly IWorldState _state = Substitute.For<IWorldState>();

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
            ExecuteTransaction<OffFlag>(transaction, _state, NullTxTracer.Instance);
        }
    }
}
