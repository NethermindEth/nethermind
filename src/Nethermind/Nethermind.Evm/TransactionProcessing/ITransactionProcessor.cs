// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Evm.Tracing;

namespace Nethermind.Evm.TransactionProcessing;

public interface ITransactionProcessor
{
    /// <summary>
    /// Execute transaction, commit state
    /// </summary>
    TransactionResult Execute(Transaction transaction, BlockExecutionContext blCtx, ITxTracer txTracer);

    /// <summary>
    /// Call transaction, rollback state
    /// </summary>
    TransactionResult CallAndRestore(Transaction transaction, BlockExecutionContext blCtx, ITxTracer txTracer);

    /// <summary>
    /// Execute transaction, keep the state uncommitted
    /// </summary>
    TransactionResult BuildUp(Transaction transaction, BlockExecutionContext blCtx, ITxTracer txTracer);

    /// <summary>
    /// Call transaction, no validations, commit state
    /// Will NOT charge gas from sender account, so stateDiff will miss gas fee
    /// </summary>
    TransactionResult Trace(Transaction transaction, BlockExecutionContext blCtx, ITxTracer txTracer);
}
