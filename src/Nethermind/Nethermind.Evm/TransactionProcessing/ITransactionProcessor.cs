// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Evm.Tracing;

namespace Nethermind.Evm.TransactionProcessing
{
    public interface ITransactionProcessor
    {
        /// <summary>
        /// Execute transaction, commit state
        /// </summary>
        void Execute(Transaction transaction, BlockHeader block, ITxTracer txTracer);

        /// <summary>
        /// Call transaction, rollback state
        /// </summary>
        void CallAndRestore(Transaction transaction, BlockHeader block, ITxTracer txTracer);

        /// <summary>
        /// Execute transaction, keep the state uncommitted
        /// </summary>
        void BuildUp(Transaction transaction, BlockHeader block, ITxTracer txTracer);

        /// <summary>
        /// Call transaction, no validations, commit state
        /// </summary>
        void Trace(Transaction transaction, BlockHeader block, ITxTracer txTracer);
    }
}
