// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Evm.Gas;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;

namespace Nethermind.Evm;

public interface IVirtualMachine<TGasPolicy>
    where TGasPolicy : struct, IGasPolicy<TGasPolicy>
{
    TransactionSubstate ExecuteTransaction<TTracingInst>(VmState<TGasPolicy> state, IWorldState worldState, ITxTracer txTracer)
        where TTracingInst : struct, IFlag;
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
