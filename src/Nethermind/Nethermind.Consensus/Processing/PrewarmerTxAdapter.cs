// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.State;

namespace Nethermind.Consensus.Processing;

/// <summary>
/// Decorates the main block-processing transaction adapter to report main-thread execution
/// progress to the prewarmer, so the prewarmer can skip speculatively executing transactions
/// the main thread has already started. Without this, the prewarmer redundantly re-executes the
/// in-flight transaction, which is wasteful and contends with the main thread — pathologically so
/// for blocks dominated by a single heavy transaction at a low index.
/// </summary>
public class PrewarmerTxAdapter(ITransactionProcessorAdapter baseAdapter, BlockCachePreWarmer preWarmer, IWorldState worldState) : ITransactionProcessorAdapter
{
    public TransactionResult Execute(Transaction transaction, ITxTracer txTracer)
    {
        if (worldState.ScopeProvider is IPreBlockCaches { IsWarmWorldState: true })
        {
            preWarmer.OnBeforeTxExecution(transaction);
        }
        return baseAdapter.Execute(transaction, txTracer);
    }

    public void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext) => baseAdapter.SetBlockExecutionContext(in blockExecutionContext);
}
