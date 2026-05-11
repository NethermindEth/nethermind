// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.Tracing;

namespace Nethermind.Evm.TransactionProcessing
{
    public class ExecuteTransactionProcessorAdapter(ITransactionProcessor transactionProcessor)
        : ITransactionProcessorAdapter
    {
        public TransactionResult Execute(Transaction transaction, ITxTracer txTracer) =>
            transactionProcessor.Execute(transaction, txTracer);

        public TransactionResult Execute<TGasPolicy>(Transaction transaction, ITxTracer txTracer, in IntrinsicGas<TGasPolicy> intrinsicGas)
            where TGasPolicy : struct, IGasPolicy<TGasPolicy>
            => transactionProcessor is TransactionProcessor<TGasPolicy> processor
                ? processor.Process(transaction, txTracer, ExecutionOptions.Commit, in intrinsicGas)
                : Execute(transaction, txTracer);

        public void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext)
            => transactionProcessor.SetBlockExecutionContext(in blockExecutionContext);
    }
}
