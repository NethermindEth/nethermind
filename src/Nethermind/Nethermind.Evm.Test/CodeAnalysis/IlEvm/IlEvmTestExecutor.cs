// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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

namespace Nethermind.Evm.Test.CodeAnalysis.IlEvm;

/// <summary>
/// Executes bytecode through the real <see cref="EthereumVirtualMachine"/> on the non-tracing
/// path (the one IL-EVM hooks into), returning the observable outcome for differential checks.
/// </summary>
internal static class IlEvmTestExecutor
{
    internal sealed record ExecutionResult(byte[] Output, long GasLeft, bool IsError);

    internal static IReleaseSpec Spec => MainnetSpecProvider.Instance.GetSpec((ForkActivation)MainnetSpecProvider.IstanbulBlockNumber);

    internal static ExecutionResult Run(CodeInfo codeInfo, long gasLimit)
    {
        IReleaseSpec spec = Spec;
        IWorldState stateProvider = TestWorldStateFactory.CreateForTest();
        using IDisposable worldStateScope = stateProvider.BeginScope(IWorldState.PreGenesis);
        stateProvider.CreateAccount(Address.Zero, 1000.Ether);
        stateProvider.Commit(spec);

        EthereumCodeInfoRepository codeInfoRepository = new(stateProvider);
        EthereumVirtualMachine virtualMachine = new(new TestBlockhashProvider(MainnetSpecProvider.Instance), MainnetSpecProvider.Instance, LimboLogs.Instance);
        BlockHeader header = new(Keccak.Zero, Keccak.Zero, Address.Zero, UInt256.One, MainnetSpecProvider.IstanbulBlockNumber, long.MaxValue, 1UL, Bytes.Empty);
        virtualMachine.SetBlockExecutionContext(new BlockExecutionContext(header, spec));
        virtualMachine.SetTxExecutionContext(new TxExecutionContext(Address.Zero, codeInfoRepository, null, 0));

        using ExecutionEnvironment environment = ExecutionEnvironment.Rent(
            executingAccount: Address.Zero,
            codeSource: Address.Zero,
            caller: Address.Zero,
            codeInfo: codeInfo,
            callDepth: 0,
            value: 0,
            inputData: default);

        using VmState<EthereumGasPolicy> evmState = VmState<EthereumGasPolicy>.RentTopLevel(
            EthereumGasPolicy.FromLong(gasLimit), ExecutionType.TRANSACTION, environment, new StackAccessTracker(), stateProvider.TakeSnapshot());

        TransactionSubstate substate = virtualMachine.ExecuteTransaction<OffFlag>(evmState, stateProvider, NullTxTracer.Instance);
        EthereumGasPolicy gasState = evmState.Gas;
        return new ExecutionResult(substate.Output.ToArray(), EthereumGasPolicy.GetRemainingGas(in gasState), substate.IsError);
    }
}
