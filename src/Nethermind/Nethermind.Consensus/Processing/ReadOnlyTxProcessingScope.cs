// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.State;

namespace Nethermind.Consensus.Processing;

public class ReadOnlyTxProcessingScope(
    IOverridableCodeInfoRepository codeInfoRepository,
    ITransactionProcessor transactionProcessor,
    IWorldState worldState,
    Hash256 originalStateRoot
) : IReadOnlyTxProcessingScope
{
    public void Dispose()
    {
        worldState.StateRoot = originalStateRoot;
        worldState.Reset();
        (worldState as OverridableWorldState)?.ResetOverrides();
        codeInfoRepository.ResetOverrides();
    }

    public ITransactionProcessor TransactionProcessor => transactionProcessor;
    public IWorldState WorldState => worldState;
}
