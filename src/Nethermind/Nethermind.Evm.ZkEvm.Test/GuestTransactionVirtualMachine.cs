// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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

namespace Nethermind.Evm.ZkEvm.Test;

/// <summary>A virtual machine that runs whole transactions over an Osaka block, where every account a frame reaches holds the same code.</summary>
/// <remarks>Nested frames run through <see cref="VirtualMachine{TGasPolicy}.ExecuteTransaction{TTracingInst}"/>, as on the guest.</remarks>
internal sealed class GuestTransactionVirtualMachine : VirtualMachine<EthereumGasPolicy>
{
    private static readonly IReleaseSpec Fork = Osaka.Instance;
    private static readonly BlockHeader Header = new(Hash256.Zero, Hash256.Zero, Address.Zero, UInt256.Zero, 1, 60_000_000, 1, []);

    private readonly IWorldState _state = Substitute.For<IWorldState>();

    /// <param name="contractCode">The code of every account a frame calls.</param>
    /// <param name="origin">The sender of the transaction.</param>
    /// <param name="gasPrice">The gas price of the transaction.</param>
    public GuestTransactionVirtualMachine(byte[] contractCode, Address origin, in UInt256 gasPrice)
        : base(Substitute.For<IBlockhashProvider>(), new SingleReleaseSpecProvider(Fork, BlockchainIds.Mainnet, BlockchainIds.Mainnet), LimboLogs.Instance)
    {
        ICodeInfoRepository codeInfoRepository = Substitute.For<ICodeInfoRepository>();
        CodeInfo codeInfo = new(contractCode);
        codeInfoRepository.GetCachedCodeInfo(Arg.Any<Address>(), Arg.Any<bool>(), Arg.Any<IReleaseSpec>(), out Arg.Any<Address?>())
            .Returns(codeInfo);
        codeInfoRepository.GetPrecompile(Arg.Any<Address>(), Arg.Any<IReleaseSpec>()).Returns((IPrecompile?)null);

        SetBlockExecutionContext(new BlockExecutionContext(Header, Fork));
        SetTxExecutionContext(new TxExecutionContext(origin, codeInfoRepository, null, gasPrice));
    }

    /// <summary>Runs <paramref name="code"/> as the transaction frame of <paramref name="account"/>, called by <paramref name="caller"/> with <paramref name="value"/>.</summary>
    /// <returns>The output of the transaction frame.</returns>
    public byte[] Call(byte[] code, ulong gas, Address account, Address caller, in UInt256 value)
    {
        using ExecutionEnvironment env = ExecutionEnvironment.Rent(new CodeInfo(code), account, caller, account, 0, value, default);
        using VmState<EthereumGasPolicy> transaction = VmState<EthereumGasPolicy>.RentTopLevel(
            EthereumGasPolicy.FromULong(gas), ExecutionType.TRANSACTION, env, new StackAccessTracker(), default);
        return ExecuteTransaction<OffFlag>(transaction, _state, NullTxTracer.Instance).Output.ToArray();
    }

    /// <summary>Runs <paramref name="initCode"/> as a CREATE frame of <paramref name="contract"/> in a transaction from <paramref name="sender"/> that runs no code of its own.</summary>
    /// <remarks>Entered directly, because the CREATE opcode derives the new address with Keccak, which a host process cannot run here.</remarks>
    public void Create(byte[] initCode, ulong gas, Address sender, Address contract)
    {
        using ExecutionEnvironment env = ExecutionEnvironment.Rent(CodeInfo.Empty, sender, sender, sender, 0, UInt256.Zero, default);
        using VmState<EthereumGasPolicy> transaction = VmState<EthereumGasPolicy>.RentTopLevel(
            EthereumGasPolicy.FromULong(gas), ExecutionType.TRANSACTION, env, new StackAccessTracker(), default);
        ExecutionEnvironment createEnv = ExecutionEnvironment.Rent(new CodeInfo(initCode), contract, sender, null, 1, UInt256.Zero, default);
        VmState<EthereumGasPolicy> create = VmState<EthereumGasPolicy>.RentFrame(
            EthereumGasPolicy.FromULong(gas / 2), 0, 0, ExecutionType.CREATE, isStatic: false, isCreateOnPreExistingAccount: false,
            createEnv, new StackAccessTracker(), default);
        StateStack.Push(transaction);
        ExecuteTransaction<OffFlag>(create, _state, NullTxTracer.Instance);
    }
}
