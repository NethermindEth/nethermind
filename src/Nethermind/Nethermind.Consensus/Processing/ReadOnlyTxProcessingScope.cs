// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.State;

namespace Nethermind.Consensus.Processing;

public class ReadOnlyTxProcessingScope(
    IOverridableCodeInfoRepository codeInfoRepository,
    IStateReader stateReader,
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
        CodeInfoRepository.ResetOverrides();
    }

    public IOverridableCodeInfoRepository CodeInfoRepository => codeInfoRepository;
    public IStateReader StateReader => stateReader;
    public ITransactionProcessor TransactionProcessor => transactionProcessor;
    public IWorldState WorldState => worldState;
}
