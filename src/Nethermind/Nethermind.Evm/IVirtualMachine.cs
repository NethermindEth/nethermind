// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Evm.Tracing;
using Nethermind.State;

namespace Nethermind.Evm
{
    public interface IVirtualMachine
    {
        TransactionSubstate ExecuteTransaction<TTracingInst>(EvmState state, IWorldState worldState, ITxTracer txTracer)
            where TTracingInst : struct, IFlag;
        ref readonly BlockExecutionContext BlockExecutionContext { get; }
        ref readonly TxExecutionContext TxExecutionContext { get; }
        public EvmState EvmState { get; }
        void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext);
        void SetTxExecutionContext(in TxExecutionContext txExecutionContext);
    }
}
