// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Consensus.Processing;

/// <summary>Runs each transaction inside a <see cref="BlockCodeCache.BeginTransaction"/> scope, so the code it uses is kept while it runs.</summary>
public sealed class BlockCodeCacheTxAdapter(ITransactionProcessorAdapter baseAdapter, BlockCodeCache codeCache) : ITransactionProcessorAdapter
{
    public TransactionResult Execute(Transaction transaction, ITxTracer txTracer)
    {
        using BlockCodeCache.TransactionScope _ = codeCache.BeginTransaction();
        return baseAdapter.Execute(transaction, txTracer);
    }

    public void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext) =>
        baseAdapter.SetBlockExecutionContext(in blockExecutionContext);

    public void PrepareForInclusionCheck(Transaction transaction, ulong stateGasAvailable) =>
        baseAdapter.PrepareForInclusionCheck(transaction, stateGasAvailable);
}
