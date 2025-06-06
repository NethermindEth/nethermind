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
    TransactionResult Execute(Transaction transaction, ITxTracer txTracer);
    TransactionResult Execute(Transaction transaction, in BlockExecutionContext blockExecutionContext, ITxTracer txTracer)
    {
        SetBlockExecutionContext(in blockExecutionContext);
        return Execute(transaction, txTracer);
    }

    /// <summary>
    /// Call transaction, rollback state
    /// </summary>
    TransactionResult CallAndRestore(Transaction transaction, ITxTracer txTracer);
    TransactionResult CallAndRestore(Transaction transaction, in BlockExecutionContext blockExecutionContext, ITxTracer txTracer)
    {
        SetBlockExecutionContext(in blockExecutionContext);
        return CallAndRestore(transaction, txTracer);
    }

    /// <summary>
    /// Execute transaction, keep the state uncommitted
    /// </summary>
    TransactionResult BuildUp(Transaction transaction, ITxTracer txTracer);
    TransactionResult BuildUp(Transaction transaction, in BlockExecutionContext blockExecutionContext, ITxTracer txTracer)
    {
        SetBlockExecutionContext(in blockExecutionContext);
        return BuildUp(transaction, txTracer);
    }

    /// <summary>
    /// Call transaction, no validations, commit state
    /// Will NOT charge gas from sender account, so stateDiff will miss gas fee
    /// </summary>
    TransactionResult Trace(Transaction transaction, ITxTracer txTracer);
    TransactionResult Trace(Transaction transaction, in BlockExecutionContext blockExecutionContext, ITxTracer txTracer)
    {
        SetBlockExecutionContext(in blockExecutionContext);
        return Trace(transaction, txTracer);
    }

    /// <summary>
    /// Call transaction, no validations, don't commit state
    /// Will NOT charge gas from sender account
    /// </summary>
    TransactionResult Warmup(Transaction transaction, ITxTracer txTracer);
    TransactionResult Warmup(Transaction transaction, in BlockExecutionContext blockExecutionContext, ITxTracer txTracer)
    {
        SetBlockExecutionContext(in blockExecutionContext);
        return Warmup(transaction, txTracer);
    }

    void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext);
}
