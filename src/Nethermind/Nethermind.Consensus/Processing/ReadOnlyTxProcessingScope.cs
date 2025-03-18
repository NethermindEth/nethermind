// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.State;

namespace Nethermind.Consensus.Processing;

public class ReadOnlyTxProcessingScope(
    ITransactionProcessor transactionProcessor,
    IWorldState worldState,
    Hash256 originalStateRoot
) : IReadOnlyTxProcessingScope
{
    public void Dispose()
    {
        Reset();
    }

    public ITransactionProcessor TransactionProcessor => transactionProcessor;
    public IWorldState WorldState => worldState;
    public void Reset()
    {
        worldState.StateRoot = originalStateRoot;
        worldState.Reset();
    }
}
