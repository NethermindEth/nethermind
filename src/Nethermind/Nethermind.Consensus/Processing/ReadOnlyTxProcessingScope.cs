// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
using Nethermind.Evm.TransactionProcessing;
using Nethermind.State;

namespace Nethermind.Consensus.Processing;

public class ReadOnlyTxProcessingScope(
    ITransactionProcessor transactionProcessor,
    IWorldStateProvider worldStateProvider
) : IReadOnlyTxProcessingScope
{
    public void Dispose()
    {
        worldStateProvider.Reset();
    }

    public ITransactionProcessor TransactionProcessor => transactionProcessor;
    public IWorldStateProvider WorldStateProvider => worldStateProvider;
}
