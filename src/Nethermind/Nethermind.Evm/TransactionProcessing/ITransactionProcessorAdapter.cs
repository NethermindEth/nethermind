// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Evm.Tracing;

namespace Nethermind.Evm.TransactionProcessing
{
    public interface ITransactionProcessorAdapter
    {
        TransactionResult Execute(Transaction transaction, ITxTracer txTracer);

        void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext);

        /// <summary>
        /// Resolves any implicit gas limit on <paramref name="transaction"/> to the value execution will use,
        /// ahead of the EIP-8037 per-tx block-gas inclusion check. Called once per transaction, in order, on the
        /// sequential block-access-list path. No-op unless the adapter defaults gas (the simulate adapter, for
        /// calls that omit <c>gas</c>).
        /// </summary>
        /// <param name="stateGasAvailable">Remaining EIP-8037 state-dimension budget for this transaction
        /// (<c>blockGasLimit - cumulativeStateGas</c>), so an implicit limit fits the state dimension too.</param>
        void PrepareForInclusionCheck(Transaction transaction, ulong stateGasAvailable) { }
    }
}
