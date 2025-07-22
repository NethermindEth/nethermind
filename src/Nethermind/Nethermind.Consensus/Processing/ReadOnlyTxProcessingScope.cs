// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Consensus.Processing;

public class ReadOnlyTxProcessingScope(
    ITransactionProcessor transactionProcessor,
    IDisposable worldStateCloser,
    IWorldState worldState
) : IReadOnlyTxProcessingScope
{
    public void Dispose()
    {
        worldStateCloser.Dispose();
    }

    public ITransactionProcessor TransactionProcessor => transactionProcessor;
    public IWorldState WorldState => worldState;
}
