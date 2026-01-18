// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;

namespace Nethermind.Evm;

public interface IVirtualMachine<TGasPolicy>
    where TGasPolicy : struct, IGasPolicy<TGasPolicy>
{
    TransactionSubstate ExecuteTransaction<TTracingInst>(VmState<TGasPolicy> state, IWorldState worldState, ITxTracer txTracer)
        where TTracingInst : struct, IFlag;

#if ZKVM
    /// <summary>
    /// Executes a transaction without relying on generic tracing flags (e.g. <c>OffFlag</c>/<c>OnFlag</c>),
    /// which can trigger generic virtual method (GVM) lookups under NativeAOT/ZKVM runtimes.
    /// </summary>
    TransactionSubstate ExecuteTransactionNoTracing(VmState<TGasPolicy> state, IWorldState worldState, ITxTracer txTracer);
#endif

    ref readonly BlockExecutionContext BlockExecutionContext { get; }
    ref readonly TxExecutionContext TxExecutionContext { get; }
    void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext);
    void SetTxExecutionContext(in TxExecutionContext txExecutionContext);
    int OpCodeCount { get; }
}

/// <summary>
/// Non-generic IVirtualMachine for backward compatibility with EthereumGasPolicy.
/// </summary>
public interface IVirtualMachine : IVirtualMachine<EthereumGasPolicy>
{
}
