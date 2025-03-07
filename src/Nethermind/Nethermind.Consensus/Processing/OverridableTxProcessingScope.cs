// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.State;

namespace Nethermind.Consensus.Processing;

public class OverridableTxProcessingScope : IOverridableTxProcessingScope
{
    private readonly IOverridableCodeInfoRepository _codeInfoRepository;
    private readonly ITransactionProcessor _transactionProcessor;
    private readonly IOverridableWorldState _worldState;

    public OverridableTxProcessingScope(IOverridableCodeInfoRepository codeInfoRepository,
        ITransactionProcessor transactionProcessor,
        IOverridableWorldState worldState,
        Hash256 stateRoot)
    {
        _codeInfoRepository = codeInfoRepository;
        _transactionProcessor = transactionProcessor;
        _worldState = worldState;
        Reset();
        _worldState.StateRoot = stateRoot;
    }

    public IOverridableCodeInfoRepository CodeInfoRepository => _codeInfoRepository;
    public ITransactionProcessor TransactionProcessor => _transactionProcessor;
    public IWorldState WorldState => _worldState;

    public void Dispose() => Reset(resetBlockCache: true);

    private void Reset(bool resetBlockCache = false)
    {
        _worldState.Reset(resetBlockCache);
        _worldState.ResetOverrides();
        _codeInfoRepository.ResetOverrides();
    }
}
