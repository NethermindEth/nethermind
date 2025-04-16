// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.State;

namespace Nethermind.Consensus.Processing;

public class ReadOnlyTxProcessingScope(
    ITransactionProcessor transactionProcessor,
    IWorldState worldState
) : IReadOnlyTxProcessingScope
{
    private IDisposable? _worldStateScopeGuard;
    private Hash256? _originalStateRoot = worldState.StateRoot;
    public void Dispose()
    {
        Reset();
        _worldStateScopeGuard?.Dispose();
    }

    public void Init(Hash256 stateRoot)
    {
        _worldStateScopeGuard = worldState.BeginScope(stateRoot);
    }

    public ITransactionProcessor TransactionProcessor => transactionProcessor;
    public IWorldState WorldState => worldState;
    public void Reset()
    {
        // this means the scope was not initialized here, so don't need to reset anything
        if (_worldStateScopeGuard is not null)
        {
            worldState.StateRoot = _originalStateRoot;
            worldState.Reset();
        }
    }
}
