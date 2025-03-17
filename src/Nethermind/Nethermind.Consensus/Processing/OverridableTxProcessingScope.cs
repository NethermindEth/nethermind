// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.State;

namespace Nethermind.Consensus.Processing;

public class OverridableTxProcessingScope : IOverridableTxProcessingScope
{
    private readonly IOverridableWorldState _worldState;

    public OverridableTxProcessingScope(IOverridableCodeInfoRepository codeInfoRepository,
        ITransactionProcessor transactionProcessor,
        IOverridableWorldState worldState,
        Hash256 stateRoot)
    {
        CodeInfoRepository = codeInfoRepository;
        TransactionProcessor = transactionProcessor;
        _worldState = worldState;
        Reset();
        _worldState.StateRoot = stateRoot;
    }

    public IOverridableCodeInfoRepository CodeInfoRepository { get; }

    public ITransactionProcessor TransactionProcessor { get; }

    public IWorldState WorldState => _worldState;

    public void Dispose() => Reset();

    public void Reset()
    {
        _worldState.Reset();
        _worldState.ResetOverrides();
        CodeInfoRepository.ResetOverrides();
    }
}
