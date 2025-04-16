// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.State;

namespace Nethermind.Consensus.Processing;

public class OverridableTxProcessingScope : IOverridableTxProcessingScope
{
    private readonly IOverridableWorldState _worldState;
    private IDisposable? _worldStateScopeGuard = null;

    public OverridableTxProcessingScope(IOverridableCodeInfoRepository codeInfoRepository,
        ITransactionProcessor transactionProcessor,
        IOverridableWorldState worldState)
    {
        CodeInfoRepository = codeInfoRepository;
        TransactionProcessor = transactionProcessor;
        _worldState = worldState;
    }

    public IOverridableCodeInfoRepository CodeInfoRepository { get; }

    public ITransactionProcessor TransactionProcessor { get; }

    public IWorldState WorldState => _worldState;

    public void Init(Hash256 stateRoot)
    {
        _worldStateScopeGuard = _worldState.BeginScope(stateRoot);
    }

    public void Dispose() => Reset();

    public void Reset()
    {
        _worldState.ResetOverrides();
        CodeInfoRepository.ResetOverrides();
        if (_worldStateScopeGuard != null)
        {
            _worldStateScopeGuard.Dispose();
            _worldStateScopeGuard = null;
        }
    }
}
