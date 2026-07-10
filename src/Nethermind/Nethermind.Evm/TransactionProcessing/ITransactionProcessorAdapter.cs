// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.Tracing;

namespace Nethermind.Evm.TransactionProcessing
{
    public interface ITransactionProcessorAdapter
    {
        TransactionResult Execute(Transaction transaction, ITxTracer txTracer);
        TransactionResult Execute<TGasPolicy>(Transaction transaction, ITxTracer txTracer, in IntrinsicGas<TGasPolicy> intrinsicGas)
            where TGasPolicy : struct, IGasPolicy<TGasPolicy>
            => Execute(transaction, txTracer);

        void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext);
    }
}
