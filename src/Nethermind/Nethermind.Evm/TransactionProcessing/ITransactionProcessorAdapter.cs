// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Evm.Tracing;

namespace Nethermind.Evm.TransactionProcessing
{
    [StableApi]
    public interface ITransactionProcessorAdapter
    {
        TransactionResult Execute(Transaction transaction, ITxTracer txTracer);

        void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext);

        /// <summary>
        /// Resolves an implicit gas limit to the value execution will use, ahead of the EIP-8037 per-tx
        /// inclusion check. No-op unless the adapter defaults gas (simulate, for calls that omit <c>gas</c>).
        /// </summary>
        /// <param name="stateGasAvailable">Remaining EIP-8037 state-dimension budget, so the limit fits that dimension too.</param>
        void PrepareForInclusionCheck(Transaction transaction, ulong stateGasAvailable) { }
    }
}
