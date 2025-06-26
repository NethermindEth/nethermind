// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.State;

namespace Nethermind.Consensus.Processing;

public class OverridableTxProcessingScope : IOverridableTxProcessingScope
{
    private readonly IWorldState _worldState;
    private readonly IOverridableWorldScope _worldScope;

    public OverridableTxProcessingScope(
        IOverridableCodeInfoRepository codeInfoRepository,
        ITransactionProcessor transactionProcessor,
        IOverridableWorldScope worldScope,
        Hash256 stateRoot)
    {
        CodeInfoRepository = codeInfoRepository;
        TransactionProcessor = transactionProcessor;
        _worldScope = worldScope;
        _worldState = _worldScope.WorldState;
        Reset();
        _worldState.StateRoot = stateRoot;
    }

    public IOverridableCodeInfoRepository CodeInfoRepository { get; }
    public IStateReader StateReader => _worldScope.GlobalStateReader;

    public ITransactionProcessor TransactionProcessor { get; }

    public IWorldState WorldState => _worldState;

    public void Dispose() => Reset();

    public void Reset()
    {
        _worldState.Reset();
        CodeInfoRepository.ResetOverrides();
        _worldScope.ResetOverrides();
    }
}
