// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.State;

namespace Nethermind.Consensus.Processing;

public class ReadOnlyTxProcessingScope(
    ITransactionProcessor transactionProcessor,
    IWorldState worldState
) : IReadOnlyTxProcessingScope
{
    public void Dispose()
    {
        worldState.Reset();
        worldState.SetBaseBlock(null);
    }

    public ITransactionProcessor TransactionProcessor => transactionProcessor;
    public IWorldState WorldState => worldState;
}
