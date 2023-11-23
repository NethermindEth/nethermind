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
    void Execute(Transaction transaction, BlockExecutionContext blCtx, ITxTracer txTracer);

    /// <summary>
    /// Call transaction, rollback state
    /// </summary>
    void CallAndRestore(Transaction transaction, BlockExecutionContext blCtx, ITxTracer txTracer);

    /// <summary>
    /// Execute transaction, keep the state uncommitted
    /// </summary>
    void BuildUp(Transaction transaction, BlockExecutionContext blCtx, ITxTracer txTracer);

    /// <summary>
    /// Call transaction, no validations, commit state
    /// Will NOT charge gas from sender account, so stateDiff will miss gas fee
    /// </summary>
    void Trace(Transaction transaction, BlockExecutionContext blCtx, ITxTracer txTracer);
}
