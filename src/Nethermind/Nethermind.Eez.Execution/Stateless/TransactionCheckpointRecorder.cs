// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Processing;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;

namespace Nethermind.Eez.Execution.Stateless;

/// <summary>
/// Records the state root after each selected transaction: pre-execution system calls and the transaction prefix
/// included, post-execution changes excluded.
/// </summary>
internal sealed class TransactionCheckpointRecorder(ISpecProvider specProvider, int[] transactionIndices)
    : BlockProcessor.BlockValidationTransactionsExecutor.ITransactionProcessedEventHandler
{
    private int _next;

    public IWorldState? WorldState { get; set; }

    public Hash256[] StateRoots { get; } = new Hash256[transactionIndices.Length];

    public void OnTransactionProcessed(TxProcessedEventArgs txProcessedEventArgs)
    {
        if (_next == transactionIndices.Length || transactionIndices[_next] != txProcessedEventArgs.Index)
        {
            return;
        }

        IWorldState worldState = WorldState!;
        worldState.Commit(specProvider.GetSpec(txProcessedEventArgs.BlockHeader), commitRoots: true);
        worldState.RecalculateStateRoot();
        StateRoots[_next++] = worldState.StateRoot;
    }
}
