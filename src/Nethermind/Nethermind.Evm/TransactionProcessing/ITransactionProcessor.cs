// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Evm.Tracing;

namespace Nethermind.Evm.TransactionProcessing
{
    [Flags]
    public enum ExecutionOptions
    {
        /// <summary>
        /// Just accumulate the state
        /// </summary>
        None = 0,

        /// <summary>
        /// Commit the state after execution
        /// </summary>
        Commit = 1,

        /// <summary>
        /// Restore state after execution
        /// </summary>
        Restore = 2,

        /// <summary>
        /// Skip potential fail checks
        /// </summary>
        NoValidation = Commit | 4,

        /// <summary>
        /// Commit and later restore state also skip validation, use for CallAndRestore
        /// </summary>
        CommitAndRestore = Commit | Restore | NoValidation
    }
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
        /// Will NOT charge gas from sender account, so stateDiff will miss gas fee
        /// </summary>
        void Trace(Transaction transaction, BlockHeader block, ITxTracer txTracer);
    }
}
