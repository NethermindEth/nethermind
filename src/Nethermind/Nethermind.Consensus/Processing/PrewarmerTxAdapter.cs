// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.State;

namespace Nethermind.Consensus.Processing;

/// <summary>
/// Reports main-thread transaction progress to the block cache prewarmer.
/// </summary>
/// <remarks>
/// Only the main warm world state reports progress; speculative prewarmer scopes are ignored by the
/// <see cref="IPreBlockCaches.IsWarmWorldState"/> guard.
/// </remarks>
public class PrewarmerTxAdapter(ITransactionProcessorAdapter baseAdapter, BlockCachePreWarmer preWarmer, IWorldState worldState) : ITransactionProcessorAdapter
{
    public TransactionResult Execute(Transaction transaction, ITxTracer txTracer)
    {
        if (worldState.ScopeProvider is IPreBlockCaches { IsWarmWorldState: true })
        {
            preWarmer.OnBeforeTxExecution();
        }

        return baseAdapter.Execute(transaction, txTracer);
    }

    public void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext) => baseAdapter.SetBlockExecutionContext(in blockExecutionContext);
}
