// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.State;

namespace Nethermind.Consensus.Processing;

public class OverridableTxProcessingScope(
    IOverridableCodeInfoRepository codeInfoRepository,
    ITransactionProcessor transactionProcessor,
    OverridableWorldState worldState,
    Hash256 originalStateRoot
) : IOverridableTxProcessingScope
{
    public void Dispose()
    {
        worldState.StateRoot = originalStateRoot;
        worldState.Reset();
        worldState.ResetOverrides();
        codeInfoRepository.ResetOverrides();
    }

    public IOverridableCodeInfoRepository CodeInfoRepository => codeInfoRepository;
    public ITransactionProcessor TransactionProcessor => transactionProcessor;
    public IWorldState WorldState => worldState;
}
