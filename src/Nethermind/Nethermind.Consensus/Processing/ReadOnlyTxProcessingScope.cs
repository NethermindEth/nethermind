// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.State;

namespace Nethermind.Consensus.Processing;

public class ReadOnlyTxProcessingScope(
    ITransactionProcessor transactionProcessor,
    IWorldState worldState,
    IDisposable worldStateCloser
) : IReadOnlyTxProcessingScope
{
    public void Dispose()
    {
        worldStateCloser.Dispose();
        worldState.Reset(); // TODO: Double check if this is still needed
    }

    public ITransactionProcessor TransactionProcessor => transactionProcessor;
    public IWorldState WorldState => worldState;
}
